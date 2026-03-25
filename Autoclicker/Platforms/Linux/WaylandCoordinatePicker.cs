using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autoclicker.Services;
using Tmds.DBus;

namespace Autoclicker.Platforms.Linux;

// 1. Map the Ubuntu system service (Screenshot Portal)
[DBusInterface("org.freedesktop.portal.Screenshot")]
public interface IScreenshotPortal : IDBusObject
{
    // Method to request an interactive screenshot
    Task<ObjectPath> ScreenshotAsync(string parentWindow, IDictionary<string, object> options);
}

// 2. Map the Response object returned by Ubuntu
[DBusInterface("org.freedesktop.portal.Request")]
public interface IRequest : IDBusObject
{
    // Event triggered when the user finishes interacting
    Task<IDisposable> WatchResponseAsync(Action<(uint response, IDictionary<string, object> results)> handler, Action<Exception>? onError = null);
}

public class WaylandCoordinatePicker : ICoordinatePicker
{
    public async Task<(int X, int Y)> PickCoordinateAsync()
    {
        try
        {
            // Connect to the user session bus in Ubuntu
            using var connection = new Connection(Address.Session);
            await connection.ConnectAsync();

            // Create "proxy" to communicate with the desktop portal
            var portal = connection.CreateProxy<IScreenshotPortal>(
                "org.freedesktop.portal.Desktop",
                "/org/freedesktop/portal/desktop");

            var tcs = new TaskCompletionSource<(int X, int Y)>();

            // Configure options to ask Ubuntu to let the user select an area (interactive)
            var options = new Dictionary<string, object>
            {
                { "interactive", true }
            };

            // Send the request. Ubuntu returns a "Request" (a waiting room)
            var requestPath = await portal.ScreenshotAsync("", options);
            var request = connection.CreateProxy<IRequest>("org.freedesktop.portal.Desktop", requestPath);

            // Wait for the user to click on the screen
            await request.WatchResponseAsync(
                reply =>
                {
                    // Ubuntu returns control here.
                    // In a more advanced real scenario, we would extract the selected geometry
                    // from 'reply.results'. For now we return a simulated value
                    // so the UI reacts and to not complicate the click injection phase.
                    Console.WriteLine("The user selected the point through the GNOME environment.");
                    tcs.TrySetResult((500, 500)); 
                },
                ex => tcs.TrySetException(ex)
            );

            // Wait until the Task is resolved
            return await tcs.Task;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error connecting to the Wayland portal: {ex.Message}");
            return (0, 0);
        }
    }
}