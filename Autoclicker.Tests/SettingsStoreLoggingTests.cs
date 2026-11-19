using System;
using System.IO;
using System.Linq;
using Autoclicker.Core;
using Autoclicker.Logging;
using Autoclicker.Services;
using Autoclicker.ViewModels;
using Microsoft.Extensions.Logging;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Xunit;

namespace Autoclicker.Tests;

public sealed class SettingsStoreLoggingTests : IDisposable
{
    private readonly string _tempRoot;

    public SettingsStoreLoggingTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "AutoclickerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup of temporary test directories
        }
    }

    [Fact]
    public void Load_WhenSettingsFileIsCorrupt_ReturnsDefaultSettingsAndWritesWarningWithExceptionToLog()
    {
        string settingsPath = Path.Combine(_tempRoot, "settings.json");
        string logDir = Path.Combine(_tempRoot, "logs");
        File.WriteAllText(settingsPath, "{ invalid json");

        AppSettings loaded;
        using (var serilogLogger = AppLogging.CreateLogger(
                   logDir,
                   LogEventLevel.Information,
                   writeToConsole: false,
                   asyncFileSink: false))
        using (var loggerFactory = new SerilogLoggerFactory(serilogLogger, dispose: false))
        {
            var store = new SettingsStore(settingsPath, loggerFactory.CreateLogger<SettingsStore>());
            loaded = store.Load();
        }

        Assert.Equal(new AppSettings().MinIntervalMs, loaded.MinIntervalMs);
        Assert.Equal(new AppSettings().MaxIntervalMs, loaded.MaxIntervalMs);
        Assert.Null(loaded.Pattern);

        string logContents = ReadCombinedLogs(logDir);
        Assert.Contains("[WRN]", logContents, StringComparison.Ordinal);
        Assert.Contains(settingsPath, logContents, StringComparison.Ordinal);
        Assert.Contains("JsonException", logContents, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_WhenDestinationIsDirectory_WritesWarningWithExceptionAndDoesNotThrow()
    {
        string settingsPath = Path.Combine(_tempRoot, "settings.json");
        string conflictingTmpDir = settingsPath + ".tmp";
        string logDir = Path.Combine(_tempRoot, "logs");
        Directory.CreateDirectory(conflictingTmpDir);

        using (var serilogLogger = AppLogging.CreateLogger(
                   logDir,
                   LogEventLevel.Information,
                   writeToConsole: false,
                   asyncFileSink: false))
        using (var loggerFactory = new SerilogLoggerFactory(serilogLogger, dispose: false))
        {
            var store = new SettingsStore(settingsPath, loggerFactory.CreateLogger<SettingsStore>());
            var exception = Record.Exception(() => store.Save(new AppSettings()));
            Assert.Null(exception);
        }

        string logContents = ReadCombinedLogs(logDir);
        Assert.Contains("[WRN]", logContents, StringComparison.Ordinal);
        Assert.Contains(settingsPath, logContents, StringComparison.Ordinal);
        Assert.Contains("Exception", logContents, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveAndLoad_WhenValid_RoundTripsWithoutWarnings()
    {
        string settingsPath = Path.Combine(_tempRoot, "settings.json");
        string logDir = Path.Combine(_tempRoot, "logs");
        var expected = new AppSettings(
            Version: 1,
            MinIntervalMs: 250,
            MaxIntervalMs: 600,
            RandomizeInterval: true,
            Repetitions: 5,
            Button: MouseButtonKind.Right,
            Action: ClickActionKind.Hold,
            HoldMs: 350,
            JitterPx: 4,
            HumanMovement: true,
            MovementSpeed: MovementSpeedKind.Fast,
            Pattern: [new Coordinate(100, 200), new Coordinate(300, 400)]);

        AppSettings loaded;
        using (var serilogLogger = AppLogging.CreateLogger(
                   logDir,
                   LogEventLevel.Information,
                   writeToConsole: false,
                   asyncFileSink: false))
        using (var loggerFactory = new SerilogLoggerFactory(serilogLogger, dispose: false))
        {
            var store = new SettingsStore(settingsPath, loggerFactory.CreateLogger<SettingsStore>());
            store.Save(expected);
            loaded = store.Load();
        }

        Assert.Equal(expected.Version, loaded.Version);
        Assert.Equal(expected.MinIntervalMs, loaded.MinIntervalMs);
        Assert.Equal(expected.MaxIntervalMs, loaded.MaxIntervalMs);
        Assert.Equal(expected.RandomizeInterval, loaded.RandomizeInterval);
        Assert.Equal(expected.Repetitions, loaded.Repetitions);
        Assert.Equal(expected.Button, loaded.Button);
        Assert.Equal(expected.Action, loaded.Action);
        Assert.Equal(expected.HoldMs, loaded.HoldMs);
        Assert.Equal(expected.JitterPx, loaded.JitterPx);
        Assert.Equal(expected.HumanMovement, loaded.HumanMovement);
        Assert.Equal(expected.MovementSpeed, loaded.MovementSpeed);
        Assert.NotNull(loaded.Pattern);
        Assert.Equal(expected.Pattern!, loaded.Pattern);

        string logContents = ReadCombinedLogs(logDir);
        Assert.Contains("[INF]", logContents, StringComparison.Ordinal);
        Assert.Contains("Saved settings", logContents, StringComparison.Ordinal);
        Assert.Contains("Loaded settings", logContents, StringComparison.Ordinal);
        Assert.DoesNotContain("[WRN]", logContents, StringComparison.Ordinal);
        Assert.DoesNotContain("[ERR]", logContents, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_WhenLegacyJsonContainsWaylandHotkeyConfirmed_IgnoresObsoletePropertyCleanly()
    {
        string settingsPath = Path.Combine(_tempRoot, "settings.json");
        string logDir = Path.Combine(_tempRoot, "logs");
        File.WriteAllText(
            settingsPath,
            """
            {
              "version": 1,
              "minIntervalMs": 300,
              "maxIntervalMs": 500,
              "randomizeInterval": true,
              "repetitions": 2,
              "button": "Left",
              "action": "Click",
              "holdMs": 200,
              "jitterPx": 0,
              "humanMovement": false,
              "movementSpeed": "Normal",
              "pattern": [{ "x": 10, "y": 20 }],
              "waylandHotkeyConfirmed": true
            }
            """);

        AppSettings loaded;
        using (var serilogLogger = AppLogging.CreateLogger(
                   logDir,
                   LogEventLevel.Information,
                   writeToConsole: false,
                   asyncFileSink: false))
        using (var loggerFactory = new SerilogLoggerFactory(serilogLogger, dispose: false))
        {
            var store = new SettingsStore(settingsPath, loggerFactory.CreateLogger<SettingsStore>());
            loaded = store.Load();
        }

        Assert.Equal(300, loaded.MinIntervalMs);
        Assert.Equal(500, loaded.MaxIntervalMs);
        Assert.Single(loaded.Pattern!);

        string logContents = ReadCombinedLogs(logDir);
        Assert.DoesNotContain("[WRN]", logContents, StringComparison.Ordinal);
        Assert.DoesNotContain("[ERR]", logContents, StringComparison.Ordinal);
    }

    private static string ReadCombinedLogs(string logDir)
    {
        if (!Directory.Exists(logDir))
        {
            return string.Empty;
        }

        string[] logFiles = Directory.GetFiles(logDir, "autoclicker-*.log");
        return string.Join(Environment.NewLine, logFiles.Select(File.ReadAllText));
    }
}
