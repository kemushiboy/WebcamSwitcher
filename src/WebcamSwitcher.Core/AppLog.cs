using System.Collections.Concurrent;
using System.Text;

namespace WebcamSwitcher.Core;

/// <summary>
/// Timestamped, size-rotated application log.
///
/// Lines look like <c>2026-10-29 12:34:56.789 [CAM-01] message</c>. Writing never
/// blocks the caller on disk I/O (capture callbacks log too): lines go into a
/// queue that one background thread drains. The file rolls over at
/// <see cref="MaxFileBytes"/> and only <see cref="MaxFiles"/> files are kept
/// (<c>WebcamSwitcher.log</c>, <c>WebcamSwitcher.1.log</c>, ...).
/// </summary>
public static class AppLog
{
    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>(), 20000);
    private static readonly object StartLock = new();
    private static Thread? _writer;
    private static long _dropped;

    /// <summary>Test hook: write synchronously to this file instead. Null in production.</summary>
    internal static string? PathOverride { get; set; }

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebcamSwitcher", "logs");

    public static string CurrentPath => Path.Combine(Directory, "WebcamSwitcher.log");

    public static int MaxFiles { get; set; } = 10;
    public static long MaxFileBytes { get; set; } = 5L * 1024 * 1024;

    /// <summary>Applies the rotation limits from the config.</summary>
    public static void Configure(int maxFiles, int maxFileMB)
    {
        MaxFiles = Math.Clamp(maxFiles, 1, 100);
        MaxFileBytes = Math.Clamp(maxFileMB, 1, 100) * 1024L * 1024L;
    }

    /// <summary>General message (no specific role).</summary>
    public static void Write(string message) => Enqueue("-", message);

    /// <summary>Message about one Camera Role (or a component tag such as "Config").</summary>
    public static void Write(string role, string message) => Enqueue(string.IsNullOrEmpty(role) ? "-" : role, message);

    private static void Enqueue(string tag, string message)
    {
        try
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{tag}] {message}";
            string? overridePath = PathOverride;
            if (overridePath != null)
            {
                lock (StartLock)
                    File.AppendAllText(overridePath, line + Environment.NewLine);
                return;
            }

            EnsureWriter();
            if (!Queue.TryAdd(line))
                Interlocked.Increment(ref _dropped);
        }
        catch
        {
            // Logging must never throw.
        }
    }

    /// <summary>Blocks until queued lines are on disk (shutdown, tests). Bounded.</summary>
    public static void Flush(int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Queue.Count > 0 && Environment.TickCount64 < deadline)
            Thread.Sleep(10);
    }

    private static void EnsureWriter()
    {
        if (_writer != null)
            return;
        lock (StartLock)
        {
            if (_writer != null)
                return;
            _writer = new Thread(WriterLoop) { IsBackground = true, Name = "WebcamSwitcher.Log" };
            _writer.Start();
        }
    }

    private static void WriterLoop()
    {
        StreamWriter? writer = null;
        long size = 0;
        foreach (var line in Queue.GetConsumingEnumerable())
        {
            try
            {
                if (writer == null)
                {
                    System.IO.Directory.CreateDirectory(Directory);
                    writer = Open(out size);
                }

                long dropped = Interlocked.Exchange(ref _dropped, 0);
                if (dropped > 0)
                    size += WriteLine(writer, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [-] log queue full: {dropped} line(s) dropped");

                size += WriteLine(writer, line);
                if (Queue.Count == 0)
                    writer.Flush();

                if (size >= MaxFileBytes)
                {
                    writer.Dispose();
                    writer = null;
                    Rotate();
                }
            }
            catch
            {
                try { writer?.Dispose(); } catch { }
                writer = null;
                Thread.Sleep(500);
            }
        }
    }

    private static long WriteLine(StreamWriter w, string line)
    {
        w.WriteLine(line);
        return Encoding.UTF8.GetByteCount(line) + 2;
    }

    private static StreamWriter Open(out long size)
    {
        var fs = new FileStream(CurrentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        size = fs.Length;
        return new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = false };
    }

    /// <summary>log -> log.1 -> log.2 ... dropping the oldest beyond MaxFiles.</summary>
    private static void Rotate()
    {
        string Numbered(int i) => Path.Combine(Directory, $"WebcamSwitcher.{i}.log");
        try
        {
            int keep = Math.Max(1, MaxFiles);
            string oldest = Numbered(keep - 1);
            if (keep == 1)
            {
                File.Delete(CurrentPath);
                return;
            }
            if (File.Exists(oldest))
                File.Delete(oldest);
            for (int i = keep - 2; i >= 1; i--)
                if (File.Exists(Numbered(i)))
                    File.Move(Numbered(i), Numbered(i + 1));
            File.Move(CurrentPath, Numbered(1));
        }
        catch
        {
            // A locked file just delays rotation to the next line.
        }
    }
}
