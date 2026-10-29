param([int]$Rescans = 8, [string]$Exe = (Join-Path $PSScriptRoot "..\artifacts\app\WebcamSwitcher.exe"))
# Opens the Camera registration window of a running/started app via UI
# Automation, hammers Re-scan while previews are still opening, saves, and
# reports whether the app survived. Reproduces the "Collection was modified"
# and duplicate-key crashes seen with several cameras / unassigned roles.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [System.Windows.Automation.AutomationElement]
function ById($root, $id) { $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition $A::AutomationIdProperty, $id)) }
function Invoke($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }

$p = Get-Process WebcamSwitcher -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { $p = Start-Process $Exe -PassThru; Start-Sleep 8 }
$main = $A::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition $A::ProcessIdProperty, $p.Id))
# Open the dialog from a job: Invoke blocks while the modal dialog is open.
$job = Start-Job { param($id) Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes; $A = [System.Windows.Automation.AutomationElement]
    $m = $A::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition $A::ProcessIdProperty, $id))
    $m.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition $A::AutomationIdProperty, "RegisterButton")).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } -ArgumentList $p.Id
Start-Sleep 2
$reg = $A::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition $A::NameProperty, "Camera registration"))
if (-not $reg) { "registration window not found"; exit 2 }
$clicked = 0
for ($i = 0; $i -lt $Rescans; $i++) {
    $btn = ById $reg "RescanButton"
    try { if ($btn.Current.IsEnabled) { Invoke $btn; $clicked++ } } catch {}
    Start-Sleep -Milliseconds 150
}
Start-Sleep 3
try { Invoke (ById $reg "SaveApplyButton") } catch { "save click: $($_.Exception.Message)" }
Start-Sleep 4
try { Invoke (ById $reg "RescanButton") } catch {}
Start-Sleep 3
$alive = -not (Get-Process -Id $p.Id -ErrorAction SilentlyContinue).HasExited
"rescan clicks accepted: $clicked; app alive: $alive"
try { Invoke (ById $reg "CloseButton") } catch {}
Remove-Job $job -Force -ErrorAction SilentlyContinue
