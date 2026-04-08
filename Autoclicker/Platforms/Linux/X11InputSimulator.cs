using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Autoclicker.Services;

namespace Autoclicker.Platforms.Linux;

public sealed partial class X11InputSimulator : IInputSimulator
{
    // Import native C functions from X11 and XTest
    [LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr XOpenDisplay(string? display);

    [LibraryImport("libX11.so.6")]
    private static partial int XFlush(IntPtr display);

    [LibraryImport("libXtst.so.6")]
    private static partial int XTestFakeMotionEvent(IntPtr display, int screenNumber, int x, int y, int delay);

    [LibraryImport("libXtst.so.6")]
    private static partial int XTestFakeButtonEvent(IntPtr display, uint button, [MarshalAs(UnmanagedType.Bool)] bool isPress, int delay);

    private IntPtr _display;

    public Task InitializeAsync()
    {
        // Open the connection to the graphics server.
        // NOTE: This implementation does not close the display handle. Since IInputSimulator
        // is registered as a singleton and lacks a Dispose method, the handle remains open
        // for the application's lifetime. The OS will reclaim it on process exit.
        _display = XOpenDisplay(null);
        if (_display == IntPtr.Zero)
        {
            throw new Exception("Could not open connection to the X11 server.");
        }
        
        return Task.CompletedTask;
    }

    public Task SimulateClick(int x, int y)
    {
        try
        {
            if (_display == IntPtr.Zero) return Task.CompletedTask;

            // 1. Move the cursor to the absolute coordinate
            _ = XTestFakeMotionEvent(_display, -1, x, y, 0);
            
            // 2. Simulate pressing the left button (In Linux, the left button is button 1)
            XTestFakeButtonEvent(_display, 1, true, 0);
            
            // 3. Simulate releasing the left button
            XTestFakeButtonEvent(_display, 1, false, 0);

            // 4. Force the immediate sending of instructions to the server
            _ = XFlush(_display);
            
            return Task.CompletedTask;
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
    }
}