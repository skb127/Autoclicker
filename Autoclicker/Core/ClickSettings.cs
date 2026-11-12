namespace Autoclicker.Core;

/// <summary>
/// Immutable snapshot of all autoclicker parameters captured when the user starts the loop.
/// Keeps the background click thread decoupled from UI property mutations.
/// </summary>
public sealed record ClickSettings(
    int MinIntervalMs,
    int MaxIntervalMs,
    bool RandomizeInterval,
    int Repetitions,
    MouseButtonKind Button,
    ClickActionKind Action,
    int HoldMs,
    int JitterPx,
    bool HumanMovement,
    MovementSpeedKind MovementSpeed,
    IntervalDistributionKind Distribution = IntervalDistributionKind.Uniform)
{
    /// <summary>
    /// Effective minimum interval in milliseconds (>= 1).
    /// </summary>
    public int EffectiveMinIntervalMs => System.Math.Max(1, MinIntervalMs);

    /// <summary>
    /// Effective maximum interval in milliseconds. Equal to <see cref="EffectiveMinIntervalMs"/>
    /// when interval randomization is disabled.
    /// </summary>
    public int EffectiveMaxIntervalMs =>
        RandomizeInterval
            ? System.Math.Max(EffectiveMinIntervalMs, MaxIntervalMs)
            : EffectiveMinIntervalMs;
}

