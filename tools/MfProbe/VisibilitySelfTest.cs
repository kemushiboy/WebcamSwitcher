using WebcamSwitcher.Core;

namespace WebcamSwitcher.MfProbe;

/// <summary>
/// Exercises the window-visibility → engine → source preview flag path without
/// cameras or pipes: verifies the flags combine correctly and that concurrent
/// toggling (the window state changing while frames are in flight) never throws.
/// </summary>
internal static class VisibilitySelfTest
{
    public static int Run()
    {
        // No publishers and no switcher: nothing here starts a pipe or a thread.
        var engine = new CameraEngine(null!, new DevicePresenceMonitor());

        bool ok = true;
        ok &= Expect("defaults: visible", engine.PreviewsVisible, true);
        ok &= Expect("defaults: enabled", engine.PreviewsEnabled, true);

        engine.PreviewsVisible = false;
        ok &= Expect("minimised", engine.PreviewsVisible, false);

        engine.PreviewsVisible = true;
        ok &= Expect("restored", engine.PreviewsVisible, true);

        engine.PreviewsEnabled = false;
        ok &= Expect("user disabled previews", engine.PreviewsEnabled, false);
        engine.PreviewsVisible = false;
        engine.PreviewsVisible = true;
        ok &= Expect("user disable survives visibility", engine.PreviewsEnabled, false);
        engine.PreviewsEnabled = true;

        // Race smoke: two threads toggling while another reads, as when the user
        // minimises/restores while capture threads are converting.
        int errors = 0;
        var stop = DateTime.UtcNow.AddMilliseconds(300);
        var t1 = new Thread(() => { try { while (DateTime.UtcNow < stop) engine.PreviewsVisible = !engine.PreviewsVisible; } catch { Interlocked.Increment(ref errors); } });
        var t2 = new Thread(() => { try { while (DateTime.UtcNow < stop) _ = engine.PreviewsVisible; } catch { Interlocked.Increment(ref errors); } });
        t1.Start(); t2.Start(); t1.Join(); t2.Join();
        ok &= Expect("no exceptions under concurrent toggle", errors, 0);

        Console.WriteLine(ok
            ? "RESULT: PASS (visibility flag path is consistent and exception-free)"
            : "RESULT: FAIL");
        return ok ? 0 : 1;
    }

    private static bool Expect<T>(string what, T actual, T expected)
    {
        bool ok = EqualityComparer<T>.Default.Equals(actual, expected);
        Console.WriteLine($"  {what,-45} {(ok ? "OK" : $"MISMATCH (got {actual}, want {expected})")}");
        return ok;
    }
}
