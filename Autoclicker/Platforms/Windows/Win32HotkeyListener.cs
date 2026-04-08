using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Autoclicker.Services;

namespace Autoclicker.Platforms.Windows;

public sealed partial class Win32HotkeyListener : IHotkeyListener
{
    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vKey);

    private const int VkEscape = 0x1B;

    private CancellationTokenSource? _cts;

    public event Action? OnStopRequested;

    public Task StartListening()
    {
        Console.WriteLine(" Starting to listen for the ESCAPE key...");
        _cts = new CancellationTokenSource();

        _ = Task.Run(() => ListenLoop(_cts.Token));

        return Task.CompletedTask;
    }

    public void StopListening()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        Console.WriteLine(" Stopped listening to the keyboard.");
    }

    private async Task ListenLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if ((GetAsyncKeyState(VkEscape) & 0x8000) != 0)
                {
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
                Console.WriteLine($"Error in Win32 listener loop: {ex.Message}");
                break;
            }
        }
    }
}