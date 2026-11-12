using System;

namespace Autoclicker.Core;

/// <summary>
/// Represents a 2D per-axis affine transformation between global desktop coordinates
/// (as reported by Avalonia / XWayland) and Wayland portal stream coordinates:
/// <c>global = Offset + Scale * stream</c>, or equivalently
/// <c>stream = (global - Offset) / Scale</c>.
/// </summary>
public readonly record struct AxisTransform(
    double ScaleX,
    double OffsetX,
    double ScaleY,
    double OffsetY)
{
    private const double MinValidScale = 0.1;
    private const double MaxValidScale = 10.0;

    // Display scaling is isotropic (equal along X and Y); allow up to 5% rounding tolerance
    private const double MaxAspectScaleMismatch = 0.05;

    /// <summary>
    /// Identity transform where global and stream coordinates are 1:1 with zero offset.
    /// </summary>
    public static AxisTransform Identity => new(1.0, 0.0, 1.0, 0.0);

    /// <summary>
    /// Converts a global coordinate (XWayland / Avalonia screen space) into the coordinate
    /// space expected by <c>NotifyPointerMotionAbsoluteAsync</c> for the active portal stream.
    /// </summary>
    public (double StreamX, double StreamY) ToStream(double globalX, double globalY)
    {
        double sx = (globalX - OffsetX) / ScaleX;
        double sy = (globalY - OffsetY) / ScaleY;
        return (sx, sy);
    }

    /// <summary>
    /// Converts a portal stream coordinate into global desktop coordinates.
    /// </summary>
    public (double GlobalX, double GlobalY) ToGlobal(double streamX, double streamY)
    {
        double gx = OffsetX + ScaleX * streamX;
        double gy = OffsetY + ScaleY * streamY;
        return (gx, gy);
    }

    /// <summary>
    /// Computes the exact deterministic <see cref="AxisTransform"/> from the known global
    /// bounds of a monitor and the portal stream's dimensions.
    /// </summary>
    public static AxisTransform? FromScreenAndStreamSize(
        int screenX,
        int screenY,
        int screenWidth,
        int screenHeight,
        int streamWidth,
        int streamHeight)
    {
        if (screenWidth <= 0 || screenHeight <= 0 || streamWidth <= 0 || streamHeight <= 0)
        {
            return null;
        }

        double scaleX = (double)screenWidth / streamWidth;
        double scaleY = (double)screenHeight / streamHeight;

        if (!IsValidScale(scaleX) || !IsValidScale(scaleY))
        {
            return null;
        }

        return new AxisTransform(scaleX, screenX, scaleY, screenY);
    }

    /// <summary>
    /// Computes the <see cref="AxisTransform"/> from two calibration samples where
    /// injecting stream coordinates <paramref name="stream1"/> and <paramref name="stream2"/>
    /// placed the cursor at global coordinates <paramref name="global1"/> and <paramref name="global2"/>.
    /// Returns <c>null</c> if the samples are degenerate, out of range, or anisotropic.
    /// </summary>
    public static AxisTransform? FromSamples(
        (double X, double Y) stream1,
        (double X, double Y) global1,
        (double X, double Y) stream2,
        (double X, double Y) global2)
    {
        double dStreamX = stream2.X - stream1.X;
        double dStreamY = stream2.Y - stream1.Y;

        if (Math.Abs(dStreamX) < 1.0 || Math.Abs(dStreamY) < 1.0)
        {
            return null;
        }

        double scaleX = (global2.X - global1.X) / dStreamX;
        double scaleY = (global2.Y - global1.Y) / dStreamY;

        if (!IsValidScale(scaleX) || !IsValidScale(scaleY))
        {
            return null;
        }

        // Reject contaminated samples where X and Y scales diverge significantly
        double relativeDiff = Math.Abs(scaleX - scaleY) / Math.Max(scaleX, scaleY);
        if (relativeDiff > MaxAspectScaleMismatch)
        {
            return null;
        }

        double offsetX = global1.X - scaleX * stream1.X;
        double offsetY = global1.Y - scaleY * stream1.Y;

        if (!double.IsFinite(offsetX) || !double.IsFinite(offsetY))
        {
            return null;
        }

        return new AxisTransform(scaleX, offsetX, scaleY, offsetY);
    }

    private static bool IsValidScale(double scale) =>
        double.IsFinite(scale) && scale >= MinValidScale && scale <= MaxValidScale;

    public override string ToString() =>
        $"Scale=({ScaleX:F4}, {ScaleY:F4}), Offset=({OffsetX:F1}, {OffsetY:F1})";
}
