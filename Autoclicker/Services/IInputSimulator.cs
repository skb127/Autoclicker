using System.Collections.Generic;
using System.Threading.Tasks;
using Autoclicker.Core;

namespace Autoclicker.Services;

/// <summary>
/// Low-level platform input primitives. Higher-level behavior (double click, hold,
/// interval timing, and human-like trajectories) is orchestrated in shared Core classes.
/// </summary>
public interface IInputSimulator
{
    /// <summary>
    /// Requests user permission (if required by the platform) and opens the input session.
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    /// Moves the pointer to the specified absolute screen coordinates.
    /// </summary>
    Task MoveToAsync(int x, int y);

    /// <summary>
    /// Presses or releases the specified mouse button at the current pointer location.
    /// </summary>
    Task ButtonAsync(MouseButtonKind button, bool isDown);

    /// <summary>
    /// Attempts to obtain the current cursor position (or the last injected position on
    /// platforms like Wayland where global cursor queries are restricted).
    /// </summary>
    bool TryGetCursorPosition(out int x, out int y);

    /// <summary>
    /// Validates whether the target coordinates can be reached in the current input session
    /// (for example, checking that all points belong to the shared monitor on Wayland).
    /// Returns a list of human-readable issues, or an empty list if all targets are valid.
    /// </summary>
    IReadOnlyList<string> ValidateTargets(IEnumerable<(int X, int Y)> targets) => [];
}
