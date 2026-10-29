using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebcamSwitcher.Core;

/// <summary>
/// One Camera Role: a fixed, named virtual camera (e.g. <c>CAM-01</c>) bound to
/// exactly one physical camera by its Windows device ID.
///
/// The role, not the position in <see cref="AppConfig.Cameras"/>, is the
/// identity: the virtual camera name, its pipe and its persistent ID all derive
/// from the role, so reordering the list or a different device enumeration
/// order can never swap two feeds.
/// </summary>
public class CameraConfig
{
    /// <summary>Camera Role, e.g. <c>CAM-01</c>. Unique across the config.</summary>
    public string Role { get; set; } = "";

    /// <summary>
    /// Friendly name the virtual camera is registered with (default: the role).
    /// Windows may append " (Windows Virtual Camera)" when listing it. Unique.
    /// </summary>
    public string VirtualCameraName { get; set; } = "";

    /// <summary>
    /// Persistent identifier of this role's virtual camera (a GUID, "N" format).
    /// Generated once and never reused by another role; the role's frame pipe
    /// is named after it, so it survives renames of the role display text.
    /// </summary>
    public string VirtualCameraId { get; set; } = "";

    /// <summary>
    /// Windows device interface ID (symbolic link) of the physical camera,
    /// e.g. <c>\\?\USB#VID_...#6&amp;...&amp;0&amp;0000#{e5323777-...}\GLOBAL</c>.
    /// Null/empty means the role is not assigned. Never matched by name or
    /// position: a missing ID is reported as MISSING, never replaced.
    /// </summary>
    public string? DeviceId { get; set; }

    /// <summary>Friendly name the device reported when it was registered (display only).</summary>
    public string PhysicalFriendlyName { get; set; } = "";

    /// <summary>
    /// Legacy (schema 1) name of the physical camera. Read for migration into
    /// <see cref="PhysicalFriendlyName"/> and never written again.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FriendlyName { get; set; }

    /// <summary>
    /// Optional capture size. Null = Auto: the supported size that best matches
    /// the output aspect, then area.
    /// </summary>
    public int? CaptureWidth { get; set; }
    public int? CaptureHeight { get; set; }

    /// <summary>
    /// Optional capture frame rate request. Null = the output frame rate. The
    /// physical camera still delivers whatever it can; this only steers which
    /// native mode is picked.
    /// </summary>
    public int? CaptureFps { get; set; }

    /// <summary>
    /// Capture-format preference: "Auto" (default), "Nv12" or "Mjpg". Auto keeps
    /// the uncompressed stream when it already runs at full rate and never
    /// assumes the camera offers MJPEG.
    /// </summary>
    public string CaptureFormat { get; set; } = "Auto";

    /// <summary>
    /// How a capture whose aspect differs from the output is fitted:
    /// "Letterbox" (default, keeps the whole picture), "Crop" (fills the frame)
    /// or "Stretch".
    /// </summary>
    public string ScaleMode { get; set; } = ScaleModes.Letterbox;

    /// <summary>Whether this role's virtual camera is registered. Default true.</summary>
    public bool VirtualCameraEnabled { get; set; } = true;

    /// <summary>Extra video delay for this role in ms (0 = delay stage bypassed).</summary>
    public int DelayMs { get; set; }

    /// <summary>Unknown properties from a newer/other version, kept on save.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>Display name of the physical camera, whichever schema it came from.</summary>
    [JsonIgnore]
    public string DisplayDeviceName =>
        !string.IsNullOrWhiteSpace(PhysicalFriendlyName) ? PhysicalFriendlyName : FriendlyName ?? "";

    [JsonIgnore]
    public bool IsAssigned => !string.IsNullOrWhiteSpace(DeviceId);

    public CameraConfig Clone()
    {
        var c = (CameraConfig)MemberwiseClone();
        c.Extra = Extra == null ? null : new Dictionary<string, JsonElement>(Extra);
        return c;
    }
}

public static class ScaleModes
{
    public const string Letterbox = "Letterbox";
    public const string Crop = "Crop";
    public const string Stretch = "Stretch";

    public static readonly string[] All = { Letterbox, Crop, Stretch };

    public static string Normalize(string? value)
    {
        foreach (var m in All)
            if (string.Equals(m, value?.Trim(), StringComparison.OrdinalIgnoreCase))
                return m;
        return Letterbox;
    }
}

/// <summary>Reconnect policy for a physical camera that disappeared or failed to open.</summary>
public class ReconnectConfig
{
    /// <summary>Delay between reconnect attempts, ms.</summary>
    public int RetryIntervalMs { get; set; } = 2000;

    /// <summary>Attempts before giving up (ignored when <see cref="RetryForever"/>).</summary>
    public int RetryCount { get; set; } = 30;

    /// <summary>Keep retrying for as long as the app runs. Default true (exhibition use).</summary>
    public bool RetryForever { get; set; } = true;

    /// <summary>
    /// At launch, wait up to this long for every assigned device to enumerate
    /// before opening cameras (USB devices can appear late after a cold boot).
    /// The virtual cameras are registered immediately and show a diagnostic
    /// picture meanwhile.
    /// </summary>
    public int StartupWaitMs { get; set; } = 15000;

    /// <summary>No frame for this long marks a connected camera NO SIGNAL.</summary>
    public int NoSignalTimeoutMs { get; set; } = 2000;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public void Normalize()
    {
        RetryIntervalMs = Math.Clamp(RetryIntervalMs, 250, 600_000);
        RetryCount = Math.Clamp(RetryCount, 0, 1_000_000);
        StartupWaitMs = Math.Clamp(StartupWaitMs, 0, 600_000);
        NoSignalTimeoutMs = Math.Clamp(NoSignalTimeoutMs, 500, 60_000);
    }
}

public class AppConfig
{
    /// <summary>Current configuration schema. 1 = original WebcamSwitcher (no field).</summary>
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public int Width { get; set; } = Protocol.DefaultWidth;
    public int Height { get; set; } = Protocol.DefaultHeight;
    public int Fps { get; set; } = Protocol.DefaultFps;
    public List<CameraConfig> Cameras { get; set; } = new();
    public int ActiveIndex { get; set; } = 0;

    public string[]? Hotkeys { get; set; }
    public string? RotateHotkey { get; set; }

    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }

    /// <summary>Legacy master switch, kept so older files load. Not consulted.</summary>
    public bool VirtualCameraOnLaunch { get; set; }

    /// <summary>
    /// Whether the switcher virtual camera ("WebcamSwitcher Virtual Camera") is
    /// created. Default false: this build is centred on the per-role cameras.
    /// </summary>
    public bool SwitcherVirtualCameraEnabled { get; set; }

    /// <summary>Whether live camera thumbnails are rendered (capture is unaffected).</summary>
    public bool PreviewsEnabled { get; set; } = true;

    /// <summary>Whether cameras are captured at launch ("auto-start capture").</summary>
    public bool CaptureEnabled { get; set; } = true;

    /// <summary>
    /// Register the enabled role virtual cameras at launch. Default true. When
    /// false nothing is registered until the user switches a camera on.
    /// </summary>
    public bool AutoRegisterVirtualCameras { get; set; } = true;

    /// <summary>
    /// Ask Windows to relaunch the app after a crash or hang
    /// (RegisterApplicationRestart; only after it has run for 60 s).
    /// </summary>
    public bool RestartOnCrash { get; set; } = true;

    public bool PreferSmooth30Fps { get; set; }

    public int CommonDelayMs { get; set; }

    public ReconnectConfig Reconnect { get; set; } = new();

    /// <summary>Seconds between performance log lines (CPU, memory, per-role fps). 0 = off.</summary>
    public int PerformanceLogIntervalSeconds { get; set; } = 60;

    /// <summary>Rotated log files kept (each up to <see cref="LogMaxFileMB"/>).</summary>
    public int LogMaxFiles { get; set; } = 10;

    /// <summary>
    /// TEST ONLY: stamps each frame's capture time as a barcode in the top 16
    /// rows so tools/CaptureTest can measure added latency. Visible in the
    /// picture - never enable in production.
    /// </summary>
    public bool LatencyProbe { get; set; }
    public int LogMaxFileMB { get; set; } = 5;

    /// <summary>Unknown top-level properties, kept on save.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public const int MaxDelayMs = 2000;

    /// <summary>
    /// Clamps/repairs values so they are always valid before use. Never renames,
    /// reorders or reassigns roles: duplicates are reported by
    /// <see cref="CameraRoles.Validate"/> instead of being silently "fixed".
    /// </summary>
    public void Normalize()
    {
        Width = ClampEven(Width, Protocol.DefaultWidth);
        Height = ClampEven(Height, Protocol.DefaultHeight);
        Fps = Math.Clamp(Fps, 1, 60);

        Cameras ??= new();
        Reconnect ??= new();
        Reconnect.Normalize();
        PerformanceLogIntervalSeconds = Math.Clamp(PerformanceLogIntervalSeconds, 0, 3600);
        LogMaxFiles = Math.Clamp(LogMaxFiles, 1, 100);
        LogMaxFileMB = Math.Clamp(LogMaxFileMB, 1, 100);

        CommonDelayMs = Math.Clamp(CommonDelayMs, 0, MaxDelayMs);
        foreach (var camera in Cameras)
        {
            camera.DelayMs = Math.Clamp(camera.DelayMs, 0, MaxDelayMs);
            camera.ScaleMode = ScaleModes.Normalize(camera.ScaleMode);
            if (string.IsNullOrWhiteSpace(camera.CaptureFormat))
                camera.CaptureFormat = "Auto";
            if (camera.CaptureFps is int f && (f < 1 || f > 120))
                camera.CaptureFps = null;
            if (camera.CaptureWidth is not > 0 || camera.CaptureHeight is not > 0)
            {
                camera.CaptureWidth = null;
                camera.CaptureHeight = null;
            }
        }

        int count = Cameras.Count;
        if (Hotkeys == null)
            Hotkeys = new string[count];
        else if (Hotkeys.Length != count)
        {
            var arr = new string[count];
            Array.Copy(Hotkeys, arr, Math.Min(Hotkeys.Length, count));
            Hotkeys = arr;
        }

        ActiveIndex = Math.Clamp(ActiveIndex, 0, Math.Max(0, count - 1));
    }

    /// <summary>Deep copy through JSON (keeps unknown fields).</summary>
    public AppConfig Clone() =>
        JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(this))!;

    private static int ClampEven(int v, int def)
    {
        if (v < 160 || v > 3840)
            v = def;
        return v & ~1;
    }
}
