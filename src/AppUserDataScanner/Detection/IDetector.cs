using System.Collections.Frozen;

namespace AppUserDataScanner.Detection;

/// <summary>
/// Contract for a directory detector.
/// Implementations must be stateless and thread-safe.
/// </summary>
internal interface IDetector
{
    /// <summary>Human-readable label for this detector's browser type.</summary>
    string Label { get; }

    FrozenDictionary<string, (int Score, bool isDirectory)> Scores { get; }

    /// <summary>
    /// Evaluates a directory path and returns a scored detection result.
    /// Must be safe to call concurrently from multiple threads.
    /// </summary>
    DetectionResult Evaluate(string directoryPath);
}