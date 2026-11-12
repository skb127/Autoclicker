using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Autoclicker.Core;
using Autoclicker.Services;

namespace Autoclicker.Platforms.Linux;

public sealed partial class X11InputSimulator : IInputSimulator
{
    // Import native C functions from X11 and XTest
    [LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr XOpenDisplay(string? display);

    [LibraryImport("libX11.so.6")]
    private static partial int XFlush(IntPtr display);

    [LibraryImport("libX11.so.6")]
    private static partial IntPtr XDefaultRootWindow(IntPtr display);

    [LibraryImport("libX11.so.6")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool XQueryPointer(
        IntPtr display,
        IntPtr window,
        out IntPtr rootReturn,
        out IntPtr childReturn,
        out int rootXReturn,
        out int rootYReturn,
        out int winXReturn,
        out int winYReturn,
        out uint maskReturn);

    [LibraryImport("libXtst.so.6")]
    private static partial int XTestFakeMotionEvent(IntPtr display, int screenNumber, int x, int y, int delay);

    [LibraryImport("libXtst.so.6")]
    private static partial int XTestFakeButtonEvent(
        IntPtr display,
        uint button,
        [MarshalAs(UnmanagedType.Bool)] bool isPress,
        int delay);

    private IntPtr _display;
    private int _lastX;
    private int _lastY;
    private bool _hasLastPosition;

    public Task InitializeAsync()
    {
        // Open the connection to the graphics server if not already open.
        // NOTE: This implementation does not close the display handle. Since IInputSimulator
        // is registered as a singleton and lacks a Dispose method, the handle remains open
        // for the application's lifetime. The OS will reclaim it on process exit.
        if (_display == IntPtr.Zero)
        {
            _display = XOpenDisplay(null);
            if (_display == IntPtr.Zero)
            {
                throw new Exception("Could not open connection to the X11 server.");
            }
        }

        return Task.CompletedTask;
    }

    public Task MoveToAsync(int x, int y)
    {
        try
        {
            if (_display == IntPtr.Zero) return Task.CompletedTask;

            _ = XTestFakeMotionEvent(_display, -1, x, y, 0);
            _ = XFlush(_display);

            _lastX = x;
            _lastY = y;
            _hasLastPosition = true;
            return Task.CompletedTask;
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
    }

    public Task ButtonAsync(MouseButtonKind button, bool isDown)
    {
        try
        {
            if (_display == IntPtr.Zero) return Task.CompletedTask;

            // In X11, button mapping is: 1 = Left, 2 = Middle, 3 = Right
            uint xButton = button switch
            {
                MouseButtonKind.Left => 1u,
                MouseButtonKind.Middle => 2u,
                MouseButtonKind.Right => 3u,
                _ => 1u
            };

            _ = XTestFakeButtonEvent(_display, xButton, isDown, 0);
            _ = XFlush(_display);
            return Task.CompletedTask;
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
    }

    public bool TryGetCursorPosition(out int x, out int y)
    {
        if (_display != IntPtr.Zero)
        {
            try
            {
                IntPtr root = XDefaultRootWindow(_display);
                if (XQueryPointer(
                        _display,
                        root,
                        out _,
                        out _,
                        out int rootX,
                        out int rootY,
                        out _,
                        out _,
                        out _))
                {
                    x = rootX;
                    y = rootY;
                    return true;
                }
            }
            catch
            {
                // Fall back to last recorded position below
            }
        }

        x = _lastX;
        y = _lastY;
        return _hasLastPosition;
    }
}
