using System.Runtime.InteropServices;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using WinRT;

namespace WebcamSwitcher.Core;

// COM interface used to read the raw bytes of a SoftwareBitmap's buffer.
[ComImport]
[Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe interface IMemoryBufferByteAccess
{
    void GetBuffer(out byte* buffer, out uint capacity);
}

/// <summary>Why a capture start failed. Each maps to a distinct operator-facing state.</summary>
public enum CaptureFailure
{
    None,
    /// <summary>The configured device ID is not enumerated (unplugged, other port, disabled).</summary>
    DeviceNotFound,
    /// <summary>The device exists but could not be opened (driver, privacy setting, generic failure).</summary>
    OpenFailed,
    /// <summary>Another application holds the camera exclusively.</summary>
    InUse,
    /// <summary>No usable capture format, or the format / reader could not be set.</summary>
    FormatFailed,
    /// <summary>The stream started but no frame arrived.</summary>
    NoFrames,
    /// <summary>Streaming could not start in a way typical of an exhausted USB controller.</summary>
    BandwidthSuspected,
}

/// <summary>
/// Captures a single physical camera (by exact device ID) via MediaFrameReader
/// and normalizes each frame to NV12 at the output size, fitting a different
/// aspect by letterbox, crop or stretch.
/// </summary>
public sealed class CameraSource : IAsyncDisposable
{
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private MfMjpegSource? _mf;
    private byte[] _nv12 = Array.Empty<byte>();
    private byte[] _preview = Array.Empty<byte>();
    private readonly object _lock = new();
    private int _nullFrames;
    private int _firstFrameLogged;
    private int _convertErrors;
    private int _framesArrived;
    private int _fpsWindowFrames;
    private DateTime _fpsWindowStart = DateTime.UtcNow;

    // --- Frame-stall watchdog -------------------------------------------------
    // WinRT MediaFrameReader delivery can silently stop after a while (observed as
    // ~0.9 fps with bursts of 75 frames). The pipeline does not notice a dead
    // reader, so the source now watches its own callback liveness and restarts the
    // reader/capture pair instead of leaving a frozen camera until the user toggles
    // capture off and on.
    private readonly SemaphoreSlim _lifecycle = new(1, 1); // serializes start/stop/recovery
    private System.Threading.Timer? _watchdog;
    private long _lastFrameTicks;            // Environment.TickCount64 of the last callback
    private long _startedTicks;              // when the current reader start completed
    private long _framesTotal;               // callbacks since the last (re)start
    private long _stallRecoveries;           // successful restarts (a frame arrived)
    private long _stallDiagLogged;           // one environment dump per source
    private int _recovering;                 // 1 while a recovery runs
    private long _recoveryRestartTicks;      // when the current recovery restart began
    private long _recoveryFirstFramePending; // 1 until the first frame after a restart
    private long _recoveryBackoffMs;         // current backoff step, 0 = none
    private long _nextRecoveryTicks;         // earliest time for the next attempt
    private int _lastCaptureWidth;           // start arguments, replayed on recovery
    private int _lastCaptureHeight;
    private IReadOnlyList<MediaFrameSourceGroup>? _lastGroups;
    private volatile bool _disposed;
    private volatile bool _captureDesired; // true while capture should be running (incl. during recovery)

    // --- Operator-facing stats (read by the role supervisor / UI) -------------
    private long _droppedFrames;            // null bitmaps, conversion errors, size mismatch
    private long _framesLifetime;           // frames across restarts
    private long _lastFrameUtcTicks;        // DateTime.UtcNow.Ticks of the last good frame
    private long _lastArrivalQpc;           // Stopwatch timestamp at the start of the last callback
    private long _processTicksTotal;        // Stopwatch ticks spent converting/publishing
    private long _processSamples;
    private double _measuredFps;
    private long _measureWindowStart = Environment.TickCount64;
    private long _measureWindowFrames;
    private volatile bool _deviceLost;

    // Geometry: the reader delivers _readW x _readH; it is placed into the
    // OutputWidth x OutputHeight canvas at (_dstX,_dstY) (letterbox) or a window
    // starting at (_srcX,_srcY) is taken from it (crop). Equal sizes = straight copy.
    private int _readW, _readH;
    private int _dstX, _dstY, _srcX, _srcY;
    private int _copyW, _copyH;
    private bool _canvasCleared;

    private const long StallThresholdMs = 2000;
    private const long StartupGraceMs = 5000;
    private const long RecoveryFirstFrameTimeoutMs = 5000;
    private const long MinRecoveryBackoffMs = 2000;
    private const long MaxRecoveryBackoffMs = 30000;
    private const int RecoverySettleMs = 300;

    public const int PreviewWidth = 320;
    public const int PreviewHeight = 180;

    public string DeviceId { get; }
    public string DisplayName { get; }
    public bool Running { get; private set; }

    /// <summary>Why the last start failed (None after a successful start).</summary>
    public CaptureFailure LastFailure { get; private set; }

    /// <summary>Human-readable detail for <see cref="LastFailure"/> (HRESULT, status...).</summary>
    public string LastFailureDetail { get; private set; } = "";

    /// <summary>
    /// Set when Windows reports the device failed or went away while streaming
    /// (MediaCapture.Failed). The supervisor then treats the camera as removed.
    /// </summary>
    public bool DeviceLost => _deviceLost;

    /// <summary>Native capture mode actually selected (size, subtype, nominal fps).</summary>
    public int InputWidth { get; private set; }
    public int InputHeight { get; private set; }
    public string InputSubtype => _chosenSubtype;
    public double InputNominalFps { get; private set; }

    /// <summary>Frames per second measured over the last ~2 s.</summary>
    public double MeasuredFps => Volatile.Read(ref _measuredFps);

    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);
    public long FramesLifetime => Interlocked.Read(ref _framesLifetime);

    /// <summary>UTC time of the last delivered frame (DateTime.MinValue when none).</summary>
    public DateTime LastFrameUtc
    {
        get
        {
            long t = Interlocked.Read(ref _lastFrameUtcTicks);
            return t == 0 ? DateTime.MinValue : new DateTime(t, DateTimeKind.Utc);
        }
    }

    /// <summary>Stopwatch timestamp taken when the frame now being published arrived.</summary>
    public long LastArrivalTimestamp => Interlocked.Read(ref _lastArrivalQpc);

    /// <summary>Average per-frame processing time on this source's thread, in microseconds.</summary>
    public double AverageProcessMicros
    {
        get
        {
            long n = Interlocked.Read(ref _processSamples);
            return n == 0 ? 0 : Interlocked.Read(ref _processTicksTotal) * 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency / n;
        }
    }

    /// <summary>"Letterbox" (default), "Crop" or "Stretch"; see <see cref="ScaleModes"/>.</summary>
    public string ScaleMode { get; set; } = ScaleModes.Letterbox;

    /// <summary>Requested capture frame rate used to choose the native mode (0 = output rate).</summary>
    public int RequestedCaptureFps { get; set; }

    /// <summary>Path the frames take, for the status display (e.g. "NV12 960x720 -> letterbox").</summary>
    public string PipelineDescription { get; private set; } = "";

    public int OutputWidth { get; private set; }
    public int OutputHeight { get; private set; }

    /// <summary>Raised with the latest normalized NV12 frame (buffer is reused).</summary>
    public event Action<byte[]>? FrameReady;

    /// <summary>Raised with the latest downscaled Bgra8 preview (buffer is reused).</summary>
    public event Action<byte[], int, int>? PreviewReady;

    /// <summary>
    /// When false, the per-frame preview downscale is skipped entirely (no CPU cost,
    /// no <see cref="PreviewReady"/>). Capture and virtual-camera publishing are
    /// unaffected.
    /// </summary>
    public bool PreviewEnabled
    {
        get => Volatile.Read(ref _previewEnabled);
        set => Volatile.Write(ref _previewEnabled, value);
    }

    private bool _previewEnabled = true;

    /// <summary>
    /// When true, exposure is pinned to manual 1/30 s after the reader starts so
    /// the camera cannot lengthen integration time in dim light and drop to its
    /// 15 fps mode. The image may be darker; that is the accepted tradeoff.
    /// </summary>
    public bool PreferSmooth30Fps { get; set; }

    /// <summary>
    /// Capture-format preference for this camera: "Auto" (default) picks the fastest
    /// stream at the chosen size and keeps NV12 when it is already at full rate,
    /// "Nv12" forces uncompressed, "Mjpg" forces compressed (higher frame rate on
    /// bandwidth-limited cameras, at the cost of a decode).
    /// </summary>
    public string PreferredFormat { get; set; } = "Auto";

    /// <summary>Subtype actually selected for the capture stream.</summary>
    private string _chosenSubtype = "NV12";

    /// <summary>
    /// Configured output rate. Only used to describe the MF output type; the rate
    /// itself is whatever the camera delivers (the pipes are drop-latest).
    /// </summary>
    public int CaptureFps { get; set; } = Protocol.DefaultFps;

    /// <summary>Successful stall recoveries for this source (a frame arrived after a restart).</summary>
    public long StallRecoveries => Interlocked.Read(ref _stallRecoveries);

    /// <summary>Frame callbacks received since the current capture was started.</summary>
    public long FramesReceived => Interlocked.Read(ref _framesTotal);

    public CameraSource(string deviceId, string displayName)
    {
        DeviceId = deviceId;
        DisplayName = displayName;
    }

    public async Task<bool> StartAsync(int outWidth, int outHeight, int captureWidth = 0, int captureHeight = 0,
        IReadOnlyList<MediaFrameSourceGroup>? groups = null)
    {
        // Serialized with the watchdog and DisposeAsync: a recovery restart must
        // never run concurrently with a user-initiated start/stop.
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            // Remember the arguments so the watchdog can replay this exact start.
            _lastCaptureWidth = captureWidth;
            _lastCaptureHeight = captureHeight;
            _lastGroups = groups;
            _disposed = false;

            // A user-initiated start is not a recovery: clear any recovery state.
            Interlocked.Exchange(ref _recoveryFirstFramePending, 0);
            Volatile.Write(ref _recoveryRestartTicks, 0);
            Interlocked.Exchange(ref _recoveryBackoffMs, 0);
            Volatile.Write(ref _nextRecoveryTicks, 0);

            _captureDesired = true;
            LastFailure = CaptureFailure.None;
            LastFailureDetail = "";
            bool ok;
            try
            {
                ok = await StartCoreAsync(outWidth, outHeight, captureWidth, captureHeight, groups).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ok = false;
                Classify(ex);
                AppLog.Write($"Source.StartAsync({DisplayName}): {LastFailure} - {LastFailureDetail}");
            }
            if (!ok)
            {
                _captureDesired = false;
                if (LastFailure == CaptureFailure.None)
                    Fail(CaptureFailure.OpenFailed, "start returned false");
                // Release whatever was opened so the device is free for a retry.
                try { await DisposeCoreAsync().ConfigureAwait(false); } catch { }
            }
            return ok;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// The actual start path. Callers must hold <see cref="_lifecycle"/>; the
    /// watchdog recovery re-enters here after tearing the old capture down.
    /// </summary>
    private async Task<bool> StartCoreAsync(int outWidth, int outHeight, int captureWidth, int captureHeight,
        IReadOnlyList<MediaFrameSourceGroup>? groups)
    {
        OutputWidth = outWidth;
        OutputHeight = outHeight;
        _deviceLost = false;
        // Reuse buffers across reconnects; only a size change reallocates.
        if (_nv12.Length != Protocol.Nv12Size(outWidth, outHeight))
            _nv12 = new byte[Protocol.Nv12Size(outWidth, outHeight)];
        if (_preview.Length != PreviewWidth * PreviewHeight * 4)
            _preview = new byte[PreviewWidth * PreviewHeight * 4];
        SetGeometry(outWidth, outHeight, outWidth, outHeight);
        // A restart must not inherit "a frame already arrived" from the last run.
        Interlocked.Exchange(ref _framesArrived, 0);
        Interlocked.Exchange(ref _firstFrameLogged, 0);
        // ... nor liveness for the watchdog.
        Interlocked.Exchange(ref _framesTotal, 0);
        Volatile.Write(ref _lastFrameTicks, Environment.TickCount64);

        AppLog.Write($"Source.StartAsync({DisplayName}): finding group");
        // Callers may pass a pre-enumerated list so N cameras don't each perform
        // a full redundant device scan; when null, enumerate as before.
        groups ??= await MediaFrameSourceGroup.FindAllAsync();
        // Exact device-ID match only. Never fall back to another camera.
        var group = groups.FirstOrDefault(g => string.Equals(g.Id, DeviceId, StringComparison.OrdinalIgnoreCase));
        if (group == null)
        {
            AppLog.Write($"Source.StartAsync({DisplayName}): device ID not enumerated ({CameraRoles.DeviceIdTail(DeviceId)})");
            Fail(CaptureFailure.DeviceNotFound, "device ID not present");
            return false;
        }

        // MJPG: the WinRT reader cannot decode it (it starts and yields no frames),
        // so when MJPG is explicitly requested try the Media Foundation source
        // reader first. It must run BEFORE MediaCapture opens the device, because
        // MediaCapture takes exclusive control and MF could not open it afterwards.
        // Any MF failure falls through to the unchanged WinRT path below.
        bool captureSizeMatches =
            (captureWidth <= 0 || captureWidth == outWidth) &&
            (captureHeight <= 0 || captureHeight == outHeight);

        if (PreferredFormat.Equals("Mjpg", StringComparison.OrdinalIgnoreCase))
        {
            if (!captureSizeMatches)
                AppLog.Write($"Source({DisplayName}): MF-MJPEG skipped (capture {captureWidth}x{captureHeight} != output {outWidth}x{outHeight})");
            else if (await TryStartMfMjpegAsync())
                return true;
        }

        _capture = new MediaCapture();
        _capture.Failed += OnCaptureFailed;
        AppLog.Write($"Source.StartAsync({DisplayName}): InitializeAsync");
        try
        {
            await _capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                SourceGroup = group,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu
            });
        }
        catch (Exception ex)
        {
            Classify(ex);
            AppLog.Write($"Source({DisplayName}): open failed: {LastFailure} - {LastFailureDetail}");
            return false;
        }

        var colorSource = _capture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
        if (colorSource == null)
        {
            Fail(CaptureFailure.FormatFailed, "device has no color stream");
            return false;
        }

        // Diagnostic: surface the native + supported formats so aspect/padding issues are visible.
        try
        {
            var fmts = colorSource.SupportedFormats
                .Select(f => $"{f.VideoFormat.Width}x{f.VideoFormat.Height}")
                .Distinct()
                .ToList();
            var cur = colorSource.CurrentFormat?.VideoFormat;
            AppLog.Write($"Source({DisplayName}): native={cur?.Width}x{cur?.Height} supported=[{string.Join(",", fmts)}]");
        }
        catch (Exception ex)
        {
            AppLog.Write($"Source({DisplayName}): format probe failed: {ex.Message}");
        }

        // Match the camera's capture format to the output aspect. The C920, for
        // example, defaults to 640x480 (4:3); scaling that into a 16:9 output
        // pillarboxes it. Selecting a native 16:9 mode fills the frame and lets
        // the Frame Server skip the resize entirely.
        if (!await TrySetCaptureFormatAsync(colorSource, captureWidth, captureHeight))
            return false;
        ComputeGeometryFrom(colorSource);

        AppLog.Write($"Source.StartAsync({DisplayName}): CreateFrameReaderAsync");
        _reader = await StartReaderAsync(colorSource);
        if (_reader == null)
            return false;
        // Realtime = drop frames rather than queue when the handler falls behind
        // (the default, set explicitly for low latency / no backlog).
        _reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
        _reader.FrameArrived += OnFrameArrived;

        // A compressed stream (MJPG) only works if the pipeline can decode it.
        // MediaFrameReader does not decode on its own, so prove frames actually
        // arrive and otherwise revert to uncompressed - a config choice must never
        // leave a camera black.
        if (IsCompressed(_chosenSubtype) && !await WaitForFirstFrameAsync(TimeSpan.FromMilliseconds(2500)))
        {
            AppLog.Write($"Source({DisplayName}): {_chosenSubtype} produced no frames (reader cannot decode it); reverting to uncompressed");

            _reader.FrameArrived -= OnFrameArrived;
            try { await _reader.StopAsync(); } catch { }
            _reader.Dispose();
            _reader = null;

            if (!await TrySetCaptureFormatAsync(colorSource, captureWidth, captureHeight, forceUncompressed: true))
                return false;
            ComputeGeometryFrom(colorSource);
            _reader = await StartReaderAsync(colorSource);
            if (_reader == null)
                return false;
            _reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            _reader.FrameArrived += OnFrameArrived;
        }

        // Must come last: selecting a capture format resets the device controls,
        // so pinning exposure any earlier would be silently undone.
        ApplyExposurePreference();

        AppLog.Write($"Source.StartAsync({DisplayName}): reader started");
        Running = true;
        // The watchdog only starts once the reader is up: startup itself can
        // legitimately take seconds (format switch, decode probe).
        Volatile.Write(ref _startedTicks, Environment.TickCount64);
        StartWatchdog();
        return true;
    }

    /// <summary>
    /// Pins exposure to a manual 1/30 s integration time when the user asked for
    /// smooth 30 fps. UVC auto-exposure lengthens exposure in dim light and drops
    /// the camera to its 1/15 s mode (exactly 15 fps); a manual 1/30 s cap keeps
    /// the frame rate at the cost of a darker image. Never throws: a camera
    /// without exposure control must keep capturing normally.
    /// </summary>
    private void ApplyExposurePreference()
    {
        if (!PreferSmooth30Fps)
        {
            RestoreAutoExposure();
            return;
        }

        try
        {
            var exposure = _capture?.VideoDeviceController?.Exposure;
            if (exposure == null || !exposure.Capabilities.Supported)
            {
                AppLog.Write($"Source({DisplayName}): exposure control unsupported; cannot pin 30 fps");
                return;
            }

            var caps = exposure.Capabilities;
            if (caps.Step <= 0)
            {
                AppLog.Write($"Source({DisplayName}): exposure control has no usable step; skipping 30 fps cap");
                return;
            }

            // Read what auto-exposure currently wants while it is still in auto, so a
            // scene that is already correctly exposed is left alone instead of being
            // brightened (and blown out) by a fixed value.
            double autoValue = 0;
            bool haveAuto = caps.AutoModeSupported && exposure.TryGetValue(out autoValue);

            // Some cameras report this control in seconds, others in log2 seconds
            // (the UVC/DirectShow convention, where -5 == 1/32 s). A range that sits
            // at or below zero with small integer values is the log2 form.
            bool log2Units = caps.Max <= 0d && caps.Min < 0d;

            double cap = log2Units ? Math.Log(1.0 / 30.0, 2.0) : 1.0 / 30.0;

            // Only ever shorten the exposure: keep auto's value when it is already
            // shorter than the cap (bright scene), otherwise clamp down to the cap.
            double target = haveAuto ? Math.Min(autoValue, cap) : cap;

            // Snap to the control's grid and keep it inside the advertised range.
            target = caps.Min + Math.Round((target - caps.Min) / caps.Step) * caps.Step;
            target = Math.Max(caps.Min, Math.Min(caps.Max, target));

            if (!exposure.TrySetAuto(false))
            {
                AppLog.Write($"Source({DisplayName}): exposure auto-off failed; cannot cap exposure");
                return;
            }

            if (!exposure.TrySetValue(target))
            {
                AppLog.Write($"Source({DisplayName}): exposure set {target} failed (min={caps.Min} max={caps.Max} step={caps.Step})");
                return;
            }

            double applied = target;
            exposure.TryGetValue(out applied);
            string pretty = log2Units
                ? $"1/{Math.Round(Math.Pow(2, -applied)):0}s"
                : $"{applied:0.0000}s";
            AppLog.Write($"Source({DisplayName}): exposure capped at {applied} ({(log2Units ? "log2 s" : "s")}) = {pretty} for 30 fps (auto wanted {autoValue})");
        }
        catch (Exception ex)
        {
            AppLog.Write($"Source({DisplayName}): exposure pinning failed {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Puts exposure back under camera control. Only meaningful after
    /// <see cref="ApplyExposurePreference"/> pinned a manual value: leaving the
    /// device in manual would keep the image dark for every other app too, so
    /// turning the option off has to undo it. Never throws.
    /// </summary>
    private void RestoreAutoExposure()
    {
        try
        {
            var exposure = _capture?.VideoDeviceController?.Exposure;
            if (exposure == null || !exposure.Capabilities.Supported || !exposure.Capabilities.AutoModeSupported)
                return;

            if (exposure.TryGetAuto(out bool auto) && auto)
                return; // already automatic: nothing was pinned on this device

            if (exposure.TrySetAuto(true))
                AppLog.Write($"Source({DisplayName}): exposure returned to automatic");
            else
                AppLog.Write($"Source({DisplayName}): exposure auto-on failed; device may still be manual");
        }
        catch (Exception ex)
        {
            AppLog.Write($"Source({DisplayName}): exposure restore failed {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Tries the Media Foundation MJPG backend. Returns true only when it started
    /// AND delivered a frame within 2.5s; on any failure the source is disposed and
    /// the caller continues with the WinRT path.
    /// </summary>
    private async Task<bool> TryStartMfMjpegAsync()
    {
        MfMjpegSource? mf = new MfMjpegSource(DeviceId, DisplayName, OutputWidth, OutputHeight, CaptureFps);
        mf.FrameReady += OnMfFrameReady;

        try
        {
            AppLog.Write($"Source({DisplayName}): trying Media Foundation MJPEG backend {OutputWidth}x{OutputHeight}");
            if (!mf.Start())
            {
                AppLog.Write($"Source({DisplayName}): MF-MJPEG failed to start; falling back to WinRT");
                return false;
            }

            if (!await WaitForFirstFrameAsync(TimeSpan.FromMilliseconds(2500)))
            {
                AppLog.Write($"Source({DisplayName}): MF-MJPEG produced no frame in 2.5s; falling back to WinRT");
                return false;
            }

            _mf = mf;
            mf = null; // ownership transferred to the source
            _chosenSubtype = "MJPG";
            InputWidth = OutputWidth;
            InputHeight = OutputHeight;
            InputNominalFps = CaptureFps;
            PipelineDescription = $"MJPG decoded by Media Foundation -> {OutputWidth}x{OutputHeight} (stretch)";
            Running = true;
            AppLog.Write($"Source.StartAsync({DisplayName}): using Media Foundation MJPEG backend {OutputWidth}x{OutputHeight}");
            if (PreferSmooth30Fps)
                AppLog.Write($"Source({DisplayName}): exposure pinning is not applied on the MF/MJPG path");
            return true;
        }
        finally
        {
            if (mf != null)
            {
                mf.FrameReady -= OnMfFrameReady;
                await mf.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Frame callback for the MF backend: publishes the same tight NV12 buffer
    /// contract as the WinRT path, plus the optional 320x180 Bgra8 preview.
    /// </summary>
    private unsafe void OnMfFrameReady(byte[] nv12)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref _lastArrivalQpc, t0);
        Interlocked.Increment(ref _framesArrived);
        CountGoodFrame();
        if (Interlocked.Exchange(ref _firstFrameLogged, 1) == 0)
            AppLog.Write($"Source({DisplayName}): first frame {OutputWidth}x{OutputHeight} NV12 (MF-MJPEG)");

        // Read once per frame: toggling previews mid-stream must not tear a frame.
        bool previews = PreviewEnabled;
        byte[] preview;
        lock (_lock)
            preview = _preview;

        if (previews)
        {
            fixed (byte* src = nv12)
                ColorConverter.Nv12ToBgra8Downscale(
                    src, OutputWidth, OutputHeight, OutputWidth,
                    preview, PreviewWidth, PreviewHeight);
        }

        try
        {
            FrameReady?.Invoke(nv12);
        }
        catch (Exception ex)
        {
            if (Interlocked.Increment(ref _convertErrors) <= 5)
                AppLog.Write($"Source({DisplayName}): MF-MJPEG frame publish threw {ex.GetType().Name}: {ex.Message}");
        }

        if (previews)
            PreviewReady?.Invoke(preview, PreviewWidth, PreviewHeight);
        AddProcessTime(t0);
    }

    /// <summary>
    /// Chooses and applies a capture format whose aspect matches the output, so the
    /// Frame Server does not pillarbox/letterbox, and whose frame rate is the best
    /// that size can sustain. Uncompressed NV12 is preferred (no conversion), but
    /// bandwidth-limited cameras (the C920 over USB 2.0, for example) only reach
    /// their full rate in MJPG, so a compressed stream is used when it is faster.
    /// </summary>
    private async Task<bool> TrySetCaptureFormatAsync(MediaFrameSource source, int captureWidth, int captureHeight, bool forceUncompressed = false)
    {
        try
        {
            var formats = source.SupportedFormats;
            if (formats == null || formats.Count == 0)
            {
                // Nothing to choose from: keep the device default.
                AppLog.Write($"Source({DisplayName}): no supported formats reported; using the device default");
                return true;
            }

            int wantW = captureWidth > 0 ? captureWidth : OutputWidth;
            int wantH = captureHeight > 0 ? captureHeight : OutputHeight;
            bool forced = captureWidth > 0 && captureHeight > 0;
            double targetAspect = (double)wantW / wantH;
            double targetArea = (double)wantW * wantH;
            double wantFps = RequestedCaptureFps > 0 ? RequestedCaptureFps : CaptureFps;
            string preferPick = forceUncompressed ? "Nv12" : (PreferredFormat ?? "Auto");
            bool mjpgOnly = preferPick.Equals("Mjpg", StringComparison.OrdinalIgnoreCase);
            bool rawOnly = preferPick.Equals("Nv12", StringComparison.OrdinalIgnoreCase);

            // 1. Pick the size: exact when forced. Otherwise sizes that reach the
            //    wanted frame rate (in an allowed subtype) come first - a USB 2.0
            //    camera without MJPEG typically offers 1280x720 only at 5-10 fps,
            //    and a 30 fps 640x480 letterboxed into 720p is the better feed -
            //    then best-matching aspect, then closest area.
            var size = formats
                .GroupBy(f => (W: f.VideoFormat.Width, H: f.VideoFormat.Height))
                .Select(g => new
                {
                    g.Key.W,
                    g.Key.H,
                    Area = (double)g.Key.W * g.Key.H,
                    AspectErr = Math.Abs((double)g.Key.W / g.Key.H - targetAspect),
                    ReachesRate = g.Any(f => FpsOf(f) >= wantFps * 0.95 &&
                                             (!mjpgOnly || IsCompressed(f.Subtype)) &&
                                             (!rawOnly || !IsCompressed(f.Subtype))),
                    Rates = g.ToList()
                })
                .OrderBy(s => forced ? 0 : (s.ReachesRate ? 0 : 1))
                .ThenBy(s => forced
                    ? (Math.Abs(s.Area - targetArea), s.AspectErr)
                    : (s.AspectErr, Math.Abs(s.Area - targetArea)))
                .First();

            // 2. Pick the subtype + rate at that size.
            // Never above what was asked for when the asked rate exists: a 60 fps
            // mode would only be thrown away by the 30 fps virtual camera.
            var candidates = size.Rates
                .Select(f => new { Format = f, Fps = FpsOf(f), Compressed = IsCompressed(f.Subtype) })
                .OrderBy(c => c.Fps >= wantFps * 0.95 && c.Fps <= wantFps * 1.05 ? 0 : 1)
                .ThenByDescending(c => c.Fps)
                .ToList();

            var rawBest = candidates.FirstOrDefault(c => !c.Compressed);
            var overall = candidates[0];
            string prefer = forceUncompressed ? "Nv12" : (PreferredFormat ?? "Auto");

            var chosen =
                prefer.Equals("Mjpg", StringComparison.OrdinalIgnoreCase)
                    ? candidates.FirstOrDefault(c => c.Compressed) ?? overall
                    : prefer.Equals("Nv12", StringComparison.OrdinalIgnoreCase)
                        ? rawBest ?? overall
                        : rawBest != null && rawBest.Fps >= overall.Fps * 0.95
                            ? rawBest
                            : overall;

            var rates = string.Join(", ", candidates.Select(c => $"{c.Format.Subtype}@{c.Fps:0.#}"));
            AppLog.Write($"Source({DisplayName}): {size.W}x{size.H} rates [{rates}] -> {chosen.Format.Subtype}@{chosen.Fps:0.#} (prefer {prefer})");

            _chosenSubtype = chosen.Format.Subtype ?? "NV12";
            InputNominalFps = chosen.Fps;

            var cur = source.CurrentFormat;
            if (cur != null &&
                cur.VideoFormat.Width == chosen.Format.VideoFormat.Width &&
                cur.VideoFormat.Height == chosen.Format.VideoFormat.Height &&
                string.Equals(cur.Subtype, _chosenSubtype, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(FpsOf(cur) - chosen.Fps) < 0.5)
            {
                AppLog.Write($"Source({DisplayName}): capture format already {size.W}x{size.H} {_chosenSubtype}@{chosen.Fps:0.#}");
                return true;
            }

            await source.SetFormatAsync(chosen.Format);
            AppLog.Write($"Source({DisplayName}): capture format -> {size.W}x{size.H} {_chosenSubtype}@{chosen.Fps:0.#} (target {wantW}x{wantH}@{wantFps:0.#}{(forced ? ", forced" : "")})");
            if (!chosen.Compressed && chosen.Fps < wantFps * 0.95)
                AppLog.Write($"Source({DisplayName}): NOTE uncompressed {size.W}x{size.H} only reaches {chosen.Fps:0.#} fps on this camera (USB 2.0 bandwidth limit of the device mode)");
            return true;
        }
        catch (Exception ex)
        {
            // A device that rejects the chosen mode keeps its current one; that is
            // still a working camera, so only log it.
            AppLog.Write($"Source({DisplayName}): SetFormat failed ({ex.Message}); keeping the device's current format");
            return true;
        }
    }

    private static double FpsOf(MediaFrameFormat f)
    {
        var r = f.FrameRate;
        if (r == null || r.Denominator == 0)
            return 0;
        return (double)r.Numerator / r.Denominator;
    }

    private static bool IsCompressed(string? subtype) =>
        string.Equals(subtype, "MJPG", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(subtype, "H264", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(subtype, "HEVC", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Creates and starts a frame reader that produces a decoded color frame.
    /// Requesting Bgra8 (rather than leaving the subtype unset) guarantees
    /// <see cref="VideoMediaFrame.SoftwareBitmap"/> is non-null for cameras whose
    /// native format is compressed (MJPG/H.264).
    /// </summary>
    private async Task<MediaFrameReader?> StartReaderAsync(MediaFrameSource colorSource)
    {
        // Uncompressed sources are requested as NV12 at the exact output size, so the
        // Frame Server does any resize + color conversion itself and the hot path is a
        // straight copy. The same request is made for a compressed source (MJPG): the
        // reader can hand back decoded NV12, and NV12 is the one subtype that works
        // for both. Bgra8 is only a fallback, because on a compressed stream it is the
        // path that yields no frames at all.
        MediaFrameReaderStartStatus status = MediaFrameReaderStartStatus.UnknownFailure;
        try
        {
            var r = await _capture!.CreateFrameReaderAsync(
                colorSource, MediaEncodingSubtypes.Nv12,
                new BitmapSize { Width = (uint)_readW, Height = (uint)_readH });
            status = await r.StartAsync();
            if (status == MediaFrameReaderStartStatus.Success)
            {
                AppLog.Write($"Source({DisplayName}): frame reader = Nv12 {_readW}x{_readH} -> {PipelineDescription}" +
                             (IsCompressed(_chosenSubtype) ? $" (decoding {_chosenSubtype})" : ""));
                return r;
            }
            AppLog.Write($"Source({DisplayName}): Nv12 reader start status {status}");
            r.Dispose();
            if (status != MediaFrameReaderStartStatus.OutputFormatNotSupported)
            {
                FailFromReaderStatus(status);
                return null;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"Source({DisplayName}): Nv12 reader failed ({ex.Message}); falling back to Bgra8");
        }

        try
        {
            var bgra = await _capture!.CreateFrameReaderAsync(colorSource, MediaEncodingSubtypes.Bgra8);
            status = await bgra.StartAsync();
            if (status != MediaFrameReaderStartStatus.Success)
            {
                bgra.Dispose();
                FailFromReaderStatus(status);
                return null;
            }
            PipelineDescription = $"Bgra8 -> software NV12 {OutputWidth}x{OutputHeight}";
            AppLog.Write($"Source({DisplayName}): frame reader = Bgra8");
            return bgra;
        }
        catch (Exception ex)
        {
            Classify(ex);
            if (LastFailure == CaptureFailure.OpenFailed)
                Fail(CaptureFailure.FormatFailed, $"frame reader: {LastFailureDetail}");
            return null;
        }
    }

    private void FailFromReaderStatus(MediaFrameReaderStartStatus status)
    {
        switch (status)
        {
            case MediaFrameReaderStartStatus.ExclusiveControlNotAvailable:
                Fail(CaptureFailure.InUse, "exclusive control not available (another app is using the camera)");
                break;
            case MediaFrameReaderStartStatus.OutputFormatNotSupported:
                Fail(CaptureFailure.FormatFailed, "output format not supported by the frame reader");
                break;
            case MediaFrameReaderStartStatus.DeviceNotAvailable:
                // The device enumerates and opened, yet cannot stream: on a USB
                // bus that already carries other cameras this is the classic
                // "not enough bandwidth / controller resources" symptom.
                Fail(CaptureFailure.BandwidthSuspected, "device not available for streaming (USB bandwidth or controller resources suspected)");
                break;
            default:
                Fail(CaptureFailure.OpenFailed, $"frame reader start status {status}");
                break;
        }
    }

    private void Fail(CaptureFailure failure, string detail)
    {
        LastFailure = failure;
        LastFailureDetail = detail;
    }

    /// <summary>Maps an exception from opening/streaming to a failure kind.</summary>
    private void Classify(Exception ex)
    {
        uint hr = unchecked((uint)ex.HResult);
        string detail = $"0x{hr:X8} {ex.Message.Trim()}";
        switch (hr)
        {
            case 0x80070005: // E_ACCESSDENIED: Windows camera privacy setting, or held by another app
                Fail(CaptureFailure.OpenFailed, $"access denied (camera privacy setting, or in use): {detail}");
                break;
            case 0xC00DABE1: // MF_E_VIDEO_RECORDING_DEVICE_PREEMPTED
                Fail(CaptureFailure.InUse, $"camera preempted by another application: {detail}");
                break;
            case 0xC00DABE0: // MF_E_VIDEO_RECORDING_DEVICE_INVALIDATED (removed while in use)
            case 0x80070490: // ERROR_NOT_FOUND
            case 0x8007048F: // ERROR_DEVICE_NOT_CONNECTED
            case 0x80070015: // ERROR_NOT_READY
                Fail(CaptureFailure.DeviceNotFound, $"device removed or not ready: {detail}");
                break;
            case 0xC00D3704: // MF_E_HW_MFT_FAILED_START_STREAMING: lack of hardware resources
            case 0x8007001F: // ERROR_GEN_FAILURE from the camera driver on stream start
            case 0x800705AA: // ERROR_NO_SYSTEM_RESOURCES
                Fail(CaptureFailure.BandwidthSuspected, $"streaming resources unavailable (USB bandwidth suspected): {detail}");
                break;
            case 0xC00D36B4: // MF_E_INVALIDMEDIATYPE
                Fail(CaptureFailure.FormatFailed, $"format negotiation failed: {detail}");
                break;
            default:
                Fail(CaptureFailure.OpenFailed, detail);
                break;
        }
    }

    private void OnCaptureFailed(MediaCapture sender, MediaCaptureFailedEventArgs args)
    {
        _deviceLost = true;
        AppLog.Write($"Source({DisplayName}): MediaCapture.Failed 0x{args.Code:X8} {args.Message?.Trim()} -> treating the device as lost");
    }

    // --- Geometry ---------------------------------------------------------------

    private void SetGeometry(int readW, int readH, int outW, int outH)
    {
        _readW = readW;
        _readH = readH;
        _copyW = Math.Min(readW, outW);
        _copyH = Math.Min(readH, outH);
        _dstX = ((outW - _copyW) / 2) & ~1;
        _dstY = ((outH - _copyH) / 2) & ~1;
        _srcX = ((readW - _copyW) / 2) & ~1;
        _srcY = ((readH - _copyH) / 2) & ~1;
        _canvasCleared = false;
    }

    /// <summary>
    /// Decides the reader size from the selected native mode. The reader (Media
    /// Foundation's video processor) does the scaling, so this costs no CPU here:
    /// letterbox asks for the largest size that fits inside the output keeping the
    /// camera's aspect, crop asks for the smallest that covers it, stretch asks for
    /// the output size. A native mode equal to the output is passed through.
    /// </summary>
    private void ComputeGeometryFrom(MediaFrameSource colorSource)
    {
        var vf = colorSource.CurrentFormat?.VideoFormat;
        int sw = (int)(vf?.Width ?? 0), sh = (int)(vf?.Height ?? 0);
        InputWidth = sw;
        InputHeight = sh;
        if (InputNominalFps <= 0 && colorSource.CurrentFormat != null)
            InputNominalFps = FpsOf(colorSource.CurrentFormat);

        int ow = OutputWidth, oh = OutputHeight;
        string mode = ScaleModes.Normalize(ScaleMode);
        if (sw <= 0 || sh <= 0 || (sw == ow && sh == oh) || mode == ScaleModes.Stretch ||
            Math.Abs((double)sw / sh - (double)ow / oh) < 0.01)
        {
            SetGeometry(ow, oh, ow, oh);
            PipelineDescription = sw == ow && sh == oh
                ? $"{_chosenSubtype} {sw}x{sh} passthrough"
                : $"{_chosenSubtype} {sw}x{sh} -> {ow}x{oh} ({(mode == ScaleModes.Stretch ? "stretch" : "same aspect")})";
            return;
        }

        var (rw, rh) = FitSize(sw, sh, ow, oh, mode);
        SetGeometry(rw, rh, ow, oh);
        PipelineDescription = $"{_chosenSubtype} {sw}x{sh} -> {rw}x{rh} {(mode == ScaleModes.Crop ? "crop" : "letterbox")} into {ow}x{oh}";
    }

    /// <summary>
    /// Reader size for a <paramref name="sw"/>x<paramref name="sh"/> camera in an
    /// <paramref name="ow"/>x<paramref name="oh"/> output: letterbox = largest even
    /// size inside the output with the camera's aspect (640x480 -> 960x720 in
    /// 1280x720); crop = smallest covering it (640x480 -> 1280x960); stretch = output.
    /// </summary>
    public static (int W, int H) FitSize(int sw, int sh, int ow, int oh, string mode)
    {
        mode = ScaleModes.Normalize(mode);
        if (sw <= 0 || sh <= 0 || mode == ScaleModes.Stretch)
            return (ow, oh);
        double scale = mode == ScaleModes.Crop
            ? Math.Max((double)ow / sw, (double)oh / sh)
            : Math.Min((double)ow / sw, (double)oh / sh);
        int rw = Math.Max(2, (int)Math.Round(sw * scale) & ~1);
        int rh = Math.Max(2, (int)Math.Round(sh * scale) & ~1);
        return mode == ScaleModes.Crop
            ? (Math.Max(rw, ow), Math.Max(rh, oh))
            : (Math.Min(rw, ow), Math.Min(rh, oh));
    }

    private void CountGoodFrame()
    {
        Interlocked.Increment(ref _framesLifetime);
        Interlocked.Exchange(ref _lastFrameUtcTicks, DateTime.UtcNow.Ticks);
        long n = Interlocked.Increment(ref _measureWindowFrames);
        long now = Environment.TickCount64;
        long start = Interlocked.Read(ref _measureWindowStart);
        long el = now - start;
        if (el >= 2000 && Interlocked.CompareExchange(ref _measureWindowStart, now, start) == start)
        {
            Volatile.Write(ref _measuredFps, n * 1000.0 / el);
            Interlocked.Exchange(ref _measureWindowFrames, 0);
        }
    }

    private void AddProcessTime(long startTimestamp)
    {
        long dt = System.Diagnostics.Stopwatch.GetTimestamp() - startTimestamp;
        // Rolling sum over a bounded window so the average follows the load.
        if (Interlocked.Increment(ref _processSamples) > 300)
        {
            Interlocked.Exchange(ref _processSamples, 1);
            Interlocked.Exchange(ref _processTicksTotal, dt);
            return;
        }
        Interlocked.Add(ref _processTicksTotal, dt);
    }

    /// <summary>Measured fps reads 0 once frames stop, instead of the last window.</summary>
    public void RefreshMeasuredFps()
    {
        long el = Environment.TickCount64 - Interlocked.Read(ref _measureWindowStart);
        if (el > 3000)
            Volatile.Write(ref _measuredFps, Interlocked.Read(ref _measureWindowFrames) * 1000.0 / el);
    }

    private async Task<bool> WaitForFirstFrameAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Volatile.Read(ref _framesArrived) > 0)
                return true;
            await Task.Delay(50);
        }
        return Volatile.Read(ref _framesArrived) > 0;
    }

    // --- Stall watchdog -------------------------------------------------------

    private void StartWatchdog()
    {
        if (_watchdog == null)
            _watchdog = new System.Threading.Timer(OnWatchdogTick, null, 1000, 1000);
    }

    private void StopWatchdog()
    {
        var timer = _watchdog;
        _watchdog = null;
        if (timer != null)
        {
            try { timer.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Runs once per second while a capture is wanted. Cheap: a few volatile reads
    /// and no allocation unless the frame stream has actually gone quiet. The
    /// recovery itself is fire-and-forget so this callback never blocks a pool
    /// thread on I/O.
    /// </summary>
    private void OnWatchdogTick(object? state)
    {
        if (_disposed || !_captureDesired)
            return;

        try
        {
            long now = Environment.TickCount64;
            if (!IsStalled(now, out _))
                return;
            if (now < Volatile.Read(ref _nextRecoveryTicks))
                return;
            if (Interlocked.CompareExchange(ref _recovering, 1, 0) != 0)
                return;

            _ = RecoverFromStallAsync();
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _recovering, 0);
            AppLog.Write($"Source({DisplayName}): watchdog tick threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Stall rule: no callback for more than 2s, at least one callback since the
    /// last (re)start, and more than 5s since that start (startup grace). A
    /// recovery restart that never delivered a frame counts as stalled too, so
    /// the backoff can retry it.
    /// </summary>
    private bool IsStalled(long now, out long stalledMs)
    {
        stalledMs = now - Volatile.Read(ref _lastFrameTicks);
        bool awaitingFirstFrame = Volatile.Read(ref _recoveryFirstFramePending) == 1;

        if (_reader == null && !awaitingFirstFrame)
            return false;
        if (!awaitingFirstFrame && Volatile.Read(ref _framesTotal) <= 0)
            return false; // nothing has ever arrived: still starting, not stalled

        long sinceStart = awaitingFirstFrame
            ? now - Volatile.Read(ref _recoveryRestartTicks)
            : now - Volatile.Read(ref _startedTicks);
        long grace = awaitingFirstFrame ? RecoveryFirstFrameTimeoutMs : StartupGraceMs;
        if (sinceStart <= grace)
            return false;

        return stalledMs > StallThresholdMs;
    }

    /// <summary>
    /// Restarts this source's capture after a stall. Never throws out (the timer
    /// runs on a pool thread and must not take the process down); every failure is
    /// logged and retried with backoff.
    /// </summary>
    private async Task RecoverFromStallAsync()
    {
        try
        {
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed || !_captureDesired)
                    return;
                if (!IsStalled(Environment.TickCount64, out long stalledMs))
                    return;

                if (Interlocked.Exchange(ref _stallDiagLogged, 1) == 0)
                    AppLog.Write($"Source({DisplayName}): stall diagnostics - readerMode=Realtime, mediaCaptureNull={_capture is null}, gcHeapMB={GC.GetTotalMemory(false) / 1048576}");

                AppLog.Write($"Source({DisplayName}): STALL - no frame for {stalledMs / 1000.0:0.0}s (frames={Interlocked.Read(ref _framesTotal)}, nulls={Volatile.Read(ref _nullFrames)}, recoveries={Interlocked.Read(ref _stallRecoveries)}); restarting capture");

                // Escalating backoff (2s, 4s, 8s ... capped at 30s) so a camera
                // that cannot be reopened is retried, not hammered.
                long backoff = Math.Min(
                    Math.Max(Interlocked.Read(ref _recoveryBackoffMs) * 2, MinRecoveryBackoffMs),
                    MaxRecoveryBackoffMs);
                Interlocked.Exchange(ref _recoveryBackoffMs, backoff);
                Volatile.Write(ref _nextRecoveryTicks, Environment.TickCount64 + backoff);

                await StopReaderAsync().ConfigureAwait(false);
                // Let the USB device / Frame Server release the handle before reopening.
                await Task.Delay(RecoverySettleMs).ConfigureAwait(false);

                _captureDesired = true;
                Interlocked.Exchange(ref _recoveryFirstFramePending, 1);
                Volatile.Write(ref _recoveryRestartTicks, Environment.TickCount64);

                bool ok;
                try
                {
                    // Fresh enumeration: a re-plugged device is a new group object.
                    ok = await StartCoreAsync(OutputWidth, OutputHeight, _lastCaptureWidth, _lastCaptureHeight, null)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    ok = false;
                    AppLog.Write($"Source({DisplayName}): STALL recovery restart threw {ex.GetType().Name}: {ex.Message}");
                }

                if (!ok)
                {
                    // Keep "awaiting first frame" set so the watchdog retries after
                    // the backoff instead of giving up on a dead camera.
                    AppLog.Write($"Source({DisplayName}): STALL recovery failed; next attempt in {backoff / 1000}s");
                }
            }
            finally
            {
                _lifecycle.Release();
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"Source({DisplayName}): STALL recovery threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _recovering, 0);
        }
    }

    /// <summary>
    /// Tears down the reader and MediaCapture so a fresh pair can be created.
    /// Logs and swallows failures: a half-dead reader must not abort recovery.
    /// </summary>
    private async Task StopReaderAsync()
    {
        var reader = _reader;
        var capture = _capture;
        _reader = null;
        _capture = null;
        Running = false;

        if (reader != null)
        {
            try { reader.FrameArrived -= OnFrameArrived; } catch { }
            try { await reader.StopAsync(); }
            catch (Exception ex) { AppLog.Write($"Source({DisplayName}): STALL stop reader threw {ex.GetType().Name}: {ex.Message}"); }
            try { reader.Dispose(); }
            catch (Exception ex) { AppLog.Write($"Source({DisplayName}): STALL dispose reader threw {ex.GetType().Name}: {ex.Message}"); }
        }

        if (capture != null)
        {
            try { capture.Dispose(); }
            catch (Exception ex) { AppLog.Write($"Source({DisplayName}): STALL dispose MediaCapture threw {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    /// <summary>First frame after a recovery restart: clear backoff and log it.</summary>
    private void OnRecoveredFirstFrame()
    {
        Interlocked.Exchange(ref _recoveryBackoffMs, 0);
        Volatile.Write(ref _nextRecoveryTicks, 0);
        long took = Math.Max(0, Environment.TickCount64 - Volatile.Read(ref _recoveryRestartTicks));
        Volatile.Write(ref _recoveryRestartTicks, 0);
        long recoveries = Interlocked.Increment(ref _stallRecoveries);
        AppLog.Write($"Source({DisplayName}): STALL recovered - first frame after {took}ms (recoveries={recoveries})");
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref _lastArrivalQpc, t0);
        // Liveness for the stall watchdog: no allocation, no logging on the hot path.
        Interlocked.Increment(ref _framesTotal);
        Volatile.Write(ref _lastFrameTicks, Environment.TickCount64);
        if (Volatile.Read(ref _recoveryFirstFramePending) == 1 &&
            Interlocked.Exchange(ref _recoveryFirstFramePending, 0) == 1)
            OnRecoveredFirstFrame();

        using var frame = sender.TryAcquireLatestFrame();
        // Disposing the SoftwareBitmap is REQUIRED, not optional: accessing it creates a
        // strong reference the runtime counts against a per-process allocation cap, and
        // Microsoft documents that exceeding that cap makes "the flow of new frames stop"
        // until the GC happens to free some. That is exactly the observed symptom (a slow
        // trickle of frames, both cameras at once, temporary recovery after a restart).
        using var sw = frame?.VideoMediaFrame?.SoftwareBitmap;
        if (sw == null)
        {
            Interlocked.Increment(ref _droppedFrames);
            if ((Interlocked.Increment(ref _nullFrames) & 0x3F) == 1)
                AppLog.Write($"Source({DisplayName}): null SoftwareBitmap x{_nullFrames}");
            return;
        }
        if (Interlocked.Exchange(ref _firstFrameLogged, 1) == 0)
            AppLog.Write($"Source({DisplayName}): first frame {sw.PixelWidth}x{sw.PixelHeight} {sw.BitmapPixelFormat}");
        Interlocked.Increment(ref _framesArrived);
        _fpsWindowFrames++;
        // Time-based window: the old code only emitted once 75 frames had piled up, so a
        // stalled camera logged "75 frames in 81.0s" and hid the stall for minutes.
        double secs = (DateTime.UtcNow - _fpsWindowStart).TotalSeconds;
        if (secs >= 4.0)
        {
            AppLog.Write($"Source({DisplayName}): {_fpsWindowFrames} frames in {secs:0.0}s = {_fpsWindowFrames / secs:0.0} fps");
            _fpsWindowStart = DateTime.UtcNow;
            _fpsWindowFrames = 0;
        }

        try
        {
            ConvertToNv12(sw);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _droppedFrames);
            if (Interlocked.Increment(ref _convertErrors) <= 5)
                AppLog.Write($"Source({DisplayName}): ConvertToNv12 EX #{_convertErrors} {ex.GetType().Name}: {ex.Message}");
        }
        AddProcessTime(t0);
    }

    private unsafe void ConvertToNv12(SoftwareBitmap sw)
    {
        using var buffer = sw.LockBuffer(BitmapBufferAccessMode.Read);
        var plane = buffer.GetPlaneDescription(0);
        using var reference = buffer.CreateReference();
        var byteAccess = reference.As<IMemoryBufferByteAccess>();
        byteAccess.GetBuffer(out byte* data, out uint _);

        byte* src = data + plane.StartIndex;
        int w = (int)plane.Width;
        int h = (int)plane.Height;
        int stride = (int)plane.Stride;

        byte[] nv12;
        byte[] preview;
        lock (_lock)
        {
            nv12 = _nv12;
            preview = _preview;
        }

        // Read once per frame: toggling previews mid-stream must not tear a frame.
        bool previews = PreviewEnabled;

        if (sw.BitmapPixelFormat == BitmapPixelFormat.Nv12)
        {
            // NV12 in → tight NV12 out (stride-aware copy). The Frame Server
            // already decoded/resized, so no color conversion on the hot path.
            // Published for EVERY camera: each source continuously drives its own
            // passthrough virtual camera, not just the active one.
            if (w == OutputWidth && h == OutputHeight)
            {
                CopyNv12Tight(src, w, h, stride, nv12);
                CountGoodFrame();
                FrameReady?.Invoke(nv12);
            }
            else if (w == _readW && h == _readH)
            {
                // Letterbox / crop: one row-wise copy of the visible window into
                // the reused canvas. The bars are painted once, not per frame.
                if (!_canvasCleared)
                {
                    FillBlack(nv12, OutputWidth, OutputHeight);
                    _canvasCleared = true;
                }
                CopyNv12Window(src, stride, h, nv12, OutputWidth, OutputHeight);
                CountGoodFrame();
                FrameReady?.Invoke(nv12);
            }
            else
            {
                // Should not happen (the reader was asked for this size).
                if ((Interlocked.Increment(ref _droppedFrames) & 0x3F) == 1)
                    AppLog.Write($"Source({DisplayName}): unexpected frame size {w}x{h} (expected {_readW}x{_readH}); frame dropped");
            }
            if (previews)
                ColorConverter.Nv12ToBgra8Downscale(src, w, h, stride, preview, PreviewWidth, PreviewHeight);
        }
        else
        {
            // Fallback: reader delivered Bgra8 (rare). Legacy conversion path.
            ColorConverter.Bgra8ToNv12(src, w, h, stride, nv12, OutputWidth, OutputHeight);
            CountGoodFrame();
            FrameReady?.Invoke(nv12);
            if (previews)
                ColorConverter.Bgra8Downscale(src, w, h, stride, preview, PreviewWidth, PreviewHeight);
        }

        if (previews)
            PreviewReady?.Invoke(preview, PreviewWidth, PreviewHeight);
    }

    /// <summary>Black NV12 (Y=16, UV=128: video range).</summary>
    internal static void FillBlack(byte[] nv12, int w, int h)
    {
        int y = w * h;
        Array.Fill(nv12, (byte)16, 0, y);
        Array.Fill(nv12, (byte)128, y, Math.Min(nv12.Length - y, w * h / 2));
    }

    /// <summary>
    /// Copies the visible window of a <c>_readW x srcH</c> NV12 frame into the
    /// output canvas (letterbox: whole frame at an offset; crop: the centre).
    /// </summary>
    private unsafe void CopyNv12Window(byte* src, int srcStride, int srcH, byte[] dst, int outW, int outH)
    {
        int cw = _copyW, ch = _copyH;
        fixed (byte* d = dst)
        {
            for (int y = 0; y < ch; y++)
                Buffer.MemoryCopy(src + (_srcY + y) * srcStride + _srcX, d + (_dstY + y) * outW + _dstX, cw, cw);

            byte* suv = src + srcStride * srcH;
            byte* duv = d + outW * outH;
            for (int y = 0; y < ch / 2; y++)
                Buffer.MemoryCopy(suv + (_srcY / 2 + y) * srcStride + _srcX, duv + (_dstY / 2 + y) * outW + _dstX, cw, cw);
        }
    }

    private static unsafe void CopyNv12Tight(byte* src, int w, int h, int srcStride, byte[] dst)
    {
        fixed (byte* d = dst)
        {
            if (srcStride == w)
            {
                // Tight source: Y and UV planes are contiguous, so one copy covers all.
                int total = w * h + w * (h / 2);
                if (total <= dst.Length)
                    Buffer.MemoryCopy(src, d, dst.Length, total);
                return;
            }

            for (int y = 0; y < h; y++)
                Buffer.MemoryCopy(src + y * srcStride, d + y * w, w, w);

            byte* uv = src + srcStride * h;
            byte* duv = d + w * h;
            for (int y = 0; y < h / 2; y++)
                Buffer.MemoryCopy(uv + y * srcStride, duv + y * w, w, w);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Serialized with StartAsync and the watchdog recovery so a restart cannot
        // resurrect a source that is being disposed.
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            _captureDesired = false;
            Interlocked.Exchange(ref _recoveryFirstFramePending, 0);
            StopWatchdog();
            await DisposeCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async ValueTask DisposeCoreAsync()
    {
        if (_mf != null)
        {
            var mf = _mf;
            _mf = null;
            mf.FrameReady -= OnMfFrameReady;
            AppLog.Write($"Source.Dispose({DisplayName}): stopping MF-MJPEG");
            try { mf.Dispose(); } catch (Exception ex) { AppLog.Write($"Source.Dispose({DisplayName}): MF-MJPEG dispose failed {ex.GetType().Name}: {ex.Message}"); }
            AppLog.Write($"Source.Dispose({DisplayName}): MF-MJPEG stopped");
        }

        try
        {
            if (_reader != null)
            {
                _reader.FrameArrived -= OnFrameArrived;
                AppLog.Write($"Source.Dispose({DisplayName}): StopAsync");
                await _reader.StopAsync();
                AppLog.Write($"Source.Dispose({DisplayName}): StopAsync done");
            }
        }
        catch { }

        _reader = null;
        if (_capture != null)
        {
            AppLog.Write($"Source.Dispose({DisplayName}): MediaCapture.Dispose");
            try { _capture.Failed -= OnCaptureFailed; } catch { }
            try { _capture.Dispose(); } catch (Exception ex) { AppLog.Write($"Source.Dispose({DisplayName}): MediaCapture.Dispose threw {ex.Message}"); }
            _capture = null;
        }
        Running = false;
    }
}
