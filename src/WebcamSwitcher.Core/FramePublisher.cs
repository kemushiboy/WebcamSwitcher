using System.Runtime.InteropServices;

namespace WebcamSwitcher.Core;

/// <summary>
/// Serves frames to the virtual-camera media source over the named pipe.
/// The app is the pipe server; the C++ source (loaded by the Frame Server in
/// session 0) connects as a client. The pipe DACL grants Everyone so that
/// SYSTEM / LOCAL SERVICE can connect cross-session.
/// </summary>
public sealed class FramePublisher : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    private readonly object _lock = new();
    private readonly List<IntPtr> _clients = new();
    private readonly string _pipeName;
    private readonly byte[] _configMsg;
    private readonly IntPtr _securityDescriptor;
    private readonly AutoResetEvent _frameSignal = new(false);
    // Set by Dispose() (stop) and by the writer when the last client leaves
    // (rebuild the listening instance). The accept loop waits on it instead of
    // blocking in ConnectNamedPipe, so it can always be woken.
    private readonly ManualResetEvent _lifecycleWake = new(false);
    private CancellationTokenSource _cts = new();
    private Task? _acceptTask;
    private Task? _writerTask;

    // Ring, all guarded by _lock:
    //   _front   - newest complete message, waiting for the writer
    //   _inWrite - buffer the writer is currently putting on the pipe; never
    //              handed back to capture until the write finishes
    //   _lastMsg - newest message already written, retained so a client that
    //              connects between two publishes still gets a frame at once
    //   _spare   - a buffer nobody references, ready to be filled again
    // Capture fills a buffer that is provably not _inWrite, so a frame being
    // written can never be overwritten, and no publish needs a second copy.
    private byte[]? _front;
    private int _frontLen;
    private byte[]? _inWrite;
    private byte[]? _lastMsg;
    private int _lastMsgLen;
    private byte[]? _spare;
    // A client is waiting for its first frame: the writer resends _lastMsg.
    private bool _needFirstFrame;
    private int _createErrorLogged;
    private long _publishCount;
    private long _overwritten;
    private long _sentCount;
    private long _startTicks;
    private int _disposed;

    public FramePublisher(string pipeName, int width, int height, int fps)
    {
        _pipeName = pipeName;
        var config = new WsConfigMsg
        {
            Width = (uint)width,
            Height = (uint)height,
            Stride = (uint)width,
            Format = Protocol.FormatNv12,
            FpsNum = (uint)fps,
            FpsDen = 1
        };
        _configMsg = BuildMessage(Protocol.TypeConfig, StructToBytes(config));

        if (!NativeInterop.ConvertStringSecurityDescriptorToSecurityDescriptorW("D:(A;;GA;;;WD)", 1, out _securityDescriptor, out _))
            _securityDescriptor = IntPtr.Zero;
    }

    public void Start()
    {
        _startTicks = DateTime.UtcNow.Ticks;
        _acceptTask = Task.Run(AcceptLoop);
        _writerTask = Task.Run(WriterLoop);
    }

    /// <summary>Queues the latest NV12 frame for delivery to every connected client.</summary>
    public void PublishFrame(byte[] nv12, WsFrameInfo info)
    {
        int headerLen = Marshal.SizeOf<WsMsgHeader>();
        int infoLen = Marshal.SizeOf<WsFrameInfo>();
        var header = new WsMsgHeader
        {
            Magic = Protocol.Magic,
            Version = Protocol.Version,
            Type = Protocol.TypeFrame,
            PayloadLen = (uint)(infoLen + nv12.Length)
        };
        int total = headerLen + infoLen + nv12.Length;

        // Assemble the message in a ring buffer that the writer does not hold.
        // The single full-frame copy here is the only one: the writer puts this
        // very buffer on the pipe. Header and info are written in place, so a
        // steady stream allocates nothing per frame.
        lock (_lock)
        {
            // A pending front has not been taken by the writer (it only takes one
            // under this lock), so it can be overwritten: that is "latest frame
            // wins" and it keeps the ring at three buffers.
            byte[]? pending = _front;
            _front = null;
            if (pending != null)
                _overwritten++;

            byte[] buf;
            if (pending != null && pending.Length == total)
            {
                buf = pending;
            }
            else if (_spare != null && _spare.Length == total)
            {
                buf = _spare;
                _spare = null;
            }
            else
            {
                // First frames, or a size change: drop any stale spare too.
                _spare = null;
                buf = new byte[total];
            }

            MemoryMarshal.Write(buf.AsSpan(0, headerLen), in header);
            MemoryMarshal.Write(buf.AsSpan(headerLen, infoLen), in info);
            Buffer.BlockCopy(nv12, 0, buf, headerLen + infoLen, nv12.Length);

            _front = buf;
            _frontLen = total;
            _frameSignal.Set();
        }
        Interlocked.Increment(ref _publishCount);
    }

    /// <summary>
    /// Frames replaced by a newer one before the writer could send them
    /// ("latest wins" drops: the consumer side was slower than capture).
    /// </summary>
    public long OverwrittenCount { get { lock (_lock) return _overwritten; } }

    /// <summary>Frames actually written to at least one client.</summary>
    public long SentCount => Interlocked.Read(ref _sentCount);

    private void WriterLoop()
    {
        // This thread must outlive every client: a dead client, a broken pipe or
        // an exception may never take it down, or the slot stops serving frames
        // until the whole app is restarted.
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (!_frameSignal.WaitOne(250))
                    continue;

                byte[]? msg;
                int msgLen;
                bool resend = false;
                lock (_lock)
                {
                    if (_front != null)
                    {
                        // Take ownership of the pending frame; capture can no
                        // longer see this buffer until it is returned below.
                        msg = _front;
                        msgLen = _frontLen;
                        _front = null;
                        _inWrite = msg;
                    }
                    else if (_needFirstFrame && _lastMsg != null)
                    {
                        // A client connected after the last publish: hand it the
                        // retained newest frame instead of letting it wait for the
                        // next capture frame.
                        msg = _lastMsg;
                        msgLen = _lastMsgLen;
                        resend = true;
                    }
                    else
                    {
                        continue;
                    }
                }

                IntPtr[] clients;
                lock (_lock)
                    clients = _clients.ToArray();

                foreach (var h in clients)
                {
                    if (!WriteAll(h, msg, 0, msgLen, out int error))
                        DropClient(h, WriteFailureReason(error));
                }
                if (clients.Length > 0 && !resend)
                    Interlocked.Increment(ref _sentCount);

                lock (_lock)
                {
                    if (!resend)
                    {
                        if (ReferenceEquals(_inWrite, msg))
                            _inWrite = null;

                        // Retain the freshly written message for future clients and
                        // recycle the one it replaces (nobody references it any more).
                        byte[]? previous = _lastMsg;
                        _lastMsg = msg;
                        _lastMsgLen = msgLen;
                        if (previous != null && !ReferenceEquals(previous, msg) &&
                            (_spare == null || _spare.Length != previous.Length))
                            _spare = previous;
                    }

                    if (_needFirstFrame && clients.Length > 0)
                        _needFirstFrame = false;
                }
            }
            catch (Exception ex)
            {
                // Never let the writer die. Log the type once per occurrence and
                // keep serving; the next publish simply overwrites _front.
                int count;
                lock (_lock) count = _clients.Count;
                AppLog.Write($"FramePublisher: writer recovered from {ex.GetType().Name}: {ex.Message} (clients={count})");
                try { Thread.Sleep(20); } catch { }
            }
        }
    }

    private static bool ConsumeOneShot(ref int flag) => Interlocked.Exchange(ref flag, 1) == 0;

    private static string WriteFailureReason(int error) => error switch
    {
        NativeInterop.ErrorBrokenPipe => "broken pipe",
        NativeInterop.ErrorNoData => "client closed",
        NativeInterop.ErrorTimeout => "write timeout",
        _ => $"win32 {error}"
    };

    /// <summary>
    /// Drops one client and its handle. When it was the last one the listening
    /// instance is destroyed and recreated, so the next connection cannot inherit
    /// a stale/broken instance.
    /// </summary>
    private void DropClient(IntPtr h, string reason)
    {
        int count;
        lock (_lock)
        {
            if (!_clients.Remove(h))
                return;
            count = _clients.Count;
        }

        NativeInterop.DisconnectNamedPipe(h);
        NativeInterop.CloseHandle(h);
        AppLog.Write($"FramePublisher({_pipeName}): client dropped ({reason}) (clients={count})");
    }

    public int ClientCount { get { lock (_lock) return _clients.Count; } }

    /// <summary>Total frames handed to this publisher. Monotonic; for diagnostics/tests.</summary>
    public long PublishCount => Interlocked.Read(ref _publishCount);

    private unsafe void AcceptLoop()
    {
        // Like the writer, this loop must survive anything: a failed create, a
        // client that dies mid-handshake, an exception. A dead accept loop used
        // to leave a listening instance that nobody served - the next client
        // connected and then never received a frame.
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                AcceptOnce();
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                AppLog.Write($"FramePublisher({_pipeName}): accept recovered from {ex.GetType().Name}: {ex.Message}");
                if (Backoff(200))
                    break;
            }
        }
    }

    /// <summary>
    /// Creates a listening instance, retrying with backoff while the name is
    /// temporarily unavailable (a previous instance still closing). Returns
    /// INVALID_HANDLE_VALUE only when the publisher is shutting down.
    /// </summary>
    private unsafe IntPtr CreateListener()
    {
        while (!_cts.IsCancellationRequested)
        {
            var sa = new SECURITY_ATTRIBUTES
            {
                nLength = sizeof(SECURITY_ATTRIBUTES),
                lpSecurityDescriptor = _securityDescriptor,
                bInheritHandle = 0
            };

            // FILE_FLAG_OVERLAPPED so ConnectNamedPipe can be cancelled and a
            // write to a wedged client can time out instead of blocking the
            // writer (and with it the whole slot) forever.
            IntPtr pipe = NativeInterop.CreateNamedPipeW(
                _pipeName,
                NativeInterop.PipeAccessDuplex | NativeInterop.FileFlagOverlapped,
                NativeInterop.PipeTypeByte | NativeInterop.PipeReadmodeByte,
                NativeInterop.PipeUnlimitedInstances,
                1 * 1024 * 1024, 0, 0, (IntPtr)(&sa));

            if (pipe != (IntPtr)(-1))
                return pipe;

            int createError = Marshal.GetLastWin32Error();
            if (ConsumeOneShot(ref _createErrorLogged))
                AppLog.Write($"FramePublisher({_pipeName}): CreateNamedPipe failed (win32 {createError}) -> retrying");
            if (Backoff(200))
                break;
        }
        return (IntPtr)(-1);
    }

    private unsafe void AcceptOnce()
    {
        // One instance is always listening: the next one is created as soon as a
        // client is accepted, before that client is configured. Otherwise a client
        // arriving while we were busy with the previous one (config write, first
        // frame) found no listening instance, waited for the next loop iteration
        // and saw hundreds of milliseconds of connect delay.
        IntPtr listener = CreateListener();

        while (!_cts.IsCancellationRequested && listener != (IntPtr)(-1))
        {
            if (!AcceptClient(listener))
            {
                CloseInstance(listener);
                listener = CreateListener();
                continue;
            }

            if (_cts.IsCancellationRequested)
            {
                CloseInstance(listener);
                break;
            }

            IntPtr pipe = listener;
            listener = CreateListener(); // start serving the next client immediately

            // Send config on connect.
            if (!WriteAll(pipe, _configMsg, 0, _configMsg.Length, out int configError))
            {
                // A client that vanishes mid-handshake is normal under churn: the
                // handle is dropped and the next listener is already waiting.
                AppLog.Write($"FramePublisher({_pipeName}): config send to new client failed ({WriteFailureReason(configError)}) -> instance recycled");
                CloseInstance(pipe);
                if (Backoff(50))
                    break;
                continue;
            }

            int count;
            lock (_lock)
            {
                _clients.Add(pipe);
                _needFirstFrame = true;
                count = _clients.Count;
            }
            AppLog.Write($"FramePublisher({_pipeName}): client connected (clients={count})");

            // Deliver the retained newest frame now: a client must never sit
            // connected without a frame until the next capture frame.
            _frameSignal.Set();
        }

        if (listener != (IntPtr)(-1))
            CloseInstance(listener);
    }

    /// <summary>Queues the latest NV12 frame for delivery to every connected client.</summary>
    private unsafe bool AcceptClient(IntPtr pipe)
    {
        var completed = new ManualResetEvent(false);
        IntPtr ov = Marshal.AllocHGlobal(Marshal.SizeOf<NativeInterop.OVERLAPPED>());
        try
        {
            var overlapped = new NativeInterop.OVERLAPPED
            {
                hEvent = completed.SafeWaitHandle.DangerousGetHandle()
            };
            Marshal.StructureToPtr(overlapped, ov, false);

            if (NativeInterop.ConnectNamedPipe(pipe, ov))
                return true;

            int error = Marshal.GetLastWin32Error();
            if (error == NativeInterop.ErrorPipeConnected)
                return true; // client connected between CreateNamedPipe and here
            if (error != NativeInterop.ErrorIoPending)
            {
                AppLog.Write($"FramePublisher({_pipeName}): ConnectNamedPipe failed (win32 {error}) -> instance recycled");
                return false;
            }

            // Every iteration owns a brand-new instance, so there is nothing to
            // recycle while waiting: stay here until a client connects (or we are
            // shutting down). Abandoning a connected instance here used to hand
            // clients a pipe that was closed underneath them.
            while (true)
            {
                if (WaitHandle.WaitAny(new WaitHandle[] { completed }, 100) == 0)
                {
                    if (NativeInterop.GetOverlappedResult(pipe, ov, out _, false))
                        return true;
                    int result = Marshal.GetLastWin32Error();
                    return result == NativeInterop.ErrorPipeConnected;
                }

                if (_cts.IsCancellationRequested)
                {
                    CancelPendingConnect(pipe, ov, completed);
                    return false;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ov);
            completed.Dispose();
        }
    }

    private unsafe void CancelPendingConnect(IntPtr pipe, IntPtr ov, ManualResetEvent completed)
    {
        NativeInterop.CancelIoEx(pipe, ov);
        completed.WaitOne(500); // drain the completion before the handle closes
    }

    private static void CloseInstance(IntPtr pipe)
    {
        NativeInterop.DisconnectNamedPipe(pipe);
        NativeInterop.CloseHandle(pipe);
    }

    /// <summary>Sleep that Dispose()/a client drop can cut short. Returns true when stopping.</summary>
    private bool Backoff(int milliseconds)
    {
        if (_cts.IsCancellationRequested)
            return true;
        WaitHandle.WaitAny(new WaitHandle[] { _lifecycleWake }, milliseconds);
        return _cts.IsCancellationRequested;
    }

    /// <summary>
    /// Writes the whole buffer through one overlapped operation chain. A client
    /// that stops draining is dropped (timeout), never waited for.
    /// </summary>
    private unsafe bool WriteAll(IntPtr pipe, byte[] data, int offset, int length, out int error)
    {
        error = 0;
        if (length <= 0)
            return true;

        var completed = new ManualResetEvent(false);
        IntPtr ov = Marshal.AllocHGlobal(Marshal.SizeOf<NativeInterop.OVERLAPPED>());
        int total = 0;
        try
        {
            fixed (byte* start = data)
            {
                while (total < length)
                {
                    var overlapped = new NativeInterop.OVERLAPPED
                    {
                        hEvent = completed.SafeWaitHandle.DangerousGetHandle()
                    };
                    Marshal.StructureToPtr(overlapped, ov, false);
                    completed.Reset();

                    // For overlapped I/O lpNumberOfBytesWritten must be NULL; the
                    // byte count comes from GetOverlappedResult.
                    if (!NativeInterop.WriteFile(pipe, (IntPtr)(start + offset + total), (uint)(length - total), IntPtr.Zero, ov))
                    {
                        error = Marshal.GetLastWin32Error();
                        if (error != NativeInterop.ErrorIoPending)
                            return false;

                        if (WaitHandle.WaitAny(new WaitHandle[] { completed }, kWriteTimeoutMs) != 0)
                        {
                            // Client stopped draining: drop it instead of blocking
                            // the writer (and with it the whole slot) forever.
                            error = NativeInterop.ErrorTimeout;
                            NativeInterop.CancelIoEx(pipe, ov);
                            completed.WaitOne(2000);
                            return false;
                        }
                    }

                    uint written;
                    if (!NativeInterop.GetOverlappedResult(pipe, ov, out written, false))
                    {
                        error = Marshal.GetLastWin32Error();
                        return false;
                    }

                    if (written == 0)
                    {
                        error = NativeInterop.ErrorBrokenPipe;
                        return false;
                    }
                    total += (int)written;
                }
            }
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(ov);
            completed.Dispose();
        }
    }

    private const int kWriteTimeoutMs = 1000;

    private static byte[] StructToBytes<T>(T value) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        byte[] buf = new byte[size];
        IntPtr p = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, p, false);
            Marshal.Copy(p, buf, 0, size);
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
        return buf;
    }

    private static byte[] BuildMessage(ushort type, byte[] payload)
    {
        var header = new WsMsgHeader { Magic = Protocol.Magic, Version = Protocol.Version, Type = type, PayloadLen = (uint)payload.Length };
        var hb = StructToBytes(header);
        byte[] msg = new byte[hb.Length + payload.Length];
        Buffer.BlockCopy(hb, 0, msg, 0, hb.Length);
        Buffer.BlockCopy(payload, 0, msg, hb.Length, payload.Length);
        return msg;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cts.Cancel();
        _lifecycleWake.Set();
        _frameSignal.Set();
        bool acceptStopped = true, writerStopped = true;
        try { acceptStopped = _acceptTask?.Wait(3000) ?? true; } catch { }
        try { writerStopped = _writerTask?.Wait(3000) ?? true; } catch { }

        double secs = (DateTime.UtcNow.Ticks - _startTicks) / (double)TimeSpan.TicksPerSecond;
        if (secs > 0)
            AppLog.Write($"FramePublisher: published {_publishCount} frames over {secs:F1}s = {_publishCount / secs:F1} fps");

        lock (_lock)
        {
            foreach (var h in _clients)
            {
                NativeInterop.DisconnectNamedPipe(h);
                NativeInterop.CloseHandle(h);
            }
            _clients.Clear();
        }
        if (_securityDescriptor != IntPtr.Zero)
            NativeInterop.LocalFree(_securityDescriptor);
        // Deliberately not disposing the events or (unless both loops have really
        // exited) the CTS: a loop that is still unwinding would throw
        // ObjectDisposedException on its next IsCancellationRequested check, and a
        // dead accept loop leaves a listening instance nobody serves - which is
        // exactly the "connected but never a frame" failure this class must not
        // have. A few leaked kernel events per pipeline rebuild are harmless.
        if (acceptStopped && writerStopped)
            _cts.Dispose();
    }
}
