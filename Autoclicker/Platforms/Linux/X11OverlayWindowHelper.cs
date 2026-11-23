using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Microsoft.Extensions.Logging;

namespace Autoclicker.Platforms.Linux;

/// <summary>
/// Configures full-screen X11/XWayland overlay windows with <c>override_redirect = 1</c> so Wayland
/// compositors (such as COSMIC <c>cosmic-comp</c> and GNOME <c>mutter</c>) place each monitor's overlay
/// at its exact global monitor coordinates instead of centering all overlays on the currently active monitor.
/// Also manages an X11 keyboard grab while the overlay is open so <c>Escape</c> is delivered reliably
/// to unmanaged override-redirect windows.
/// </summary>
internal static partial class X11OverlayWindowHelper
{
    private const ulong CwOverrideRedirect = 1UL << 9;
    private const int RevertToParent = 2;
    private const int GrabModeAsync = 1;
    private const int GrabSuccess = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct XSetWindowAttributes
    {
        public IntPtr background_pixmap;
        public IntPtr background_pixel;
        public IntPtr border_pixmap;
        public IntPtr border_pixel;
        public int bit_gravity;
        public int win_gravity;
        public int backing_store;
        public IntPtr backing_planes;
        public IntPtr backing_pixel;
        public int save_under;
        public IntPtr event_mask;
        public IntPtr do_not_propagate_mask;
        public int override_redirect;
        public IntPtr colormap;
        public IntPtr cursor;
    }

    [LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr XOpenDisplay(string? display);

    [LibraryImport("libX11.so.6")]
    private static partial int XCloseDisplay(IntPtr display);

    [LibraryImport("libX11.so.6")]
    private static partial int XChangeWindowAttributes(
        IntPtr display,
        IntPtr w,
        ulong valuemask,
        ref XSetWindowAttributes attributes);

    [LibraryImport("libX11.so.6")]
    private static partial int XMoveResizeWindow(
        IntPtr display,
        IntPtr w,
        int x,
        int y,
        uint width,
        uint height);

    [LibraryImport("libX11.so.6")]
    private static partial int XRaiseWindow(IntPtr display, IntPtr w);

    [LibraryImport("libX11.so.6")]
    private static partial int XSetInputFocus(IntPtr display, IntPtr focus, int revertTo, IntPtr time);

    [LibraryImport("libX11.so.6")]
    private static partial int XGrabKeyboard(
        IntPtr display,
        IntPtr grabWindow,
        int ownerEvents,
        int pointerMode,
        int keyboardMode,
        IntPtr time);

    [LibraryImport("libX11.so.6")]
    private static partial int XUngrabKeyboard(IntPtr display, IntPtr time);

    [LibraryImport("libX11.so.6")]
    private static partial int XFlush(IntPtr display);

    /// <summary>
    /// Marks <paramref name="window"/> as an X11 override-redirect window before it is mapped and
    /// sets its explicit physical screen geometry so XWayland/X11 window managers never relocate or
    /// stack multi-monitor overlays onto a single screen.
    /// </summary>
    public static void ConfigureOverrideRedirect(
        Window window,
        int x,
        int y,
        int width,
        int height,
        ILogger? logger = null)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            if (window.TryGetPlatformHandle()?.Handle is not { } xid || xid == IntPtr.Zero)
            {
                return;
            }

            IntPtr display = XOpenDisplay(null);
            if (display == IntPtr.Zero)
            {
                return;
            }

            try
            {
                var attrs = new XSetWindowAttributes
                {
                    override_redirect = 1
                };
                _ = XChangeWindowAttributes(display, xid, CwOverrideRedirect, ref attrs);

                if (width > 0 && height > 0)
                {
                    _ = XMoveResizeWindow(display, xid, x, y, (uint)width, (uint)height);
                }

                _ = XFlush(display);
            }
            finally
            {
                _ = XCloseDisplay(display);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not set X11 override_redirect on overlay window");
        }
    }

    /// <summary>
    /// Raises <paramref name="window"/>, assigns X11 input focus, and grabs keyboard input for the
    /// lifetime of the returned <see cref="IDisposable"/> so an override-redirect overlay window
    /// reliably receives <c>Escape</c> key presses.
    /// </summary>
    public static IDisposable? AcquireKeyboardFocus(Window window, ILogger? logger = null)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            if (window.TryGetPlatformHandle()?.Handle is not { } xid || xid == IntPtr.Zero)
            {
                return null;
            }

            IntPtr display = XOpenDisplay(null);
            if (display == IntPtr.Zero)
            {
                return null;
            }

            _ = XRaiseWindow(display, xid);
            _ = XSetInputFocus(display, xid, RevertToParent, IntPtr.Zero);
            int grabResult = XGrabKeyboard(display, xid, ownerEvents: 1, GrabModeAsync, GrabModeAsync, IntPtr.Zero);
            _ = XFlush(display);

            if (grabResult != GrabSuccess)
            {
                logger?.LogDebug("X11 keyboard grab returned {GrabResult}; using XSetInputFocus only", grabResult);
            }

            return new KeyboardFocusLease(display, grabResult == GrabSuccess);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not acquire X11 keyboard focus for overlay window");
            return null;
        }
    }

    private sealed class KeyboardFocusLease : IDisposable
    {
        private IntPtr _display;
        private readonly bool _hasGrab;

        public KeyboardFocusLease(IntPtr display, bool hasGrab)
        {
            _display = display;
            _hasGrab = hasGrab;
        }

        public void Dispose()
        {
            IntPtr display = _display;
            if (display == IntPtr.Zero)
            {
                return;
            }

            _display = IntPtr.Zero;
            try
            {
                if (_hasGrab)
                {
                    _ = XUngrabKeyboard(display, IntPtr.Zero);
                    _ = XFlush(display);
                }
            }
            catch
            {
                // Ignore cleanup errors on shutdown
            }
            finally
            {
                try
                {
                    _ = XCloseDisplay(display);
                }
                catch
                {
                    // Ignore display close errors
                }
            }
        }
    }
}
