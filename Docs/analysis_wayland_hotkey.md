# Wayland GlobalShortcuts Post-Mortem

This document serves as a technical analysis and history of our implementation of the `org.freedesktop.portal.GlobalShortcuts` API on Wayland (GNOME 48, COSMIC, KDE), explaining why the fallback D-Bus option is strictly required and how portal availability is probed.

## The Problem
Wayland compositors (like Mutter/GNOME and cosmic-comp/COSMIC) strictly isolate client applications for security reasons. Unlike X11, an application cannot place a global keyboard hook to listen for an "emergency stop" key. 

To resolve this, Freedesktop introduced the `GlobalShortcuts` portal API, which allows an app to request a shortcut through a native dialog.

## Implementation Hurdles Encountered

### 1. App Identity and D-Bus Names
The GNOME implementation of `xdg-desktop-portal` strictly validates the application requesting the shortcut. It requires that:
- A valid `.desktop` file exists in `~/.local/share/applications/` (e.g., `com.autoclicker.app.desktop`).
- The D-Bus connection making the portal request **must own a well-known bus name** exactly matching the `AppId` (e.g. `com.autoclicker.app`).

If a connection makes a request and its D-Bus name doesn't match the `.desktop` file name, GNOME silently blocks the request and classifies it as a spoofing attempt, returning `Action denied (Code: 2)`.

### 2. Session Poisoning
When creating a session for the shortcut portal (`CreateSessionAsync`), passing a unique UUID every execution caused GNOME's permission backend (`xdg-permission-store`) to hit internal limits and eventually refuse any further requests. 
We fixed this by passing a **constant** session token (e.g. `autoclicker_shortcut_session`).

However, this introduced a new failure state: **Poisoned Sessions**.
If the user assigns a shortcut in the dialog, but Mutter (the window manager) rejects it (for example, attempting to bind `Ctrl+Esc` which is reserved, or running inside a Virtual Machine which intercepts raw keys), `BindShortcutsAsync` drops the request and returns Code 2.
Because the session was never explicitly closed upon failure, the constant session token remained active but internally broken in GNOME's RAM. All subsequent runs of the application would immediately crash upon trying to resume the broken session.

**Solution:** Mandatory `CloseAsync()` on the session instance inside the `catch` block to cleanly destroy the poisoned session state, along with temporarily bumping the `AppId` and `SessionToken` versions to evade the permanently blocked IDs in the user's local `xdg-permission-store`.

### 3. Ultimate Fallback (The D-Bus Native Solution)
Even perfectly formatted Portal requests fail silently if the underlying compositor (e.g., inside VirtualBox/VMWare) refuses to bind keys via the Portal dialog. 

Because `GlobalShortcuts` lacks stability across varying compositor setups, the `WaylandDbusStopService` provides an absolute fallback. It exposes a native D-Bus API (`Stop`) which a user can manually bind inside their desktop's native keyboard settings (`busctl --user call...`). The desktop's settings daemon directly communicates with the compositor, completely bypassing the brittle Desktop Portal restrictions and guaranteeing the user can always stop the application.

*MainViewModel automatically handles falling back to this manual flow if the Portal throws Code 2.*

### 4. Probing `GlobalShortcuts` Availability & `host.portal.Registry` Compatibility (COSMIC / Ubuntu 24.04)
Two additional D-Bus subtleties surfaced when testing on **COSMIC Desktop (`xdg-desktop-portal-cosmic` 1.11 + `xdg-desktop-portal` 1.18.4)**:

1. **Why Signal Subscriptions (`WatchActivatedAsync`) Cannot Probe Interface Existence:**
   - Initially, `ProbePortalAvailableAsync` tested whether `org.freedesktop.portal.GlobalShortcuts` existed by subscribing to its `Activated` signal via `WatchActivatedAsync`.
   - Under D-Bus, subscribing to a signal only registers a local `AddMatch` filter rule on `dbus-daemon` — it **never contacts `/org/freedesktop/portal/desktop`**. Consequently, `WatchActivatedAsync` succeeded even on desktop backends (like COSMIC) that do not implement `GlobalShortcuts`, falsely enabling the *"Configure via Portal"* button.
   - **Fix:** Query the `version` property via `org.freedesktop.DBus.Properties.Get("org.freedesktop.portal.GlobalShortcuts", "version")` on `/org/freedesktop/portal/desktop`. This performs a real round-trip to `xdg-desktop-portal` and throws `DBusException` whenever the active portal backend does not implement `GlobalShortcuts`.
2. **`org.freedesktop.host.portal.Registry` Requires `xdg-desktop-portal >= 1.20`:**
   - The `org.freedesktop.host.portal.Registry` interface was introduced in `xdg-desktop-portal 1.20`. On distributions shipping `xdg-desktop-portal 1.18.x` (such as Ubuntu 24.04 LTS and Pop!_OS 24.04), calling `Registry.RegisterAsync` unconditionally throws `org.freedesktop.DBus.Error.UnknownMethod`.
   - **Fix:** Wrap `Registry.RegisterAsync` in a non-fatal `try / catch` block so host registration is best-effort and never blocks `GlobalShortcuts` on `xdg-desktop-portal < 1.20`.
3. **Auto-Detection & Persistence of Custom Shortcuts:**
   - On startup, `WaylandHotkeyListener` inspects `~/.config/cosmic/com.system76.CosmicSettings.Shortcuts/v1/custom` (on COSMIC) and `AppSettings.WaylandHotkeyConfirmed` (`settings.json`) so users who have already configured the `busctl` stop shortcut do not need to re-confirm it on every application launch.
