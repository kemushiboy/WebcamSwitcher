using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WebcamSwitcher.Core;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using Image = System.Windows.Controls.Image;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace WebcamSwitcher.App;

/// <summary>
/// Registers physical cameras to Camera Roles. Every connected camera is listed
/// by device ID with a live picture (from its role's running capture, or a
/// temporary preview for a camera no role uses yet), so identical models with
/// identical friendly names can be told apart.
/// </summary>
public partial class RegistrationWindow : Window
{
    private const string Unassigned = "(not assigned)";

    private readonly PipelineController _pipeline;
    private readonly AppConfig _draft;
    private readonly List<DeviceRow> _deviceRows = new();
    private readonly List<RoleRow> _roleRows = new();
    private readonly System.Windows.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public RegistrationWindow(PipelineController pipeline)
    {
        InitializeComponent();
        _pipeline = pipeline;
        _draft = pipeline.Config.Clone();
        Loaded += async (_, _) => await LoadAsync();
        Closed += async (_, _) =>
        {
            _timer.Stop();
            await ReleaseTemporaryPreviewsAsync();
            foreach (var r in _deviceRows)
                r.Unhook();
        };
        _timer.Tick += (_, _) => RefreshDeviceStatus();
    }

    // ------------------------------------------------------------------ load

    private async Task LoadAsync()
    {
        BuildRoleRows();
        await RescanAsync();
        _timer.Start();
    }

    private async Task RescanAsync()
    {
        await ReleaseTemporaryPreviewsAsync();
        foreach (var r in _deviceRows)
            r.Unhook();
        _deviceRows.Clear();
        DevicesPanel.Children.Clear();

        List<PhysicalCamera> cams;
        try
        {
            cams = await CameraEngine.EnumeratePhysicalCamerasAsync();
        }
        catch (Exception ex)
        {
            ErrorText.Text = "Camera enumeration failed: " + ex.Message;
            return;
        }
        AppLog.Write("Registration", $"scan: {cams.Count} physical camera(s)");

        var channels = _pipeline.Channels.ToDictionary(c => c.Config.DeviceId ?? "", StringComparer.OrdinalIgnoreCase);
        foreach (var cam in cams)
        {
            var owner = _draft.Cameras.FirstOrDefault(c => string.Equals(c.DeviceId, cam.DeviceId, StringComparison.OrdinalIgnoreCase));
            channels.TryGetValue(cam.DeviceId, out var channel);
            var row = new DeviceRow(this, cam, owner?.Role, channel);
            _deviceRows.Add(row);
            DevicesPanel.Children.Add(row.Panel);
        }

        // Registered devices that are not connected right now: visible, never reassigned.
        foreach (var c in _draft.Cameras.Where(c => c.IsAssigned &&
                     !cams.Any(p => string.Equals(p.DeviceId, c.DeviceId, StringComparison.OrdinalIgnoreCase))))
        {
            DevicesPanel.Children.Add(new TextBlock
            {
                Text = $"{c.Role}: registered device '{c.DisplayDeviceName}' {CameraRoles.DeviceIdTail(c.DeviceId)} is NOT connected (MISSING). " +
                       "Plug it into the same USB port, or assign another camera to this role explicitly.",
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x00)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 6)
            });
        }

        if (cams.Count == 0)
            DevicesPanel.Children.Add(new TextBlock { Text = "No physical camera found.", Foreground = Brushes.Gray });

        RefreshRoleChoices();
        foreach (var row in _deviceRows)
            await row.StartPreviewAsync();
        RefreshDeviceStatus();
    }

    private async Task ReleaseTemporaryPreviewsAsync()
    {
        foreach (var r in _deviceRows)
            await r.StopTemporaryPreviewAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RescanAsync();

    private void RefreshDeviceStatus()
    {
        var snaps = _pipeline.Snapshot();
        foreach (var r in _deviceRows)
            r.RefreshStatus(snaps);
    }

    // ----------------------------------------------------------------- roles

    private void BuildRoleRows()
    {
        RolesPanel.Children.Clear();
        _roleRows.Clear();
        foreach (var c in _draft.Cameras)
            AddRoleRow(c);
    }

    private void AddRoleRow(CameraConfig c)
    {
        var row = new RoleRow(this, c);
        _roleRows.Add(row);
        RolesPanel.Children.Add(row.Panel);
    }

    private void AddRole_Click(object sender, RoutedEventArgs e)
    {
        var all = _roleRows.Select(r => new CameraConfig { Role = r.RoleBox.Text.Trim() }).ToList();
        var c = CameraRoles.CreateRole(CameraRoles.NextFreeRole(all));
        _draft.Cameras.Add(c);
        AddRoleRow(c);
        RefreshRoleChoices();
    }

    internal void RemoveRole(RoleRow row)
    {
        _roleRows.Remove(row);
        _draft.Cameras.Remove(row.Camera);
        RolesPanel.Children.Remove(row.Panel);
        RefreshRoleChoices();
    }

    /// <summary>Role names as currently typed, for the device rows' dropdowns.</summary>
    private void RefreshRoleChoices()
    {
        var names = _roleRows.Select(r => r.RoleBox.Text.Trim()).Where(n => n.Length > 0).ToList();
        foreach (var d in _deviceRows)
            d.SetChoices(names);
    }

    internal void OnRoleTextChanged(RoleRow row, string oldName, string newName)
    {
        foreach (var d in _deviceRows)
            d.RenameChoice(oldName, newName);
        RefreshRoleChoices();
    }

    // ------------------------------------------------------------------ save

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        var next = _pipeline.Config.Clone();
        var byId = next.Cameras.ToDictionary(c => c.VirtualCameraId, StringComparer.OrdinalIgnoreCase);

        // Role table edits (identity = VirtualCameraId).
        var cameras = new List<CameraConfig>();
        foreach (var row in _roleRows)
        {
            var c = byId.TryGetValue(row.Camera.VirtualCameraId, out var existing) ? existing : row.Camera.Clone();
            c.Role = row.RoleBox.Text.Trim();
            c.VirtualCameraName = row.NameBox.Text.Trim();
            c.VirtualCameraEnabled = row.EnabledBox.IsChecked == true;
            if (row.Unassign)
            {
                c.DeviceId = null;
                c.PhysicalFriendlyName = "";
            }
            cameras.Add(c);
        }

        // Device -> role assignments from the connected-camera list.
        var problems = new List<string>();
        var chosen = new Dictionary<string, DeviceRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in _deviceRows)
        {
            string? role = d.SelectedRole;
            // A camera taken out of its role: that role becomes unassigned.
            foreach (var c in cameras.Where(c => string.Equals(c.DeviceId, d.Camera.DeviceId, StringComparison.OrdinalIgnoreCase) &&
                                                 !string.Equals(c.Role, role, StringComparison.OrdinalIgnoreCase)))
            {
                c.DeviceId = null;
                c.PhysicalFriendlyName = "";
            }
            if (role == null)
                continue;
            if (chosen.TryGetValue(role, out var other))
            {
                problems.Add($"{role} is selected for two cameras ({CameraRoles.DeviceIdTail(other.Camera.DeviceId)} and {CameraRoles.DeviceIdTail(d.Camera.DeviceId)}).");
                continue;
            }
            chosen[role] = d;
            var target = cameras.FirstOrDefault(c => string.Equals(c.Role, role, StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                problems.Add($"Role {role} does not exist.");
                continue;
            }
            target.DeviceId = d.Camera.DeviceId;
            target.PhysicalFriendlyName = d.Camera.FriendlyName;
        }

        next.Cameras = cameras;
        problems.AddRange(CameraRoles.Validate(cameras).Select(i => i.Message));
        if (problems.Count > 0)
        {
            ErrorText.Text = string.Join(Environment.NewLine, problems.Distinct());
            return;
        }

        SaveBtn.IsEnabled = false;
        try
        {
            // Free every temporarily previewed camera first: it may now belong to a role.
            await ReleaseTemporaryPreviewsAsync();
            await _pipeline.ApplyAsync(next);
            AppLog.Write("Registration", $"saved {next.Cameras.Count} role(s): " +
                string.Join(", ", next.Cameras.Select(c => $"{c.Role}='{c.VirtualCameraName}'<-{CameraRoles.DeviceIdTail(c.DeviceId)}")));
            _draft.Cameras = next.Clone().Cameras;
            BuildRoleRows();
            await RescanAsync();
            ErrorText.Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0xC3, 0x4A));
            ErrorText.Text = "Saved. Virtual cameras updated.";
        }
        catch (Exception ex)
        {
            ErrorText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80));
            ErrorText.Text = "Could not apply: " + ex.Message;
        }
        finally
        {
            SaveBtn.IsEnabled = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ============================================================ device row

    private sealed class DeviceRow
    {
        private readonly RegistrationWindow _owner;
        private readonly RoleChannel? _channel;
        private readonly WriteableBitmap _bitmap = new(CameraSource.PreviewWidth, CameraSource.PreviewHeight, 96, 96, PixelFormats.Bgra32, null);
        private readonly TextBlock _status;
        private readonly ComboBox _roleCombo;
        private CameraSource? _temp;
        private long _lastRender;
        private Action<byte[], int, int>? _hook;

        public PhysicalCamera Camera { get; }
        public Border Panel { get; }

        public string? SelectedRole =>
            _roleCombo.SelectedItem is string s && s != Unassigned ? s : null;

        public DeviceRow(RegistrationWindow owner, PhysicalCamera cam, string? role, RoleChannel? channel)
        {
            _owner = owner;
            Camera = cam;
            _channel = channel;

            var image = new Image { Source = _bitmap, Width = 240, Height = 135, Stretch = Stretch.Uniform };
            var info = new StackPanel { Margin = new Thickness(12, 0, 0, 0), Width = 560 };
            info.Children.Add(new TextBlock { Text = cam.FriendlyName, Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 14 });
            info.Children.Add(new TextBlock
            {
                Text = "ID tail: " + CameraRoles.DeviceIdTail(cam.DeviceId),
                Foreground = new SolidColorBrush(Color.FromRgb(0x90, 0xCA, 0xF9)),
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 13,
                Margin = new Thickness(0, 2, 0, 0)
            });
            var idBox = new TextBox
            {
                Text = cam.DeviceId,
                IsReadOnly = true,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 10.5,
                Background = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB)),
                BorderThickness = new Thickness(0),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
                ToolTip = "Full Windows device ID (symbolic link)"
            };
            info.Children.Add(idBox);
            var copy = new Button { Content = "Copy full device ID", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), Foreground = Brushes.White };
            copy.Click += (_, _) =>
            {
                try { Clipboard.SetText(cam.DeviceId); } catch { }
            };
            info.Children.Add(copy);
            _status = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)), Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
            info.Children.Add(_status);

            var assign = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
            assign.Children.Add(new TextBlock { Text = "Camera Role", Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)) });
            _roleCombo = new ComboBox { Width = 140, Margin = new Thickness(0, 4, 0, 0) };
            System.Windows.Automation.AutomationProperties.SetAutomationId(_roleCombo, "RoleFor_" + CameraRoles.DeviceIdTail(cam.DeviceId));
            assign.Children.Add(_roleCombo);
            _initialRole = role;

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(image);
            row.Children.Add(info);
            row.Children.Add(assign);
            Panel = new Border
            {
                Child = row,
                Padding = new Thickness(8),
                Margin = new Thickness(0, 0, 0, 8),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x18))
            };
        }

        private string? _initialRole;

        public void SetChoices(List<string> roles)
        {
            string? current = SelectedRole ?? _initialRole;
            _initialRole = null;
            var items = new List<string> { Unassigned };
            items.AddRange(roles);
            _roleCombo.ItemsSource = items;
            _roleCombo.SelectedItem = current != null && roles.Contains(current, StringComparer.OrdinalIgnoreCase)
                ? roles.First(r => string.Equals(r, current, StringComparison.OrdinalIgnoreCase))
                : Unassigned;
        }

        public void RenameChoice(string oldName, string newName)
        {
            if (string.Equals(SelectedRole, oldName, StringComparison.OrdinalIgnoreCase))
                _initialRole = newName;
        }

        /// <summary>Live picture: the role's capture if running, else a short-lived private capture.</summary>
        public async Task StartPreviewAsync()
        {
            if (_channel != null)
            {
                _hook = (bgra, w, h) => OnPreview(bgra);
                _channel.PreviewReady += _hook;
                _channel.PreviewEnabled = true;
                return;
            }

            var src = new CameraSource(Camera.DeviceId, "preview " + Camera.FriendlyName) { PreviewEnabled = true, ScaleMode = ScaleModes.Letterbox };
            src.PreviewReady += (bgra, w, h) => OnPreview(bgra);
            _status.Text = "opening a temporary preview…";
            bool ok = await src.StartAsync(640, 360);
            if (ok)
            {
                _temp = src;
                _status.Text = $"temporary preview: {src.InputWidth}x{src.InputHeight} {src.InputSubtype} @ {src.InputNominalFps:0.#} fps (not assigned to a role)";
            }
            else
            {
                _status.Text = $"cannot preview: {src.LastFailure} - {src.LastFailureDetail}";
                await src.DisposeAsync();
            }
        }

        public async Task StopTemporaryPreviewAsync()
        {
            var t = _temp;
            _temp = null;
            if (t != null)
                await t.DisposeAsync();
        }

        public void Unhook()
        {
            if (_channel != null && _hook != null)
                _channel.PreviewReady -= _hook;
            _hook = null;
        }

        private void OnPreview(byte[] bgra)
        {
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastRender) < 100)
                return;
            Interlocked.Exchange(ref _lastRender, now);
            var copy = (byte[])bgra.Clone();
            _owner.Dispatcher.BeginInvoke(() =>
                _bitmap.WritePixels(new Int32Rect(0, 0, _bitmap.PixelWidth, _bitmap.PixelHeight), copy, _bitmap.PixelWidth * 4, 0));
        }

        public void RefreshStatus(IReadOnlyList<RoleSnapshot> snaps)
        {
            if (_channel == null)
                return;
            var r = snaps.FirstOrDefault(s => string.Equals(s.VirtualCameraId, _channel.Key, StringComparison.OrdinalIgnoreCase));
            if (r == null)
                return;
            _status.Text = $"{r.Role}: {RoleStatusText.Label(r.Status)} · input {r.InputWidth}x{r.InputHeight} {r.InputPixelFormat} " +
                           $"{r.InputFps:0.0}/{r.InputNominalFps:0.#} fps · output {r.OutputWidth}x{r.OutputHeight}@{r.OutputFps}" +
                           (r.Warning.Length > 0 ? $"\n! {r.Warning}" : r.Detail.Length > 0 ? $"\n{r.Detail}" : "");
        }
    }

    // ============================================================== role row

    internal sealed class RoleRow
    {
        public CameraConfig Camera { get; }
        public TextBox RoleBox { get; }
        public TextBox NameBox { get; }
        public CheckBox EnabledBox { get; }
        public bool Unassign { get; private set; }
        public Border Panel { get; }

        public RoleRow(RegistrationWindow owner, CameraConfig c)
        {
            Camera = c;
            var fg = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
            RoleBox = new TextBox { Text = c.Role, Width = 90, Margin = new Thickness(4, 0, 12, 0) };
            NameBox = new TextBox { Text = c.VirtualCameraName, Width = 150, Margin = new Thickness(4, 0, 12, 0) };
            EnabledBox = new CheckBox { Content = "Virtual camera on", IsChecked = c.VirtualCameraEnabled, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            System.Windows.Automation.AutomationProperties.SetAutomationId(RoleBox, "RoleBox_" + c.VirtualCameraId);
            System.Windows.Automation.AutomationProperties.SetAutomationId(NameBox, "VcamNameBox_" + c.VirtualCameraId);

            string previous = c.Role;
            RoleBox.TextChanged += (_, _) =>
            {
                string now = RoleBox.Text.Trim();
                // Keep the virtual camera name in step while it mirrors the role.
                if (string.Equals(NameBox.Text.Trim(), previous, StringComparison.Ordinal))
                    NameBox.Text = now;
                owner.OnRoleTextChanged(this, previous, now);
                previous = now;
            };

            var device = new TextBlock
            {
                Text = c.IsAssigned ? $"{c.DisplayDeviceName}  {CameraRoles.DeviceIdTail(c.DeviceId)}" : "(no device)",
                Foreground = fg,
                Width = 300,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = c.DeviceId ?? ""
            };
            var unassign = new Button { Content = "Unassign device", Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 6, 0), Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), Foreground = Brushes.White, IsEnabled = c.IsAssigned };
            unassign.Click += (_, _) =>
            {
                Unassign = true;
                device.Text = "(device will be unassigned on save)";
                unassign.IsEnabled = false;
            };
            var remove = new Button { Content = "Remove role", Padding = new Thickness(6, 2, 6, 2), Background = new SolidColorBrush(Color.FromRgb(0x5A, 0x24, 0x24)), Foreground = Brushes.White };
            remove.Click += (_, _) => owner.RemoveRole(this);

            var row = new WrapPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = "Role", Foreground = fg, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(RoleBox);
            row.Children.Add(new TextBlock { Text = "Virtual Camera Name", Foreground = fg, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(NameBox);
            row.Children.Add(EnabledBox);
            row.Children.Add(device);
            row.Children.Add(unassign);
            row.Children.Add(remove);
            var outer = new StackPanel();
            outer.Children.Add(row);
            outer.Children.Add(new TextBlock
            {
                Text = $"Virtual Camera ID {c.VirtualCameraId}  ·  pipe {Protocol.RolePipeName(c.VirtualCameraId)}",
                Foreground = Brushes.Gray,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 10.5,
                Margin = new Thickness(0, 3, 0, 0)
            });
            Panel = new Border
            {
                Child = outer,
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 6),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22))
            };
        }
    }
}
