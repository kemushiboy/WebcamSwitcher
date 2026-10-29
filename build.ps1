param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# Locate MSBuild via vswhere (falls back to a known path).
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = $null
if (Test-Path $vswhere) {
    $vs = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
    if ($vs) { $msbuild = Join-Path $vs "MSBuild\Current\Bin\amd64\MSBuild.exe" }
}
if (-not $msbuild -or -not (Test-Path $msbuild)) {
    $msbuild = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\amd64\MSBuild.exe"
}

# 1. Restore C++ NuGet packages.
$nuget = Join-Path $root "tools\nuget.exe"
if (-not (Test-Path $nuget)) {
    New-Item -ItemType Directory -Force (Split-Path $nuget) | Out-Null
    Invoke-WebRequest -Uri "https://dist.nuget.org/win-x86-commandline/latest/nuget.exe" -OutFile $nuget
}
& $nuget restore (Join-Path $root "src\WebcamSwitcher.Source\WebcamSwitcher.Source.vcxproj") -SolutionDirectory $root -NonInteractive
if ($LASTEXITCODE -ne 0) { throw "nuget restore failed" }

# 2. Build the C++ media source.
& $msbuild (Join-Path $root "src\WebcamSwitcher.Source\WebcamSwitcher.Source.vcxproj") /p:Configuration=$Configuration /p:Platform=x64 /m /v:m /nologo
if ($LASTEXITCODE -ne 0) { throw "C++ build failed" }

# 3. Publish the WPF app (self-contained, no .NET runtime required).
$appOut = Join-Path $root "artifacts\app"
& dotnet publish (Join-Path $root "src\WebcamSwitcher.App\WebcamSwitcher.App.csproj") -c $Configuration -r win-x64 --self-contained true -o $appOut
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# 4. Copy the media source DLL next to the app.
#    The Frame Server (svchost) keeps this DLL mapped as soon as a virtual camera
#    has been used, and the running app holds the managed outputs. A plain copy
#    then fails and used to leave a STALE native DLL in artifacts\app - which makes
#    the app crash with DllNotFoundException at the next start. So: stop the app,
#    retry the copy, and if the DLL is still mapped stop the Frame Server briefly.
#    A build that cannot replace this DLL fails loudly instead of shipping stale bits.
$builtSource = Join-Path $root "src\WebcamSwitcher.Source\x64\$Configuration\WebcamSwitcher.Source.dll"
$sourceDll = Join-Path $appOut "WebcamSwitcher.Source.dll"

$runningApp = Get-Process -Name "WebcamSwitcher" -ErrorAction SilentlyContinue
if ($runningApp) {
    Write-Host "Stopping the running app before deploying..."
    $runningApp | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

$copied = $false
for ($attempt = 1; $attempt -le 3 -and -not $copied; $attempt++) {
    try {
        Copy-Item $builtSource $appOut -Force -ErrorAction Stop
        $copied = $true
    } catch {
        if ($attempt -lt 3) { Start-Sleep -Seconds 2 }
    }
}

if (-not $copied) {
    $elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
    if ($elevated) {
        Write-Host "Media source DLL is locked; stopping the Frame Server to replace it..."
        $stopped = @()
        foreach ($svc in @("FrameServer", "FrameServerMonitor")) {
            $s = Get-Service -Name $svc -ErrorAction SilentlyContinue
            if ($s -and $s.Status -eq "Running") {
                Stop-Service -Name $svc -Force -ErrorAction SilentlyContinue
                $stopped += $svc
            }
        }
        Start-Sleep -Seconds 2
        try {
            Copy-Item $builtSource $appOut -Force -ErrorAction Stop
            $copied = $true
        } catch {
            Write-Warning "Still locked after stopping the Frame Server: $($_.Exception.Message)"
        }
        foreach ($svc in $stopped) { Start-Service -Name $svc -ErrorAction SilentlyContinue }
    }
}

if (-not $copied) {
    throw "Could not update $sourceDll (in use by the Frame Server). Close WebcamSwitcher and any app using the cameras, or run this script from an elevated shell, then build again."
}

# 5. (Re)register the media source with COM so the Frame Server can load it.
#    DllRegisterServer stores the DLL's ABSOLUTE path in HKLM, so this must run
#    again whenever the repo moves. Needs an elevated shell; failure here is
#    non-fatal - the app logs an actionable fix at startup.
#    regsvr32 is a GUI program: PowerShell does not wait for it and
#    $LASTEXITCODE would be stale, so wait explicitly and then verify the
#    registration in HKLM instead of trusting the exit code.
$sourceDll = Join-Path $appOut "WebcamSwitcher.Source.dll"
$reg = Start-Process -FilePath "$env:SystemRoot\System32\regsvr32.exe" -ArgumentList "/s", "`"$sourceDll`"" -Wait -PassThru -WindowStyle Hidden
$clsidKey = "HKLM:\Software\Classes\CLSID\{7B1C4D2E-8F3A-4B5C-9D6E-1F2A3B4C5D6E}\InprocServer32"
$registered = (Get-ItemProperty $clsidKey -ErrorAction SilentlyContinue).'(default)'
if ($reg.ExitCode -eq 0 -and $registered -eq $sourceDll) {
    Write-Host "Registered media source: $sourceDll"
} else {
    Write-Warning "Media source NOT registered for this build (regsvr32 exit $($reg.ExitCode); HKLM points to '$registered'). From an elevated shell: regsvr32 /s `"$sourceDll`""
    Write-Warning "The Frame Server runs as LOCAL SERVICE and cannot read DLLs under a user profile (e.g. Documents): install to C:\Program Files\WebcamSwitcher for real use."
}

Write-Host "Build complete. App: $appOut"
