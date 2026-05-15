using System.Threading;

namespace AppUserDataScanner.Scanner;

internal enum WorkerState
{
    Idle,
    Dequeuing,
    Detecting,
    Enumerating
}

internal sealed class WorkerDiagnostics
{
    internal readonly WorkerState[] States;
    internal readonly string?[] CurrentPaths;

    private long maxEnumerationTimeMs;
    private string? slowestDirectory;

    internal long MaxEnumerationTimeMs => Interlocked.Read(ref maxEnumerationTimeMs);
    internal string? SlowestDirectory => slowestDirectory;

    internal WorkerDiagnostics(int workerCount)
    {
        States = new WorkerState[workerCount];
        CurrentPaths = new string?[workerCount];
    }

    internal void UpdateSlowest(long elapsedMs, string path)
    {
        long currentMax = Interlocked.Read(ref maxEnumerationTimeMs);
        while (elapsedMs > currentMax)
        {
            long initial = Interlocked.CompareExchange(ref maxEnumerationTimeMs, elapsedMs, currentMax);
            if (initial == currentMax)
            {
                Interlocked.Exchange(ref slowestDirectory, path);
                break;
            }
            currentMax = Interlocked.Read(ref maxEnumerationTimeMs);
        }
    }
}