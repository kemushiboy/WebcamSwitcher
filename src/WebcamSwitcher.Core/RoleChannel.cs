using System.Diagnostics;

namespace WebcamSwitcher.Core;

/// <summary>Operator-facing state of one Camera Role.</summary>
public enum RoleStatus
{
    Unassigned,
    Connecting,
    Connected,
    Missing,
    Reconnecting,
    NoSignal,
    FormatError,
    VirtualCameraStopped,
    Duplicated,
    Error,
}

public static class RoleStatusText
{
    public static string Label(RoleStatus s) => s switch
    {
        RoleStatus.Unassigned => "UNASSIGNED",
        RoleStatus.Connecting => "CONNECTING",
        RoleStatus.Connected => "CONNECTED",
        RoleStatus.Missing => "MISSING",
        RoleStatus.Reconnecting => "RECONNECTING",
        RoleStatus.NoSignal => "NO SIGNAL",
        RoleStatus.FormatError => "FORMAT ERROR",
        RoleStatus.VirtualCameraStopped => "VIRTUAL CAMERA STOPPED",
        RoleStatus.Duplicated => "DUPLICATED",
        _ => "ERROR",
    };
}

/// <summary>Point-in-time view of a role, for the UI, the log and tests.</summary>
public sealed record RoleSnapshot
{
    public string Role { get; init; } = "";
    public string VirtualCameraName { get; init; } = "";
    public string VirtualCameraId { get; init; } = "";
    public string? DeviceId { get; init; }
    public string DeviceIdTail { get; init; } = "";
    public string PhysicalFriendlyName { get; init; } = "";
    public RoleStatus CameraStatus { get; init; }
    /// <summary>Camera status combined with the virtual camera state.</summary>
    public RoleStatus Status { get; init; }
    public string Detail { get; init; } = "";
    public CaptureFailure Failure { get; init; }
    public string Warning { get; init; } = "";
    public int InputWidth { get; init; }
    public int InputHeight { get; init; }
    public string InputPixelFormat { get; init; } = "";
    public double InputNominalFps { get; init; }
    public double InputFps { get; init; }
    public int OutputWidth { get; init; }
    public int OutputHeight { get; init; }
    public int OutputFps { get; init; }
    public double OutputFpsMeasured { get; init; }
    public long FramesReceived { get; init; }
    public long DroppedFrames { get; init; }
    public DateTime LastFrameUtc { get; init; }
    public long Reconnects { get; init; }
    public int ReconnectAttempts { get; init; }
    public bool VirtualCameraEnabled { get; init; }
    public bool VirtualCameraRunning { get; init; }
    /// <summary>A consumer (through the Frame Server) is reading this role's pipe.</summary>
    public bool VirtualCameraInUse { get; init; }
    public double ProcessMicros { get; init; }
    public string Pipeline { get; init; } = "";
}

/// <summary>Receives a role's frames for the switcher feed (only the active role).</summary>
public interface IRoleMirror
{
    /// <summary>True when this role's frames should go to the switcher right now.</summary>
    bool Wants(RoleChannel channel);
    void Publish(RoleChannel channel, byte[] nv12, WsFrameInfo info);
}

/// <summary>
/// One Camera Role's pipeline: exactly one physical device ID -> optional
/// delay -> this role's own pipe -> this role's virtual camera.
///
/// A supervisor tick (every 500 ms) owns the camera's lifecycle: it opens the
/// configured device ID when it is present, notices removal, retries with the
/// configured interval/count, and whenever no live picture is flowing it feeds
/// the virtual camera a diagnostic picture instead (so the virtual camera stays
/// registered and consumers see why). It never opens any other device. Roles
/// are fully independent: one role failing, reconnecting or being rebuilt never
/// touches another.
/// </summary>
public sealed class RoleChannel : IAsyncDisposable
{
    private const int TickMs = 500;
    private const long DiagnosticEveryMs = 500;
    private const long BandwidthWindowMs = 10_000;

    private readonly CameraConfig _cfg;
    private readonly int _w, _h, _fps;
    private readonly ReconnectConfig _reconnect;
    private readonly bool _preferSmooth;
    private readonly DevicePresenceMonitor _presence;
    private readonly IRoleMirror? _mirror;
    private readonly FramePublisher _publisher;
    private readonly FrameDelayBuffer _delay = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly bool _duplicated;
    private DiagnosticFrame? _diag;
    private Timer? _timer;

    private CameraSource? _source;
    private volatile bool _disposed;
    private int _ticking;

    private RoleStatus _status;
    private string _detail = "";
    private string _warning = "";
    private CaptureFailure _failure;
    private bool _everConnected;
    private int _attempts;
    private long _reconnects;
    private long _nextAttemptTicks;
    private long _sourceStartedTicks;
    private long _lastDiagTicks;
    private long _lowFpsSinceTicks;
    private long _framesBeforeSource;   // frames of disposed sources, so counters are per role
    private long _droppedBeforeSource;
    private DateTime _lastFrameUtc = DateTime.MinValue;
    private ulong _sequence;
    private long _outWindowStart = Environment.TickCount64;
    private long _outWindowSent;
    private double _outFps;
    private int _delayMs;
    private int _commonDelayMs;
    private volatile bool _previewEnabled = true;

    public RoleChannel(CameraConfig config, AppConfig app, DevicePresenceMonitor presence, bool duplicated,
        IRoleMirror? mirror)
    {
        _cfg = config.Clone();
        _w = app.Width;
        _h = app.Height;
        _fps = app.Fps;
        _reconnect = app.Reconnect;
        _preferSmooth = app.PreferSmooth30Fps;
        _presence = presence;
        _duplicated = duplicated;
        _mirror = mirror;
        _delayMs = config.DelayMs;
        _commonDelayMs = app.CommonDelayMs;
        _publisher = new FramePublisher(Protocol.RolePipeName(config.VirtualCameraId), _w, _h, _fps);
        _status = duplicated ? RoleStatus.Duplicated : config.IsAssigned ? RoleStatus.Connecting : RoleStatus.Unassigned;
        ApplyDelay();
    }

    public string Role => _cfg.Role;
    public string Key => _cfg.VirtualCameraId;
    public CameraConfig Config => _cfg;
    public CameraSource? Source => _source;
    public FramePublisher Publisher => _publisher;
    public RoleStatus Status => _status;

    /// <summary>Before this time a not-yet-enumerated device reads CONNECTING, not MISSING.</summary>
    public long StartupUntilTicks { get; set; }

    /// <summary>Supplied by the controller: whether this role's virtual camera is registered.</summary>
    public Func<bool>? VirtualCameraRunning { get; set; }

    /// <summary>Raised with the latest 320x180 Bgra8 preview of whatever source is current.</summary>
    public event Action<byte[], int, int>? PreviewReady;

    /// <summary>Raised (pool thread) when the status changes.</summary>
    public event Action<RoleChannel>? StatusChanged;

    public bool PreviewEnabled
    {
        get => _previewEnabled;
        set
        {
            _previewEnabled = value;
            var s = _source;
            if (s != null)
                s.PreviewEnabled = value;
        }
    }

    /// <summary>Test/measurement hook: stamp the arrival time into each published frame.</summary>
    public static bool LatencyProbeEnabled { get; set; }

    public void Start()
    {
        _publisher.Start();
        _presence.Changed += OnPresenceChanged;
        _timer = new Timer(_ => OnTick(), null, 0, TickMs);
        AppLog.Write(Role, $"role started: virtual camera '{_cfg.VirtualCameraName}', device {(_cfg.IsAssigned ? CameraRoles.DeviceIdTail(_cfg.DeviceId) : "(unassigned)")}" +
                           (_duplicated ? " - DUPLICATED, will not open the camera" : ""));
    }

    // ------------------------------------------------------------------ delay

    public void SetDelay(int delayMs, int commonDelayMs)
    {
        Volatile.Write(ref _delayMs, Math.Clamp(delayMs, 0, AppConfig.MaxDelayMs));
        Volatile.Write(ref _commonDelayMs, Math.Clamp(commonDelayMs, 0, AppConfig.MaxDelayMs));
        ApplyDelay();
    }

    private void ApplyDelay()
    {
        int ms = Volatile.Read(ref _delayMs) + Volatile.Read(ref _commonDelayMs);
        int depth = ms <= 0 ? 0 : Math.Clamp((int)Math.Round(ms * (double)_fps / 1000.0), 0, FrameDelayBuffer.MaxDepth);
        if (_delay.Depth == depth)
            return;
        _delay.Configure(depth);
        AppLog.Write(Role, $"delay {ms} ms @ {_fps} fps -> {depth} frame(s)");
    }

    // ------------------------------------------------------------- supervisor

    private void OnPresenceChanged(string deviceId, bool present)
    {
        if (!string.Equals(deviceId, _cfg.DeviceId, StringComparison.OrdinalIgnoreCase))
            return;
        AppLog.Write(Role, present
            ? "assigned device ID appeared -> reconnecting now"
            : "assigned device ID disappeared");
        if (present)
        {
            _attempts = 0; // a fresh arrival restarts a retry budget that may be exhausted
            Volatile.Write(ref _nextAttemptTicks, 0);
        }
        OnTick();
    }

    private void OnTick()
    {
        if (_disposed || Interlocked.Exchange(ref _ticking, 1) == 1)
            return;
        _ = SuperviseAsync();
    }

    private async Task SuperviseAsync()
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_disposed)
                    await SuperviseCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            AppLog.Write(Role, $"supervisor threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    private async Task SuperviseCoreAsync()
    {
        long now = Environment.TickCount64;
        UpdateOutputFps(now);

        if (_duplicated)
        {
            SetStatus(RoleStatus.Duplicated, "duplicate role, name, ID or device - fix the registration");
            PublishDiagnostic(now);
            return;
        }
        if (!_cfg.IsAssigned)
        {
            SetStatus(RoleStatus.Unassigned, "no physical camera registered for this role");
            PublishDiagnostic(now);
            return;
        }
        if (SyntheticSource.IsSynthetic(_cfg.DeviceId))
        {
            _synthetic ??= StartSynthetic();
            _lastFrameUtc = _synthetic.LastFrameUtc;
            SetStatus(RoleStatus.Connected, "SYNTHETIC TEST PATTERN (load testing only)");
            return;
        }

        var src = _source;
        if (src != null)
        {
            bool removed = _presence.Ready && !_presence.IsPresent(_cfg.DeviceId);
            if (removed || (src.DeviceLost && src.StreamFailure == CaptureFailure.DeviceNotFound))
            {
                AppLog.Write(Role, $"physical camera DISCONNECTED (device removed); last frame {FormatTime(src.LastFrameUtc)}");
                await DisposeSourceAsync().ConfigureAwait(false);
                _failure = CaptureFailure.DeviceNotFound;
                SetStatus(RoleStatus.Missing, "physical camera disconnected - waiting for the same device ID");
                Volatile.Write(ref _nextAttemptTicks, now + _reconnect.RetryIntervalMs);
                PublishDiagnostic(now);
                return;
            }
            if (src.DeviceLost)
            {
                // Still enumerated, but Windows stopped the stream: not MISSING.
                var kind = src.StreamFailure;
                string detail = src.StreamFailureDetail;
                AppLog.Write(Role, $"capture FAILED while streaming ({kind}): {detail}; last frame {FormatTime(src.LastFrameUtc)}");
                await DisposeSourceAsync().ConfigureAwait(false);
                _failure = kind;
                if (kind == CaptureFailure.BandwidthSuspected)
                    StepDownForBandwidth(detail);
                SetStatus(RoleStatus.Error, kind switch
                {
                    CaptureFailure.BandwidthSuspected => $"USB BANDWIDTH INSUFFICIENT: {detail}",
                    CaptureFailure.InUse => $"camera taken by another application: {detail}",
                    _ => $"capture failed while streaming: {detail}",
                });
                Volatile.Write(ref _nextAttemptTicks, now + RetryDelayMs());
                PublishDiagnostic(now, force: true);
                return;
            }

            src.RefreshMeasuredFps();
            DateTime last = src.LastFrameUtc;
            if (last != DateTime.MinValue)
                _lastFrameUtc = last;

            if (src.FramesLifetime == 0)
            {
                long since = now - _sourceStartedTicks;
                if (since > _reconnect.NoSignalTimeoutMs + 3000)
                {
                    _failure = CaptureFailure.NoFrames;
                    SetStatus(RoleStatus.NoSignal, $"stream started but no frame in {since / 1000.0:0.0}s (USB bandwidth or driver suspected)");
                }
                else
                {
                    SetStatus(_everConnected ? RoleStatus.Reconnecting : RoleStatus.Connecting, "waiting for the first frame");
                }
                PublishDiagnostic(now);
                return;
            }

            double age = (DateTime.UtcNow - last).TotalMilliseconds;
            if (age > _reconnect.NoSignalTimeoutMs)
            {
                SetStatus(RoleStatus.NoSignal, $"no frame for {age / 1000.0:0.0}s (capture restart in progress)");
                PublishDiagnostic(now);
                return;
            }

            _failure = CaptureFailure.None;
            if (_pendingConnect)
            {
                // Counted only now that frames flow: an open that fails at once is no reconnect.
                _pendingConnect = false;
                if (_everConnected)
                    Interlocked.Increment(ref _reconnects);
                AppLog.Write(Role, _everConnected
                    ? $"RECONNECTED (reconnect #{Interlocked.Read(ref _reconnects)}) after {_attempts} attempt(s)"
                    : $"CONNECTED after {_attempts} attempt(s)");
                _everConnected = true;
                _attempts = 0;
            }
            SetStatus(RoleStatus.Connected, src.PipelineDescription + (_bandwidthStep > 0 ? $" [bandwidth fallback step {_bandwidthStep}]" : ""));
            CheckFrameRate(src, now);
            return;
        }

        // ---- no source: decide whether to try opening the configured ID ----
        if (!_presence.IsPresent(_cfg.DeviceId))
        {
            bool starting = !_presence.Ready || now < StartupUntilTicks;
            if (starting)
                SetStatus(RoleStatus.Connecting, "waiting for the registered device to enumerate");
            else
            {
                _failure = CaptureFailure.DeviceNotFound;
                SetStatus(RoleStatus.Missing, _everConnected
                    ? "physical camera disconnected - waiting for the same device ID"
                    : "registered device ID is not connected (unplugged, or moved to another USB port?)");
            }
            PublishDiagnostic(now);
            return;
        }

        if (now < Volatile.Read(ref _nextAttemptTicks))
        {
            PublishDiagnostic(now);
            return;
        }

        if (!_reconnect.RetryForever && _attempts >= _reconnect.RetryCount)
        {
            SetStatus(RoleStatus.Error, $"gave up after {_attempts} attempt(s); replug the camera or restart capture to retry");
            PublishDiagnostic(now);
            return;
        }

        _attempts++;
        SetStatus(_everConnected ? RoleStatus.Reconnecting : RoleStatus.Connecting, $"opening the camera (attempt {_attempts})");
        PublishDiagnostic(now, force: true);

        var source = new CameraSource(_cfg.DeviceId!, $"{Role} {_cfg.DisplayDeviceName}")
        {
            PreviewEnabled = _previewEnabled,
            PreferSmooth30Fps = _preferSmooth,
            CaptureFps = _fps,
            RequestedCaptureFps = _cfg.CaptureFps ?? 0,
            PreferredFormat = _formatOverride ?? (string.IsNullOrWhiteSpace(_cfg.CaptureFormat) ? "Auto" : _cfg.CaptureFormat),
            ScaleMode = _cfg.ScaleMode,
        };
        source.FrameReady += OnFrame;
        source.PreviewReady += OnPreview;

        int capW = _sizeOverride?.W ?? _cfg.CaptureWidth ?? 0;
        int capH = _sizeOverride?.H ?? _cfg.CaptureHeight ?? 0;
        bool ok;
        try
        {
            ok = await source.StartAsync(_w, _h, capW, capH).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ok = false;
            AppLog.Write(Role, $"open threw {ex.GetType().Name}: {ex.Message}");
        }

        if (_disposed)
        {
            await DisposeQuietly(source).ConfigureAwait(false);
            return;
        }

        if (ok)
        {
            _source = source;
            _sourceStartedTicks = Environment.TickCount64;
            _pendingConnect = true;
            AppLog.Write(Role, $"physical camera OPENED (attempt {_attempts}): " +
                               $"input {source.InputWidth}x{source.InputHeight} {source.InputSubtype} @ {source.InputNominalFps:0.#} fps, " +
                               $"output {_w}x{_h} NV12 @ {_fps} fps, {source.PipelineDescription}");
            return;
        }

        _failure = source.LastFailure;
        string why = source.LastFailureDetail;
        await DisposeQuietly(source).ConfigureAwait(false);
        if (_failure == CaptureFailure.BandwidthSuspected)
            StepDownForBandwidth(why);
        Volatile.Write(ref _nextAttemptTicks, Environment.TickCount64 + RetryDelayMs());

        var (status, text) = _failure switch
        {
            CaptureFailure.DeviceNotFound => (RoleStatus.Missing, $"device not found: {why}"),
            CaptureFailure.FormatFailed => (RoleStatus.FormatError, $"format negotiation failed: {why}"),
            CaptureFailure.InUse => (RoleStatus.Error, $"camera is in use by another application: {why}"),
            CaptureFailure.BandwidthSuspected => (RoleStatus.Error, $"USB bandwidth suspected: {why}"),
            CaptureFailure.NoFrames => (RoleStatus.NoSignal, $"no frames: {why}"),
            _ => (RoleStatus.Error, $"camera could not be opened: {why}"),
        };
        SetStatus(status, text);
        AppLog.Write(Role, $"open FAILED ({_failure}) attempt {_attempts}: {why}; next attempt in {RetryDelayMs()} ms");
        PublishDiagnostic(Environment.TickCount64, force: true);
    }

    /// <summary>
    /// The configured interval, except that a camera the USB bus still cannot
    /// carry after every fallback step is retried at most every 15 s, so its
    /// open/close attempts do not keep disturbing the cameras sharing the bus.
    /// </summary>
    private int RetryDelayMs() =>
        _failure == CaptureFailure.BandwidthSuspected && _bandwidthStep >= 2
            ? Math.Max(_reconnect.RetryIntervalMs, 15000)
            : _reconnect.RetryIntervalMs;

    private bool _pendingConnect;
    private int _bandwidthStep;
    private string? _formatOverride;
    private (int W, int H)? _sizeOverride;

    /// <summary>
    /// After Windows reports the USB bus cannot carry this camera's stream, the
    /// next attempts on the SAME device ask for less: first its compressed
    /// (MJPEG) mode when the role is on Auto, then 640x480 (letterboxed). An
    /// explicit Nv12/Mjpg choice is respected and only the size is reduced.
    /// Lasts until the role is restarted; logged so it can be pinned in Settings.
    /// </summary>
    private void StepDownForBandwidth(string detail)
    {
        bool auto = string.IsNullOrWhiteSpace(_cfg.CaptureFormat) || _cfg.CaptureFormat.Equals("Auto", StringComparison.OrdinalIgnoreCase);
        if (_bandwidthStep == 0 && auto)
        {
            _bandwidthStep = 1;
            _formatOverride = "Mjpg";
            AppLog.Write(Role, "bandwidth fallback step 1: next attempt uses the camera's MJPEG mode (if it has one)");
        }
        else if (_bandwidthStep <= 1)
        {
            _bandwidthStep = 2;
            _formatOverride = auto ? "Auto" : _formatOverride;
            _sizeOverride = (640, 480);
            AppLog.Write(Role, "bandwidth fallback step 2: next attempt uses 640x480 (letterboxed into the output)");
        }
    }

    /// <summary>
    /// Flags a connected camera that delivers far fewer frames than its mode
    /// promises - usually several cameras sharing one USB 2.0 controller - and
    /// a mode that cannot reach the output rate at all.
    /// </summary>
    private void CheckFrameRate(CameraSource src, long now)
    {
        double measured = src.MeasuredFps;
        double nominal = src.InputNominalFps;
        string warning = "";
        if (nominal > 0 && nominal < _fps * 0.95)
            warning = $"camera mode only offers {nominal:0.#} fps at {src.InputWidth}x{src.InputHeight}";

        if (measured > 0 && nominal > 0 && measured < nominal * 0.6)
        {
            if (_lowFpsSinceTicks == 0)
                _lowFpsSinceTicks = now;
            else if (now - _lowFpsSinceTicks > BandwidthWindowMs)
                warning = $"LOW FPS {measured:0.#}/{nominal:0.#} - USB bandwidth (or long exposure in low light) suspected";
        }
        else
        {
            _lowFpsSinceTicks = 0;
        }

        if (warning != _warning)
        {
            if (warning.Length > 0)
                AppLog.Write(Role, "WARNING " + warning);
            else if (_warning.Length > 0)
                AppLog.Write(Role, "frame rate back to normal");
            _warning = warning;
        }
    }

    private void SetStatus(RoleStatus status, string detail)
    {
        bool changed = status != _status;
        _status = status;
        _detail = detail;
        if (status != RoleStatus.Connected)
            _warning = "";
        if (changed)
        {
            AppLog.Write(Role, $"state -> {RoleStatusText.Label(status)}: {detail}");
            try { StatusChanged?.Invoke(this); } catch { }
        }
    }

    // ------------------------------------------------------------ frame path

    private void OnFrame(byte[] nv12)
    {
        var src = _source;
        if (LatencyProbeEnabled)
            LatencyProbe.Stamp(nv12, _w, _h, src?.LastArrivalTimestamp ?? Stopwatch.GetTimestamp());

        // Delay stage: while the ring is filling the newest frame goes out.
        if (_delay.TryPush(nv12, out byte[]? delayed) && delayed != null)
            nv12 = delayed;

        var info = MakeInfo(nv12.Length);
        if (_publisher.ClientCount > 0)
        {
            _publisher.PublishFrame(nv12, info);
            Interlocked.Increment(ref _outWindowSent);
        }
        if (_mirror != null && _mirror.Wants(this))
            _mirror.Publish(this, nv12, info);
    }

    private SyntheticSource? _synthetic;

    private SyntheticSource StartSynthetic()
    {
        var s = new SyntheticSource(_w, _h, _fps, _cfg.Role);
        s.FrameReady += OnFrame;
        s.Start();
        AppLog.Write(Role, $"SYNTHETIC test pattern source started ({_w}x{_h}@{_fps}) - not a physical camera");
        return s;
    }

    private void OnPreview(byte[] bgra, int w, int h)
    {
        try { PreviewReady?.Invoke(bgra, w, h); } catch { }
    }

    private WsFrameInfo MakeInfo(int payload) => new()
    {
        Sequence = Interlocked.Increment(ref _sequence),
        TimestampQpc = 0,
        Width = (uint)_w,
        Height = (uint)_h,
        Stride = (uint)_w,
        Format = Protocol.FormatNv12,
        PayloadBytes = (uint)payload,
        Flags = Protocol.FlagNone
    };

    /// <summary>
    /// Feeds the diagnostic picture while no live picture flows. Only rendered
    /// when someone reads the pipe (or this role feeds the switcher), and the
    /// text is re-rendered only when it changes.
    /// </summary>
    private void PublishDiagnostic(long now, bool force = false)
    {
        if (!force && now - _lastDiagTicks < DiagnosticEveryMs)
            return;
        _lastDiagTicks = now;

        bool reader = _publisher.ClientCount > 0;
        bool mirror = _mirror != null && _mirror.Wants(this);
        if (!reader && !mirror)
            return;

        _diag ??= new DiagnosticFrame(_w, _h);
        var lines = BuildDiagnosticLines();
        int accent = _status is RoleStatus.Connecting or RoleStatus.Reconnecting or RoleStatus.NoSignal ? 1
            : _status == RoleStatus.Unassigned ? 2 : 0;
        byte[] buf;
        lock (_diag)
            buf = _diag.Render(lines, accent);
        var info = MakeInfo(buf.Length);
        if (reader)
        {
            _publisher.PublishFrame(buf, info);
            Interlocked.Increment(ref _outWindowSent);
        }
        if (mirror)
            _mirror!.Publish(this, buf, info);
    }

    private List<string> BuildDiagnosticLines()
    {
        string headline = _status switch
        {
            RoleStatus.Missing => "PHYSICAL CAMERA MISSING",
            RoleStatus.Unassigned => "NO PHYSICAL CAMERA ASSIGNED",
            RoleStatus.Duplicated => "DUPLICATED REGISTRATION",
            RoleStatus.NoSignal => "NO SIGNAL",
            RoleStatus.FormatError => "FORMAT ERROR",
            RoleStatus.Connecting => "CONNECTING",
            RoleStatus.Reconnecting => "RECONNECTING",
            _ => _failure switch
            {
                CaptureFailure.BandwidthSuspected => "USB BANDWIDTH INSUFFICIENT",
                CaptureFailure.InUse => "CAMERA IN USE BY ANOTHER APP",
                CaptureFailure.OpenFailed => "CAMERA CANNOT BE OPENED",
                _ => "CAMERA ERROR",
            },
        };
        var lines = new List<string>
        {
            _cfg.Role,
            headline,
            "STATE: " + RoleStatusText.Label(_status) + (_attempts > 0 ? $" (ATTEMPT {_attempts})" : ""),
            "LAST FRAME: " + FormatTime(_lastFrameUtc),
            "NOW: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            "DEVICE: " + (_cfg.IsAssigned ? CameraRoles.DeviceIdTail(_cfg.DeviceId) : "(NONE)"),
        };
        if (!string.IsNullOrEmpty(_detail))
            lines.Add(_detail);
        return lines;
    }

    private static string FormatTime(DateTime utc) =>
        utc == DateTime.MinValue ? "NEVER" : utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    private long _synthLastFrames;
    private double _synthFps;

    private void UpdateOutputFps(long now)
    {
        long el = now - _outWindowStart;
        if (el < 2000)
            return;
        _outFps = Interlocked.Exchange(ref _outWindowSent, 0) * 1000.0 / el;
        if (_synthetic != null)
        {
            long f = _synthetic.Frames;
            _synthFps = (f - _synthLastFrames) * 1000.0 / el;
            _synthLastFrames = f;
        }
        _outWindowStart = now;
    }

    // ------------------------------------------------------------- snapshot

    public RoleSnapshot Snapshot()
    {
        var src = _source;
        bool vcam = VirtualCameraRunning?.Invoke() ?? false;
        var cameraStatus = _status;
        var status = cameraStatus == RoleStatus.Connected && !vcam ? RoleStatus.VirtualCameraStopped : cameraStatus;
        return new RoleSnapshot
        {
            Role = _cfg.Role,
            VirtualCameraName = _cfg.VirtualCameraName,
            VirtualCameraId = _cfg.VirtualCameraId,
            DeviceId = _cfg.DeviceId,
            DeviceIdTail = CameraRoles.DeviceIdTail(_cfg.DeviceId),
            PhysicalFriendlyName = _cfg.DisplayDeviceName,
            CameraStatus = cameraStatus,
            Status = status,
            Detail = status == RoleStatus.VirtualCameraStopped ? "camera OK; this role's virtual camera is switched off" : _detail,
            Failure = _failure,
            Warning = _warning,
            InputWidth = src?.InputWidth ?? (_synthetic != null ? _w : 0),
            InputHeight = src?.InputHeight ?? (_synthetic != null ? _h : 0),
            InputPixelFormat = src != null ? src.InputSubtype : _synthetic != null ? "SYNTHETIC" : "",
            InputNominalFps = src?.InputNominalFps ?? (_synthetic != null ? _fps : 0),
            InputFps = src?.MeasuredFps ?? _synthFps,
            OutputWidth = _w,
            OutputHeight = _h,
            OutputFps = _fps,
            OutputFpsMeasured = _outFps,
            FramesReceived = _framesBeforeSource + (src?.FramesLifetime ?? 0) + (_synthetic?.Frames ?? 0),
            DroppedFrames = _droppedBeforeSource + (src?.DroppedFrames ?? 0) + _publisher.OverwrittenCount,
            LastFrameUtc = src?.LastFrameUtc is { } t && t != DateTime.MinValue ? t : _lastFrameUtc,
            Reconnects = Interlocked.Read(ref _reconnects),
            ReconnectAttempts = _attempts,
            VirtualCameraEnabled = _cfg.VirtualCameraEnabled,
            VirtualCameraRunning = vcam,
            VirtualCameraInUse = _publisher.ClientCount > 0,
            ProcessMicros = src?.AverageProcessMicros ?? 0,
            Pipeline = src?.PipelineDescription ?? "",
        };
    }

    // -------------------------------------------------------------- teardown

    private async Task DisposeSourceAsync()
    {
        var src = _source;
        _source = null;
        if (src != null)
        {
            _framesBeforeSource += src.FramesLifetime;
            _droppedBeforeSource += src.DroppedFrames;
            var last = src.LastFrameUtc;
            if (last != DateTime.MinValue)
                _lastFrameUtc = last;
            await DisposeQuietly(src).ConfigureAwait(false);
        }
    }

    private async Task DisposeQuietly(CameraSource src)
    {
        src.FrameReady -= OnFrame;
        src.PreviewReady -= OnPreview;
        try { await src.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { AppLog.Write(Role, $"source dispose threw {ex.GetType().Name}: {ex.Message}"); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _presence.Changed -= OnPresenceChanged;
        try { _timer?.Dispose(); } catch { }
        _timer = null;

        // Wait (bounded) for an in-flight supervisor step, e.g. an open.
        bool entered = await _gate.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        try
        {
            await DisposeSourceAsync().ConfigureAwait(false);
        }
        finally
        {
            if (entered)
                _gate.Release();
        }
        _synthetic?.Dispose();
        _delay.Configure(0);
        _publisher.Dispose();
        AppLog.Write(Role, "role stopped");
    }
}
