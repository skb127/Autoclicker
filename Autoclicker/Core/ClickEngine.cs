using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Autoclicker.Services;
using Autoclicker.ViewModels;

namespace Autoclicker.Core;

/// <summary>
/// Executes individual mouse actions (movement, jitter, click, double-click, hold, and drag)
/// and runs the start-to-start deadline loop across the configured coordinate pattern.
/// </summary>
public sealed class ClickEngine
{
    // Short pauses (in ms) after pressing and before releasing during a drag so the target
    // application and window manager reliably register the grab and drop events.
    private const int DragGrabSettleMs = 35;
    private const int DragDropSettleMs = 35;

    // Default number of interpolated steps and total duration when dragging with WindMouse disabled,
    // ensuring the OS drag distance/time threshold is always crossed.
    private const int LinearDragSteps = 14;
    private const double LinearDragDurationMs = 110.0;

    private readonly IInputSimulator _simulator;
    private readonly Random _rng;

    public ClickEngine(IInputSimulator simulator, Random? rng = null)
    {
        _simulator = simulator;
        _rng = rng ?? Random.Shared;
    }

    /// <summary>
    /// Runs the autoclicker pattern loop until <paramref name="ct"/> is cancelled or
    /// the configured number of repetitions is reached.
    /// <para>
    /// Intervals are scheduled <b>start-to-start</b>: if an action (such as a long hold,
    /// drag, or human-like movement) takes longer than the sampled interval, the next action
    /// starts immediately without extra waiting.
    /// </para>
    /// </summary>
    public async Task RunAsync(
        IReadOnlyList<Coordinate> pattern,
        ClickSettings settings,
        CancellationToken ct)
    {
        if (pattern.Count == 0) return;

        var intervalProvider = new IntervalProvider(
            settings.EffectiveMinIntervalMs,
            settings.EffectiveMaxIntervalMs,
            settings.Distribution,
            _rng);

        int completedRepeats = 0;

        while (!ct.IsCancellationRequested)
        {
            if (settings.Action == ClickActionKind.Drag)
            {
                ct.ThrowIfCancellationRequested();

                long actionStartTicks = Stopwatch.GetTimestamp();
                int intervalMs = intervalProvider.NextIntervalMs();
                long nextDeadlineTicks = actionStartTicks + PreciseWait.MsToTicks(intervalMs);

                await ExecuteDragSequenceAsync(pattern, settings, ct);

                PreciseWait.Until(nextDeadlineTicks, ct);
            }
            else
            {
                foreach (var point in pattern)
                {
                    ct.ThrowIfCancellationRequested();

                    long actionStartTicks = Stopwatch.GetTimestamp();
                    int intervalMs = intervalProvider.NextIntervalMs();
                    long nextDeadlineTicks = actionStartTicks + PreciseWait.MsToTicks(intervalMs);

                    await ExecutePointActionAsync(point, settings, ct);

                    // Wait until the start-to-start deadline (if the action already exceeded it, Until returns immediately)
                    PreciseWait.Until(nextDeadlineTicks, ct);
                }
            }

            completedRepeats++;
            if (settings.Repetitions > 0 && completedRepeats >= settings.Repetitions)
            {
                break;
            }
        }
    }

    private async Task ExecutePointActionAsync(
        Coordinate point,
        ClickSettings settings,
        CancellationToken ct)
    {
        // 1. Apply optional coordinate jitter (+/- JitterPx)
        var (targetX, targetY) = ApplyJitter(point, settings.JitterPx);

        // 2. Move cursor to target (human trajectory if enabled and starting position is known, otherwise direct jump)
        await MoveCursorAsync(targetX, targetY, settings, ct);

        // 3. Execute button action (Click, DoubleClick, or Hold)
        switch (settings.Action)
        {
            case ClickActionKind.Click:
                await PressAndReleaseAsync(settings.Button, holdMs: 0, ct);
                break;

            case ClickActionKind.DoubleClick:
                await PressAndReleaseAsync(settings.Button, holdMs: 0, ct);
                // Natural inter-click gap (40..75 ms), well below OS double-click threshold (~500 ms)
                int gapMs = _rng.Next(40, 76);
                PreciseWait.ForMs(gapMs, ct);
                await PressAndReleaseAsync(settings.Button, holdMs: 0, ct);
                break;

            case ClickActionKind.Hold:
                int holdDurationMs = Math.Max(1, settings.HoldMs);
                await PressAndReleaseAsync(settings.Button, holdDurationMs, ct);
                break;
        }
    }

    /// <summary>
    /// Executes a continuous drag operation across <paramref name="pattern"/> using the left mouse button:
    /// moves to the first coordinate, presses the left button down, drags through all subsequent
    /// coordinates in the pattern, and releases the button at the final coordinate.
    /// Guarantees inside a <c>finally</c> block that the left mouse button is always released
    /// even if cancellation occurs mid-drag.
    /// </summary>
    private async Task ExecuteDragSequenceAsync(
        IReadOnlyList<Coordinate> pattern,
        ClickSettings settings,
        CancellationToken ct)
    {
        if (pattern.Count < 2) return;

        // 1. Move to the starting coordinate (with the mouse button released)
        var (startX, startY) = ApplyJitter(pattern[0], settings.JitterPx);
        await MoveCursorAsync(startX, startY, settings, ct);

        // 2. Press and hold the Left mouse button (Drag is restricted to the left button)
        ct.ThrowIfCancellationRequested();
        await _simulator.ButtonAsync(MouseButtonKind.Left, isDown: true);

        try
        {
            // Brief settle pause so the target window registers the button press at the origin
            PreciseWait.ForMs(DragGrabSettleMs, ct);

            int currentX = startX;
            int currentY = startY;

            // 3. Drag through each subsequent coordinate in the sequence
            for (int i = 1; i < pattern.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (nextX, nextY) = ApplyJitter(pattern[i], settings.JitterPx);

                await DragSegmentAsync(currentX, currentY, nextX, nextY, settings, ct);

                currentX = nextX;
                currentY = nextY;
            }

            // Brief settle pause at the final coordinate before releasing the button
            PreciseWait.ForMs(DragDropSettleMs, ct);
        }
        finally
        {
            // Always release the left button so it never remains stuck after emergency stop
            try
            {
                await _simulator.ButtonAsync(MouseButtonKind.Left, isDown: false);
            }
            catch
            {
                // Best-effort release on shutdown
            }
        }
    }

    /// <summary>
    /// Moves the pointer from (<paramref name="startX"/>, <paramref name="startY"/>) to
    /// (<paramref name="destX"/>, <paramref name="destY"/>) while the mouse button is held down.
    /// Uses <see cref="WindMouse"/> when human movement is enabled, or linear multi-step
    /// interpolation when disabled so OS drag thresholds are always satisfied.
    /// </summary>
    private async Task DragSegmentAsync(
        int startX,
        int startY,
        int destX,
        int destY,
        ClickSettings settings,
        CancellationToken ct)
    {
        double dist = double.Hypot(destX - startX, destY - startY);
        if (dist < 1.0)
        {
            await _simulator.MoveToAsync(destX, destY);
            return;
        }

        if (settings.HumanMovement)
        {
            var trajectory = WindMouse.GenerateTrajectory(
                startX,
                startY,
                destX,
                destY,
                settings.MovementSpeed,
                _rng);

            foreach (var step in trajectory)
            {
                ct.ThrowIfCancellationRequested();
                await _simulator.MoveToAsync(step.X, step.Y);
                if (step.DelayMs > 0)
                {
                    PreciseWait.ForMs(step.DelayMs, ct);
                }
            }
        }
        else
        {
            int steps = Math.Clamp((int)Math.Round(dist / 12.0), 6, LinearDragSteps);
            double stepDelayMs = LinearDragDurationMs / steps;

            for (int step = 1; step <= steps; step++)
            {
                ct.ThrowIfCancellationRequested();
                double t = (double)step / steps;
                int ix = (int)Math.Round(startX + (destX - startX) * t);
                int iy = (int)Math.Round(startY + (destY - startY) * t);

                await _simulator.MoveToAsync(ix, iy);
                PreciseWait.ForMs(stepDelayMs, ct);
            }
        }
    }

    private (int X, int Y) ApplyJitter(Coordinate point, int jitterPx)
    {
        if (jitterPx <= 0)
        {
            return (point.X, point.Y);
        }

        int j = jitterPx;
        int x = Math.Max(0, point.X + _rng.Next(-j, j + 1));
        int y = Math.Max(0, point.Y + _rng.Next(-j, j + 1));
        return (x, y);
    }

    private async Task MoveCursorAsync(
        int targetX,
        int targetY,
        ClickSettings settings,
        CancellationToken ct)
    {
        if (settings.HumanMovement && _simulator.TryGetCursorPosition(out int startX, out int startY))
        {
            double dist = double.Hypot(targetX - startX, targetY - startY);
            if (dist >= 2.0)
            {
                var trajectory = WindMouse.GenerateTrajectory(
                    startX,
                    startY,
                    targetX,
                    targetY,
                    settings.MovementSpeed,
                    _rng);

                foreach (var step in trajectory)
                {
                    ct.ThrowIfCancellationRequested();
                    await _simulator.MoveToAsync(step.X, step.Y);
                    if (step.DelayMs > 0)
                    {
                        PreciseWait.ForMs(step.DelayMs, ct);
                    }
                }

                return;
            }
        }

        // Instant jump when human movement is disabled, distance is negligible,
        // or on the very first move in Wayland when no prior position exists.
        ct.ThrowIfCancellationRequested();
        await _simulator.MoveToAsync(targetX, targetY);
    }

    /// <summary>
    /// Presses the specified button, waits for <paramref name="holdMs"/> if greater than zero,
    /// and guarantees inside a <c>finally</c> block that the button is always released even if
    /// cancellation is triggered mid-hold.
    /// </summary>
    private async Task PressAndReleaseAsync(
        MouseButtonKind button,
        int holdMs,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await _simulator.ButtonAsync(button, isDown: true);
        try
        {
            if (holdMs > 0)
            {
                PreciseWait.ForMs(holdMs, ct);
            }
        }
        finally
        {
            // Always release the button so it never stays stuck after emergency stop
            try
            {
                await _simulator.ButtonAsync(button, isDown: false);
            }
            catch
            {
                // Best-effort release on shutdown
            }
        }
    }
}
