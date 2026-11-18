using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Logging;
using Serilog;
using AvaloniaLogEventLevel = Avalonia.Logging.LogEventLevel;
using SerilogLogEventLevel = Serilog.Events.LogEventLevel;

namespace Autoclicker.Logging;

/// <summary>
/// Bridges Avalonia internal log messages into Serilog while preserving message templates and areas.
/// </summary>
internal sealed class AvaloniaSerilogSink : ILogSink
{
    private readonly AvaloniaLogEventLevel _minimumLevel;
    private readonly HashSet<string>? _areas;

    public AvaloniaSerilogSink(AvaloniaLogEventLevel minimumLevel, params string[] areas)
    {
        _minimumLevel = minimumLevel;
        _areas = areas is { Length: > 0 }
            ? new HashSet<string>(areas, StringComparer.OrdinalIgnoreCase)
            : null;
    }

    public bool IsEnabled(AvaloniaLogEventLevel level, string area)
    {
        if (level < _minimumLevel)
        {
            return false;
        }

        if (_areas is not null && !_areas.Contains(area))
        {
            return false;
        }

        return Serilog.Log.IsEnabled(ToSerilogLevel(level));
    }

    public void Log(AvaloniaLogEventLevel level, string area, object? source, string messageTemplate)
    {
        if (!IsEnabled(level, area))
        {
            return;
        }

        Write(level, area, source, messageTemplate);
    }

    public void Log(
        AvaloniaLogEventLevel level,
        string area,
        object? source,
        string messageTemplate,
        params object?[] propertyValues)
    {
        if (!IsEnabled(level, area))
        {
            return;
        }

        Write(level, area, source, messageTemplate, propertyValues);
    }

    private static void Write(
        AvaloniaLogEventLevel level,
        string area,
        object? source,
        string messageTemplate,
        params object?[] propertyValues)
    {
        string sourceContext = string.IsNullOrWhiteSpace(area)
            ? "Avalonia"
            : $"Avalonia.{area}";

        ILogger logger = Serilog.Log.ForContext("SourceContext", sourceContext);
        if (source is not null)
        {
            logger = logger.ForContext("AvaloniaSource", source.GetType().Name);
        }

        logger.Write(ToSerilogLevel(level), messageTemplate, propertyValues);
    }

    private static SerilogLogEventLevel ToSerilogLevel(AvaloniaLogEventLevel level) => level switch
    {
        AvaloniaLogEventLevel.Verbose => SerilogLogEventLevel.Verbose,
        AvaloniaLogEventLevel.Debug => SerilogLogEventLevel.Debug,
        AvaloniaLogEventLevel.Information => SerilogLogEventLevel.Information,
        AvaloniaLogEventLevel.Warning => SerilogLogEventLevel.Warning,
        AvaloniaLogEventLevel.Error => SerilogLogEventLevel.Error,
        AvaloniaLogEventLevel.Fatal => SerilogLogEventLevel.Fatal,
        _ => SerilogLogEventLevel.Information
    };
}

/// <summary>
/// Extension methods for routing Avalonia framework diagnostics to Serilog.
/// </summary>
public static class AvaloniaLoggingExtensions
{
    /// <summary>
    /// Configures Avalonia's internal logger to forward events to Serilog.
    /// Defaults to <see cref="AvaloniaLogEventLevel.Warning"/>, or <see cref="AvaloniaLogEventLevel.Information"/>
    /// when <see cref="AppLogging.MinimumLevel"/> is set to Debug or lower.
    /// </summary>
    public static AppBuilder LogToSerilog(
        this AppBuilder builder,
        AvaloniaLogEventLevel? level = null,
        params string[] areas)
    {
        AvaloniaLogEventLevel effectiveLevel = level ??
            (AppLogging.MinimumLevel <= SerilogLogEventLevel.Debug
                ? AvaloniaLogEventLevel.Information
                : AvaloniaLogEventLevel.Warning);

        Logger.Sink = new AvaloniaSerilogSink(effectiveLevel, areas);
        return builder;
    }
}
