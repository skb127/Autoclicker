using System.Collections.Generic;
using Autoclicker.Services;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace Autoclicker.Platforms.Common;

/// <summary>
/// Reads physical monitor rectangles from Avalonia's <c>Screens.All</c> API.
/// </summary>
public sealed class AvaloniaScreenBoundsProvider : IScreenBoundsProvider
{
    public IReadOnlyList<ScreenArea> GetScreens()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime
            {
                MainWindow.Screens.All: { Count: > 0 } screens
            })
        {
            return [];
        }

        var result = new List<ScreenArea>(screens.Count);
        for (int i = 0; i < screens.Count; i++)
        {
            var screen = screens[i];
            var bounds = screen.Bounds;
            string name = string.IsNullOrWhiteSpace(screen.DisplayName)
                ? $"Display {i + 1}"
                : screen.DisplayName;

            result.Add(new ScreenArea(name, bounds.X, bounds.Y, bounds.Width, bounds.Height));
        }

        return result;
    }
}
