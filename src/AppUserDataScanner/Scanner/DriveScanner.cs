using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using AppUserDataScanner.Detection;
using AppUserDataScanner.Infrastructure;
using AppUserDataScanner.Reporting;

using Microsoft.Extensions.Logging;

using ZLogger;

namespace AppUserDataScanner.Scanner;

/// <summary>
/// <para>
/// Orchestrates the full-drive scan using Parallel.ForEachAsync over
/// directory worker tasks backed by bounded Channels.
/// </para>
/// <list type="bullet">
/// Architecture:
/// <item>One bounded Channel&lt;(string, int)&gt; for pending directories (BFS queue)</item>
/// <item>One unbounded Channel&lt;string&gt; for matched results</item>
/// <item>N concurrent DirectoryWorker tasks (N = ScanOptions.WorkerCount)</item>
/// <item>1 dedicated result-writer task (console output, never blocks scan workers)</item>
/// </para>
/// <para>
/// The channel is completed when all workers have finished and no more
/// directories are pending — detected via an active-work counter using
/// Interlocked operations for thread safety without locks.
/// </para>
/// </summary>
internal sealed class DriveScanner
{
    private const string NA = "N/A";
    private readonly ScanOptions options;
    private readonly IDetector[] detectors;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger<DriveScanner> logger;
    private static readonly char[] ActivityIndicatorFrames = ['⢄', '⢂', '⢁', '⡁', '⡈', '⡐', '⡠'];
    private static readonly Lock locker = new();

    internal DriveScanner(ScanOptions options, IDetector[] detectors, ILoggerFactory loggerFactory)
    {
        this.options = options;
        this.detectors = detectors;
        this.loggerFactory = loggerFactory;
        this.logger = loggerFactory.CreateLogger<DriveScanner>();
    }

    internal async Task ScanAsync(string[] driveRoots, CancellationToken cancellationToken)
    {
        DateTime startTime = DateTime.Now;
        Stopwatch stopwatch = Stopwatch.StartNew();
        ScanMetrics metrics = new();

        logger.ZLogInformation($"Starting scan with {options.WorkerCount} workers, max depth {options.MaxDepth}, min score {options.MinScore}, min size {options.MinSizeBytes?.ToString() ?? "None"}");
        logger.ZLogInformation($"Scan roots ({driveRoots.Length}): {string.Join(", ", driveRoots)}");
        // Bounded channel for directories pending processing.
        // Backpressure prevents unbounded memory growth on fast enumeration.
        Channel<(string Path, int Depth)> dirChannel = Channel.CreateBounded<(string, int)>(
            new BoundedChannelOptions(options.ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = false,
                SingleReader = false,
                AllowSynchronousContinuations = false
            });

        WorkTracker workTracker = new(dirChannel.Writer);
        WorkerDiagnostics diagnostics = new(options.WorkerCount);

        // Unbounded results channel — matches are rare, memory impact is negligible
        Channel<ScanMatch> resultsChannel = Channel.CreateUnbounded<ScanMatch>(
            new UnboundedChannelOptions
            {
                SingleWriter = false,
                SingleReader = true,
                AllowSynchronousContinuations = false
            });

        // Seed the directory channel with all drive roots at depth 0
        foreach (string root in driveRoots)
        {
            if (Directory.Exists(root))
            {
                logger.ZLogDebug($"Seeding root: {root}");
                workTracker.OnItemQueued();
                await dirChannel.Writer.WriteAsync((root, 0), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                logger.ZLogWarning($"Root not found (skipping): {root}");
            }
        }
        logger.ZLogInformation($"Seeded {driveRoots.Count(r => Directory.Exists(r))} root directories.");

        // Start the async activityIndicator task
        Task activityIndicatorTask = Task.Run(() => RunActivityIndicatorAsync(resultsChannel.Reader.Completion, metrics, workTracker, diagnostics, options, logger, startTime, cancellationToken), cancellationToken);

        // Start the result-writer task — dedicated consumer of the results channel
        Task writerTask = Task.Run(() => WriteResultsAsync(resultsChannel.Reader, metrics, options, cancellationToken), cancellationToken);

        // Launch N workers concurrently using Parallel.ForEachAsync
        // Each worker is identified by an index [0..WorkerCount)
        int[] workerIndices = new int[options.WorkerCount];
        for (int i = 0; i < options.WorkerCount; i++) workerIndices[i] = i;

        await Parallel.ForEachAsync(
            workerIndices,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = options.WorkerCount,
                CancellationToken = cancellationToken
            },
            async (_, ct) =>
            {
                int workerId = Interlocked.Increment(ref workerIndices[0]) - 1; // Simple ID assignment
                var worker = new DirectoryWorker(
                    workerId,
                    dirChannel.Reader,
                    dirChannel.Writer,
                    resultsChannel.Writer,
                    detectors,
                    options,
                    workTracker,
                    metrics,
                    diagnostics,
                    loggerFactory.CreateLogger<DirectoryWorker>());

                await worker.RunAsync(ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

        // Wait for all work to complete
        // WorkTracker will automatically complete the channel when both counters hit zero
        await dirChannel.Reader.Completion.ConfigureAwait(false);
        logger.ZLogInformation($"Channel completed. Workers finished. Total dirs scanned: {metrics.TotalDirsScanned}");

        // All workers done — signal result writer to finish
        resultsChannel.Writer.Complete();

        // Wait for all results to be written to console
        await activityIndicatorTask.ConfigureAwait(false);
        await writerTask.ConfigureAwait(false);
        stopwatch.Stop();
        logger.ZLogInformation($"Scan completed ({stopwatch.Elapsed.TotalSeconds:F2}s). Matches: {metrics.TotalMatchesFound}, Bytes: {metrics.TotalBytesDiscovered}");
        await WriteFinalSummaryAsync(metrics, stopwatch.Elapsed, options, startTime).ConfigureAwait(false);
    }

    private async Task WriteResultsAsync(
        ChannelReader<ScanMatch> reader,
        ScanMetrics metrics,
        ScanOptions options,
        CancellationToken cancellationToken)
    {
        var isWriteReport = options.EnableReport && !string.IsNullOrEmpty(options.ReportFileName);
        HashSet<ScanMatch>? matches = isWriteReport ? new() : null;

        // Deduplicate overlapping roots (e.g., seeding AppData AND C:\)
        // Memory impact is negligible because matches are rare (<1000 typically).
        HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);

        await foreach (ScanMatch match in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!seenPaths.Add(match.DirectoryPath))
                continue; // Skip duplicate match

            metrics.IncrementMatchesFound();
            if (match.SizeBytes.HasValue)
                metrics.AddBytesDiscovered(match.SizeBytes.Value);

            var sizeStr = match.SizeBytes.HasValue
                ? DirectorySizeCalculator.FormatBytes(match.SizeBytes.Value).ToString()
                : NA;

            // Coordinate with activityIndicator to ensure clean lines
            lock (locker)
            {
                Console.Write("\r\x1b[2K"); // ANSI clear entire current line
                logger.ZLogInformation($"[{match.Label}] (score={match.Score}) (size={sizeStr}) {match.DirectoryPath}\n  Signals: {match.MatchedSignals}\n");
            }

            if (isWriteReport)
            {
                matches!.Add(match);
            }
        }

        if (isWriteReport)
        {
            await WriteReportAsync(matches!, options).ConfigureAwait(false);
        }
    }

    private static async Task RunActivityIndicatorAsync(Task completionTask, ScanMetrics metrics, WorkTracker workTracker, WorkerDiagnostics diagnostics, ScanOptions options, ILogger logger, DateTime startTime, CancellationToken cancellationToken)
    {
        int frame = 0;
        long lastScanned = -1;
        DateTime lastProgressTime = DateTime.Now;

        while (!completionTask.IsCompleted && !cancellationToken.IsCancellationRequested)
        {
            TimeSpan elapsed = DateTime.Now - startTime;
            long scanned = metrics.TotalDirsScanned;
            int remaining = workTracker.RemainingWork;
            long total = scanned + remaining;
            int inChannel = workTracker.ItemsInChannel;

            // Hang Detection Logic
            if (scanned == lastScanned && inChannel > 0)
            {
                if ((DateTime.Now - lastProgressTime).TotalSeconds >= 5)
                {
                    lock (locker)
                    {
                        Console.Write("\r\x1b[2K"); // Clear activityIndicator line
                        logger.ZLogWarning($"HANG DETECTED! Channel: {inChannel}/{options.ChannelCapacity}");

                        for (int i = 0; i < options.WorkerCount; i++)
                        {
                            logger.ZLogWarning($"Worker {i} State: {diagnostics.States[i]} | Path: {diagnostics.CurrentPaths[i]}");
                        }
                        logger.ZLogInformation($"Slowest directory so far: {diagnostics.SlowestDirectory} ({diagnostics.MaxEnumerationTimeMs}ms)");
                    }
                    lastProgressTime = DateTime.Now; // Reset to avoid spamming
                }
            }
            else if (scanned != lastScanned)
            {
                lastScanned = scanned;
                lastProgressTime = DateTime.Now;
            }

            lock (locker)
            {
                Console.Write($"\r{ActivityIndicatorFrames[frame++ % ActivityIndicatorFrames.Length]} Scanning... {scanned:#,0}/{total:#,0} dirs | Matches: {metrics.TotalMatchesFound:#,0} | Elapsed: {elapsed:hh\\:mm\\:ss}");
            }

            try
            {
                // Task.Delay with cancellation allows instant exit when scan completes early
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // Clear activityIndicator line completely upon completion
        lock (locker)
        {
            Console.Write("\r\x1b[2K");
        }
    }

    private static async Task WriteReportAsync(HashSet<ScanMatch> matches, ScanOptions options)
    {
        ArgumentNullException.ThrowIfNull(matches);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(options?.ReportFileName);

        await using StreamWriter? reportWriter = new(options.ReportFileName, append: false);
        await reportWriter.WriteLineAsync("# App User Data Scan Report\n").ConfigureAwait(false);
        await reportWriter.WriteLineAsync($"**Date:** {DateTime.Now:yyyy-MM-dd HH:mm:ss}").ConfigureAwait(false);
        await reportWriter.WriteLineAsync($"**Config:** Workers={options.WorkerCount}, MaxDepth={options.MaxDepth}, MinScore={options.MinScore}, MinSize={options.MinSizeBytes?.ToString() ?? "None"}\n").ConfigureAwait(false);
        await reportWriter.WriteLineAsync("| Browser Type | Score | Size | Path | Signals |").ConfigureAwait(false);
        await reportWriter.WriteLineAsync("|---|---|---|---|---|").ConfigureAwait(false);

        foreach (var match in matches.OrderBy(x => x.Label).ThenByDescending(x => x.SizeBytes))
        {
            var sizeStr = match.SizeBytes.HasValue
                    ? DirectorySizeCalculator.FormatBytes(match.SizeBytes.Value)
                    : NA;
            string mdPath = match.DirectoryPath.Replace("\\", "\\\\");
            string mdSignals = match.MatchedSignals?.Replace("|", "\\|") ?? "";
            await reportWriter.WriteLineAsync($"| {match.Label} | {match.Score} | {sizeStr} | `{mdPath}` | {mdSignals} |").ConfigureAwait(false);
        }
    }

    private async Task WriteFinalSummaryAsync(ScanMetrics metrics, TimeSpan elapsed, ScanOptions options, DateTime startTime)
    {
        var totalDataStr = DirectorySizeCalculator.FormatBytes(metrics.TotalBytesDiscovered);
        logger.ZLogInformation($"╔══════════════════════════════════════════════════╗");
        logger.ZLogInformation($"║                 SCAN SUMMARY                     ║");
        logger.ZLogInformation($"╚══════════════════════════════════════════════════╝");
        logger.ZLogInformation($"  Start Time      : {startTime:yyyy-MM-dd HH:mm:ss}");
        logger.ZLogInformation($"  Elapsed Time    : {elapsed.TotalSeconds:F2} seconds");
        logger.ZLogInformation($"  Total Dirs      : {metrics.TotalDirsScanned:#,0}");
        logger.ZLogInformation($"  Matches Found   : {metrics.TotalMatchesFound:#,0}");
        logger.ZLogInformation($"  Data Discovered : {totalDataStr.ToString()}");
        logger.ZLogInformation($"──────────────────────────────────────────────────");

        if (options.EnableReport && !string.IsNullOrEmpty(options.ReportFileName))
        {
            var tds = totalDataStr.ToString();
            await using StreamWriter writer = new(options.ReportFileName, append: true);
            await writer.WriteLineAsync("\n## Execution Summary\n").ConfigureAwait(false);
            await writer.WriteLineAsync("| Metric | Value |").ConfigureAwait(false);
            await writer.WriteLineAsync("|---|---|").ConfigureAwait(false);
            await writer.WriteLineAsync($"| Start Time | {startTime:yyyy-MM-dd HH:mm:ss} |").ConfigureAwait(false);
            await writer.WriteLineAsync($"| Elapsed Time | {elapsed.TotalSeconds:F2} seconds |").ConfigureAwait(false);
            await writer.WriteLineAsync($"| Total Dirs Scanned | {metrics.TotalDirsScanned:#,0} |").ConfigureAwait(false);
            await writer.WriteLineAsync($"| Matches Found | {metrics.TotalMatchesFound:#,0} |").ConfigureAwait(false);
            await writer.WriteLineAsync($"| Total Data | {tds} |").ConfigureAwait(false);
            logger.ZLogInformation($"Report saved to: {options.ReportFileName}");
        }
    }
}