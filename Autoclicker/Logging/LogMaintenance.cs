using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Autoclicker.Logging;

/// <summary>
/// Summary of log files found in the active log directory.
/// </summary>
internal readonly record struct LogFolderStats(int FileCount, long TotalBytes);

/// <summary>
/// Result of deleting older log files while preserving the most recent log file.
/// </summary>
internal readonly record struct LogCleanupResult(int DeletedFiles, long FreedBytes, int Failed);

/// <summary>
/// Inspects and cleans up <c>autoclicker-*.log</c> rolling log files.
/// </summary>
internal static class LogMaintenance
{
    private const string LogFilePattern = "autoclicker-*.log";

    /// <summary>
    /// Counts <c>autoclicker-*.log</c> files and sums their sizes inside <paramref name="directory"/>.
    /// Never throws; returns zero stats if the directory is missing or inaccessible.
    /// </summary>
    public static LogFolderStats GetStats(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return new LogFolderStats(0, 0);
        }

        try
        {
            var dirInfo = new DirectoryInfo(directory);
            var files = dirInfo.GetFiles(LogFilePattern, SearchOption.TopDirectoryOnly);

            int count = 0;
            long totalBytes = 0;

            foreach (var file in files)
            {
                try
                {
                    count++;
                    totalBytes += file.Length;
                }
                catch
                {
                    // Ignore files that disappear or cannot be stat'd concurrently.
                }
            }

            return new LogFolderStats(count, totalBytes);
        }
        catch
        {
            return new LogFolderStats(0, 0);
        }
    }

    /// <summary>
    /// Deletes every <c>autoclicker-*.log</c> file in <paramref name="directory"/> except the most
    /// recently modified one (the active session log). Never throws; locked files are counted in
    /// <see cref="LogCleanupResult.Failed"/>.
    /// </summary>
    public static LogCleanupResult DeleteOldLogs(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return new LogCleanupResult(0, 0, 0);
        }

        FileInfo[] files;
        try
        {
            var dirInfo = new DirectoryInfo(directory);
            files = dirInfo.GetFiles(LogFilePattern, SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return new LogCleanupResult(0, 0, 0);
        }

        if (files.Length <= 1)
        {
            return new LogCleanupResult(0, 0, 0);
        }

        var oldFiles = files
            .OrderByDescending(f => SafeGetLastWriteTimeUtc(f))
            .ThenByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Skip(1);

        int deleted = 0;
        long freedBytes = 0;
        int failed = 0;

        foreach (var file in oldFiles)
        {
            long length = 0;
            try
            {
                length = file.Length;
            }
            catch
            {
                // Ignore if length cannot be read before deletion.
            }

            try
            {
                file.Delete();
                deleted++;
                freedBytes += length;
            }
            catch
            {
                failed++;
            }
        }

        return new LogCleanupResult(deleted, freedBytes, failed);
    }

    /// <summary>
    /// Formats a byte count into a compact human-readable string (B, KB, MB, GB).
    /// </summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        if (bytes < 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");
        }

        double kb = bytes / 1024.0;
        if (kb < 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{kb:0.#} KB");
        }

        double mb = kb / 1024.0;
        if (mb < 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{mb:0.#} MB");
        }

        double gb = mb / 1024.0;
        return string.Create(CultureInfo.InvariantCulture, $"{gb:0.##} GB");
    }

    /// <summary>
    /// Formats <see cref="LogFolderStats"/> as <c>"N file(s) · X.Y KB"</c>.
    /// </summary>
    public static string FormatStats(LogFolderStats stats)
    {
        string fileWord = stats.FileCount == 1 ? "file" : "files";
        return $"{stats.FileCount} {fileWord} · {FormatBytes(stats.TotalBytes)}";
    }

    private static DateTime SafeGetLastWriteTimeUtc(FileInfo file)
    {
        try
        {
            return file.LastWriteTimeUtc;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }
}
