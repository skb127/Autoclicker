namespace Autoclicker.Core;

/// <summary>
/// Mouse button to simulate during each action.
/// </summary>
public enum MouseButtonKind
{
    Left,
    Right,
    Middle
}

/// <summary>
/// Type of mouse action to execute at the configured coordinates.
/// </summary>
public enum ClickActionKind
{
    Click,
    DoubleClick,
    Hold,
    Drag
}

/// <summary>
/// Speed preset for human-like cursor movement (WindMouse).
/// </summary>
public enum MovementSpeedKind
{
    Slow,
    Normal,
    Fast
}

/// <summary>
/// Probability distribution used when randomizing intervals.
/// Uniform is the default; TruncatedGaussian is available internally.
/// </summary>
public enum IntervalDistributionKind
{
    Uniform,
    TruncatedGaussian
}
