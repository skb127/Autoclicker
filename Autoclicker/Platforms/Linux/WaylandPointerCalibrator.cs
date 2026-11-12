using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autoclicker.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Autoclicker.Platforms.Linux;

/// <summary>
/// Result of the automatic Wayland pointer coordinate calibration.
/// </summary>
/// <param name="Transform">Affine transformation mapping global coordinates to stream coordinates.</param>
/// <param name="SharedMonitorGlobalBounds">
/// Detected global bounds (in XWayland / Avalonia screen coordinates) of the monitor associated
/// with the portal stream, if identified.
/// </param>
/// <param name="SharedMonitorName">Display name of the detected monitor, if available.</param>
public sealed record WaylandCalibrationResult(
    AxisTransform Transform,
    PixelRect? SharedMonitorGlobalBounds,
    string? SharedMonitorName);

/// <summary>
/// Resolves the coordinate transformation between Avalonia/XWayland global screen coordinates
/// and Wayland <c>RemoteDesktop</c> portal stream coordinates.
/// <para>
/// Strategy:
/// <list type="number">
///   <item>
///     <description>
///     <b>Deterministic Direct Match (Priority 1):</b> When the portal reports the stream's
///     <c>size</c> and <c>mapping_id</c> (e.g., <c>HDMI-A-1</c> or <c>eDP-1</c> on COSMIC), or
///     when only a single monitor is connected, matches the corresponding Avalonia <see cref="Screen"/>
///     directly and computes <c>Scale = Screen.Bounds.Size / Stream.Size</c> and
///     <c>Offset = Screen.Bounds.Position</c> without opening any windows or moving the pointer.
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>Hardened Sensor Probe (Fallback):</b> On multi-monitor setups where the compositor
///     omits <c>mapping_id</c>, opens temporary sensor windows, drains initial XWayland motion
///     events so hand/startup cursor position is ignored, and probes which monitor receives
///     the injected stream motion.
///     </description>
///   </item>
/// </list>
/// </para>
/// </summary>
public static class WaylandPointerCalibrator
{
    private const int WindowMapSettleDelayMs = 110;
    private const int ProbeTimeoutMs = 280;
    private const double MinMovementFromBaselinePx = 8.0;

    public static async Task<WaylandCalibrationResult?> CalibrateAsync(
        WaylandInputSimulator simulator,
        PortalStreamInfo streamInfo)
    {
        // 1. Try deterministic direct matching first (instantaneous, zero cursor movement)
        var directMatch = await TryDeterministicScreenMatchAsync(streamInfo);
        if (directMatch is not null)
        {
            return directMatch;
        }

        // 2. Fallback to hardened sensor probe when mapping_id is not provided on multi-monitor setups
        return await ProbeWithSensorWindowsAsync(simulator, streamInfo);
    }

    private static async Task<WaylandCalibrationResult?> TryDeterministicScreenMatchAsync(
        PortalStreamInfo streamInfo)
    {
        if (streamInfo.Width <= 0 || streamInfo.Height <= 0)
        {
            return null;
        }

        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var screens = GetAvailableScreens();
            if (screens.Count == 0)
            {
                return null;
            }

            Screen? matchedScreen = null;

            // Match by connector / output name (e.g. "HDMI-A-1", "eDP-1", "DP-1")
            if (!string.IsNullOrWhiteSpace(streamInfo.MappingId))
            {
                matchedScreen = screens.FirstOrDefault(s =>
                    string.Equals(s.DisplayName, streamInfo.MappingId, StringComparison.OrdinalIgnoreCase));
            }

            // If there is only a single monitor connected, it unambiguously corresponds to the stream
            if (matchedScreen is null && screens.Count == 1)
            {
                matchedScreen = screens[0];
            }

            if (matchedScreen is null)
            {
                return null;
            }

            var bounds = matchedScreen.Bounds;
            var transform = AxisTransform.FromScreenAndStreamSize(
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height,
                streamInfo.Width,
                streamInfo.Height);

            if (transform is null)
            {
                return null;
            }

            Console.WriteLine(
                $"Wayland exact screen match ('{matchedScreen.DisplayName ?? "Single"}'): " +
                $"Bounds={bounds}, StreamSize={streamInfo.Width}x{streamInfo.Height} => {transform.Value}");

            return new WaylandCalibrationResult(
                transform.Value,
                bounds,
                matchedScreen.DisplayName);
        });
    }

    private static async Task<WaylandCalibrationResult?> ProbeWithSensorWindowsAsync(
        WaylandInputSimulator simulator,
        PortalStreamInfo streamInfo)
    {
        (double X, double Y) streamPoint1;
        (double X, double Y) streamPoint2;

        if (streamInfo is { Width: >= 200, Height: >= 200 })
        {
            streamPoint1 = (Math.Round(streamInfo.Width * 0.25), Math.Round(streamInfo.Height * 0.25));
            streamPoint2 = (Math.Round(streamInfo.Width * 0.75), Math.Round(streamInfo.Height * 0.75));
        }
        else
        {
            streamPoint1 = (300.0, 250.0);
            streamPoint2 = (900.0, 650.0);
        }

        var sensorWindows = new List<Window>();
        PixelPoint? ignoredBaselinePoint = null;
        TaskCompletionSource<(PixelPoint Point, Screen Screen)>? activeProbeTcs = null;

        try
        {
            bool windowsCreated = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var screens = GetAvailableScreens();
                if (screens.Count == 0)
                {
                    return false;
                }

                foreach (var screen in screens)
                {
                    double scale = screen.Scaling > 0 ? screen.Scaling : 1.0;
                    var win = new Window
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Position = screen.Bounds.Position,
                        Width = screen.Bounds.Width / scale,
                        Height = Math.Max(100, (screen.Bounds.Height / scale) - 1.0),
                        SystemDecorations = SystemDecorations.None,
                        Topmost = true,
                        ShowInTaskbar = false,
                        TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
                        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0))
                    };

                    var capturedScreen = screen;
                    win.PointerMoved += (_, e) =>
                    {
                        var localPos = e.GetPosition(win);
                        var globalPixel = win.PointToScreen(localPos);

                        var tcs = activeProbeTcs;
                        if (tcs is null || tcs.Task.IsCompleted)
                        {
                            // Record initial XWayland motion while settling so we can ignore it later
                            ignoredBaselinePoint = globalPixel;
                            return;
                        }

                        if (ignoredBaselinePoint is { } baseline)
                        {
                            double dx = globalPixel.X - baseline.X;
                            double dy = globalPixel.Y - baseline.Y;
                            if (double.Hypot(dx, dy) < MinMovementFromBaselinePx)
                            {
                                // Ignore spurious motion event at the pre-existing cursor position
                                return;
                            }
                        }

                        tcs.TrySetResult((globalPixel, capturedScreen));
                    };

                    sensorWindows.Add(win);
                    win.Show();
                }

                return true;
            });

            if (!windowsCreated)
            {
                return null;
            }

            // Wait for XWayland to map the windows and fire any initial enter/motion events
            await Task.Delay(WindowMapSettleDelayMs);

            // Probe point 1
            var sample1 = await ProbePointAsync(
                simulator,
                streamPoint1,
                tcs => activeProbeTcs = tcs);

            if (sample1 is null)
            {
                Console.WriteLine("Wayland calibration warning: did not receive XWayland motion event for probe 1.");
                return null;
            }

            // If the portal provided exact stream dimensions, identifying which Screen received
            // probe 1 is sufficient to compute the exact deterministic transform for that Screen.
            if (streamInfo is { Width: >= 200, Height: >= 200 })
            {
                var detectedScreen = sample1.Value.Screen;
                var bounds = detectedScreen.Bounds;
                var exactTransform = AxisTransform.FromScreenAndStreamSize(
                    bounds.X,
                    bounds.Y,
                    bounds.Width,
                    bounds.Height,
                    streamInfo.Width,
                    streamInfo.Height);

                if (exactTransform is not null)
                {
                    Console.WriteLine(
                        $"Wayland probe identified screen '{detectedScreen.DisplayName ?? "Unknown"}' " +
                        $"(Bounds={bounds}, StreamSize={streamInfo.Width}x{streamInfo.Height}) => {exactTransform.Value}");

                    return new WaylandCalibrationResult(
                        exactTransform.Value,
                        bounds,
                        detectedScreen.DisplayName);
                }
            }

            // Otherwise, update baseline to sample1 and probe point 2 to solve from two samples
            ignoredBaselinePoint = sample1.Value.Point;

            var sample2 = await ProbePointAsync(
                simulator,
                streamPoint2,
                tcs => activeProbeTcs = tcs);

            if (sample2 is null)
            {
                Console.WriteLine("Wayland calibration warning: did not receive XWayland motion event for probe 2.");
                return null;
            }

            var g1 = ((double)sample1.Value.Point.X, (double)sample1.Value.Point.Y);
            var g2 = ((double)sample2.Value.Point.X, (double)sample2.Value.Point.Y);

            var transform = AxisTransform.FromSamples(streamPoint1, g1, streamPoint2, g2);
            if (transform is null)
            {
                Console.WriteLine(
                    $"Wayland calibration rejected anisotropic/invalid samples: S1={streamPoint1}->G1={g1}, S2={streamPoint2}->G2={g2}");
                return null;
            }

            var screen2 = sample2.Value.Screen;
            Console.WriteLine(
                $"Wayland two-point calibration succeeded: S1={streamPoint1}->G1={g1}, S2={streamPoint2}->G2={g2} => {transform.Value}");

            return new WaylandCalibrationResult(
                transform.Value,
                screen2.Bounds,
                screen2.DisplayName);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Wayland calibration error: {ex.Message}");
            return null;
        }
        finally
        {
            activeProbeTcs = null;
            if (sensorWindows.Count > 0)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    foreach (var win in sensorWindows)
                    {
                        try
                        {
                            win.Close();
                        }
                        catch
                        {
                            // Ignore errors closing temporary calibration windows
                        }
                    }
                });
            }
        }
    }

    private static async Task<(PixelPoint Point, Screen Screen)?> ProbePointAsync(
        WaylandInputSimulator simulator,
        (double X, double Y) streamPoint,
        Action<TaskCompletionSource<(PixelPoint Point, Screen Screen)>?> setProbeTcs)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var tcs = new TaskCompletionSource<(PixelPoint Point, Screen Screen)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            setProbeTcs(tcs);

            await simulator.MoveToRawStreamAsync(streamPoint.X, streamPoint.Y);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(ProbeTimeoutMs));
            if (completed == tcs.Task && tcs.Task.IsCompletedSuccessfully)
            {
                return await tcs.Task;
            }

            // Nudge by 12 pixels on retry in case the pointer was already resting near streamPoint
            streamPoint = (streamPoint.X + 12.0, streamPoint.Y + 12.0);
        }

        return null;
    }

    private static IReadOnlyList<Screen> GetAvailableScreens()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
            {
                MainWindow.Screens.All: { Count: > 0 } screens
            })
        {
            return screens;
        }

        return [];
    }
}
