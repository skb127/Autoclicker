using System.Collections.Generic;
using System.IO;
using Autoclicker.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;
using AvaloniaLogEventLevel = Avalonia.Logging.LogEventLevel;

namespace Autoclicker.Tests;

public sealed class LogPathsAndLevelTests
{
    [Fact]
    public void ResolveXdgStateHome_WhenFullyQualified_ReturnsEnvVarValue()
    {
        // Arrange
        string expected = Path.Combine(Path.GetTempPath(), "custom-xdg-state");
        string userProfile = Path.Combine(Path.GetTempPath(), "fake-home");

        // Act
        string resolved = LogPaths.ResolveXdgStateHome(expected, userProfile);

        // Assert
        Assert.Equal(expected, resolved);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative/state/path")]
    public void ResolveXdgStateHome_WhenMissingOrRelative_FallsBackToDotLocalState(string? xdgStateHome)
    {
        // Arrange
        string userProfile = Path.Combine(Path.GetTempPath(), "fake-home");
        string expected = Path.Combine(userProfile, ".local", "state");

        // Act
        string resolved = LogPaths.ResolveXdgStateHome(xdgStateHome, userProfile);

        // Assert
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
        // Arrange
        string? input = rawLevel;

        // Act
        LogEventLevel parsed = AppLogging.ParseMinimumLevel(input);

        // Assert
        Assert.Equal(expected, parsed);
    }

    [Fact]
    public void AvaloniaSerilogSink_StripsEmbeddedNullCharactersFromTemplateAndStringProperties()
    {
        // Arrange
        var captured = new List<LogEvent>();
        using var serilogLogger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(new CapturingSink(captured))
            .CreateLogger();

        var sink = new AvaloniaSerilogSink(AvaloniaLogEventLevel.Warning, serilogLogger);
        object?[] propertyValues = ["SESSION_MANAGER\0\0\0", 42];

        // Act
        sink.Log(
            AvaloniaLogEventLevel.Warning,
            "X11Platform",
            source: null,
            "SMLib error:\0\0 fixed buffer\0");

        sink.Log(
            AvaloniaLogEventLevel.Error,
            "X11Platform",
            source: null,
            "Native\0 failure: {Reason} (code {Code})\0",
            propertyValues);

        // Assert
        Assert.Equal(2, captured.Count);
        Assert.Equal("SMLib error: fixed buffer", captured[0].MessageTemplate.Text);
        Assert.Equal("SMLib error: fixed buffer", captured[0].RenderMessage());

        Assert.Equal("Native failure: {Reason} (code {Code})", captured[1].MessageTemplate.Text);
        Assert.Equal("Native failure: \"SESSION_MANAGER\" (code 42)", captured[1].RenderMessage());
        Assert.DoesNotContain('\0', captured[1].RenderMessage());
        Assert.Equal("SESSION_MANAGER\0\0\0", propertyValues[0]);
    }

    private sealed class CapturingSink(List<LogEvent> events) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => events.Add(logEvent);
    }
}
