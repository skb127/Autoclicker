using System;
using System.Collections.Generic;
using System.IO;
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

[DBusInterface("org.freedesktop.DBus.Properties")]
public interface IDBusProperties : IDBusObject
{
    Task<object> GetAsync(string interfaceName, string propName);
}

/// <summary>
/// Hybrid hotkey listener for Wayland environments.
/// 
/// Strategy:
/// 1. Always starts a D-Bus service (com.autoclicker.app) as the guaranteed fallback.
///    The user can configure a system shortcut in their desktop's Keyboard Settings.
/// 2. Automatically checks if a custom shortcut calling com.autoclicker.app is already
///    configured in COSMIC Settings (or previously confirmed in app settings).
/// 3. If the GlobalShortcuts portal is genuinely supported by the desktop backend,
///    offers a "Configure via Portal" button that opens the native shortcut dialog.
/// 4. The Start button is blocked until the emergency key is ready through either method.
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

    // Wayland always requires explicit setup (unlike Win32/X11 where ESC works transparently)
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
    /// Starts the D-Bus fallback service, checks for existing custom shortcuts, and probes
    /// whether the GlobalShortcuts portal interface is genuinely implemented by the desktop backend.
    /// </summary>
    public async Task InitializeAsync()
    {
        await _dbusService.StartAsync();

        if (TryDetectExistingSystemShortcut())
        {
            _isHotkeyConfigured = true;
            Console.WriteLine("Existing system custom shortcut detected — emergency key marked as ready.");
        }

        _isPortalAvailable = await ProbePortalAvailableAsync();

        Console.WriteLine(_isPortalAvailable
            ? "GlobalShortcuts portal detected — 'Configure via Portal' button enabled."
            : "GlobalShortcuts portal not supported by current desktop backend — using D-Bus shortcut.");
    }

    // Portal Configuration (called from UI button)

    /// <summary>
    /// Attempts to configure the emergency hotkey via the XDG GlobalShortcuts portal.
    /// Opens an interactive dialog where the user can choose a key binding.
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

            // Best-effort registration with org.freedesktop.host.portal.Registry.
            // This interface was introduced in xdg-desktop-portal 1.20; on systems running
            // xdg-desktop-portal 1.18 (such as Ubuntu 24.04 / Pop!_OS 24.04), it does not exist
            // and must not abort the GlobalShortcuts flow.
            try
            {
                var registry = connection.CreateProxy<IPortalRegistry>(
                    "org.freedesktop.portal.Desktop",
                    "/org/freedesktop/portal/desktop");
                await registry.RegisterAsync(AppId, new Dictionary<string, object>());
                Console.WriteLine($"App identity registered via host portal registry: {AppId}");
            }
            catch (Exception regEx)
            {
                Console.WriteLine(
                    $"Note: org.freedesktop.host.portal.Registry not available ({regEx.Message}); continuing without explicit host registration.");
            }

            _portal = connection.CreateProxy<IGlobalShortcutsPortal>(
                "org.freedesktop.portal.Desktop",
                "/org/freedesktop/portal/desktop");

            // Use a CONSTANT session token so the portal restores the previously granted shortcut.
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
                // If BindShortcuts fails, we MUST close the session. Otherwise, the compositor keeps
                // the 'poisoned' session active in memory, and because our token is constant,
                // subsequent app runs will fail trying to resume it.
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
    /// Called when the user confirms they have configured the custom shortcut manually.
    /// </summary>
    public void ConfirmManualSetup()
    {
        _isHotkeyConfigured = true;
        Console.WriteLine("Manual D-Bus shortcut confirmed.");
    }

    // StartListening / StopListening

    public Task StartListening()
    {
        // The D-Bus service is already listening (started in InitializeAsync).
        // The portal subscription is already active (if configured via ConfigureHotkeyAsync).
        return Task.CompletedTask;
    }

    public void StopListening()
    {
        // Keep the D-Bus service and portal subscription alive for the next Start cycle.
    }

    // Private Helpers

    /// <summary>
    /// Checks whether the GlobalShortcuts portal interface is genuinely implemented by the
    /// active desktop portal backend by querying its D-Bus <c>version</c> property.
    /// <para>
    /// Note: Subscribing to a signal (such as <c>WatchActivatedAsync</c>) cannot be used to probe
    /// an interface because D-Bus <c>AddMatch</c> succeeds locally even when the remote object
    /// does not implement the interface.
    /// </para>
    /// </summary>
    private async Task<bool> ProbePortalAvailableAsync()
    {
        try
        {
            var connection = _dbusService.Connection;
            if (connection is null)
            {
                return false;
            }

            var propertiesProxy = connection.CreateProxy<IDBusProperties>(
                "org.freedesktop.portal.Desktop",
                "/org/freedesktop/portal/desktop");

            object versionObj = await propertiesProxy.GetAsync(
                "org.freedesktop.portal.GlobalShortcuts",
                "version");

            uint version = Convert.ToUInt32(versionObj);
            return version >= 1;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks whether the user has already configured the D-Bus stop command in their desktop's
    /// custom shortcuts configuration (for example, COSMIC's <c>Shortcuts/v1/custom</c> file).
    /// </summary>
    private static bool TryDetectExistingSystemShortcut()
    {
        try
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home))
            {
                return false;
            }

            // Check COSMIC Desktop custom shortcuts file
            string cosmicCustomShortcutsPath = Path.Combine(
                home,
                ".config",
                "cosmic",
                "com.system76.CosmicSettings.Shortcuts",
                "v1",
                "custom");

            if (File.Exists(cosmicCustomShortcutsPath))
            {
                string content = File.ReadAllText(cosmicCustomShortcutsPath);
                if (content.Contains("com.autoclicker.app", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Ignore file read errors and fall back to persisted or manual confirmation
        }

        return false;
    }

    private async Task<IDictionary<string, object>> CallPortalAsync(
        Func<string, Task<ObjectPath>> portalMethod,
        int timeoutSeconds = 5)
    {
        var connectionInfo = _dbusService.ConnectionInfo;
        var connection = _dbusService.Connection;
        if (connection == null || connectionInfo == null)
            throw new Exception("D-Bus connection not initialized.");

        string token = "req" + Guid.NewGuid().ToString("N");

        // ":1.23" -> "1_23"
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
