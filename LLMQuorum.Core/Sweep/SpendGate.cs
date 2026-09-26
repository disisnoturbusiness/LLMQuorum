using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

namespace LLMQuorum.Core.Sweep;

/// <summary>Why a paid attempt was not sent, or the reservation it may be sent under.</summary>
/// <param name="SpendId">Reservation id when the attempt may be sent.</param>
/// <param name="ReservedUsd">What was reserved.</param>
/// <param name="Skip">Reason when it may not be sent.</param>
/// <param name="AlreadyAsked">True when this seat was already asked this question (never re-asked).</param>
/// <param name="KeyUsageBefore">The paid key's lifetime usage read just before the send.</param>
public sealed record SpendReservation( long? SpendId, decimal ReservedUsd, string? Skip, bool AlreadyAsked, decimal? KeyUsageBefore = null );

/// <summary>State of the paid lane for the plan runner.</summary>
/// <param name="Spent">USD counted against the cap locally.</param>
/// <param name="Limit">The cap.</param>
/// <param name="HaltedReason">Set when the lane was halted and must be cleared by the owner.</param>
public sealed record PaidLaneStatus( decimal Spent, decimal Limit, string? HaltedReason );

/// <summary>
/// The dollar cap for paid seats, version 5 (2026-09-20).
///
/// The owner chose each vendor's NATIVE web search. OpenRouter passes its cost through, ignores max_results and
/// max_uses, and charges a request when it finishes, so nothing bounds one call from inside. The estimate is a
/// planning figure, not a limit. What actually holds the line, checked before every paid attempt:
///   1. the paid key carries a fixed OpenRouter-side limit (no reset, at most <see cref="KEY_LIMIT_MAX_USD"/>)
///      whose limit_remaining covers the estimate; that is what stops NEW requests;
///   2. the account balance minus twice the estimate stays above a margin, so the free models keep credit;
///   3. the local cap in quorum.SpendCap, over the larger of the key's live usage and the local total plus the
///      reservations in flight; the reservation row is written before the send;
///   4. RECONCILE, in quorum.ReserveSpend: the key's lifetime usage may never exceed what the ledger accounts for.
///      A call that billed without being recorded (a timeout that finished upstream, a runaway search, a lost
///      settle) halts the lane. This is the check an estimate cannot make.
/// Any reservation marks the seat asked for that question, forever, whatever it settles at; only a rejection that
/// provably happened before any provider ran is voided. A halt is stored in the database AND as a file, so it
/// survives a database that was unreachable at the moment it had to be raised.
/// </summary>
public sealed class SpendGate
{
    #region Data Members

    /// <summary>The paid lane's cap in quorum.SpendCap.</summary>
    public const string CAP_NAME = "openrouter-paid";

    /// <summary>Account balance that must remain after the estimate, so the free models keep credit.</summary>
    public const decimal ACCOUNT_MARGIN_USD = 5.00m;

    /// <summary>
    /// The paid key's OpenRouter limit must be fixed and no higher than this. The owner's rule, 2026-09-25:
    /// "there should be no limits, use what's there till you run out". So the limit is no longer sized to a
    /// forecast; it sits above any balance he is likely to hold, and what actually stops the lane is the
    /// account balance itself, kept above <see cref="ACCOUNT_MARGIN_USD"/> so the free models keep working.
    /// The limit remains required and fixed because it is the only brake OpenRouter applies on its own side.
    /// </summary>
    public const decimal KEY_LIMIT_MAX_USD = 100.00m;

    /// <summary>File written beside the keys when the lane halts, so a halt survives an unreachable database.</summary>
    public const string HALT_FILE = "paid-halt.txt";

    /// <summary>Free key file, used only to read the account balance if the paid key is refused on that route.</summary>
    private const string FREE_KEY_FILE = "openrouter.key";

    private const string KEY_URL = "https://openrouter.ai/api/v1/key";
    private const string CREDITS_URL = "https://openrouter.ai/api/v1/credits";
    private const string GENERATION_URL = "https://openrouter.ai/api/v1/generation?id=";

    /// <summary>Waits before reading a generation record after a call: it appears a few seconds late.</summary>
    private static readonly int[] _settleWaitsMs = { 1500, 4000, 8000 };

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds( 20 ) };

    private readonly string _connectionString;
    private readonly string _keysDirectory;

    #endregion Data Members

    #region Constructor

    /// <summary>Builds the gate.</summary>
    /// <param name="connectionString">LLMQuorum database.</param>
    /// <param name="keysDirectory">Directory holding the key files.</param>
    public SpendGate( string connectionString, string keysDirectory )
    {
        _connectionString = connectionString;
        _keysDirectory = keysDirectory;
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>
    /// Estimate reserved for one attempt, priced on the tier the prompt actually lands in (MEASURED 2026-09-20: the
    /// OpenAI endpoints double at 272,000 prompt tokens, Google and xAI at 200,000). A web seat is priced on its
    /// input bound plus every assumed search, or on its own max_cost stop plus one more full turn, whichever is
    /// larger. For native search these are planning figures, not limits: the key's OpenRouter limit, the balance
    /// margin and the reconcile check are the backstops.
    /// </summary>
    /// <param name="profile">Paid seat.</param>
    /// <param name="promptTokens">Estimated prompt tokens.</param>
    /// <param name="cap">Output cap about to be sent.</param>
    /// <returns>USD.</returns>
    public static decimal WorstCaseUsd( ModelProfile profile, int promptTokens, int cap )
    {
        var turn = PriceTurn( profile, promptTokens, cap );

        if( profile.Group != "web-search" )
        {
            return Math.Round( turn, 8 );
        }

        // Whatever the bound says, nothing can be read past the model's own context window.
        var bound = Math.Min( Math.Max( promptTokens, profile.WebInputBoundTokens ), Math.Max( promptTokens, profile.ContextWindow - cap ) );
        var searched = PriceTurn( profile, bound, cap ) + profile.MaxSearchesPerCall * ( profile.SearchFeeUsd ?? 0 );
        var loop = MaxCostStop( profile ) + turn;
        return Math.Round( Math.Max( searched, loop ), 8 );
    }

    /// <summary>One turn at the price tier its prompt size lands in.</summary>
    /// <param name="profile">Paid seat.</param>
    /// <param name="promptTokens">Prompt tokens.</param>
    /// <param name="completionTokens">Completion tokens.</param>
    /// <returns>USD.</returns>
    public static decimal PriceTurn( ModelProfile profile, int promptTokens, int completionTokens )
    {
        var isLong = profile.LongPromptThresholdTokens is int threshold && promptTokens >= threshold;
        var inPrice = ( isLong ? profile.PriceInLongUsdPerM : profile.PriceInUsdPerM ) ?? 0m;
        var outPrice = ( isLong ? profile.PriceOutLongUsdPerM : profile.PriceOutUsdPerM ) ?? 0m;
        return ( promptTokens * inPrice + completionTokens * outPrice ) / 1_000_000m;
    }

    /// <summary>
    /// Real USD for an attempt with usage: the larger of OpenRouter's usage.cost and the cost priced from the
    /// reported tokens plus searches (whether usage.cost carries the search fee is not documented).
    /// </summary>
    /// <param name="profile">Paid seat.</param>
    /// <param name="extracted">Extracted response.</param>
    /// <returns>USD.</returns>
    public static decimal ActualUsd( ModelProfile profile, ExtractedResponse extracted )
    {
        var searches = profile.ModelId.StartsWith( "perplexity/", StringComparison.Ordinal )
            ? Math.Max( extracted.WebSearchRequests ?? 0, 1 )
            : extracted.WebSearchRequests ?? 0;

        var priced = PriceTurn( profile, extracted.PromptTokens ?? 0, extracted.CompletionTokens ?? 0 )
                     + searches * ( profile.SearchFeeUsd ?? 0 );

        return Math.Round( Math.Max( extracted.Cost ?? 0m, priced ), 8 );
    }

    /// <summary>Checks all three brakes and writes the reservation (the durable "sent" mark) before the send.</summary>
    /// <param name="profile">Paid seat.</param>
    /// <param name="questionId">Question being asked.</param>
    /// <param name="sweepId">Sweep it belongs to.</param>
    /// <param name="promptTokens">Estimated prompt tokens.</param>
    /// <param name="cap">Output cap about to be sent.</param>
    /// <param name="cancellationToken">Cancels the checks.</param>
    /// <returns>The reservation, or why the attempt must not be sent.</returns>
    public async Task<SpendReservation> ReserveAsync(
        ModelProfile profile, int questionId, long sweepId, int promptTokens, int cap, CancellationToken cancellationToken )
    {
        var worst = WorstCaseUsd( profile, promptTokens, cap );

        if( FileHalt() is string fileHalt )
        {
            return new SpendReservation( null, worst, $"paid lane halted: {fileHalt}", false );
        }

        var key = await ReadKeyAsync( profile.KeyFile, cancellationToken );

        if( key.Error is not null )
        {
            return new SpendReservation( null, worst, $"paid key status unreadable ({key.Error}); refusing to spend", false );
        }

        if( key.Limit is null || key.Remaining is null || key.LimitReset is not null || key.Limit > KEY_LIMIT_MAX_USD )
        {
            return new SpendReservation( null, worst,
                $"paid key must have a fixed OpenRouter limit of at most ${KEY_LIMIT_MAX_USD:0} (limit {key.Limit?.ToString( "0.00", CultureInfo.InvariantCulture ) ?? "none"}, reset {key.LimitReset ?? "none"}); refusing to spend", false );
        }

        if( key.Remaining < worst )
        {
            return new SpendReservation( null, worst, $"OpenRouter key limit: ${key.Remaining:0.0000} left, estimate ${worst:0.0000}", false );
        }

        var (balance, balanceError) = await ReadBalanceAsync( profile.KeyFile, cancellationToken );

        if( balanceError is not null )
        {
            return new SpendReservation( null, worst, $"account balance unreadable ({balanceError}); refusing to spend", false );
        }

        // Native search cannot be bounded per call, so the shared balance must absorb twice the estimate: any overrun
        // halts the lane, and this keeps even a 2x call from pushing the account (and the free models) negative.
        if( balance - 2 * worst < ACCOUNT_MARGIN_USD )
        {
            return new SpendReservation( null, worst, $"account balance ${balance:0.00} minus twice the estimate ${2 * worst:0.0000} would leave under ${ACCOUNT_MARGIN_USD:0.00}", false );
        }

        await using var connection = new SqlConnection( _connectionString );
        await connection.OpenAsync( cancellationToken );
        await using var command = new SqlCommand( "quorum.ReserveSpend", connection ) { CommandType = CommandType.StoredProcedure };
        command.Parameters.Add( "@CapName", SqlDbType.VarChar, 40 ).Value = CAP_NAME;
        command.Parameters.Add( "@SeatId", SqlDbType.VarChar, 200 ).Value = profile.SeatId;
        command.Parameters.Add( "@QuestionId", SqlDbType.Int ).Value = questionId;
        command.Parameters.Add( "@SweepId", SqlDbType.BigInt ).Value = sweepId;
        command.Parameters.Add( Money( "@ReserveUsd", worst ) );
        command.Parameters.Add( Money( "@ServerUsedUsd", key.Usage ?? 0m ) );
        var id = command.Parameters.Add( "@SpendId", SqlDbType.BigInt );
        id.Direction = ParameterDirection.Output;
        var spent = command.Parameters.Add( new SqlParameter( "@SpentUsd", SqlDbType.Decimal ) { Precision = 18, Scale = 8, Direction = ParameterDirection.Output } );
        var limit = command.Parameters.Add( new SqlParameter( "@LimitUsd", SqlDbType.Decimal ) { Precision = 10, Scale = 4, Direction = ParameterDirection.Output } );
        var duplicate = command.Parameters.Add( new SqlParameter( "@Duplicate", SqlDbType.Bit ) { Direction = ParameterDirection.Output } );
        var halted = command.Parameters.Add( new SqlParameter( "@HaltedReason", SqlDbType.NVarChar, 400 ) { Direction = ParameterDirection.Output } );
        command.Parameters.Add( new SqlParameter( "@HeldUsd", SqlDbType.Decimal ) { Precision = 18, Scale = 8, Direction = ParameterDirection.Output } );
        await command.ExecuteNonQueryAsync( cancellationToken );

        if( halted.Value is string reason )
        {
            // The proc raises this itself when the key's usage outruns the ledger, so mirror it to the file too.
            WriteFileHalt( reason );
            return new SpendReservation( null, worst, $"paid lane halted: {reason}", false );
        }

        if( duplicate.Value is true )
        {
            return new SpendReservation( null, worst, "already asked this question (a reservation or charge exists); never re-asked", true );
        }

        return id.Value is long spendId
            ? new SpendReservation( spendId, worst, null, false, key.Usage )
            : new SpendReservation( null, worst,
                $"dollar cap: ${Convert.ToDecimal( spent.Value ):0.0000} spent or held, estimate ${worst:0.0000}, cap ${Convert.ToDecimal( limit.Value ):0.00}", false );
    }

    /// <summary>
    /// A rejection OpenRouter documents as happening before any provider ran, so nothing could have billed: bad
    /// request, auth, gated, unmet pin or unknown model, and its own credit, key-limit or in-flight-budget 402 (which
    /// carries limit_source). A 429, 408, 5xx or unreadable body is never assumed free.
    /// </summary>
    /// <param name="attempt">The attempt.</param>
    /// <returns>True when the call provably did not run.</returns>
    public static bool IsPreRoutingRejection( CallAttempt attempt ) =>
        attempt.Extracted?.ErrorCode is not null && attempt.Extracted.Cost is null && attempt.Extracted.PromptTokens is null
        && attempt.HttpStatus switch
        {
            400 or 401 or 403 or 404 => true,
            402 => attempt.Extracted.ErrorMetadata?.Contains( "limit_source", StringComparison.OrdinalIgnoreCase ) == true,
            // MEASURED 2026-09-20 on the first paid run: OpenRouter answers an upstream rate limit with 429 and
            // metadata naming the limit_source ("upstream_provider_shared_pool"), which means a queue refused the
            // request rather than a model running it. The key's own usage confirmed that call billed nothing.
            // A 429 without that metadata is still treated as possibly billed.
            429 => attempt.Extracted.ErrorMetadata?.Contains( "limit_source", StringComparison.OrdinalIgnoreCase ) == true,
            // The docs give 404 in provider-selection and 503 in the error table for the same unmet pin.
            503 => IsUnmetPin( attempt.Extracted.ErrorMessage ),
            _ => false
        };

    /// <summary>The wording OpenRouter uses when no provider satisfies provider.only, so nothing was routed to.</summary>
    /// <param name="message">Error message.</param>
    /// <returns>True when the pin went unmet.</returns>
    public static bool IsUnmetPin( string? message ) =>
        message is not null
        && ( message.Contains( "No endpoints", StringComparison.OrdinalIgnoreCase )
             || message.Contains( "No allowed providers", StringComparison.OrdinalIgnoreCase )
             || message.Contains( "routing", StringComparison.OrdinalIgnoreCase ) );

    /// <summary>
    /// Settles a reservation for the record. It never re-opens the seat: any reservation marks the seat asked, except a
    /// provable pre-routing rejection, which is voided. Cost comes from usage and OpenRouter's generation record when
    /// there is one; otherwise the estimate stays held as unknown and the key's live usage carries the real bill.
    /// </summary>
    /// <param name="reservation">The reservation the attempt was sent under.</param>
    /// <param name="attempt">The attempt.</param>
    /// <returns>USD settled, or null when held as unknown or voided.</returns>
    public async Task<decimal?> SettleAsync( SpendReservation reservation, CallAttempt attempt )
    {
        var x = attempt.Extracted;
        var usage = x is not null && ( x.Cost is not null || x.PromptTokens is not null ) ? ActualUsd( attempt.Profile, x ) : (decimal?)null;
        var generation = x?.GenerationId is string gen ? await ReadGenerationCostAsync( attempt.Profile.KeyFile, gen ) : null;

        decimal? actual = null;
        var state = "unknown";
        string note;

        if( usage is not null || generation is not null )
        {
            actual = Math.Max( usage ?? 0m, generation ?? 0m );
            state = "settled";
            note = generation is null ? "from usage" : usage is null ? "from generation record" : "max of usage and generation record";
        }
        else if( IsPreRoutingRejection( attempt ) )
        {
            state = "voided";
            note = $"HTTP {attempt.HttpStatus} rejected before any provider ran; seat may be asked again";
        }
        else
        {
            note = $"{attempt.Status} (HTTP {attempt.HttpStatus?.ToString( CultureInfo.InvariantCulture ) ?? "none"}): no usage or generation record; estimate held, seat counts as asked";
        }

        await using var connection = new SqlConnection( _connectionString );
        await connection.OpenAsync( CancellationToken.None );
        await using var command = new SqlCommand( @"
UPDATE quorum.SpendLedger SET ActualUsd = @Actual, State = @State, Note = @Note, GenerationId = @Gen, SettledUtc = SYSUTCDATETIME()
 WHERE SpendId = @Id AND State = 'reserved';", connection );
        command.Parameters.Add( Money( "@Actual", actual ) );
        command.Parameters.Add( "@State", SqlDbType.VarChar, 12 ).Value = state;
        command.Parameters.Add( "@Note", SqlDbType.NVarChar, 400 ).Value = note;
        command.Parameters.Add( "@Gen", SqlDbType.VarChar, 100 ).Value = (object?)x?.GenerationId ?? DBNull.Value;
        command.Parameters.Add( "@Id", SqlDbType.BigInt ).Value = reservation.SpendId!.Value;
        await command.ExecuteNonQueryAsync( CancellationToken.None );
        return actual;
    }

    /// <summary>
    /// Stores a halt in both places it has to survive: the file beside the keys and quorum.SpendCap.HaltedReason.
    /// Every later reservation is refused until BOTH are cleared, which is what <see cref="ClearHaltAsync"/> does.
    /// </summary>
    /// <param name="reason">Why.</param>
    public async Task HaltAsync( string reason )
    {
        // The file goes first: a halt is usually raised because something already went wrong, and the database is
        // one of the things that can be wrong. A file halt on its own still refuses every later reservation.
        WriteFileHalt( reason );

        await using var connection = new SqlConnection( _connectionString );
        await connection.OpenAsync( CancellationToken.None );
        await using var command = new SqlCommand( @"
UPDATE quorum.SpendCap SET HaltedReason = @Reason, HaltedUtc = SYSUTCDATETIME() WHERE CapName = @Cap AND HaltedReason IS NULL;", connection );
        command.Parameters.Add( "@Reason", SqlDbType.NVarChar, 400 ).Value = reason.Length > 400 ? reason[..400] : reason;
        command.Parameters.Add( "@Cap", SqlDbType.VarChar, 40 ).Value = CAP_NAME;
        await command.ExecuteNonQueryAsync( CancellationToken.None );
    }

    /// <summary>Clears a halt: the file first, then the database, so a half-cleared halt still refuses.</summary>
    /// <returns>What was cleared, for the log.</returns>
    public async Task<string> ClearHaltAsync()
    {
        var had = FileHalt();

        try
        {
            var path = Path.Combine( _keysDirectory, HALT_FILE );

            if( File.Exists( path ) )
            {
                File.Delete( path );
            }
        }
        catch( Exception ex ) when( ex is IOException or UnauthorizedAccessException )
        {
            return $"could not delete {HALT_FILE} ({ex.GetType().Name}); the lane is still halted";
        }

        await using var connection = new SqlConnection( _connectionString );
        await connection.OpenAsync( CancellationToken.None );
        await using var command = new SqlCommand(
            "UPDATE quorum.SpendCap SET HaltedReason = NULL, HaltedUtc = NULL WHERE CapName = @Cap;", connection );
        command.Parameters.Add( "@Cap", SqlDbType.VarChar, 40 ).Value = CAP_NAME;
        var rows = await command.ExecuteNonQueryAsync( CancellationToken.None );
        return $"cleared: file halt {( had is null ? "none" : "removed" )}, {rows} cap row(s) reset";
    }

    /// <summary>
    /// Paid seats already asked this question: any reservation that was not voided, plus any stored call that either
    /// billed or cannot be proven not to have billed (a timeout or a network failure, where the upstream call may
    /// have finished anyway). These are never sent again from any entry point.
    ///
    /// The spend ledger is the authority. An answered call whose ledger row the owner voids IS asked again, which is
    /// the only way to replace a bad answer (2026-09-20: Gemini 3.1 pro wrote its thinking into the answer). The
    /// second branch exists for calls the ledger cannot price, not to overrule it.
    /// </summary>
    /// <param name="questionId">Question.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Seat ids.</returns>
    public async Task<HashSet<string>> AskedSeatsAsync( int questionId, CancellationToken cancellationToken )
    {
        const string SQL = @"
SELECT SeatId FROM quorum.SpendLedger
 WHERE CapName = @Cap AND QuestionId = @Q AND State <> 'voided'
UNION
SELECT sc.SeatId FROM quorum.SweepCall sc JOIN quorum.Sweep s ON s.SweepId = sc.SweepId
 WHERE s.QuestionId = @Q AND sc.Platform = @Paid AND sc.SeatId IS NOT NULL
   AND ( sc.CostUsd > 0 OR sc.Status IN ( 'Timeout', 'Network' ) );";

        var seats = new HashSet<string>( StringComparer.Ordinal );
        await using var connection = new SqlConnection( _connectionString );
        await connection.OpenAsync( cancellationToken );
        await using var command = new SqlCommand( SQL, connection );
        command.Parameters.Add( "@Cap", SqlDbType.VarChar, 40 ).Value = CAP_NAME;
        command.Parameters.Add( "@Q", SqlDbType.Int ).Value = questionId;
        command.Parameters.Add( "@Paid", SqlDbType.VarChar, 40 ).Value = ModelProfile.PAID_PROVIDER;
        await using var reader = await command.ExecuteReaderAsync( cancellationToken );

        while( await reader.ReadAsync( cancellationToken ) )
        {
            seats.Add( reader.GetString( 0 ) );
        }

        return seats;
    }

    /// <summary>
    /// The lane's local spend, counted exactly as quorum.ReserveSpend counts it (without the key's live usage):
    /// settled rows at their cost, reservations in flight at their estimate, and 'unknown' rows at nothing, since
    /// the key's own usage is what carries those and holding their estimate here used to drop later seats for
    /// money that was never spent.
    /// </summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The status.</returns>
    public async Task<PaidLaneStatus> StatusAsync( CancellationToken cancellationToken )
    {
        await using var connection = new SqlConnection( _connectionString );
        await connection.OpenAsync( cancellationToken );
        await using var command = new SqlCommand( @"
SELECT ISNULL( ( SELECT SUM( CASE WHEN State = 'settled' THEN ActualUsd
                                  WHEN State = 'reserved' AND CreatedUtc > DATEADD( HOUR, -2, SYSUTCDATETIME() ) THEN ReservedUsd
                                  ELSE 0 END )
                   FROM quorum.SpendLedger WHERE CapName = @Cap ), 0 ),
       c.LimitUsd, c.HaltedReason
  FROM quorum.SpendCap c WHERE c.CapName = @Cap;", connection );
        command.Parameters.Add( "@Cap", SqlDbType.VarChar, 40 ).Value = CAP_NAME;
        await using var reader = await command.ExecuteReaderAsync( cancellationToken );

        return await reader.ReadAsync( cancellationToken )
            ? new PaidLaneStatus( reader.GetDecimal( 0 ), reader.GetDecimal( 1 ),
                                  ( reader.IsDBNull( 2 ) ? null : reader.GetString( 2 ) ) ?? FileHalt() )
            : new PaidLaneStatus( 0m, 0m, "no spend cap row" );
    }

    /// <summary>True when the paid key file exists; without it nothing can be sent.</summary>
    /// <returns>Whether the key file exists.</returns>
    public bool PaidKeyPresent() => File.Exists( Path.Combine( _keysDirectory, ModelProfile.PAID_KEY_FILE ) );

    /// <summary>
    /// The paid key as the plan runner needs it before starting an item: what is left of its fixed limit, its
    /// lifetime usage, and a reason when the key is unreadable or not configured the way the lane requires.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>(Remaining, Usage, Error).</returns>
    public async Task<(decimal? Remaining, decimal? Usage, string? Error)> ReadPaidKeyAsync( CancellationToken cancellationToken )
    {
        var key = await ReadKeyAsync( ModelProfile.PAID_KEY_FILE, cancellationToken );

        if( key.Error is not null )
        {
            return (null, null, key.Error);
        }

        // Caught here rather than seat by seat: a key with no limit, or one that resets, is a configuration the
        // lane must wait on, not something to burn a plan item over.
        return key.Limit is null || key.Remaining is null || key.LimitReset is not null || key.Limit > KEY_LIMIT_MAX_USD
            ? (null, key.Usage, $"the key needs a fixed limit of at most ${KEY_LIMIT_MAX_USD:0} with no reset (limit {key.Limit?.ToString( "0.00", CultureInfo.InvariantCulture ) ?? "none"}, reset {key.LimitReset ?? "none"})")
            : (key.Remaining, key.Usage, null);
    }

    /// <summary>The account balance, for the plan runner's start check.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>(Balance, Error).</returns>
    public async Task<(decimal Balance, string? Error)> ReadAccountBalanceAsync( CancellationToken cancellationToken ) =>
        await ReadBalanceAsync( ModelProfile.PAID_KEY_FILE, cancellationToken );

    #endregion Public Methods

    #region Private Methods

    /// <summary>The stored file halt, if one was written.</summary>
    private string? FileHalt()
    {
        try
        {
            var path = Path.Combine( _keysDirectory, HALT_FILE );
            return File.Exists( path ) ? File.ReadAllText( path ).Trim() : null;
        }
        catch( Exception ex ) when( ex is IOException or UnauthorizedAccessException )
        {
            // A halt file that cannot be read is still a halt.
            return "halt file unreadable";
        }
    }

    /// <summary>Writes the halt file, keeping the first reason.</summary>
    private void WriteFileHalt( string reason )
    {
        try
        {
            var path = Path.Combine( _keysDirectory, HALT_FILE );

            if( !File.Exists( path ) )
            {
                File.WriteAllText( path, reason );
            }
        }
        catch( Exception ex ) when( ex is IOException or UnauthorizedAccessException )
        {
            // Nothing more to do: the database halt and the run-scoped stop still apply.
        }
    }

    /// <summary>The web seat's own stop_server_tools_when max_cost, or 0 when none is set.</summary>
    private static decimal MaxCostStop( ModelProfile profile )
    {
        if( profile.ExtraBody?["stop_server_tools_when"] is not JsonArray stops )
        {
            return 0m;
        }

        foreach( var stop in stops )
        {
            // Read through the JSON text: a node parsed from profiles.json and one built in code hold different CLR types.
            if( stop?["type"]?.GetValue<string>() == "max_cost" && stop["max_cost_in_dollars"] is JsonValue value &&
                decimal.TryParse( value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dollars ) )
            {
                return dollars;
            }
        }

        return 0m;
    }

    private static SqlParameter Money( string name, decimal? value ) =>
        new( name, SqlDbType.Decimal ) { Precision = 18, Scale = 8, Value = (object?)value ?? DBNull.Value };

    private string ReadKey( string keyFile ) => File.ReadAllText( Path.Combine( _keysDirectory, keyFile ) ).Trim();

    /// <summary>GET /api/v1/key: limit, limit_remaining, limit_reset and lifetime usage.</summary>
    private async Task<(decimal? Limit, decimal? Remaining, string? LimitReset, decimal? Usage, string? Error)> ReadKeyAsync(
        string keyFile, CancellationToken cancellationToken )
    {
        try
        {
            using var document = await GetJsonAsync( KEY_URL, ReadKey( keyFile ), cancellationToken );

            if( document is null )
            {
                return (null, null, null, null, "HTTP error");
            }

            var data = document.RootElement.GetProperty( "data" );
            var reset = data.TryGetProperty( "limit_reset", out var r ) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            return (Number( data, "limit" ), Number( data, "limit_remaining" ), reset, Number( data, "usage" ), null);
        }
        catch( Exception ex ) when( IsReadFailure( ex ) )
        {
            return (null, null, null, null, ex.GetType().Name);
        }
    }

    /// <summary>
    /// GET /api/v1/credits: account balance (total_credits - total_usage). Tried with the paid key, then the free key;
    /// both are ordinary keys on the same account, and the route answered an ordinary key live on 2026-09-18.
    /// </summary>
    private async Task<(decimal Balance, string? Error)> ReadBalanceAsync( string keyFile, CancellationToken cancellationToken )
    {
        string? error = null;

        foreach( var file in new[] { keyFile, FREE_KEY_FILE } )
        {
            try
            {
                using var document = await GetJsonAsync( CREDITS_URL, ReadKey( file ), cancellationToken );

                if( document is null )
                {
                    error = "HTTP error";
                    continue;
                }

                var data = document.RootElement.GetProperty( "data" );
                var credits = Number( data, "total_credits" );
                var usage = Number( data, "total_usage" );

                if( credits is not null && usage is not null )
                {
                    return (credits.Value - usage.Value, null);
                }

                error = "missing totals";
            }
            catch( Exception ex ) when( IsReadFailure( ex ) )
            {
                error = ex.GetType().Name;
            }
        }

        return (0m, error);
    }

    /// <summary>GET /api/v1/generation?id=: total_cost once the record exists, retried briefly; null if never found.</summary>
    private async Task<decimal?> ReadGenerationCostAsync( string keyFile, string generationId )
    {
        foreach( var waitMs in _settleWaitsMs )
        {
            await Task.Delay( waitMs );

            try
            {
                using var document = await GetJsonAsync( GENERATION_URL + Uri.EscapeDataString( generationId ), ReadKey( keyFile ), CancellationToken.None );

                if( document is not null && document.RootElement.TryGetProperty( "data", out var data ) && Number( data, "total_cost" ) is decimal cost )
                {
                    return cost;
                }
            }
            catch( Exception ex ) when( IsReadFailure( ex ) )
            {
                // Not there yet, or a blip: try again after the next wait.
            }
        }

        return null;
    }

    private static bool IsReadFailure( Exception ex ) =>
        ex is HttpRequestException or TaskCanceledException or JsonException or IOException or KeyNotFoundException or InvalidOperationException
            or UnauthorizedAccessException;

    private static async Task<JsonDocument?> GetJsonAsync( string url, string key, CancellationToken cancellationToken )
    {
        using var request = new HttpRequestMessage( HttpMethod.Get, url );
        request.Headers.TryAddWithoutValidation( "Authorization", $"Bearer {key}" );
        using var response = await _http.SendAsync( request, cancellationToken );
        return response.IsSuccessStatusCode ? JsonDocument.Parse( await response.Content.ReadAsByteArrayAsync( cancellationToken ) ) : null;
    }

    private static decimal? Number( JsonElement element, string name ) =>
        element.TryGetProperty( name, out var value ) && value.ValueKind == JsonValueKind.Number ? value.GetDecimal() : null;

    #endregion Private Methods
}
