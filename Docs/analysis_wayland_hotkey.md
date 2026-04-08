# Wayland GlobalShortcuts Post-Mortem

This document serves as a technical analysis and history of our implementation of the `org.freedesktop.portal.GlobalShortcuts` API on GNOME 48 (Wayland), explaining why the fallback D-Bus option is strictly required.

## The Problem
Wayland compositors (like Mutter/GNOME) strictly isolate client applications for security reasons. Unlike X11, an application cannot place a global keyboard hook to listen for an "emergency stop" key. 

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

Because `GlobalShortcuts` lacks stability across varying compositor setups, the `WaylandDbusStopService` provides an absolute fallback. It exposes a native D-Bus API (`Stop`) which a user can manually bind inside GNOME's native keyboard settings (`busctl --user call...`). GNOME Settings directly communicates with Mutter, completely bypassing the brittle Desktop Portal restrictions, guaranteeing the user can always stop the application.

*MainViewModel automatically handles falling back to this manual flow if the Portal throws Code 2.*
