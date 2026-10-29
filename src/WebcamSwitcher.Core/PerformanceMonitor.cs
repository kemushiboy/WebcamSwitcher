using System.Diagnostics;

namespace WebcamSwitcher.Core;

/// <summary>Process-wide resource figures for the status bar and the log.</summary>
public sealed record ProcessStats(double CpuPercent, long WorkingSetMB, long PrivateMB, long GcHeapMB, int Threads);

/// <summary>
/// Samples this process's CPU (share of all cores) and memory, and writes one
/// performance line per role to the log every N seconds:
/// <code>PERF cpu=12.3% ws=410MB private=520MB gc=35MB threads=61
/// PERF [CAM-01] CONNECTED in=29.9fps out=30.0fps recv=107640 drop=3 proc=0.41ms reconnects=0 inUse=yes</code>
/// GPU usage is not sampled here (see tools/soak-monitor.ps1).
/// </summary>
public sealed class PerformanceMonitor : IDisposable
{
    private readonly Func<IReadOnlyList<RoleSnapshot>> _roles;
    private readonly Process _self = Process.GetCurrentProcess();
    private Timer? _timer;
    private TimeSpan _lastCpu;
    private long _lastTicks;
    private ProcessStats _latest = new(0, 0, 0, 0, 0);

    public PerformanceMonitor(Func<IReadOnlyList<RoleSnapshot>> roles) => _roles = roles;

    public ProcessStats Latest => _latest;

    public void Start(int logIntervalSeconds)
    {
        _lastCpu = _self.TotalProcessorTime;
        _lastTicks = Stopwatch.GetTimestamp();
        _timer?.Dispose();
        int sampleMs = 2000;
        int every = logIntervalSeconds <= 0 ? 0 : Math.Max(1, logIntervalSeconds * 1000 / sampleMs);
        int n = 0;
        _timer = new Timer(_ =>
        {
            try
            {
                Sample();
                if (every > 0 && ++n >= every)
                {
                    n = 0;
                    LogNow();
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("Perf", $"sample failed: {ex.Message}");
            }
        }, null, sampleMs, sampleMs);
    }

    private void Sample()
    {
        _self.Refresh();
        var cpu = _self.TotalProcessorTime;
        long now = Stopwatch.GetTimestamp();
        double wall = (now - _lastTicks) / (double)Stopwatch.Frequency;
        double pct = wall > 0 ? (cpu - _lastCpu).TotalSeconds / wall / Environment.ProcessorCount * 100.0 : 0;
        _lastCpu = cpu;
        _lastTicks = now;
        _latest = new ProcessStats(pct, _self.WorkingSet64 / 1048576, _self.PrivateMemorySize64 / 1048576,
            GC.GetTotalMemory(false) / 1048576, _self.Threads.Count);
    }

    public void LogNow()
    {
        var p = _latest;
        AppLog.Write("Perf", $"PERF cpu={p.CpuPercent:0.0}% ws={p.WorkingSetMB}MB private={p.PrivateMB}MB gc={p.GcHeapMB}MB threads={p.Threads}");
        foreach (var r in _roles())
            AppLog.Write(r.Role, $"PERF {RoleStatusText.Label(r.Status)} in={r.InputFps:0.0}fps out={r.OutputFpsMeasured:0.0}fps " +
                                 $"recv={r.FramesReceived} drop={r.DroppedFrames} proc={r.ProcessMicros / 1000.0:0.00}ms " +
                                 $"reconnects={r.Reconnects} inUse={(r.VirtualCameraInUse ? "yes" : "no")}" +
                                 (r.Warning.Length > 0 ? $" warn='{r.Warning}'" : ""));
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
