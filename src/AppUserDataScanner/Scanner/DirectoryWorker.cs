using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
/// A single worker task that:
/// <list type="number">
/// <item>Reads a (path, depth) work item from the directory channel.</item>
/// <item>Applies skip-list and depth pruning.</item>
/// <item>Applies zero-allocation span-based name pre-filter.</item>
/// <item>Runs score-based detectors on candidate directories.</item>
/// <item>Posts matched paths to the results channel.</item>
/// <item>Posts subdirectories back to the directory channel (BFS).</item>
/// </list>
/// <para>Stateless and thread-safe — safe to run N instances concurrently.</para>
/// </summary>
internal sealed class DirectoryWorker
{
    // Hard limit to prevent pathological NTFS enumeration hangs
    // Tuned for performance: prevents >100k dir explosions (e.g. node-style trees)
    private const int MaxSubdirectoriesPerDirectory = 2000;

    private readonly int workerIndex;
    private readonly ChannelReader<(string Path, int Depth)> dirReader;
    private readonly ChannelWriter<(string Path, int Depth)> dirWriter;
    private readonly ChannelWriter<ScanMatch> resultsWriter;
    private readonly IDetector[] detectors;
    private readonly ScanOptions options;
    private readonly WorkTracker workTracker;
    private readonly ScanMetrics metrics;
    private readonly WorkerDiagnostics diagnostics;
    private readonly ILogger logger;
    private readonly EnumerationOptions enumOptions;

    internal DirectoryWorker(
        int workerIndex,
        ChannelReader<(string Path, int Depth)> dirReader,
        ChannelWriter<(string Path, int Depth)> dirWriter,
        ChannelWriter<ScanMatch> resultsWriter,
        IDetector[] detectors,
        ScanOptions options,
        WorkTracker workTracker,
        ScanMetrics metrics,
        WorkerDiagnostics diagnostics,
        ILogger logger)
    {
        this.workerIndex = workerIndex;
        this.dirReader = dirReader;
        this.dirWriter = dirWriter;
        this.resultsWriter = resultsWriter;
        this.detectors = detectors;
        this.options = options;
        this.workTracker = workTracker;
        this.metrics = metrics;
        this.diagnostics = diagnostics;
        this.logger = logger;
        this.enumOptions = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline, IgnoreInaccessible = true };
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        diagnostics.States[workerIndex] = WorkerState.Dequeuing;
        logger.ZLogDebug($"Worker {workerIndex} started.");

        // Reusable local stack for DFS fallback. Zero per-directory allocation.
        Stack<(string Path, int Depth)> localStack = new();

        await foreach ((string rootPath, int rootDepth) in dirReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            // Mark item as dequeued (transfers from channel count to processing count)
            workTracker.OnItemDequeued();
            localStack.Push((rootPath, rootDepth));
            // Process the dequeued item AND anything that spills into the local stack
            while (localStack.TryPop(out var current))
            {
                string dirPath = current.Path;
                int depth = current.Depth;

                logger.ZLogTrace($"Worker {workerIndex} processing: {dirPath} (depth={depth})");
                diagnostics.CurrentPaths[workerIndex] = dirPath;
                diagnostics.States[workerIndex] = WorkerState.Detecting;

                if (depth > options.MaxDepth)
                {
                    logger.ZLogTrace($"Worker {workerIndex} skipping (max depth:{depth}): {dirPath}");
                    continue;
                }

                if (PathHelper.ShouldSkip(dirPath.AsSpan()))
                {
                    logger.ZLogTrace($"Worker {workerIndex} skipping (path filter): {dirPath}");
                    continue;
                }

                metrics.IncrementDirsScanned();

                // Deduplication: Find single highest-scoring match
                ScanMatch? bestMatch = null;
                foreach (IDetector detector in detectors)
                {
                    DetectionResult result = detector.Evaluate(dirPath);
                    if (result.Score > 0 && result.Score < options.MinScore)
                    {
                        logger.ZLogTrace($"Worker {workerIndex} detector '{detector.Label}' found {dirPath} (score={result.Score}, below threshold {options.MinScore})");
                    }
                    if (result.IsMatch(options.MinScore))
                    {
                        if (bestMatch == null || result.Score >= bestMatch.Value.Score)
                        {
                            bestMatch = new ScanMatch
                            {
                                DirectoryPath = dirPath,
                                Label = detector.Label,
                                Score = result.Score,
                                MatchedSignals = result.MatchedSignals
                            };
                        }
                    }
                }
                if (bestMatch.HasValue)
                {
                    long? calculatedSize = null;
                    logger.ZLogDebug($"Worker {workerIndex} match found: {bestMatch.Value.DirectoryPath} (label={bestMatch.Value.Label}, score={bestMatch.Value.Score}, signals={bestMatch.Value.MatchedSignals})");
                    if (options.MinSizeBytes.HasValue)
                    {
                        calculatedSize = DirectorySizeCalculator.CalculateSizeBytes(dirPath);
                        if (calculatedSize.Value < options.MinSizeBytes.Value)
                        {
                            logger.ZLogTrace($"Worker {workerIndex} match filtered (size {calculatedSize.Value} < min {options.MinSizeBytes.Value}): {dirPath}");
                            continue; // Filter out below threshold
                        }
                    }
                    ScanMatch match = bestMatch.Value;
                    ScanMatch finalizedMatch = new()
                    {
                        DirectoryPath = match.DirectoryPath,
                        Label = match.Label,
                        Score = match.Score,
                        MatchedSignals = match.MatchedSignals,
                        SizeBytes = calculatedSize
                    };

                    // Fire and forget write to unbounded results channel
                    resultsWriter.TryWrite(finalizedMatch);
                    continue; // Do not recurse into matched user data roots
                }

                diagnostics.States[workerIndex] = WorkerState.Enumerating;
                long startTicks = Stopwatch.GetTimestamp();
                int childCount = 0;

                // Inline EnqueueChildren logic
                try
                {
                    foreach (string childDir in Directory.EnumerateDirectories(dirPath, "*", enumOptions))
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            logger.ZLogDebug($"Worker {workerIndex} cancellation requested during enumeration: {dirPath}");
                            break;
                        }

                        // 🔴 PRE-FILTER: Prevent garbage from ever entering the channel or stack
                        if (PathHelper.ShouldSkip(childDir.AsSpan()))
                            continue;

                        if (++childCount > MaxSubdirectoriesPerDirectory)
                        {
                            logger.ZLogWarning($"Worker {workerIndex} hit subdirectory limit ({MaxSubdirectoriesPerDirectory}) at: {dirPath}");
                            break;
                        }

                        var nextItem = (childDir, depth + 1);

                        // Hybrid BFS/DFS: Try channel first, fallback to local stack
                        if (!dirWriter.TryWrite(nextItem))
                        {
                            localStack.Push(nextItem);
                        }
                        else
                        {
                            workTracker.OnItemQueued();
                        }
                    }
                }
                catch (UnauthorizedAccessException ex) { logger.ZLogTrace(ex, $"Worker {workerIndex} access denied: {dirPath}"); }
                catch (DirectoryNotFoundException) { logger.ZLogTrace($"Worker {workerIndex} directory not found (deleted between checks): {dirPath}"); }
                catch (IOException ex) { logger.ZLogTrace(ex, $"Worker {workerIndex} I/O error enumerating: {dirPath}"); }

                long elapsedMs = (long)Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;
                diagnostics.UpdateSlowest(elapsedMs, dirPath);

                if (elapsedMs > 500)
                {
                    logger.ZLogWarning($"Worker {workerIndex} slow enumeration ({elapsedMs}ms): {dirPath} with {childCount} children");
                }
            }

            // Only complete the item when the local stack is fully drained
            workTracker.OnItemCompleted();
            diagnostics.States[workerIndex] = WorkerState.Dequeuing;
        }
        diagnostics.States[workerIndex] = WorkerState.Idle;
        logger.ZLogDebug($"Worker {workerIndex} finished.");
    }
}