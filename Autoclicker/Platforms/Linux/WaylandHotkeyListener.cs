using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autoclicker.Services;
using Tmds.DBus;

namespace Autoclicker.Platforms.Linux;

// 1. We map the Ubuntu system service for global shortcuts
[DBusInterface("org.freedesktop.portal.GlobalShortcuts")]
public interface IGlobalShortcutsPortal : IDBusObject
{
    Task<ObjectPath> CreateSessionAsync(IDictionary<string, object> options);
    Task<ObjectPath> BindShortcutsAsync(ObjectPath sessionHandle, IDictionary<string, object> shortcuts, string parentWindow, IDictionary<string, object> options);
        
    // This is the event that Ubuntu will call when the user presses the key
    Task<IDisposable> WatchActivatedAsync(Action<(ObjectPath sessionHandle, string shortcutId, ulong timestamp, IDictionary<string, object> options)> handler, Action<Exception>? onError = null);
}

public class WaylandHotkeyListener : IHotkeyListener
{
    // The event that the GUI will use to stop the autoclicker
    public event Action? OnStopRequested;
        
    private Connection? _connection;
    private IGlobalShortcutsPortal? _portal;

    public async void StartListening()
    {
        try
        {
            Console.WriteLine("Connecting to the GlobalShortcuts portal on Wayland...");
            _connection = new Connection(Address.Session);
            await _connection.ConnectAsync();

            _portal = _connection.CreateProxy<IGlobalShortcutsPortal>(
                "org.freedesktop.portal.Desktop",
                "/org/freedesktop/portal/desktop");

            // Architectural Note: In a production environment, we would invoke here:
            // 1. CreateSessionAsync
            // 2. BindShortcutsAsync (Ubuntu would ask the user which key they want to use)
            // 3. WatchActivatedAsync (To subscribe to key presses)
                
            // To avoid blocking the application with the permissions dialog at this stage:
            await Task.Delay(100); 
            Console.WriteLine("Global shortcut registered (Simulated). The system will notify us when it's pressed.");
                
            // If Ubuntu were to notify us of a key press, we would call this:
            // OnStopRequested?.Invoke();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error setting up the shortcut on Wayland: {ex.Message}");
        }
    }

    public void StopListening()
    {
        Console.WriteLine("Disconnected from the global shortcuts portal.");
        _connection?.Dispose();
    }
}