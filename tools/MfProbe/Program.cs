using System.Diagnostics;
using WebcamSwitcher.Core;
using WebcamSwitcher.MfProbe;
using Windows.Devices.Enumeration;

// Diagnostic harness for the Media Foundation MJPEG/NV12 path. NOT part of the
// shipped app: it opens the physical cameras through MfMjpegSource's public
// entry point and reports, per device, whether NV12 at the destination size was
// negotiated, the buffer stride, the sample byte count and the Y-plane mean
// luma / frame rate actually delivered.
//
// Usage:
//   WebcamSwitcher.MfProbe [width] [height] [fps] [seconds] [name-filter]
//   WebcamSwitcher.MfProbe --legacy ...   (same args; forces the decoder-only path)
//   WebcamSwitcher.MfProbe --copytest     (no camera: verifies NV12 plane copying
//                                          for tight, padded and bottom-up strides)
// Defaults: 1280 720 30 3 (no filter)

// Never write into the running app's rotated log: the visibility race test
// alone logs ~150k lines in 300 ms and would rotate the real history away.
AppLog.PathOverride = Path.Combine(Path.GetTempPath(), "WebcamSwitcher.MfProbe.log");

var argList = args.ToList();
bool legacy = argList.Remove("--legacy");
bool copyTest = argList.Remove("--copytest");
bool pubTest = argList.Remove("--pubtest");
bool convBench = argList.Remove("--convbench");
bool previewBench = argList.Remove("--previewbench");
bool visibilityTest = argList.Remove("--visibilitytest");
bool delayTest = argList.Remove("--delaytest");
bool abuseTest = argList.Remove("--abuse");
if (argList.Remove("--abusedebug"))
    PublisherAbuse.DebugOnly = 1;
if (argList.Remove("--abusedebug2"))
    PublisherAbuse.DebugOnly = 2;
if (PublisherAbuse.DebugOnly != 0) abuseTest = true;
int fileIdx = argList.IndexOf("--file");
string? filePath = fileIdx >= 0 && fileIdx + 1 < argList.Count ? argList[fileIdx + 1] : null;
if (fileIdx >= 0)
{
    argList.RemoveRange(fileIdx, Math.Min(2, argList.Count - fileIdx));
}
args = argList.ToArray();

if (copyTest)
    return CopyPathSelfTest.Run();

if (pubTest)
    return PublisherSelfTest.Run();

if (convBench)
    return ConvBench.Run();

if (previewBench)
    return PreviewBench.Run();

if (visibilityTest)
    return VisibilitySelfTest.Run();

if (delayTest)
    return DelaySelfTest.Run();

if (abuseTest)
    return PublisherAbuse.Run();

if (filePath != null)
{
    int fw = args.Length > 0 && int.TryParse(args[0], out int fw0) ? fw0 : 1280;
    int fh = args.Length > 1 && int.TryParse(args[1], out int fh0) ? fh0 : 720;
    int ffps = args.Length > 2 && int.TryParse(args[2], out int ff0) ? ff0 : 30;
    int fcount = args.Length > 3 && int.TryParse(args[3], out int fc0) ? fc0 : 120;
    return FileProbe.Run(filePath, fw, fh, ffps, fcount, advanced: !legacy);
}

int wantW = args.Length > 0 && int.TryParse(args[0], out int w0) ? w0 : 1280;
int wantH = args.Length > 1 && int.TryParse(args[1], out int h0) ? h0 : 720;
int wantFps = args.Length > 2 && int.TryParse(args[2], out int f0) ? f0 : 30;
int seconds = args.Length > 3 && int.TryParse(args[3], out int s0) ? s0 : 3;
string? filter = args.Length > 4 ? args[4] : null;

Console.WriteLine($"MfProbe: target {wantW}x{wantH}@{wantFps}, {seconds}s per device, advanced={!legacy}");

var all = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);

// Physical cameras only: the app's own virtual cameras are served by this very
// pipeline, so probing them would deadlock on the pipe instead of testing MF.
var devices = all
    .Where(d => d.Name.Contains("WebcamSwitcher", StringComparison.OrdinalIgnoreCase) == false)
    .Where(d => filter == null || d.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
    .ToList();

Console.WriteLine($"devices: {devices.Count} (of {all.Count} video capture devices)");
foreach (var d in devices)
    Console.WriteLine($"  - {d.Name} [{d.Id}]");

if (devices.Count == 0)
{
    Console.WriteLine("NO DEVICES: nothing to probe");
    return 2;
}

int exit = 0;
foreach (var dev in devices)
{
    Console.WriteLine();
    Console.WriteLine($"=== {dev.Name}");

    using var src = new MfMjpegSource(dev.Id, dev.Name, wantW, wantH, wantFps)
    {
        PreferAdvancedVideoProcessing = !legacy
    };

    long frames = 0;
    long lumaSum = 0;
    long lumaFrames = 0;
    var done = new TaskCompletionSource();
    src.FrameReady += frame =>
    {
        Interlocked.Increment(ref frames);

        // Mean luma of the first Nv12Size/w*h bytes (the Y plane, tight stride).
        int ySize = wantW * wantH;
        if (frame.Length < ySize)
            return;
        long sum = 0;
        for (int i = 0; i < ySize; i++)
            sum += frame[i];
        Interlocked.Add(ref lumaSum, sum / ySize);
        Interlocked.Increment(ref lumaFrames);
    };

    var sw = Stopwatch.StartNew();
    bool started = src.Start();
    if (!started)
    {
        Console.WriteLine("  Start(): FALSE -> device source or NV12 negotiation failed (see %TEMP%\\WebcamSwitcher.apply.log)");
        exit = 1;
        continue;
    }

    await Task.Delay(TimeSpan.FromSeconds(seconds));
    sw.Stop();
    src.Dispose();

    long f = Interlocked.Read(ref frames);
    long lf = Interlocked.Read(ref lumaFrames);
    double fps = sw.Elapsed.TotalSeconds > 0 ? f / sw.Elapsed.TotalSeconds : 0;
    double mean = lf > 0 ? Interlocked.Read(ref lumaSum) / (double)lf : double.NaN;
    int expected = wantW * wantH * 3 / 2;

    Console.WriteLine($"  Start():                TRUE");
    Console.WriteLine($"  SetCurrentMediaType NV12 {wantW}x{wantH}: SUCCEEDED");
    Console.WriteLine($"  negotiation path:       {src.NegotiationPath} (advanced={src.UsesAdvancedVideoProcessing})");
    Console.WriteLine($"  MF_MT_DEFAULT_STRIDE:   {src.NegotiatedStride}");
    Console.WriteLine($"  sample bytes:           {src.LastSampleBytes} (expected {expected}, delta {src.LastSampleBytes - expected})");
    Console.WriteLine($"  frames:                 {f} in {sw.Elapsed.TotalSeconds:F2}s = {fps:F1} fps");
    Console.WriteLine($"  mean Y luma:            {mean:F1}");

    bool ok = f > 0 && Math.Abs(src.LastSampleBytes - expected) <= src.NegotiatedStride;
    if (!ok)
        exit = 1;
}

return exit;
