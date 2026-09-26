using System.Data;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace LLMQuorum.Core.Sweep;

/// <summary>
/// Asks one stored question to every enabled seat in profiles.json and persists every attempt.
///
/// Providers run concurrently; seats within a provider run one at a time with the profile's
/// spacing. Serial per provider is deliberate: Z.ai's free flash models rejected a parallel
/// pair with 1305, Groq and OpenRouter meter per minute and per day, and Cloudflare's neuron
/// gate must see each call's cost before pricing the next.
/// </summary>
public sealed class SweepRunner
{
    #region Data Members

    /// <summary>Recorded on the sweep so results can be tied to the harness that produced them.</summary>
    public const string HARNESS_VERSION = "sweep-v2";

    private readonly string _connectionString;
    private readonly ProfileCaller _caller;

    #endregion Data Members

    #region Constructor

    /// <summary>Builds a runner.</summary>
    /// <param name="connectionString">LLMQuorum database.</param>
    /// <param name="caller">Seat caller with the quota gate attached.</param>
    public SweepRunner( string connectionString, ProfileCaller caller )
    {
        _connectionString = connectionString;
        _caller = caller;
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>Runs the sweep and returns its id.</summary>
    /// <param name="questionId">Stored question to ask.</param>
    /// <param name="profiles">Seats to call; disabled ones are ignored.</param>
    /// <param name="label">Optional label.</param>
    /// <param name="providerFilter">Optional provider keys to restrict the sweep to.</param>
    /// <param name="seatFilter">Optional seat id fragments; a seat runs when its SeatId contains any of them.</param>
    /// <param name="progress">Receives one line per attempt.</param>
    /// <param name="cancellationToken">Cancels the sweep.</param>
    /// <returns>The sweep id.</returns>
    public async Task<long> RunAsync(
        int questionId, IReadOnlyList<ModelProfile> profiles, string? label, IReadOnlyCollection<string>? providerFilter,
        IReadOnlyCollection<string>? seatFilter, IProgress<string>? progress, CancellationToken cancellationToken )
    {
        var prompt = await LoadPromptAsync( questionId, cancellationToken );

        // Paid seats already asked this question (answered, billed, held or in flight) are never sent again, whatever
        // the entry point: plan runner, ad-hoc sweep, or a Hangfire replay after a restart.
        var asked = profiles.Any( p => p.Paid )
            ? await new SpendGate( _connectionString, string.Empty ).AskedSeatsAsync( questionId, cancellationToken )
            : new HashSet<string>();

        var seats = profiles
            .Where( p => p.Enabled && ( providerFilter is null || providerFilter.Count == 0 || providerFilter.Contains( p.Provider ) ) )
            .Where( p => seatFilter is null || seatFilter.Count == 0 || seatFilter.Any( f => SeatMatches( p, f ) ) )
            .Where( p => !p.Paid || ( seatFilter is not null && seatFilter.Contains( p.SeatId, StringComparer.Ordinal ) && !asked.Contains( p.SeatId ) ) )
            .ToList();

        var sweepId = await CreateSweepAsync( questionId, label, cancellationToken );
        var context = new SweepContext( questionId, sweepId );
        var state = new ProviderRunState();

        await Task.WhenAll( seats.GroupBy( p => p.Provider )
                                 .Select( group => RunProviderAsync( sweepId, prompt, group.ToList(), state, context, progress, cancellationToken ) ) );

        await ExecuteAsync( "UPDATE quorum.Sweep SET CompletedUtc = SYSUTCDATETIME() WHERE SweepId = @Id;",
                            cancellationToken, ( "@Id", sweepId ) );
        return sweepId;
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// Free seats match a filter by case-insensitive substring, as always. Paid seats match only their exact seat id,
    /// never a fragment, a provider filter or an empty filter.
    /// </summary>
    private static bool SeatMatches( ModelProfile profile, string filter ) =>
        profile.Paid
            ? string.Equals( filter, profile.SeatId, StringComparison.Ordinal )
            : profile.SeatId.Contains( filter, StringComparison.OrdinalIgnoreCase );

    /// <summary>Calls one provider's seats serially and persists each attempt as it happens.</summary>
    private async Task RunProviderAsync(
        long sweepId, string prompt, List<ModelProfile> seats, ProviderRunState state, SweepContext context,
        IProgress<string>? progress, CancellationToken cancellationToken )
    {
        foreach( var seat in seats )
        {
            cancellationToken.ThrowIfCancellationRequested();

            var attempts = await _caller.CallAsync( seat, prompt, state, cancellationToken, context );

            foreach( var attempt in attempts )
            {
                // A paid attempt is persisted even if the job is being cancelled: it may have billed.
                await PersistAsync( sweepId, attempt, seat.Paid ? CancellationToken.None : cancellationToken );
                progress?.Report( $"{seat.Provider,-11} {seat.ModelId,-50} #{attempt.AttemptNo} {attempt.Status,-18} {attempt.Decision}" );
            }

            if( attempts.Any( a => a.AttemptNo > 0 ) )
            {
                await Task.Delay( seat.SpacingMs, cancellationToken );
            }
        }
    }

    /// <summary>Writes one attempt. Append only.</summary>
    private async Task PersistAsync( long sweepId, CallAttempt attempt, CancellationToken cancellationToken )
    {
        const string SQL = @"
INSERT INTO quorum.SweepCall
    ( SweepId, Platform, ModelId, Mode, AttemptNo, RequestJson, MaxTokensSent, ContextWindow, LatencyMs, HttpStatus,
      IsSuccess, ErrorClass, ErrorText, FinishReason, PromptTokens, CompletionTokens, ReasoningTokens, ReasoningStripped,
      ResponseBytes, AnswerText, SeatId, BaseModel, ReasoningModeUsed, Status, IsRefusal, ReasoningEvidence,
      RequestBytes, ResponseHeaders, RawContent, ReasoningText, Neurons, Decision, ExtractorVersion,
      CostUsd, ServedProvider, GenerationId, WebSearchRequests )
VALUES
    ( @SweepId, @Platform, @ModelId, @Mode, @AttemptNo, @RequestJson, @MaxTokensSent, @ContextWindow, @LatencyMs, @HttpStatus,
      @IsSuccess, @ErrorClass, @ErrorText, @FinishReason, @PromptTokens, @CompletionTokens, @ReasoningTokens, @ReasoningStripped,
      @ResponseBytes, @AnswerText, @SeatId, @BaseModel, @ReasoningModeUsed, @Status, @IsRefusal, @ReasoningEvidence,
      @RequestBytes, @ResponseHeaders, @RawContent, @ReasoningText, @Neurons, @Decision, @ExtractorVersion,
      @CostUsd, @ServedProvider, @GenerationId, @WebSearchRequests );";

        var p = attempt.Profile;
        var x = attempt.Extracted;

        await ExecuteAsync( SQL, cancellationToken,
            ( "@SweepId", sweepId ), ( "@Platform", p.Provider ), ( "@ModelId", p.ModelId ), ( "@Mode", p.Group ),
            ( "@AttemptNo", Math.Max( attempt.AttemptNo, 0 ) ),
            ( "@RequestJson", attempt.RequestBytes is null ? null : Encoding.UTF8.GetString( attempt.RequestBytes ) ),
            ( "@MaxTokensSent", attempt.MaxTokensSent ), ( "@ContextWindow", p.ContextWindow ), ( "@LatencyMs", attempt.LatencyMs ),
            ( "@HttpStatus", attempt.HttpStatus ), ( "@IsSuccess", attempt.Status == "Answered" ),
            ( "@ErrorClass", MapErrorClass( attempt.Status ) ),
            ( "@ErrorText", Clip( x?.ErrorMessage ?? attempt.Decision, 2000 ) ), ( "@FinishReason", Clip( x?.FinishReason, 40 ) ),
            ( "@PromptTokens", x?.PromptTokens ), ( "@CompletionTokens", x?.CompletionTokens ), ( "@ReasoningTokens", x?.ReasoningTokens ),
            ( "@ReasoningStripped", x?.RawContent is not null && x.Answer is not null && x.RawContent.Trim() != x.Answer ),
            ( "@ResponseBytes", attempt.ResponseBytes ), ( "@AnswerText", x?.Answer ), ( "@SeatId", p.SeatId ),
            ( "@BaseModel", p.BaseModel ), ( "@ReasoningModeUsed", attempt.ReasoningModeUsed ), ( "@Status", attempt.Status ),
            ( "@IsRefusal", x?.IsRefusal ?? false ), ( "@ReasoningEvidence", x?.ReasoningEvidence ?? false ),
            ( "@RequestBytes", attempt.RequestBytes ),
            ( "@ResponseHeaders", attempt.Headers.Count == 0 ? null : JsonSerializer.Serialize( attempt.Headers ) ),
            ( "@RawContent", x?.RawContent ), ( "@ReasoningText", x?.ReasoningText ), ( "@Neurons", attempt.Neurons ),
            ( "@Decision", Clip( attempt.Decision, 200 ) ), ( "@ExtractorVersion", ResponseExtractor.VERSION ),
            ( "@CostUsd", attempt.CostUsd ?? ( p.Provider == "openrouter" ? x?.Cost : null ) ),
            ( "@ServedProvider", Clip( x?.ServedProvider, 100 ) ), ( "@GenerationId", Clip( x?.GenerationId, 100 ) ),
            ( "@WebSearchRequests", x?.WebSearchRequests ) );
    }

    /// <summary>Maps attempt status onto the CHECK-constrained ErrorClass column.</summary>
    private static string? MapErrorClass( string status ) => status switch
    {
        "Answered" => null,
        "Truncated" or "ThinkingExhausted" => "truncated",
        "Empty" => "empty",
        "Timeout" => "timeout",
        "Network" => "network",
        "Blocked" => "gated",
        "BudgetSkipped" or "ProviderStopped" or "AlreadyAsked" => "rate-limit",
        _ => "other"
    };

    private async Task<string> LoadPromptAsync( int questionId, CancellationToken cancellationToken )
    {
        await using var connection = new SqlConnection( _connectionString );
        await connection.OpenAsync( cancellationToken );
        await using var command = new SqlCommand( "SELECT Prompt FROM quorum.Question WHERE QuestionId = @Id;", connection );
        command.Parameters.AddWithValue( "@Id", questionId );
        return (string?)await command.ExecuteScalarAsync( cancellationToken )
               ?? throw new InvalidOperationException( $"Question {questionId} not found." );
    }

    private async Task<long> CreateSweepAsync( int questionId, string? label, CancellationToken cancellationToken )
    {
        await using var connection = new SqlConnection( _connectionString );
        await connection.OpenAsync( cancellationToken );
        await using var command = new SqlCommand( @"
INSERT INTO quorum.Sweep ( QuestionId, Label, HarnessVersion ) OUTPUT INSERTED.SweepId
VALUES ( @QuestionId, @Label, @Version );", connection );
        command.Parameters.AddWithValue( "@QuestionId", questionId );
        command.Parameters.AddWithValue( "@Label", (object?)label ?? DBNull.Value );
        command.Parameters.AddWithValue( "@Version", HARNESS_VERSION );
        return Convert.ToInt64( await command.ExecuteScalarAsync( cancellationToken ) );
    }

    /// <summary>Executes a statement with parameters, sending byte arrays as VARBINARY(MAX).</summary>
    private async Task ExecuteAsync( string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters )
    {
        await using var connection = new SqlConnection( _connectionString );
        await connection.OpenAsync( cancellationToken );
        await using var command = new SqlCommand( sql, connection );

        foreach( var (name, value) in parameters )
        {
            // A null byte array reaches here untyped; AddWithValue would bind it as NVARCHAR and
            // SQL Server refuses the implicit NVARCHAR to VARBINARY(MAX) conversion.
            if( value is byte[] || name.EndsWith( "Bytes", StringComparison.Ordinal ) )
            {
                command.Parameters.Add( name, SqlDbType.VarBinary, -1 ).Value = value ?? DBNull.Value;
            }
            else
            {
                command.Parameters.AddWithValue( name, value ?? DBNull.Value );
            }
        }

        await command.ExecuteNonQueryAsync( cancellationToken );
    }

    private static string? Clip( string? value, int max ) => value is null || value.Length <= max ? value : value[..max];

    #endregion Private Methods
}
