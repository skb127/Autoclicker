using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autoclicker.Services;
using Tmds.DBus;

namespace Autoclicker.Platforms.Linux
{
    [DBusInterface("org.freedesktop.portal.RemoteDesktop")]
    public interface IRemoteDesktopPortal : IDBusObject
    {
        Task<ObjectPath> CreateSessionAsync(IDictionary<string, object> options);
        Task<ObjectPath> SelectDevicesAsync(ObjectPath sessionHandle, IDictionary<string, object> options);
        Task<ObjectPath> StartAsync(ObjectPath sessionHandle, string parentWindow, IDictionary<string, object> options);

        Task NotifyPointerMotionAbsoluteAsync(ObjectPath sessionHandle, IDictionary<string, object> options,
            uint stream, double x, double y);

        Task NotifyPointerButtonAsync(ObjectPath sessionHandle, IDictionary<string, object> options, int button,
            uint state);
    }

    [DBusInterface("org.freedesktop.portal.ScreenCast")]
    public interface IScreenCastPortal : IDBusObject
    {
        Task<ObjectPath> SelectSourcesAsync(ObjectPath sessionHandle, IDictionary<string, object> options);
    }

    [DBusInterface("org.freedesktop.portal.Request")]
    public interface IRequest : IDBusObject
    {
        Task<IDisposable> WatchResponseAsync(Action<(uint response, IDictionary<string, object> results)> handler,
            Action<Exception>? onError = null);
    }

    public sealed class WaylandInputSimulator : IInputSimulator
    {
        private Connection? _connection;
        private ConnectionInfo? _connectionInfo;
        private IRemoteDesktopPortal? _portal;
        private IScreenCastPortal? _screenCastPortal;
        private ObjectPath _sessionHandle;
        private uint _streamId;

        public async Task InitializeAsync()
        {
            Console.WriteLine("Connecting to Wayland portals...");
            _connection = new Connection(Address.Session);
            _connectionInfo = await _connection.ConnectAsync();

            _portal = _connection.CreateProxy<IRemoteDesktopPortal>(
                "org.freedesktop.portal.Desktop",
                "/org/freedesktop/portal/desktop");

            _screenCastPortal = _connection.CreateProxy<IScreenCastPortal>(
                "org.freedesktop.portal.Desktop",
                "/org/freedesktop/portal/desktop");

            string sessionToken = "s" + Guid.NewGuid().ToString("N");

            var sessionResults = await CallPortalAsync(token =>
                _portal.CreateSessionAsync(new Dictionary<string, object>
                {
                    { "session_handle_token", sessionToken },
                    { "handle_token", token }
                })
            );
            _sessionHandle = new ObjectPath(sessionResults["session_handle"].ToString()!);

            await CallPortalAsync(token =>
                _portal.SelectDevicesAsync(_sessionHandle, new Dictionary<string, object>
                {
                    { "types", 2u },
                    { "handle_token", token }
                })
            );

            await CallPortalAsync(token =>
                _screenCastPortal.SelectSourcesAsync(_sessionHandle, new Dictionary<string, object>
                {
                    { "types", 1u },
                    { "multiple", false },
                    { "handle_token", token }
                })
            );

            var startResults = await CallPortalAsync(token =>
                _portal.StartAsync(_sessionHandle, "", new Dictionary<string, object>
                {
                    { "handle_token", token }
                })
            );

            if (startResults.TryGetValue("devices", out object? devicesObj))
            {
                uint allowedDevices = Convert.ToUInt32(devicesObj);
                if ((allowedDevices & 2u) == 0) // 2 is Pointer
                {
                    throw new Exception("You must check 'Allow pointer control' to simulate clicks.");
                }
            }
            else
            {
                throw new Exception("Device control permissions were not returned.");
            }

            try
            {
                if (startResults.TryGetValue("streams", out object? streamsObj))
                {
                    var streamsArray = (Array)streamsObj;
                    if (streamsArray.Length > 0)
                    {
                        dynamic firstStream = streamsArray.GetValue(0)!;
                        _streamId = (uint)firstStream.Item1;
                        Console.WriteLine($"Stream ID obtained successfully: {_streamId}");
                    }
                    else
                    {
                        throw new Exception("You must select a monitor to share.");
                    }
                }
                else
                {
                    throw new Exception("Monitor sharing permissions were not returned.");
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Screen mapping permission was denied: {ex.Message}");
            }

            Console.WriteLine("Wayland session started. Permissions granted.");
        }

        public async Task SimulateClick(int x, int y)
        {
            if (_portal is null) return;

            int leftButton = 272; // Evdev physical code for BTN_LEFT
            var options = new Dictionary<string, object>();

            try
            {
                await _portal.NotifyPointerMotionAbsoluteAsync(_sessionHandle, options, _streamId, x, y);
                await _portal.NotifyPointerButtonAsync(_sessionHandle, options, leftButton, 1);
                await _portal.NotifyPointerButtonAsync(_sessionHandle, options, leftButton, 0);

                Console.WriteLine($"Click injected in Wayland -> X:{x}, Y:{y}");
            }
            catch (Exception ex)
            {
                string errorMsg = ex.InnerException?.Message ?? ex.Message;
                Console.WriteLine($"Error injecting click: {errorMsg}");
            }
        }

        /// <summary>
        /// Invokes a Wayland portal method using D-Bus and waits for the response.
        /// </summary>
        /// <param name="portalMethod">
        /// A function that executes the portal method with a string token as input
        /// and returns a Task containing the ObjectPath of the request.
        /// </param>
        /// <returns>
        /// A dictionary containing the response data from the Wayland portal method.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown if there is no active D-Bus connection or connection information.
        /// </exception>
        /// <exception cref="TimeoutException">
        /// Thrown if the portal method fails to respond within the allowed time frame.
        /// </exception>
        private async Task<IDictionary<string, object>> CallPortalAsync(Func<string, Task<ObjectPath>> portalMethod)
        {
            if (_connection == null || _connectionInfo == null)
                throw new InvalidOperationException("No D-Bus connection.");

            string token = "h" + Guid.NewGuid().ToString("N");
            string senderName = _connectionInfo.LocalName.Substring(1).Replace('.', '_');
            ObjectPath expectedPath = new ObjectPath($"/org/freedesktop/portal/desktop/request/{senderName}/{token}");

            var requestProxy = _connection.CreateProxy<IRequest>("org.freedesktop.portal.Desktop", expectedPath);
            var tcs = new TaskCompletionSource<IDictionary<string, object>>();

            using var disposable = await requestProxy.WatchResponseAsync(
                reply =>
                {
                    if (reply.response == 0) tcs.TrySetResult(reply.results);
                    else tcs.TrySetException(new Exception($"Action canceled (Code: {reply.response})"));
                },
                ex => tcs.TrySetException(ex)
            );

            await portalMethod(token);

            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30));
            if (await Task.WhenAny(tcs.Task, timeoutTask) == timeoutTask)
                throw new TimeoutException(
                    "The portal did not respond within 30s. Tip: If you clicked 'Share' without explicitly highlighting a screen preview first, GNOME silently hangs the request. You must click the screen before sharing.");

            return await tcs.Task;
        }
    }
}