using System;
using System.Threading.Tasks;

namespace Autoclicker.Services;

public interface IHotkeyListener
{
    // This event is raised when the user presses the emergency stop key
    event Action OnStopRequested;
    Task StartListening();
    void StopListening();

    // Wayland-specific members with defaults so Win32/X11 don't need changes.
    // On Win32 and X11, the hotkey works transparently (no manual setup needed).
    // On Wayland, the user must explicitly configure the emergency key.

    /// <summary>True if the platform requires explicit hotkey setup (Wayland only).</summary>
    bool RequiresManualSetup => false;

    /// <summary>True if the emergency hotkey has been configured and is ready to use.</summary>
    bool IsHotkeyConfigured => true;

    /// <summary>True if the GlobalShortcuts portal is available on the system.</summary>
    bool IsPortalAvailable => false;

    /// <summary>The busctl command the user needs to set up a manual GNOME shortcut.</summary>
    string SetupInstructions => string.Empty;

    /// <summary>Performs one-time initialization (e.g. starting D-Bus services).</summary>
    Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Attempts to configure the hotkey via the portal dialog. Returns true on success.</summary>
    Task<bool> ConfigureHotkeyAsync() => Task.FromResult(true);

    /// <summary>Marks the hotkey as configured manually by the user.</summary>
    void ConfirmManualSetup()
    {
    }
}