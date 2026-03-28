using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Autoclicker.Services;

namespace Autoclicker.Platforms.Windows;

public class Win32HotkeyListener : IHotkeyListener
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    // The hexadecimal code for the ESCAPE key
    private const int VkEscape = 0x1B; 
        
    private CancellationTokenSource? _cts;

    public event Action? OnStopRequested;

    public void StartListening()
    {
        Console.WriteLine(" Starting to listen for the ESCAPE key...");
        _cts = new CancellationTokenSource();
            
        // We start a secondary thread that will watch the key
        Task.Run(() => ListenLoop(_cts.Token));
    }

    public void StopListening()
    {
        _cts?.Cancel();
        Console.WriteLine(" Stopped listening to the keyboard.");
    }

    private async Task ListenLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            // If the most significant bit (0x8000) is on, the key is being pressed
            if ((GetAsyncKeyState(VkEscape) & 0x8000)!= 0)
            {
                OnStopRequested?.Invoke();
                    
                // We wait half a second to avoid firing the event multiple times for a single press
                await Task.Delay(500, token);
            }
                
            // Light polling every 50ms to avoid consuming the processor
            await Task.Delay(50, token); 
        }
    }
}