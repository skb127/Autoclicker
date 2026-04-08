using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autoclicker.Services;
using Tmds.DBus;

namespace Autoclicker.Platforms.Linux;

// Portal D-Bus Interfaces
[DBusInterface("org.freedesktop.portal.GlobalShortcuts")]
public interface IGlobalShortcutsPortal : IDBusObject
{
    Task<ObjectPath> CreateSessionAsync(IDictionary<string, object> options);

    Task<ObjectPath> BindShortcutsAsync(
        ObjectPath sessionHandle,
        (string, IDictionary<string, object>)[] shortcuts,
        string parentWindow,
        IDictionary<string, object> options);

    Task<IDisposable> WatchActivatedAsync(
        Action<(ObjectPath sessionHandle, string shortcutId, ulong timestamp, IDictionary<string, object> options)>
            handler,
        Action<Exception>? onError = null);
}

[DBusInterface("org.freedesktop.portal.Request")]
public interface IPortalRequest : IDBusObject
{
    Task<IDisposable> WatchResponseAsync(
        Action<(uint response, IDictionary<string, object> results)> handler,
        Action<Exception>? onError = null);
}

[DBusInterface("org.freedesktop.portal.Session")]
public interface IPortalSession : IDBusObject
{
    Task CloseAsync();
}

[DBusInterface("org.freedesktop.host.portal.Registry")]
public interface IPortalRegistry : IDBusObject
{
    Task RegisterAsync(string appId, IDictionary<string, object> options);
}

/// <summary>
/// Hybrid hotkey listener for Wayland environments.
/// 
/// Strategy:
/// 1. Always starts a D-Bus service (com.autoclicker.App) as the guaranteed fallback.
///    The user can configure a system shortcut in GNOME Settings → Keyboard → Custom Shortcuts.
/// 2. If the GlobalShortcuts portal is available (GNOME 48+), offers a "Configure via Portal"
///    button that opens GNOME's native shortcut configuration dialog.
/// 3. The Start button is blocked until the user has configured the emergency key through
///    either method.
/// </summary>
public sealed class WaylandHotkeyListener : IHotkeyListener
{
    private const string AppId = "com.autoclicker.app";

    private readonly WaylandDbusStopService _dbusService;

    // Portal state
    private IGlobalShortcutsPortal? _portal;
    private ObjectPath _sessionHandle;

    // Configuration state
    private bool _isHotkeyConfigured;
    private bool _isPortalAvailable;
    
    public event Action? OnStopRequested;

    // Wayland always requires manual setup (unlike Win32/X11 where ESC works transparently)
    public bool RequiresManualSetup => true;
    public bool IsHotkeyConfigured => _isHotkeyConfigured;
    public bool IsPortalAvailable => _isPortalAvailable;
    public string SetupInstructions => WaylandDbusStopService.StopCommand; 

    public WaylandHotkeyListener(WaylandDbusStopService dbusService)
    {
        _dbusService = dbusService;

        _dbusService.OnStopReceived += () =>
        {
            Console.WriteLine("Emergency stop via D-Bus!");
            OnStopRequested?.Invoke();
        };
    }

    // Initialization (called once on app startup)

    /// <summary>
    /// Starts the D-Bus fallback service and probes whether the GlobalShortcuts portal exists.
    /// </summary>
    public async Task InitializeAsync()
    {
        await _dbusService.StartAsync();

        _isPortalAvailable = await ProbePortalAvailableAsync();

        Console.WriteLine(_isPortalAvailable
            ? "GlobalShortcuts portal detected — 'Configure via Portal' button enabled."
            : "GlobalShortcuts portal not detected — manual D-Bus setup required.");
    }

    // Portal Configuration (called from UI button)

    /// <summary>
    /// Attempts to configure the emergency hotkey via the GNOME GlobalShortcuts portal.
    /// This opens an interactive dialog where the user can choose a key binding.
    /// Returns true if the shortcut was configured successfully.
    /// </summary>
    public async Task<bool> ConfigureHotkeyAsync()
    {
        try
        {
            Console.WriteLine("Starting portal shortcut configuration...");

            var connection = _dbusService.Connection;
            if (connection == null)
            {
                throw new Exception("D-Bus service is not running. Cannot configure portal.");
            }

            var registry = connection.CreateProxy<IPortalRegistry>(
                "org.freedesktop.portal.Desktop",
                "/org/freedesktop/portal/desktop");
            await registry.RegisterAsync(AppId, new Dictionary<string, object>());
            Console.WriteLine($"App identity registered: {AppId}");

            _portal = connection.CreateProxy<IGlobalShortcutsPortal>(
                "org.freedesktop.portal.Desktop",
                "/org/freedesktop/portal/desktop");

            // Use a CONSTANT session token so GNOME restores the previously granted shortcut.
            string sessionToken = "autoclicker_session_v3";
            var sessionResults = await CallPortalAsync(token =>
                _portal.CreateSessionAsync(new Dictionary<string, object>
                {
                    { "session_handle_token", sessionToken },
                    { "handle_token", token }
                })
            );
            _sessionHandle = new ObjectPath(sessionResults["session_handle"].ToString()!);
            Console.WriteLine("GlobalShortcuts session created.");

            var shortcuts = new (string, IDictionary<string, object>)[]
            {
                ("stop-autoclicker-v2",
                    new Dictionary<string, object>
                    {
                        { "description", "Emergency Stop Autoclicker" }
                    })
            };

            try
            {
                Console.WriteLine("Waiting for shortcut configuration dialog...");
                await CallPortalAsync(token =>
                        _portal.BindShortcutsAsync(_sessionHandle, shortcuts, "", new Dictionary<string, object>
                        {
                            { "handle_token", token }
                        }),
                    timeoutSeconds: 60
                );
            }
            catch
            {
                // If BindShortcuts fails, we MUST close the session. Otherwise, GNOME keeps the 
                // 'poisoned' session active in memory, and because our token is constant, 
                // subsequent app runs will instantly crash trying to resume it.
                var sessionProxy = connection.CreateProxy<IPortalSession>(
                    "org.freedesktop.portal.Desktop",
                    _sessionHandle);
                try { await sessionProxy.CloseAsync(); } catch { /* Ignore */ }
                throw;
            }

            await _portal.WatchActivatedAsync(
                args =>
                {
                    if (args.shortcutId == "stop-autoclicker-v2")
                    {
                        Console.WriteLine("Portal global shortcut triggered!");
                        OnStopRequested?.Invoke();
                    }
                },
                ex => Console.WriteLine($"Portal subscription error: {ex.Message}")
            );

            _isHotkeyConfigured = true;
            Console.WriteLine("Portal shortcut configured successfully!");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Portal configuration failed: {ex.Message}");
            _isPortalAvailable = false;
            return false;
        }
    }

    // Manual D-Bus Confirmation

    /// <summary>
    /// Called when the user confirms they have configured the GNOME custom shortcut manually.
    /// </summary>
    public void ConfirmManualSetup()
    {
        _isHotkeyConfigured = true;
        Console.WriteLine("Manual D-Bus shortcut confirmed by user.");
    }

    // StartListening / StopListening

    public Task StartListening()
    {
        // The D-Bus service is already listening (started in InitializeAsync).
        // The portal subscription is already active (if configured via ConfigureHotkeyAsync).
        // Nothing additional needed here.
        return Task.CompletedTask;
    }

    public void StopListening()
    {
        // Don't dispose the D-Bus service or portal subscription.
        // They should stay active for the next Start cycle.
    }

    // Private Helpers

    /// <summary>
    /// Checks whether the GlobalShortcuts portal interface is available on the system.
    /// </summary>
    private async Task<bool> ProbePortalAvailableAsync()
    {
        try
        {
            var connection = _dbusService.Connection!;

            // Verify the GlobalShortcuts interface is available by checking D-Bus for
            // the portal service name. Creating a proxy is cheap and synchronous;
            // the real test is whether the underlying name is resolvable.
            var portal = connection.CreateProxy<IGlobalShortcutsPortal>(
                "org.freedesktop.portal.Desktop",
                "/org/freedesktop/portal/desktop");

            // Make a lightweight call: subscribe+unsubscribe to the Activated signal.
            // If the interface doesn't exist, this will throw a DBusException.
            var sub = await portal.WatchActivatedAsync(_ => { });
            sub.Dispose();

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Executes a portal method via D-Bus and waits for a response within a specified timeout period.
    /// </summary>
    /// <param name="portalMethod">
    /// A delegate that represents the portal method to be called, which accepts a token as input
    /// and returns a task that resolves to an object path.
    /// </param>
    /// <param name="timeoutSeconds">
    /// The maximum duration, in seconds, to wait for a response from the portal method. Defaults to 5 seconds.
    /// </param>
    /// <returns>
    /// A task that resolves to a dictionary containing the response data from the portal method.
    /// </returns>
    /// <exception cref="Exception">
    /// Thrown if the D-Bus connection is not initialized.
    /// </exception>
    /// <exception cref="TimeoutException">
    /// Thrown if the portal does not respond within the specified timeout period.
    /// </exception>
    private async Task<IDictionary<string, object>> CallPortalAsync(
        Func<string, Task<ObjectPath>> portalMethod,
        int timeoutSeconds = 5)
    {
        var connectionInfo = _dbusService.ConnectionInfo;
        var connection = _dbusService.Connection;
        if (connection == null || connectionInfo == null)
            throw new Exception("D-Bus connection not initialized.");

        string token = "req" + Guid.NewGuid().ToString("N");

        // ":1.23" → "1_23"
        string senderName = connectionInfo.LocalName[1..].Replace('.', '_');
        ObjectPath expectedPath = new($"/org/freedesktop/portal/desktop/request/{senderName}/{token}");

        var requestProxy = connection.CreateProxy<IPortalRequest>(
            "org.freedesktop.portal.Desktop", expectedPath);
        var tcs = new TaskCompletionSource<IDictionary<string, object>>();

        using var disposable = await requestProxy.WatchResponseAsync(
            reply =>
            {
                if (reply.response == 0)
                    tcs.TrySetResult(reply.results);
                else
                    tcs.TrySetException(new Exception($"Action denied (Code: {reply.response})"));
            },
            ex => tcs.TrySetException(ex)
        );

        await portalMethod(token);

        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));
        if (await Task.WhenAny(tcs.Task, timeoutTask) == timeoutTask)
            throw new TimeoutException($"The portal did not respond within {timeoutSeconds}s.");

        return await tcs.Task;
    }
}