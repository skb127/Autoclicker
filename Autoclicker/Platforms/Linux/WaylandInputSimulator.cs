using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autoclicker.Services;
using Tmds.DBus;

namespace Autoclicker.Platforms.Linux;

// Map the Linux Remote Desktop portal interface
[DBusInterface("org.freedesktop.portal.RemoteDesktop")]
public interface IRemoteDesktopPortal : IDBusObject
{
    Task<ObjectPath> CreateSessionAsync(IDictionary<string, object> options);
    Task<ObjectPath> SelectDevicesAsync(ObjectPath sessionHandle, IDictionary<string, object> options);
    Task<ObjectPath> StartAsync(ObjectPath sessionHandle, string parentWindow, IDictionary<string, object> options);
        
    // Mouse simulation methods
    Task NotifyPointerMotionAbsoluteAsync(ObjectPath sessionHandle, IDictionary<string, object> options, uint stream, double x, double y);
    Task NotifyPointerButtonAsync(ObjectPath sessionHandle, IDictionary<string, object> options, int button, uint state);
}

public class WaylandInputSimulator : IInputSimulator
{
    private Connection? _connection;
    private IRemoteDesktopPortal? _portal;
        
    // In a production-ready app, this path is returned by the CreateSession method.
    // We use a test path to structure the injection.
    private ObjectPath _sessionHandle = new ObjectPath("/org/freedesktop/portal/desktop/session/test");
    
    public async Task InitializeAsync()
    {
        Console.WriteLine("Connecting to the RemoteDesktop portal on Wayland...");
        _connection = new Connection(Address.Session);
        await _connection.ConnectAsync();

        _portal = _connection.CreateProxy<IRemoteDesktopPortal>(
            "org.freedesktop.portal.Desktop",
            "/org/freedesktop/portal/desktop");

        // Architectural note: For the system to accept clicks, 
        // CreateSessionAsync -> SelectDevicesAsync -> StartAsync must be called here.
        // Upon doing this, Ubuntu will show the user a dialog asking for "Remote Control" permission.
        await Task.Delay(100);
    }
    
    public void SimulateClick(int x, int y)
    {
        if (_portal == null) return;

        // In Linux, the left mouse button has hardware code 272
        int leftButton = 272; 
        var options = new Dictionary<string, object>();

        try
        {
            // 1. Move the cursor to the exact X and Y position
            _portal.NotifyPointerMotionAbsoluteAsync(_sessionHandle, options, 0, x, y);
                
            // 2. Simulate pressing the button (state 1 = pressed)
            _portal.NotifyPointerButtonAsync(_sessionHandle, options, leftButton, 1);
                
            // 3. Simulate releasing the button (state 0 = released)
            _portal.NotifyPointerButtonAsync(_sessionHandle, options, leftButton, 0);
                
            Console.WriteLine($" Mouse events sent to X:{x}, Y:{y}");
        }
        catch (Exception ex)
        {
            // Since we didn't open the full interactive session, Tmds.DBus might throw an exception here.
            Console.WriteLine($"D-Bus error when trying to inject click: {ex.Message}");
        }
    }
}