using System.Collections.Concurrent;
using WebcamSwitcher.Core;

namespace WebcamSwitcher.App;

/// <summary>
/// Owns the virtual cameras, the per-role capture engine, device presence,
/// hotkeys and the performance log, and applies config changes in-process.
///
/// Everything is keyed by Camera Role (its persistent virtual camera ID): the
/// index-based members exist for the UI and are only a view of the current
/// role order. A role change touches only that role's virtual camera and
/// capture; the output format is the one global setting that rebuilds all.
/// </summary>
public sealed class PipelineController : IAsyncDisposable
{
    private AppConfig _config = new();
    private VirtualCameraService _vcam = new();
    private readonly DevicePresenceMonitor _presence = new();
    private CameraEngine? _engine;
    private HotkeyService? _hotkeys;
    private PerformanceMonitor? _perf;

    // Per-key virtual camera gates: a native create/stop takes tens to hundreds
    // of ms and must not overlap itself; different roles proceed independently.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _vcamGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _vcamBusy = new(StringComparer.OrdinalIgnoreCase);
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;
    private readonly SemaphoreSlim _applyGate = new(1, 1);

    public AppConfig Config => _config;

    /// <summary>True while at least one virtual camera (any role, or the switcher) is running.</summary>
    public bool VirtualCameraActive => _vcam.IsRunning;
    public bool CaptureActive => _engine != null;
    public bool PreviewsActive => _config.PreviewsEnabled;

    private volatile bool _previewsVisible = true;
    public CameraEngine? Engine => _engine;
    public DevicePresenceMonitor Presence => _presence;
    public IReadOnlyList<RoleChannel> Channels => _engine?.Channels ?? Array.Empty<RoleChannel>();
    public ProcessStats? ProcessStats => _perf?.Latest;

    public int ActiveIndex
    {
        get => _engine?.ActiveIndex ?? _config.ActiveIndex;
        set
        {
            int index = Math.Clamp(value, 0, Math.Max(0, _config.Cameras.Count - 1));
            if (_engine != null)
                _engine.ActiveIndex = index;
            PersistActiveIndex(index);
        }
    }

    private void PersistActiveIndex(int index)
    {
        if (_config.ActiveIndex == index)
            return;
        _config.ActiveIndex = index;
        ConfigService.Save(_config);
    }

    /// <summary>
    /// Test hook: adopts a config and a service with a fake native layer, without
    /// starting capture, hotkeys or touching the on-disk format file.
    /// </summary>
    internal void InitializeForTest(AppConfig config, VirtualCameraService vcam)
    {
        _config = config;
        _config.Normalize();
        _vcam = vcam;
    }

    /// <summary>Raised when the role/channel set changed (UI should rebind tiles and tray).</summary>
    public event Action? SourcesChanged;
    public event Action? VirtualCameraStateChanged;
    public event Action? CaptureStateChanged;
    public event Action? PreviewsStateChanged;
    /// <summary>Raised on the UI thread after every virtual-camera state change.</summary>
    public event Action? VcamStateChanged;
    /// <summary>Raised on the UI thread when any role's status changes.</summary>
    public event Action? RoleStatusChanged;

    /// <summary>Number of Camera Roles (the switcher is extra).</summary>
    public int CameraCount => _config.Cameras.Count;

    private string KeyAt(int index) => VirtualCameraService.KeyOf(_config.Cameras[index]);

    public IReadOnlyList<RoleSnapshot> Snapshot()
    {
        var engine = _engine;
        if (engine != null)
            return engine.Snapshot();
        // Capture off: still report every role (from config) so the UI lists them.
        return _config.Cameras.Select(c => new RoleSnapshot
        {
            Role = c.Role,
            VirtualCameraName = c.VirtualCameraName,
            VirtualCameraId = c.VirtualCameraId,
            DeviceId = c.DeviceId,
            DeviceIdTail = CameraRoles.DeviceIdTail(c.DeviceId),
            PhysicalFriendlyName = c.DisplayDeviceName,
            CameraStatus = c.IsAssigned ? RoleStatus.Error : RoleStatus.Unassigned,
            Status = c.IsAssigned ? RoleStatus.Error : RoleStatus.Unassigned,
            Detail = c.IsAssigned ? "capture is switched off" : "no physical camera registered",
            OutputWidth = _config.Width,
            OutputHeight = _config.Height,
            OutputFps = _config.Fps,
            VirtualCameraEnabled = c.VirtualCameraEnabled,
            VirtualCameraRunning = _vcam.IsKeyRunning(c.VirtualCameraId),
        }).ToList();
    }

    public bool IsVcamRunning(int cameraIndex) =>
        cameraIndex >= 0 && cameraIndex < CameraCount && _vcam.IsKeyRunning(KeyAt(cameraIndex));

    public bool IsSwitcherVcamRunning => _vcam.IsKeyRunning(VirtualCameraService.SwitcherKey);

    public bool IsVcamBusy(int cameraIndex) =>
        cameraIndex >= 0 && cameraIndex < CameraCount && BusyCount(KeyAt(cameraIndex)) > 0;

    public bool IsSwitcherVcamBusy => BusyCount(VirtualCameraService.SwitcherKey) > 0;

    /// <summary>Starts or stops one role's virtual camera off the UI thread. Never throws.</summary>
    public Task<bool> SetVcamRunningAsync(int cameraIndex, bool running)
    {
        if (cameraIndex < 0 || cameraIndex >= CameraCount)
        {
            AppLog.Write($"SetVcamRunningAsync({cameraIndex}): no such role");
            return Task.FromResult(false);
        }
        return SetKeyRunningAsync(KeyAt(cameraIndex), running, persist: true);
    }

    public Task<bool> SetSwitcherVcamRunningAsync(bool running) =>
        SetKeyRunningAsync(VirtualCameraService.SwitcherKey, running, persist: true);

    /// <summary>
    /// Turning on starts exactly the roles marked enabled (and the switcher if
    /// enabled); turning off stops everything. Never rewrites per-role flags.
    /// </summary>
    public async Task<bool> SetAllVcamsRunningAsync(bool running)
    {
        var tasks = new List<Task<bool>>();
        foreach (var c in _config.Cameras.ToList())
        {
            if (running && !c.VirtualCameraEnabled)
                continue;
            tasks.Add(SetKeyRunningAsync(VirtualCameraService.KeyOf(c), running, persist: false));
        }
        if (!running || _config.SwitcherVirtualCameraEnabled)
            tasks.Add(SetKeyRunningAsync(VirtualCameraService.SwitcherKey, running, persist: false));

        if (tasks.Count == 0)
            return true;
        bool[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
        bool ok = results.All(r => r);
        AppLog.Write($"SetAllVcamsRunningAsync({running}): {(ok ? "ok" : "partial failure")} ({results.Count(r => r)}/{results.Length})");
        return ok;
    }

    private async Task<bool> SetKeyRunningAsync(string key, bool running, bool persist)
    {
        bool ok = false;
        EnterBusy(key);
        RaiseVcamStateChanged();
        try
        {
            var gate = _vcamGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _vcam.Prepare(_config.Cameras);
                if (running)
                    ok = await Task.Run(() => _vcam.StartKey(key)).ConfigureAwait(false);
                else
                {
                    await Task.Run(() => _vcam.StopKey(key)).ConfigureAwait(false);
                    ok = !_vcam.IsKeyRunning(key);
                }
            }
            finally
            {
                gate.Release();
            }

            if (ok && persist)
                PersistVcamChoice(key, running);
        }
        catch (Exception ex)
        {
            AppLog.Write($"Vcam {key} {(running ? "start" : "stop")} threw {ex.GetType().Name}: {ex.Message}");
            ok = false;
        }
        finally
        {
            ExitBusy(key);
            RaiseVcamStateChanged();
            Raise(VirtualCameraStateChanged);
        }
        return ok;
    }

    private void PersistVcamChoice(string key, bool running)
    {
        if (key == VirtualCameraService.SwitcherKey)
        {
            if (_config.SwitcherVirtualCameraEnabled == running)
                return;
            _config.SwitcherVirtualCameraEnabled = running;
        }
        else
        {
            var cam = _config.Cameras.FirstOrDefault(c => string.Equals(c.VirtualCameraId, key, StringComparison.OrdinalIgnoreCase));
            if (cam == null || cam.VirtualCameraEnabled == running)
                return;
            cam.VirtualCameraEnabled = running;
            AppLog.Write(cam.Role, $"virtual camera switched {(running ? "ON" : "OFF")} by the user");
        }
        ConfigService.Save(_config);
    }

    /// <summary>Starts the enabled role virtual cameras (and the switcher if enabled). Blocking.</summary>
    private void StartEnabledVcams()
    {
        _vcam.Prepare(_config.Cameras);
        foreach (var c in _config.Cameras)
            if (c.VirtualCameraEnabled)
                _vcam.StartKey(VirtualCameraService.KeyOf(c));
        if (_config.SwitcherVirtualCameraEnabled)
            _vcam.StartKey(VirtualCameraService.SwitcherKey);
        AppLog.Write($"Vcam: enabled virtual cameras started ({CountRunning()} running)");
    }

    private int CountRunning() =>
        _config.Cameras.Count(c => _vcam.IsKeyRunning(c.VirtualCameraId)) + (IsSwitcherVcamRunning ? 1 : 0);

    private int BusyCount(string key) => _vcamBusy.TryGetValue(key, out int n) ? n : 0;
    private void EnterBusy(string key) => _vcamBusy.AddOrUpdate(key, 1, (_, n) => n + 1);
    private void ExitBusy(string key) => _vcamBusy.AddOrUpdate(key, 0, (_, n) => Math.Max(0, n - 1));

    private void RaiseVcamStateChanged() => Raise(VcamStateChanged);

    private void Raise(Action? handler)
    {
        if (handler == null)
            return;
        var context = _uiContext;
        if (context == null || ReferenceEquals(SynchronizationContext.Current, context))
        {
            try { handler(); } catch (Exception ex) { AppLog.Write($"UI event handler threw {ex.GetType().Name}: {ex.Message}"); }
            return;
        }
        context.Post(_ =>
        {
            try { handler(); } catch (Exception ex) { AppLog.Write($"UI event handler threw {ex.GetType().Name}: {ex.Message}"); }
        }, null);
    }

    // ------------------------------------------------------------- lifecycle

    public async Task StartAsync(AppConfig config)
    {
        _config = config;
        _config.Normalize();
        foreach (var issue in CameraRoles.Validate(_config.Cameras))
            AppLog.Write(issue.Role, $"CONFIG PROBLEM {issue.Field}: {issue.Message}");

        SourceFormatFile.Write(_config.Width, _config.Height, _config.Fps);
        ApplyStartWithWindows(_config.StartWithWindows);
        RoleChannel.LatencyProbeEnabled = _config.LatencyProbe;
        if (_config.LatencyProbe)
            AppLog.Write("App", "LATENCY PROBE ENABLED (test mode): frames carry a timestamp barcode");

        _presence.Start();
        await _presence.WaitReadyAsync(3000);
        LogDeviceMatch();

        // Virtual cameras first: consumers (TouchDesigner, browsers) can open
        // CAM-xx at once and see the diagnostic picture until the camera opens.
        if (_config.AutoRegisterVirtualCameras)
            await Task.Run(StartEnabledVcams);
        else
        {
            _vcam.Prepare(_config.Cameras);
            AppLog.Write("StartAsync: automatic virtual camera registration is off");
        }

        if (_config.CaptureEnabled)
            await RebuildPipelineAsync();
        else
            AppLog.Write("StartAsync: capture auto-start is off - cameras not opened");

        RebuildHotkeys();
        _perf = new PerformanceMonitor(Snapshot);
        _perf.Start(_config.PerformanceLogIntervalSeconds);

        SourcesChanged?.Invoke();
        CaptureStateChanged?.Invoke();
        VirtualCameraStateChanged?.Invoke();
        VcamStateChanged?.Invoke();
    }

    /// <summary>Logs, per role, whether its registered device ID is present right now.</summary>
    private void LogDeviceMatch()
    {
        var present = _presence.Snapshot().Where(p => !p.IsVirtual).ToList();
        foreach (var p in present)
        {
            var owner = _config.Cameras.FirstOrDefault(c => string.Equals(c.DeviceId, p.DeviceId, StringComparison.OrdinalIgnoreCase));
            AppLog.Write(owner?.Role ?? "Devices", $"detected '{p.FriendlyName}' {CameraRoles.DeviceIdTail(p.DeviceId)} -> " +
                                                    (owner != null ? $"matches {owner.Role}" : "not registered to any role"));
        }
        foreach (var c in _config.Cameras.Where(c => c.IsAssigned))
            if (!present.Any(p => string.Equals(p.DeviceId, c.DeviceId, StringComparison.OrdinalIgnoreCase)))
                AppLog.Write(c.Role, $"registered device {CameraRoles.DeviceIdTail(c.DeviceId)} is NOT present at startup (no other camera will be used)");
    }

    /// <summary>
    /// Applies a validated config. Role changes are applied per role: a renamed
    /// role's old virtual camera is stopped and unregistered before the new name
    /// is registered; untouched roles keep running. Throws
    /// <see cref="InvalidOperationException"/> when the role list is invalid.
    /// </summary>
    public async Task ApplyAsync(AppConfig next)
    {
        next.Normalize();
        var issues = CameraRoles.Validate(next.Cameras);
        if (issues.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, issues.Select(i => i.Message)));

        await _applyGate.WaitAsync();
        try
        {
            AppLog.Write($"ApplyAsync begin format={next.Width}x{next.Height}@{next.Fps} roles={next.Cameras.Count}");
            LogRoleChanges(_config, next);

            bool formatChanged = next.Width != _config.Width || next.Height != _config.Height || next.Fps != _config.Fps;
            bool startupChanged = next.StartWithWindows != _config.StartWithWindows;
            bool perfChanged = next.PerformanceLogIntervalSeconds != _config.PerformanceLogIntervalSeconds;
            _config = next;
            AppLog.Configure(_config.LogMaxFiles, _config.LogMaxFileMB);

            if (formatChanged)
                SourceFormatFile.Write(_config.Width, _config.Height, _config.Fps);

            // 1) stop + unregister virtual cameras whose role vanished or was renamed
            await Task.Run(() => _vcam.Prepare(_config.Cameras));

            // 2) capture: whole rebuild for a new format, else per role
            if (_engine != null)
            {
                if (formatChanged)
                    await RebuildPipelineAsync();
                else
                    await _engine.ApplyRolesAsync(_config);
            }

            // 3) register the (new) names; the format needs every vcam re-created
            await Task.Run(() =>
            {
                if (formatChanged)
                {
                    var running = _config.Cameras.Where(c => _vcam.IsKeyRunning(c.VirtualCameraId)).Select(c => c.VirtualCameraId).ToList();
                    bool sw = IsSwitcherVcamRunning;
                    _vcam.Stop();
                    foreach (var k in running)
                        _vcam.StartKey(k);
                    if (sw)
                        _vcam.StartKey(VirtualCameraService.SwitcherKey);
                }
                foreach (var c in _config.Cameras)
                {
                    if (c.VirtualCameraEnabled)
                        _vcam.StartKey(c.VirtualCameraId);
                    else
                        _vcam.StopKey(c.VirtualCameraId);
                }
                if (_config.SwitcherVirtualCameraEnabled)
                    _vcam.StartKey(VirtualCameraService.SwitcherKey);
                else
                    _vcam.StopKey(VirtualCameraService.SwitcherKey);
            });

            RebuildHotkeys();
            if (startupChanged)
                ApplyStartWithWindows(_config.StartWithWindows);
            if (perfChanged)
                _perf?.Start(_config.PerformanceLogIntervalSeconds);

            // 4) persist
            ConfigService.Save(_config);
            AppLog.Write("ApplyAsync end");
        }
        finally
        {
            _applyGate.Release();
        }

        SourcesChanged?.Invoke();
        RaiseVcamStateChanged();
        Raise(VirtualCameraStateChanged);
    }

    private static void LogRoleChanges(AppConfig before, AppConfig after)
    {
        var old = before.Cameras.ToDictionary(c => c.VirtualCameraId, StringComparer.OrdinalIgnoreCase);
        foreach (var c in after.Cameras)
        {
            if (!old.Remove(c.VirtualCameraId, out var o))
            {
                AppLog.Write(c.Role, $"ROLE ADDED: virtual camera '{c.VirtualCameraName}', device {CameraRoles.DeviceIdTail(c.DeviceId)}");
                continue;
            }
            if (o.Role != c.Role || o.VirtualCameraName != c.VirtualCameraName)
                AppLog.Write(c.Role, $"ROLE RENAMED: {o.Role}/'{o.VirtualCameraName}' -> {c.Role}/'{c.VirtualCameraName}'");
            if (!string.Equals(o.DeviceId, c.DeviceId, StringComparison.OrdinalIgnoreCase))
                AppLog.Write(c.Role, $"DEVICE CHANGED: {CameraRoles.DeviceIdTail(o.DeviceId)} -> {CameraRoles.DeviceIdTail(c.DeviceId)}");
        }
        foreach (var o in old.Values)
            AppLog.Write(o.Role, $"ROLE REMOVED: virtual camera '{o.VirtualCameraName}'");
    }

    /// <summary>Starts only the switcher virtual camera.</summary>
    public void StartVirtualCamera() => _ = SetSwitcherVcamRunningAsync(true);

    /// <summary>Stops only the switcher virtual camera.</summary>
    public void StopVirtualCamera() => _ = SetSwitcherVcamRunningAsync(false);

    public void SetPreviewsEnabled(bool enabled)
    {
        if (_config.PreviewsEnabled == enabled)
            return;
        _config.PreviewsEnabled = enabled;
        if (_engine != null)
            _engine.PreviewsEnabled = enabled;
        ConfigService.Save(_config);
        AppLog.Write($"Previews: {(enabled ? "on" : "off")}");
        PreviewsStateChanged?.Invoke();
    }

    public void SetPreviewsVisible(bool visible)
    {
        if (_previewsVisible == visible)
            return;
        _previewsVisible = visible;
        if (_engine != null)
            _engine.PreviewsVisible = visible;
    }

    public async Task StartCaptureAsync()
    {
        if (_engine != null)
            return;
        AppLog.Write("StartCapture");
        await RebuildPipelineAsync();
        SetCaptureEnabled(true);
        if (_config.AutoRegisterVirtualCameras)
            await Task.Run(StartEnabledVcams);
        RebuildHotkeys();
        SourcesChanged?.Invoke();
        CaptureStateChanged?.Invoke();
        VcamStateChanged?.Invoke();
    }

    private void SetCaptureEnabled(bool enabled)
    {
        if (_config.CaptureEnabled == enabled)
            return;
        _config.CaptureEnabled = enabled;
        ConfigService.Save(_config);
    }

    public async Task StopCaptureAsync()
    {
        if (_engine == null)
            return;
        AppLog.Write("StopCapture");
        var engine = _engine;
        _engine = null;
        await Task.Run(async () => await engine.DisposeAsync());
        SetCaptureEnabled(false);
        RebuildHotkeys();
        SourcesChanged?.Invoke();
        CaptureStateChanged?.Invoke();
    }

    private Task RebuildPipelineAsync() => Task.Run(RebuildPipelineCoreAsync);

    private async Task RebuildPipelineCoreAsync()
    {
        if (_engine != null)
        {
            AppLog.Write("Rebuild: dispose engine begin");
            await _engine.DisposeAsync();
            AppLog.Write("Rebuild: dispose engine end");
            _engine = null;
        }

        var switcher = new FramePublisher(Protocol.PipeName, _config.Width, _config.Height, _config.Fps);
        var engine = new CameraEngine(switcher, _presence)
        {
            PreviewsEnabled = _config.PreviewsEnabled,
            PreviewsVisible = _previewsVisible,
        };
        engine.RoleStatusChanged += _ => Raise(RoleStatusChanged);
        await engine.StartAsync(_config, key => _vcam.IsKeyRunning(key));
        _engine = engine;
    }

    private void RebuildHotkeys()
    {
        void Do()
        {
            _hotkeys?.Dispose();
            _hotkeys = null;
            if (_engine != null)
            {
                _hotkeys = new HotkeyService(_config, this);
                _hotkeys.Register();
            }
        }
        // HwndSource is UI-affine.
        var context = _uiContext;
        if (context == null || ReferenceEquals(SynchronizationContext.Current, context))
            Do();
        else
            context.Send(_ => Do(), null);
    }

    /// <summary>
    /// Pushes delays into the running engine at once, without saving and without
    /// rebuilding (the settings dialog calls this while typing).
    /// </summary>
    public void ApplyDelaysLive(int commonDelayMs, IReadOnlyList<int> slotDelays) =>
        _engine?.SetDelays(_config, slotDelays, commonDelayMs);

    private static void ApplyStartWithWindows(bool enabled)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null)
                return;
            if (enabled)
                key.SetValue("WebcamSwitcher", $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue("WebcamSwitcher", throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            AppLog.Write($"StartWithWindows: registry update failed {ex.Message}");
        }
    }

    public void DisposeHotkeys()
    {
        _hotkeys?.Dispose();
        _hotkeys = null;
    }

    public async ValueTask DisposeAsync()
    {
        _hotkeys?.Dispose();
        _hotkeys = null;
        _perf?.LogNow();
        _perf?.Dispose();
        if (_engine != null)
        {
            await _engine.DisposeAsync();
            _engine = null;
        }
        _vcam.Dispose();
        _presence.Dispose();
    }
}
