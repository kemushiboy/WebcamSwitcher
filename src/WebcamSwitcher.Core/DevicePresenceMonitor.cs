using Windows.Devices.Enumeration;

namespace WebcamSwitcher.Core;

/// <summary>A video capture device as Windows enumerates it right now.</summary>
public sealed record PhysicalCamera(string DeviceId, string FriendlyName, bool IsVirtual);

/// <summary>
/// Tracks which video capture devices are present, by exact device ID, using a
/// <see cref="DeviceWatcher"/> (no polling). Roles ask "is my ID present?" and
/// subscribe to <see cref="Changed"/> to retry immediately on arrival. Nothing
/// here ever maps one device to another: a role whose ID is gone stays missing.
/// </summary>
public sealed class DevicePresenceMonitor : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, PhysicalCamera> _present = new(StringComparer.OrdinalIgnoreCase);
    private DeviceWatcher? _watcher;
    private volatile bool _enumerated;

    /// <summary>Raised (on a pool thread) with the device ID that arrived or left.</summary>
    public event Action<string, bool>? Changed;

    /// <summary>True once the initial enumeration finished.</summary>
    public bool Ready => _enumerated;

    public void Start()
    {
        if (_watcher != null)
            return;
        try
        {
            _watcher = DeviceInformation.CreateWatcher(DeviceClass.VideoCapture);
            _watcher.Added += (_, info) => OnAdded(info);
            _watcher.Updated += (_, upd) => OnUpdated(upd);
            _watcher.Removed += (_, upd) => OnRemoved(upd.Id);
            _watcher.EnumerationCompleted += (_, _) =>
            {
                _enumerated = true;
                AppLog.Write("Devices", $"initial enumeration: {Snapshot().Count(c => !c.IsVirtual)} physical camera(s)");
            };
            _watcher.Stopped += (_, _) => AppLog.Write("Devices", "device watcher stopped");
            _watcher.Start();
        }
        catch (Exception ex)
        {
            AppLog.Write("Devices", $"device watcher failed to start: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Waits (bounded) for the initial enumeration.</summary>
    public async Task WaitReadyAsync(int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!_enumerated && Environment.TickCount64 < deadline)
            await Task.Delay(50).ConfigureAwait(false);
    }

    public bool IsPresent(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
            return false;
        lock (_lock)
            return _present.ContainsKey(deviceId);
    }

    public IReadOnlyList<PhysicalCamera> Snapshot()
    {
        lock (_lock)
            return _present.Values.ToList();
    }

    /// <summary>
    /// Virtual cameras (ours and other vendors') enumerate as software devices
    /// (<c>SWD#</c>) and must never be offered as a physical source.
    /// </summary>
    public static bool LooksVirtual(string id, string name) =>
        id.Contains(@"\SWD#", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith(@"\\?\SWD#", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Windows Virtual Camera", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("WebcamSwitcher", StringComparison.OrdinalIgnoreCase);

    private static bool IsEnabled(DeviceInformation info) =>
        info.IsEnabled;

    private void OnAdded(DeviceInformation info)
    {
        try
        {
            if (!IsEnabled(info))
                return;
            var cam = new PhysicalCamera(info.Id, info.Name ?? "", LooksVirtual(info.Id, info.Name ?? ""));
            bool added;
            lock (_lock)
                added = _present.TryAdd(info.Id, cam);
            if (added)
            {
                if (_enumerated)
                    AppLog.Write("Devices", $"camera arrived: '{cam.FriendlyName}' {CameraRoles.DeviceIdTail(cam.DeviceId)}{(cam.IsVirtual ? " (virtual)" : "")}");
                Changed?.Invoke(info.Id, true);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("Devices", $"Added handler threw {ex.Message}");
        }
    }

    private void OnUpdated(DeviceInformationUpdate upd)
    {
        try
        {
            // Interface enable/disable arrives as an update.
            if (upd.Properties.TryGetValue("System.Devices.InterfaceEnabled", out var v) && v is bool enabled)
            {
                if (!enabled)
                {
                    OnRemoved(upd.Id);
                    return;
                }
                lock (_lock)
                    if (_present.ContainsKey(upd.Id))
                        return;
                _ = AddByIdAsync(upd.Id);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("Devices", $"Updated handler threw {ex.Message}");
        }
    }

    private async Task AddByIdAsync(string id)
    {
        try
        {
            var info = await DeviceInformation.CreateFromIdAsync(id);
            OnAdded(info);
        }
        catch (Exception ex)
        {
            AppLog.Write("Devices", $"re-enable lookup failed for {CameraRoles.DeviceIdTail(id)}: {ex.Message}");
        }
    }

    private void OnRemoved(string id)
    {
        PhysicalCamera? cam;
        lock (_lock)
        {
            if (!_present.Remove(id, out cam))
                return;
        }
        AppLog.Write("Devices", $"camera removed: '{cam.FriendlyName}' {CameraRoles.DeviceIdTail(id)}");
        try { Changed?.Invoke(id, false); } catch { }
    }

    public void Dispose()
    {
        try
        {
            if (_watcher != null && _watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                _watcher.Stop();
        }
        catch { }
        _watcher = null;
    }
}
