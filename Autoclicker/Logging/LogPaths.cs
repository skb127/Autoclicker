using System;
using System.IO;

namespace Autoclicker.Logging;

/// <summary>
/// Resolves and prepares OS-appropriate local directories for application log files.
/// </summary>
internal static class LogPaths
{
    private const string AppFolderName = "Autoclicker";
    private const string LogsFolderName = "logs";

    /// <summary>
    /// Resolves the preferred log directory for the current operating system:
    /// <c>%LOCALAPPDATA%\Autoclicker\logs</c> on Windows, or
    /// <c>$XDG_STATE_HOME/Autoclicker/logs</c> (default <c>~/.local/state/Autoclicker/logs</c>) on Unix.
    /// </summary>
    public static string ResolvePreferredDirectory()
    {
        string baseDir = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : ResolveXdgStateHome(
                Environment.GetEnvironmentVariable("XDG_STATE_HOME"),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        return Path.Combine(baseDir, AppFolderName, LogsFolderName);
    }

    /// <summary>
    /// Resolves the XDG state home directory according to the XDG Base Directory Specification,
    /// using <paramref name="xdgStateHomeEnv"/> only when it is a fully qualified path.
    /// </summary>
    internal static string ResolveXdgStateHome(string? xdgStateHomeEnv, string userProfilePath)
    {
        if (!string.IsNullOrWhiteSpace(xdgStateHomeEnv) && Path.IsPathFullyQualified(xdgStateHomeEnv))
        {
            return xdgStateHomeEnv;
        }

        return Path.Combine(userProfilePath, ".local", "state");
    }

    /// <summary>
    /// Attempts to create the preferred log directory (with owner-only permissions on Unix),
    /// falling back to the OS temporary directory if creation fails. Never throws.
    /// </summary>
    public static string? PrepareDirectory(string? preferredDirectory = null)
    {
        string preferred = preferredDirectory ?? ResolvePreferredDirectory();
        if (TryCreateSecureDirectory(preferred))
        {
            return preferred;
        }

        string fallback = Path.Combine(Path.GetTempPath(), AppFolderName, LogsFolderName);
        if (TryCreateSecureDirectory(fallback))
        {
            return fallback;
        }

        return null;
    }

    /// <summary>
    /// Ensures <paramref name="directoryPath"/> exists and enforces owner-only permissions (<c>0700</c>) on Unix.
    /// </summary>
    internal static bool TryCreateSecureDirectory(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return false;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directoryPath);
                return true;
            }

            const UnixFileMode ownerOnlyMode =
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

            bool alreadyExists = Directory.Exists(directoryPath);
            Directory.CreateDirectory(directoryPath, ownerOnlyMode);

            if (alreadyExists)
            {
                File.SetUnixFileMode(directoryPath, ownerOnlyMode);
            }

            return true;
        }
        catch
        {
            // Logging directory setup must never crash the application.
            return false;
        }
    }
}
