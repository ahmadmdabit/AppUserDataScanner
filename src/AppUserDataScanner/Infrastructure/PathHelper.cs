using System;
using System.Collections.Generic;
using System.IO;

namespace AppUserDataScanner.Infrastructure;

/// <summary>
/// Zero-allocation path utilities using ReadOnlySpan&lt;char&gt;.
/// </summary>
internal static class PathHelper
{
    private static readonly IReadOnlySet<string> shouldSkipDirectories =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            $"{Path.DirectorySeparatorChar}Packages{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}OneDrive{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}AppData{Path.DirectorySeparatorChar}Local{Path.DirectorySeparatorChar}Microsoft{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}AppData{Path.DirectorySeparatorChar}Local{Path.DirectorySeparatorChar}Temp{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}WindowsApps{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}ProgramData{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}$Recycle.Bin{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}System Volume Information{Path.DirectorySeparatorChar}",
        };

    /// <summary>
    /// Extracts the last path segment (directory name) from a full path
    /// without allocating a new string.
    /// Equivalent to Path.GetFileName but returns a span over the original buffer.
    /// </summary>
    internal static ReadOnlySpan<char> GetDirectoryNameSpan(ReadOnlySpan<char> fullPath)
    {
        // Trim any trailing separator to handle paths like "C:\Foo\"
        ReadOnlySpan<char> path = fullPath.TrimEnd(Path.DirectorySeparatorChar);

        int lastSep = path.LastIndexOf(Path.DirectorySeparatorChar);
        return lastSep < 0 ? path : path[(lastSep + 1)..];
    }

    /// <summary>
    /// Returns true if the directory name of the given full path is found
    /// in the skip list — using zero-allocation span comparison.
    /// </summary>
    internal static bool ShouldSkip(ReadOnlySpan<char> fullPath)
    {
        ReadOnlySpan<char> name = GetDirectoryNameSpan(fullPath);

        // 🔴 HARD PATH PRE-FILTER (zero I/O, prevents NTFS hangs)
        // Avoid known pathological Windows directories
        foreach (var directoryName in shouldSkipDirectories)
        {
            if (fullPath.IndexOf(directoryName, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        // .NET 9+ AlternateLookup allows true zero-allocation span checks against IReadOnlySet<string>
        return SkipList.DirectoryScores.GetAlternateLookup<ReadOnlySpan<char>>().ContainsKey(name);
    }

    /// <summary>
    /// Computes the depth of a path relative to a root by counting
    /// directory separator characters — no allocation.
    /// </summary>
    internal static int GetDepth(ReadOnlySpan<char> fullPath, ReadOnlySpan<char> root)
    {
        if (fullPath.Length <= root.Length)
            return 0;

        ReadOnlySpan<char> relative = fullPath[root.Length..];
        int depth = 0;
        foreach (char c in relative)
        {
            if (c == Path.DirectorySeparatorChar)
                depth++;
        }
        return depth;
    }
}