using System.Threading.Tasks;
using Autoclicker.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Autoclicker.Platforms.Windows;

public sealed class Win32CoordinatePicker : ICoordinatePicker
{
    public Task<(int X, int Y)> PickCoordinateAsync()
    {
        var tcs = new TaskCompletionSource<(int, int)>();

        // Create an invisible full-screen window
        var overlayWindow = new Window
        {
            WindowState = WindowState.FullScreen,
            SystemDecorations = SystemDecorations.None, // No borders or close buttons
            Topmost = true, // Always on top of other apps
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            // Black background with 1/255 opacity to catch the click
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
            Cursor = new Cursor(StandardCursorType.Cross) // Crosshair cursor for aiming
        };

        // Listen for the click event
        overlayWindow.PointerPressed += (sender, e) =>
        {
            // Get the global monitor position in pixels
            var point = e.GetPosition(overlayWindow);
            PixelPoint screenPoint = overlayWindow.PointToScreen(point);
                
            // Return the result to the ViewModel
            tcs.TrySetResult((screenPoint.X, screenPoint.Y));
                
            // Close the window
            overlayWindow.Close();
        };

        overlayWindow.Show();

        return tcs.Task;
    }
}