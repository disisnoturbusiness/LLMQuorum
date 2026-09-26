using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMQuorum.Core.Models;
using LLMQuorum.Core.Providers;

namespace LLMQuorum.Core.Sweep;

/// <summary>One HTTP attempt against one seat, recorded whether it succeeded or not.</summary>
public sealed class CallAttempt
{
    /// <summary>Seat that was called.</summary>
    public required ModelProfile Profile { get; init; }

    /// <summary>1 for the first attempt, higher for retries.</summary>
    public int AttemptNo { get; init; }

    /// <summary>Reasoning mode actually sent. Differs from the profile after a reasoning fallback.</summary>
    public required string ReasoningModeUsed { get; init; }

    /// <summary>Exact request body sent. Never contains a credential.</summary>
    public byte[]? RequestBytes { get; init; }

    /// <summary>Output cap sent.</summary>
    public int? MaxTokensSent { get; init; }

    /// <summary>HTTP status, when a response arrived.</summary>
    public int? HttpStatus { get; init; }

    /// <summary>Selected rate-limit and metering headers.</summary>
    public Dictionary<string, string> Headers { get; init; } = new( StringComparer.OrdinalIgnoreCase );

    /// <summary>Exact response bytes.</summary>
    public byte[]? ResponseBytes { get; init; }

    /// <summary>Round-trip time.</summary>
    public int LatencyMs { get; init; }

    /// <summary>Extractor output, when a body was received.</summary>
    public ExtractedResponse? Extracted { get; init; }

    /// <summary>Final status for this attempt: extractor status, or BudgetSkipped, ProviderStopped, Blocked, Timeout, Network.</summary>
    public required string Status { get; init; }

    /// <summary>Retry decision or skip reason.</summary>
    public string? Decision { get; init; }

    /// <summary>Cloudflare neurons charged, from the header or priced from usage.</summary>
    public decimal? Neurons { get; init; }

    /// <summary>Paid seats: USD settled against the dollar cap for this attempt (null when held as unknown).</summary>
    public decimal? CostUsd { get; init; }
}

/// <summary>Which question and sweep a call belongs to. Paid seats must have one: it is written before the send.</summary>
/// <param name="QuestionId">Question being asked.</param>
/// <param name="SweepId">Sweep the attempt is persisted under.</param>
public sealed record SweepContext( int QuestionId, long SweepId );

/// <summary>
/// Run-scoped provider state so a spent or unsafe provider stops being called. Provider groups run in parallel in one
/// sweep, so both collections are concurrent.
/// </summary>
public sealed class ProviderRunState
{
    /// <summary>Providers stopped for the rest of the run, with the reason.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, string> Stopped { get; } = new( StringComparer.OrdinalIgnoreCase );

    /// <summary>Seats blocked for the rest of the run (value unused).</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, bool> BlockedModels { get; } = new( StringComparer.OrdinalIgnoreCase );

    /// <summary>Blocks a seat for the rest of the run.</summary>
    /// <param name="seatId">Seat.</param>
    public void Block( string seatId ) => BlockedModels.TryAdd( seatId, true );

    /// <summary>True when the seat was blocked earlier in the run.</summary>
    /// <param name="seatId">Seat.</param>
    /// <returns>Whether it is blocked.</returns>
    public bool IsBlocked( string seatId ) => BlockedModels.ContainsKey( seatId );
}

/// <summary>
/// Calls a seat exactly as its profile says, applies the quota gate before sending, retries
/// only when the error classifier says a retry can succeed, and returns every attempt so each
/// one is persisted.
/// </summary>
public sealed class ProfileCaller
{
    #region Data Members

    /// <summary>Most attempts per seat: the original plus two retries.</summary>
    private const int MAX_ATTEMPTS = 3;

    /// <summary>Headroom left in the context window for system prompts providers inject.</summary>
    private const int CONTEXT_MARGIN = 256;

    /// <summary>Extra sends allowed for a paid rejection that provably billed nothing (OpenRouter's own 429 or 503).</summary>
    private const int UNBILLED_RETRIES = 2;

    /// <summary>Wait between those sends: the upstream pool needs a moment, not a millisecond.</summary>
    private const int UNBILLED_RETRY_WAIT_SECONDS = 45;

    /// <summary>Headers worth keeping: rate limits, retry hints, metering, and the Cohere trial fingerprint.</summary>
    private static readonly string[] _keptHeaders =
    {
        "retry-after", "x-should-retry", "cf-ai-neurons", "x-trial-endpoint-call-limit", "x-endpoint-monthly-call-limit",
        "x-ratelimit-limit-requests", "x-ratelimit-remaining-requests", "x-ratelimit-limit-tokens", "x-ratelimit-remaining-tokens",
        "x-ratelimit-limit", "x-ratelimit-remaining", "x-ratelimit-reset"
    };

    private static readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly string _keysDirectory;
    private readonly string _configDirectory;
    private readonly QuotaLedger _ledger;
    private readonly SpendGate _spendGate;

    #endregion Data Members

    #region Constructor

    /// <summary>Builds a caller.</summary>
    /// <param name="keysDirectory">Directory holding provider key files.</param>
    /// <param name="configDirectory">Directory relative paths (Claude sandbox) resolve against.</param>
    /// <param name="ledger">Quota ledger that gates and records calls.</param>
    public ProfileCaller( string keysDirectory, string configDirectory, QuotaLedger ledger )
    {
        _keysDirectory = keysDirectory;
        _configDirectory = configDirectory;
        _ledger = ledger;
        _spendGate = new SpendGate( ledger.ConnectionString, keysDirectory );
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>Calls one seat, returning every attempt made (at least one record, even when skipped).</summary>
    /// <param name="profile">Seat to call.</param>
    /// <param name="prompt">Question text, identical for every seat.</param>
    /// <param name="state">Run-scoped provider state.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <param name="context">Question and sweep; required for paid seats.</param>
    /// <returns>Attempts in order.</returns>
    public async Task<List<CallAttempt>> CallAsync(
        ModelProfile profile, string prompt, ProviderRunState state, CancellationToken cancellationToken, SweepContext? context = null )
    {
        if( profile.Paid && context is null )
        {
            return new List<CallAttempt> { Skip( profile, "BudgetSkipped", "paid seats run only inside a sweep with a question context" ) };
        }

        if( state.Stopped.TryGetValue( profile.Provider, out var why ) )
        {
            return new List<CallAttempt> { Skip( profile, "ProviderStopped", why ) };
        }

        if( state.IsBlocked( profile.SeatId ) )
        {
            return new List<CallAttempt> { Skip( profile, "Blocked", "blocked earlier in run" ) };
        }

        return profile.Shape == ApiShape.ClaudeCli
            ? new List<CallAttempt> { await CallClaudeCliAsync( profile, prompt, cancellationToken ) }
            : await CallHttpAsync( profile, prompt, state, context, cancellationToken );
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>HTTP path with gate, retries and post-call free-tier fingerprints.</summary>
    private async Task<List<CallAttempt>> CallHttpAsync(
        ModelProfile profile, string prompt, ProviderRunState state, SweepContext? context, CancellationToken cancellationToken )
    {
        var promptEstimate = (int)Math.Ceiling( Encoding.UTF8.GetByteCount( prompt ) / 3.0 ) + 16;
        var cap = Math.Min( profile.MaxOutput, profile.ContextWindow - promptEstimate - CONTEXT_MARGIN );

        if( profile.Paid )
        {
            return new List<CallAttempt> { await CallPaidAsync( profile, prompt, state, context!, promptEstimate, cap, cancellationToken ) };
        }

        var attempts = new List<CallAttempt>();
        var sendReasoning = true;

        for( var attemptNo = 1; attemptNo <= MAX_ATTEMPTS; attemptNo++ )
        {
            var (gatedCap, skip) = await _ledger.GateAsync( profile, promptEstimate, cap, cancellationToken );

            if( gatedCap is null )
            {
                attempts.Add( Skip( profile, "BudgetSkipped", skip ) );
                return attempts;
            }

            var attempt = await SendAsync( profile, prompt, gatedCap.Value, sendReasoning, attemptNo, cancellationToken );
            await _ledger.RecordAttemptAsync( profile, attempt.Neurons, cancellationToken );

            var fingerprint = CheckFreeTierFingerprint( profile, attempt );

            if( fingerprint is not null )
            {
                state.Stopped[profile.Provider] = fingerprint;
                attempts.Add( With( attempt, attempt.Status, fingerprint ) );
                return attempts;
            }

            if( attempt.Status != "Error" )
            {
                attempts.Add( attempt );
                return attempts;
            }

            var decision = ErrorClassifier.Decide( profile.Provider, attempt.HttpStatus ?? 0, attempt.Headers, attempt.Extracted! );
            attempts.Add( With( attempt, "Error", $"{decision.Action}: {decision.Reason}" ) );

            var retry = await ApplyDecisionAsync( profile, decision, state, cancellationToken );

            if( !retry.Retry )
            {
                return attempts;
            }

            cap = retry.NewCap ?? cap;
            sendReasoning = sendReasoning && !retry.DropReasoning;
        }

        return attempts;
    }

    /// <summary>
    /// Paid path: one send, because any send may bill. The reservation written before the send marks the seat asked for
    /// good. A cost over twice the estimate, a response from anyone but the pinned provider, or a ledger failure halts
    /// the paid lane (stored when the database allows it, and for this run in any case). A timeout or network failure
    /// stops the lane for this run, since that call may still be running and billing upstream.
    ///
    /// The single exception to "one send": a rejection OpenRouter names as its own (a 429 or 402 carrying limit_source,
    /// an unmet pin) is voided, having provably billed nothing, and may be sent again after a wait. MEASURED 2026-09-20
    /// on the first paid run: OpenRouter's shared Google pool answered every grounded Gemini call with such a 429, and
    /// the key's own meter showed $0.0000 for all five.
    /// </summary>
    private async Task<CallAttempt> CallPaidAsync(
        ModelProfile profile, string prompt, ProviderRunState state, SweepContext context, int promptEstimate, int cap,
        CancellationToken cancellationToken )
    {
        for( var tryNo = 1; ; tryNo++ )
        {
            var result = await SendPaidOnceAsync( profile, prompt, state, context, promptEstimate, cap, tryNo, cancellationToken );

            if( !result.MayRetry || tryNo > UNBILLED_RETRIES )
            {
                return result.Attempt;
            }

            await Task.Delay( TimeSpan.FromSeconds( UNBILLED_RETRY_WAIT_SECONDS ), cancellationToken );
        }
    }

    /// <summary>One paid send with its reservation, settle and outcome. MayRetry is set only for an unbilled rejection.</summary>
    private async Task<(CallAttempt Attempt, bool MayRetry)> SendPaidOnceAsync(
        ModelProfile profile, string prompt, ProviderRunState state, SweepContext context, int promptEstimate, int cap,
        int tryNo, CancellationToken cancellationToken )
    {
        var (gatedCap, skip) = await _ledger.GateAsync( profile, promptEstimate, cap, cancellationToken );

        if( gatedCap is null )
        {
            return (Skip( profile, "BudgetSkipped", skip ), false);
        }

        var reservation = await _spendGate.ReserveAsync( profile, context.QuestionId, context.SweepId, promptEstimate, gatedCap.Value, cancellationToken );

        if( reservation.SpendId is null )
        {
            return (Skip( profile, reservation.AlreadyAsked ? "AlreadyAsked" : "BudgetSkipped", reservation.Skip ), false);
        }

        // From the send on, nothing honours cancellation: the call may bill, so it always runs to the end and is recorded.
        var attempt = await SendAsync( profile, prompt, gatedCap.Value, true, tryNo, CancellationToken.None );
        var notes = new List<string>();
        var x = attempt.Extracted;
        decimal? known = x is not null && ( x.Cost is not null || x.PromptTokens is not null ) ? SpendGate.ActualUsd( profile, x ) : null;
        // Native search cannot be bounded inside a call, so a call that costs somewhat more than its estimate is
        // expected and only recorded. Twice the estimate means the estimate model is broken, and it is also the
        // figure the balance margin is sized for, so that halts the lane.
        var halt = known > reservation.ReservedUsd * 2
            ? $"cost ${known:0.0000}, more than twice the estimate ${reservation.ReservedUsd:0.0000}"
            : CheckPaidPin( profile, attempt );

        // Halt before settling: settling waits on OpenRouter and the database, and the halt must not depend on either.
        if( halt is not null )
        {
            await HaltPaidAsync( profile, state, halt, notes );
        }

        var cost = known;

        try
        {
            cost = await _spendGate.SettleAsync( reservation, attempt ) ?? known;
        }
        catch( Exception ex )
        {
            // Any failure here is a ledger failure; the row stays 'reserved', which still counts the seat as asked.
            await HaltPaidAsync( profile, state, $"spend ledger settle failed ({ex.GetType().Name})", notes );
        }

        if( halt is null && cost > reservation.ReservedUsd * 2 )
        {
            await HaltPaidAsync( profile, state, $"billed ${cost:0.0000}, more than twice the estimate ${reservation.ReservedUsd:0.0000}", notes );
        }
        else if( halt is null && cost > reservation.ReservedUsd )
        {
            notes.Add( $"cost ${cost:0.0000} above the estimate ${reservation.ReservedUsd:0.0000}" );
        }

        // Voided means OpenRouter turned the request away before a provider ran, so nothing billed and the seat is
        // still unasked. Worth one more try after a wait; the lane is not stopped and no seat is blocked yet.
        var unbilled = SpendGate.IsPreRoutingRejection( attempt ) && attempt.HttpStatus is 429 or 503;

        if( unbilled && tryNo <= UNBILLED_RETRIES )
        {
            notes.Add( $"{attempt.HttpStatus} not billed; waiting {UNBILLED_RETRY_WAIT_SECONDS}s and asking again" );
            return (With( attempt, attempt.Status, string.Join( "; ", notes ) ), true);
        }

        await RecordPaidOutcomeAsync( profile, attempt, state, notes );

        // A failure that reported no usage and no generation record is money we cannot count yet: the cap only
        // learns about it once OpenRouter's own key usage moves. One is tolerable; a run of them would spend past
        // the cap while every check still reads the old number. So the lane stops here for this run.
        if( cost is null && attempt.Status != "Answered" && !SpendGate.IsPreRoutingRejection( attempt )
            && !state.Stopped.ContainsKey( profile.Provider ) )
        {
            state.Stopped[profile.Provider] = $"{profile.SeatId}: {attempt.Status} with no usage reported; paid seats stopped for this run until the key's own usage settles";
            notes.Add( "no usage reported; paid seats stopped for this run" );
        }
        return (With( WithCost( attempt, cost ), attempt.Status, notes.Count == 0 ? attempt.Decision : string.Join( "; ", notes ) ), false);
    }

    /// <summary>Notes a paid attempt's non-money outcome: timeout stop, error decision without retry, unverified pin.</summary>
    private async Task RecordPaidOutcomeAsync( ModelProfile profile, CallAttempt attempt, ProviderRunState state, List<string> notes )
    {
        try
        {
            await _ledger.RecordAttemptAsync( profile, attempt.Neurons, CancellationToken.None );
        }
        catch( Exception ex )
        {
            notes.Add( $"attempt count not recorded ({ex.GetType().Name})" );
        }

        if( attempt.Status is "Timeout" or "Network" )
        {
            state.Stopped[profile.Provider] = $"{profile.SeatId} {attempt.Status}: that call may still bill; paid seats stopped for this run";
            notes.Add( $"{attempt.Decision}; paid seats stopped for this run" );
            return;
        }

        if( attempt.Status == "Error" )
        {
            var decision = ErrorClassifier.Decide( profile.Provider, attempt.HttpStatus ?? 0, attempt.Headers, attempt.Extracted! );
            notes.Insert( 0, $"{decision.Action}: {decision.Reason}" );

            if( decision.Action is ErrorAction.StopProvider or ErrorAction.StopProviderForDay )
            {
                state.Stopped[profile.Provider] = decision.Reason;
            }
            else if( decision.Action == ErrorAction.BlockModel )
            {
                state.Block( profile.SeatId );
            }

            return;
        }

        // The request pins the provider with fallbacks off; a body that names no provider is noted, not passed silently.
        if( attempt.HttpStatus is < 400 && attempt.Extracted?.ServedProvider is null )
        {
            notes.Add( "pin unverified: response named no provider" );
        }
    }

    /// <summary>Halts the paid lane for this run, and stores the halt so later runs refuse too until the owner clears it.</summary>
    private async Task HaltPaidAsync( ModelProfile profile, ProviderRunState state, string reason, List<string> notes )
    {
        state.Stopped[profile.Provider] = reason;
        notes.Add( $"{reason}; paid seats halted" );

        try
        {
            await _spendGate.HaltAsync( $"{profile.SeatId}: {reason}" );
        }
        catch( Exception ex )
        {
            notes.Add( $"halt not stored ({ex.GetType().Name}); stopped for this run only" );
        }
    }

    /// <summary>Carries out a retry decision. Returns whether to retry and with what changes.</summary>
    private static async Task<(bool Retry, int? NewCap, bool DropReasoning)> ApplyDecisionAsync(
        ModelProfile profile, ErrorDecision decision, ProviderRunState state, CancellationToken cancellationToken )
    {
        switch( decision.Action )
        {
            case ErrorAction.RetryAfterWait:
                await Task.Delay( TimeSpan.FromSeconds( decision.WaitSeconds ), cancellationToken );
                return (true, null, false);
            case ErrorAction.RetryWithCap:
                return (true, decision.NewCap, false);
            case ErrorAction.RetryWithoutReasoning:
                return profile.ReasoningBody is null ? (false, null, false) : (true, null, true);
            case ErrorAction.StopProviderForDay:
            case ErrorAction.StopProvider:
                state.Stopped[profile.Provider] = decision.Reason;
                return (false, null, false);
            case ErrorAction.BlockModel:
                state.Block( profile.SeatId );
                return (false, null, false);
            default:
                return (false, null, false);
        }
    }

    /// <summary>Builds the body from the profile, sends it and extracts the response.</summary>
    private async Task<CallAttempt> SendAsync(
        ModelProfile profile, string prompt, int cap, bool sendReasoning, int attemptNo, CancellationToken cancellationToken )
    {
        var body = BuildBody( profile, prompt, cap, sendReasoning );
        var requestBytes = Encoding.UTF8.GetBytes( body.ToJsonString() );
        var modeUsed = sendReasoning || profile.ReasoningBody is null ? profile.ReasoningMode : "default-fallback";
        var stopwatch = Stopwatch.StartNew();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
        timeout.CancelAfter( TimeSpan.FromSeconds( profile.TimeoutSeconds ) );

        try
        {
            using var request = new HttpRequestMessage( HttpMethod.Post, profile.Endpoint )
            {
                Content = new ByteArrayContent( requestBytes )
            };

            request.Content.Headers.TryAddWithoutValidation( "Content-Type", "application/json" );
            request.Headers.TryAddWithoutValidation( "Authorization", $"Bearer {ReadKey( profile )}" );
            request.Headers.TryAddWithoutValidation( "User-Agent", "LLMQuorum/1.1" );

            using var response = await _http.SendAsync( request, timeout.Token );
            var bytes = await response.Content.ReadAsByteArrayAsync( timeout.Token );
            stopwatch.Stop();

            return BuildAttempt( profile, attemptNo, modeUsed, requestBytes, cap, response, bytes, (int)stopwatch.ElapsedMilliseconds );
        }
        catch( OperationCanceledException ) when( !cancellationToken.IsCancellationRequested )
        {
            return Failure( profile, attemptNo, modeUsed, requestBytes, cap, "Timeout", $"no response in {profile.TimeoutSeconds}s", stopwatch );
        }
        catch( HttpRequestException ex )
        {
            return Failure( profile, attemptNo, modeUsed, requestBytes, cap, "Network", ex.Message, stopwatch );
        }
    }

    /// <summary>
    /// Assembles the request. Exactly one token parameter is written, the reasoning fragment is
    /// merged explicitly (because "omitted" means off on one host and maximum effort on another),
    /// and temperature 0 is sent unless the profile opts out.
    /// </summary>
    private static JsonObject BuildBody( ModelProfile profile, string prompt, int cap, bool sendReasoning )
    {
        var body = new JsonObject
        {
            ["model"] = profile.ModelId,
            ["messages"] = new JsonArray( new JsonObject { ["role"] = "user", ["content"] = prompt } ),
            [profile.TokenParam] = cap
        };

        if( profile.SendTemperature )
        {
            body["temperature"] = 0;
        }

        if( sendReasoning && profile.ReasoningBody is not null )
        {
            foreach( var (key, value) in profile.ReasoningBody )
            {
                body[key] = value?.DeepClone();
            }
        }

        // Pins and tools are merged on every attempt, including a reasoning fallback, so a retry can never go unpinned.
        if( profile.ExtraBody is not null )
        {
            foreach( var (key, value) in profile.ExtraBody )
            {
                body[key] = value?.DeepClone();
            }
        }

        return body;
    }

    /// <summary>Turns a response into an attempt record, pricing Cloudflare neurons from the header or usage.</summary>
    private static CallAttempt BuildAttempt(
        ModelProfile profile, int attemptNo, string modeUsed, byte[] requestBytes, int cap,
        HttpResponseMessage response, byte[] bytes, int latencyMs )
    {
        var headers = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );

        foreach( var name in _keptHeaders )
        {
            if( response.Headers.TryGetValues( name, out var values ) || response.Content.Headers.TryGetValues( name, out values ) )
            {
                headers[name] = string.Join( ",", values );
            }
        }

        var extracted = ResponseExtractor.Extract( profile, bytes, cap );
        var status = response.StatusCode == HttpStatusCode.OK || extracted.Status != "Error" ? extracted.Status : "Error";

        if( !response.IsSuccessStatusCode && extracted.Status != "Error" )
        {
            extracted = extracted with { Status = "Error", ErrorCode = ((int)response.StatusCode).ToString() };
            status = "Error";
        }

        decimal? neurons = null;

        if( profile.Provider == "cloudflare" )
        {
            neurons = headers.TryGetValue( "cf-ai-neurons", out var h ) && decimal.TryParse( h, System.Globalization.NumberStyles.Float,
                          System.Globalization.CultureInfo.InvariantCulture, out var parsed )
                ? parsed
                : QuotaLedger.PriceNeurons( profile, extracted.PromptTokens ?? 0, extracted.CompletionTokens ?? 0 );
        }

        return new CallAttempt
        {
            Profile = profile, AttemptNo = attemptNo, ReasoningModeUsed = modeUsed, RequestBytes = requestBytes,
            MaxTokensSent = cap, HttpStatus = (int)response.StatusCode, Headers = headers, ResponseBytes = bytes,
            LatencyMs = latencyMs, Extracted = extracted, Status = status, Neurons = neurons
        };
    }

    /// <summary>
    /// Stops a provider whose response does not look like a free tier. OpenRouter must report
    /// zero cost; Cohere trial responses always carry x-trial-endpoint-call-limit. A mismatch
    /// means the next call could bill.
    /// </summary>
    private static string? CheckFreeTierFingerprint( ModelProfile profile, CallAttempt attempt )
    {
        if( attempt.HttpStatus is null or >= 400 )
        {
            return null;
        }

        if( profile.Provider == "openrouter" && attempt.Extracted?.Cost is decimal cost && cost != 0 )
        {
            return $"OpenRouter reported cost {cost}; halting provider";
        }

        return profile.Provider == "cohere" && !attempt.Headers.ContainsKey( "x-trial-endpoint-call-limit" )
            ? "Cohere response lacks the trial-key header; halting provider"
            : null;
    }

    /// <summary>
    /// Stops the paid lane when a paid call was served by anyone but the pinned provider. The call has already billed;
    /// stopping keeps a routing change from billing the rest of the run at another host's price.
    /// </summary>
    private static string? CheckPaidPin( ModelProfile profile, CallAttempt attempt )
    {
        if( attempt.HttpStatus is null or >= 400 || attempt.Extracted?.ServedProvider is not string served )
        {
            return null;
        }

        return string.Equals( served, profile.PinnedProvider, StringComparison.OrdinalIgnoreCase )
            ? null
            : $"served by {served}, pinned to {profile.PinnedProvider}; halting paid seats";
    }

    /// <summary>Claude subscription seat through the CLI.</summary>
    private async Task<CallAttempt> CallClaudeCliAsync( ModelProfile profile, string prompt, CancellationToken cancellationToken )
    {
        var definition = new ProviderDefinition
        {
            Key = profile.Provider, DisplayName = profile.ModelId, ModelId = profile.ModelId, Lab = "Anthropic",
            Endpoint = string.IsNullOrWhiteSpace( profile.Endpoint ) ? "auto" : profile.Endpoint, ApiKeyFile = string.Empty,
            Protocol = ProviderProtocol.ClaudeCli, TimeoutSeconds = profile.TimeoutSeconds,
            CliTools = profile.CliTools, CliMaxTurns = profile.CliMaxTurns
        };

        var answer = await new ClaudeCliProvider( definition, _configDirectory ).AskAsync( prompt, 1, cancellationToken );
        var normalized = ResponseExtractor.Normalize( answer.AnswerText );

        var status = answer.IsSuccess
            ? ( normalized.Length == 0 ? "Empty" : "Answered" )
            : answer.ErrorClass switch
            {
                CallErrorClass.Timeout => "Timeout",
                CallErrorClass.Truncated => "Truncated",
                _ => "Error"
            };

        var extracted = new ExtractedResponse( status, answer.AnswerText, null, normalized.Length > 0 ? normalized : null,
            answer.FinishReason, answer.PromptTokens, answer.CompletionTokens, null, false,
            status == "Answered" && ResponseExtractor.IsRefusal( normalized ),
            answer.IsSuccess ? null : answer.ErrorClass.ToString(), answer.ErrorText, null, null );

        return new CallAttempt
        {
            Profile = profile, AttemptNo = 1, ReasoningModeUsed = profile.ReasoningMode, ResponseBytes = answer.ResponseBytes,
            LatencyMs = answer.LatencyMs, Extracted = extracted, Status = status
        };
    }

    private string ReadKey( ModelProfile profile ) =>
        File.ReadAllText( Path.Combine( _keysDirectory, profile.KeyFile ) ).Trim();

    private static CallAttempt Skip( ModelProfile profile, string status, string? reason ) =>
        new() { Profile = profile, AttemptNo = 0, ReasoningModeUsed = profile.ReasoningMode, Status = status, Decision = reason };

    private static CallAttempt With( CallAttempt attempt, string status, string? decision ) => new()
    {
        Profile = attempt.Profile, AttemptNo = attempt.AttemptNo, ReasoningModeUsed = attempt.ReasoningModeUsed,
        RequestBytes = attempt.RequestBytes, MaxTokensSent = attempt.MaxTokensSent, HttpStatus = attempt.HttpStatus,
        Headers = attempt.Headers, ResponseBytes = attempt.ResponseBytes, LatencyMs = attempt.LatencyMs,
        Extracted = attempt.Extracted, Status = status, Decision = decision, Neurons = attempt.Neurons, CostUsd = attempt.CostUsd
    };

    private static CallAttempt WithCost( CallAttempt attempt, decimal? costUsd ) => new()
    {
        Profile = attempt.Profile, AttemptNo = attempt.AttemptNo, ReasoningModeUsed = attempt.ReasoningModeUsed,
        RequestBytes = attempt.RequestBytes, MaxTokensSent = attempt.MaxTokensSent, HttpStatus = attempt.HttpStatus,
        Headers = attempt.Headers, ResponseBytes = attempt.ResponseBytes, LatencyMs = attempt.LatencyMs,
        Extracted = attempt.Extracted, Status = attempt.Status, Decision = attempt.Decision, Neurons = attempt.Neurons, CostUsd = costUsd
    };

    private static CallAttempt Failure(
        ModelProfile profile, int attemptNo, string modeUsed, byte[] requestBytes, int cap, string status, string reason, Stopwatch stopwatch ) =>
        new()
        {
            Profile = profile, AttemptNo = attemptNo, ReasoningModeUsed = modeUsed, RequestBytes = requestBytes,
            MaxTokensSent = cap, LatencyMs = (int)stopwatch.ElapsedMilliseconds, Status = status, Decision = reason
        };

    #endregion Private Methods
}
