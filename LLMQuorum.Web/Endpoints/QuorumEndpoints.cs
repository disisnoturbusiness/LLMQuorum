using System.Text.Json;
using Hangfire;
using LLMQuorum.Core.Configuration;
using LLMQuorum.Core.Models;
using LLMQuorum.Core.Storage;
using LLMQuorum.Web.Jobs;
using Microsoft.Data.SqlClient;

namespace LLMQuorum.Web.Endpoints;

/// <summary>
/// HTTP surface for the portal. Kept as minimal-API endpoints rather than MVC
/// because every one of them is a thin read or an enqueue, and controllers would
/// add ceremony without adding behaviour.
/// </summary>
public static class QuorumEndpoints
{
    #region Public Methods

    /// <summary>
    /// Registers every portal endpoint. Run enqueues are fire-and-forget into
    /// Hangfire so the browser never holds a connection open for the many
    /// minutes a full sweep takes.
    /// </summary>
    /// <param name="app">Application being configured.</param>
    /// <returns>The same application, for chaining.</returns>
    public static WebApplication MapQuorumEndpoints( this WebApplication app )
    {
        app.MapGet( "/api/panel", ( QuorumConfig config ) => Results.Json( new
        {
            config.Panel,
            Ladder = config.GetLadder().Select( p => new
            {
                p.Key, p.DisplayName, p.ModelId, p.Lab, p.LadderPosition
            } ),
            Disabled = config.Providers.Where( p => !p.IsEnabled ).Select( p => p.Key )
        } ) );

        app.MapGet( "/api/question-sets", async ( QuorumConfig config ) =>
            Results.Json( await QueryAsync( config.ConnectionString, @"
SELECT s.QuestionSetId, s.Name,
       ( SELECT COUNT(*) FROM quorum.Question q
          WHERE q.QuestionSetId = s.QuestionSetId AND q.IsEnabled = 1 ) AS QuestionCount
  FROM quorum.QuestionSet s
 ORDER BY s.Name;" ) ) );

        app.MapGet( "/api/runs", async ( QuorumConfig config ) =>
            Results.Json( await QueryAsync( config.ConnectionString, @"
SELECT TOP 50 r.RunId, r.Label, s.Name AS SetName, r.StartedUtc, r.CompletedUtc,
       ( SELECT COUNT(*) FROM quorum.QuestionRun qr WHERE qr.RunId = r.RunId ) AS Questions
  FROM quorum.Run r
       INNER JOIN quorum.QuestionSet s ON s.QuestionSetId = r.QuestionSetId
 ORDER BY r.RunId DESC;" ) ) );

        // One row per question, never per verdict. A replayed run carries several
        // verdicts per question under different matcher versions, and joining
        // them all would show each question once per rule set - which reads as
        // duplicate questions rather than as a rule comparison.
        // Defaults to the newest verdict; ?matcher= pins a specific rule set.
        app.MapGet( "/api/runs/{runId:long}", async (
            long runId, string? matcher, QuorumConfig config ) =>
            Results.Json( await QueryAsync( config.ConnectionString, @"
SELECT q.Ordinal, q.Category, q.Prompt, q.ExpectedAnswer,
       v.Outcome, v.WinningAnswer, v.WinningVotes, v.PartitionSig, v.ClustersJson,
       v.MatcherVersion, qr.ProvidersAsked
  FROM quorum.QuestionRun qr
       INNER JOIN quorum.Question q ON q.QuestionId = qr.QuestionId
       OUTER APPLY (
            SELECT TOP 1 *
              FROM quorum.Verdict vv
             WHERE vv.QuestionRunId = qr.QuestionRunId
               AND ( @Matcher IS NULL OR vv.MatcherVersion = @Matcher )
             ORDER BY vv.ComputedUtc DESC, vv.VerdictId DESC ) AS v
 WHERE qr.RunId = @RunId
 ORDER BY q.Ordinal;", ( "@RunId", runId ), ( "@Matcher", (object?)matcher ?? DBNull.Value ) ) ) );

        // Which rule sets have been applied to a run, so two can be compared.
        app.MapGet( "/api/runs/{runId:long}/matchers", async ( long runId, QuorumConfig config ) =>
            Results.Json( await QueryAsync( config.ConnectionString, @"
SELECT v.MatcherVersion, COUNT(*) AS Questions, MIN(v.ComputedUtc) AS ComputedUtc
  FROM quorum.Verdict v
       INNER JOIN quorum.QuestionRun qr ON qr.QuestionRunId = v.QuestionRunId
 WHERE qr.RunId = @RunId
 GROUP BY v.MatcherVersion
 ORDER BY MIN(v.ComputedUtc);", ( "@RunId", runId ) ) ) );

        app.MapGet( "/api/profile", async ( QuorumConfig config ) =>
            Results.Json( await QueryAsync( config.ConnectionString, @"
SELECT ProviderKey, Lab, Category, CallsAnswered, Successes, RateLimited,
       AvgLatencyMs, JoinedWinningCluster
  FROM quorum.vProviderProfile
 ORDER BY ProviderKey, Category;" ) ) );

        // Re-judges a completed run under a new rule set. Spends no API quota:
        // it reads the answers already stored and writes a verdict under a new
        // matcher version, leaving the original decision intact for comparison.
        app.MapPost( "/api/runs/{runId:long}/replay", async (
            long runId, ReplayRequest request, QuorumConfig config ) =>
        {
            var matcher = new Core.Matching.RuleBasedMatcher(
                request.MatcherVersion,
                request.ScalarTolerance ?? config.Panel.ScalarTolerance );

            var replayer = new Core.Matching.VerdictReplayer( config.ConnectionString );
            var summary = await replayer.ReplayRunAsync( runId, matcher );

            return Results.Ok( new
            {
                runId,
                request.MatcherVersion,
                summary.QuestionsReplayed,
                summary.OutcomesChanged
            } );
        } );

        // Model sweep: one question across every enabled seat in profiles.json.
        // RunAtUtc schedules it, which is how a sweep is lined up to start right after the
        // 00:00 UTC reset of the daily free allowances.
        app.MapPost( "/api/sweeps", ( SweepRequest request ) =>
        {
            var jobId = request.RunAtUtc is DateTime at && at > DateTime.UtcNow
                ? BackgroundJob.Schedule<ModelSweepJob>(
                    job => job.ExecuteAsync( request.QuestionId, request.Label, request.Providers, request.Seats, CancellationToken.None ),
                    new DateTimeOffset( DateTime.SpecifyKind( at, DateTimeKind.Utc ) ) )
                : BackgroundJob.Enqueue<ModelSweepJob>(
                    job => job.ExecuteAsync( request.QuestionId, request.Label, request.Providers, request.Seats, CancellationToken.None ) );

            return Results.Accepted( "/jobs", new { jobId, request.QuestionId, request.RunAtUtc } );
        } );

        app.MapGet( "/api/sweeps/{sweepId:long}/report", async ( long sweepId, QuorumConfig config ) =>
        {
            var directory = config.ResolvePath( config.SweepReportDirectory );
            Directory.CreateDirectory( directory );
            var markdown = await new Core.Sweep.SweepReport( config.ConnectionString )
                .WriteAsync( sweepId, Path.Combine( directory, $"sweep-{sweepId}.md" ), CancellationToken.None );
            return Results.Text( markdown, "text/markdown" );
        } );

        // Sweep plan status, a manual nudge, and removal of a superseded scheduled job.
        app.MapGet( "/api/plans/{planName}", async ( string planName, QuorumConfig config ) =>
            Results.Json( await new Core.Sweep.SweepPlanner( config.ConnectionString ).LoadAsync( planName, CancellationToken.None ) ) );

        app.MapPost( "/api/plans/{planName}/run", ( string planName ) =>
            Results.Accepted( "/jobs", new { jobId = BackgroundJob.Enqueue<PlanRunnerJob>( job => job.RunAsync( planName, CancellationToken.None ) ) } ) );

        app.MapDelete( "/api/jobs/{jobId}", ( string jobId ) => Results.Json( new { jobId, deleted = BackgroundJob.Delete( jobId ) } ) );

        // ---- The report: what every model answered, and how it was graded. -------------------------------
        // One row per seat (a model in one mode), counted by verdict. A seat is a model plus how it was asked,
        // so the same model appears twice when it was asked both from memory and with web search, which is the
        // comparison the whole harness exists to make.
        app.MapGet( "/api/report/models", async ( QuorumConfig config ) =>
            Results.Json( await QueryAsync( config.ConnectionString, @"
SELECT g.SeatId, g.Provider, g.ModelId, g.BaseModel,
       CASE WHEN g.SeatId LIKE '%web-search%' THEN 'web' ELSE 'memory' END AS Mode,
       COUNT(*) AS Asked,
       SUM( CASE WHEN g.Bucket = 'Correct' THEN 1 ELSE 0 END ) AS Correct,
       SUM( CASE WHEN g.Bucket = 'Hallucinated' THEN 1 ELSE 0 END ) AS Hallucinated,
       SUM( CASE WHEN g.Bucket = 'Outdated' THEN 1 ELSE 0 END ) AS Outdated,
       SUM( CASE WHEN g.Bucket = 'Refusal' THEN 1 ELSE 0 END ) AS Refusal,
       SUM( CASE WHEN g.Bucket = 'Truncated' THEN 1 ELSE 0 END ) AS Truncated,
       SUM( CASE WHEN g.Bucket = 'Error' THEN 1 ELSE 0 END ) AS [Error],
       SUM( CASE WHEN g.Bucket IN ( 'Wrong', 'Unclassified' ) THEN 1 ELSE 0 END ) AS Other,
       CAST( 100.0 * SUM( CASE WHEN g.Bucket = 'Correct' THEN 1 ELSE 0 END ) / NULLIF( COUNT(*), 0 ) AS DECIMAL(5,1) ) AS Accuracy,
       ( SELECT CAST( ISNULL( SUM( l.ActualUsd ), 0 ) AS DECIMAL(10,4) )
           FROM quorum.SpendLedger l WHERE l.SeatId = g.SeatId AND l.State = 'settled' ) AS CostUsd
  FROM quorum.SeatGrade g
 GROUP BY g.SeatId, g.Provider, g.ModelId, g.BaseModel
 ORDER BY Accuracy DESC, Asked DESC;" ) ) );

        // Every question put to one seat, with what it said and how that was graded. The optional bucket
        // filter is what makes a count on the summary row clickable: "show me the eleven it hallucinated".
        app.MapGet( "/api/report/models/{seatId}/answers", async ( string seatId, string? bucket, QuorumConfig config ) =>
            Results.Json( await QueryAsync( config.ConnectionString, @"
SELECT g.QuestionId, q.Ordinal, q.Topic, s.Name AS SetName, q.Prompt, q.ExpectedAnswer, q.ExpectedSource,
       g.Bucket, g.Grade, g.GradeText, g.AnswerText, g.HowGraded, g.LatencyMs, g.SweepCallId
  FROM quorum.SeatGrade g
       INNER JOIN quorum.Question q ON q.QuestionId = g.QuestionId
       INNER JOIN quorum.QuestionSet s ON s.QuestionSetId = q.QuestionSetId
 WHERE g.SeatId = @SeatId AND ( @Bucket = '' OR g.Bucket = @Bucket )
 ORDER BY s.Name, q.Ordinal;",
                ( "@SeatId", seatId ), ( "@Bucket", bucket ?? string.Empty ) ) ) );

        // One row per question: how the whole field did on it, hardest first.
        app.MapGet( "/api/report/questions", async ( QuorumConfig config ) =>
            Results.Json( await QueryAsync( config.ConnectionString, @"
SELECT q.QuestionId, q.Ordinal, q.Topic, s.Name AS SetName, q.Prompt, q.ExpectedAnswer, q.ExpectedSource,
       COUNT(*) AS Asked,
       SUM( CASE WHEN g.Bucket = 'Correct' THEN 1 ELSE 0 END ) AS Correct,
       SUM( CASE WHEN g.Bucket = 'Hallucinated' THEN 1 ELSE 0 END ) AS Hallucinated,
       SUM( CASE WHEN g.Bucket = 'Outdated' THEN 1 ELSE 0 END ) AS Outdated,
       SUM( CASE WHEN g.Bucket = 'Refusal' THEN 1 ELSE 0 END ) AS Refusal,
       SUM( CASE WHEN g.Bucket IN ( 'Error', 'Truncated', 'Wrong', 'Unclassified' ) THEN 1 ELSE 0 END ) AS Other,
       CAST( 100.0 * SUM( CASE WHEN g.Bucket = 'Correct' THEN 1 ELSE 0 END ) / NULLIF( COUNT(*), 0 ) AS DECIMAL(5,1) ) AS Accuracy
  FROM quorum.SeatGrade g
       INNER JOIN quorum.Question q ON q.QuestionId = g.QuestionId
       INNER JOIN quorum.QuestionSet s ON s.QuestionSetId = q.QuestionSetId
 GROUP BY q.QuestionId, q.Ordinal, q.Topic, s.Name, q.Prompt, q.ExpectedAnswer, q.ExpectedSource
 ORDER BY Accuracy, Asked DESC;" ) ) );

        // Every seat's answer to one question, so a bad question and a bad model can be told apart.
        app.MapGet( "/api/report/questions/{questionId:int}/answers", async ( int questionId, string? bucket, QuorumConfig config ) =>
            Results.Json( await QueryAsync( config.ConnectionString, @"
SELECT g.SeatId, g.Provider, g.ModelId, g.BaseModel,
       CASE WHEN g.SeatId LIKE '%web-search%' THEN 'web' ELSE 'memory' END AS Mode,
       g.Bucket, g.Grade, g.GradeText, g.AnswerText, g.HowGraded, g.LatencyMs
  FROM quorum.SeatGrade g
 WHERE g.QuestionId = @QuestionId AND ( @Bucket = '' OR g.Bucket = @Bucket )
 ORDER BY CASE g.Bucket WHEN 'Correct' THEN 0 WHEN 'Outdated' THEN 1 WHEN 'Hallucinated' THEN 2 ELSE 3 END, g.ModelId;",
                ( "@QuestionId", questionId ), ( "@Bucket", bucket ?? string.Empty ) ) ) );

        // Every graded answer in one payload. It is what the published site ships instead of a database:
        // 76 seats x 76 questions, the answer text included, which is small enough to send as one file and
        // means the site works as a snapshot that cannot leak a key or a connection string.
        app.MapGet( "/api/report/answers", async ( QuorumConfig config ) =>
            Results.Json( await QueryAsync( config.ConnectionString, @"
SELECT g.SeatId, g.QuestionId, g.Bucket, g.Grade, g.LatencyMs,
       LEFT( ISNULL( g.AnswerText, '' ), 600 ) AS AnswerText,
       LEFT( ISNULL( g.GradeText, '' ), 300 ) AS GradeText
  FROM quorum.SeatGrade g
 ORDER BY g.SeatId, g.QuestionId;" ) ) );

        // The headline the whole exercise was built to produce, plus what it cost to produce it.
        app.MapGet( "/api/report/summary", async ( QuorumConfig config ) =>
            Results.Json( new
            {
                Modes = await QueryAsync( config.ConnectionString, @"
SELECT CASE WHEN g.SeatId LIKE '%web-search%' THEN 'web' ELSE 'memory' END AS Mode,
       COUNT( DISTINCT g.SeatId ) AS Seats, COUNT(*) AS Asked,
       SUM( CASE WHEN g.Bucket = 'Correct' THEN 1 ELSE 0 END ) AS Correct,
       SUM( CASE WHEN g.Bucket = 'Hallucinated' THEN 1 ELSE 0 END ) AS Hallucinated,
       SUM( CASE WHEN g.Bucket = 'Outdated' THEN 1 ELSE 0 END ) AS Outdated,
       SUM( CASE WHEN g.Bucket IN ( 'Error', 'Truncated' ) THEN 1 ELSE 0 END ) AS Failed,
       SUM( CASE WHEN g.Bucket NOT IN ( 'Error', 'Truncated' ) THEN 1 ELSE 0 END ) AS Answered,
       CAST( 100.0 * SUM( CASE WHEN g.Bucket = 'Correct' THEN 1 ELSE 0 END ) / NULLIF( COUNT(*), 0 ) AS DECIMAL(5,1) ) AS Accuracy,
       CAST( 100.0 * SUM( CASE WHEN g.Bucket = 'Correct' THEN 1 ELSE 0 END )
             / NULLIF( SUM( CASE WHEN g.Bucket NOT IN ( 'Error', 'Truncated' ) THEN 1 ELSE 0 END ), 0 ) AS DECIMAL(5,1) ) AS AccuracyOfAnswers
  FROM quorum.SeatGrade g GROUP BY CASE WHEN g.SeatId LIKE '%web-search%' THEN 'web' ELSE 'memory' END;" ),
                // The only comparison that controls for the model. Only some models can search, so setting all
                // the memory seats against all the search seats compares two different populations; these are
                // the models that were asked both ways, which is the comparison the harness exists to make.
                Paired = await QueryAsync( config.ConnectionString, @"
WITH s AS ( SELECT DISTINCT ModelId, CASE WHEN SeatId LIKE '%web-search%' THEN 'web' ELSE 'memory' END AS m
              FROM quorum.SeatGrade ),
     both AS ( SELECT ModelId FROM s GROUP BY ModelId HAVING COUNT( DISTINCT m ) = 2 )
SELECT CASE WHEN g.SeatId LIKE '%web-search%' THEN 'web' ELSE 'memory' END AS Mode,
       COUNT( DISTINCT g.ModelId ) AS Models, COUNT(*) AS Asked,
       SUM( CASE WHEN g.Bucket = 'Correct' THEN 1 ELSE 0 END ) AS Correct,
       CAST( 100.0 * SUM( CASE WHEN g.Bucket = 'Correct' THEN 1 ELSE 0 END ) / NULLIF( COUNT(*), 0 ) AS DECIMAL(5,1) ) AS Accuracy
  FROM quorum.SeatGrade g INNER JOIN both b ON b.ModelId = g.ModelId
 GROUP BY CASE WHEN g.SeatId LIKE '%web-search%' THEN 'web' ELSE 'memory' END;" ),
                PairedByModel = await QueryAsync( config.ConnectionString, @"
WITH s AS ( SELECT DISTINCT ModelId, CASE WHEN SeatId LIKE '%web-search%' THEN 'web' ELSE 'memory' END AS m
              FROM quorum.SeatGrade ),
     both AS ( SELECT ModelId FROM s GROUP BY ModelId HAVING COUNT( DISTINCT m ) = 2 )
SELECT g.ModelId,
       CAST( 100.0 * SUM( CASE WHEN g.Bucket = 'Correct' AND g.SeatId NOT LIKE '%web-search%' THEN 1 ELSE 0 END )
             / NULLIF( SUM( CASE WHEN g.SeatId NOT LIKE '%web-search%' THEN 1 ELSE 0 END ), 0 ) AS DECIMAL(5,1) ) AS Memory,
       CAST( 100.0 * SUM( CASE WHEN g.Bucket = 'Correct' AND g.SeatId LIKE '%web-search%' THEN 1 ELSE 0 END )
             / NULLIF( SUM( CASE WHEN g.SeatId LIKE '%web-search%' THEN 1 ELSE 0 END ), 0 ) AS DECIMAL(5,1) ) AS Web
  FROM quorum.SeatGrade g INNER JOIN both b ON b.ModelId = g.ModelId
 GROUP BY g.ModelId ORDER BY 2;" ),
                Sets = await QueryAsync( config.ConnectionString, @"
SELECT s.Name AS SetName, COUNT( DISTINCT q.QuestionId ) AS Questions,
       CAST( 100.0 * SUM( CASE WHEN g.Bucket = 'Correct' THEN 1 ELSE 0 END ) / NULLIF( COUNT( g.SeatGradeId ), 0 ) AS DECIMAL(5,1) ) AS Accuracy
  FROM quorum.QuestionSet s
       INNER JOIN quorum.Question q ON q.QuestionSetId = s.QuestionSetId
       LEFT JOIN quorum.SeatGrade g ON g.QuestionId = q.QuestionId
 GROUP BY s.Name HAVING COUNT( g.SeatGradeId ) > 0 ORDER BY s.Name;" ),
                Money = await QueryAsync( config.ConnectionString, @"
SELECT CAST( SUM( CASE WHEN State = 'settled' THEN ActualUsd ELSE 0 END ) AS DECIMAL(10,4) ) AS RecordedUsd,
       SUM( CASE WHEN State = 'settled' THEN 1 ELSE 0 END ) AS PaidCalls
  FROM quorum.SpendLedger;" )
            } ) );

        // One sheet per question across all of its sweeps: Correct, Truncated, Refusal, Outdated, Hallucinated, Error.
        app.MapGet( "/api/questions/{questionId:int}/sheet", async ( int questionId, QuorumConfig config ) =>
        {
            var directory = config.ResolvePath( config.SweepReportDirectory );
            Directory.CreateDirectory( directory );
            var path = Path.Combine( directory, $"question-{questionId}.xlsx" );
            var sheet = await Core.Sweep.QuestionSheetFactory.Create( config ).WriteAsync( questionId, path, CancellationToken.None );
            return Results.File( sheet.Path, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", Path.GetFileName( sheet.Path ) );
        } );

        app.MapPost( "/api/runs", ( RunRequest request ) =>
        {
            var jobId = BackgroundJob.Enqueue<SweepJob>(
                job => job.ExecuteAsync( request.QuestionSetId, request.Label, CancellationToken.None ) );

            return Results.Accepted( $"/jobs", new { jobId, request.QuestionSetId } );
        } );

        app.MapPost( "/api/question-sets/import", async (
            ImportRequest request, QuorumRepository repository ) =>
        {
            var setId = await repository.EnsureQuestionSetAsync( request.SetName );
            var ordinal = 0;

            foreach( var item in request.Questions )
            {
                ordinal++;
                await repository.UpsertQuestionAsync( setId, new QuestionItem
                {
                    Ordinal = ordinal,
                    Category = item.Category,
                    Prompt = item.Prompt,
                    Shape = Enum.Parse<AnswerShape>( item.Shape, ignoreCase: true ),
                    ExpectedAnswer = item.ExpectedAnswer
                }, item.ExpectedSource );
            }

            return Results.Ok( new { questionSetId = setId, imported = ordinal } );
        } );

        return app;
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// Runs a read query and projects every row to a dictionary. Generic on
    /// purpose: these endpoints are read-only projections for a single-user
    /// portal, and typed DTOs for each would be maintenance with no payoff.
    /// </summary>
    /// <param name="connectionString">Target database.</param>
    /// <param name="sql">Query text.</param>
    /// <param name="parameters">Optional parameter name and value pairs.</param>
    /// <returns>One dictionary per row, column name to value.</returns>
    private static async Task<List<Dictionary<string, object?>>> QueryAsync(
        string connectionString,
        string sql,
        params (string Name, object Value)[] parameters )
    {
        var rows = new List<Dictionary<string, object?>>();

        await using var connection = new SqlConnection( connectionString );
        await connection.OpenAsync();

        await using var command = new SqlCommand( sql, connection );

        foreach( var (name, value) in parameters )
        {
            command.Parameters.AddWithValue( name, value );
        }

        await using var reader = await command.ExecuteReaderAsync();

        while( await reader.ReadAsync() )
        {
            var row = new Dictionary<string, object?>( reader.FieldCount );

            for( var i = 0; i < reader.FieldCount; i++ )
            {
                row[reader.GetName( i )] = reader.IsDBNull( i ) ? null : reader.GetValue( i );
            }

            rows.Add( row );
        }

        return rows;
    }

    #endregion Private Methods
}

/// <summary>Request to re-judge a stored run under a different rule set.</summary>
/// <param name="MatcherVersion">Identifier for the new rules; stored alongside the new verdict so rule
/// changes can be diffed against the same evidence.</param>
/// <param name="ScalarTolerance">Optional override for numeric tolerance; falls back to the panel default.</param>
public sealed record ReplayRequest( string MatcherVersion, double? ScalarTolerance );

/// <summary>Request to run one question across every enabled seat.</summary>
/// <param name="QuestionId">Stored question to ask.</param>
/// <param name="Label">Optional label.</param>
/// <param name="Providers">Optional provider keys to restrict to.</param>
/// <param name="Seats">Optional seat id fragments to restrict to, e.g. "zai|glm-4.5-flash".</param>
/// <param name="RunAtUtc">Optional UTC start time; used to start right after the daily allowance reset.</param>
public sealed record SweepRequest( int QuestionId, string? Label, string[]? Providers, string[]? Seats, DateTime? RunAtUtc );

/// <summary>Request to enqueue a sweep.</summary>
/// <param name="QuestionSetId">Set to execute.</param>
/// <param name="Label">Optional human label recorded against the run.</param>
public sealed record RunRequest( int QuestionSetId, string? Label );

/// <summary>Request to import or replace the questions of a set.</summary>
/// <param name="SetName">Set name; created if new.</param>
/// <param name="Questions">Questions in the order they should run.</param>
public sealed record ImportRequest( string SetName, List<ImportQuestion> Questions );

/// <summary>One imported question and its optional answer key.</summary>
/// <param name="Category">Taxonomy bucket driving the characterization matrix.</param>
/// <param name="Prompt">Exact text sent to every provider.</param>
/// <param name="Shape">Declared comparison shape: text, scalar, enum, bool or set.</param>
/// <param name="ExpectedAnswer">Known-correct answer, used only by the offline scorer.</param>
/// <param name="ExpectedSource">Primary-source URL backing the answer key.</param>
public sealed record ImportQuestion(
    string Category,
    string Prompt,
    string Shape,
    string? ExpectedAnswer,
    string? ExpectedSource );
