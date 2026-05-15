namespace AppUserDataScanner.Detection;

/// <summary>
/// Immutable result of a detector evaluation against one directory.
/// </summary>
internal readonly struct DetectionResult
{
    /// <summary>Confidence score. Higher = more certain match.</summary>
    internal int Score { get; init; }

    /// <summary>Human-readable description of which signals were found.</summary>
    internal string? MatchedSignals { get; init; }

    /// <summary>True if score meets or exceeds the configured minimum.</summary>
    internal bool IsMatch(int minScore) => Score >= minScore;

    /// <summary>Sentinel for a zero-score non-match.</summary>
    internal static readonly DetectionResult NoMatch = new() { Score = 0, MatchedSignals = null };
}