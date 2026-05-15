using System.Collections.Frozen;
using System.Collections.Generic;

namespace AppUserDataScanner.Detection;

/// <summary>
/// <para>Score-based detector for Electron-based application User Data directories.</para>
/// <para>
/// Electron apps store user data in %APPDATA%\&lt;AppName&gt;\ directly,
/// NOT inside a "User Data" subdirectory like Chromium does.
/// </para>
/// <para>
/// Signal scoring rationale:
///   "Preferences" file        → score 3  (very strong: root-level JSON prefs, Electron convention)
///   "Local Storage" dir       → score 2  (strong: Electron stores LevelDB here at root level)
///   "app.asar" file           → score 3  (very strong: Electron app package format)
///   "package.json" file       → score 2  (strong: Node/Electron app manifest)
///   "resources" directory     → score 1  (moderate: Electron app resources dir)
///   "Session Storage" dir     → score 1  (moderate: Electron session data)
///   "IndexedDB" dir           → score 1  (moderate: Electron web storage)
///   "blob_storage" dir        → score 1  (moderate: Chromium/Electron blob cache)
///   "GPUCache" dir            → score 1  (moderate: GPU cache present in both but common in Electron)
///   "Cache" dir               → score 0  (too generic — not scored)
/// </para>
/// <para>
/// Max possible score: 15
/// Recommended minimum to report: 2
/// </para>
/// <para>
/// NOTE: We intentionally do NOT score "Default" directory here because
/// that would overlap with the Chromium detector.
/// </para>
/// </summary>
internal sealed class ElectronDetector : BaseDetector
{
    private const string label = "Electron-based";
    public override string Label => label;

    private static readonly FrozenDictionary<string, (int Score, bool isDirectory)> scores =
        new Dictionary<string, (int Score, bool isDirectory)>()
        {
            // Signal: "Preferences" file at root level — weight 3
            // Electron stores preferences directly at root, NOT inside Default\
            ["Preferences"] = (3, false),

            // Signal: "app.asar" file — weight 3
            // Electron application archive format, strongest possible signal
            ["app.asar"] = (3, false),

            // Signal: "package.json" file — weight 2
            // Node.js / Electron application manifest
            ["package.json"] = (2, false),

            // Signal: "Local Storage" directory at root level — weight 2
            ["Local Storage"] = (2, true),

            // Signal: "Session Storage" directory — weight 1
            ["Session Storage"] = (1, true),

            // Signal: "IndexedDB" directory — weight 1
            ["IndexedDB"] = (1, true),

            // Signal: "blob_storage" directory — weight 1
            ["blob_storage"] = (1, true),

            // Signal: "GPUCache" directory — weight 1
            ["GPUCache"] = (1, true),

            // Signal: "resources" directory — weight 1
            ["resources"] = (1, true),
        }.ToFrozenDictionary();

    public override FrozenDictionary<string, (int Score, bool isDirectory)> Scores => scores;
}