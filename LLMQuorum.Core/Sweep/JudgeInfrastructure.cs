using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;

namespace LLMQuorum.Core.Sweep;

/// <summary>
/// A judge backed by an ordinary seat profile, so judge calls go through the same quota gate,
/// retry rules and free-tier checks as sweep calls.
/// </summary>
public sealed class ProfileJudge : IAnswerJudge
{
    #region Data Members

    private readonly ProfileCaller _caller;
    private readonly ModelProfile _profile;
    private readonly SemaphoreSlim _spacing = new( 1, 1 );

    #endregion Data Members

    #region Constructor

    /// <summary>Builds a judge on a seat.</summary>
    /// <param name="caller">Shared caller.</param>
    /// <param name="profile">Seat to use as the judge.</param>
    public ProfileJudge( ProfileCaller caller, ModelProfile profile )
    {
        _caller = caller;
        _profile = profile;
    }

    #endregion Constructor

    #region Public Methods

    /// <inheritdoc />
    public string JudgeSeat => _profile.SeatId;

    /// <inheritdoc />
    public async Task<JudgeCall> AskAsync( string prompt, CancellationToken cancellationToken )
    {
        // One call at a time per judge, spaced as the seat's profile requires.
        await _spacing.WaitAsync( cancellationToken );

        try
        {
            var attempts = await _caller.CallAsync( _profile, prompt, new ProviderRunState(), cancellationToken );
            var last = attempts[^1];
            var text = last.Extracted?.RawContent ?? last.Extracted?.Answer;
            var ok = last.Status == "Answered" && !string.IsNullOrWhiteSpace( text );
            await Task.Delay( _profile.SpacingMs, cancellationToken );
            return new JudgeCall( ok, text, last.ResponseBytes, last.LatencyMs,
                                  ok ? null : $"{last.Status}: {last.Decision ?? last.Extracted?.ErrorMessage}" );
        }
        finally
        {
            _spacing.Release();
        }
    }

    #endregion Public Methods
}

/// <summary>Judgements in quorum.SweepJudgement.</summary>
public sealed class SqlJudgementStore : IJudgementStore
{
    #region Data Members

    private readonly string _connectionString;

    #endregion Data Members

    #region Constructor

    /// <summary>Builds the store.</summary>
    /// <param name="connectionString">LLMQuorum database.</param>
    public SqlJudgementStore( string connectionString )
    {
        _connectionString = connectionString;
    }

    #endregion Constructor

    #region Public Methods

    /// <inheritdoc />
    public async Task<JudgeVerdict?> FindAsync( long sweepCallId, string judgeSeat, string rubricVersion, CancellationToken cancellationToken )
    {
        await using var connection = new SqlConnection( _connectionString );
        await connection.OpenAsync( cancellationToken );
        await using var command = new SqlCommand( @"
SELECT TOP 1 Verdict, Reason, MatchLabel FROM quorum.SweepJudgement
 WHERE SweepCallId = @CallId AND JudgeSeat = @Seat AND RubricVersion = @Rubric AND Verdict IS NOT NULL
 ORDER BY SweepJudgementId DESC;", connection );
        command.Parameters.Add( "@CallId", SqlDbType.BigInt ).Value = sweepCallId;
        command.Parameters.Add( "@Seat", SqlDbType.VarChar, 300 ).Value = judgeSeat;
        command.Parameters.Add( "@Rubric", SqlDbType.VarChar, 40 ).Value = rubricVersion;
        await using var reader = await command.ExecuteReaderAsync( cancellationToken );

        return await reader.ReadAsync( cancellationToken )
            ? new JudgeVerdict( judgeSeat, reader.GetString( 0 ), reader.IsDBNull( 1 ) ? null : reader.GetString( 1 ), reader.IsDBNull( 2 ) ? null : reader.GetString( 2 ) )
            : null;
    }

    /// <inheritdoc />
    public async Task SaveAsync( long sweepCallId, string rubricVersion, string prompt, JudgeCall call, JudgeVerdict verdict,
                                 CancellationToken cancellationToken )
    {
        await using var connection = new SqlConnection( _connectionString );
        await connection.OpenAsync( cancellationToken );
        await using var command = new SqlCommand( @"
INSERT INTO quorum.SweepJudgement
    ( SweepCallId, JudgeSeat, RubricVersion, GraderVersion, Verdict, Reason, MatchLabel, IsSuccess, ErrorText, PromptText, ReplyText, ResponseBytes, LatencyMs )
VALUES
    ( @CallId, @Seat, @Rubric, @Grader, @Verdict, @Reason, @Match, @Success, @Error, @Prompt, @Reply, @Bytes, @Latency );", connection );

        command.Parameters.Add( "@CallId", SqlDbType.BigInt ).Value = sweepCallId;
        command.Parameters.Add( "@Seat", SqlDbType.VarChar, 300 ).Value = verdict.JudgeSeat;
        command.Parameters.Add( "@Rubric", SqlDbType.VarChar, 40 ).Value = rubricVersion;
        command.Parameters.Add( "@Grader", SqlDbType.VarChar, 40 ).Value = SweepGrader.VERSION;
        command.Parameters.Add( "@Verdict", SqlDbType.VarChar, 20 ).Value = (object?)verdict.Verdict ?? DBNull.Value;
        command.Parameters.Add( "@Reason", SqlDbType.NVarChar, 400 ).Value = (object?)Clip( verdict.Reason, 400 ) ?? DBNull.Value;
        command.Parameters.Add( "@Match", SqlDbType.NVarChar, 200 ).Value = (object?)verdict.MatchLabel ?? DBNull.Value;
        command.Parameters.Add( "@Success", SqlDbType.Bit ).Value = call.IsSuccess;
        command.Parameters.Add( "@Error", SqlDbType.NVarChar, 2000 ).Value = (object?)Clip( call.ErrorText, 2000 ) ?? DBNull.Value;
        command.Parameters.Add( "@Prompt", SqlDbType.NVarChar, -1 ).Value = prompt;
        command.Parameters.Add( "@Reply", SqlDbType.NVarChar, -1 ).Value = (object?)call.Text ?? DBNull.Value;
        command.Parameters.Add( "@Bytes", SqlDbType.VarBinary, -1 ).Value = (object?)call.ResponseBytes ?? DBNull.Value;
        command.Parameters.Add( "@Latency", SqlDbType.Int ).Value = call.LatencyMs;
        await command.ExecuteNonQueryAsync( cancellationToken );
    }

    #endregion Public Methods

    #region Private Methods

    private static string? Clip( string? value, int max ) => value is null || value.Length <= max ? value : value[..max];

    #endregion Private Methods
}
