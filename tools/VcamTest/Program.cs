using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using WebcamSwitcher.App;
using WebcamSwitcher.Core;

// Harness for the per-slot virtual camera contract. NOT part of the shipped app.
//
// It never touches the real system: the native create/stop layer is replaced by
// a fake, and vcams.ini / config.json are redirected to a scratch directory.
// The only real interaction is read-only enumeration of MF capture devices,
// which is reported before/after to show that nothing was registered or lost.
namespace WebcamSwitcher.VcamTest;

internal static class Program
{
    private static int _failures;
    private static readonly List<string> _problems = new();

    [STAThread]
    private static int Main(string[] args)
    {
        int cycles = 25;
        int idx = Array.IndexOf(args, "--cycles");
        if (idx >= 0 && idx + 1 < args.Length && int.TryParse(args[idx + 1], out int c))
            cycles = c;

        var ui = new PumpContext();
        SynchronizationContext.SetSynchronizationContext(ui);

        string scratch = Path.Combine(Path.GetTempPath(), "wsvcam-" + Environment.ProcessId);
        Directory.CreateDirectory(scratch);
        ConfigService.PathOverride = Path.Combine(scratch, "config.json");
        AppLog.PathOverride = Path.Combine(scratch, "harness.log");
        string ini = Path.Combine(scratch, "vcams.ini");

        Console.WriteLine($"VcamTest: cycles={cycles} scratch={scratch}");
        Console.WriteLine($"  devices before: {DescribeDevices()}");

        try
        {
            ServiceTests(ini, cycles);
            ControllerTests(ini, ui);
        }
        catch (Exception ex)
        {
            Fail($"UNCAUGHT {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex);
        }
        finally
        {
            ConfigService.PathOverride = null;
            AppLog.PathOverride = null;
            try { Directory.Delete(scratch, true); } catch { }
        }

        Console.WriteLine($"  devices after:  {DescribeDevices()}");

        if (_failures == 0)
        {
            Console.WriteLine("RESULT: PASS (0 failures)");
            return 0;
        }

        foreach (var p in _problems)
            Console.WriteLine($"  - {p}");
        Console.WriteLine($"RESULT: FAIL ({_failures} failure(s))");
        return 1;
    }

    // ---------------------------------------------------------------- helpers

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
        if (!ok)
            Fail(what);
    }

    private static void Fail(string what)
    {
        _failures++;
        _problems.Add(what);
    }

    private static string DescribeDevices()
    {
        try
        {
            var all = Windows.Devices.Enumeration.DeviceInformation
                .FindAllAsync(Windows.Devices.Enumeration.DeviceClass.VideoCapture)
                .AsTask().GetAwaiter().GetResult();
            var names = all.Select(d => d.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            int ours = names.Count(n => n.Contains("WebcamSwitcher", StringComparison.OrdinalIgnoreCase));
            return $"{names.Count} device(s), {ours} WebcamSwitcher -> [{string.Join(" | ", names)}]";
        }
        catch (Exception ex)
        {
            return $"enumeration unavailable ({ex.GetType().Name})";
        }
    }

    private static AppConfig MakeConfig(int cameras)
    {
        var cfg = new AppConfig { Width = 1280, Height = 720, Fps = 30 };
        for (int i = 0; i < cameras; i++)
            cfg.Cameras.Add(new CameraConfig { DeviceId = $"dev{i}", FriendlyName = i == 1 ? "C920" : $"Cam {i}" });
        cfg.SchemaVersion = 1;
        CameraRoles.Migrate(cfg); // schema-1 entries get roles CAM-01.. and persistent IDs
        cfg.SwitcherVirtualCameraEnabled = true; // this harness exercises the switcher too
        cfg.Normalize();
        return cfg;
    }

    // -------------------------------------------------------- service tests

    private static void ServiceTests(string ini, int cycles)
    {
        Console.WriteLine();
        Console.WriteLine("== VirtualCameraService: per-slot lifecycle (fake native layer)");

        var cfg = MakeConfig(3);
        var native = new FakeNative(delayMs: 12);
        var svc = new VirtualCameraService(native, ini, skipPreflight: true);

        bool prepared = svc.Prepare(cfg.Cameras);
        Check(prepared && svc.SlotCount == 4, $"Prepare computes 4 slots for 3 cameras (got {svc.SlotCount})");
        string[] names = Enumerable.Range(0, svc.SlotCount).Select(i => svc.SlotName(i) ?? "").ToArray();
        Console.WriteLine($"  names: {string.Join(" | ", names)}");
        Check(names[3] == VirtualCameraService.SwitcherName, "switcher slot keeps its friendly name");
        Check(File.Exists(ini), "vcams.ini written before any creation");
        string iniText = File.ReadAllText(ini);
        Check(iniText.Split('\n').Count(l => l.Contains('\t')) == 4, "vcams.ini carries the full 4-slot mapping");

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 4; i++)
            svc.StartSlot(i);
        long startAllMs = sw.ElapsedMilliseconds;
        Check(native.LiveCount == 4 && svc.IsRunning, $"start all slots -> 4 live handles (got {native.LiveCount})");
        Console.WriteLine($"  start-all 4 slots: {startAllMs} ms");

        IntPtr[] handles = Enumerable.Range(0, 4).Select(svc.SlotHandleForTest).ToArray();
        Check(handles.All(h => h != IntPtr.Zero), "every slot holds a non-zero handle");

        sw.Restart();
        bool stopped = svc.StopSlot(1);
        long stopMs = sw.ElapsedMilliseconds;
        Check(stopped && svc.SlotHandleForTest(1) == IntPtr.Zero, $"stop slot 1 -> handle released ({stopMs} ms)");
        Check(native.LiveCount == 3, $"stop slot 1 -> 3 live handles (got {native.LiveCount})");
        Check(svc.SlotHandleForTest(0) == handles[0] && svc.SlotHandleForTest(2) == handles[2] && svc.SlotHandleForTest(3) == handles[3],
              "stop slot 1 leaves slots 0, 2 and 3 untouched");
        Check(!svc.IsSlotRunning(1) && svc.IsRunning, "IsSlotRunning(1)=false while the service is still running");
        Check(svc.SlotName(1) == names[1], "slot 1 keeps its friendly name while stopped");

        sw.Restart();
        bool restarted = svc.StartSlot(1);
        long restartMs = sw.ElapsedMilliseconds;
        Check(restarted && svc.SlotHandleForTest(1) != IntPtr.Zero, $"start slot 1 again ({restartMs} ms)");
        Check(svc.SlotName(1) == names[1], "restarted slot keeps the identical friendly name");
        Check(native.CreatedNames.Count(n => n == names[1]) == 2, "native saw the same name both times");

        // Repeated toggling: no leak of handles or of live devices.
        int threadsBefore = Process.GetCurrentProcess().Threads.Count;
        int maxLive = 0;
        var toggleTimes = new List<long>();
        for (int i = 0; i < cycles; i++)
        {
            sw.Restart();
            svc.StopSlot(1);
            svc.StartSlot(1);
            toggleTimes.Add(sw.ElapsedMilliseconds);
            maxLive = Math.Max(maxLive, native.LiveCount);
        }
        Thread.Sleep(150);
        int threadsAfter = Process.GetCurrentProcess().Threads.Count;
        Check(native.LiveCount == 4 && maxLive == 4, $"{cycles} stop/start cycles -> live handles never above 4 (max {maxLive}, now {native.LiveCount})");
        Check(native.StoppedHandles.Distinct().Count() == native.StoppedHandles.Count, "no handle was stopped twice");
        Check(svc.SlotName(1) == names[1], $"{cycles} cycles -> name still identical");
        Console.WriteLine($"  {cycles} cycles: total {toggleTimes.Sum()} ms, avg {toggleTimes.Average():F1} ms, max {toggleTimes.Max()} ms; " +
                          $"native creates={native.CreatedNames.Count}, stops={native.StoppedHandles.Count}; " +
                          $"threads {threadsBefore} -> {threadsAfter}");

        // Failure paths must not create state.
        var bad = new FakeNative(delayMs: 1) { FailWith = unchecked((int)0x80070005) };
        var badSvc = new VirtualCameraService(bad, Path.Combine(Path.GetDirectoryName(ini)!, "bad.ini"), skipPreflight: true);
        badSvc.Prepare(cfg.Cameras);
        Check(!badSvc.StartSlot(0) && !badSvc.IsRunning, "native hr<0 -> start reports false, no handle kept");
        bad.FailWith = 0;
        bad.ReturnNullHandle = true;
        Check(!badSvc.StartSlot(0) && !badSvc.IsRunning, "hr=0 with a null handle -> start reports false");
        Check(!badSvc.IsSlotRunning(99) && badSvc.SlotName(99) == null, "out-of-range slot is reported as not running");
        badSvc.StopSlot(99);
        Check(true, "start/stop of an invalid slot does not throw");

        svc.Stop();
        Check(!svc.IsRunning && native.LiveCount == 0, "Stop() releases every handle");

        // Start after a stop re-arms the same names (no restart needed).
        svc.StartSlot(2);
        Check(svc.IsSlotRunning(2) && svc.SlotName(2) == names[2], "a slot can be started after Stop() with the same name");
        svc.Dispose();
        Check(native.LiveCount == 0, "Dispose() releases every handle");
    }

    // ----------------------------------------------------- controller tests

    private static void ControllerTests(string ini, PumpContext ui)
    {
        Console.WriteLine();
        Console.WriteLine("== PipelineController: public per-slot contract (fake native layer)");

        var cfg = MakeConfig(3);
        var native = new FakeNative(delayMs: 40);
        var svc = new VirtualCameraService(native, ini, skipPreflight: true);
        var ctrl = new PipelineController();
        ctrl.InitializeForTest(cfg, svc);

        int uiThread = Environment.CurrentManagedThreadId;
        var eventThreads = new List<int>();
        int eventCount = 0;
        ctrl.VcamStateChanged += () =>
        {
            lock (eventThreads) { eventThreads.Add(Environment.CurrentManagedThreadId); }
            Interlocked.Increment(ref eventCount);
        };

        Check(ctrl.CameraCount == 3, $"CameraCount=3 (got {ctrl.CameraCount})");
        Check(!ctrl.IsVcamRunning(0) && !ctrl.IsSwitcherVcamRunning, "nothing reports running before the first start");

        // Busy must be set synchronously, before returning to the UI.
        var start0 = ctrl.SetVcamRunningAsync(0, true);
        bool busySync = ctrl.IsVcamBusy(0);
        Check(busySync, "IsVcamBusy(0) is true synchronously after the call returns");
        bool ok0 = start0.GetAwaiter().GetResult();
        ui.Pump();
        Check(ok0 && ctrl.IsVcamRunning(0), "SetVcamRunningAsync(0,true) -> running");
        Check(!ctrl.IsVcamBusy(0), "busy cleared after completion");
        Check(eventThreads.Count > 0 && eventThreads.All(t => t == uiThread), $"VcamStateChanged raised on the UI thread ({eventThreads.Count} event(s))");

        // Same slot serialises: two concurrent starts must never overlap natively.
        native.ResetConcurrency();
        var a = Task.Run(() => ctrl.SetVcamRunningAsync(1, true));
        var b = Task.Run(() => ctrl.SetVcamRunningAsync(1, true));
        Task.WaitAll(a, b);
        ui.Pump();
        Check(native.MaxConcurrentForAnySingleSlot() == 1, $"same-slot calls serialised (max concurrent for one slot = {native.MaxConcurrentForAnySingleSlot()})");
        Check(native.PeakConcurrentTotal >= 1, $"native calls observed (total peak {native.PeakConcurrentTotal})");

        // Different slots run independently (start from a clean state so both
        // slots actually reach the native layer).
        ctrl.SetVcamRunningAsync(0, false).GetAwaiter().GetResult();
        ctrl.SetVcamRunningAsync(2, false).GetAwaiter().GetResult();
        ui.Pump();
        native.ResetConcurrency();
        var sw = Stopwatch.StartNew();
        var t1 = Task.Run(() => ctrl.SetVcamRunningAsync(0, true));
        var t2 = Task.Run(() => ctrl.SetVcamRunningAsync(2, true));
        Task.WaitAll(t1, t2);
        long parallelMs = sw.ElapsedMilliseconds;
        ui.Pump();
        Check(native.PeakConcurrentTotal >= 2, $"different slots overlap natively (peak {native.PeakConcurrentTotal})");
        // Wall time now also includes two durable config saves (fsync + atomic
        // replace, serialised by the store), so overlap is asserted on the native
        // layer above and the time is only reported.
        Console.WriteLine($"  (info) two 40 ms slots + 2 durable saves took {parallelMs} ms");
        Check(native.MaxConcurrentForAnySingleSlot() == 1, "parallel start of different slots never doubled up one slot");
        Check(ctrl.IsVcamRunning(0) && ctrl.IsVcamRunning(2), "both parallel-started slots are running");

        // Switcher slot.
        Check(ctrl.SetSwitcherVcamRunningAsync(true).GetAwaiter().GetResult(), "switcher start -> true");
        ui.Pump();
        Check(ctrl.IsSwitcherVcamRunning, "IsSwitcherVcamRunning -> true");
        Check(!ctrl.IsSwitcherVcamBusy, "switcher busy cleared");

        // Persistence: toggling writes the flags through ConfigService.
        Check(ctrl.SetVcamRunningAsync(2, false).GetAwaiter().GetResult(), "stop camera slot 2 -> true");
        ui.Pump();
        var saved = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigService.PathOverride!))!;
        Check(saved.Cameras.Count == 3 && !saved.Cameras[2].VirtualCameraEnabled, "config.json saved VirtualCameraEnabled=false for slot 2");
        Check(saved.Cameras[0].VirtualCameraEnabled && saved.Cameras[1].VirtualCameraEnabled, "other cameras stay enabled");
        Check(saved.SwitcherVirtualCameraEnabled, "switcher flag saved as enabled");

        // SetAll(true) starts exactly the enabled slots.
        Check(ctrl.SetAllVcamsRunningAsync(false).GetAwaiter().GetResult(), "SetAll(false) -> all stopped");
        ui.Pump();
        Check(!ctrl.IsVcamRunning(0) && !ctrl.IsVcamRunning(1) && !ctrl.IsVcamRunning(2) && !ctrl.IsSwitcherVcamRunning,
              "SetAll(false) stops camera slots and the switcher");

        Check(ctrl.SetAllVcamsRunningAsync(true).GetAwaiter().GetResult(), "SetAll(true) -> ok");
        ui.Pump();
        Check(ctrl.IsVcamRunning(0) && ctrl.IsVcamRunning(1), "SetAll(true) started the enabled camera slots");
        Check(!ctrl.IsVcamRunning(2), "SetAll(true) left the individually disabled slot 2 OFF");
        Check(ctrl.IsSwitcherVcamRunning, "SetAll(true) started the switcher");

        // A disabled slot can still be started explicitly.
        Check(ctrl.SetVcamRunningAsync(2, true).GetAwaiter().GetResult() && ctrl.IsVcamRunning(2),
              "an individually disabled slot can still be started explicitly");
        ui.Pump();
        saved = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigService.PathOverride!))!;
        Check(saved.Cameras[2].VirtualCameraEnabled, "starting it explicitly re-enables it in config.json");

        // Disabled switcher flag is honoured by SetAll.
        ctrl.SetSwitcherVcamRunningAsync(false).GetAwaiter().GetResult();
        ui.Pump();
        saved = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigService.PathOverride!))!;
        Check(!saved.SwitcherVirtualCameraEnabled, "switcher flag saved as disabled");
        ctrl.SetAllVcamsRunningAsync(false).GetAwaiter().GetResult();
        Check(ctrl.SetAllVcamsRunningAsync(true).GetAwaiter().GetResult(), "SetAll(true) with the switcher disabled -> ok");
        ui.Pump();
        Check(ctrl.IsVcamRunning(0) && !ctrl.IsSwitcherVcamRunning, "disabled switcher slot stays off under SetAll(true)");

        // Invalid input never throws.
        Check(!ctrl.SetVcamRunningAsync(-1, true).GetAwaiter().GetResult(), "invalid index -> false");
        Check(!ctrl.SetVcamRunningAsync(99, true).GetAwaiter().GetResult(), "out-of-range index -> false");
        Check(!ctrl.IsVcamRunning(-1) && !ctrl.IsVcamRunning(99) && !ctrl.IsVcamBusy(99), "invalid index reports not running / not busy");

        // Cross-thread call: the event must still land on the UI thread.
        eventThreads.Clear();
        var off = Task.Run(() => ctrl.SetVcamRunningAsync(0, false)); // from a pool thread
        off.GetAwaiter().GetResult();
        int pumped = ui.Pump();
        lock (eventThreads)
            Check(pumped > 0 && eventThreads.All(t => t == uiThread),
                  $"cross-thread call posts VcamStateChanged to the UI thread ({pumped} posted, threads={string.Join(",", eventThreads.Distinct())})");
        Check(!ctrl.IsVcamRunning(0), "slot 0 stopped from a worker thread");

        // Repeated toggling through the controller: no handle growth, no thread growth.
        int threadsBefore = Process.GetCurrentProcess().Threads.Count;
        int baseline = native.LiveCount; // slots left running by the tests above
        int peak = 0;
        for (int i = 0; i < 10; i++)
        {
            ctrl.SetVcamRunningAsync(0, true).GetAwaiter().GetResult();
            ctrl.SetVcamRunningAsync(0, false).GetAwaiter().GetResult();
            peak = Math.Max(peak, native.LiveCount);
        }
        ui.Pump();
        Thread.Sleep(150);
        int threadsAfter = Process.GetCurrentProcess().Threads.Count;
        Check(peak <= baseline + 1,
              $"controller toggling never holds more than one handle for slot 0 (baseline {baseline}, peak {peak})");
        Check(native.LiveCount == baseline, $"after toggling, live handle count is back to baseline ({native.LiveCount} vs {baseline})");
        Check(threadsAfter <= threadsBefore + 4, $"thread count stable under controller churn ({threadsBefore} -> {threadsAfter})");

        Check(native.CreatedNames.All(n => n.Length > 0), "every native create carried a friendly name");
        Check(native.SpuriousStops == 0, $"no handle was stopped twice or never created (spurious stops: {native.SpuriousStops})");
        Console.WriteLine($"  native: creates={native.CreatedNames.Count} stops={native.StoppedHandles.Count} live={native.LiveCount}");
        Console.WriteLine($"  events raised: {eventCount}");
    }

    // ------------------------------------------------------------------ fake

    private sealed class FakeNative : IVirtualCameraNative
    {
        private readonly int _delayMs;
        private readonly object _lock = new();
        private readonly HashSet<IntPtr> _live = new();
        private readonly Dictionary<IntPtr, string> _nameOfHandle = new();
        private int _handleSeq;
        private int _concurrentTotal;
        private int _peakTotal;
        private readonly Dictionary<string, int> _concurrentPerSlot = new();
        private readonly Dictionary<string, int> _peakPerSlot = new();

        public FakeNative(int delayMs) => _delayMs = delayMs;

        public int FailWith { get; set; }
        public bool ReturnNullHandle { get; set; }

        public List<string> CreatedNames { get; } = new();
        public List<IntPtr> StoppedHandles { get; } = new();
        public int SpuriousStops { get; private set; }

        public int LiveCount { get { lock (_lock) return _live.Count; } }
        public int PeakConcurrentTotal => Volatile.Read(ref _peakTotal);

        public void ResetConcurrency()
        {
            Volatile.Write(ref _peakTotal, 0);
            lock (_lock)
            {
                _peakPerSlot.Clear();
            }
        }

        public int MaxConcurrentForAnySingleSlot()
        {
            lock (_lock)
                return _peakPerSlot.Count == 0 ? 0 : _peakPerSlot.Values.Max();
        }

        public int Start(string friendlyName, string sourceId, out IntPtr handle)
        {
            handle = IntPtr.Zero;
            string key = friendlyName;
            int now = Interlocked.Increment(ref _concurrentTotal);
            InterlockedMax(ref _peakTotal, now);
            lock (_lock)
            {
                _concurrentPerSlot.TryGetValue(key, out int n);
                _concurrentPerSlot[key] = n + 1;
                _peakPerSlot.TryGetValue(key, out int p);
                _peakPerSlot[key] = Math.Max(p, n + 1);
                CreatedNames.Add(friendlyName);
            }

            try
            {
                Thread.Sleep(_delayMs);
                int fail = FailWith;
                if (fail < 0)
                    return fail;
                if (ReturnNullHandle)
                    return 0;

                // Handle ids must be unique across concurrent starts: allocate the
                // id under the same lock that registers it.
                IntPtr h;
                lock (_lock)
                {
                    h = new IntPtr(0x1000 + ++_handleSeq);
                    _live.Add(h);
                    _nameOfHandle[h] = friendlyName;
                }
                handle = h;
                return 0;
            }
            finally
            {
                Interlocked.Decrement(ref _concurrentTotal);
                lock (_lock)
                {
                    _concurrentPerSlot.TryGetValue(key, out int n);
                    _concurrentPerSlot[key] = Math.Max(0, n - 1);
                }
            }
        }

        public void Stop(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
                return;
            lock (_lock)
            {
                if (!_live.Remove(handle))
                {
                    SpuriousStops++;
                    Fail($"fake: stop called for a handle that is not live (0x{handle.ToInt64():X})");
                }
                _nameOfHandle.Remove(handle);
                StoppedHandles.Add(handle);
            }
            Thread.Sleep(Math.Max(1, _delayMs / 2));
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while (value > (current = Volatile.Read(ref target)))
                if (Interlocked.CompareExchange(ref target, value, current) == current)
                    break;
        }

        private static void Fail(string what) => Console.WriteLine($"    (fake) {what}");
    }

    /// <summary>Minimal stand-in for the WPF dispatcher: queues Posts, runs them on Pump().</summary>
    private sealed class PumpContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback cb, object? state)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException("Send");

        public int Pump()
        {
            int n = 0;
            while (_queue.TryDequeue(out var item))
            {
                item.cb(item.state);
                n++;
            }
            return n;
        }
    }
}
