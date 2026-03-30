using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Autoclicker.Services;

namespace Autoclicker.Platforms.Linux;

public sealed partial class X11HotkeyListener : IHotkeyListener
{
    // Import native C functions from X11
    [LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr XOpenDisplay(string? display);

    [LibraryImport("libX11.so.6")]
    private static partial int XQueryKeymap(IntPtr display, byte[] keys);

    [LibraryImport("libX11.so.6")]
    private static partial int XCloseDisplay(IntPtr display);

    private IntPtr _display;
    private CancellationTokenSource? _cts;
    public event Action? OnStopRequested;

    public void StartListening()
    {
        Console.WriteLine(" Starting to listen for the ESCAPE key...");
        // Avoid starting multiple listeners
        if (_cts is { IsCancellationRequested: false }) return;

        _display = XOpenDisplay(null);
        if (_display == IntPtr.Zero)
        {
            Console.WriteLine("Warning: Could not open X11 display for hotkey listener. Hotkey will not work.");
            return;
        }

        _cts = new CancellationTokenSource();
            
        // We launch the listener in the background
        Task.Run(() => ListenLoop(_cts.Token));
    }

    public void StopListening()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        if (_display != IntPtr.Zero)
        {
            XCloseDisplay(_display);
            _display = IntPtr.Zero;
        }
        Console.WriteLine(" Stopped listening to the keyboard.");
    }

    private async Task ListenLoop(CancellationToken token)
    {
        var keymap = new byte[32];
        // In X11, the Escape key is usually mapped to keycode 9 by default
        const int escKeyCode = 9; 

        while (!token.IsCancellationRequested)
        {
            // We get the state of the 256 logical keys
            XQueryKeymap(_display, keymap);
                
            // We check if the bit corresponding to Escape is on
            if ((keymap[escKeyCode / 8] & (1 << (escKeyCode % 8))) != 0)
            {
                OnStopRequested?.Invoke();
                await Task.Delay(500, token); // Pause to avoid multiple triggers
            }
            
            try
            {
                // Polling every 50ms to avoid saturating the processor
                await Task.Delay(50, token);
            }
            catch (TaskCanceledException)
            {
                // This is expected when stopping, so we can just break the loop.
                break;
            }
        }
    }
}