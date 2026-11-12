using System;
using System.Collections.Generic;

namespace Autoclicker.Core;

/// <summary>
/// Tuning parameters for the <see cref="WindMouse"/> human-like cursor trajectory generator.
/// </summary>
/// <param name="Gravity">
/// Pull strength toward the destination point (typical value: 9.0).
/// Acts like an invisible elastic band pulling the cursor toward the target.
/// </param>
/// <param name="Wind">
/// Magnitude of random lateral fluctuations (typical value: 3.0).
/// Simulates the natural micro-tremors and curved arcs of a human hand.
/// </param>
/// <param name="MaxStep">
/// Initial maximum pixel distance the cursor may jump in a single step (typical value: 15.0).
/// Caps peak speed so the cursor cannot teleport.
/// </param>
/// <param name="TargetArea">
/// Radius in pixels around the target where "braking" begins (typical value: 12.0).
/// Outside this radius, wind adds randomness; inside this radius, wind is damped so the
/// cursor settles smoothly onto the exact destination pixel.
/// </param>
public readonly record struct WindMouseOptions(
    double Gravity = 9.0,
    double Wind = 3.0,
    double MaxStep = 15.0,
    double TargetArea = 12.0)
{
    public static WindMouseOptions Default => new(9.0);

    public static WindMouseOptions ForSpeed(MovementSpeedKind speed) => speed switch
    {
        MovementSpeedKind.Slow => new WindMouseOptions(Gravity: 8.0, Wind: 3.5, MaxStep: 11.0, TargetArea: 14.0),
        MovementSpeedKind.Fast => new WindMouseOptions(Gravity: 11.0, Wind: 2.5, MaxStep: 22.0, TargetArea: 10.0),
        _ => Default
    };
}

/// <summary>
/// Represents a single intermediate point along a human-like mouse trajectory,
/// together with the pause (in milliseconds) to wait after moving to that point.
/// </summary>
public readonly record struct PathStep(int X, int Y, double DelayMs);

/// <summary>
/// Implements the classic "WindMouse" algorithm (originally popularized by BenLand100)
/// to generate realistic, human-like mouse trajectories between two screen coordinates.
/// <para>
/// <b>How WindMouse works in simple terms:</b>
/// Imagine the mouse cursor is a small puck sliding across a table toward a magnet (the target)
/// while a gentle breeze blows from the side:
/// <list type="number">
///   <item>
///     <description>
///     <b>Gravity (The Magnet):</b> At every step, a steady force pulls the cursor toward the
///     destination. Without anything else, the cursor would move in a straight robotic line.
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>Wind (Hand Wobble):</b> A random fluctuating force pushes the cursor slightly off
///     course, creating the gentle curves and imperfections of a real human hand.
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>Target Area (Fine Aiming / Braking Zone):</b> When a person gets close to a button
///     (within <see cref="WindMouseOptions.TargetArea"/> pixels), they slow down and stop
///     wobbling so they don't miss. Inside this circle, WindMouse dampens the wind and shrinks
///     the maximum step size so the cursor converges cleanly onto the target.
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>Speed Limit (<see cref="WindMouseOptions.MaxStep"/>):</b> If the combined forces
///     make the cursor move too fast, its velocity is clipped to a maximum speed with a bit
///     of random variation.
///     </description>
///   </item>
/// </list>
/// </para>
/// </summary>
public static class WindMouse
{
    private static readonly double Sqrt3 = Math.Sqrt(3.0);
    private static readonly double Sqrt5 = Math.Sqrt(5.0);

    // Safety cap so pathological parameters can never cause an infinite loop
    private const int MaxRawIterations = 2000;

    // Maximum number of emitted OS move events per trajectory (keeps Wayland D-Bus responsive)
    private const int MaxEmittedSteps = 90;

    /// <summary>
    /// Generates a sequence of <see cref="PathStep"/> points from (<paramref name="startX"/>, <paramref name="startY"/>)
    /// to (<paramref name="destX"/>, <paramref name="destY"/>) with per-step timing inspired by Fitts's Law.
    /// </summary>
    public static IReadOnlyList<PathStep> GenerateTrajectory(
        int startX,
        int startY,
        int destX,
        int destY,
        MovementSpeedKind speed,
        Random? rng = null)
    {
        rng ??= Random.Shared;
        var options = WindMouseOptions.ForSpeed(speed);
        var rawPoints = GeneratePoints(startX, startY, destX, destY, options, rng);

        if (rawPoints.Count == 0)
        {
            return [new PathStep(destX, destY, 0)];
        }

        // Subsample if the path has more points than MaxEmittedSteps (always keeping the final point)
        var points = DownsampleIfNeeded(rawPoints, MaxEmittedSteps);

        // Estimate total human travel time (ms) using a Fitts's-law logarithmic relationship:
        // TotalTime = BaseMs + ScaleMs * log2(Distance / TargetSize + 1)
        double totalDistance = double.Hypot(destX - startX, destY - startY);
        double totalDurationMs = ComputeTargetDurationMs(totalDistance, speed, rng);

        int count = points.Count;
        var steps = new List<PathStep>(count);

        if (count == 1)
        {
            steps.Add(new PathStep(points[0].X, points[0].Y, 0));
            return steps;
        }

        // Distribute the total duration across steps with a bell-shaped velocity profile
        // (slightly slower at the very beginning and end, faster in the middle of the stroke)
        double[] weights = new double[count];
        double weightSum = 0;

        for (int i = 0; i < count; i++)
        {
            double t = (double)i / (count - 1); // 0.0 -> 1.0 along the path
            // Edge factor is higher near t=0 and t=1 (more delay per step = slower cursor)
            double edgeSlowdown = 1.0 + 0.8 * Math.Pow(2.0 * t - 1.0, 2);
            weights[i] = edgeSlowdown;
            weightSum += edgeSlowdown;
        }

        for (int i = 0; i < count; i++)
        {
            double stepDelay = totalDurationMs * (weights[i] / weightSum);
            steps.Add(new PathStep(points[i].X, points[i].Y, stepDelay));
        }

        return steps;
    }

    /// <summary>
    /// Pure geometric WindMouse generator. Produces the sequence of unique integer pixel
    /// coordinates from start to destination.
    /// </summary>
    public static List<(int X, int Y)> GeneratePoints(
        double startX,
        double startY,
        double destX,
        double destY,
        WindMouseOptions options,
        Random rng)
    {
        var path = new List<(int X, int Y)>();

        double currentX = startX;
        double currentY = startY;

        // Current velocity components (how fast the cursor is moving along X and Y)
        double velocityX = 0;
        double velocityY = 0;

        // Current wind force components (random lateral drift)
        double windX = 0;
        double windY = 0;

        double maxStep = Math.Max(1.0, options.MaxStep);
        int lastEmittedX = (int)Math.Round(startX);
        int lastEmittedY = (int)Math.Round(startY);

        int iterations = 0;
        double distance;

        // Keep stepping until the cursor is within 1 pixel of the target
        while ((distance = double.Hypot(destX - currentX, destY - currentY)) >= 1.0 &&
               iterations++ < MaxRawIterations)
        {
            // Never let the wind force exceed the remaining distance to the target
            double windMagnitude = Math.Min(options.Wind, distance);

            if (distance >= options.TargetArea)
            {
                // Phase 1 — Cruising (outside the braking zone):
                // Gradually decay the old wind (divide by sqrt(3)) and add a fresh random push
                // in [-windMagnitude, +windMagnitude] scaled by sqrt(5).
                windX = windX / Sqrt3 + (2.0 * rng.NextDouble() - 1.0) * windMagnitude / Sqrt5;
                windY = windY / Sqrt3 + (2.0 * rng.NextDouble() - 1.0) * windMagnitude / Sqrt5;
            }
            else
            {
                // Phase 2 — Braking / Fine Aiming (inside TargetArea):
                // Stop injecting new random wind and dampen existing wind so the cursor stabilizes.
                windX /= Sqrt3;
                windY /= Sqrt3;

                // Shrink the maximum allowed step so the cursor decelerates smoothly
                if (maxStep < 3.0)
                {
                    maxStep = rng.NextDouble() * 3.0 + 3.0;
                }
                else
                {
                    maxStep /= Sqrt5;
                }
            }

            // Apply both forces to the current velocity:
            //   1. Wind force (hand wobble)
            //   2. Gravity force pulling directly along the unit vector (dest - current) / distance
            velocityX += windX + options.Gravity * (destX - currentX) / distance;
            velocityY += windY + options.Gravity * (destY - currentY) / distance;

            // Clamp total speed if it exceeds maxStep
            double speed = double.Hypot(velocityX, velocityY);
            if (speed > maxStep)
            {
                // Clip to a random speed between [maxStep / 2, maxStep] to avoid constant robotic speed
                double clippedSpeed = maxStep / 2.0 + rng.NextDouble() * (maxStep / 2.0);
                velocityX = (velocityX / speed) * clippedSpeed;
                velocityY = (velocityY / speed) * clippedSpeed;
            }

            // Advance cursor position
            currentX += velocityX;
            currentY += velocityY;

            int pixelX = (int)Math.Round(currentX);
            int pixelY = (int)Math.Round(currentY);

            // Emit only when the rounded pixel coordinate actually changes
            if (pixelX != lastEmittedX || pixelY != lastEmittedY)
            {
                path.Add((pixelX, pixelY));
                lastEmittedX = pixelX;
                lastEmittedY = pixelY;
            }
        }

        // Guarantee that the final coordinate in the path is the exact destination
        int finalX = (int)Math.Round(destX);
        int finalY = (int)Math.Round(destY);
        if (path.Count == 0 || path[^1].X != finalX || path[^1].Y != finalY)
        {
            path.Add((finalX, finalY));
        }

        return path;
    }

    private static double ComputeTargetDurationMs(double distancePx, MovementSpeedKind speed, Random rng)
    {
        if (distancePx < 2.0)
        {
            return 0.0;
        }

        // Fitts's law index of difficulty: log2(D / W + 1), assuming effective target width W = 16 px
        double indexOfDifficulty = Math.Log2(distancePx / 16.0 + 1.0);

        (double baseMs, double slopeMs) = speed switch
        {
            MovementSpeedKind.Slow => (110.0, 95.0), // ~200 ms (short) to ~680 ms (long)
            MovementSpeedKind.Fast => (45.0, 42.0), // ~85 ms (short) to ~300 ms (long)
            _ => (75.0, 65.0) // Normal: ~140 ms (short) to ~460 ms (long)
        };

        double rawMs = baseMs + slopeMs * indexOfDifficulty;
        // Add +/- 10% natural timing variance per stroke
        double jitterFactor = 0.9 + rng.NextDouble() * 0.2;
        return rawMs * jitterFactor;
    }

    private static List<(int X, int Y)> DownsampleIfNeeded(List<(int X, int Y)> source, int maxCount)
    {
        if (source.Count <= maxCount || maxCount < 2)
        {
            return source;
        }

        var result = new List<(int X, int Y)>(maxCount);
        double stride = (double)(source.Count - 1) / (maxCount - 1);

        for (int i = 0; i < maxCount - 1; i++)
        {
            int idx = (int)Math.Round(i * stride);
            result.Add(source[idx]);
        }

        // Always preserve the exact destination point
        result.Add(source[^1]);
        return result;
    }
}
