using System.Diagnostics;

namespace WebcamSwitcher.Core;

/// <summary>
/// Load-testing stand-in for a camera: a role whose DeviceId is
/// <c>synthetic:</c>(anything) gets a moving NV12 test pattern at the output
/// size and rate instead of a physical device. It exercises everything after
/// USB capture (delay, pipe, Frame Server, consumers) so N-camera throughput
/// can be measured without N cameras. Never used unless configured explicitly.
/// </summary>
public sealed class SyntheticSource : IDisposable
{
    public const string Prefix = "synthetic:";

    private readonly int _w, _h, _fps;
    private readonly byte[] _frame;
    private readonly string _label;
    private Thread? _thread;
    private volatile bool _stop;
    private long _frames;
    private long _lastTicks;

    public SyntheticSource(int width, int height, int fps, string label)
    {
        _w = width;
        _h = height;
        _fps = Math.Max(1, fps);
        _label = label;
        _frame = new byte[Protocol.Nv12Size(width, height)];
        var d = new DiagnosticFrame(width, height);
        Buffer.BlockCopy(d.Render(new[] { label, "SYNTHETIC TEST PATTERN", "LOAD TEST ONLY - NOT A CAMERA" }, 2), 0, _frame, 0, _frame.Length);
    }

    public static bool IsSynthetic(string? deviceId) =>
        deviceId != null && deviceId.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public event Action<byte[]>? FrameReady;

    public long Frames => Interlocked.Read(ref _frames);

    public DateTime LastFrameUtc
    {
        get
        {
            long t = Interlocked.Read(ref _lastTicks);
            return t == 0 ? DateTime.MinValue : new DateTime(t, DateTimeKind.Utc);
        }
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Synthetic " + _label, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Run()
    {
        long period = Stopwatch.Frequency / _fps;
        long next = Stopwatch.GetTimestamp();
        int bar = 0;
        while (!_stop)
        {
            // A moving bar so a frozen pipe is visible to the eye.
            int x0 = bar;
            for (int y = _h - 40; y < _h; y++)
                Array.Fill(_frame, (byte)24, y * _w, _w);
            for (int y = _h - 40; y < _h; y++)
                Array.Fill(_frame, (byte)235, y * _w + x0, Math.Min(40, _w - x0));
            bar = (bar + 8) % (_w - 40);

            try { FrameReady?.Invoke(_frame); } catch { }
            Interlocked.Increment(ref _frames);
            Interlocked.Exchange(ref _lastTicks, DateTime.UtcNow.Ticks);

            next += period;
            long wait = next - Stopwatch.GetTimestamp();
            if (wait > 0)
                Thread.Sleep((int)(wait * 1000 / Stopwatch.Frequency));
            else if (wait < -period * 5)
                next = Stopwatch.GetTimestamp(); // fell far behind: resync, no burst
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(1000);
    }
}
