using System;
using System.IO;
using System.Runtime.InteropServices;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Autoclicker.Logging;

/// <summary>
/// Configures and manages the application-wide Serilog logger lifecycle.
/// </summary>
public static class AppLogging
{
    public const string LogLevelEnvironmentVariable = "AUTOCLICKER_LOG_LEVEL";

    public const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    private const long FileSizeLimitBytes = 5 * 1024 * 1024;
    private const int RetainedFileCountLimit = 7;

    /// <summary>
    /// Directory where rolling log files are currently written, or <c>null</c> when running console-only.
    /// </summary>
    public static string? ActiveLogDirectory { get; private set; }

    /// <summary>
    /// Active minimum Serilog event level resolved during initialization.
    /// </summary>
    public static LogEventLevel MinimumLevel { get; private set; } = LogEventLevel.Information;

    /// <summary>
    /// Initializes the global Serilog logger using <see cref="LogLevelEnvironmentVariable"/>
    /// and OS-appropriate rolling file paths, and writes a startup diagnostic banner.
    /// </summary>
    public static void Initialize()
    {
        MinimumLevel = ParseMinimumLevel(Environment.GetEnvironmentVariable(LogLevelEnvironmentVariable));
        ActiveLogDirectory = LogPaths.PrepareDirectory();

        Log.Logger = CreateLogger(ActiveLogDirectory, MinimumLevel);

        string version = typeof(AppLogging).Assembly.GetName().Version?.ToString() ?? "unknown";
        string? rawSessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        string sessionType = string.IsNullOrWhiteSpace(rawSessionType) ? "N/A" : rawSessionType;

        Log.ForContext(typeof(AppLogging)).Information(
            "Autoclicker starting. Version={Version}, OS={OS}, Runtime={Runtime}, SessionType={SessionType}, LogLevel={LogLevel}, LogDirectory={LogDirectory}",
            version,
            RuntimeInformation.OSDescription,
            RuntimeInformation.FrameworkDescription,
            sessionType,
            MinimumLevel,
            ActiveLogDirectory ?? "(console only)");
    }

    /// <summary>
    /// Logs a fatal unhandled termination exception before process shutdown.
    /// </summary>
    public static void LogUnhandledTermination(Exception exception)
    {
        Log.ForContext(typeof(AppLogging)).Fatal(exception, "Autoclicker terminated unexpectedly");
    }

    /// <summary>
    /// Logs application shutdown and flushes all buffered log sinks.
    /// </summary>
    public static void Shutdown()
    {
        Log.ForContext(typeof(AppLogging)).Information("Autoclicker shutting down");
        Log.CloseAndFlush();
    }

    internal static LogEventLevel ParseMinimumLevel(string? rawLevel)
    {
        if (string.IsNullOrWhiteSpace(rawLevel))
        {
            return LogEventLevel.Information;
        }

        return rawLevel.Trim().ToLowerInvariant() switch
        {
            "verbose" or "trace" => LogEventLevel.Verbose,
            "debug" => LogEventLevel.Debug,
            "information" or "info" => LogEventLevel.Information,
            "warning" or "warn" => LogEventLevel.Warning,
            "error" => LogEventLevel.Error,
            "fatal" or "critical" => LogEventLevel.Fatal,
            _ => LogEventLevel.Information
        };
    }

    internal static Logger CreateLogger(
        string? logDirectory,
        LogEventLevel minimumLevel,
        bool writeToConsole = true,
        bool asyncFileSink = true)
    {
        var configuration = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .Enrich.FromLogContext();

        if (writeToConsole)
        {
            configuration = configuration.WriteTo.Console(outputTemplate: OutputTemplate);
        }

        if (!string.IsNullOrWhiteSpace(logDirectory))
        {
            string filePath = Path.Combine(logDirectory, "autoclicker-.log");

            if (asyncFileSink)
            {
                configuration = configuration.WriteTo.Async(a => a.File(
                    path: filePath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: RetainedFileCountLimit,
                    fileSizeLimitBytes: FileSizeLimitBytes,
                    rollOnFileSizeLimit: true,
                    shared: true,
                    outputTemplate: OutputTemplate));
            }
            else
            {
                configuration = configuration.WriteTo.File(
                    path: filePath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: RetainedFileCountLimit,
                    fileSizeLimitBytes: FileSizeLimitBytes,
                    rollOnFileSizeLimit: true,
                    shared: true,
                    outputTemplate: OutputTemplate);
            }
        }

        return configuration.CreateLogger();
    }
}
