using LLMQuorum.Core.Models;
using Microsoft.Data.SqlClient;

namespace LLMQuorum.Core.Sweep;

/// <summary>One seat's chosen result on the per-question sheet.</summary>
public sealed class SheetSeat
{
    public long SweepId { get; init; }
    public long SweepCallId { get; init; }
    public string SeatId { get; init; } = "";
    public string Provider { get; init; } = "";
    public string ModelId { get; init; } = "";
    public string BaseModel { get; init; } = "";
    public string Group { get; init; } = "";
    public string ReasoningModeUsed { get; init; } = "";
    public bool ReasoningEvidence { get; init; }
    public string Status { get; init; } = "";
    public bool IsRefusal { get; init; }
    public string? Answer { get; init; }
    public int? MaxTokensSent { get; init; }
    public int? CompletionTokens { get; init; }
    public int? ReasoningTokens { get; init; }
    public int LatencyMs { get; init; }
    public int Attempts { get; init; }
    public string? Decision { get; init; }
    public string? ErrorText { get; init; }
    public int Runs { get; set; } = 1;
    public string Grade { get; set; } = "";
    public string GradeNote { get; set; } = "";
    public string Route { get; set; } = "";
    public string? MatchNote { get; set; }
    public IReadOnlyList<JudgeVerdict> Verdicts { get; set; } = Array.Empty<JudgeVerdict>();
    public SheetGrade Bucket { get; set; }
    public string? ClusterKey { get; set; }
    public int SameAnswerSeats { get; set; }
    public int SameAnswerModels { get; set; }
}

/// <summary>A written sheet.</summary>
/// <param name="Seats">Graded seats in sheet order.</param>
/// <param name="Path">Where the workbook was written.</param>
public sealed record SheetResult( IReadOnlyList<SheetSeat> Seats, string Path );

/// <summary>The question fields the sheet needs.</summary>
/// <param name="Id">Question id.</param>
/// <param name="Prompt">Question text.</param>
/// <param name="Expected">Answer key.</param>
/// <param name="Source">Where the key came from.</param>
/// <param name="Shape">Answer shape.</param>
/// <param name="Category">Question category.</param>
/// <param name="Known">Verified non-key answers (older versions, other forms).</param>
/// <param name="JudgeNonExact">Judge-mode question: rules accept only exact matches.</param>
/// <param name="Topic">Display category for the public site.</param>
public sealed record SheetQuestion( int Id, string Prompt, string? Expected, string? Source, AnswerShape Shape, string Category,
                                    IReadOnlyList<KnownAnswer> Known, bool JudgeNonExact = false, string? Topic = null )
{
    /// <summary>Grading context for this question.</summary>
    public QuestionContext Context => new( Expected, Shape, Category, Known, JudgeNonExact );
}

/// <summary>
/// One spreadsheet per question across every sweep of it. Each current seat shows its newest result
/// that reached the model (a newer transient error never hides an older real answer). Rules grade
/// failed calls and plain lists; judges grade the rest. Rows in quorum.SweepExclusion and seats no
/// longer in profiles.json are left out and named on the sheet.
/// </summary>
public sealed class QuestionSheet
{
    #region Data Members

    private static readonly HashSet<string> _reachedModel = new( StringComparer.Ordinal )
    {
        "Answered", "Truncated", "ThinkingExhausted", "Empty", "Filtered"
    };

    private readonly string _connectionString;
    private readonly JudgePanel _panel;
    private readonly IReadOnlySet<string>? _currentSeatIds;
    private readonly SweepGrader _grader = new();

    #endregion Data Members

    #region Constructor

    /// <summary>Builds a sheet generator.</summary>
    /// <param name="connectionString">LLMQuorum database.</param>
    /// <param name="panel">Judges for answers rules cannot grade.</param>
    /// <param name="currentSeatIds">Seat ids in profiles.json; null keeps every seat ever run.</param>
    public QuestionSheet( string connectionString, JudgePanel panel, IReadOnlySet<string>? currentSeatIds )
    {
        _connectionString = connectionString;
        _panel = panel;
        _currentSeatIds = currentSeatIds;
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>Grades every seat for a question and writes the .xlsx.</summary>
    /// <param name="questionId">Question to report.</param>
    /// <param name="outputPath">.xlsx path; overwritten unless it is open elsewhere.</param>
    /// <param name="cancellationToken">Cancels queries and judge calls.</param>
    /// <returns>The graded seats in sheet order and the path actually written.</returns>
    public async Task<SheetResult> WriteAsync( int questionId, string outputPath, CancellationToken cancellationToken )
    {
        var question = await LoadQuestionAsync( questionId, cancellationToken );
        var candidates = await LoadCandidatesAsync( questionId, cancellationToken );
        var exclusions = await LoadExclusionsAsync( questionId, cancellationToken );

        var retired = _currentSeatIds is null ? new List<string>()
            : candidates.Select( c => c.SeatId ).Distinct().Where( id => !_currentSeatIds.Contains( id ) ).OrderBy( id => id ).ToList();
        exclusions.AddRange( retired.Select( id => $"not shown: {id} (disabled or retired in profiles.json)" ) );

        var seats = PickLatestUsable( candidates, _currentSeatIds );
        await GradeAsync( seats, question, cancellationToken );
        var ordered = Order( seats, question.Shape );

        var sweepIds = candidates.Select( c => c.SweepId ).Distinct().OrderBy( id => id ).ToList();
        var layout = QuestionSheetLayout.Build( question, sweepIds, exclusions, ordered );
        var tempPath = Path.Combine( Path.GetDirectoryName( outputPath )!, $".question-{questionId}-{Guid.NewGuid():N}.tmp" );
        XlsxWriter.Write( tempPath, $"Question {questionId}", layout.Rows, QuestionSheetLayout.ColumnWidths, layout.FilterRange );
        await SaveGradesAsync( questionId, ordered, cancellationToken );
        return new SheetResult( ordered, PlaceFile( tempPath, outputPath ) );
    }

    /// <summary>
    /// Moves the finished workbook into place. MEASURED 2026-09-17: the owner had the previous sheet open
    /// in Excel, which locks it, and the refresh failed after the judges had already run. A locked target
    /// now gets a timestamped sibling instead of losing the work.
    /// </summary>
    /// <param name="tempPath">Written workbook.</param>
    /// <param name="outputPath">Preferred path.</param>
    /// <returns>The path the workbook ended up at.</returns>
    public static string PlaceFile( string tempPath, string outputPath )
    {
        try
        {
            File.Move( tempPath, outputPath, overwrite: true );
            return outputPath;
        }
        catch( Exception ex ) when( ex is IOException or UnauthorizedAccessException )
        {
            var alternate = Path.Combine( Path.GetDirectoryName( outputPath )!,
                $"{Path.GetFileNameWithoutExtension( outputPath )}-{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension( outputPath )}" );
            File.Move( tempPath, alternate, overwrite: true );
            return alternate;
        }
    }

    /// <summary>
    /// Newest result per current seat that reached the model; if none did, the newest failure.
    /// </summary>
    /// <param name="candidates">Final attempt per (sweep, seat).</param>
    /// <param name="currentSeatIds">Seat ids still configured; null keeps all.</param>
    /// <returns>One row per seat, with Runs set to how many sweeps included it.</returns>
    public static List<SheetSeat> PickLatestUsable( IEnumerable<SheetSeat> candidates, IReadOnlySet<string>? currentSeatIds )
    {
        var picked = new List<SheetSeat>();

        foreach( var group in candidates.Where( c => currentSeatIds is null || currentSeatIds.Contains( c.SeatId ) )
                                        .GroupBy( c => c.SeatId, StringComparer.Ordinal ) )
        {
            var newestFirst = group.OrderByDescending( c => c.SweepId ).ToList();
            var chosen = newestFirst.FirstOrDefault( c => _reachedModel.Contains( c.Status ) ) ?? newestFirst[0];
            chosen.Runs = newestFirst.Count;
            picked.Add( chosen );
        }

        return picked;
    }

    /// <summary>Rule tier first; judges only for what rules hand over.</summary>
    /// <param name="seats">Chosen seats.</param>
    /// <param name="question">Question with key.</param>
    /// <param name="cancellationToken">Cancels judge calls.</param>
    /// <returns>Completes when every seat has a grade.</returns>
    public async Task GradeAsync( IReadOnlyList<SheetSeat> seats, SheetQuestion question, CancellationToken cancellationToken )
    {
        var context = question.Context;

        foreach( var seat in seats )
        {
            var rule = _grader.Grade( seat.Status, seat.IsRefusal, seat.Answer, context );
            seat.Route = rule.Route;

            if( rule.NeedsJudge )
            {
                var panel = await _panel.GradeAsync( seat.SweepCallId, question.Prompt, question.Source, seat.Answer ?? string.Empty,
                                                     context, cancellationToken );
                var (grade, note) = panel.Grade == "WRONG"
                    ? SweepGrader.ClassifyJudgedWrong( panel.MatchLabel, panel.MatchAgreed, context )
                    : (panel.Grade, null);

                seat.Grade = grade;
                seat.GradeNote = panel.Note;
                seat.MatchNote = note;
                seat.Verdicts = panel.Verdicts;
            }
            else
            {
                seat.Grade = rule.Grade!;
                seat.GradeNote = rule.Grade == "CORRECT-LOOSE" ? "loose" : "";
                seat.MatchNote = rule.Note;
            }

            seat.Bucket = SweepGrader.Bucket( seat.Grade );
        }
    }

    /// <summary>Counts identical answers and sorts into sheet order.</summary>
    /// <param name="seats">Graded seats.</param>
    /// <param name="shape">Answer shape.</param>
    /// <returns>Seats in bucket order, largest same-answer group first.</returns>
    public List<SheetSeat> Order( IReadOnlyList<SheetSeat> seats, AnswerShape shape )
    {
        foreach( var seat in seats )
        {
            seat.ClusterKey = ( seat.Bucket is SheetGrade.Correct or SheetGrade.Outdated or SheetGrade.Hallucinated or SheetGrade.Wrong or SheetGrade.Unclassified )
                              && seat.Answer is not null
                ? _grader.ClusterKey( seat.Answer, shape ) : null;
        }

        foreach( var cluster in seats.Where( s => s.ClusterKey is not null ).GroupBy( s => s.ClusterKey! ) )
        {
            var models = cluster.Select( s => s.BaseModel ).Distinct( StringComparer.OrdinalIgnoreCase ).Count();

            foreach( var seat in cluster )
            {
                seat.SameAnswerSeats = cluster.Count();
                seat.SameAnswerModels = models;
            }
        }

        return seats
            .OrderBy( s => s.Bucket )
            .ThenBy( s => s.MatchNote, StringComparer.Ordinal )
            .ThenByDescending( s => s.SameAnswerSeats )
            .ThenBy( s => s.ClusterKey, StringComparer.Ordinal )
            .ThenBy( s => s.Provider, StringComparer.Ordinal )
            .ThenBy( s => s.ModelId, StringComparer.Ordinal )
            .ThenBy( s => s.Group, StringComparer.Ordinal )
            .ToList();
    }

    #endregion Public Methods

    #region Private Methods

    private async Task<SheetQuestion> LoadQuestionAsync( int questionId, CancellationToken cancellationToken )
    {
        await using var connection = new SqlConnection( _connectionString );
        await connection.OpenAsync( cancellationToken );
        await using var command = new SqlCommand( @"
SELECT Prompt, ExpectedAnswer, ExpectedSource, AnswerShape, Category, GradeMode, Topic FROM quorum.Question WHERE QuestionId = @Id;
SELECT Kind, Label, Answer FROM quorum.QuestionKnownAnswer WHERE QuestionId = @Id ORDER BY Kind DESC, Label;", connection );
        command.Parameters.AddWithValue( "@Id", questionId );
        await using var reader = await command.ExecuteReaderAsync( cancellationToken );

        if( !await reader.ReadAsync( cancellationToken ) )
        {
            throw new InvalidOperationException( $"Question {questionId} not found." );
        }

        var (prompt, expected, source) = (reader.GetString( 0 ), NullableString( reader, 1 ), NullableString( reader, 2 ));
        var shape = Enum.Parse<AnswerShape>( reader.GetString( 3 ), ignoreCase: true );
        var category = reader.GetString( 4 );
        var judgeMode = reader.GetString( 5 ) == "judge";
        var topic = NullableString( reader, 6 );
        var known = new List<KnownAnswer>();
        await reader.NextResultAsync( cancellationToken );

        while( await reader.ReadAsync( cancellationToken ) )
        {
            known.Add( new KnownAnswer( reader.GetString( 0 ), reader.GetString( 1 ), reader.GetString( 2 ) ) );
        }

        return new SheetQuestion( questionId, prompt, expected, source, shape, category, known, judgeMode, topic );
    }

    /// <summary>Final attempt per (sweep, seat) for every non-excluded sweep row of the question.</summary>
    private async Task<List<SheetSeat>> LoadCandidatesAsync( int questionId, CancellationToken cancellationToken )
    {
        const string SQL = @"
WITH ranked AS (
    SELECT sc.*,
           ROW_NUMBER() OVER ( PARTITION BY sc.SweepId, sc.SeatId ORDER BY sc.AttemptNo DESC, sc.SweepCallId DESC ) AS rn,
           COUNT(*) OVER ( PARTITION BY sc.SweepId, sc.SeatId ) AS attempts
      FROM quorum.SweepCall sc
      JOIN quorum.Sweep s ON s.SweepId = sc.SweepId
     WHERE s.QuestionId = @Id AND sc.SeatId IS NOT NULL
       AND NOT EXISTS ( SELECT 1 FROM quorum.SweepExclusion x WHERE x.SweepId = sc.SweepId AND x.Platform = sc.Platform ) )
SELECT SweepId, SweepCallId, SeatId, Platform, ModelId, ISNULL( BaseModel, ModelId ), Mode, ISNULL( ReasoningModeUsed, '' ),
       ReasoningEvidence, ISNULL( Status, 'Unknown' ), IsRefusal, AnswerText, MaxTokensSent, CompletionTokens, ReasoningTokens,
       ISNULL( LatencyMs, 0 ), attempts, Decision, ErrorText
  FROM ranked WHERE rn = 1;";

        var rows = new List<SheetSeat>();
        await using var connection = new SqlConnection( _connectionString );
        await connection.OpenAsync( cancellationToken );
        await using var command = new SqlCommand( SQL, connection );
        command.Parameters.AddWithValue( "@Id", questionId );
        await using var reader = await command.ExecuteReaderAsync( cancellationToken );

        while( await reader.ReadAsync( cancellationToken ) )
        {
            rows.Add( new SheetSeat
            {
                SweepId = reader.GetInt64( 0 ), SweepCallId = reader.GetInt64( 1 ), SeatId = reader.GetString( 2 ), Provider = reader.GetString( 3 ),
                ModelId = reader.GetString( 4 ), BaseModel = reader.GetString( 5 ), Group = reader.GetString( 6 ), ReasoningModeUsed = reader.GetString( 7 ),
                ReasoningEvidence = reader.GetBoolean( 8 ), Status = reader.GetString( 9 ), IsRefusal = reader.GetBoolean( 10 ),
                Answer = NullableString( reader, 11 ), MaxTokensSent = NullableInt( reader, 12 ), CompletionTokens = NullableInt( reader, 13 ),
                ReasoningTokens = NullableInt( reader, 14 ), LatencyMs = reader.GetInt32( 15 ), Attempts = reader.GetInt32( 16 ),
                Decision = NullableString( reader, 17 ), ErrorText = NullableString( reader, 18 )
            } );
        }

        return rows;
    }

    /// <summary>Exclusions that touch this question's sweeps, as display lines.</summary>
    private async Task<List<string>> LoadExclusionsAsync( int questionId, CancellationToken cancellationToken )
    {
        var lines = new List<string>();
        await using var connection = new SqlConnection( _connectionString );
        await connection.OpenAsync( cancellationToken );
        await using var command = new SqlCommand( @"
SELECT x.SweepId, x.Platform, x.Reason FROM quorum.SweepExclusion x
  JOIN quorum.Sweep s ON s.SweepId = x.SweepId WHERE s.QuestionId = @Id ORDER BY x.SweepId, x.Platform;", connection );
        command.Parameters.AddWithValue( "@Id", questionId );
        await using var reader = await command.ExecuteReaderAsync( cancellationToken );

        while( await reader.ReadAsync( cancellationToken ) )
        {
            lines.Add( $"sweep {reader.GetInt64( 0 )} {reader.GetString( 1 )}: {reader.GetString( 2 )}" );
        }

        return lines;
    }

    /// <summary>
    /// Replaces this question's rows in quorum.SeatGrade with the grades just computed, in one transaction,
    /// so the public site reads exactly what the sheet shows.
    /// </summary>
    private async Task SaveGradesAsync( int questionId, IReadOnlyList<SheetSeat> seats, CancellationToken cancellationToken )
    {
        await using var connection = new SqlConnection( _connectionString );
        await connection.OpenAsync( cancellationToken );
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync( cancellationToken );

        await using( var delete = new SqlCommand( "DELETE FROM quorum.SeatGrade WHERE QuestionId = @Q;", connection, transaction ) )
        {
            delete.Parameters.AddWithValue( "@Q", questionId );
            await delete.ExecuteNonQueryAsync( cancellationToken );
        }

        foreach( var seat in seats )
        {
            await using var insert = new SqlCommand( @"
INSERT INTO quorum.SeatGrade ( QuestionId, SeatId, SweepId, SweepCallId, Provider, ModelId, BaseModel, Mode, Grade, Bucket, GradeText,
                               MatchNote, HowGraded, AnswerText, SameAnswerSeats, SameAnswerModels, LatencyMs, Runs, GraderVersion )
VALUES ( @Q, @Seat, @Sweep, @Call, @Provider, @Model, @Base, @Mode, @Grade, @Bucket, @Text, @Match, @How, @Answer, @Same, @Models, @Latency, @Runs, @Grader );",
                connection, transaction );

            insert.Parameters.AddWithValue( "@Q", questionId );
            insert.Parameters.AddWithValue( "@Seat", seat.SeatId );
            insert.Parameters.AddWithValue( "@Sweep", seat.SweepId );
            insert.Parameters.AddWithValue( "@Call", seat.SweepCallId );
            insert.Parameters.AddWithValue( "@Provider", seat.Provider );
            insert.Parameters.AddWithValue( "@Model", seat.ModelId );
            insert.Parameters.AddWithValue( "@Base", seat.BaseModel );
            insert.Parameters.AddWithValue( "@Mode", seat.Group );
            insert.Parameters.AddWithValue( "@Grade", seat.Grade );
            insert.Parameters.AddWithValue( "@Bucket", seat.Bucket.ToString() );
            insert.Parameters.AddWithValue( "@Text", QuestionSheetLayout.GradeText( seat ) );
            insert.Parameters.AddWithValue( "@Match", (object?)seat.MatchNote ?? DBNull.Value );
            insert.Parameters.AddWithValue( "@How", QuestionSheetLayout.HowGraded( seat ) );
            insert.Parameters.AddWithValue( "@Answer", QuestionSheetLayout.AnswerText( seat ) );
            insert.Parameters.AddWithValue( "@Same", seat.ClusterKey is null ? DBNull.Value : (object)seat.SameAnswerSeats );
            insert.Parameters.AddWithValue( "@Models", seat.ClusterKey is null ? DBNull.Value : (object)seat.SameAnswerModels );
            insert.Parameters.AddWithValue( "@Latency", seat.LatencyMs );
            insert.Parameters.AddWithValue( "@Runs", seat.Runs );
            insert.Parameters.AddWithValue( "@Grader", SweepGrader.VERSION );
            await insert.ExecuteNonQueryAsync( cancellationToken );
        }

        await transaction.CommitAsync( cancellationToken );
    }

    private static string? NullableString( SqlDataReader reader, int ordinal ) => reader.IsDBNull( ordinal ) ? null : reader.GetString( ordinal );

    private static int? NullableInt( SqlDataReader reader, int ordinal ) => reader.IsDBNull( ordinal ) ? null : reader.GetInt32( ordinal );

    #endregion Private Methods
}
