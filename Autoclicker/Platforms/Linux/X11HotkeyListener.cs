using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Autoclicker.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Autoclicker.Platforms.Linux;

public sealed partial class X11HotkeyListener : IHotkeyListener
{
    [LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr XOpenDisplay(string? display);

    [LibraryImport("libX11.so.6")]
    private static partial int XQueryKeymap(IntPtr display, byte[] keys);

    [LibraryImport("libX11.so.6")]
    private static partial int XCloseDisplay(IntPtr display);

    private readonly ILogger<X11HotkeyListener> _logger;
    private IntPtr _display;
    private CancellationTokenSource? _cts;

    public event Action? OnStopRequested;

    public X11HotkeyListener(ILogger<X11HotkeyListener>? logger = null)
    {
        _logger = logger ?? NullLogger<X11HotkeyListener>.Instance;
    }

    public Task StartListening()
    {
        _logger.LogInformation("Started listening for ESCAPE hotkey on X11");
        if (_cts is { IsCancellationRequested: false }) return Task.CompletedTask;

        _display = XOpenDisplay(null);
        if (_display == IntPtr.Zero)
        {
            _logger.LogWarning("Could not open X11 display for hotkey listener; emergency ESCAPE hotkey will not work");
            return Task.CompletedTask;
        }

        _cts = new CancellationTokenSource();

        _ = Task.Run(() => ListenLoop(_cts.Token));
        return Task.CompletedTask;
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

        _logger.LogInformation("Stopped listening for ESCAPE hotkey on X11");
    }

    private async Task ListenLoop(CancellationToken token)
    {
        var keymap = new byte[32];
        const int escKeyCode = 9;

        while (!token.IsCancellationRequested)
        {
            try
            {
                XQueryKeymap(_display, keymap);

                if ((keymap[escKeyCode / 8] & (1 << (escKeyCode % 8))) != 0)
                {
                    _logger.LogInformation("Emergency stop hotkey (ESCAPE) triggered");
                    OnStopRequested?.Invoke();
                    await Task.Delay(500, token); // Pause to avoid multiple triggers
                }

                await Task.Delay(50, token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in X11 hotkey listener loop");
                break;
            }
        }
    }
}