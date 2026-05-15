namespace AppUserDataScanner.Reporting;

/// <summary>
/// Zero-allocation struct representing a verified browser user data match.
/// </summary>
/// <summary>
/// Zero-allocation struct representing a verified browser user data match.
/// </summary>
internal readonly struct ScanMatch
{
    internal readonly string DirectoryPath { get; init; }
    internal readonly string Label { get; init; }
    internal readonly int Score { get; init; }
    internal readonly string? MatchedSignals { get; init; }
    internal readonly long? SizeBytes { get; init; }
}