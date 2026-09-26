using System.Text;
using LLMQuorum.Core.Matching;
using LLMQuorum.Core.Models;
using Microsoft.Data.SqlClient;

namespace LLMQuorum.Core.Sweep;

/// <summary>
/// Grades a completed sweep against the question's answer key and writes a readable report.
/// Grading happens here, at report time, from stored answers, so a grading fix never needs a
/// re-run. Only Answered, non-refusal responses are graded; truncated, exhausted and failed
/// calls are reported by status and never counted as wrong.
/// </summary>
public sealed class SweepReport
{
    #region Data Members

    private readonly string _connectionString;
    private readonly SweepGrader _grader = new();

    #endregion Data Members

    #region Constructor

    /// <summary>Builds a report generator.</summary>
    /// <param name="connectionString">LLMQuorum database.</param>
    public SweepReport( string connectionString )
    {
        _connectionString = connectionString;
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>Writes the report for a sweep.</summary>
    /// <param name="sweepId">Sweep to report.</param>
    /// <param name="outputPath">Markdown file to write.</param>
    /// <param name="cancellationToken">Cancels the queries.</param>
    /// <returns>The markdown text.</returns>
    public async Task<string> WriteAsync( long sweepId, string outputPath, CancellationToken cancellationToken )
    {
        var (prompt, expected, shape) = await LoadQuestionAsync( sweepId, cancellationToken );
        var rows = await LoadFinalAttemptsAsync( sweepId, cancellationToken );

        foreach( var row in rows )
        {
            row.Grade = Grade( row, expected, shape );
        }

        var markdown = Render( sweepId, prompt, expected, shape, rows );
        await File.WriteAllTextAsync( outputPath, markdown, cancellationToken );
        return markdown;
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// Rule tier only: this per-sweep report never calls judges. Answers the rules hand to judges show
    /// as NEEDS-JUDGE; their judged grade is on the per-question sheet.
    /// </summary>
    private string Grade( SweepRow row, string? expected, AnswerShape shape ) =>
        _grader.Grade( row.Status, row.IsRefusal, row.Answer, new QuestionContext( expected, shape, string.Empty, null ) ).Grade ?? "NEEDS-JUDGE";

    /// <summary>Builds the markdown.</summary>
    private string Render( long sweepId, string prompt, string? expected, AnswerShape shape, List<SweepRow> rows )
    {
        var sb = new StringBuilder();
        sb.AppendLine( $"# Sweep {sweepId}" ).AppendLine();
        sb.AppendLine( $"Question: {prompt}" ).AppendLine();
        sb.AppendLine( $"Answer key: {expected ?? "(none)"}" ).AppendLine();

        sb.AppendLine( "## Summary" ).AppendLine();
        sb.AppendLine( "| Group | Seats | Answered | Correct | Wrong | Refusal | Truncated/exhausted | Failed/skipped |" );
        sb.AppendLine( "|---|---|---|---|---|---|---|---|" );

        foreach( var group in rows.GroupBy( r => r.Group ).OrderBy( g => g.Key ) )
        {
            var g = group.ToList();
            sb.AppendLine( $"| {group.Key} | {g.Count} | {g.Count( r => r.Status == "Answered" )} | " +
                           $"{g.Count( r => r.Grade.StartsWith( "CORRECT" ) )} | {g.Count( r => r.Grade == "WRONG" )} | " +
                           $"{g.Count( r => r.Grade == "REFUSAL" )} | {g.Count( r => r.Status is "Truncated" or "ThinkingExhausted" )} | " +
                           $"{g.Count( r => r.Status is not ( "Answered" or "Truncated" or "ThinkingExhausted" ) )} |" );
        }

        var correctBases = rows.Where( r => r.Grade.StartsWith( "CORRECT" ) ).Select( r => r.BaseModel ).Distinct().ToList();
        sb.AppendLine().AppendLine( $"Distinct underlying models correct: {correctBases.Count} ({string.Join( ", ", correctBases )})" ).AppendLine();

        RenderClusters( sb, rows, shape );
        RenderDetail( sb, rows );
        return sb.ToString();
    }

    /// <summary>Groups graded answers by what they said, flagging duplicate weights.</summary>
    private void RenderClusters( StringBuilder sb, List<SweepRow> rows, AnswerShape shape )
    {
        sb.AppendLine( "## What they said (answered, non-refusal)" ).AppendLine();

        var clusters = rows
            .Where( r => r.Status == "Answered" && !r.IsRefusal && r.Answer is not null )
            .GroupBy( r => _grader.ClusterKey( r.Answer!, shape ) )
            .OrderByDescending( g => g.Count() );

        foreach( var cluster in clusters )
        {
            var seats = cluster.ToList();
            var distinctWeights = seats.Select( s => s.BaseModel ).Distinct().Count();
            sb.AppendLine( $"**{seats.Count} seats / {distinctWeights} distinct models, {seats[0].Grade}:** " +
                           seats[0].Answer!.Replace( "\n", " / " ) );

            foreach( var seat in seats )
            {
                sb.AppendLine( $"- {seat.Provider}: {seat.ModelId} [{seat.Group}, reasoning {seat.ReasoningModeUsed}] base={seat.BaseModel}" );
            }

            sb.AppendLine();
        }
    }

    /// <summary>One line per seat with the limit decision and outcome.</summary>
    private static void RenderDetail( StringBuilder sb, List<SweepRow> rows )
    {
        sb.AppendLine( "## Every seat" ).AppendLine();
        sb.AppendLine( "| Provider | Model | Group | Reasoning sent | Grade | Cap sent | Finish | Out tok | Reason tok | ms | Attempts | Answer or reason |" );
        sb.AppendLine( "|---|---|---|---|---|---|---|---|---|---|---|---|" );

        foreach( var r in rows.OrderBy( r => r.Provider ).ThenBy( r => r.ModelId ).ThenBy( r => r.Group ) )
        {
            var text = r.Status == "Answered" ? r.Answer : r.Decision ?? r.ErrorText;
            text = ( text ?? string.Empty ).Replace( "\r", "" ).Replace( "\n", " / " ).Replace( "|", "\\|" );
            text = text.Length > 180 ? text[..180] + "..." : text;

            sb.AppendLine( $"| {r.Provider} | {r.ModelId} | {r.Group} | {r.ReasoningModeUsed}{( r.ReasoningEvidence ? " (reasoned)" : "" )} | " +
                           $"{r.Grade} | {r.MaxTokensSent} | {r.FinishReason} | {r.CompletionTokens} | {r.ReasoningTokens} | " +
                           $"{r.LatencyMs} | {r.Attempts} | {text} |" );
        }
    }

    private async Task<(string Prompt, string? Expected, AnswerShape Shape)> LoadQuestionAsync( long sweepId, CancellationToken cancellationToken )
    {
        await using var connection = new SqlConnection( _connectionString );
        await connection.OpenAsync( cancellationToken );
        await using var command = new SqlCommand( @"
SELECT q.Prompt, q.ExpectedAnswer, q.AnswerShape FROM quorum.Sweep s
  JOIN quorum.Question q ON q.QuestionId = s.QuestionId WHERE s.SweepId = @Id;", connection );
        command.Parameters.AddWithValue( "@Id", sweepId );
        await using var reader = await command.ExecuteReaderAsync( cancellationToken );

        if( !await reader.ReadAsync( cancellationToken ) )
        {
            throw new InvalidOperationException( $"Sweep {sweepId} not found." );
        }

        return (reader.GetString( 0 ), reader.IsDBNull( 1 ) ? null : reader.GetString( 1 ),
                Enum.Parse<AnswerShape>( reader.GetString( 2 ), ignoreCase: true ));
    }

    /// <summary>Final attempt per seat: the highest attempt number, with the attempt count.</summary>
    private async Task<List<SweepRow>> LoadFinalAttemptsAsync( long sweepId, CancellationToken cancellationToken )
    {
        const string SQL = @"
WITH ranked AS (
    SELECT sc.*, ROW_NUMBER() OVER ( PARTITION BY sc.SeatId ORDER BY sc.AttemptNo DESC, sc.SweepCallId DESC ) AS rn,
           COUNT(*) OVER ( PARTITION BY sc.SeatId ) AS attempts
      FROM quorum.SweepCall sc WHERE sc.SweepId = @Id )
SELECT Platform, ModelId, Mode, ReasoningModeUsed, Status, IsRefusal, ReasoningEvidence, AnswerText, MaxTokensSent,
       FinishReason, CompletionTokens, ReasoningTokens, LatencyMs, attempts, Decision, ErrorText, BaseModel
  FROM ranked WHERE rn = 1;";

        var rows = new List<SweepRow>();
        await using var connection = new SqlConnection( _connectionString );
        await connection.OpenAsync( cancellationToken );
        await using var command = new SqlCommand( SQL, connection );
        command.Parameters.AddWithValue( "@Id", sweepId );
        await using var reader = await command.ExecuteReaderAsync( cancellationToken );

        while( await reader.ReadAsync( cancellationToken ) )
        {
            rows.Add( new SweepRow
            {
                Provider = reader.GetString( 0 ), ModelId = reader.GetString( 1 ), Group = reader.GetString( 2 ),
                ReasoningModeUsed = reader.IsDBNull( 3 ) ? "" : reader.GetString( 3 ), Status = reader.IsDBNull( 4 ) ? "Unknown" : reader.GetString( 4 ),
                IsRefusal = reader.GetBoolean( 5 ), ReasoningEvidence = reader.GetBoolean( 6 ),
                Answer = reader.IsDBNull( 7 ) ? null : reader.GetString( 7 ), MaxTokensSent = reader.IsDBNull( 8 ) ? null : reader.GetInt32( 8 ),
                FinishReason = reader.IsDBNull( 9 ) ? null : reader.GetString( 9 ), CompletionTokens = reader.IsDBNull( 10 ) ? null : reader.GetInt32( 10 ),
                ReasoningTokens = reader.IsDBNull( 11 ) ? null : reader.GetInt32( 11 ), LatencyMs = reader.IsDBNull( 12 ) ? 0 : reader.GetInt32( 12 ),
                Attempts = reader.GetInt32( 13 ), Decision = reader.IsDBNull( 14 ) ? null : reader.GetString( 14 ),
                ErrorText = reader.IsDBNull( 15 ) ? null : reader.GetString( 15 ), BaseModel = reader.IsDBNull( 16 ) ? reader.GetString( 1 ) : reader.GetString( 16 )
            } );
        }

        return rows;
    }

    #endregion Private Methods
}

/// <summary>Final attempt for one seat, as reported.</summary>
internal sealed class SweepRow
{
    public string Provider { get; init; } = "";
    public string ModelId { get; init; } = "";
    public string Group { get; init; } = "";
    public string ReasoningModeUsed { get; init; } = "";
    public string Status { get; init; } = "";
    public bool IsRefusal { get; init; }
    public bool ReasoningEvidence { get; init; }
    public string? Answer { get; init; }
    public int? MaxTokensSent { get; init; }
    public string? FinishReason { get; init; }
    public int? CompletionTokens { get; init; }
    public int? ReasoningTokens { get; init; }
    public int LatencyMs { get; init; }
    public int Attempts { get; init; }
    public string? Decision { get; init; }
    public string? ErrorText { get; init; }
    public string BaseModel { get; init; } = "";
    public string Grade { get; set; } = "";
}
