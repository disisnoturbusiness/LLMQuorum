# Freezes the current results into the files the published site reads. The site itself is one HTML page;
# everything it shows comes from these four JSON files, so what gets published is a snapshot with no
# database behind it, no connection string in it and nothing live to break.
#
# Run it after a sweep, then deploy wwwroot\report as a static site.

[CmdletBinding()]
param(
    # Minutes of grading silence required before a snapshot may be taken.
    [int] $QuietMinutes = 30,

    # Skip the settling gate, for when the lane is deliberately still running and a partial snapshot is
    # wanted. It says so on the way past, so the choice is on the record.
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$root = 'C:\Temp\ForClaude\LLMQuorum'
$out = Join-Path $root 'LLMQuorum.Web\wwwroot\report\data'
$api = 'http://localhost:5199/api/report'

# THE SETTLING GATE BELOW IS NOT OPTIONAL. On 2026-10-05 this script exported at 19:10 UTC while a
# re-grade was running from 19:55 to 21:06. The snapshot was a run in progress. It went to the live site,
# to a social card, to a LinkedIn post and to a Hacker News comment, and every figure in all four ended up
# off by a tenth of a point against the very database those posts invite the reader to go and check.
# Nothing looked wrong at export time, because nothing was wrong yet.
#
# So the rule is not "remember to check". The rule is that this script refuses to freeze a snapshot out of
# a database that was written to recently, or that still has open plan work. Override by hand with -Force.

$probe = @'
SET NOCOUNT ON;
SELECT CAST( DATEDIFF( MINUTE, MAX( GradedUtc ), SYSUTCDATETIME() ) AS VARCHAR(20) )
       + '|' + CAST( COUNT(*) AS VARCHAR(20) )
       + '|' + CAST( ( SELECT COUNT(*) FROM quorum.SweepPlan
                        WHERE Status NOT IN ( 'done', 'cut-off' ) ) AS VARCHAR(20) )
  FROM quorum.SeatGrade;
'@

$raw = ( & sqlcmd -S localhost -d LLMQuorum -E -C -h -1 -W -Q $probe ) |
       Where-Object { $_ -match [regex]::Escape( '|' ) } | Select-Object -First 1

if( -not $raw )
{
    throw 'export-site: could not read grading state. Refusing to publish a snapshot blind.'
}

$parts = $raw.Trim() -split [regex]::Escape( '|' )
$quietFor = [int] $parts[ 0 ]
$graded = [int] $parts[ 1 ]
$openWork = [int] $parts[ 2 ]

"grading last touched $quietFor min ago; $graded graded answers; $openWork plan items open"

if( $Force )
{
    "-Force: skipping the settling gate ($quietFor min quiet, $openWork open plan items)"
}
else
{
    if( $quietFor -lt $QuietMinutes )
    {
        throw ( "export-site: the last grade landed $quietFor minutes ago and the gate wants $QuietMinutes " +
                'minutes of quiet. Grading is probably still in flight, and a snapshot taken now will ' +
                'disagree with the database the moment it finishes. Wait, or pass -Force.' )
    }

    if( $openWork -gt 0 )
    {
        throw ( "export-site: $openWork plan items are still open, so more answers are coming. Wait for " +
                'the plan to finish, or pass -Force if a partial snapshot is what you actually want.' )
    }
}


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
