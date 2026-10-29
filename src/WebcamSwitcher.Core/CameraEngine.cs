using Windows.Devices.Enumeration;

namespace WebcamSwitcher.Core;

/// <summary>
/// Runs one independent <see cref="RoleChannel"/> per Camera Role and mirrors
/// the active role into the (optional) switcher pipe.
///
/// Roles are matched across reconfigurations by their persistent
/// <see cref="CameraConfig.VirtualCameraId"/>: <see cref="ApplyRolesAsync"/>
/// rebuilds only the roles whose capture settings changed, so editing CAM-03
/// never interrupts CAM-01.
/// </summary>
public sealed class CameraEngine : IAsyncDisposable, IRoleMirror
{
    private readonly FramePublisher _switcher;
    private readonly DevicePresenceMonitor _presence;
    private readonly object _lock = new();
    private List<RoleChannel> _channels = new();
    private RoleChannel? _active;
    private int _activeIndex;
    private volatile bool _previewsEnabled = true;
    private volatile bool _previewsVisible = true;
    private Func<string, bool>? _vcamRunning;
    private AppConfig _config = new();

    public CameraEngine(FramePublisher switcher, DevicePresenceMonitor presence)
    {
        _switcher = switcher;
        _presence = presence;
    }

    /// <summary>Roles in configuration order (for display only; identity is the role).</summary>
    public IReadOnlyList<RoleChannel> Channels
    {
        get { lock (_lock) return _channels.ToList(); }
    }

    /// <summary>Currently open physical sources (roles that are connected).</summary>
    public IReadOnlyList<CameraSource> Sources =>
        Channels.Select(c => c.Source).Where(s => s != null).Cast<CameraSource>().ToList();

    /// <summary>Raised when any role's status changes (pool thread).</summary>
    public event Action<RoleChannel>? RoleStatusChanged;

    public int ActiveIndex
    {
        get => Volatile.Read(ref _activeIndex);
        set
        {
            lock (_lock)
            {
                int i = Math.Clamp(value, 0, Math.Max(0, _channels.Count - 1));
                _activeIndex = i;
                _active = i < _channels.Count ? _channels[i] : null;
            }
        }
    }

    public RoleChannel? ActiveChannel => Volatile.Read(ref _active);

    public bool PreviewsEnabled
    {
        get => _previewsEnabled;
        set { _previewsEnabled = value; PushPreviewState(); }
    }

    public bool PreviewsVisible
    {
        get => _previewsVisible;
        set
        {
            if (_previewsVisible == value)
                return;
            _previewsVisible = value;
            PushPreviewState();
            AppLog.Write($"Previews: window {(value ? "visible" : "hidden")}, conversion {(value && _previewsEnabled ? "on" : "off")}");
        }
    }

    private void PushPreviewState()
    {
        bool on = _previewsEnabled && _previewsVisible;
        foreach (var c in Channels)
            c.PreviewEnabled = on;
    }

    /// <summary>Lists physical video capture devices (virtual cameras excluded), by device ID.</summary>
    public static async Task<List<PhysicalCamera>> EnumeratePhysicalCamerasAsync()
    {
        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        return devices
            .Select(d => new PhysicalCamera(d.Id, d.Name ?? "", DevicePresenceMonitor.LooksVirtual(d.Id, d.Name ?? "")))
            .Where(c => !c.IsVirtual)
            .ToList();
    }

    /// <summary>
    /// Starts every role. Each role opens (or waits for) its own device on its
    /// own schedule; this returns once the channels exist, not once cameras open.
    /// </summary>
    public Task StartAsync(AppConfig config, Func<string, bool> vcamRunning)
    {
        _vcamRunning = vcamRunning;
        _config = config;
        _switcher.Start();
        long startupUntil = Environment.TickCount64 + config.Reconnect.StartupWaitMs;
        var dupes = CameraRoles.DuplicatedRoles(config.Cameras);

        var list = new List<RoleChannel>();
        foreach (var c in config.Cameras)
            list.Add(CreateChannel(c, config, dupes, startupUntil));

        lock (_lock)
            _channels = list;
        ActiveIndex = config.ActiveIndex;
        foreach (var ch in list)
            ch.Start();
        AppLog.Write($"Engine: {list.Count} role(s) started; waiting up to {config.Reconnect.StartupWaitMs} ms for devices to enumerate");
        return Task.CompletedTask;
    }

    private RoleChannel CreateChannel(CameraConfig c, AppConfig config, HashSet<string> dupes, long startupUntil)
    {
        var ch = new RoleChannel(c, config, _presence, dupes.Contains(c.Role), this)
        {
            StartupUntilTicks = startupUntil,
            PreviewEnabled = _previewsEnabled && _previewsVisible,
        };
        string key = c.VirtualCameraId;
        ch.VirtualCameraRunning = () => _vcamRunning?.Invoke(key) ?? false;
        ch.StatusChanged += OnChannelStatus;
        return ch;
    }

    private void OnChannelStatus(RoleChannel ch)
    {
        try { RoleStatusChanged?.Invoke(ch); } catch { }
    }

    /// <summary>True when a role must be rebuilt (anything that affects capture or identity).</summary>
    private static bool NeedsRebuild(CameraConfig a, CameraConfig b) =>
        !string.Equals(a.DeviceId, b.DeviceId, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(a.Role, b.Role, StringComparison.Ordinal) ||
        !string.Equals(a.VirtualCameraName, b.VirtualCameraName, StringComparison.Ordinal) ||
        a.CaptureWidth != b.CaptureWidth || a.CaptureHeight != b.CaptureHeight || a.CaptureFps != b.CaptureFps ||
        !string.Equals(a.CaptureFormat, b.CaptureFormat, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(a.ScaleMode, b.ScaleMode, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Applies a new role list without touching unchanged roles. The caller
    /// rebuilds the whole engine instead when the output format changed.
    /// </summary>
    public async Task ApplyRolesAsync(AppConfig config)
    {
        var old = Channels.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
        var dupesBefore = CameraRoles.DuplicatedRoles(_config.Cameras);
        var dupes = CameraRoles.DuplicatedRoles(config.Cameras);
        _config = config;

        var next = new List<RoleChannel>();
        var toDispose = new List<RoleChannel>();
        var toStart = new List<RoleChannel>();
        foreach (var c in config.Cameras)
        {
            if (old.Remove(c.VirtualCameraId, out var existing) &&
                !NeedsRebuild(existing.Config, c) &&
                dupesBefore.Contains(existing.Role) == dupes.Contains(c.Role) &&
                existing.Config.PhysicalFriendlyName == c.PhysicalFriendlyName)
            {
                existing.SetDelay(c.DelayMs, config.CommonDelayMs);
                next.Add(existing);
                continue;
            }
            if (existing != null)
            {
                AppLog.Write(c.Role, "role settings changed -> restarting this role only");
                toDispose.Add(existing);
            }
            var ch = CreateChannel(c, config, dupes, 0);
            next.Add(ch);
            toStart.Add(ch);
        }
        foreach (var removed in old.Values)
        {
            AppLog.Write(removed.Role, "role removed from the configuration");
            toDispose.Add(removed);
        }

        // Release devices first so a camera moved between roles can be reopened.
        await Task.WhenAll(toDispose.Select(d => d.DisposeAsync().AsTask())).ConfigureAwait(false);

        lock (_lock)
            _channels = next;
        ActiveIndex = config.ActiveIndex;
        foreach (var ch in toStart)
            ch.Start();
    }

    public void SetDelays(AppConfig config, IReadOnlyList<int>? perRole = null, int? common = null)
    {
        var channels = Channels;
        for (int i = 0; i < channels.Count; i++)
        {
            int ms = perRole != null && i < perRole.Count ? perRole[i] : channels[i].Config.DelayMs;
            channels[i].SetDelay(ms, common ?? config.CommonDelayMs);
        }
    }

    public IReadOnlyList<RoleSnapshot> Snapshot() => Channels.Select(c => c.Snapshot()).ToList();

    // IRoleMirror: the switcher carries whichever role is active.
    bool IRoleMirror.Wants(RoleChannel channel) =>
        ReferenceEquals(channel, Volatile.Read(ref _active)) && _switcher.ClientCount > 0;

    void IRoleMirror.Publish(RoleChannel channel, byte[] nv12, WsFrameInfo info)
    {
        if (ReferenceEquals(channel, Volatile.Read(ref _active)))
            _switcher.PublishFrame(nv12, info);
    }

    public async ValueTask DisposeAsync()
    {
        List<RoleChannel> channels;
        lock (_lock)
        {
            channels = _channels;
            _channels = new();
            _active = null;
        }
        await Task.WhenAll(channels.Select(c => c.DisposeAsync().AsTask())).ConfigureAwait(false);
        _switcher.Dispose();
    }
}
