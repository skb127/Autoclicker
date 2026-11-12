using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autoclicker.Core;
using Autoclicker.Services;
using Avalonia;
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

    /// <summary>
    /// Metadata returned by the XDG ScreenCast / RemoteDesktop portal for the selected stream.
    /// </summary>
    public sealed record PortalStreamInfo(
        uint StreamId,
        int PositionX,
        int PositionY,
        int Width,
        int Height,
        uint SourceType,
        string? MappingId)
    {
        public static PortalStreamInfo Parse(object rawStreamEntry)
        {
            dynamic entry = rawStreamEntry;
            uint streamId = (uint)entry.Item1;

            int posX = 0, posY = 0;
            int width = 0, height = 0;
            uint sourceType = 0;
            string? mappingId = null;

            if (entry.Item2 is IDictionary<string, object> props)
            {
                var logParts = new List<string>();
                foreach (var kv in props)
                {
                    logParts.Add($"{kv.Key}={FormatPortalValue(kv.Value)}");
                }

                Console.WriteLine(
                    $"Wayland portal stream [{streamId}] properties: " +
                    (logParts.Count > 0 ? string.Join(", ", logParts) : "(none)"));

                if (props.TryGetValue("position", out object? posObj) &&
                    TryExtractIntPair(posObj, out int px, out int py))
                {
                    posX = px;
                    posY = py;
                }

                if (props.TryGetValue("size", out object? sizeObj) &&
                    TryExtractIntPair(sizeObj, out int sw, out int sh))
                {
                    width = sw;
                    height = sh;
                }

                if (props.TryGetValue("source_type", out object? stObj))
                {
                    try
                    {
                        sourceType = Convert.ToUInt32(stObj);
                    }
                    catch
                    {
                        /* ignore */
                    }
                }

                if (props.TryGetValue("mapping_id", out object? mapObj) || props.TryGetValue("id", out mapObj))
                {
                    mappingId = mapObj.ToString();
                }
            }

            return new PortalStreamInfo(streamId, posX, posY, width, height, sourceType, mappingId);
        }

        /// <summary>
        /// Builds a static fallback <see cref="AxisTransform"/> using the stream's reported
        /// logical position offset when interactive calibration is unavailable.
        /// </summary>
        public AxisTransform CreateStaticFallbackTransform() =>
            new(ScaleX: 1.0, OffsetX: PositionX, ScaleY: 1.0, OffsetY: PositionY);

        private static bool TryExtractIntPair(object? obj, out int first, out int second)
        {
            first = 0;
            second = 0;
            if (obj is null) return false;

            try
            {
                if (obj is ValueTuple<int, int> vtInt)
                {
                    first = vtInt.Item1;
                    second = vtInt.Item2;
                    return true;
                }

                if (obj is ValueTuple<uint, uint> vtUint)
                {
                    first = (int)vtUint.Item1;
                    second = (int)vtUint.Item2;
                    return true;
                }

                var type = obj.GetType();
                var item1Field = type.GetField("Item1");
                var item2Field = type.GetField("Item2");
                if (item1Field is not null && item2Field is not null)
                {
                    first = Convert.ToInt32(item1Field.GetValue(obj));
                    second = Convert.ToInt32(item2Field.GetValue(obj));
                    return true;
                }

                if (obj is IList { Count: >= 2 } list)
                {
                    first = Convert.ToInt32(list[0]);
                    second = Convert.ToInt32(list[1]);
                    return true;
                }
            }
            catch
            {
                // Ignore extraction failure
            }

            return false;
        }

        private static string FormatPortalValue(object? value)
        {
            if (value is null) return "null";
            if (TryExtractIntPair(value, out int a, out int b)) return $"({a}, {b})";
            return value.ToString() ?? string.Empty;
        }
    }

    public sealed class WaylandInputSimulator : IInputSimulator
    {
        // Linux evdev button codes (from linux/input-event-codes.h)
        private const int BtnLeft = 272;
        private const int BtnRight = 273;
        private const int BtnMiddle = 274;

        // Margin in pixels tolerated when checking if a coordinate belongs to the shared monitor
        private const int BoundsTolerancePx = 8;

        private static readonly IDictionary<string, object> EmptyOptions = new Dictionary<string, object>();

        private Connection? _connection;
        private ConnectionInfo? _connectionInfo;
        private IRemoteDesktopPortal? _portal;
        private IScreenCastPortal? _screenCastPortal;
        private ObjectPath _sessionHandle;
        private uint _streamId;
        private bool _isSessionActive;

        private PortalStreamInfo? _streamInfo;
        private AxisTransform _transform = AxisTransform.Identity;
        private PixelRect? _sharedMonitorGlobalBounds;
        private string? _sharedMonitorName;

        private int _lastX;
        private int _lastY;
        private bool _hasLastPosition;

        public async Task InitializeAsync()
        {
            // Reuse the existing portal session if it is already active and calibrated
            if (_isSessionActive && _portal is not null && _streamInfo is not null)
            {
                _hasLastPosition = false;
                Console.WriteLine($"Reusing active Wayland portal session (Stream {_streamId}).");
                return;
            }

            ResetSession();

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
                        object firstStreamObj = streamsArray.GetValue(0)!;
                        _streamInfo = PortalStreamInfo.Parse(firstStreamObj);
                        _streamId = _streamInfo.StreamId;
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

            // Resolve global-to-stream coordinate mapping (direct match by mapping_id/size, or sensor probe)
            if (_streamInfo is not null)
            {
                var calibration = await WaylandPointerCalibrator.CalibrateAsync(this, _streamInfo);
                if (calibration is not null)
                {
                    _transform = calibration.Transform;
                    _sharedMonitorGlobalBounds = calibration.SharedMonitorGlobalBounds;
                    _sharedMonitorName = calibration.SharedMonitorName;
                }
                else
                {
                    _transform = _streamInfo.CreateStaticFallbackTransform();
                    _sharedMonitorGlobalBounds = null;
                    _sharedMonitorName = null;
                    Console.WriteLine($"Using static fallback stream transform: {_transform}");
                }
            }

            _hasLastPosition = false;
            _isSessionActive = true;
            Console.WriteLine("Wayland session started. Permissions granted.");
        }

        /// <summary>
        /// Moves the pointer using raw stream coordinates without applying <see cref="_transform"/>.
        /// Used internally by <see cref="WaylandPointerCalibrator"/> during fallback probe calibration.
        /// </summary>
        internal async Task MoveToRawStreamAsync(double streamX, double streamY)
        {
            if (_portal is null) return;

            try
            {
                await _portal.NotifyPointerMotionAbsoluteAsync(
                    _sessionHandle,
                    EmptyOptions,
                    _streamId,
                    streamX,
                    streamY);
            }
            catch (Exception ex)
            {
                string errorMsg = ex.InnerException?.Message ?? ex.Message;
                Console.WriteLine($"Error injecting calibration pointer motion: {errorMsg}");
            }
        }

        public async Task MoveToAsync(int x, int y)
        {
            if (_portal is null) return;

            try
            {
                var (streamX, streamY) = _transform.ToStream(x, y);

                // Clamp to valid stream bounds so edge jitter never triggers 'Invalid position'
                if (_streamInfo is { Width: > 1, Height: > 1 })
                {
                    streamX = Math.Clamp(streamX, 0.0, _streamInfo.Width - 1.0);
                    streamY = Math.Clamp(streamY, 0.0, _streamInfo.Height - 1.0);
                }

                await _portal.NotifyPointerMotionAbsoluteAsync(
                    _sessionHandle,
                    EmptyOptions,
                    _streamId,
                    streamX,
                    streamY);

                // Store the global coordinate so WindMouse trajectories remain in global space
                _lastX = x;
                _lastY = y;
                _hasLastPosition = true;
            }
            catch (Exception ex)
            {
                string errorMsg = ex.InnerException?.Message ?? ex.Message;
                Console.WriteLine($"Error injecting pointer motion: {errorMsg}");
                // Mark session inactive if the portal rejected the call because the user stopped sharing
                if (errorMsg.Contains("Invalid session", StringComparison.OrdinalIgnoreCase) ||
                    errorMsg.Contains("Closed", StringComparison.OrdinalIgnoreCase))
                {
                    ResetSession();
                }
            }
        }

        public async Task ButtonAsync(MouseButtonKind button, bool isDown)
        {
            if (_portal is null) return;

            int evdevButton = button switch
            {
                MouseButtonKind.Left => BtnLeft,
                MouseButtonKind.Right => BtnRight,
                MouseButtonKind.Middle => BtnMiddle,
                _ => BtnLeft
            };

            try
            {
                await _portal.NotifyPointerButtonAsync(_sessionHandle, EmptyOptions, evdevButton, isDown ? 1u : 0u);
            }
            catch (Exception ex)
            {
                string errorMsg = ex.InnerException?.Message ?? ex.Message;
                Console.WriteLine($"Error injecting pointer button: {errorMsg}");
                if (errorMsg.Contains("Invalid session", StringComparison.OrdinalIgnoreCase) ||
                    errorMsg.Contains("Closed", StringComparison.OrdinalIgnoreCase))
                {
                    ResetSession();
                }
            }
        }

        public bool TryGetCursorPosition(out int x, out int y)
        {
            // Wayland compositors do not expose global cursor coordinates to clients.
            // We return the last global position injected by this simulator (if any) so that
            // subsequent trajectories can smoothly start from the previous target point.
            x = _lastX;
            y = _lastY;
            return _hasLastPosition;
        }

        public IReadOnlyList<string> ValidateTargets(IEnumerable<(int X, int Y)> targets)
        {
            var issues = new List<string>();

            if (_sharedMonitorGlobalBounds is { } bounds)
            {
                int minX = bounds.X - BoundsTolerancePx;
                int minY = bounds.Y - BoundsTolerancePx;
                int maxX = bounds.Right + BoundsTolerancePx;
                int maxY = bounds.Bottom + BoundsTolerancePx;

                string monitorDesc = string.IsNullOrWhiteSpace(_sharedMonitorName)
                    ? $"({bounds.X}, {bounds.Y}) {bounds.Width}x{bounds.Height}"
                    : $"{_sharedMonitorName} [({bounds.X}, {bounds.Y}) {bounds.Width}x{bounds.Height}]";

                foreach (var (x, y) in targets)
                {
                    if (x < minX || x > maxX || y < minY || y > maxY)
                    {
                        issues.Add(
                            $"Coordinate (X: {x}, Y: {y}) is outside the shared monitor {monitorDesc}. " +
                            "Please share the monitor where your pattern coordinates are located.");
                    }
                }
            }
            else if (_streamInfo is { Width: > 0, Height: > 0 })
            {
                foreach (var (x, y) in targets)
                {
                    var (sx, sy) = _transform.ToStream(x, y);
                    if (sx < -BoundsTolerancePx ||
                        sy < -BoundsTolerancePx ||
                        sx > _streamInfo.Width + BoundsTolerancePx ||
                        sy > _streamInfo.Height + BoundsTolerancePx)
                    {
                        issues.Add(
                            $"Coordinate (X: {x}, Y: {y}) maps outside the shared stream bounds ({_streamInfo.Width}x{_streamInfo.Height}). " +
                            "Please share the monitor where your pattern coordinates are located.");
                    }
                }
            }

            // If target validation fails, reset the cached session so the next Start click
            // prompts the user to select the appropriate monitor in the portal dialog.
            if (issues.Count > 0)
            {
                ResetSession();
            }

            return issues;
        }

        private void ResetSession()
        {
            _isSessionActive = false;
            _hasLastPosition = false;
            _portal = null;
            _screenCastPortal = null;
            _streamInfo = null;
            try
            {
                _connection?.Dispose();
            }
            catch
            {
                // Ignore disposal errors
            }

            _connection = null;
            _connectionInfo = null;
        }

        /// <summary>
        /// Invokes a Wayland portal method using D-Bus and waits for the response.
        /// </summary>
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
                    "The portal did not respond within 30s. Tip: If you clicked 'Share' without explicitly highlighting a screen preview first, GNOME/COSMIC may wait for a selection. Click the monitor preview before sharing.");

            return await tcs.Task;
        }
    }
}
