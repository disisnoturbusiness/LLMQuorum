using System.Globalization;
using System.Text.RegularExpressions;

namespace LLMQuorum.Core.Sweep;

/// <summary>What to do after a failed call.</summary>
public enum ErrorAction
{
    /// <summary>Wait and try the same request again.</summary>
    RetryAfterWait,

    /// <summary>Resend once with a corrected output cap parsed from the error.</summary>
    RetryWithCap,

    /// <summary>Resend once without the reasoning fragment; the result is a different seat identity.</summary>
    RetryWithoutReasoning,

    /// <summary>This provider's daily allowance is spent. Stop calling it until 00:00 UTC.</summary>
    StopProviderForDay,

    /// <summary>Stop calling this provider for the run: billing, quota or a failed free-tier fingerprint.</summary>
    StopProvider,

    /// <summary>This model refuses this caller. Never call it again in the run.</summary>
    BlockModel,

    /// <summary>Record the failure and move on. Retrying cannot succeed.</summary>
    GiveUp
}

/// <summary>A retry decision with its parameters.</summary>
/// <param name="Action">What to do.</param>
/// <param name="WaitSeconds">Seconds to wait before a retry.</param>
/// <param name="NewCap">Corrected output cap for RetryWithCap.</param>
/// <param name="Reason">Short reason, stored with the attempt.</param>
public sealed record ErrorDecision( ErrorAction Action, int WaitSeconds, int? NewCap, string Reason );

/// <summary>
/// Maps a failure to an action per provider. One generic "429 means retry" bucket was the
/// original design, and the verification found four kinds of 429 that can never succeed on
/// retry: Groq's output-tokens-per-minute pre-check (x-should-retry: false), Cloudflare 3036
/// (daily neurons spent), OpenRouter's own free-model daily cap, and Z.ai 1308/1113
/// (quota or billing). Retrying those only burns what is left of the allowance.
/// </summary>
public static partial class ErrorClassifier
{
    #region Public Methods

    /// <summary>Decides what to do with a failed attempt.</summary>
    /// <param name="provider">Provider key.</param>
    /// <param name="httpStatus">HTTP status code.</param>
    /// <param name="headers">Selected response headers, case-insensitive.</param>
    /// <param name="error">Extracted error code, message and metadata.</param>
    /// <returns>The action to take.</returns>
    public static ErrorDecision Decide(
        string provider, int httpStatus, IReadOnlyDictionary<string, string> headers, ExtractedResponse error )
    {
        var code = error.ErrorCode ?? string.Empty;
        var message = error.ErrorMessage ?? string.Empty;

        return provider switch
        {
            "groq" => DecideGroq( httpStatus, headers, code, message ),
            "openrouter" => DecideOpenRouter( httpStatus, headers, code, message, error.ErrorMetadata ),
            ModelProfile.PAID_PROVIDER => DecideOpenRouterPaid( httpStatus, code, message, error.ErrorMetadata ),
            "cloudflare" => DecideCloudflare( httpStatus, code, message ),
            "zai" => DecideZai( httpStatus, code, message ),
            "cohere" => DecideCohere( httpStatus, message ),
            _ => new ErrorDecision( ErrorAction.GiveUp, 0, null, $"{httpStatus} {code}" )
        };
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>Groq: OTPM pre-check is a config fix, x-should-retry false is final, 413 on compound is final.</summary>
    private static ErrorDecision DecideGroq( int status, IReadOnlyDictionary<string, string> headers, string code, string message )
    {
        var otpm = OtpmPattern().Match( message );

        if( otpm.Success )
        {
            var limit = int.Parse( otpm.Groups[1].Value, CultureInfo.InvariantCulture );
            var requested = int.Parse( otpm.Groups[2].Value, CultureInfo.InvariantCulture );
            return requested > limit
                ? new ErrorDecision( ErrorAction.RetryWithCap, 0, limit, $"OTPM cap {limit}" )
                : new ErrorDecision( ErrorAction.RetryAfterWait, 60, null, "OTPM window" );
        }

        if( headers.TryGetValue( "x-should-retry", out var shouldRetry ) && shouldRetry.Equals( "false", StringComparison.OrdinalIgnoreCase ) )
        {
            return new ErrorDecision( ErrorAction.GiveUp, 0, null, "x-should-retry false" );
        }

        if( status == 400 && ParamRejectedPattern().IsMatch( message ) )
        {
            return new ErrorDecision( ErrorAction.RetryWithoutReasoning, 0, null, "param rejected" );
        }

        if( status == 429 )
        {
            var wait = headers.TryGetValue( "retry-after", out var ra ) && int.TryParse( ra, out var s ) ? s : 30;
            return message.Contains( "requests per day", StringComparison.OrdinalIgnoreCase )
                ? new ErrorDecision( ErrorAction.StopProviderForDay, 0, null, "RPD spent" )
                : new ErrorDecision( ErrorAction.RetryAfterWait, wait, null, "rate limit" );
        }

        return new ErrorDecision( ErrorAction.GiveUp, 0, null, $"{status} {code}" );
    }

    /// <summary>
    /// OpenRouter: upstream 429s and 502/503 are worth waiting on. Its own 429 is either the free per-minute
    /// limit (wait for the reset) or the free daily cap (stop for the day). The docs say its own 429 carries
    /// X-RateLimit-Limit/-Remaining/-Reset for the limit that was hit, so a limit below the daily cap is the
    /// per-minute one. Without headers or a telling message it is treated as the daily cap, as before.
    /// </summary>
    private static ErrorDecision DecideOpenRouter(
        int status, IReadOnlyDictionary<string, string> headers, string code, string message, string? metadata )
    {
        var effective = int.TryParse( code, out var c ) ? c : status;

        if( effective == 402 )
        {
            return new ErrorDecision( ErrorAction.StopProvider, 0, null, $"402 credits or key limit {Short( metadata ?? message )}" );
        }

        if( effective == 403 )
        {
            return new ErrorDecision( ErrorAction.BlockModel, 0, null, "gated" );
        }

        if( effective == 400 && ContextLengthPattern().IsMatch( message ) )
        {
            var match = ContextLengthPattern().Match( message );
            var context = int.Parse( match.Groups[1].Value, CultureInfo.InvariantCulture );
            return new ErrorDecision( ErrorAction.RetryWithCap, 0, Math.Max( 256, context - 512 ), "context length" );
        }

        if( effective == 429 )
        {
            var upstream = metadata?.Contains( "upstream", StringComparison.OrdinalIgnoreCase ) ?? false;
            var wait = headers.TryGetValue( "retry-after", out var ra ) && int.TryParse( ra, out var s ) ? s : 10;
            if( upstream || message.Contains( "Provider returned error", StringComparison.OrdinalIgnoreCase ) )
            {
                return new ErrorDecision( ErrorAction.RetryAfterWait, Math.Max( wait, 10 ), null, "upstream rate limit" );
            }

            return IsOpenRouterPerMinute( headers, message )
                ? new ErrorDecision( ErrorAction.RetryAfterWait, OpenRouterResetWait( headers, wait ), null, "free per-minute limit" )
                : new ErrorDecision( ErrorAction.StopProviderForDay, 0, null, "free daily cap" );
        }

        return effective is 502 or 503 or 504
            ? new ErrorDecision( ErrorAction.RetryAfterWait, 15, null, "upstream unavailable" )
            : new ErrorDecision( ErrorAction.GiveUp, 0, null, $"{effective} {Short( message )}" );
    }

    /// <summary>
    /// Paid OpenRouter seats, one attempt each. 402 is the key limit, the balance or the in-flight budget: stop the paid
    /// lane for the run. 403 (gated) and an unmet pin (404, or 503 in the older wording) block the seat. A 429 or 5xx is
    /// recorded and not retried; the seat counts as asked unless the rejection provably came before routing.
    /// </summary>
    private static ErrorDecision DecideOpenRouterPaid(
        int status, string code, string message, string? metadata )
    {
        var effective = int.TryParse( code, out var c ) ? c : status;
        var md = metadata ?? string.Empty;

        // Paid seats are never retried (any send may bill), so nothing here waits: a decision only labels the failure
        // and says whether the rest of the lane stops for this run or just this seat.
        return effective switch
        {
            // limits.md: an in-flight budget 402 is transient, so the lane stops for this run and the plan tries the
            // unasked seats later; a single request too big for the budget blocks that seat; a spent key limit or
            // balance stops the lane.
            402 when md.Contains( "in_flight_budget_exhausted", StringComparison.OrdinalIgnoreCase ) ||
                     md.Contains( "openrouter_in_flight_budget", StringComparison.OrdinalIgnoreCase )
                => new ErrorDecision( ErrorAction.StopProvider, 0, null, "402 in-flight budget; paid seats stopped for this run" ),
            402 when md.Contains( "weight_exceeds_budget", StringComparison.OrdinalIgnoreCase )
                => new ErrorDecision( ErrorAction.BlockModel, 0, null, "402 request larger than the in-flight budget" ),
            402 => new ErrorDecision( ErrorAction.StopProvider, 0, null, $"402 credits or key limit {Short( md.Length > 0 ? md : message )}" ),
            403 => new ErrorDecision( ErrorAction.BlockModel, 0, null, "gated" ),
            // provider-routing.md: an unsatisfiable provider.only fails with 404; 503 kept for the older wording.
            404 or 503 when message.Contains( "routing", StringComparison.OrdinalIgnoreCase ) || message.Contains( "No endpoints", StringComparison.OrdinalIgnoreCase )
                            || message.Contains( "No allowed providers", StringComparison.OrdinalIgnoreCase )
                => new ErrorDecision( ErrorAction.BlockModel, 0, null, $"pin unmet: {Short( message )}" ),
            // MEASURED 2026-09-20: "temporarily rate-limited upstream", limit_source upstream_provider_shared_pool.
            // It bills nothing and it is one provider's queue, so the seat steps aside and the rest of the lane runs.
            429 when md.Contains( "limit_source", StringComparison.OrdinalIgnoreCase )
                => new ErrorDecision( ErrorAction.BlockModel, 0, null, $"429 upstream rate limit, not billed; seat left for a later run {Short( md )}" ),
            429 => new ErrorDecision( ErrorAction.GiveUp, 0, null, $"429 capacity, not retried {Short( message )}" ),
            _ => new ErrorDecision( ErrorAction.GiveUp, 0, null, $"{effective} {Short( message )}" )
        };
    }

    /// <summary>Cloudflare: 3036 means the free daily neurons are spent; 3040 is momentary capacity.</summary>
    private static ErrorDecision DecideCloudflare( int status, string code, string message )
    {
        if( code == "3036" || message.Contains( "daily free allocation", StringComparison.OrdinalIgnoreCase ) )
        {
            return new ErrorDecision( ErrorAction.StopProviderForDay, 0, null, "neurons spent" );
        }

        return code switch
        {
            "3040" => new ErrorDecision( ErrorAction.RetryAfterWait, 10, null, "capacity" ),
            "5016" => new ErrorDecision( ErrorAction.BlockModel, 0, null, "model agreement required" ),
            "5035" => new ErrorDecision( ErrorAction.BlockModel, 0, null, "paid only" ),
            "8007" => new ErrorDecision( ErrorAction.RetryWithoutReasoning, 0, null, "param rejected" ),
            _ => new ErrorDecision( ErrorAction.GiveUp, 0, null, $"{status} {code} {Short( message )}" )
        };
    }

    /// <summary>Z.ai: 1305/1302 overloaded or rate limited; 1308/1113 quota or billing; 1210 is an out-of-range cap.</summary>
    private static ErrorDecision DecideZai( int status, string code, string message )
    {
        if( code == "1210" )
        {
            var range = RangePattern().Match( message );
            return range.Success
                ? new ErrorDecision( ErrorAction.RetryWithCap, 0, int.Parse( range.Groups[1].Value, CultureInfo.InvariantCulture ), "max_tokens range" )
                : new ErrorDecision( ErrorAction.GiveUp, 0, null, "1210 unparsed" );
        }

        return code switch
        {
            "1305" or "1302" => new ErrorDecision( ErrorAction.RetryAfterWait, 20, null, "overloaded" ),
            "1308" or "1113" or "1311" => new ErrorDecision( ErrorAction.StopProvider, 0, null, "quota or billing" ),
            _ when status == 500 => new ErrorDecision( ErrorAction.RetryAfterWait, 15, null, "server error" ),
            _ => new ErrorDecision( ErrorAction.GiveUp, 0, null, $"{status} {code} {Short( message )}" )
        };
    }

    /// <summary>Cohere: 422 means the body is wrong for this model, so never resend it unchanged.</summary>
    private static ErrorDecision DecideCohere( int status, string message )
    {
        return status switch
        {
            422 => new ErrorDecision( ErrorAction.RetryWithoutReasoning, 0, null, "422 invalid generation" ),
            429 => new ErrorDecision( ErrorAction.RetryAfterWait, 60, null, "rate limit" ),
            _ => new ErrorDecision( ErrorAction.GiveUp, 0, null, $"{status} {Short( message )}" )
        };
    }

    /// <summary>True when OpenRouter's own 429 is the per-minute limit rather than the daily cap.</summary>
    private static bool IsOpenRouterPerMinute( IReadOnlyDictionary<string, string> headers, string message )
    {
        if( message.Contains( "per-day", StringComparison.OrdinalIgnoreCase ) )
        {
            return false;
        }

        if( message.Contains( "per-min", StringComparison.OrdinalIgnoreCase ) )
        {
            return true;
        }

        return headers.TryGetValue( "x-ratelimit-limit", out var limit )
            && int.TryParse( limit, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n )
            && n < QuotaLedger.OPENROUTER_FREE_DAILY;
    }

    /// <summary>Seconds until OpenRouter's X-RateLimit-Reset (epoch milliseconds), kept between 10 and 120.</summary>
    private static int OpenRouterResetWait( IReadOnlyDictionary<string, string> headers, int fallback )
    {
        if( headers.TryGetValue( "x-ratelimit-reset", out var reset )
            && long.TryParse( reset, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms ) )
        {
            var seconds = (int)Math.Ceiling( ( DateTimeOffset.FromUnixTimeMilliseconds( ms ) - DateTimeOffset.UtcNow ).TotalSeconds );
            return Math.Clamp( seconds, 10, 120 );
        }

        return Math.Clamp( fallback, 10, 120 );
    }

    private static string Short( string message ) => message.Length <= 120 ? message : message[..120];

    /// <summary>Groq OTPM rejection: "output tokens per minute (OTPM): Limit 1000, Requested 1001".</summary>
    [GeneratedRegex( @"OTPM\):\s*Limit\s*(\d+),\s*Requested\s*(\d+)", RegexOptions.IgnoreCase )]
    private static partial Regex OtpmPattern();

    /// <summary>Groq messages for an unsupported reasoning parameter.</summary>
    [GeneratedRegex( @"reasoning|must be one of|not supported", RegexOptions.IgnoreCase )]
    private static partial Regex ParamRejectedPattern();

    /// <summary>OpenRouter: "maximum context length is 32768 tokens".</summary>
    [GeneratedRegex( @"maximum context length is\s*(\d+)", RegexOptions.IgnoreCase )]
    private static partial Regex ContextLengthPattern();

    /// <summary>Z.ai 1210: "[1,98304]".</summary>
    [GeneratedRegex( @"\[\s*1\s*,\s*(\d+)\s*\]" )]
    private static partial Regex RangePattern();

    #endregion Private Methods
}
