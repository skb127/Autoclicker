using System;
using Autoclicker.Core;
using Xunit;

namespace Autoclicker.Tests;

public sealed class AppInfoTests
{
    [Theory]
    [InlineData("0.1.0-dev+c725479abcdef1234", "0.1.0-dev (c725479)")]
    [InlineData("0.1.0+c725479abcdef", "0.1.0 (c725479)")]
    [InlineData("0.1.0+abc", "0.1.0 (abc)")]
    [InlineData("0.1.0+", "0.1.0")]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData("  1.2.3-dev+123456789  ", "1.2.3-dev (1234567)")]
    [InlineData(null, "unknown")]
    [InlineData("", "unknown")]
    [InlineData("   ", "unknown")]
    [InlineData("+c725479", "unknown")]
    public void FormatVersion_FormatsInformationalVersionAsExpected(string? raw, string expected)
    {
        // Arrange
        string? informationalVersion = raw;

        // Act
        string actual = AppInfo.FormatVersion(informationalVersion);

        // Assert
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("0.1.0-dev+c725479", true)]
    [InlineData("0.1.0-DEV", true)]
    [InlineData("0.1.0", false)]
    [InlineData("0.1.0+dev1234", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsDev_DetectsDevelopmentPreReleaseTag(string? raw, bool expected)
    {
        // Arrange
        string? informationalVersion = raw;

        // Act
        bool actual = AppInfo.IsDev(informationalVersion);

        // Assert
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(true, false, null, "Windows")]
    [InlineData(true, false, "wayland", "Windows")]
    [InlineData(false, true, "wayland", "Linux (Wayland)")]
    [InlineData(false, true, "WAYLAND", "Linux (Wayland)")]
    [InlineData(false, true, "x11", "Linux (X11)")]
    [InlineData(false, true, null, "Linux (unknown session)")]
    [InlineData(false, true, "  ", "Linux (unknown session)")]
    [InlineData(false, true, "tty", "Linux (tty)")]
    public void DescribeSession_ReturnsExpectedSessionLabel(
        bool isWindows,
        bool isLinux,
        string? xdgSessionType,
        string expected)
    {
        // Arrange
        bool windows = isWindows;
        bool linux = isLinux;
        string? sessionType = xdgSessionType;

        // Act
        string actual = AppInfo.DescribeSession(windows, linux, sessionType);

        // Assert
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildDiagnostics_IncludesVersionSessionAndLogDirectory()
    {
        // Arrange
        string customLogDir = "/tmp/autoclicker-test-logs";

        // Act
        string diagnostics = AppInfo.BuildDiagnostics(customLogDir);
        string consoleDiagnostics = AppInfo.BuildDiagnostics(null);

        // Assert
        Assert.Contains(AppInfo.ProductName, diagnostics, StringComparison.Ordinal);
        Assert.Contains(AppInfo.DisplayVersion, diagnostics, StringComparison.Ordinal);
        Assert.Contains(AppInfo.SessionDescription, diagnostics, StringComparison.Ordinal);
        Assert.Contains(customLogDir, diagnostics, StringComparison.Ordinal);
        Assert.Contains("console only", consoleDiagnostics, StringComparison.Ordinal);
    }
}
