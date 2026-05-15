using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace AppUserDataScanner.Infrastructure;

/// <summary>
/// Static read-only skip-list of directory names that should never be
/// recursed into during a full-drive scan on Windows 10/11.
/// Uses IReadOnlySet for optimal read-only lookup performance.
/// </summary>
internal static class SkipList
{
    /// <summary>
    /// <para>
    /// Directories to prune with an estimated density score (1-5).
    /// Score represents the volume of files/sub-dirs typically contained.
    /// </para>
    /// <para>
    /// <strong>Score Rubric (1-5)</strong>
    /// <list type="bullet">
    /// <item>5 (Extreme): Massive file counts, deep recursion, or high churn (e.g., `node_modules`, `WinSxS`, `Packages`).</item>
    /// <item>4 (High): Significant directory structures and many small files (e.g., `Program Files`, `.git`, `.gradle`).</item>
    /// <item>3 (Medium): Moderate complexity; mix of binaries and configuration (e.g., `bin`, `obj`, `SoftwareDistribution`).</item>
    /// <item>2 (Low): Mostly flat or contains fewer, larger files (e.g., `drivers`, `Prefetch`, `Music`).</item>
    /// <item>1 (Minimal): Very low file count or often near-empty (e.g., `$WinREAgent`, `.DS_Store`).</item>
    /// </list>
    /// </para>
    /// </summary>
    internal static readonly FrozenDictionary<string, int> DirectoryScores =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            // Windows OS internals
            { "assembly", 4 },
            { "CbsTemp", 1 },
            { "drivers", 2 },
            { "inf", 3 },
            { "Logs", 3 },
            { "Microsoft.NET", 4 },
            { "Panther", 2 },
            { "Prefetch", 2 },
            { "servicing", 3 },
            { "SoftwareDistribution", 4 },
            { "System32", 5 },
            { "SysWOW64", 5 },
            { "Tasks", 2 },
            { "Temp", 5 },
            { "Windows", 5 },
            { "WinSxS", 5 },

            // Windows special shell / recycle
            { "$Recycle.Bin", 3 },
            { "$WINDOWS.~BT", 4 },
            { "$WINDOWS.~WS", 4 },
            { "$WinREAgent", 1 },
            { "Recovery", 1 },
            { "System Volume Information", 2 },

            // Program Files
            { "Program Files (x86)", 5 },
            { "Program Files", 5 },

            // Common dev/tooling (The "Bloat" Category)
            { "__pycache__", 2 },
            { ".cargo", 4 },
            { ".git", 4 },
            { ".gradle", 5 },
            { ".hg", 3 },
            { ".idea", 3 },
            { ".m2", 4 },
            { ".mypy_cache", 2 },
            { ".svn", 3 },
            { ".vs", 4 },
            { "bin", 3 },
            { "build", 3 },
            { "dist", 3 },
            { "node_modules", 5 },
            { "obj", 3 },
            { "target", 4 },

            // Package caches
            { "npm", 4 },
            { "NuGet", 4 },
            { "pip", 3 },
            { "yarn", 4 },

            // Virtual environments
            { ".env", 2 },
            { ".venv", 4 },
            { "env", 4 },
            { "venv", 4 },

            // macOS artifacts
            { ".DS_Store", 1 },

            // Windows Installer
            { "Installer", 5 },
            { "MsiExec", 2 },

            // Game/media
            { "Downloads", 4 },
            { "Music", 2 },
            { "Pictures", 3 },
            { "Steam", 3 },
            { "steamapps", 5 },
            { "Videos", 2 },

            // Large IDE / tooling caches
            { ".cache", 5 },
            { ".nuget", 4 },
            { ".vscode", 3 },

            // Common large data folders
            { "Archives", 2 },
            { "Backups", 3 },

            // Critical: AppData heavy hitters
            { "Code", 4 },
            { "Code - Insiders", 4 },
            { "Packages", 5 },

            // Electron / dev cache explosions
            { "Cache", 5 },
            { "CachedData", 4 },
            { "GPUCache", 3 },

            // Browser irrelevant heavy storage
            { "File System", 4 },
            { "IndexedDB", 4 },
            { "Service Worker", 4 },
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
}