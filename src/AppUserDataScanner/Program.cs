using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using AppUserDataScanner.Detection;
using AppUserDataScanner.Scanner;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;

using ZLogger;

namespace AppUserDataScanner;

internal static class Program
{
    private static ILogger logger = null!;
    private static readonly DefaultObjectPoolProvider objectPoolProvider = new();

    internal static readonly ObjectPool<StringBuilder> StringBuilderObjectPool =
        objectPoolProvider.Create(new StringBuilderPooledObjectPolicy()
        {
            InitialCapacity = 128,
        });

    private static async Task<int> Main(string[] args)
    {
        if (args.IndexOf("--help") > -1 || args.IndexOf("-h") > -1)
        {
            PrintHelp();
            return 0;
        }

        ScanOptions options;
        LogLevel logLevel = LogLevel.Information;

        if (args.Length == 0)
        {
            options = RunInteractive(out logLevel);
            if (options == null!) return 0; // User cancelled or error
        }
        else
        {
            logLevel = (args.IndexOf("--verbose") > -1 || args.IndexOf("-v") > -1) ?
                LogLevel.Debug : LogLevel.Information;
            options = ParseOptions(args);
        }

        using var loggerFactory = LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(logLevel);

            logging.AddZLoggerConsole(options =>
            {
                options.UsePlainTextFormatter(formatter =>
                {
                    formatter.SetPrefixFormatter($"{0:utc-longdate} [{1:short}] ", (in MessageTemplate template, in LogInfo info) => template.Format(info.Timestamp, info.LogLevel));
                    //formatter.SetSuffixFormatter($" ({0})", (in MessageTemplate template, in LogInfo info) => template.Format(info.Category));
                    formatter.SetExceptionFormatter((writer, ex) => Utf8StringInterpolation.Utf8String.Format(writer, $"\n{ex}"));
                });
            });
        });

        logger = loggerFactory.CreateLogger("Program");

        logger.ZLogInformation($"Arguments: {string.Join(" ", args)}");
        // ScanOptions options = ParseOptions(args); // Handled above
        logger.ZLogInformation($"Parsed options: Workers={options.WorkerCount}, MaxDepth={options.MaxDepth}, MinScore={options.MinScore}, MinSize={options.MinSizeBytes?.ToString() ?? "None"}, Report={options.EnableReport}");

        // Collect all scan roots
        DateTime rootDiscoveryStart = DateTime.Now;
        string[] driveRoots = GetScanRoots();
        logger.ZLogInformation($"Root discovery completed in {(DateTime.Now - rootDiscoveryStart).TotalMilliseconds}ms. Found {driveRoots.Length} roots.");

        if (driveRoots.Length == 0)
        {
            logger.ZLogError($"No scan roots found.");
            return 1;
        }

        IDetector[] detectors =
        [
            new ChromiumDetector(),
            new ElectronDetector()
        ];

        PrintHeader(options, driveRoots);

        using CancellationTokenSource cts = new();

        // Allow Ctrl+C to cancel gracefully
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // prevent immediate process kill
            cts.Cancel();
            logger.ZLogWarning($"\nCancellation requested. Finishing current work...");
        };

        DriveScanner scanner = new(options, detectors, loggerFactory);

        try
        {
            await scanner.ScanAsync(driveRoots, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            logger.ZLogWarning($"Scan cancelled.");
            return 2;
        }
        catch (Exception ex)
        {
            logger.ZLogError($"Unexpected: {ex.Message}");
            return 1;
        }

        logger.ZLogInformation($"Scan complete.");

        logger.ZLogInformation($"Press any key to exit.");
        Console.ReadKey();
        return 0;
    }

    /// <summary>
    /// Returns prioritized scan roots:
    /// <list type="number">
    /// <item>High-value user data paths (AppData, Local, Roaming)</item>
    /// <item>Then full drives as fallback</item>
    /// </list>
    /// </summary>
    private static string[] GetScanRoots()
    {
        var roots = new List<string>(32);

        string systemDrive = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!string.IsNullOrEmpty(systemDrive))
        {
            string driveRoot = Path.GetPathRoot(systemDrive)!;

            string usersPath = Path.Combine(driveRoot, "Users");
            if (Directory.Exists(usersPath))
            {
                try
                {
                    foreach (string userDir in Directory.EnumerateDirectories(usersPath))
                    {
                        roots.Add(Path.Combine(userDir, "AppData", "Local"));
                        roots.Add(Path.Combine(userDir, "AppData", "Roaming"));
                    }
                }
                catch { }
            }
        }

        // Fallback to full drives (ensures completeness)
        roots.AddRange(GetFixedDriveRoots());

        return [.. roots];
    }

    private static string[] GetFixedDriveRoots()
    {
        // DriveType.Fixed covers local HDDs/SSDs on Windows 10/11.
        // Excludes: Network, CDRom, Ram, Removable, Unknown.
        List<string> roots = [];
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                roots.Add(drive.RootDirectory.FullName);
        }
        return [.. roots];
    }

    /// <summary>
    /// Parses CLI arguments:
    ///   --workers N   : number of parallel workers (default 4)
    ///   --max-depth N : maximum recursion depth (default 12)
    ///   --min-score N : minimum detection score to report (default 3)
    ///   --min-size U  : min size filter (e.g. 100MB, 500KB, 10GB, 1048576)
    ///   --report      : exports markdown report to Report-yyyy-MM-dd--HH-mm.md
    /// </summary>
    private static ScanOptions ParseOptions(string[] args)
    {
        int workers = 4;
        int maxDepth = 12;
        int minScore = 3;
        long? minSize = null; // 1000 * 1024 * 1024;
        bool report = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--workers":
                case "-w":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int w) && w > 0)
                    {
                        workers = w;
                        i++;
                    }
                    break;

                case "--max-depth":
                case "-d":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int d) && d > 0)
                    {
                        maxDepth = d;
                        i++;
                    }
                    break;

                case "--min-score":
                case "-s":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int s) && s > 0)
                    {
                        minScore = s;
                        i++;
                    }
                    break;

                case "--min-size":
                case "-S":
                    if (i + 1 < args.Length)
                    {
                        minSize = ParseSize(args[i + 1]);
                        i++;
                    }
                    break;

                case "--report":
                case "-r":
                    report = true;
                    break;
            }
        }

        string? reportName = report ? $"Report-{DateTime.Now:yyyy-MM-dd--HH-mm}.md" : null;

        return new ScanOptions
        {
            WorkerCount = workers,
            MaxDepth = maxDepth,
            MinScore = minScore,
            MinSizeBytes = minSize,
            EnableReport = report,
            ReportFileName = reportName
        };
    }

    private static long? ParseSize(string input)
    {
        input = input.Trim().ToUpperInvariant();
        long multiplier = 1;
        if (input.EndsWith("GB")) { multiplier = 1073741824; input = input[..^2]; }
        else if (input.EndsWith("MB")) { multiplier = 1048576; input = input[..^2]; }
        else if (input.EndsWith("KB")) { multiplier = 1024; input = input[..^2]; }
        else if (input.EndsWith("B")) { input = input[..^1]; }

        if (double.TryParse(input, out double val))
        {
            return (long)(val * multiplier);
        }
        return null;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("App User Data Scanner");
        Console.WriteLine("Usage: AppUserDataScanner.exe [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -w, --workers <N>    Number of parallel worker tasks (default: 4)");
        Console.WriteLine("  -d, --max-depth <N>  Maximum directory recursion depth (default: 12)");
        Console.WriteLine("  -s, --min-score <N>  Minimum detection score to report (default: 3)");
        Console.WriteLine("  -S, --min-size <U>   Minimum directory size (e.g., 100MB, 1GB, 500KB)");
        Console.WriteLine("  -r, --report         Generate a Markdown report file");
        Console.WriteLine("  -v, --verbose        Enable debug-level diagnostic logging");
        Console.WriteLine("  -h, --help           Show this help message");
        Console.WriteLine();
    }

    private static void PrintHeader(ScanOptions options, string[] roots)
    {
        logger.ZLogInformation($"╔══════════════════════════════════════════════════╗");
        logger.ZLogInformation($"║              App User Data Scanner               ║");
        logger.ZLogInformation($"╚══════════════════════════════════════════════════╝");
        logger.ZLogInformation($"  Workers  : {options.WorkerCount}");
        logger.ZLogInformation($"  Max Depth: {options.MaxDepth}");
        logger.ZLogInformation($"  Min Score: {options.MinScore}");
        logger.ZLogInformation($"  Min Size : {(options.MinSizeBytes.HasValue ? Infrastructure.DirectorySizeCalculator.FormatBytes(options.MinSizeBytes.Value).ToString() : "None (Fast Mode)")}");
        logger.ZLogInformation($"  Report   : {(options.EnableReport ? options.ReportFileName : "Disabled")}");
        logger.ZLogInformation($"  Drives   : {string.Join(", ", roots)}");
        logger.ZLogInformation($"──────────────────────────────────────────────────");
        logger.ZLogInformation($"");
    }

    private static ScanOptions RunInteractive(out LogLevel logLevel)
    {
        logLevel = LogLevel.Information;
        Console.WriteLine("App User Data Scanner - Interactive Mode");
        Console.WriteLine("────────────────────────────────────────");
        Console.WriteLine("1. Run with Defaults (4 Workers, Depth 12, Score 3)");
        Console.WriteLine("2. Custom Configuration");
        Console.WriteLine("Q. Quit");
        Console.Write("\nSelect an option: ");

        var choice = Console.ReadKey().Key;
        Console.WriteLine("\n");

        if (choice == ConsoleKey.Q) return null!;
        if (choice == ConsoleKey.D1 || choice == ConsoleKey.NumPad1) return new ScanOptions { WorkerCount = 4, MaxDepth = 12, MinScore = 3 };

        int workers = ReadInt("Number of parallel workers", 4);
        int maxDepth = ReadInt("Maximum recursion depth", 12);
        int minScore = ReadInt("Minimum detection score", 3);

        Console.Write("Minimum directory size filter (e.g. 1GB, 500MB) [None]: ");
        string sizeInput = Console.ReadLine() ?? "";
        long? minSize = string.IsNullOrWhiteSpace(sizeInput) ? null : ParseSize(sizeInput);

        Console.Write("Enable Markdown Report? (y/N): ");
        bool report = Console.ReadKey().Key == ConsoleKey.Y;
        Console.WriteLine();

        Console.Write("Enable Verbose Logging? (y/N): ");
        bool verbose = Console.ReadKey().Key == ConsoleKey.Y;
        Console.WriteLine();

        if (verbose) logLevel = LogLevel.Debug;

        return new ScanOptions
        {
            WorkerCount = workers,
            MaxDepth = maxDepth,
            MinScore = minScore,
            MinSizeBytes = minSize,
            EnableReport = report,
            ReportFileName = report ? $"Report-{DateTime.Now:yyyy-MM-dd--HH-mm}.md" : null
        };
    }

    private static int ReadInt(string prompt, int defaultValue)
    {
        Console.Write($"{prompt} [{defaultValue}]: ");
        string input = Console.ReadLine() ?? "";
        if (string.IsNullOrWhiteSpace(input)) return defaultValue;
        return int.TryParse(input, out int result) ? result : defaultValue;
    }
}