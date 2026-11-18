using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Autoclicker.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Autoclicker.Platforms.Windows;

public sealed partial class Win32HotkeyListener : IHotkeyListener
{
    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vKey);

    private const int VkEscape = 0x1B;

    private readonly ILogger<Win32HotkeyListener> _logger;
    private CancellationTokenSource? _cts;

    public event Action? OnStopRequested;

    public Win32HotkeyListener(ILogger<Win32HotkeyListener>? logger = null)
    {
        _logger = logger ?? NullLogger<Win32HotkeyListener>.Instance;
    }

    public Task StartListening()
    {
        _logger.LogInformation("Started listening for ESCAPE hotkey on Win32");
        _cts = new CancellationTokenSource();

        _ = Task.Run(() => ListenLoop(_cts.Token));

        return Task.CompletedTask;
    }

    public void StopListening()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        _logger.LogInformation("Stopped listening for ESCAPE hotkey on Win32");
    }

    private async Task ListenLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if ((GetAsyncKeyState(VkEscape) & 0x8000) != 0)
                {
                    _logger.LogInformation("Emergency stop hotkey (ESCAPE) triggered");
                    OnStopRequested?.Invoke();
                    await Task.Delay(500, token);
                }

                await Task.Delay(50, token);
            }
            catch (TaskCanceledException)
            {
                // Expected when StopListening is called
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Win32 hotkey listener loop");
                break;
            }
        }
    }
}