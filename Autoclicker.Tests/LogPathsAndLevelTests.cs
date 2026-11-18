using System;
using System.IO;
using Autoclicker.Logging;
using Serilog.Events;
using Xunit;

namespace Autoclicker.Tests;

public sealed class LogPathsAndLevelTests
{
    [Fact]
    public void ResolveXdgStateHome_WhenFullyQualified_ReturnsEnvVarValue()
    {
        string expected = Path.Combine(Path.GetTempPath(), "custom-xdg-state");
        string userProfile = Path.Combine(Path.GetTempPath(), "fake-home");

        string resolved = LogPaths.ResolveXdgStateHome(expected, userProfile);

        Assert.Equal(expected, resolved);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative/state/path")]
    public void ResolveXdgStateHome_WhenMissingOrRelative_FallsBackToDotLocalState(string? xdgStateHome)
    {
        string userProfile = Path.Combine(Path.GetTempPath(), "fake-home");
        string expected = Path.Combine(userProfile, ".local", "state");

        string resolved = LogPaths.ResolveXdgStateHome(xdgStateHome, userProfile);

        Assert.Equal(expected, resolved);
    }

    [Theory]
    [InlineData("Verbose", LogEventLevel.Verbose)]
    [InlineData("trace", LogEventLevel.Verbose)]
    [InlineData("  DEBUG ", LogEventLevel.Debug)]
    [InlineData("Information", LogEventLevel.Information)]
    [InlineData("info", LogEventLevel.Information)]
    [InlineData("Warning", LogEventLevel.Warning)]
    [InlineData("warn", LogEventLevel.Warning)]
    [InlineData("Error", LogEventLevel.Error)]
    [InlineData("Fatal", LogEventLevel.Fatal)]
    [InlineData("critical", LogEventLevel.Fatal)]
    [InlineData(null, LogEventLevel.Information)]
    [InlineData("", LogEventLevel.Information)]
    [InlineData("invalid-level", LogEventLevel.Information)]
    public void ParseMinimumLevel_ResolvesExpectedSerilogLevel(string? rawLevel, LogEventLevel expected)
    {
        LogEventLevel parsed = AppLogging.ParseMinimumLevel(rawLevel);

        Assert.Equal(expected, parsed);
    }
}
