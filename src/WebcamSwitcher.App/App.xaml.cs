using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using WebcamSwitcher.Core;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace WebcamSwitcher.App;

public partial class App : Application
{
    private PipelineController? _pipeline;
    private NotifyIcon? _tray;
    private ToolStripMenuItem[]? _trayCameraItems;
    private MainWindow? _mainWindow;
    private AppConfig _config = new();
    private Mutex? _singleInstance;
    private System.Windows.Threading.DispatcherTimer? _heartbeat;
    private EventWaitHandle? _activateEvent;

    private volatile bool _shuttingDown;
    private int _teardownStarted;
    private bool _forceStart;

    // Keep in sync with the task-scheduler action / Run registry value so a
    // stale instance can be identified and reaped. The heartbeat file lets a
    // new instance tell a live-but-idle process from a genuinely hung one.
    private const string ProcessName = "WebcamSwitcher";
    private const string SingleInstanceName = @"Local\WebcamSwitcher.SingleInstance";
    private const string ActivateEventName = @"Local\WebcamSwitcher.Activate";
    private const string ExitRequestEventName = @"Local\WebcamSwitcher.ExitRequest";
    private const string HeartbeatFileName = "WebcamSwitcher.heartbeat";
    private const int HeartbeatFreshMs = 4000;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // `--exit` asks a running instance to quit cleanly (used by scripts and
        // shutdown automation); it must not start a pipeline itself.
        if (e.Args.Any(a => string.Equals(a, "--exit", StringComparison.OrdinalIgnoreCase)))
        {
            AppLog.Write("Startup: --exit requested; signalling running instance");
            RequestExistingExit();
            Environment.Exit(0);
            return;
        }

        if (!await ClaimOrRecoverSingleInstanceAsync())
        {
            // ShutdownMode=OnExplicitShutdown would otherwise leave a windowless
            // message loop alive. Nothing is initialised on these paths, so exit
            // hard (Shutdown() from OnStartup is not reliable).
            Environment.Exit(0);
            return;
        }

        _config = ConfigService.Load();
        AppLog.Configure(_config.LogMaxFiles, _config.LogMaxFileMB);
        AppLog.Write("App", $"=== WebcamSwitcher {typeof(App).Assembly.GetName().Version} started (pid {Environment.ProcessId}, args '{string.Join(" ", e.Args)}', config '{ConfigService.Path}', log '{AppLog.CurrentPath}') ===");
        HookUnhandledExceptions();
        if (_config.RestartOnCrash)
            RegisterForRestart();

        _pipeline = new PipelineController();
        _pipeline.SourcesChanged += OnSourcesChanged;

        // Bring the UI up immediately so the window is responsive while the
        // pipeline (virtual cameras + capture devices) initialises in the
        // background. The window shows an empty/starting state, then refreshes
        // when PipelineController.StartAsync raises SourcesChanged.
        StartHeartbeat();
        StartActivateListener();
        StartExitListener();
        CreateTray();
        if (_config.StartMinimized)
            AppLog.Write("App", "starting minimized to the tray");
        else
            ShowMain();

        try
        {
            await StartPipelineAsync();
        }
        catch (Exception ex)
        {
            AppLog.Write($"Startup: pipeline init failed: {ex}");
        }

        // First run: nothing is registered yet, and nothing is adopted
        // automatically - open the registration screen instead.
        if (_config.Cameras.Count == 0 && !_config.StartMinimized)
        {
            ShowMain();
            new RegistrationWindow(_pipeline) { Owner = _mainWindow }.ShowDialog();
        }
    }

    private static void HookUnhandledExceptions()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            AppLog.Write("App", $"FATAL unhandled exception: {args.ExceptionObject}");
            AppLog.Flush();
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Write("App", $"unobserved task exception: {args.Exception.GetBaseException().GetType().Name}: {args.Exception.GetBaseException().Message}");
            args.SetObserved();
        };
        Current.DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Write("App", $"UI thread exception: {args.Exception}");
            AppLog.Flush();
        };
    }

    // Windows Error Reporting relaunches a process that registered itself and
    // crashed or hung after running for at least 60 s.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string? commandLine, int flags);

    private static void RegisterForRestart()
    {
        try
        {
            int hr = RegisterApplicationRestart("--restarted", 0);
            AppLog.Write("App", hr == 0 ? "registered for automatic restart after a crash/hang" : $"RegisterApplicationRestart failed hr=0x{hr:X8}");
        }
        catch (Exception ex)
        {
            AppLog.Write("App", $"RegisterApplicationRestart unavailable: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------------
    // Single instance + stale/hung instance recovery
    // ---------------------------------------------------------------------

    private async Task<bool> ClaimOrRecoverSingleInstanceAsync()
    {
        AppLog.Write("Startup: checking single instance");
        if (TryClaimMutex())
            return true;

        var existing = FindOtherInstance();
        if (existing == null)
        {
            // The holder died between acquiring and looking it up; one more try.
            return TryClaimMutex();
        }

        return await ResolveExistingInstanceAsync(existing, allowPrompt: true);
    }

    private bool TryClaimMutex()
    {
        try
        {
            var m = new Mutex(initiallyOwned: false, SingleInstanceName, out bool isNew);
            bool owned;
            try { owned = m.WaitOne(0); }
            catch (AbandonedMutexException) { owned = true; }

            if (isNew || owned)
            {
                _singleInstance = m;
                return true;
            }
            m.Dispose();
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Write($"Startup: mutex claim failed: {ex.Message}");
            return false;
        }
    }

    private static Process? FindOtherInstance()
    {
        int self = Environment.ProcessId;
        try
        {
            return Process.GetProcessesByName(ProcessName).FirstOrDefault(p => p.Id != self);
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> ResolveExistingInstanceAsync(Process existing, bool allowPrompt)
    {
        int pid = existing.Id;

        // Never touch an instance running in another Windows session (e.g. a
        // different logged-in user); we can't safely reap it and shouldn't.
        try
        {
            if (existing.SessionId != Process.GetCurrentProcess().SessionId)
            {
                AppLog.Write($"Startup: instance pid={pid} is in another session; exiting.");
                return false;
            }
        }
        catch { /* SessionId unavailable: fall through and use heartbeat */ }

        bool healthy = IsInstanceHealthy(existing, out string detail);
        if (healthy && !_forceStart)
        {
            AppLog.Write($"Startup: existing instance pid={pid} is live ({detail}); activating it and exiting.");
            SignalActivate();
            return false;
        }

        // A stale heartbeat already tells us this process is hung: don't waste
        // 3s asking it to close politely, go straight to termination.
        bool stopped = await StopInstanceAsync(existing, pid, hung: !healthy);
        if (!stopped)
        {
            if (!allowPrompt)
            {
                AppLog.Write($"Startup: instance pid={pid} still running after graceful+force kill; giving up.");
                return false;
            }

            AppLog.Write($"Startup: instance pid={pid} did not stop; prompting user.");
            var choice = ShowRecoveryDialog(pid);
            if (choice == RecoveryChoice.Exit)
                return false;

            _forceStart = true;
            return await ResolveExistingInstanceAsync(existing, allowPrompt: false);
        }

        // Wait for the dead process to release the mutex (and, indirectly, the
        // cameras / named pipes) before we proceed.
        for (int i = 0; i < 20; i++)
        {
            if (TryClaimMutex())
            {
                AppLog.Write($"Startup: reaped stale instance pid={pid} and reclaimed single instance.");
                return true;
            }
            await Task.Delay(250);
        }

        AppLog.Write("Startup: could not reclaim single instance after reaping; exiting.");
        return false;
    }

    private static bool IsInstanceHealthy(Process p, out string detail)
    {
        detail = "no heartbeat";
        try
        {
            var path = Path.Combine(Path.GetTempPath(), HeartbeatFileName);
            if (File.Exists(path))
            {
                var parts = File.ReadAllText(path).Split('|');
                if (parts.Length == 2 && int.TryParse(parts[0], out int pid) && long.TryParse(parts[1], out long ms))
                {
                    if (pid == p.Id)
                    {
                        long age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ms;
                        detail = $"heartbeat {age} ms old";
                        return age < HeartbeatFreshMs;
                    }
                    // Heartbeat names a different PID — this process is stale.
                    detail = $"heartbeat owned by pid {pid}";
                    return false;
                }
            }

            // No usable heartbeat (e.g. this is an older build, or the file was
            // deleted): fall back to process liveness rather than killing a
            // possibly-healthy instance. A responding window means it's alive.
            try
            {
                if (!p.HasExited && p.Responding)
                {
                    detail = "process responding (no heartbeat)";
                    return true;
                }
            }
            catch { }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> StopInstanceAsync(Process p, int pid, bool hung = false)
    {
        // A hung process (stale heartbeat) won't service a close request, so
        // skip the polite phase and terminate it directly.
        if (!hung)
        {
            try { if (!p.HasExited) p.CloseMainWindow(); } catch { }
            if (await WaitForExitAsync(p, 3000))
                return true;
        }
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        return await WaitForExitAsync(p, 7000);
    }

    private static async Task<bool> WaitForExitAsync(Process p, int ms)
    {
        try { return await Task.Run(() => p.WaitForExit(ms)); }
        catch { return true; }
    }

    private enum RecoveryChoice { Retry, Exit }

    private static RecoveryChoice ShowRecoveryDialog(int pid)
    {
        var text =
            "A previous WebcamSwitcher process (PID " + pid + ") could not be stopped.\n\n" +
            "It may be holding the cameras or the virtual camera.\n\n" +
            "Choose Yes to try starting anyway (camera access may still fail),\n" +
            "or No to close this instance and end the other process manually.";
        var result = MessageBox.Show(
            text, "WebcamSwitcher", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return result == MessageBoxResult.Yes ? RecoveryChoice.Retry : RecoveryChoice.Exit;
    }

    private static void SignalActivate()
    {
        for (int i = 0; i < 6; i++)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var ev))
                {
                    ev.Set();
                    ev.Dispose();
                    return;
                }
            }
            catch { }
            Thread.Sleep(250);
        }
    }

    private static void RequestExistingExit()
    {
        for (int i = 0; i < 12; i++)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ExitRequestEventName, out var ev))
                {
                    ev.Set();
                    ev.Dispose();
                    return;
                }
            }
            catch { }
            Thread.Sleep(250);
        }
    }

    // ---------------------------------------------------------------------
    // Heartbeat + activation listener (running instance side)
    // ---------------------------------------------------------------------

    private void StartHeartbeat()
    {
        WriteHeartbeat();
        _heartbeat = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _heartbeat.Tick += (_, _) => WriteHeartbeat();
        _heartbeat.Start();
    }

    private static void WriteHeartbeat()
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), HeartbeatFileName);
            File.WriteAllText(
                path,
                $"{Environment.ProcessId}|{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
        }
        catch { }
    }

    private void StartActivateListener()
    {
        try
        {
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        }
        catch
        {
            return;
        }

        var thread = new Thread(() =>
        {
            while (!_shuttingDown)
            {
                try { _activateEvent.WaitOne(); }
                catch { return; }
                if (_shuttingDown)
                    return;
                Dispatcher.BeginInvoke(ActivateMainWindow);
            }
        }) { IsBackground = true, Name = "WebcamSwitcher.Activate" };
        thread.Start();
    }

    private void StartExitListener()
    {
        EventWaitHandle? exitEvent;
        try
        {
            exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitRequestEventName);
        }
        catch
        {
            return;
        }

        var thread = new Thread(() =>
        {
            while (!_shuttingDown)
            {
                try { exitEvent.WaitOne(); }
                catch { return; }
                if (_shuttingDown)
                    return;
                AppLog.Write("ExitListener: clean shutdown requested");
                Dispatcher.BeginInvoke(() => Shutdown());
                return;
            }
        }) { IsBackground = true, Name = "WebcamSwitcher.ExitListener" };
        thread.Start();
    }

    // ---------------------------------------------------------------------
    // Pipeline startup
    // ---------------------------------------------------------------------

    private async Task StartPipelineAsync()
    {
        // No automatic adoption of "the first cameras found": roles are only
        // ever bound to a device ID the user registered.
        await _pipeline!.StartAsync(_config);
    }

    private void OnSourcesChanged()
    {
        Dispatcher.BeginInvoke(RebuildTrayMenu);
    }

    // ---------------------------------------------------------------------
    // Window + tray
    // ---------------------------------------------------------------------

    private void ShowMain()
    {
        if (_mainWindow == null)
        {
            _mainWindow = new MainWindow(_pipeline!);
            _mainWindow.Closed += (_, _) => _mainWindow = null;
        }
        _mainWindow.Show();
        _mainWindow.Activate();
    }

    private void ActivateMainWindow()
    {
        ShowMain();
        if (_mainWindow == null)
            return;
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
        // Nudge the window to the foreground without leaving it always-on-top.
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
    }

    private void CreateTray()
    {
        _tray = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "WebcamSwitcher",
            Visible = true
        };
        _tray.DoubleClick += (_, _) => ShowMain();

        var menu = new ContextMenuStrip();
        _tray.ContextMenuStrip = menu;

        RebuildTrayMenu();

        menu.Opening += (_, _) => UpdateTrayChecks();
    }

    private void RebuildTrayMenu()
    {
        if (_tray?.ContextMenuStrip == null)
            return;

        var menu = _tray.ContextMenuStrip;
        menu.Items.Clear();

        var roles = _pipeline?.Config.Cameras ?? new List<CameraConfig>();
        _trayCameraItems = new ToolStripMenuItem[roles.Count];
        for (int i = 0; i < roles.Count; i++)
        {
            int idx = i;
            var item = new ToolStripMenuItem($"{roles[i].Role} ({roles[i].VirtualCameraName})", null, (_, _) => { if (_pipeline != null) _pipeline.ActiveIndex = idx; });
            _trayCameraItems[i] = item;
            menu.Items.Add(item);
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Camera registration", null, (_, _) => OpenRegistration());
        menu.Items.Add("Settings", null, (_, _) => OpenSettings());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());
    }

    private void UpdateTrayChecks()
    {
        if (_trayCameraItems == null || _pipeline == null)
            return;
        for (int i = 0; i < _trayCameraItems.Length; i++)
            _trayCameraItems[i].Checked = _pipeline.ActiveIndex == i;
    }

    private void OpenRegistration()
    {
        ShowMain();
        if (_pipeline != null)
            new RegistrationWindow(_pipeline) { Owner = _mainWindow }.ShowDialog();
    }

    private void OpenSettings()
    {
        ShowMain();
        if (_pipeline != null)
            new SettingsWindow(_pipeline) { Owner = _mainWindow }.ShowDialog();
    }

    // ---------------------------------------------------------------------
    // Shutdown (bounded, so the process always fully ends)
    // ---------------------------------------------------------------------

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        AppLog.Write("SessionEnding: OS is ending the session");
        if (Interlocked.Exchange(ref _teardownStarted, 1) == 0)
        {
            try { RunTeardown(TimeSpan.FromSeconds(5)); } catch { }
        }
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (Interlocked.Exchange(ref _teardownStarted, 1) == 0)
        {
            try { RunTeardown(TimeSpan.FromSeconds(7)); }
            catch (Exception ex) { AppLog.Write($"OnExit: teardown error: {ex}"); }
        }
        ArmShutdownWatchdog();
        base.OnExit(e);
    }

    private void RunTeardown(TimeSpan budget)
    {
        AppLog.Write("Shutdown: teardown begin");
        _shuttingDown = true;
        try { _heartbeat?.Stop(); } catch { }
        try { _activateEvent?.Set(); } catch { }
        try { _tray?.Dispose(); } catch { } _tray = null;

        // Remove our heartbeat so a future launch never mistakes this dead
        // process for a live one.
        try { File.Delete(Path.Combine(Path.GetTempPath(), HeartbeatFileName)); } catch { }

        // Hotkeys/HwndSource are UI-affine; dispose them on this (UI) thread.
        try { _pipeline?.DisposeHotkeys(); } catch { }

        // Everything else (camera release, pipe close, vcam stop, config save)
        // is I/O-bound and must be bounded: if it stalls, we still exit rather
        // than hang forever holding the cameras.
        var work = Task.Run(async () =>
        {
            try { if (_pipeline != null) await _pipeline.DisposeAsync(); } catch { }
            try { ConfigService.Save(_pipeline?.Config ?? _config); } catch { }
        });

        if (!work.Wait(budget))
            AppLog.Write($"Shutdown: teardown exceeded {budget.TotalSeconds:F0}s; continuing");
        AppLog.Write("App", "=== WebcamSwitcher exited ===");
        AppLog.Flush();
    }

    // Belt-and-braces: if WPF's shutdown path ever stalls (e.g. a background
    // task refuses to die), terminate the process so it never lingers holding
    // the cameras, which was the cause of the "won't start again" symptom.
    private static void ArmShutdownWatchdog()
    {
        var thread = new Thread(() =>
        {
            Thread.Sleep(4000);
            AppLog.Write("Shutdown watchdog: process still alive; forcing exit");
            Environment.Exit(0);
        }) { IsBackground = true, Name = "WebcamSwitcher.ShutdownWatchdog" };
        thread.Start();
    }
}
