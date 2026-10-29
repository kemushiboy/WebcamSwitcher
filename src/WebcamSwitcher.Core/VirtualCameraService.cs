using System.Text;

namespace WebcamSwitcher.Core;

/// <summary>
/// Native seam for creating/stopping one Windows 11 Media Foundation virtual
/// camera. Production uses <see cref="InteropVirtualCameraNative"/>; tests inject
/// a fake so the slot logic can be exercised without registering real devices.
/// </summary>
public interface IVirtualCameraNative
{
    /// <summary>Creates and starts one virtual camera. Returns an HRESULT.</summary>
    int Start(string friendlyName, string sourceId, out IntPtr handle);

    /// <summary>Stops and releases one virtual camera handle.</summary>
    void Stop(IntPtr handle);
}

/// <summary>Default <see cref="IVirtualCameraNative"/> over the shipped DLL shim.</summary>
internal sealed class InteropVirtualCameraNative : IVirtualCameraNative
{
    public int Start(string friendlyName, string sourceId, out IntPtr handle)
    {
        try
        {
            return NativeInterop.WebcamSwitcher_Start(friendlyName, sourceId, out handle);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            // A broken or mismatched native DLL must never take the app down.
            AppLog.Write($"VirtualCameraService: native media source unavailable ({ex.GetType().Name}: {ex.Message}). " +
                         "Rebuild with build.ps1 and re-run regsvr32 if the app moved.");
            handle = IntPtr.Zero;
            return unchecked((int)0x8007007E); // ERROR_MOD_NOT_FOUND
        }
        catch (Exception ex)
        {
            AppLog.Write($"VirtualCameraService: native start threw {ex.GetType().Name}: {ex.Message}");
            handle = IntPtr.Zero;
            return unchecked((int)0x80004005); // E_FAIL
        }
    }

    public void Stop(IntPtr handle)
    {
        try { NativeInterop.WebcamSwitcher_Stop(handle); }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            AppLog.Write($"VirtualCameraService: native stop unavailable ({ex.GetType().Name}: {ex.Message})");
        }
        catch (Exception ex)
        {
            AppLog.Write($"VirtualCameraService: native stop threw {ex.GetType().Name}: {ex.Message}");
        }
    }
}

/// <summary>
/// Manages the Windows 11 Media Foundation virtual cameras via the native shim
/// in WebcamSwitcher.Source.dll (MFCreateVirtualCamera + Start/Stop).
///
/// Every virtual camera is identified by a <b>key</b> - the persistent
/// <see cref="CameraConfig.VirtualCameraId"/> of its Camera Role, or
/// <see cref="SwitcherKey"/> for the switcher - never by its position. Its
/// friendly name is the role's <see cref="CameraConfig.VirtualCameraName"/>
/// (no automatic "(2)" suffixes: duplicates are refused instead) and its pipe
/// is <see cref="Protocol.RolePipeName"/> of the key.
///
/// The index-based members (<see cref="StartSlot"/>, <see cref="SlotName"/>, ...)
/// remain for the UI and tests: index i is camera i of the last
/// <see cref="Prepare"/> call and index <c>cameras.Count</c> is the switcher.
/// They are only a view; reordering the list never stops or renames anything.
/// </summary>
public sealed class VirtualCameraService : IDisposable
{
    /// <summary>Friendly name of the switcher virtual camera (unchanged).</summary>
    public const string SwitcherName = "WebcamSwitcher Virtual Camera";

    /// <summary>Key of the switcher slot.</summary>
    public const string SwitcherKey = "switcher";

    private static readonly string DefaultVcamsIniPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WebcamSwitcher",
        "vcams.ini");

    /// <summary>Media source DLL shipped next to the app (the registered path).</summary>
    private static readonly string SourceDll =
        Path.Combine(AppContext.BaseDirectory, "WebcamSwitcher.Source.dll");

    /// <summary>HKLM COM registration key for the media source (stores an absolute DLL path).</summary>
    private const string SourceClsidKey =
        @"Software\Classes\CLSID\" + Protocol.SourceClsid + @"\InprocServer32";

    private sealed class Slot
    {
        public required string Key;
        public required string Role;
        public string Name = "";
        public string Pipe = "";
        public IntPtr Handle;
        public bool Blocked;          // duplicate name/key: never started
        public readonly object Gate = new();
    }

    private readonly IVirtualCameraNative _native;
    private readonly string _vcamsIniPath;

    // Tests inject a fake native layer, which has no DLL/COM registration to check.
    private readonly bool _skipPreflight;

    private readonly object _slotsLock = new();
    private readonly Dictionary<string, Slot> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = new(); // index view: cameras..., switcher
    private readonly object _prepareLock = new();
    private string? _lastFingerprint;

    public VirtualCameraService() : this(new InteropVirtualCameraNative(), null)
    {
    }

    /// <summary>Test constructor: fake native layer and/or a scratch vcams.ini.</summary>
    internal VirtualCameraService(IVirtualCameraNative native, string? vcamsIniPath, bool skipPreflight = false)
    {
        _native = native;
        _vcamsIniPath = vcamsIniPath ?? DefaultVcamsIniPath;
        _skipPreflight = skipPreflight;
    }

    /// <summary>Number of slots: one per camera plus the switcher.</summary>
    public int SlotCount
    {
        get { lock (_slotsLock) return _order.Count; }
    }

    /// <summary>True while at least one virtual camera handle is held.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_slotsLock)
                return _byKey.Values.Any(s => s.Handle != IntPtr.Zero);
        }
    }

    public string FriendlyName => SwitcherName;

    private Slot? SlotAt(int index)
    {
        lock (_slotsLock)
            return index >= 0 && index < _order.Count && _byKey.TryGetValue(_order[index], out var s) ? s : null;
    }

    private Slot? SlotFor(string key)
    {
        lock (_slotsLock)
            return _byKey.TryGetValue(key, out var s) ? s : null;
    }

    /// <summary>Friendly name of a slot, or null when the slot does not exist.</summary>
    public string? SlotName(int index) => SlotAt(index)?.Name;

    /// <summary>True when that slot currently holds a live native handle.</summary>
    public bool IsSlotRunning(int index) => SlotAt(index)?.Handle is { } h && h != IntPtr.Zero;

    /// <summary>True when the virtual camera with this key is live.</summary>
    public bool IsKeyRunning(string key) => SlotFor(key)?.Handle is { } h && h != IntPtr.Zero;

    /// <summary>True when this key's slot is blocked as a duplicate.</summary>
    public bool IsKeyBlocked(string key) => SlotFor(key)?.Blocked == true;

    /// <summary>Pipe name of the virtual camera with this key, or null.</summary>
    public string? PipeFor(string key) => SlotFor(key)?.Pipe;

    /// <summary>Test hook: the native handle held by a slot (IntPtr.Zero when stopped).</summary>
    internal IntPtr SlotHandleForTest(int index) => SlotAt(index)?.Handle ?? IntPtr.Zero;

    /// <summary>Key of a camera's virtual camera (its persistent ID).</summary>
    public static string KeyOf(CameraConfig camera) => camera.VirtualCameraId;

    /// <summary>
    /// Brings the slot table in line with the role list and writes the routing
    /// file, without creating anything. Only slots whose role disappeared or
    /// whose name changed are stopped (old name first: stop, unregister, then
    /// the new name can be started); every other running slot is untouched.
    /// Idempotent.
    /// </summary>
    public bool Prepare(IReadOnlyList<CameraConfig>? cameras)
    {
        cameras ??= Array.Empty<CameraConfig>();
        var dupes = CameraRoles.DuplicatedRoles(cameras);

        var desired = new List<(string Key, string Role, string Name, string Pipe, bool Blocked)>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in cameras)
        {
            string key = KeyOf(c);
            bool blocked = dupes.Contains(c.Role) || string.IsNullOrWhiteSpace(key) ||
                           !CameraRoles.IsValidVirtualCameraName(c.VirtualCameraName);
            if (string.IsNullOrWhiteSpace(key) || !seenKeys.Add(key))
                key = "invalid:" + c.Role + ":" + desired.Count; // never collides with a real slot
            desired.Add((key, c.Role, c.VirtualCameraName ?? "", Protocol.RolePipeName(key), blocked));
        }
        desired.Add((SwitcherKey, "SWITCHER", SwitcherName, Protocol.PipeName, false));

        // Called before every start/stop: an unchanged role list must cost
        // nothing (no lock contention between roles, no file write).
        string fingerprint = string.Join("\n", desired.Select(d => $"{d.Key}\t{d.Role}\t{d.Name}\t{d.Blocked}"));
        lock (_prepareLock)
        {
            if (fingerprint == _lastFingerprint)
                return true;
            PrepareCore(desired);
            _lastFingerprint = fingerprint;
        }
        return true;
    }

    private void PrepareCore(List<(string Key, string Role, string Name, string Pipe, bool Blocked)> desired)
    {
        var toStop = new List<Slot>();
        lock (_slotsLock)
        {
            var wanted = desired.ToDictionary(d => d.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var slot in _byKey.Values.ToList())
            {
                bool gone = !wanted.TryGetValue(slot.Key, out var d);
                bool renamed = !gone && (!string.Equals(d.Name, slot.Name, StringComparison.Ordinal) || d.Blocked);
                if ((gone || renamed) && slot.Handle != IntPtr.Zero)
                    toStop.Add(slot);
                if (gone)
                    _byKey.Remove(slot.Key);
            }
        }

        // Stop outside the table lock: a native stop can take ~1 s.
        foreach (var slot in toStop)
        {
            AppLog.Write(slot.Role, $"virtual camera '{slot.Name}' is being renamed or removed -> stopping and unregistering it");
            StopSlotCore(slot);
        }

        lock (_slotsLock)
        {
            _order.Clear();
            foreach (var d in desired)
            {
                if (!_byKey.TryGetValue(d.Key, out var slot))
                {
                    slot = new Slot { Key = d.Key, Role = d.Role };
                    _byKey[d.Key] = slot;
                }
                slot.Role = d.Role;
                slot.Name = d.Name;
                slot.Pipe = d.Pipe;
                slot.Blocked = d.Blocked;
                _order.Add(d.Key);
            }
        }

        WriteVcamsIni(desired.Where(d => !d.Blocked).Select(d => (d.Name, d.Pipe)).ToList());
    }

    /// <summary>
    /// Starts every slot for which <paramref name="enabled"/> returns true.
    /// Returns whether any slot ended up running.
    /// </summary>
    public bool StartEnabled(IReadOnlyList<CameraConfig>? cameras, Func<int, bool> enabled)
    {
        if (!_skipPreflight && !PreflightNativeSource())
            return false;

        Prepare(cameras);

        int count = SlotCount;
        bool any = false;
        for (int i = 0; i < count; i++)
        {
            if (enabled != null && !enabled(i))
                continue;
            if (StartSlot(i))
                any = true;
        }
        return any;
    }

    /// <summary>Starts every slot (all roles plus the switcher).</summary>
    public bool Start(IReadOnlyList<CameraConfig> cameras) => StartEnabled(cameras, _ => true);

    /// <summary>Creates and starts one slot by index. Idempotent; never throws.</summary>
    public bool StartSlot(int index)
    {
        var slot = SlotAt(index);
        if (slot == null)
        {
            AppLog.Write($"VirtualCameraService.StartSlot({index}): no such slot");
            return false;
        }
        return StartSlotCore(slot);
    }

    /// <summary>Creates and starts the virtual camera with this key. Idempotent; never throws.</summary>
    public bool StartKey(string key)
    {
        var slot = SlotFor(key);
        if (slot == null)
        {
            AppLog.Write($"VirtualCameraService.StartKey({key}): no such virtual camera (Prepare first)");
            return false;
        }
        return StartSlotCore(slot);
    }

    private bool StartSlotCore(Slot slot)
    {
        lock (slot.Gate)
        {
            if (slot.Handle != IntPtr.Zero)
                return true; // already running

            if (slot.Blocked)
            {
                AppLog.Write(slot.Role, $"DUPLICATED: virtual camera '{slot.Name}' not registered (duplicate role, name or ID)");
                return false;
            }

            if (!_skipPreflight && !PreflightNativeSource())
                return false;

            int hr = _native.Start(slot.Name, Protocol.SourceClsid, out IntPtr handle);
            if (hr < 0 || handle == IntPtr.Zero)
            {
                AppLog.Write(slot.Role, $"virtual camera '{slot.Name}' registration FAILED hr=0x{hr:X8}" +
                                        (handle == IntPtr.Zero && hr >= 0 ? " (null handle)" : string.Empty));
                return false;
            }

            lock (_slotsLock)
                slot.Handle = handle;

            AppLog.Write(slot.Role, $"virtual camera '{slot.Name}' registered and started (pipe {slot.Pipe})");
            return true;
        }
    }

    /// <summary>Stops one slot by index. Other slots are untouched. Never throws.</summary>
    public bool StopSlot(int index)
    {
        var slot = SlotAt(index);
        if (slot == null)
        {
            AppLog.Write($"VirtualCameraService.StopSlot({index}): no such slot");
            return false;
        }
        return StopSlotCore(slot);
    }

    /// <summary>Stops the virtual camera with this key. Never throws.</summary>
    public bool StopKey(string key)
    {
        var slot = SlotFor(key);
        return slot == null || StopSlotCore(slot);
    }

    private bool StopSlotCore(Slot slot)
    {
        lock (slot.Gate)
        {
            IntPtr handle;
            lock (_slotsLock)
            {
                handle = slot.Handle;
                if (handle == IntPtr.Zero)
                    return true; // already stopped
                slot.Handle = IntPtr.Zero; // never hand this handle out twice
            }

            try
            {
                _native.Stop(handle);
                AppLog.Write(slot.Role, $"virtual camera '{slot.Name}' stopped and unregistered");
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Write(slot.Role, $"virtual camera '{slot.Name}' stop threw {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>Stops every started virtual camera. Idempotent.</summary>
    public void Stop()
    {
        List<Slot> slots;
        lock (_slotsLock)
            slots = _byKey.Values.ToList();
        foreach (var s in slots)
            StopSlotCore(s);
    }

    public void Dispose() => Stop();

    /// <summary>
    /// The media source is registered in HKLM by absolute DLL path (see
    /// <c>WebcamSwitcher.Source/DllRegisterServer</c>). Moving the app - or
    /// deleting its build output - after registration leaves that path dangling,
    /// and <c>MFCreateVirtualCamera</c> then fails with 0x8007007E
    /// (ERROR_MOD_NOT_FOUND), so activation silently does nothing. Detect it up
    /// front and log an actionable fix instead of an opaque HRESULT.
    /// </summary>
    /// <returns>
    /// False when the virtual cameras cannot possibly start (DLL missing, or the
    /// registration is absent or points at a deleted file); true otherwise.
    /// </returns>
    private static bool PreflightNativeSource()
    {
        // The DllImport below resolves against the app directory. If the file is
        // gone, calling into it throws DllNotFoundException and kills the process,
        // so refuse to call it at all.
        if (!File.Exists(SourceDll))
        {
            AppLog.Write($"VirtualCameraService: '{SourceDll}' is missing - run build.ps1 to restore it. " +
                         "Virtual cameras are unavailable until then.");
            return false;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(SourceClsidKey);

            if (key?.GetValue(null) is not string registered || registered.Length == 0)
            {
                AppLog.Write($"VirtualCameraService: media source not registered in HKLM ({SourceClsidKey}). " +
                             $"Fix (elevated): regsvr32 /s \"{SourceDll}\"");
                return false;
            }

            if (!File.Exists(registered))
            {
                AppLog.Write($"VirtualCameraService: media source registration is stale - '{registered}' does not exist. " +
                             $"Fix (elevated): regsvr32 /s \"{SourceDll}\"");
                return false;
            }
        }
        catch (Exception ex)
        {
            // An unreadable key (permissions etc.) must not block a legitimate
            // start - let the create call decide.
            AppLog.Write($"VirtualCameraService: registration check failed: {ex.Message}");
        }

        return true;
    }

    private void WriteVcamsIni(IReadOnlyList<(string Name, string Pipe)> map)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_vcamsIniPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // UTF-8 without BOM, CRLF line endings.
            var sb = new StringBuilder();
            foreach (var (name, pipe) in map)
                sb.Append(name).Append('\t').Append(pipe).Append("\r\n");

            // Temp + move: the Frame Server may read this file at any moment.
            string tmp = _vcamsIniPath + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tmp, _vcamsIniPath, overwrite: true);
            AppLog.Write($"VirtualCameraService: wrote {map.Count} mapping(s) to {_vcamsIniPath}");
        }
        catch (Exception ex)
        {
            AppLog.Write($"VirtualCameraService: failed to write {_vcamsIniPath}: {ex.Message}");
        }
    }
}
