using WebcamSwitcher.Core;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

// ---------------------------------------------------------------------------
// Role integration test (real cameras, real virtual cameras).
//
//   CaptureTest [--seconds N] [--latency] [--roles K]
//   CaptureTest consume <name>          read from an existing virtual camera
//
// Registers one role per physical camera present (CAM-01..), starts the engine
// and the role virtual cameras exactly like the app, then opens every CAM-xx as
// a consumer and checks that (a) it delivers 1280x720, (b) it is served by its
// OWN role pipe (no cross-routing), (c) its frame rate, and optionally (d) the
// added latency from the capture callback to the consumer, decoded from the
// LatencyProbe barcode. --roles K adds unassigned extra roles (diagnostic
// picture only) to exercise more simultaneous virtual cameras.
// ---------------------------------------------------------------------------

if (args.Length >= 2 && args[0] == "consume")
{
    var r = await Consume(args[1], 3, false, () => "n/a");
    Console.WriteLine($"CONSUME '{args[1]}' -> {(r == null ? "NO FRAME" : $"{r.Value.w}x{r.Value.h} {r.Value.fmt} {r.Value.fps:0.0} fps")}");
    return r != null ? 0 : 1;
}

int seconds = 10;
bool latency = args.Contains("--latency");
int extraRoles = 0;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--seconds") int.TryParse(args[i + 1], out seconds);
    if (args[i] == "--roles") int.TryParse(args[i + 1], out extraRoles);
}

Console.WriteLine("=== WebcamSwitcher role CaptureTest ===");
var physical = await CameraEngine.EnumeratePhysicalCamerasAsync();
foreach (var c in physical) Console.WriteLine($"  physical: '{c.FriendlyName}' {CameraRoles.DeviceIdTail(c.DeviceId)}");
if (physical.Count == 0) { Console.WriteLine("RESULT: no physical camera"); return 1; }

var config = new AppConfig { Width = 1280, Height = 720, Fps = 30 };
foreach (var p in physical)
{
    var role = CameraRoles.CreateRole(CameraRoles.NextFreeRole(config.Cameras));
    role.DeviceId = p.DeviceId;
    role.PhysicalFriendlyName = p.FriendlyName;
    config.Cameras.Add(role);
}
for (int i = 0; i < extraRoles; i++)
    config.Cameras.Add(CameraRoles.CreateRole(CameraRoles.NextFreeRole(config.Cameras)));
config.Normalize();
SourceFormatFile.Write(config.Width, config.Height, config.Fps);
RoleChannel.LatencyProbeEnabled = latency;

using var presence = new DevicePresenceMonitor();
presence.Start();
await presence.WaitReadyAsync(3000);

using var vcam = new VirtualCameraService();
vcam.Prepare(config.Cameras);
foreach (var c in config.Cameras)
    Console.WriteLine($"  vcam {c.Role} '{c.VirtualCameraName}' start={vcam.StartKey(c.VirtualCameraId)}");

var engine = new CameraEngine(new FramePublisher(Protocol.PipeName, 1280, 720, 30), presence);
await engine.StartAsync(config, k => vcam.IsKeyRunning(k));
for (int i = 0; i < 60 && engine.Snapshot().Any(r => r.DeviceId != null && r.CameraStatus != RoleStatus.Connected); i++)
    await Task.Delay(250);
foreach (var r in engine.Snapshot())
    Console.WriteLine($"  {r.Role}: {RoleStatusText.Label(r.Status)} in {r.InputWidth}x{r.InputHeight} {r.InputPixelFormat} @{r.InputNominalFps:0.#} ({r.Pipeline}) {r.Detail}");

int failures = 0;
foreach (var role in config.Cameras)
{
    var channels = engine.Channels;
    var mine = channels.First(c => c.Key == role.VirtualCameraId);
    var res = await Consume(role.VirtualCameraName, seconds, latency, () =>
        $"own={mine.Publisher.ClientCount} others={channels.Where(c => c != mine).Sum(c => c.Publisher.ClientCount)}");
    if (res == null) { Console.WriteLine($"ROLE {role.Role}: FAIL (no frame)"); failures++; continue; }
    var (w, h, fmt, frames, fps, routing, lat) = res.Value;
    bool routed = routing.StartsWith("own=1 ") && routing.EndsWith("others=0");
    bool ok = w == 1280 && h == 720 && routed && frames > 0;
    if (!ok) failures++;
    Console.WriteLine($"ROLE {role.Role}: {(ok ? "OK" : "FAIL")} {w}x{h} {fmt} frames={frames} fps={fps:0.0} routing[{routing}]{lat}");
}

foreach (var r in engine.Snapshot())
    Console.WriteLine($"  final {r.Role}: {RoleStatusText.Label(r.Status)} recv={r.FramesReceived} drop={r.DroppedFrames} in={r.InputFps:0.0}fps proc={r.ProcessMicros / 1000:0.00}ms");
await engine.DisposeAsync();
vcam.Stop();
Console.WriteLine(failures == 0 ? "RESULT: PASS" : $"RESULT: FAIL ({failures})");
AppLog.Flush();
return failures == 0 ? 0 : 1;

// Opens the virtual camera with this exact name (Windows may append
// " (Windows Virtual Camera)"), reads frames for `secs`, and samples routing
// while open. With `lat`, decodes the probe barcode and reports added latency.
static async Task<(int w, int h, string fmt, int frames, double fps, string routing, string lat)?> Consume(
    string name, int secs, bool lat, Func<string> routing)
{
    var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
    var dev = devices.FirstOrDefault(d => d.Name == name || d.Name.StartsWith(name + " (", StringComparison.Ordinal));
    if (dev == null) { Console.WriteLine($"  [consume] '{name}' not enumerated"); return null; }
    var groups = await MediaFrameSourceGroup.FindAllAsync();
    var group = groups.FirstOrDefault(g => g.Id == dev.Id);
    if (group == null) { Console.WriteLine($"  [consume] no group for '{name}'"); return null; }
    Console.WriteLine($"  [consume] '{dev.Name}' id={dev.Id}");

    var capture = new MediaCapture();
    try
    {
        await capture.InitializeAsync(new MediaCaptureInitializationSettings
        {
            SourceGroup = group,
            SharingMode = MediaCaptureSharingMode.ExclusiveControl,
            StreamingCaptureMode = StreamingCaptureMode.Video,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu
        });
        var color = capture.FrameSources.Values.First(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
        var reader = await capture.CreateFrameReaderAsync(color, MediaEncodingSubtypes.Nv12);
        reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
        int frames = 0, w = 0, h = 0;
        string fmt = "";
        var lats = new List<double>();
        reader.FrameArrived += (s, e) =>
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            using var f = s.TryAcquireLatestFrame();
            using var sw = f?.VideoMediaFrame?.SoftwareBitmap;
            if (sw == null) return;
            Interlocked.Increment(ref frames);
            w = sw.PixelWidth; h = sw.PixelHeight; fmt = sw.BitmapPixelFormat.ToString();
            if (!lat) return;
            double ms = ReadLatencyMs(sw, now);
            if (ms > 0 && ms < 2000) lock (lats) lats.Add(ms);
        };
        await reader.StartAsync();
        await Task.Delay(1500);
        string route = routing();
        int startFrames = frames;
        var t0 = DateTime.UtcNow;
        await Task.Delay(TimeSpan.FromSeconds(secs));
        double fps = (frames - startFrames) / (DateTime.UtcNow - t0).TotalSeconds;
        await reader.StopAsync();
        reader.Dispose();
        string latText = "";
        if (lat && lats.Count > 0)
        {
            lats.Sort();
            double P(double q) => lats[Math.Min(lats.Count - 1, (int)(q * lats.Count))];
            latText = $" latency ms: min={lats[0]:0.0} avg={lats.Average():0.0} p50={P(0.5):0.0} p95={P(0.95):0.0} max={lats[^1]:0.0} " +
                      $"(n={lats.Count}, avg {lats.Average() / 33.333:0.00} frames @30fps)";
        }
        else if (lat)
            latText = " latency: no stamped frames decoded";
        return (w, h, fmt, frames, fps, route, latText);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [consume] '{name}' failed {ex.GetType().Name}: {ex.Message}");
        return null;
    }
    finally { capture.Dispose(); }
}

static unsafe double ReadLatencyMs(Windows.Graphics.Imaging.SoftwareBitmap sw, long now)
{
    using var buf = sw.LockBuffer(Windows.Graphics.Imaging.BitmapBufferAccessMode.Read);
    var plane = buf.GetPlaneDescription(0);
    using var reference = buf.CreateReference();
    WinRT.CastExtensions.As<IMemoryBufferByteAccess>(reference).GetBuffer(out byte* data, out uint _);
    var span = new ReadOnlySpan<byte>(data + plane.StartIndex, plane.Stride * LatencyProbe.CellHeight);
    long stamp = LatencyProbe.Read(span, plane.Stride);
    return (now - stamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
}

[System.Runtime.InteropServices.ComImport]
[System.Runtime.InteropServices.Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
[System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
unsafe interface IMemoryBufferByteAccess
{
    void GetBuffer(out byte* buffer, out uint capacity);
}
