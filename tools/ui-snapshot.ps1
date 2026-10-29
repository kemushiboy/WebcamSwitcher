param([string]$OutDir = (Join-Path $PSScriptRoot "..\docs\images"))
# Captures the running app's main window and the Camera registration window
# (opened via UI Automation) to PNG files. Run in the interactive session.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Win {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@
New-Item -ItemType Directory -Force $OutDir | Out-Null
function Snap([IntPtr]$h, [string]$file) {
    $r = New-Object Win+RECT; [void][Win]::GetWindowRect($h, [ref]$r)
    $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc()
    [void][Win]::PrintWindow($h, $hdc, 2); $g.ReleaseHdc($hdc); $g.Dispose()
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    "saved $file"
}
$root = [System.Windows.Automation.AutomationElement]::RootElement
$p = Get-Process WebcamSwitcher | Select-Object -First 1
$cond = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ProcessIdProperty), $p.Id
$main = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
$h = [IntPtr]$main.Current.NativeWindowHandle
[void][Win]::ShowWindow($h, 9); [void][Win]::SetForegroundWindow($h); Start-Sleep 2
Snap $h (Join-Path $OutDir "main-window.png")
$btnCond = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::AutomationIdProperty), "RegisterButton"
$btn = $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
$inv = $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
# Invoke on a worker: the button opens a modal dialog and would block this call.
$job = Start-Job -ScriptBlock { param($pid2) Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $c = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ProcessIdProperty), $pid2
    $m = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $c)
    $bc = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::AutomationIdProperty), "RegisterButton"
    $m.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $bc).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } -ArgumentList $p.Id
Start-Sleep 6
$titleCond = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty), "Camera registration"
$reg = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $titleCond)
if ($reg) {
    $rh = [IntPtr]$reg.Current.NativeWindowHandle
    [void][Win]::SetForegroundWindow($rh); Start-Sleep 2
    Snap $rh (Join-Path $OutDir "registration-window.png")
    $closeCond = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::AutomationIdProperty), "CloseButton"
    $reg.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $closeCond).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
} else { "registration window not found" }
Remove-Job $job -Force -ErrorAction SilentlyContinue
