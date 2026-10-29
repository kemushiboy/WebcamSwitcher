param(
    [int]$Minutes = 120,
    [int]$IntervalSeconds = 30,
    [string]$OutCsv = "soak-$(Get-Date -Format yyyyMMdd-HHmmss).csv"
)
# Samples system-level resource use during a long run of WebcamSwitcher and
# writes one CSV row per interval:
#   time, total CPU %, WebcamSwitcher CPU % / private MB / handles / threads,
#   Frame Server (svchost hosting FrameServer) CPU % / private MB,
#   GPU utilisation % (sum of 3D + VideoProcessing engines), available RAM MB.
# Per-role fps / drops / reconnects come from the app's own PERF log lines
# (%LOCALAPPDATA%\WebcamSwitcher\logs). Use both together for a soak report.
$ErrorActionPreference = "SilentlyContinue"
$cores = [Environment]::ProcessorCount
function FrameServerPid {
    (Get-CimInstance Win32_Service -Filter "Name='FrameServer'").ProcessId
}
function CpuTimes($id) { $p = Get-Process -Id $id; if ($p) { $p.TotalProcessorTime.TotalSeconds } else { 0 } }

"time,total_cpu_pct,app_cpu_pct,app_private_mb,app_handles,app_threads,fs_cpu_pct,fs_private_mb,gpu_pct,avail_ram_mb" | Set-Content $OutCsv
$end = (Get-Date).AddMinutes($Minutes)
$app = Get-Process WebcamSwitcher | Select-Object -First 1
$fsPid = FrameServerPid
$lastApp = CpuTimes $app.Id; $lastFs = CpuTimes $fsPid; $lastT = Get-Date
while ((Get-Date) -lt $end) {
    Start-Sleep -Seconds $IntervalSeconds
    $now = Get-Date; $dt = ($now - $lastT).TotalSeconds
    $app = Get-Process WebcamSwitcher | Select-Object -First 1
    $fsPid = FrameServerPid
    $a = CpuTimes $app.Id; $f = CpuTimes $fsPid
    $appCpu = [math]::Round(($a - $lastApp) / $dt / $cores * 100, 2)
    $fsCpu = [math]::Round(($f - $lastFs) / $dt / $cores * 100, 2)
    $lastApp = $a; $lastFs = $f; $lastT = $now
    $total = [math]::Round((Get-Counter '\Processor(_Total)\% Processor Time').CounterSamples[0].CookedValue, 2)
    $gpu = (Get-Counter '\GPU Engine(*)\Utilization Percentage').CounterSamples |
        Where-Object { $_.InstanceName -match 'engtype_3d|engtype_videoprocessing' } |
        Measure-Object CookedValue -Sum
    $avail = (Get-Counter '\Memory\Available MBytes').CounterSamples[0].CookedValue
    $fs = Get-Process -Id $fsPid
    "{0},{1},{2},{3},{4},{5},{6},{7},{8},{9}" -f $now.ToString("s"), $total, $appCpu,
        [math]::Round($app.PrivateMemorySize64 / 1MB), $app.HandleCount, $app.Threads.Count,
        $fsCpu, [math]::Round($fs.PrivateMemorySize64 / 1MB), [math]::Round($gpu.Sum, 2), $avail |
        Add-Content $OutCsv
}
"done: $OutCsv"
