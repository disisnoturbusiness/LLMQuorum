using Microsoft.Data.SqlClient;

namespace LLMQuorum.Core.Sweep;

/// <summary>
/// Local count of what each free allowance has consumed, and the gate that stops a call
/// before it can exceed one.
///
/// Why local: none of the allowances can be read back reliably before a call. OpenRouter reports
/// the free daily count only on GET /api/v1/key, Cloudflare refuses this token on its usage APIs, and
/// Cohere reports no remaining-calls header. The only brake that works before a call is one
/// kept here. On Cloudflare it is also the only brake at all if the account were ever on
/// Workers Paid, where past 10,000 neurons nothing errors and every neuron bills.
/// </summary>
public sealed class QuotaLedger
{
    #region Data Members

    /// <summary>
    /// OpenRouter free models: requests per UTC day. 1,000 once $10 of credits has ever been bought
    /// (bought 2026-09-18; GET /api/v1/key then reported free_model_daily_requests.limit = 1000).
    /// Without that purchase it is 50.
    /// </summary>
    public const int OPENROUTER_FREE_DAILY = 1000;

    /// <summary>Cloudflare free allocation is 10,000 neurons per UTC day; stop short of it.</summary>
    public const decimal CLOUDFLARE_NEURON_CEILING = 9500m;

    /// <summary>Cohere trial keys: calls per month.</summary>
    public const int COHERE_MONTHLY = 1000;

    /// <summary>Cloudflare bills $0.011 per 1,000 neurons, so neurons per USD is 1000 / 0.011.</summary>
    private const decimal NEURONS_PER_USD = 1000m / 0.011m;

    private readonly string _connectionString;

    #endregion Data Members

    #region Properties

    /// <summary>The database this ledger writes to, shared with the paid-seat dollar gate.</summary>
    public string ConnectionString => _connectionString;

    #endregion Properties

    #region Constructor

    /// <summary>Builds a ledger over the LLMQuorum database.</summary>
    /// <param name="connectionString">Connection string.</param>
    public QuotaLedger( string connectionString )
    {
        _connectionString = connectionString;
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>
    /// Decides whether a call may be made, and at what output cap. Cloudflare's cap is
    /// reduced to fit the neurons left today, priced at the model's list rate, so a single
    /// long generation cannot cross the ceiling.
    /// </summary>
    /// <param name="profile">Seat about to be called.</param>
    /// <param name="promptTokensEstimate">Estimated prompt tokens.</param>
    /// <param name="requestedCap">Cap the profile wants to send.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>Null cap with a reason when the call must be skipped.</returns>
    public async Task<(int? Cap, string? SkipReason)> GateAsync(
        ModelProfile profile, int promptTokensEstimate, int requestedCap, CancellationToken cancellationToken )
    {
        var day = DayKey();

        switch( profile.Provider )
        {
            case "openrouter":
                var orUsed = await SumAsync( "openrouter", null, "request", day, cancellationToken );
                return orUsed >= OPENROUTER_FREE_DAILY ? (null, $"OpenRouter free daily cap reached ({orUsed})") : (requestedCap, null);

            case "cohere":
                var coUsed = await SumAsync( "cohere", null, "request", MonthKey(), cancellationToken );
                return coUsed >= COHERE_MONTHLY - 5 ? (null, $"Cohere monthly cap nearly reached ({coUsed})") : (requestedCap, null);

            case "cloudflare":
                return await GateCloudflareAsync( profile, promptTokensEstimate, requestedCap, day, cancellationToken );

            case "groq" when profile.DailyRequestLimit is int limit:
                var groqUsed = await SumAsync( "groq", profile.ModelId, "request", day, cancellationToken );
                return groqUsed >= limit ? (null, $"Groq daily requests for model reached ({groqUsed})") : (requestedCap, null);

            default:
                return (requestedCap, null);
        }
    }

    /// <summary>Amount used so far in a window, for plan-level budget checks.</summary>
    /// <param name="provider">Provider key.</param>
    /// <param name="modelId">Model, or null for the whole provider.</param>
    /// <param name="unit">"request" or "neuron".</param>
    /// <param name="window">Day or month key.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>Amount used.</returns>
    public Task<decimal> UsedAsync( string provider, string? modelId, string unit, string window, CancellationToken cancellationToken ) =>
        SumAsync( provider, modelId, unit, window, cancellationToken );

    /// <summary>Records one attempt, and Cloudflare neurons when known.</summary>
    /// <param name="profile">Seat that was called.</param>
    /// <param name="neurons">Neurons consumed, Cloudflare only.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async Task RecordAttemptAsync( ModelProfile profile, decimal? neurons, CancellationToken cancellationToken )
    {
        var window = profile.Provider == "cohere" ? MonthKey() : DayKey();
        await InsertAsync( profile.Provider, profile.ModelId, "request", 1, window, "SweepCall", cancellationToken );

        if( neurons is decimal n && n > 0 )
        {
            await InsertAsync( profile.Provider, profile.ModelId, "neuron", n, DayKey(), "SweepCall", cancellationToken );
        }
    }

    /// <summary>Prices tokens in neurons at a Cloudflare model's list rate.</summary>
    /// <param name="profile">Cloudflare profile carrying USD prices.</param>
    /// <param name="inputTokens">Input tokens.</param>
    /// <param name="outputTokens">Output tokens.</param>
    /// <returns>Neurons, or null when the profile has no price.</returns>
    public static decimal? PriceNeurons( ModelProfile profile, int inputTokens, int outputTokens )
    {
        if( profile.PriceInUsdPerM is not decimal inPrice || profile.PriceOutUsdPerM is not decimal outPrice )
        {
            return null;
        }

        return ( inputTokens * inPrice + outputTokens * outPrice ) / 1_000_000m * NEURONS_PER_USD;
    }

    /// <summary>UTC day window key.</summary>
    /// <returns>yyyy-MM-dd.</returns>
    public static string DayKey() => DateTime.UtcNow.ToString( "yyyy-MM-dd" );

    /// <summary>UTC month window key.</summary>
    /// <returns>yyyy-MM.</returns>
    public static string MonthKey() => DateTime.UtcNow.ToString( "yyyy-MM" );

    #endregion Public Methods

    #region Private Methods

    /// <summary>Fits the output cap to the neurons left today at worst case, or skips when too little is left.</summary>
    private async Task<(int? Cap, string? SkipReason)> GateCloudflareAsync(
        ModelProfile profile, int promptTokensEstimate, int requestedCap, string day, CancellationToken cancellationToken )
    {
        if( profile.PriceOutUsdPerM is not decimal outPrice || outPrice <= 0 )
        {
            return (null, "Cloudflare profile has no output price; refusing to call without a neuron estimate");
        }

        var used = await SumAsync( "cloudflare", null, "neuron", day, cancellationToken );
        var promptCost = PriceNeurons( profile, promptTokensEstimate, 0 ) ?? 0;
        var room = CLOUDFLARE_NEURON_CEILING - used - promptCost;
        var neuronsPerOutputToken = outPrice / 1_000_000m * NEURONS_PER_USD;
        var affordable = (int)Math.Floor( room / neuronsPerOutputToken );
        var cap = Math.Min( requestedCap, affordable );

        return cap < 64
            ? (null, $"Cloudflare neurons left today too low ({used:F0} used)")
            : (cap, null);
    }

    private async Task<decimal> SumAsync(
        string provider, string? modelId, string unit, string window, CancellationToken cancellationToken )
    {
        const string SQL = @"
SELECT ISNULL( SUM( Amount ), 0 ) FROM quorum.QuotaLedger
 WHERE Provider = @Provider AND Unit = @Unit AND WindowKey = @Window
   AND ( @ModelId IS NULL OR ModelId = @ModelId );";

        await using var connection = new SqlConnection( _connectionString );
        await connection.OpenAsync( cancellationToken );
        await using var command = new SqlCommand( SQL, connection );
        command.Parameters.AddWithValue( "@Provider", provider );
        command.Parameters.AddWithValue( "@Unit", unit );
        command.Parameters.AddWithValue( "@Window", window );
        command.Parameters.AddWithValue( "@ModelId", (object?)modelId ?? DBNull.Value );
        return Convert.ToDecimal( await command.ExecuteScalarAsync( cancellationToken ) );
    }

    private async Task InsertAsync(
        string provider, string? modelId, string unit, decimal amount, string window, string source, CancellationToken cancellationToken )
    {
        const string SQL = @"
INSERT INTO quorum.QuotaLedger ( Provider, ModelId, Unit, Amount, WindowKey, Source )
VALUES ( @Provider, @ModelId, @Unit, @Amount, @Window, @Source );";

        await using var connection = new SqlConnection( _connectionString );
        await connection.OpenAsync( cancellationToken );
        await using var command = new SqlCommand( SQL, connection );
        command.Parameters.AddWithValue( "@Provider", provider );
        command.Parameters.AddWithValue( "@ModelId", (object?)modelId ?? DBNull.Value );
        command.Parameters.AddWithValue( "@Unit", unit );
        command.Parameters.AddWithValue( "@Amount", amount );
        command.Parameters.AddWithValue( "@Window", window );
        command.Parameters.AddWithValue( "@Source", source );
        await command.ExecuteNonQueryAsync( cancellationToken );
    }

    #endregion Private Methods
}
