param(
    [string]$Browser = "",
    [int]$Port = 9333,
    [int]$WaitSeconds = 25,
    # Keep a profile between runs to compare deviceIds (they are salted per profile+origin).
    [string]$ProfileDir = ""
)
# Runs enumerate.html in a headless Chromium (Edge or Chrome) with a throwaway
# profile, auto-granting camera permission (--use-fake-ui-for-media-stream: the
# permission prompt is accepted, the REAL devices are used), then reads the
# result over the DevTools protocol and prints it as JSON.
$ErrorActionPreference = "Stop"
if (-not $Browser) {
    $Browser = @(
        "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Google\Chrome\Application\chrome.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Browser) { throw "No Chromium browser found" }
$page = "file:///" + ((Join-Path $PSScriptRoot "enumerate.html") -replace '\\', '/') + "#auto"
$keep = [bool]$ProfileDir
$profile = if ($keep) { $ProfileDir } else { Join-Path ([IO.Path]::GetTempPath()) ("wscheck-" + [guid]::NewGuid().ToString("N")) }
$proc = Start-Process $Browser -PassThru -ArgumentList @(
    "--headless=new", "--remote-debugging-port=$Port", "--user-data-dir=`"$profile`"",
    "--use-fake-ui-for-media-stream", "--no-first-run", "--disable-extensions", $page)
try {
    Start-Sleep -Seconds $WaitSeconds
    $targets = Invoke-RestMethod "http://127.0.0.1:$Port/json"
    $t = $targets | Where-Object { $_.type -eq "page" -and $_.url -like "*enumerate.html*" } | Select-Object -First 1
    $ws = [System.Net.WebSockets.ClientWebSocket]::new()
    $ws.ConnectAsync([Uri]$t.webSocketDebuggerUrl, [Threading.CancellationToken]::None).Wait()
    $msg = '{"id":1,"method":"Runtime.evaluate","params":{"expression":"JSON.stringify({summary: document.getElementById(\"summary\").textContent, result: window.__result || null, ua: navigator.userAgent})","returnByValue":true}}'
    $bytes = [Text.Encoding]::UTF8.GetBytes($msg)
    $ws.SendAsync([ArraySegment[byte]]::new($bytes), "Text", $true, [Threading.CancellationToken]::None).Wait()
    $buf = New-Object byte[] 1048576
    $sb = [Text.StringBuilder]::new()
    do {
        $r = $ws.ReceiveAsync([ArraySegment[byte]]::new($buf), [Threading.CancellationToken]::None).Result
        [void]$sb.Append([Text.Encoding]::UTF8.GetString($buf, 0, $r.Count))
    } while (-not $r.EndOfMessage)
    $reply = $sb.ToString() | ConvertFrom-Json
    $reply.result.result.value
} finally {
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    Get-Process msedge, chrome -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -like "*$profile*" } | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    if (-not $keep) { Remove-Item $profile -Recurse -Force -ErrorAction SilentlyContinue }
}
