using System.Threading;

namespace AppUserDataScanner.Reporting;

/// <summary>
/// Thread-safe, lock-free container for scanning metrics.
/// </summary>
internal sealed class ScanMetrics
{
    private long totalDirsScanned;
    private long totalMatchesFound;
    private long totalBytesDiscovered;

    internal long TotalDirsScanned => Interlocked.Read(ref totalDirsScanned);
    internal long TotalMatchesFound => Interlocked.Read(ref totalMatchesFound);
    internal long TotalBytesDiscovered => Interlocked.Read(ref totalBytesDiscovered);

    internal void IncrementDirsScanned() =>
        Interlocked.Increment(ref totalDirsScanned);

    internal void IncrementMatchesFound() =>
        Interlocked.Increment(ref totalMatchesFound);

    internal void AddBytesDiscovered(long bytes) =>
        Interlocked.Add(ref totalBytesDiscovered, bytes);
}