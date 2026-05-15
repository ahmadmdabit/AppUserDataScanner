namespace AppUserDataScanner.Scanner;

/// <summary>
/// Configurable parameters for the drive scan.
/// </summary>
internal sealed class ScanOptions
{
    /// <summary>
    /// Number of parallel directory worker tasks.
    /// Recommended: 4–8 for SSD, 1–2 for HDD.
    /// Default: 4 (configurable via CLI arg).
    /// </summary>
    internal int WorkerCount { get; init; } = 4;

    /// <summary>
    /// Maximum directory depth to recurse from the drive root.
    /// Browser user data is never found beyond ~10 levels deep.
    /// </summary>
    internal int MaxDepth { get; init; } = 12;

    /// <summary>
    /// Minimum score threshold for a directory to be reported
    /// as a Chromium or Electron user data candidate.
    /// </summary>
    internal int MinScore { get; init; } = 2;

    /// <summary>
    /// Bounded channel capacity for pending directories.
    /// Limits memory usage when producers outpace consumers.
    /// </summary>
    internal int ChannelCapacity { get; init; } = 4096;

    /// <summary>
    /// Minimum directory size threshold in bytes to filter out from output.
    /// If null, size calculation is entirely skipped for maximum speed.
    /// </summary>
    internal long? MinSizeBytes { get; init; }

    /// <summary>
    /// True if markdown report generation is enabled.
    /// </summary>
    internal bool EnableReport { get; init; }

    /// <summary>
    /// Generated filename for the markdown report.
    /// </summary>
    internal string? ReportFileName { get; init; }
}