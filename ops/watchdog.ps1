# LLMQuorum watchdog. Run by the "LLMQuorum Watchdog" scheduled task every 10 minutes while the owner is
# logged on. If the portal on :5199 does not answer, it starts it. The multi-day sweep plan only advances
# while the app is up, and the app has already been found stopped between sessions once.
#
# Pause: create ops\watchdog.pause (e.g. during a rebuild) and the watchdog does nothing until it is deleted.

$ErrorActionPreference = 'Stop'
$root = 'C:\Temp\ForClaude\LLMQuorum'
$log = Join-Path $root 'ops\watchdog.log'
$pause = Join-Path $root 'ops\watchdog.pause'

function Write-Log( [string]$message )
{
    $line = '{0:yyyy-MM-dd HH:mm:ss} {1}' -f ( Get-Date ), $message
    Add-Content -Path $log -Value $line -Encoding UTF8

    # Keep the log small: last 500 lines.
    $lines = Get-Content -Path $log -Encoding UTF8
    if( $lines.Count -gt 600 ) { $lines | Select-Object -Last 500 | Set-Content -Path $log -Encoding UTF8 }
}

if( Test-Path $pause )
{
    Write-Log 'paused (watchdog.pause present)'
    return
}

try
{
    Invoke-RestMethod -Uri 'http://localhost:5199/api/panel' -TimeoutSec 10 | Out-Null
    return
}
catch
{
    Write-Log "portal not answering: $( $_.Exception.Message )"
}

$env:ASPNETCORE_URLS = 'http://localhost:5199'
Start-Process -FilePath 'C:\Program Files\dotnet\dotnet.exe' `
              -ArgumentList 'run --no-launch-profile --no-build' `
              -WorkingDirectory ( Join-Path $root 'LLMQuorum.Web' ) `
              -RedirectStandardOutput ( Join-Path $root 'app.log' ) `
              -RedirectStandardError ( Join-Path $root 'app.err' ) `
              -WindowStyle Hidden

for( $i = 0; $i -lt 45; $i++ )
{
    Start-Sleep -Seconds 2

    try
    {
        Invoke-RestMethod -Uri 'http://localhost:5199/api/panel' -TimeoutSec 3 | Out-Null
        Write-Log "started; portal answering after $( ( $i + 1 ) * 2 )s"
        return
    }
    catch { }
}

Write-Log 'started but portal still not answering after 90s; see app.err'
