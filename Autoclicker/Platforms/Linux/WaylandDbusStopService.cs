using System;
using System.Threading.Tasks;
using Tmds.DBus;

namespace Autoclicker.Platforms.Linux;

/// <summary>
/// D-Bus interface for requesting the well-known bus name.
/// </summary>
[DBusInterface("org.freedesktop.DBus")]
public interface IDBusDaemon : IDBusObject
{
    Task<uint> RequestNameAsync(string name, uint flags);
}

/// <summary>
/// The D-Bus interface exposed by our application.
/// External callers (e.g. GNOME keyboard shortcuts) can call Stop to halt the autoclicker.
/// </summary>
[DBusInterface("com.autoclicker.app")]
public interface IAutoclickerDbusInterface : IDBusObject
{
    Task StopAsync();
}

/// <summary>
/// A D-Bus service that exposes a Stop method on the session bus.
/// This allows the user to configure a system keyboard shortcut in GNOME Settings
/// that calls this method to stop the autoclicker, bypassing Wayland's restriction on
/// global keyboard listeners.
///
/// Usage from terminal or GNOME custom shortcut:
///   busctl --user call com.autoclicker.App /com/autoclicker/App com.autoclicker.App Stop
/// </summary>
public sealed class WaylandDbusStopService : IAutoclickerDbusInterface
{
    private const string ServiceName = "com.autoclicker.app";
    private const string ObjectPathStr = "/com/autoclicker/app";

    /// <summary>
    /// The full busctl command the user needs to configure as a GNOME custom shortcut.
    /// </summary>
    public static string StopCommand =>
        $"busctl --user call {ServiceName} {ObjectPathStr} {ServiceName} Stop";

    /// <summary>Fired when an external caller invokes the Stop method via D-Bus.</summary>
    public event Action? OnStopReceived;

    public ObjectPath ObjectPath => new(ObjectPathStr);

    public Connection? Connection => _connection;
    public ConnectionInfo? ConnectionInfo => _connectionInfo;
    private Connection? _connection;
    private ConnectionInfo? _connectionInfo;
    private bool _isRunning;

    /// <summary>
    /// Starts the D-Bus service: connects to the session bus, registers the object,
    /// and requests the well-known name so external callers can find us.
    /// </summary>
    public async Task StartAsync()
    {
        if (_isRunning) return;

        try
        {
            _connection = new Connection(Address.Session);
            _connectionInfo = await _connection.ConnectAsync();

            await _connection.RegisterObjectAsync(this);

            var daemon = _connection.CreateProxy<IDBusDaemon>(
                "org.freedesktop.DBus",
                "/org/freedesktop/DBus");
            uint result = await daemon.RequestNameAsync(ServiceName, 0);

            if (result == 1) // DBUS_REQUEST_NAME_REPLY_PRIMARY_OWNER
            {
                _isRunning = true;
                Console.WriteLine($"D-Bus service active: {ServiceName}");
                Console.WriteLine($"Stop command: {StopCommand}");
            }
            else
            {
                Console.WriteLine($"Warning: Could not acquire D-Bus name (result: {result})");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Could not start D-Bus service: {ex.Message}");
        }
    }

    /// <summary>
    /// Called when an external process invokes the Stop method via D-Bus.
    /// This is the entry point for the GNOME custom shortcut.
    /// </summary>
    public Task StopAsync()
    {
        Console.WriteLine("Emergency stop received via D-Bus!");
        OnStopReceived?.Invoke();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _isRunning = false;
    }
}
