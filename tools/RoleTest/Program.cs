using System.Text.Json.Nodes;
using WebcamSwitcher.Core;

// Headless tests for the Camera Role layer. No cameras, no COM registration:
// the native virtual camera layer is faked and every file goes to a scratch
// directory, so this is safe to run anywhere (CI included).
//
//   WebcamSwitcher.RoleTest           -> exit code 0 on success
namespace WebcamSwitcher.RoleTest;

internal static class Program
{
    private static int _failures;

    private static int Main()
    {
        string scratch = Path.Combine(Path.GetTempPath(), "wsroletest-" + Environment.ProcessId);
        Directory.CreateDirectory(scratch);
        AppLog.PathOverride = Path.Combine(scratch, "test.log");
        try
        {
            Section("Role naming");
            RoleNaming();
            Section("Validation / duplicates");
            Validation();
            Section("Schema 1 migration");
            Migration(scratch);
            Section("Atomic save, backup, corrupt-file recovery, unknown fields");
            Persistence(scratch);
            Section("VirtualCameraService keyed by role");
            VcamService(scratch);
            Section("Fit geometry (letterbox / crop / stretch)");
            Geometry();
            Section("Diagnostic frame and latency probe");
            DiagnosticAndProbe();
        }
        catch (Exception ex)
        {
            Fail($"UNCAUGHT {ex}");
        }
        finally
        {
            AppLog.PathOverride = null;
            try { Directory.Delete(scratch, true); } catch { }
        }

        Console.WriteLine(_failures == 0 ? "RESULT: PASS" : $"RESULT: FAIL ({_failures})");
        return _failures == 0 ? 0 : 1;
    }

    private static void Section(string name) => Console.WriteLine($"\n== {name}");

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
        if (!ok)
            _failures++;
    }

    private static void Fail(string what)
    {
        Console.WriteLine($"  [FAIL] {what}");
        _failures++;
    }

    // ------------------------------------------------------------------

    private static void RoleNaming()
    {
        Check(CameraRoles.StandardRole(1) == "CAM-01", "1 -> CAM-01");
        Check(CameraRoles.StandardRole(11) == "CAM-11", "11 -> CAM-11");
        Check(CameraRoles.StandardRole(123) == "CAM-123", "no fixed maximum: 123 -> CAM-123");
        var list = new List<CameraConfig> { CameraRoles.CreateRole("CAM-01"), CameraRoles.CreateRole("CAM-03") };
        Check(CameraRoles.NextFreeRole(list) == "CAM-02", "next free role fills the gap (CAM-02)");
        var many = Enumerable.Range(1, 16).Select(i => CameraRoles.CreateRole(CameraRoles.StandardRole(i))).ToList();
        Check(CameraRoles.NextFreeRole(many) == "CAM-17", "16 roles -> next is CAM-17");
        Check(many.Select(c => c.VirtualCameraId).Distinct().Count() == 16, "every new role gets its own persistent ID");
        Check(CameraRoles.DeviceIdTail(@"\\?\USB#VID_0C45&PID_6D1E&MI_00#6&8ec54cd&0&0000#{e5323777-f976-4f5b-9b55-b94699c46e44}\GLOBAL")
              == "VID_0C45&PID_6D1E&MI_00 #6&8ec54cd&0&0000", "device ID tail shows VID/PID and the USB instance");
    }

    private static List<CameraConfig> TwoRoles()
    {
        var a = CameraRoles.CreateRole("CAM-01");
        a.DeviceId = @"\\?\USB#VID_1234&PID_0001#7&aaa&0&1#{x}\GLOBAL";
        var b = CameraRoles.CreateRole("CAM-02");
        b.DeviceId = @"\\?\USB#VID_1234&PID_0001#7&bbb&0&2#{x}\GLOBAL";
        return new() { a, b };
    }

    private static void Validation()
    {
        var ok = TwoRoles();
        Check(CameraRoles.Validate(ok).Count == 0, "two roles with distinct IDs validate");

        var sameDevice = TwoRoles();
        sameDevice[1].DeviceId = sameDevice[0].DeviceId;
        var issues = CameraRoles.Validate(sameDevice);
        Check(issues.Any(i => i.Field == nameof(CameraConfig.DeviceId)), "same physical device ID on CAM-01 and CAM-02 is rejected");

        var sameName = TwoRoles();
        sameName[1].VirtualCameraName = "cam-01"; // case-insensitive like Windows
        Check(CameraRoles.Validate(sameName).Any(i => i.Field == nameof(CameraConfig.VirtualCameraName)), "duplicate Virtual Camera Name rejected (case-insensitive)");

        var sameId = TwoRoles();
        sameId[1].VirtualCameraId = sameId[0].VirtualCameraId;
        Check(CameraRoles.Validate(sameId).Any(i => i.Field == nameof(CameraConfig.VirtualCameraId)), "same Virtual Camera ID on two roles rejected");

        var sameRole = TwoRoles();
        sameRole[1].Role = "CAM-01";
        Check(CameraRoles.Validate(sameRole).Any(i => i.Field == nameof(CameraConfig.Role)), "duplicate Camera Role rejected");

        var dup = CameraRoles.DuplicatedRoles(sameDevice);
        Check(dup.Contains("CAM-01") && dup.Contains("CAM-02"), "both roles of a duplicate are marked DUPLICATED");

        var bad = TwoRoles();
        bad[0].VirtualCameraName = "CAM\t01";
        Check(CameraRoles.Validate(bad).Any(), "a tab in the virtual camera name is rejected (vcams.ini is tab-separated)");
        bad[0].VirtualCameraName = VirtualCameraService.SwitcherName;
        Check(CameraRoles.Validate(bad).Any(), "a role cannot take the switcher's name");
    }

    private const string V1Json = """
    {
      "Width": 1920, "Height": 1080, "Fps": 30,
      "Cameras": [
        { "DeviceId": "\\\\?\\USB#VID_1#A#{x}\\GLOBAL", "FriendlyName": "USB Camera", "CaptureFormat": "Auto", "DelayMs": 40, "VirtualCameraEnabled": true },
        { "DeviceId": "\\\\?\\USB#VID_1#B#{x}\\GLOBAL", "FriendlyName": "USB Camera", "VirtualCameraEnabled": false }
      ],
      "ActiveIndex": 1,
      "Hotkeys": [ "Ctrl+Alt+1", "Ctrl+Alt+2" ],
      "SwitcherVirtualCameraEnabled": true,
      "SomeFutureSetting": { "x": 1 }
    }
    """;

    private static void Migration(string scratch)
    {
        var cfg = ConfigStore.Parse(V1Json, out int schema);
        Check(schema == 1, "a file without SchemaVersion is detected as schema 1");
        var notes = CameraRoles.Migrate(cfg);
        Check(cfg.SchemaVersion == 2, "migrated to schema 2");
        Check(cfg.Cameras[0].Role == "CAM-01" && cfg.Cameras[1].Role == "CAM-02", "roles assigned in saved order: CAM-01, CAM-02");
        Check(cfg.Cameras[0].VirtualCameraName == "CAM-01", "virtual camera name defaults to the role");
        Check(cfg.Cameras.All(c => Guid.TryParse(c.VirtualCameraId, out _)), "persistent virtual camera IDs generated");
        Check(cfg.Cameras[0].PhysicalFriendlyName == "USB Camera" && cfg.Cameras[0].FriendlyName == null, "FriendlyName moved to PhysicalFriendlyName");
        Check(cfg.Cameras[0].DeviceId!.EndsWith(@"#A#{x}\GLOBAL"), "device IDs kept as they were");
        Check(cfg.Cameras[0].DelayMs == 40 && !cfg.Cameras[1].VirtualCameraEnabled, "per-camera settings kept");
        Check(!cfg.SwitcherVirtualCameraEnabled, "switcher virtual camera off after migration");
        Check(cfg.Extra != null && cfg.Extra.ContainsKey("SomeFutureSetting"), "unknown top-level field preserved");
        Check(notes.Count > 0, $"migration notes logged ({notes.Count})");

        // Migrating again changes nothing.
        string id0 = cfg.Cameras[0].VirtualCameraId;
        CameraRoles.Migrate(cfg);
        Check(cfg.Cameras[0].VirtualCameraId == id0 && cfg.Cameras[0].Role == "CAM-01", "migration is idempotent (IDs never regenerated)");

        // Through the store: load v1 from disk, IDs stable across reloads once saved.
        string path = Path.Combine(scratch, "migrate", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, V1Json);
        var store = new ConfigStore(path);
        var loaded = store.Load(out bool migrated);
        Check(migrated, "store reports that the file needed migration");
        store.Save(loaded);
        var again = new ConfigStore(path).Load(out bool migrated2);
        Check(!migrated2 && again.Cameras[1].VirtualCameraId == loaded.Cameras[1].VirtualCameraId, "after saving, reload keeps the same IDs and needs no migration");
        string json = File.ReadAllText(path);
        Check(json.Contains("\"SchemaVersion\": 2") && !json.Contains("\"FriendlyName\""), "saved file is schema 2 without the legacy FriendlyName");
        Check(json.Contains("SomeFutureSetting"), "unknown field written back");
    }

    private static void Persistence(string scratch)
    {
        string path = Path.Combine(scratch, "persist", "config.json");
        var store = new ConfigStore(path);
        var cfg = new AppConfig();
        cfg.Cameras.AddRange(TwoRoles());
        Check(store.Save(cfg) && File.Exists(path) && !File.Exists(store.TempPath), "first save creates the file, no temp left behind");

        cfg.Cameras[0].DelayMs = 123;
        Check(store.Save(cfg) && File.Exists(store.BackupPath), "second save keeps the previous file as .bak");
        Check(new ConfigStore(path).Load().Cameras[0].DelayMs == 123, "saved value reads back");
        var bak = ConfigStore.Parse(File.ReadAllText(store.BackupPath), out _);
        Check(bak.Cameras[0].DelayMs == 0, ".bak holds the previous version");

        // Simulated crash mid-write: a truncated main file must fall back to .bak.
        File.WriteAllText(path, "{ \"SchemaVersion\": 2, \"Cameras\": [ { \"Role\": ");
        var recovered = new ConfigStore(path).Load(out bool fromBackup);
        Check(recovered.Cameras.Count == 2 && fromBackup, "truncated config -> loaded from backup");
        Check(Directory.GetFiles(Path.GetDirectoryName(path)!, "config.json.corrupt-*").Length == 1, "unreadable file kept aside as .corrupt-*");

        // A stale temp file from an interrupted save never replaces the config.
        File.WriteAllText(store.TempPath, "garbage");
        store.Save(recovered);
        Check(new ConfigStore(path).Load().Cameras.Count == 2, "a stale .tmp is overwritten, config intact");

        // Unknown per-camera field preserved through a round trip.
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        node["Cameras"]![0]!["VendorTuning"] = 7;
        File.WriteAllText(path, node.ToJsonString());
        var withExtra = new ConfigStore(path).Load();
        new ConfigStore(path).Save(withExtra);
        Check(File.ReadAllText(path).Contains("VendorTuning"), "unknown per-camera field preserved");

        Check(new ConfigStore(Path.Combine(scratch, "none", "config.json")).Load().Cameras.Count == 0, "no file -> defaults (no roles)");
        var defaults = new AppConfig();
        Check(defaults.Width == 1280 && defaults.Height == 720 && defaults.Fps == 30, "default output 1280x720@30");
        Check(!defaults.SwitcherVirtualCameraEnabled, "switcher virtual camera off by default");
    }

    private sealed class FakeNative : IVirtualCameraNative
    {
        private long _next = 0x1000;
        public readonly Dictionary<IntPtr, string> Live = new();
        public readonly List<string> Log = new();
        public int Start(string friendlyName, string sourceId, out IntPtr handle)
        {
            lock (Live)
            {
                handle = new IntPtr(_next++);
                Live[handle] = friendlyName;
                Log.Add("start " + friendlyName);
            }
            return 0;
        }
        public void Stop(IntPtr handle)
        {
            lock (Live)
            {
                Log.Add("stop " + (Live.TryGetValue(handle, out var n) ? n : "?"));
                Live.Remove(handle);
            }
        }
        public IReadOnlyCollection<string> Names { get { lock (Live) return Live.Values.ToList(); } }
    }

    private static void VcamService(string scratch)
    {
        string ini = Path.Combine(scratch, "vcams.ini");
        var native = new FakeNative();
        var svc = new VirtualCameraService(native, ini, skipPreflight: true);
        var roles = TwoRoles();
        var c3 = CameraRoles.CreateRole("CAM-03");
        roles.Add(c3);

        svc.Prepare(roles);
        foreach (var r in roles)
            svc.StartKey(r.VirtualCameraId);
        Check(native.Names.OrderBy(n => n).SequenceEqual(new[] { "CAM-01", "CAM-02", "CAM-03" }), "role virtual cameras registered with their fixed names (no suffixes)");
        string iniText = File.ReadAllText(ini);
        Check(iniText.Contains("CAM-01\t" + Protocol.RolePipeName(roles[0].VirtualCameraId)), "vcams.ini maps the name to the role's own pipe");
        var handle1 = svc.SlotHandleForTest(0);

        // Reorder: nothing may stop or rename.
        var reordered = new List<CameraConfig> { roles[2], roles[0], roles[1] };
        int logBefore = native.Log.Count;
        svc.Prepare(reordered);
        Check(native.Log.Count == logBefore, "reordering roles stops nothing");
        Check(svc.IsKeyRunning(roles[0].VirtualCameraId) && svc.SlotName(1) == "CAM-01", "after reorder index 1 is CAM-01 and still running");

        // Rename CAM-03 -> CAM-07: only that one is stopped (old name), then the new one starts.
        var renamed = reordered.Select(c => c.Clone()).ToList();
        renamed[0].Role = "CAM-07";
        renamed[0].VirtualCameraName = "CAM-07";
        logBefore = native.Log.Count;
        svc.Prepare(renamed);
        var delta = native.Log.Skip(logBefore).ToList();
        Check(delta.SequenceEqual(new[] { "stop CAM-03" }), $"rename stops only the old virtual camera ({string.Join(", ", delta)})");
        Check(svc.IsKeyRunning(roles[0].VirtualCameraId) && svc.IsKeyRunning(roles[1].VirtualCameraId), "other roles keep running through the rename");
        svc.StartKey(renamed[0].VirtualCameraId);
        Check(native.Names.Contains("CAM-07") && !native.Names.Contains("CAM-03"), "new name registered, old name gone");
        Check(File.ReadAllText(ini).Contains("CAM-07\t") && !File.ReadAllText(ini).Contains("CAM-03\t"), "vcams.ini updated for the rename");

        // Remove CAM-02.
        var removed = renamed.Where(c => c.Role != "CAM-02").ToList();
        svc.Prepare(removed);
        Check(!native.Names.Contains("CAM-02") && native.Names.Contains("CAM-01"), "removing a role unregisters only its virtual camera");

        // Duplicates are never registered.
        var dupes = removed.Select(c => c.Clone()).ToList();
        dupes[1].VirtualCameraName = dupes[0].VirtualCameraName;
        svc.Prepare(dupes);
        Check(!svc.StartKey(dupes[0].VirtualCameraId) && !svc.StartKey(dupes[1].VirtualCameraId), "duplicate virtual camera name -> neither is started");
        Check(svc.IsKeyBlocked(dupes[0].VirtualCameraId), "duplicate is reported as blocked");

        svc.Stop();
        Check(native.Names.Count == 0, "Stop releases every virtual camera");
        Check(handle1 != IntPtr.Zero, "handles were real (sanity)");
    }

    private static void Geometry()
    {
        Check(CameraSource.FitSize(640, 480, 1280, 720, "Letterbox") == (960, 720), "640x480 letterbox -> 960x720 in 1280x720");
        Check(CameraSource.FitSize(640, 480, 1280, 720, "Crop") == (1280, 960), "640x480 crop -> 1280x960, centre 720 rows used");
        Check(CameraSource.FitSize(640, 480, 1280, 720, "Stretch") == (1280, 720), "stretch -> output size");
        Check(CameraSource.FitSize(1280, 720, 1280, 720, "Letterbox") == (1280, 720), "1280x720 camera -> passthrough size");
        Check(CameraSource.FitSize(640, 360, 1280, 720, "Letterbox") == (1280, 720), "same aspect -> full frame");
        var (w, h) = CameraSource.FitSize(800, 600, 1280, 720, "Letterbox");
        Check(w % 2 == 0 && h % 2 == 0 && w <= 1280 && h <= 720, $"sizes are even and inside the output ({w}x{h})");
        Check(ScaleModes.Normalize("crop") == "Crop" && ScaleModes.Normalize(null) == "Letterbox", "scale mode parsing (default Letterbox)");
    }

    private static void DiagnosticAndProbe()
    {
        var d = new DiagnosticFrame(1280, 720);
        var buf = d.Render(new[] { "CAM-01", "PHYSICAL CAMERA MISSING", "STATE: MISSING", "LAST FRAME: NEVER" }, 0);
        Check(buf.Length == 1280 * 720 * 3 / 2, "diagnostic frame is a full 1280x720 NV12 frame");
        int bright = 0;
        for (int i = 0; i < 1280 * 720; i++)
            if (buf[i] > 180) bright++;
        Check(bright > 2000, $"text pixels are drawn ({bright} bright luma pixels)");
        var same = d.Render(new[] { "CAM-01", "PHYSICAL CAMERA MISSING", "STATE: MISSING", "LAST FRAME: NEVER" }, 0);
        Check(ReferenceEquals(buf, same), "re-render reuses the same buffer (no per-frame allocation)");

        var frame = new byte[1280 * 720 * 3 / 2];
        long ts = System.Diagnostics.Stopwatch.GetTimestamp();
        LatencyProbe.Stamp(frame, 1280, 720, ts);
        Check(LatencyProbe.Read(frame, 1280) == ts, "latency probe stamp round-trips");
    }
}
