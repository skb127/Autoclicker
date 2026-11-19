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
    private readonly ILogger? _logger;

    public AvaloniaSerilogSink(AvaloniaLogEventLevel minimumLevel, params string[] areas)
        : this(minimumLevel, logger: null, areas)
    {
    }

    internal AvaloniaSerilogSink(AvaloniaLogEventLevel minimumLevel, ILogger? logger, params string[] areas)
    {
        _minimumLevel = minimumLevel;
        _logger = logger;
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

        return (_logger ?? Serilog.Log.Logger).IsEnabled(ToSerilogLevel(level));
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

    private void Write(
        AvaloniaLogEventLevel level,
        string area,
        object? source,
        string messageTemplate,
        params object?[] propertyValues)
    {
        string sourceContext = string.IsNullOrWhiteSpace(area)
            ? "Avalonia"
            : $"Avalonia.{area}";

        ILogger logger = (_logger ?? Serilog.Log.Logger).ForContext("SourceContext", sourceContext);
        if (source is not null)
        {
            logger = logger.ForContext("AvaloniaSource", source.GetType().Name);
        }

        string sanitizedTemplate = StripNullChars(messageTemplate);
        object?[] sanitizedValues = SanitizePropertyValues(propertyValues);

        logger.Write(ToSerilogLevel(level), sanitizedTemplate, sanitizedValues);
    }

    private static string StripNullChars(string value) =>
        string.IsNullOrEmpty(value) || !value.Contains('\0')
            ? value
            : value.Replace("\0", string.Empty, StringComparison.Ordinal);

    private static object?[] SanitizePropertyValues(object?[]? propertyValues)
    {
        if (propertyValues is null)
        {
            return [];
        }

        object?[]? copy = null;
        for (int i = 0; i < propertyValues.Length; i++)
        {
            if (propertyValues[i] is string str && str.Contains('\0'))
            {
                copy ??= (object?[])propertyValues.Clone();
                copy[i] = StripNullChars(str);
            }
        }

        return copy ?? propertyValues;
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
