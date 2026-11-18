using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autoclicker.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Autoclicker.Platforms.Common;

public sealed class CrossPlatformCoordinatePicker : ICoordinatePicker
{
    // Brief delay after hiding MainWindow so the OS/compositor repaints the desktop
    // underneath before the translucent capture overlays appear.
    private const int HideSettleDelayMs = 120;

    private readonly ILogger<CrossPlatformCoordinatePicker> _logger;

    public CrossPlatformCoordinatePicker(ILogger<CrossPlatformCoordinatePicker>? logger = null)
    {
        _logger = logger ?? NullLogger<CrossPlatformCoordinatePicker>.Instance;
    }

    public async Task<(int X, int Y)?> PickCoordinateAsync()
    {
        var mainWindow = GetMainWindow();
        // Enumerate screens before hiding MainWindow so screen metadata remains accessible.
        var screens = GetAvailableScreens(mainWindow);

        bool wasMainWindowVisible = mainWindow is { IsVisible: true };
        WindowState savedWindowState = mainWindow?.WindowState ?? WindowState.Normal;
        PixelPoint? savedPosition = mainWindow is not null && savedWindowState == WindowState.Normal
            ? mainWindow.Position
            : null;

        var tcs = new TaskCompletionSource<(int X, int Y)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlayWindows = new List<Window>();
        bool isClosingAll = false;

        void CloseAllOverlays()
        {
            if (isClosingAll) return;
            isClosingAll = true;

            foreach (var win in overlayWindows)
            {
                try
                {
                    win.Close();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to close coordinate picker overlay window");
                }
            }
        }

        try
        {
            _logger.LogInformation("Starting coordinate capture across {ScreenCount} screen(s)", screens.Count);

            if (mainWindow is not null && wasMainWindowVisible)
            {
                mainWindow.Hide();
                await Task.Delay(HideSettleDelayMs);
            }

            if (screens.Count > 0)
            {
                foreach (var screen in screens)
                {
                    var window = CreateOverlayForScreen(screen, tcs, _logger);
                    overlayWindows.Add(window);
                }
            }
            else
            {
                // Fallback if screen enumeration is unavailable
                var fallbackWindow = CreateOverlayForScreen(null, tcs, _logger);
                overlayWindows.Add(fallbackWindow);
            }

            foreach (var window in overlayWindows)
            {
                window.Show();
            }

            // Activate and focus the first overlay window so Escape is captured immediately
            if (overlayWindows.Count > 0)
            {
                overlayWindows[0].Activate();
                overlayWindows[0].Focus();
            }

            return await tcs.Task;
        }
        finally
        {
            // Restore and activate MainWindow before closing the overlays so the OS
            // preserves foreground-activation permission for this process, then re-activate
            // once the topmost overlays are gone.
            if (mainWindow is not null && wasMainWindowVisible)
            {
                mainWindow.Show();

                if (savedWindowState == WindowState.Maximized &&
                    mainWindow.WindowState != WindowState.Maximized)
                {
                    mainWindow.WindowState = WindowState.Maximized;
                }
                else if (savedWindowState == WindowState.Normal &&
                         savedPosition is { } pos &&
                         mainWindow.Position != pos)
                {
                    mainWindow.Position = pos;
                }

                mainWindow.Activate();
            }

            CloseAllOverlays();

            if (mainWindow is not null && wasMainWindowVisible)
            {
                mainWindow.Activate();
            }
        }
    }

    private static Window CreateOverlayForScreen(
        Screen? screen,
        TaskCompletionSource<(int X, int Y)?> tcs,
        ILogger<CrossPlatformCoordinatePicker> logger)
    {
        var liveCoordText = new TextBlock
        {
            Text = "X: —, Y: —",
            Foreground = Brushes.White,
            FontWeight = FontWeight.SemiBold,
            FontSize = 13
        };

        var liveBadge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(220, 24, 24, 28)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(220, 255, 165, 0)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 4),
            IsVisible = false,
            IsHitTestVisible = false,
            Child = liveCoordText
        };

        var banner = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(215, 24, 24, 28)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(180, 255, 165, 0)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 8),
            Margin = new Thickness(0, 24, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "⊕ Click anywhere to capture coordinate  ·  Press ESC to cancel",
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeight.Medium
            }
        };

        var canvas = new Canvas
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false
        };
        canvas.Children.Add(liveBadge);

        var rootGrid = new Grid
        {
            // Light translucent veil (#33000000) so the desktop remains visible underneath
            Background = new SolidColorBrush(Color.FromArgb(51, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        rootGrid.Children.Add(banner);
        rootGrid.Children.Add(canvas);

        var overlayWindow = new Window
        {
            SystemDecorations = SystemDecorations.None,
            Topmost = true,
            CanResize = false,
            ShowInTaskbar = false,
            TransparencyLevelHint =
            [
                WindowTransparencyLevel.Transparent,
                WindowTransparencyLevel.Blur,
                WindowTransparencyLevel.None
            ],
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Cross),
            Content = rootGrid
        };

        if (screen is not null)
        {
            double scale = screen.Scaling > 0 ? screen.Scaling : 1.0;
            double logicalWidth = screen.Bounds.Width / scale;
            // On Linux/XWayland, subtracting 1 px in height prevents compositors (like COSMIC)
            // from treating the window as an opaque unredirected fullscreen surface.
            double logicalHeight = OperatingSystem.IsLinux()
                ? Math.Max(100, (screen.Bounds.Height / scale) - 1.0)
                : screen.Bounds.Height / scale;

            overlayWindow.WindowStartupLocation = WindowStartupLocation.Manual;
            overlayWindow.Position = screen.Bounds.Position;
            overlayWindow.Width = logicalWidth;
            overlayWindow.Height = logicalHeight;
        }
        else
        {
            overlayWindow.WindowState = WindowState.FullScreen;
        }

        overlayWindow.PointerMoved += (_, e) =>
        {
            var localPos = e.GetPosition(overlayWindow);
            var screenPoint = overlayWindow.PointToScreen(localPos);

            liveCoordText.Text = $"X: {screenPoint.X}, Y: {screenPoint.Y}";
            liveBadge.IsVisible = true;

            // Offset the floating coordinate badge slightly down and right from the crosshair
            Canvas.SetLeft(liveBadge, localPos.X + 16);
            Canvas.SetTop(liveBadge, localPos.Y + 16);
        };

        overlayWindow.PointerExited += (_, _) =>
        {
            liveBadge.IsVisible = false;
        };

        overlayWindow.PointerPressed += (_, e) =>
        {
            if (tcs.Task.IsCompleted) return;

            var props = e.GetCurrentPoint(overlayWindow).Properties;
            if (props.IsRightButtonPressed)
            {
                // Right-click also cancels coordinate capture
                logger.LogInformation("Coordinate capture canceled by user (right-click)");
                tcs.TrySetResult(null);
                return;
            }

            var localPos = e.GetPosition(overlayWindow);
            var screenPoint = overlayWindow.PointToScreen(localPos);

            logger.LogInformation(
                "Coordinate picked at ({X}, {Y}) on screen {ScreenName} (Bounds={Bounds}, Scaling={Scaling}, Transparency={ActualTransparency})",
                screenPoint.X,
                screenPoint.Y,
                screen?.DisplayName ?? "Fallback",
                screen?.Bounds,
                screen?.Scaling,
                overlayWindow.ActualTransparencyLevel);

            tcs.TrySetResult((screenPoint.X, screenPoint.Y));
        };

        overlayWindow.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                logger.LogInformation("Coordinate capture canceled by user (Escape)");
                tcs.TrySetResult(null);
            }
        };

        overlayWindow.Closed += (_, _) =>
        {
            if (!tcs.Task.IsCompleted)
            {
                tcs.TrySetResult(null);
            }
        };

        return overlayWindow;
    }

    private static Window? GetMainWindow()
    {
        return Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;
    }

    private static IReadOnlyList<Screen> GetAvailableScreens(Window? mainWindow)
    {
        if (mainWindow?.Screens.All is { Count: > 0 } screens)
        {
            return screens;
        }

        return [];
    }
}
