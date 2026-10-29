using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WebcamSwitcher.Core;
using Button = System.Windows.Controls.Button;

namespace WebcamSwitcher.App;

public partial class MainWindow : Window
{
    private readonly PipelineController _pipeline;
    private readonly ObservableCollection<PreviewVm> _previews = new();
    private readonly object _renderLock = new();
    private readonly Dictionary<int, long> _lastRenderTicks = new();
    private readonly System.Windows.Threading.DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<(RoleChannel Channel, Action<byte[], int, int> Handler)> _previewHooks = new();

    // True while the global button is applying all slots at once. Chips are
    // locked during that time so the two controls cannot fight each other.

    public MainWindow(PipelineController pipeline)
    {
        InitializeComponent();
        _pipeline = pipeline;
        Previews.ItemsSource = _previews;
        _pipeline.SourcesChanged += OnSourcesChanged;
        _pipeline.VirtualCameraStateChanged += OnStateChanged;
        _pipeline.CaptureStateChanged += OnStateChanged;
        _pipeline.PreviewsStateChanged += OnStateChanged;
        _pipeline.VcamStateChanged += OnVcamStateChanged;

        _pipeline.RoleStatusChanged += () => Dispatcher.BeginInvoke(RefreshRoleStatus);

        RebuildPreviews();
        UpdateToggles();

        _statusTimer.Tick += (_, _) => RefreshRoleStatus();
        _statusTimer.Start();
        Closed += (_, _) => _statusTimer.Stop();

        // Minimised or hidden windows cannot show previews: stop converting them.
        // Frames keep flowing to the virtual cameras either way, and the first
        // frame after restore is converted normally.
        StateChanged += (_, _) => UpdatePreviewVisibility();
        IsVisibleChanged += (_, _) => UpdatePreviewVisibility();
        UpdatePreviewVisibility();
    }

    private void UpdatePreviewVisibility()
    {
        _pipeline.SetPreviewsVisible(IsVisible && WindowState != WindowState.Minimized);
    }

    private void OnStateChanged() => Dispatcher.BeginInvoke(UpdateToggles);

    private void UpdateToggles()
    {
        CaptureToggle.Content = _pipeline.CaptureActive ? "Cameras: ON" : "Cameras: OFF";
        PreviewsToggle.Content = _pipeline.PreviewsActive ? "Previews: ON" : "Previews: OFF";

        bool previews = _pipeline.PreviewsActive;
        PreviewScroller.Visibility = previews ? Visibility.Visible : Visibility.Collapsed;
        PreviewsOffText.Visibility = previews ? Visibility.Collapsed : Visibility.Visible;

        UpdateVcamToggle();
        UpdateStatus();
    }

    // ----- Virtual camera state -----

    private void OnVcamStateChanged()
    {
        if (Dispatcher.CheckAccess())
            RefreshVcamUi();
        else
            Dispatcher.BeginInvoke(RefreshVcamUi);
    }

    /// <summary>Pulls the real per-slot state into the tiles and the global button.</summary>
    private void RefreshVcamUi()
    {
        int count = _pipeline.CameraCount;

        for (int i = 0; i < _previews.Count; i++)
        {
            var vm = _previews[i];

            if (vm.Index >= 0 && vm.Index < count)
                vm.ApplyVcamState(_pipeline.IsVcamRunning(vm.Index), _pipeline.IsVcamBusy(vm.Index));
            else
                vm.ApplyVcamState(false, false); // camera list shrank under the tile
        }

        UpdateVcamToggle();
        UpdateStatus();
    }

    /// <summary>
    /// The main button owns the switcher virtual camera and nothing else. Each
    /// passthrough camera has its own switch on its preview tile, so starting or
    /// stopping the switcher never disturbs them.
    /// </summary>
    private void UpdateVcamToggle()
    {
        bool running = _pipeline.IsSwitcherVcamRunning;
        bool busy = _pipeline.IsSwitcherVcamBusy;

        VcamToggle.Content = busy
            ? "Virtual Camera: ..."
            : running
                ? "Virtual Camera: ON"
                : "Virtual Camera: OFF";

        VcamToggle.IsEnabled = !busy;
    }

    private async void VcamToggle_Click(object sender, RoutedEventArgs e)
    {
        bool turnOn = !_pipeline.IsSwitcherVcamRunning;
        UpdateVcamToggle();

        try
        {
            await _pipeline.SetSwitcherVcamRunningAsync(turnOn);
        }
        catch
        {
            // The pipeline reports failures through its state events.
        }

        RefreshVcamUi();
    }

    // ----- Per-tile switches -----

    private async void VcamChip_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // never let the chip click also select the tile
        if (sender is not Button { DataContext: PreviewVm vm })
            return;

        // Busy, locked by the global button, or a stale tile after a camera
        // change: ignore the click instead of starting overlapping work.
        if (!vm.VcamClickable || vm.Index < 0 || vm.Index >= _pipeline.CameraCount)
            return;

        // Capture the direction once: the chip words itself from this for the
        // whole call, since a stop clears Running long before it finishes.
        bool turnOn = !vm.VcamRunning;
        vm.BeginVcamChange(turnOn);

        bool ok;
        try
        {
            ok = await _pipeline.SetVcamRunningAsync(vm.Index, turnOn);
        }
        catch
        {
            ok = false;
        }

        RefreshVcamUi();

        if (ok)
            vm.ClearVcamFailure();
        else
            vm.MarkVcamFailed();
    }

    // ----- Previews -----

    private void PreviewsToggle_Click(object sender, RoutedEventArgs e)
    {
        _pipeline.SetPreviewsEnabled(!_pipeline.PreviewsActive);
        UpdateToggles();
    }

    private async void CaptureToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_pipeline.CaptureActive)
            await _pipeline.StopCaptureAsync();
        else
            await _pipeline.StartCaptureAsync();
        UpdateToggles();
    }

    private void OnSourcesChanged() => Dispatcher.BeginInvoke(RebuildPreviews);

    /// <summary>
    /// One tile per Camera Role, in configuration order, whether or not its
    /// camera is connected (a missing camera must stay visible as MISSING).
    /// Previews hook the role's channel, which survives reconnects.
    /// </summary>
    private void RebuildPreviews()
    {
        foreach (var (ch, h) in _previewHooks)
            ch.PreviewReady -= h;
        _previewHooks.Clear();
        _previews.Clear();
        lock (_renderLock)
            _lastRenderTicks.Clear();

        var channels = _pipeline.Channels.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
        var roles = _pipeline.Config.Cameras;
        for (int i = 0; i < roles.Count; i++)
        {
            int idx = i;
            var role = roles[i];
            var vm = new PreviewVm(idx, $"{role.Role}  ({role.VirtualCameraName})") { Key = role.VirtualCameraId };
            if (channels.TryGetValue(role.VirtualCameraId, out var ch))
            {
                Action<byte[], int, int> handler = (bgra, w, h) => OnPreview(idx, vm, bgra);
                ch.PreviewReady += handler;
                _previewHooks.Add((ch, handler));
            }
            _previews.Add(vm);
        }

        UpdateActive();
        RefreshVcamUi();
        RefreshRoleStatus();
    }

    private void RefreshRoleStatus()
    {
        var snaps = _pipeline.Snapshot().ToDictionary(r => r.VirtualCameraId, StringComparer.OrdinalIgnoreCase);
        foreach (var vm in _previews)
            if (snaps.TryGetValue(vm.Key, out var r))
                vm.ApplySnapshot(r);
        UpdateStatus();
    }

    private void OnPreview(int idx, PreviewVm vm, byte[] bgra)
    {
        long now = DateTime.UtcNow.Ticks;
        bool recent;
        lock (_renderLock)
        {
            recent = _lastRenderTicks.TryGetValue(idx, out var t) && (now - t) < TimeSpan.TicksPerMillisecond * 66;
            if (!recent)
                _lastRenderTicks[idx] = now;
        }
        if (recent)
            return; // ~15fps throttle

        var copy = new byte[bgra.Length];
        Buffer.BlockCopy(bgra, 0, copy, 0, bgra.Length);
        Dispatcher.BeginInvoke(() => Render(vm, copy));
    }

    private void Render(PreviewVm vm, byte[] bgra)
    {
        var bmp = vm.Bitmap;
        bmp.WritePixels(new Int32Rect(0, 0, bmp.PixelWidth, bmp.PixelHeight), bgra, bmp.PixelWidth * 4, 0);
    }

    private void Preview_Click(object sender, MouseButtonEventArgs e)
    {
        // Clicks that land on the chip never select the camera.
        if (IsInsideButton(e.OriginalSource as DependencyObject))
            return;

        if (sender is Border b && b.Tag is int idx)
        {
            _pipeline.ActiveIndex = idx;
            UpdateActive();
        }
    }

    private static bool IsInsideButton(DependencyObject? node)
    {
        while (node != null)
        {
            if (node is Button)
                return true;

            node = node is Visual || node is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    private void UpdateActive()
    {
        int active = _pipeline.ActiveIndex;
        for (int i = 0; i < _previews.Count; i++)
            _previews[i].IsActive = i == active;
    }

    private void UpdateStatus()
    {
        var cfg = _pipeline.Config;
        var snaps = _pipeline.Snapshot();
        int connected = snaps.Count(r => r.CameraStatus == RoleStatus.Connected);
        var cam = _pipeline.CaptureActive
            ? $"{connected}/{snaps.Count} role(s) connected · out {cfg.Width}x{cfg.Height} @ {cfg.Fps}fps NV12"
            : "capture stopped";
        if (_pipeline.ProcessStats is { } p)
            cam += $" · CPU {p.CpuPercent:0.0}% · mem {p.PrivateMB} MB";

        int passthrough = 0;
        for (int i = 0; i < _pipeline.CameraCount; i++)
            if (_pipeline.IsVcamRunning(i))
                passthrough++;

        string switcher = _pipeline.IsSwitcherVcamRunning ? "switcher on" : "switcher off";
        string pass = _pipeline.CameraCount == 0
            ? "no passthrough cameras"
            : $"passthrough {passthrough}/{_pipeline.CameraCount} on";

        StatusText.Text = $"{cam} · {switcher} · {pass}";
    }

    private void Register_Click(object sender, RoutedEventArgs e)
    {
        new RegistrationWindow(_pipeline) { Owner = this }.ShowDialog();
        RebuildPreviews();
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppLog.Directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppLog.Directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Write($"OpenLog: {ex.Message}");
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        new SettingsWindow(_pipeline) { Owner = this }.ShowDialog();
        UpdateActive();
        UpdateStatus();
    }
}
