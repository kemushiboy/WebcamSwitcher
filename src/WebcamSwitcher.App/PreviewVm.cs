using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WebcamSwitcher.Core;

namespace WebcamSwitcher.App;

public sealed class PreviewVm : INotifyPropertyChanged
{
    public int Index { get; }
    public string Label { get; set; } = "";
    public WriteableBitmap Bitmap { get; }

    /// <summary>Persistent ID of the role this tile shows (identity, not the index).</summary>
    public string Key { get; init; } = "";

    // ----- Role status (refreshed once a second from the pipeline snapshot) -----

    private string _statusText = "";
    private string _statusColor = "#8A8A8A";
    private string _stats = "";
    private string _detail = "";

    public string StatusText => _statusText;
    public string StatusColor => _statusColor;
    public string Stats => _stats;
    public string Detail => _detail;

    public void ApplySnapshot(RoleSnapshot r)
    {
        string status = RoleStatusText.Label(r.Status);
        string color = r.Status switch
        {
            RoleStatus.Connected => "#4CAF50",
            RoleStatus.Connecting or RoleStatus.Reconnecting or RoleStatus.NoSignal => "#FFB300",
            RoleStatus.VirtualCameraStopped or RoleStatus.Unassigned => "#8A8A8A",
            _ => "#E05252",
        };
        string last = r.LastFrameUtc == DateTime.MinValue ? "never" : r.LastFrameUtc.ToLocalTime().ToString("HH:mm:ss");
        string stats = r.CameraStatus == RoleStatus.Connected || r.InputWidth > 0
            ? $"in {r.InputWidth}x{r.InputHeight} {r.InputPixelFormat} {r.InputFps:0.0}/{r.InputNominalFps:0.#}fps  ->  out {r.OutputWidth}x{r.OutputHeight} {r.OutputFpsMeasured:0.0}/{r.OutputFps}fps\n" +
              $"recv {r.FramesReceived}  drop {r.DroppedFrames}  last {last}  proc {r.ProcessMicros / 1000.0:0.00}ms  reconn {r.Reconnects}"
            : $"last frame {last}  reconn {r.Reconnects}";
        string vcam = !r.VirtualCameraRunning ? "vcam off" : r.VirtualCameraInUse ? "vcam IN USE" : "vcam idle";
        stats = $"{r.PhysicalFriendlyName}  {r.DeviceIdTail}\n{stats}\n{vcam}";
        string detail = r.Warning.Length > 0 ? "! " + r.Warning : r.Detail;

        if (status == _statusText && color == _statusColor && stats == _stats && detail == _detail)
            return;
        _statusText = status;
        _statusColor = color;
        _stats = stats;
        _detail = detail;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusColor)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Stats)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Detail)));
    }

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive != value)
            {
                _isActive = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
            }
        }
    }

    // ----- Per-camera virtual camera chip -----

    private bool _vcamRunning;
    private bool _vcamBusy;
    private bool _vcamFailed;
    private bool _vcamLocked;

    // Direction of the change this chip started (true = turning on), held for
    // the whole busy period. A stop clears Running as soon as the native stop
    // begins while Busy stays set, so the direction cannot be read back from
    // Running without the chip briefly claiming it is starting.
    private bool? _vcamChangeTarget;

    // Running state at the moment a switch failed, so a later real change
    // (from the global button or the backend) clears the red "failed" chip.
    private bool? _failedWhileRunning;

    public bool VcamRunning => _vcamRunning;
    public bool VcamBusy => _vcamBusy;
    public bool VcamFailed => _vcamFailed;

    /// <summary>False while this slot is switching or the global switch is running.</summary>
    public bool VcamClickable => !_vcamBusy && !_vcamLocked;

    /// <summary>
    /// True while the in-flight change is a start. Uses the direction captured
    /// at BeginVcamChange; falls back to the real state for a busy period the
    /// chip did not start.
    /// </summary>
    private bool VcamChangeIsStart => _vcamChangeTarget ?? _vcamRunning;

    public string VcamText =>
        _vcamBusy ? (VcamChangeIsStart ? "vcam starting" : "vcam stopping")
        : _vcamFailed ? "vcam failed"
        : _vcamRunning ? "vcam on"
        : "vcam off";

    public string VcamHint =>
        "Off removes this virtual camera from other apps' device lists, such as OBS or TikTok, until you switch it back on.";

    public string VcamAutomationId => $"VcamChip_{Index}";

    public string VcamAutomationName
    {
        get
        {
            string state = _vcamBusy
                ? (VcamChangeIsStart ? "starting" : "stopping")
                : _vcamFailed ? "failed to switch"
                : _vcamRunning ? "on"
                : "off";
            string name = string.IsNullOrWhiteSpace(Label) ? $"Camera {Index + 1}" : Label;
            return $"{name} virtual camera, {state}";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public PreviewVm(int index, string label)
    {
        Index = index;
        Label = label;
        Bitmap = new WriteableBitmap(CameraSource.PreviewWidth, CameraSource.PreviewHeight, 96, 96, PixelFormats.Bgra32, null);
    }

    /// <summary>
    /// Applies the real state from the pipeline. A failure stays visible until
    /// the running state actually changes, then clears itself.
    /// </summary>
    public void ApplyVcamState(bool running, bool busy)
    {
        bool changed = _vcamRunning != running || _vcamBusy != busy;
        _vcamRunning = running;
        _vcamBusy = busy;

        // Ends of the busy period drop the captured direction; anything busy
        // after that is not this chip's change and words itself from the
        // real state.
        if (!busy)
            _vcamChangeTarget = null;

        if (_vcamFailed && _failedWhileRunning.HasValue && running != _failedWhileRunning.Value)
        {
            _vcamFailed = false;
            _failedWhileRunning = null;
            changed = true;
        }

        if (changed)
            RaiseVcamChanged();
    }

    /// <summary>
    /// Marks the chip busy before the call starts, so a second click is ignored,
    /// and remembers which way it is going for the busy wording.
    /// </summary>
    public void BeginVcamChange(bool turnOn)
    {
        _vcamFailed = false;
        _failedWhileRunning = null;
        _vcamChangeTarget = turnOn;
        _vcamBusy = true;
        RaiseVcamChanged();
    }

    public void MarkVcamFailed()
    {
        _vcamFailed = true;
        _failedWhileRunning = _vcamRunning;
        RaiseVcamChanged();
    }

    public void ClearVcamFailure()
    {
        if (!_vcamFailed)
            return;
        _vcamFailed = false;
        _failedWhileRunning = null;
        RaiseVcamChanged();
    }

    /// <summary>Locks every chip while the global switch is applying.</summary>
    public void SetVcamLocked(bool locked)
    {
        if (_vcamLocked == locked)
            return;
        _vcamLocked = locked;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VcamClickable)));
    }

    private void RaiseVcamChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VcamRunning)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VcamBusy)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VcamFailed)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VcamClickable)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VcamText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VcamAutomationName)));
    }
}
