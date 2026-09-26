# Rebuilds each public-set question's sheet (which re-grades the stored answers, no paid calls) and then
# prints what the paid lane found: verdicts per question, the models that were wrong, and what it all cost.
# Written 2026-09-20 for the overnight paid run.

$ErrorActionPreference = 'Stop'
$root = 'C:\Temp\ForClaude\LLMQuorum'
$outFile = Join-Path $root 'ops\paid-summary.txt'
$server = 'localhost'
$db = 'LLMQuorum'

function Query( [string]$sql )
{
    return sqlcmd -S $server -d $db -E -C -h -1 -W -s '|' -Q "SET NOCOUNT ON; $sql"
}

# Only questions the paid lane actually answered are worth re-grading.
$questions = Query "SELECT DISTINCT s.QuestionId FROM quorum.SweepCall sc JOIN quorum.Sweep s ON s.SweepId = sc.SweepId WHERE sc.Platform = 'openrouter-paid' AND sc.Status = 'Answered' ORDER BY s.QuestionId;"

foreach( $q in $questions )
{
    $id = $q.Trim()
    if( -not $id ) { continue }

    try
    {
        Invoke-WebRequest -Uri "http://localhost:5199/api/questions/$id/sheet" -OutFile ( Join-Path $env:TEMP "sheet-$id.xlsx" ) -TimeoutSec 900 -UseBasicParsing | Out-Null
        "rebuilt sheet for question $id"
    }
    catch
    {
        "sheet for question $id FAILED: $( $_.Exception.Message )"
    }
}

$report = @()
$report += "LLMQuorum paid lane, summary $( Get-Date -Format 'yyyy-MM-dd HH:mm' )"
$report += ''
$report += 'Per question (paid seats only): Correct / Hallucinated / Outdated / Refusal / other'
$report += ( Query @"
SELECT CONCAT( '  q', g.QuestionId, ' ', LEFT( q.Topic, 28 ), ' -> ',
       SUM( CASE WHEN g.Bucket = 'Correct' THEN 1 ELSE 0 END ), ' correct, ',
       SUM( CASE WHEN g.Bucket = 'Hallucinated' THEN 1 ELSE 0 END ), ' hallucinated, ',
       SUM( CASE WHEN g.Bucket = 'Outdated' THEN 1 ELSE 0 END ), ' outdated, ',
       SUM( CASE WHEN g.Bucket = 'Refusal' THEN 1 ELSE 0 END ), ' refusal, ',
       SUM( CASE WHEN g.Bucket NOT IN ( 'Correct', 'Hallucinated', 'Outdated', 'Refusal' ) THEN 1 ELSE 0 END ), ' other' )
  FROM quorum.SeatGrade g JOIN quorum.Question q ON q.QuestionId = g.QuestionId
 WHERE g.Provider = 'openrouter-paid'
 GROUP BY g.QuestionId, q.Topic ORDER BY g.QuestionId;
"@ )

$report += ''
$report += 'Memory seats versus web seats, every paid question:'
$report += ( Query @"
SELECT CONCAT( '  ', CASE WHEN g.SeatId LIKE '%web-search%' THEN 'web   ' ELSE 'memory' END, ' -> ',
       SUM( CASE WHEN g.Bucket = 'Correct' THEN 1 ELSE 0 END ), ' correct of ', COUNT( * ) )
  FROM quorum.SeatGrade g WHERE g.Provider = 'openrouter-paid'
 GROUP BY CASE WHEN g.SeatId LIKE '%web-search%' THEN 'web   ' ELSE 'memory' END;
"@ )

$report += ''
$report += 'Worst models (paid seats, wrong answers):'
$report += ( Query @"
SELECT TOP 10 CONCAT( '  ', g.ModelId, ' ', CASE WHEN g.SeatId LIKE '%web-search%' THEN '(web)' ELSE '(memory)' END,
       ' -> ', COUNT( * ), ' wrong' )
  FROM quorum.SeatGrade g WHERE g.Provider = 'openrouter-paid' AND g.Bucket IN ( 'Hallucinated', 'Outdated' )
 GROUP BY g.ModelId, CASE WHEN g.SeatId LIKE '%web-search%' THEN '(web)' ELSE '(memory)' END
 ORDER BY COUNT( * ) DESC;
"@ )

$report += ''
$report += 'Money:'
$report += ( Query @"
SELECT CONCAT( '  settled $', CAST( SUM( CASE WHEN State = 'settled' THEN ActualUsd ELSE 0 END ) AS DECIMAL(10,4) ),
       ' over ', SUM( CASE WHEN State = 'settled' THEN 1 ELSE 0 END ), ' calls; ',
       SUM( CASE WHEN State = 'voided' THEN 1 ELSE 0 END ), ' refused and unbilled; ',
       SUM( CASE WHEN State = 'unknown' THEN 1 ELSE 0 END ), ' unpriced' )
  FROM quorum.SpendLedger WHERE CapName = 'openrouter-paid';
"@ )

$report += ( Query "SELECT CONCAT( '  cap $', CAST( LimitUsd AS DECIMAL(10,2) ), ', halted: ', ISNULL( HaltedReason, 'no' ) ) FROM quorum.SpendCap WHERE CapName = 'openrouter-paid';" )

$report += ''
$report += 'Plan:'
$report += ( Query "SELECT CONCAT( '  ', Status, ' x', COUNT(*) ) FROM quorum.SweepPlan WHERE PlanName = 'public-v2' AND ProviderGroup = 'openrouter-paid' GROUP BY Status;" )

$report | Set-Content -Path $outFile -Encoding utf8
$report
