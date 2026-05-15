using System;
using System.IO;
using System.IO.Enumeration;
using System.Linq;

namespace AppUserDataScanner.Infrastructure;

/// <summary>
/// High-performance, zero-allocation directory size calculator.
/// </summary>
internal static class DirectorySizeCalculator
{
    private static readonly EnumerationOptions options = new EnumerationOptions
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        // Skip ReparsePoints (symlinks/junctions) to prevent infinite recursion
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline
    };

    /// <summary>
    /// Enumerates file lengths directly from OS filesystem buffers without allocating
    /// FileInfo or string paths per file. Automatically handles inaccessible paths.
    /// </summary>
    internal static long CalculateSizeBytes(string directoryPath)
    {
        try
        {
            return new FileSystemEnumerable<long>(
                directoryPath,
                (ref entry) => entry.Length,
                options)
            {
                ShouldIncludePredicate = (ref entry) => !entry.IsDirectory
            }.Sum();
        }
        catch (Exception)
        {
            // Root directory access denied or deleted between check and enumeration
            return 0;
        }
    }

    internal static ReadOnlySpan<char> FormatBytes(long bytes)
    {
        if (bytes >= 1073741824)
            return $"{bytes / 1073741824.0:F2} GB";

        if (bytes >= 1048576)
            return $"{bytes / 1048576.0:F2} MB";

        if (bytes >= 1024)
            return $"{bytes / 1024.0:F2} KB";

        return $"{bytes} B";
    }
}