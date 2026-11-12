using System;
using System.Diagnostics;
using System.Threading;

namespace Autoclicker.Core;

/// <summary>
/// Hybrid high-precision wait helper. Uses <see cref="Thread.Sleep(int)"/> for the bulk
/// of the delay to keep CPU usage near 0%, and switches to <see cref="Thread.SpinWait"/>
/// only during the final ~16 ms window to absorb OS scheduler granularity.
/// </summary>
public static class PreciseWait
{
    private const double SpinThresholdMs = 16.0;
    private const int MaxSleepSliceMs = 50;

    /// <summary>
    /// Converts a duration in milliseconds into <see cref="Stopwatch"/> timestamp ticks.
    /// </summary>
    public static long MsToTicks(double milliseconds)
    {
        if (milliseconds <= 0) return 0;
        return (long)Math.Round(milliseconds * Stopwatch.Frequency / 1000.0);
    }

    /// <summary>
    /// Blocks the calling thread until <see cref="Stopwatch.GetTimestamp"/> reaches
    /// <paramref name="deadlineTicks"/>, or throws if <paramref name="ct"/> is cancelled.
    /// </summary>
    public static void Until(long deadlineTicks, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            long remainingTicks = deadlineTicks - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
            {
                return;
            }

            double remainingMs = remainingTicks * 1000.0 / Stopwatch.Frequency;
            if (remainingMs > SpinThresholdMs)
            {
                // Sleep in short slices (<= 50 ms) so emergency cancellation reacts immediately
                int sleepMs = (int)Math.Min(remainingMs - SpinThresholdMs, MaxSleepSliceMs);
                if (sleepMs > 0)
                {
                    Thread.Sleep(sleepMs);
                }
            }
            else
            {
                Thread.SpinWait(20);
            }
        }
    }

    /// <summary>
    /// Blocks the calling thread for the specified duration in milliseconds using hybrid waiting.
    /// </summary>
    public static void ForMs(double milliseconds, CancellationToken ct)
    {
        if (milliseconds <= 0)
        {
            ct.ThrowIfCancellationRequested();
            return;
        }

        long deadline = Stopwatch.GetTimestamp() + MsToTicks(milliseconds);
        Until(deadline, ct);
    }
}

