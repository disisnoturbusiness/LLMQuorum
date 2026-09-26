# Freezes the current results into the files the published site reads. The site itself is one HTML page;
# everything it shows comes from these four JSON files, so what gets published is a snapshot with no
# database behind it, no connection string in it and nothing live to break.
#
# Run it after a sweep, then deploy wwwroot\report as a static site.

$ErrorActionPreference = 'Stop'
$root = 'C:\Temp\ForClaude\LLMQuorum'
$out = Join-Path $root 'LLMQuorum.Web\wwwroot\report\data'
$api = 'http://localhost:5199/api/report'

New-Item -ItemType Directory -Force -Path $out | Out-Null

foreach( $name in 'summary', 'models', 'questions', 'answers' )
{
    $path = Join-Path $out "$name.json"
    Invoke-WebRequest -Uri "$api/$name" -OutFile $path -TimeoutSec 600 -UseBasicParsing
    $size = [Math]::Round( ( Get-Item $path ).Length / 1KB, 1 )
    "$name.json  ${size} KB"
}

# A snapshot should say when it was taken, and the page shows it.
$stamp = @{ takenUtc = ( Get-Date ).ToUniversalTime().ToString( 'yyyy-MM-dd HH:mm' ) + ' UTC' } | ConvertTo-Json
Set-Content -Path ( Join-Path $out 'taken.json' ) -Value $stamp -Encoding utf8

"exported to $out"
