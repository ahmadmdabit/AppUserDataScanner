using System.Collections.Frozen;
using System.Collections.Generic;

namespace AppUserDataScanner.Detection;

/// <summary>
/// <para>Score-based detector for Chromium-based browser User Data directories.</para>
/// <para>
/// Signal scoring rationale:
///   "Local State" file      → score 3  (very strong: unique to Chromium, JSON with browser state)
///   "Default" directory     → score 2  (strong: default profile directory)
///   "Local Storage" dir     → score 1  (moderate: shared with Electron)
///   "Network" directory     → score 1  (moderate: Chromium network cache dir)
///   "CrashPad" directory    → score 2  (strong: Chromium-specific crash reporter)
///   "BrowserMetrics" dir    → score 1  (moderate: Chromium UMA metrics)
///   "GrShaderCache" dir     → score 1  (moderate: GPU shader cache, Chromium-specific naming)
///   "Crashpad" file/dir     → score 1  (additional crash signal)
/// </para>
/// <para>
/// Max possible score: 12
/// Recommended minimum to report: 2
/// </para>
/// </summary>
internal sealed class ChromiumDetector : BaseDetector
{
    private const string label = "Chromium-based";
    public override string Label => label;

    private static readonly FrozenDictionary<string, (int Score, bool isDirectory)> scores =
        new Dictionary<string, (int Score, bool isDirectory)>()
        {
            // Signal: "Local State" file — weight 3
            ["Local State"] = (3, false),

            // Signal: "Default" directory — weight 2
            ["Default"] = (2, true),

            // Signal: "CrashPad" directory — weight 2
            ["CrashPad"] = (2, true),

            // Signal: "Local Storage" directory — weight 1
            ["Local Storage"] = (1, true),

            // Signal: "Network" directory — weight 1
            ["Network"] = (1, true),

            // Signal: "GrShaderCache" directory — weight 1
            ["GrShaderCache"] = (1, true),

            // Signal: "BrowserMetrics" directory — weight 1
            ["BrowserMetrics"] = (1, true),
        }.ToFrozenDictionary();

    public override FrozenDictionary<string, (int Score, bool isDirectory)> Scores => scores;
}