using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WebcamSwitcher.Core;
using MessageBox = System.Windows.MessageBox;
using Panel = System.Windows.Controls.Panel;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using StackPanel = System.Windows.Controls.StackPanel;
using Orientation = System.Windows.Controls.Orientation;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Thickness = System.Windows.Thickness;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace WebcamSwitcher.App;

public partial class SettingsWindow : Window
{
    private readonly PipelineController _pipeline;
    private readonly List<CameraRow> _rows = new();

    public SettingsWindow(PipelineController pipeline)
    {
        InitializeComponent();
        _pipeline = pipeline;
        Loaded += (_, _) => Initialize();

        // Delay boxes are live while typing; undo anything not applied.
        Closed += (_, _) =>
        {
            if (DialogResult != true)
                RevertLiveDelays();
        };
    }

    private void RevertLiveDelays()
    {
        var config = _pipeline.Config;
        _pipeline.ApplyDelaysLive(config.CommonDelayMs, config.Cameras.Select(c => c.DelayMs).ToList());
    }

    private void Initialize()
    {
        var config = _pipeline.Config;

        WidthCombo.ItemsSource = new[] { "3840", "1920", "1280", "960", "640" };
        WidthCombo.Text = config.Width.ToString();
        HeightCombo.ItemsSource = new[] { "2160", "1080", "720", "540", "480" };
        HeightCombo.Text = config.Height.ToString();
        FpsCombo.ItemsSource = new[] { "60", "30", "24", "15" };
        FpsCombo.Text = config.Fps.ToString();

        RetryIntervalBox.Text = config.Reconnect.RetryIntervalMs.ToString();
        RetryCountBox.Text = config.Reconnect.RetryCount.ToString();
        RetryForeverBox.IsChecked = config.Reconnect.RetryForever;
        StartupWaitBox.Text = config.Reconnect.StartupWaitMs.ToString();
        NoSignalBox.Text = config.Reconnect.NoSignalTimeoutMs.ToString();

        RotateHotkeyBox.Text = config.RotateHotkey ?? "";
        CommonDelayBox.Text = config.CommonDelayMs.ToString();
        StartWithWindowsBox.IsChecked = config.StartWithWindows;
        StartMinimizedBox.IsChecked = config.StartMinimized;
        CaptureEnabledBox.IsChecked = config.CaptureEnabled;
        AutoRegisterBox.IsChecked = config.AutoRegisterVirtualCameras;
        RestartOnCrashBox.IsChecked = config.RestartOnCrash;
        SwitcherBox.IsChecked = config.SwitcherVirtualCameraEnabled;
        PreferSmooth30FpsBox.IsChecked = config.PreferSmooth30Fps;
        PerfIntervalBox.Text = config.PerformanceLogIntervalSeconds.ToString();
        LogFilesBox.Text = config.LogMaxFiles.ToString();
        LogSizeBox.Text = config.LogMaxFileMB.ToString();

        for (int i = 0; i < config.Cameras.Count; i++)
        {
            var hotkey = config.Hotkeys is { Length: > 0 } && i < config.Hotkeys.Length ? config.Hotkeys[i] ?? "" : "";
            var row = new CameraRow(config.Cameras[i], hotkey, i);
            row.DelayBox.TextChanged += (_, _) => ApplyDelaysLiveNow();
            _rows.Add(row);
            CamerasPanel.Children.Add(row.Panel);
        }
        if (config.Cameras.Count == 0)
            CamerasPanel.Children.Add(new TextBlock { Text = "No roles yet - use Camera registration.", Foreground = Brushes.Gray });

        NoteText.Text = "Changing a role's capture options restarts only that role. Changing the output format re-creates every virtual camera (consumers must re-open them). Startup options take effect at the next launch.";
        CommonDelayBox.TextChanged += (_, _) => ApplyDelaysLiveNow();
    }

    private void ApplyDelaysLiveNow()
    {
        if (!int.TryParse(CommonDelayBox.Text.Trim(), out int common) || common < 0 || common > AppConfig.MaxDelayMs)
            return;
        var delays = new List<int>(_rows.Count);
        foreach (var row in _rows)
        {
            if (!int.TryParse(row.DelayBox.Text.Trim(), out int ms) || ms < 0 || ms > AppConfig.MaxDelayMs)
                return;
            delays.Add(ms);
        }
        _pipeline.ApplyDelaysLive(common, delays);
    }

    private bool Warn(string text, string title)
    {
        MessageBox.Show(this, text, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private static bool Int(TextBox box, int min, int max, out int value) =>
        int.TryParse(box.Text.Trim(), out value) && value >= min && value <= max;

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(WidthCombo.Text, out int w) || !int.TryParse(HeightCombo.Text, out int h) || !int.TryParse(FpsCombo.Text, out int fps))
        {
            Warn("Width, Height, and FPS must be numbers.", "Invalid output");
            return;
        }
        if (!Int(CommonDelayBox, 0, AppConfig.MaxDelayMs, out int commonDelayMs))
        {
            Warn($"Common video delay must be 0-{AppConfig.MaxDelayMs} ms.", "Invalid delay");
            return;
        }
        if (!Int(RetryIntervalBox, 250, 600_000, out int retryMs) || !Int(RetryCountBox, 0, 1_000_000, out int retryCount) ||
            !Int(StartupWaitBox, 0, 600_000, out int startupMs) || !Int(NoSignalBox, 500, 60_000, out int noSignalMs))
        {
            Warn("Reconnect: interval 250-600000 ms, count 0-1000000, startup wait 0-600000 ms, no-signal 500-60000 ms.", "Invalid reconnect settings");
            return;
        }
        if (!Int(PerfIntervalBox, 0, 3600, out int perf) || !Int(LogFilesBox, 1, 100, out int logFiles) || !Int(LogSizeBox, 1, 100, out int logMb))
        {
            Warn("Performance log interval 0-3600 s, log files 1-100, log size 1-100 MB.", "Invalid log settings");
            return;
        }

        var next = _pipeline.Config.Clone();
        next.Width = w;
        next.Height = h;
        next.Fps = fps;
        next.CommonDelayMs = commonDelayMs;
        next.Reconnect.RetryIntervalMs = retryMs;
        next.Reconnect.RetryCount = retryCount;
        next.Reconnect.RetryForever = RetryForeverBox.IsChecked == true;
        next.Reconnect.StartupWaitMs = startupMs;
        next.Reconnect.NoSignalTimeoutMs = noSignalMs;
        next.PerformanceLogIntervalSeconds = perf;
        next.LogMaxFiles = logFiles;
        next.LogMaxFileMB = logMb;

        var hotkeys = new List<string>();
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            var cam = next.Cameras.FirstOrDefault(c => c.VirtualCameraId == row.Key);
            if (cam == null)
                continue;
            if (!string.IsNullOrWhiteSpace(row.HotkeyBox.Text) && Hotkey.Parse(row.HotkeyBox.Text) == null)
            {
                Warn($"Invalid hotkey '{row.HotkeyBox.Text}' for {cam.Role}. Use e.g. 'Ctrl+Alt+1', or leave it empty.", "Invalid hotkey");
                return;
            }
            if (!Int(row.DelayBox, 0, AppConfig.MaxDelayMs, out int delayMs))
            {
                Warn($"Delay for {cam.Role} must be 0-{AppConfig.MaxDelayMs} ms.", "Invalid delay");
                return;
            }
            int? capFps = null;
            if (!string.IsNullOrWhiteSpace(row.FpsBox.Text) && !row.FpsBox.Text.Trim().Equals("Auto", StringComparison.OrdinalIgnoreCase))
            {
                if (!Int(row.FpsBox, 1, 120, out int f))
                {
                    Warn($"Capture FPS for {cam.Role} must be Auto or 1-120.", "Invalid capture FPS");
                    return;
                }
                capFps = f;
            }
            var (cw, ch) = row.SelectedCapture;
            cam.CaptureWidth = cw;
            cam.CaptureHeight = ch;
            cam.CaptureFps = capFps;
            cam.CaptureFormat = row.SelectedFormat;
            cam.ScaleMode = ScaleModes.Normalize(row.ScaleCombo.Text);
            cam.DelayMs = delayMs;
            hotkeys.Add(row.HotkeyBox.Text.Trim());
        }

        if (!string.IsNullOrWhiteSpace(RotateHotkeyBox.Text) && Hotkey.Parse(RotateHotkeyBox.Text) == null)
        {
            Warn("Invalid rotate hotkey.", "Invalid hotkey");
            return;
        }

        next.Hotkeys = hotkeys.ToArray();
        next.RotateHotkey = RotateHotkeyBox.Text.Trim();
        next.StartWithWindows = StartWithWindowsBox.IsChecked == true;
        next.StartMinimized = StartMinimizedBox.IsChecked == true;
        next.CaptureEnabled = CaptureEnabledBox.IsChecked == true;
        next.AutoRegisterVirtualCameras = AutoRegisterBox.IsChecked == true;
        next.RestartOnCrash = RestartOnCrashBox.IsChecked == true;
        next.SwitcherVirtualCameraEnabled = SwitcherBox.IsChecked == true;
        next.PreferSmooth30Fps = PreferSmooth30FpsBox.IsChecked == true;
        // CaptureEnabled is the persisted "auto-start capture" choice; the running
        // capture state is changed with the main window's Cameras button.
        bool captureWasRunning = _pipeline.CaptureActive;

        try
        {
            await _pipeline.ApplyAsync(next);
            if (!captureWasRunning && next.CaptureEnabled)
                AppLog.Write("Settings: capture auto-start enabled (takes effect at next launch or via the Cameras button)");
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Failed to apply settings: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private sealed class CameraRow
    {
        public string Key { get; }
        public Panel Panel { get; }
        public ComboBox CaptureCombo { get; }
        public TextBox FpsBox { get; }
        public ComboBox FormatCombo { get; }
        public ComboBox ScaleCombo { get; }
        public TextBox DelayBox { get; }
        public TextBox HotkeyBox { get; }

        private static readonly string[] CaptureSizes =
        {
            "Auto", "1920x1080", "1280x720", "1024x576", "960x720", "800x600", "640x480", "640x360"
        };

        private static readonly string[] FormatOptions = { "Auto", "Nv12", "Mjpg" };

        public (int? W, int? H) SelectedCapture
        {
            get
            {
                var s = CaptureCombo.Text?.Trim();
                if (!string.IsNullOrEmpty(s) && !s.Equals("Auto", StringComparison.OrdinalIgnoreCase))
                {
                    var p = s.Split('x');
                    if (p.Length == 2 && int.TryParse(p[0], out int cw) && int.TryParse(p[1], out int ch))
                        return (cw, ch);
                }
                return (null, null);
            }
        }

        public string SelectedFormat
        {
            get
            {
                var s = FormatCombo.Text?.Trim();
                if (string.Equals(s, "Nv12", StringComparison.OrdinalIgnoreCase))
                    return "Nv12";
                if (string.Equals(s, "Mjpg", StringComparison.OrdinalIgnoreCase))
                    return "Mjpg";
                return "Auto";
            }
        }

        private static TextBlock Label(string text) => new()
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 4, 0)
        };

        public CameraRow(CameraConfig cam, string hotkey, int index)
        {
            Key = cam.VirtualCameraId;
            var title = new TextBlock
            {
                Text = $"{cam.Role}",
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                Width = 64,
                VerticalAlignment = VerticalAlignment.Center
            };
            var device = new TextBlock
            {
                Text = cam.IsAssigned ? CameraRoles.DeviceIdTail(cam.DeviceId) : "(unassigned)",
                Foreground = Brushes.Gray,
                Width = 170,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = cam.DeviceId ?? ""
            };

            CaptureCombo = new ComboBox
            {
                Width = 96,
                IsEditable = true,
                ItemsSource = CaptureSizes,
                Text = cam.CaptureWidth is int sw && cam.CaptureHeight is int sh ? $"{sw}x{sh}" : "Auto",
                ToolTip = "Capture resolution. Auto: a mode reaching the output fps first, then the closest aspect and size."
            };
            FpsBox = new TextBox { Width = 40, Text = cam.CaptureFps?.ToString() ?? "Auto", ToolTip = "Capture fps request (Auto = output fps)." };
            FormatCombo = new ComboBox
            {
                Width = 70,
                IsEditable = true,
                ItemsSource = FormatOptions,
                Text = string.IsNullOrWhiteSpace(cam.CaptureFormat) ? "Auto" : cam.CaptureFormat,
                ToolTip = "Auto keeps uncompressed when it reaches the rate and never assumes MJPEG exists. Mjpg forces the compressed stream."
            };
            ScaleCombo = new ComboBox { Width = 86, ItemsSource = ScaleModes.All, Text = ScaleModes.Normalize(cam.ScaleMode), IsEditable = true, ToolTip = "How a different aspect (e.g. 640x480) is fitted into the output." };
            DelayBox = new TextBox { Width = 46, Text = Math.Clamp(cam.DelayMs, 0, AppConfig.MaxDelayMs).ToString() };
            HotkeyBox = new TextBox { Width = 90, Text = hotkey };
            System.Windows.Automation.AutomationProperties.SetAutomationId(DelayBox, "DelayBox" + index);
            System.Windows.Automation.AutomationProperties.SetAutomationId(ScaleCombo, "ScaleCombo" + index);

            Panel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            Panel.Children.Add(title);
            Panel.Children.Add(device);
            Panel.Children.Add(Label("Capture"));
            Panel.Children.Add(CaptureCombo);
            Panel.Children.Add(Label("fps"));
            Panel.Children.Add(FpsBox);
            Panel.Children.Add(Label("Format"));
            Panel.Children.Add(FormatCombo);
            Panel.Children.Add(Label("Fit"));
            Panel.Children.Add(ScaleCombo);
            Panel.Children.Add(Label("Delay"));
            Panel.Children.Add(DelayBox);
            Panel.Children.Add(Label("Hotkey"));
            Panel.Children.Add(HotkeyBox);
        }
    }
}
