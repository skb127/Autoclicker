using System.Collections.Generic;
using System.Text.Json.Serialization;
using Autoclicker.ViewModels;

namespace Autoclicker.Core;

/// <summary>
/// Serializable model representing user settings and the saved coordinate pattern
/// persisted on disk in <c>settings.json</c>.
/// </summary>
public sealed record AppSettings(
    int Version = 1,
    int MinIntervalMs = 1000,
    int MaxIntervalMs = 1000,
    bool RandomizeInterval = false,
    int Repetitions = 0,
    MouseButtonKind Button = MouseButtonKind.Left,
    ClickActionKind Action = ClickActionKind.Click,
    int HoldMs = 200,
    int JitterPx = 0,
    bool HumanMovement = false,
    MovementSpeedKind MovementSpeed = MovementSpeedKind.Normal,
    List<Coordinate>? Pattern = null,
    bool WaylandHotkeyConfirmed = false);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal partial class AppSettingsJsonContext : JsonSerializerContext;
