using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autoclicker.Platforms.Linux;
using Autoclicker.Services;
using Autoclicker.ViewModels;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace Autoclicker.Views.Controls;

/// <summary>
/// Manages one or more full-screen, borderless, topmost translucent countdown overlay windows.
/// On Windows and Linux (X11), the countdown is displayed across all connected monitors simultaneously.
/// On Linux (Wayland), where input injection targets a single shared monitor, the countdown is displayed
/// on the monitor containing the click pattern coordinates.
/// </summary>
public sealed class FullScreenCountdownOverlay : IDisposable
{
    private readonly List<Window> _windows;
    private readonly List<TextBlock> _secondsBlocks;
    private readonly ILogger? _logger;
    private IDisposable? _keyboardFocusLease;
    private bool _isClosed;

    private FullScreenCountdownOverlay(
        List<Window> windows,
        List<TextBlock> secondsBlocks,
        ILogger? logger)
    {
        _windows = windows;
        _secondsBlocks = secondsBlocks;
        _logger = logger;
    }

    /// <summary>
    /// Selects which monitor rectangles should display the full-screen countdown overlay.
    /// <list type="bullet">
    /// <item>When <paramref name="singleMonitorTargetOnly"/> is <c>false</c> (Windows / X11), returns all screens.</item>
    /// <item>When <paramref name="singleMonitorTargetOnly"/> is <c>true</c> (Wayland), returns the monitor(s)
    /// containing at least one coordinate from <paramref name="pattern"/>, falling back to all screens if none match.</item>
    /// </list>
    /// </summary>
    internal static IReadOnlyList<ScreenArea> SelectTargetScreenAreas(
        IReadOnlyList<ScreenArea> allScreens,
        IReadOnlyList<Coordinate> pattern,
        bool singleMonitorTargetOnly)
    {
        if (allScreens.Count <= 1 || !singleMonitorTargetOnly || pattern.Count == 0)
        {
            return allScreens;
        }

        var matching = allScreens
            .Where(screen => pattern.Any(pt => screen.Contains(pt.X, pt.Y)))
            .ToList();

        return matching.Count > 0 ? matching : allScreens;
    }

    /// <summary>
    /// Creates the full-screen countdown overlay window(s) for the target screen(s).
    /// </summary>
    public static FullScreenCountdownOverlay Create(
        Window? mainWindow,
        IReadOnlyList<Coordinate> pattern,
        bool singleMonitorTargetOnly,
        Action onCancelRequested,
        ILogger? logger = null)
    {
        var allScreens = GetAvailableScreens(mainWindow);
        var targetScreens = SelectTargetScreens(allScreens, pattern, singleMonitorTargetOnly);

        var windows = new List<Window>();
        var secondsBlocks = new List<TextBlock>();

        if (targetScreens.Count > 0)
        {
            foreach (var screen in targetScreens)
            {
                var (window, secondsText) = CreateOverlayWindowForScreen(screen, onCancelRequested, logger);
                windows.Add(window);
                secondsBlocks.Add(secondsText);
            }
        }
        else
        {
            var (fallbackWindow, secondsText) = CreateOverlayWindowForScreen(null, onCancelRequested, logger);
            windows.Add(fallbackWindow);
            secondsBlocks.Add(secondsText);
        }

        logger?.LogInformation(
            "Prepared full-screen countdown overlay on {TargetScreenCount} of {TotalScreenCount} monitor(s) (SingleMonitorTargetOnly={SingleMonitorTargetOnly})",
            windows.Count,
            allScreens.Count,
            singleMonitorTargetOnly);

        return new FullScreenCountdownOverlay(windows, secondsBlocks, logger);
    }

    public void Show(int initialSeconds)
    {
        if (_isClosed) return;

        UpdateSeconds(initialSeconds);

        foreach (var window in _windows)
        {
            window.Show();
        }

        if (_windows.Count > 0)
        {
            _windows[0].Activate();
            _windows[0].Focus();
            _keyboardFocusLease = X11OverlayWindowHelper.AcquireKeyboardFocus(_windows[0], _logger);
        }
    }

    public void UpdateSeconds(int seconds)
    {
        if (_isClosed) return;

        string text = seconds.ToString(CultureInfo.InvariantCulture);
        foreach (var block in _secondsBlocks)
        {
            block.Text = text;
        }
    }

    public void Close()
    {
        if (_isClosed) return;
        _isClosed = true;

        _keyboardFocusLease?.Dispose();
        _keyboardFocusLease = null;

        foreach (var window in _windows)
        {
            try
            {
                window.Close();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to close full-screen countdown overlay window");
            }
        }
    }

    public void Dispose()
    {
        Close();
    }

    private static IReadOnlyList<Screen> SelectTargetScreens(
        IReadOnlyList<Screen> allScreens,
        IReadOnlyList<Coordinate> pattern,
        bool singleMonitorTargetOnly)
    {
        if (allScreens.Count <= 1 || !singleMonitorTargetOnly || pattern.Count == 0)
        {
            return allScreens;
        }

        var screenAreas = allScreens
            .Select((s, index) => new ScreenArea(
                s.DisplayName ?? $"Display-{index + 1}",
                s.Bounds.X,
                s.Bounds.Y,
                s.Bounds.Width,
                s.Bounds.Height))
            .ToList();

        var selectedAreas = SelectTargetScreenAreas(screenAreas, pattern, singleMonitorTargetOnly);
        if (selectedAreas.Count == allScreens.Count)
        {
            return allScreens;
        }

        var matchingScreens = new List<Screen>(selectedAreas.Count);
        for (int i = 0; i < allScreens.Count; i++)
        {
            var area = screenAreas[i];
            if (selectedAreas.Contains(area))
            {
                matchingScreens.Add(allScreens[i]);
            }
        }

        return matchingScreens.Count > 0 ? matchingScreens : allScreens;
    }

    private static (Window Window, TextBlock SecondsText) CreateOverlayWindowForScreen(
        Screen? screen,
        Action onCancelRequested,
        ILogger? logger = null)
    {
        var headerText = new TextBlock
        {
            Text = "Autoclick starts in",
            FontSize = 24,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            Opacity = 0.9,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12)
        };

        var secondsText = new TextBlock
        {
            Text = "3",
            FontSize = 200,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetLiveSetting(secondsText, AutomationLiveSetting.Assertive);

        var viewbox = new Viewbox
        {
            MaxHeight = 260,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4),
            Child = secondsText
        };

        var subtitleText = new TextBlock
        {
            Text = "Clicks will begin as soon as the countdown reaches 0",
            FontSize = 14,
            Foreground = Brushes.White,
            Opacity = 0.75,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 28)
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 160,
            Padding = new Thickness(22, 10),
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsCancel = true
        };
        cancelButton.Click += (_, _) => onCancelRequested();

        var hintText = new TextBlock
        {
            Text = "Press Esc or your emergency shortcut to cancel",
            FontSize = 13,
            Foreground = Brushes.White,
            Opacity = 0.65,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var bottomStack = new StackPanel
        {
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                cancelButton,
                hintText
            }
        };

        var centerStack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                headerText,
                viewbox,
                subtitleText,
                bottomStack
            }
        };

        // ~75% opacity dark translucent veil (#BF0E0E12) so the target desktop remains visible underneath
        var rootBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(191, 14, 14, 18)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Padding = new Thickness(32),
            Child = centerStack
        };

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
            Content = rootBorder
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

            X11OverlayWindowHelper.ConfigureOverrideRedirect(
                overlayWindow,
                screen.Bounds.X,
                screen.Bounds.Y,
                screen.Bounds.Width,
                OperatingSystem.IsLinux() ? Math.Max(100, screen.Bounds.Height - 1) : screen.Bounds.Height,
                logger);
        }
        else
        {
            overlayWindow.WindowState = WindowState.FullScreen;
        }

        overlayWindow.Opened += (_, _) =>
        {
            Dispatcher.UIThread.Post(() => cancelButton.Focus(), DispatcherPriority.Input);
        };

        overlayWindow.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                onCancelRequested();
            }
        };

        return (overlayWindow, secondsText);
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
