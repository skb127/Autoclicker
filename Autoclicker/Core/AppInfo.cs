using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;

namespace Autoclicker.Core;

/// <summary>
/// Provides formatted application version metadata and system diagnostic information.
/// </summary>
public static class AppInfo
{
    private const int ShortCommitHashLength = 7;

    public const string ProductName = "Autoclicker";
    public const string RepositoryUrl = "https://github.com/skb127/Autoclicker";

    private static readonly string? RawInformationalVersion =
        typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString();

    /// <summary>
    /// Human-readable application version (e.g. <c>0.1.0-dev (c725479)</c>).
    /// </summary>
    public static string DisplayVersion { get; } = FormatVersion(RawInformationalVersion);

    /// <summary>
    /// True when the current build carries a <c>-dev</c> pre-release suffix.
    /// </summary>
    public static bool IsDevelopmentBuild { get; } = IsDev(RawInformationalVersion);

    /// <summary>
    /// Operating system description reported by the runtime.
    /// </summary>
    public static string OsDescription { get; } =
        $"{RuntimeInformation.OSDescription.Trim()} ({RuntimeInformation.OSArchitecture})";

    /// <summary>
    /// Display server / session description (e.g. <c>Linux (Wayland)</c>, <c>Linux (X11)</c>, <c>Windows</c>).
    /// </summary>
    public static string SessionDescription { get; } = DescribeSession(
        OperatingSystem.IsWindows(),
        OperatingSystem.IsLinux(),
        Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
        RuntimeInformation.OSDescription);

    /// <summary>
    /// Active .NET runtime description (e.g. <c>.NET 8.0.x</c>).
    /// </summary>
    public static string DotNetRuntime { get; } = RuntimeInformation.FrameworkDescription;

    /// <summary>
    /// Loaded Avalonia UI framework version.
    /// </summary>
    public static string AvaloniaVersion { get; } = ResolveAvaloniaVersion();

    /// <summary>
    /// Formats a raw <see cref="AssemblyInformationalVersionAttribute.InformationalVersion"/> string
    /// by shortening any <c>+commit</c> build metadata to 7 characters in parentheses.
    /// </summary>
    internal static string FormatVersion(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return "unknown";
        }

        string trimmed = informationalVersion.Trim();
        int plusIndex = trimmed.IndexOf('+');
        if (plusIndex < 0)
        {
            return trimmed;
        }

        string versionPart = trimmed[..plusIndex].Trim();
        if (string.IsNullOrEmpty(versionPart))
        {
            return "unknown";
        }

        string metadataPart = trimmed[(plusIndex + 1)..].Trim();
        if (string.IsNullOrEmpty(metadataPart))
        {
            return versionPart;
        }

        string shortMetadata = metadataPart.Length > ShortCommitHashLength
            ? metadataPart[..ShortCommitHashLength]
            : metadataPart;

        return $"{versionPart} ({shortMetadata})";
    }

    /// <summary>
    /// Returns true when <paramref name="informationalVersion"/> contains a <c>-dev</c> pre-release tag.
    /// </summary>
    internal static bool IsDev(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return false;
        }

        string trimmed = informationalVersion.Trim();
        int plusIndex = trimmed.IndexOf('+');
        string versionPart = plusIndex >= 0 ? trimmed[..plusIndex] : trimmed;

        return versionPart.Contains("-dev", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Produces a user-friendly session label for the given platform flags and <c>XDG_SESSION_TYPE</c>.
    /// </summary>
    internal static string DescribeSession(
        bool isWindows,
        bool isLinux,
        string? xdgSessionType,
        string? osDescription = null)
    {
        if (isWindows)
        {
            return "Windows";
        }

        if (isLinux)
        {
            string normalized = xdgSessionType?.Trim().ToLowerInvariant() ?? string.Empty;
            return normalized switch
            {
                "wayland" => "Linux (Wayland)",
                "x11" => "Linux (X11)",
                "" => "Linux (unknown session)",
                _ => $"Linux ({xdgSessionType!.Trim()})"
            };
        }

        return !string.IsNullOrWhiteSpace(osDescription)
            ? osDescription.Trim()
            : "Unknown OS";
    }

    /// <summary>
    /// Builds a multi-line diagnostic block suitable for copying into bug reports.
    /// </summary>
    public static string BuildDiagnostics(string? logDirectory)
    {
        string? desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
        string logDirDisplay = string.IsNullOrWhiteSpace(logDirectory) ? "console only" : logDirectory;

        var sb = new StringBuilder();
        sb.AppendLine($"{ProductName}: {DisplayVersion}");
        sb.AppendLine($"OS: {OsDescription}");
        sb.AppendLine($"Session: {SessionDescription}");
        if (!string.IsNullOrWhiteSpace(desktop))
        {
            sb.AppendLine($"Desktop: {desktop.Trim()}");
        }

        sb.AppendLine($".NET Runtime: {DotNetRuntime}");
        sb.AppendLine($"Avalonia: {AvaloniaVersion}");
        sb.Append($"Log Directory: {logDirDisplay}");
        return sb.ToString();
    }

    private static string ResolveAvaloniaVersion()
    {
        var assembly = typeof(Application).Assembly;
        string? infoVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(infoVersion))
        {
            return FormatVersion(infoVersion);
        }

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
