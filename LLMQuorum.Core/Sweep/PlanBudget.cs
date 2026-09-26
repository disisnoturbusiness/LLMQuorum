using Microsoft.Data.SqlClient;

namespace LLMQuorum.Core.Sweep;

/// <summary>What a provider group needs to start a question, and what it has left.</summary>
/// <param name="Needed">Amount one question costs this group (requests or neurons).</param>
/// <param name="Remaining">Amount left in the current window, or null when the provider has no known cap.</param>
/// <param name="Unit">"requests", "neurons" or "none".</param>
public sealed record BudgetCheck( decimal Needed, decimal? Remaining, string Unit )
{
    /// <summary>True when the question fits in what is left.</summary>
    public bool CanStart => Remaining is null || Remaining >= Needed;

    /// <summary>Human-readable line for logs and plan notes.</summary>
    public override string ToString() =>
        Remaining is null ? "no cap"
            : Unit == "usd" ? $"${Remaining:0.00} left, question needs ${Needed:0.00}"
            : $"{Remaining:0.#} {Unit} left, question needs {Needed:0.#}";
}

/// <summary>
/// The owner's rule, 2026-09-18: "they all seem to only allow x, so why are we asking for more than x".
/// A provider group starts a question only when its remaining allowance covers what that question actually
/// costs: one request per seat on request-capped providers (OpenRouter daily, Cohere monthly, Groq per model),
/// and the measured neurons per seat on Cloudflare. No padding; the cut-off rule redoes a question if retries
/// push it over.
/// </summary>
public sealed class PlanBudget
{
    #region Data Members

    /// <summary>Cloudflare neurons per seat when there is no history yet.</summary>
    public const decimal DEFAULT_CLOUDFLARE_NEURONS_PER_SEAT = 50m;

    private readonly string _connectionString;
    private readonly QuotaLedger _ledger;
    private readonly string _keysDirectory;

    #endregion Data Members

    #region Constructor

    /// <summary>Builds the budget checker.</summary>
    /// <param name="connectionString">LLMQuorum database.</param>
    /// <param name="ledger">Quota ledger.</param>
    /// <param name="keysDirectory">Key directory, so the paid check sees the halt file where it is actually written.</param>
    public PlanBudget( string connectionString, QuotaLedger ledger, string keysDirectory = "" )
    {
        _connectionString = connectionString;
        _ledger = ledger;
        _keysDirectory = keysDirectory;
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>Checks whether a provider group can start a question with these seats now.</summary>
    /// <param name="group">Provider group.</param>
    /// <param name="seats">Enabled seats the question will ask.</param>
    /// <param name="cancellationToken">Cancels the lookups.</param>
    /// <returns>The check.</returns>
    public async Task<BudgetCheck> CheckAsync( string group, IReadOnlyList<ModelProfile> seats, CancellationToken cancellationToken )
    {
        var day = QuotaLedger.DayKey();

        switch( group )
        {
            case "openrouter":
                var orUsed = await _ledger.UsedAsync( "openrouter", null, "request", day, cancellationToken );
                return Requests( seats.Count, QuotaLedger.OPENROUTER_FREE_DAILY - orUsed );

            case "cohere":
                var coUsed = await _ledger.UsedAsync( "cohere", null, "request", QuotaLedger.MonthKey(), cancellationToken );
                return Requests( seats.Count, QuotaLedger.COHERE_MONTHLY - 5 - coUsed );

            case "cloudflare":
                var cfUsed = await _ledger.UsedAsync( "cloudflare", null, "neuron", day, cancellationToken );
                var perSeat = await CloudflareNeuronsPerSeatAsync( cancellationToken );
                return new BudgetCheck( Math.Round( perSeat * seats.Count, 1 ), QuotaLedger.CLOUDFLARE_NEURON_CEILING - cfUsed, "neurons" );

            case "groq":
                return await GroqAsync( seats, day, cancellationToken );

            case ModelProfile.PAID_PROVIDER:
                return await PaidAsync( seats, cancellationToken );

            default:
                return new BudgetCheck( 0, null, "none" );
        }
    }

    /// <summary>One request per seat, against what is left.</summary>
    /// <param name="seatCount">Seats to ask.</param>
    /// <param name="remaining">Requests left.</param>
    /// <returns>The check.</returns>
    public static BudgetCheck Requests( int seatCount, decimal remaining ) => new( seatCount, Math.Max( remaining, 0 ), "requests" );

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// Groq caps each model separately, so the question fits only if every seat's model has a request left.
    /// Reported as the tightest model's remaining count against a need of one.
    /// </summary>
    private async Task<BudgetCheck> GroqAsync( IReadOnlyList<ModelProfile> seats, string day, CancellationToken cancellationToken )
    {
        decimal? tightest = null;

        foreach( var seat in seats.Where( s => s.DailyRequestLimit is not null ) )
        {
            var used = await _ledger.UsedAsync( "groq", seat.ModelId, "request", day, cancellationToken );
            var left = seat.DailyRequestLimit!.Value - used;
            tightest = tightest is null ? left : Math.Min( tightest.Value, left );
        }

        return tightest is null ? new BudgetCheck( 0, null, "none" ) : new BudgetCheck( 1, Math.Max( tightest.Value, 0 ), "requests" );
    }

    /// <summary>
    /// Paid lane: starts a question while the dollars left cover at least the cheapest seat's worst case. This is only
    /// the start check; every attempt still reserves its own worst case against the cap before it is sent.
    /// </summary>
    private async Task<BudgetCheck> PaidAsync( IReadOnlyList<ModelProfile> seats, CancellationToken cancellationToken )
    {
        var status = await new SpendGate( _connectionString, _keysDirectory ).StatusAsync( cancellationToken );
        var cheapest = seats.Count == 0 ? 0m : seats.Min( s => SpendGate.WorstCaseUsd( s, 64, s.MaxOutput ) );
        var left = status.HaltedReason is null ? Math.Max( status.Limit - status.Spent, 0 ) : 0m;
        return new BudgetCheck( Math.Round( cheapest, 4 ), left, "usd" );
    }

    /// <summary>
    /// Measured neurons per Cloudflare seat over the last five Cloudflare question runs that were not cut off.
    /// MEASURED 2026-09-17: whole-question costs ran 104 to 1110 neurons for 17 seats.
    /// </summary>
    private async Task<decimal> CloudflareNeuronsPerSeatAsync( CancellationToken cancellationToken )
    {
        const string SQL = @"
SELECT ISNULL( SUM( n ) / NULLIF( SUM( seats ), 0 ), @Default ) FROM (
    SELECT TOP 5 SUM( ISNULL( Neurons, 0 ) ) AS n, COUNT( DISTINCT SeatId ) AS seats
      FROM quorum.SweepCall WHERE Platform = 'cloudflare'
     GROUP BY SweepId
    HAVING SUM( CASE WHEN Status IN ( 'BudgetSkipped', 'ProviderStopped' ) THEN 1 ELSE 0 END ) = 0 AND SUM( ISNULL( Neurons, 0 ) ) > 0
     ORDER BY SweepId DESC ) recent;";

        await using var connection = new SqlConnection( _connectionString );
        await connection.OpenAsync( cancellationToken );
        await using var command = new SqlCommand( SQL, connection );
        command.Parameters.AddWithValue( "@Default", DEFAULT_CLOUDFLARE_NEURONS_PER_SEAT );
        return Convert.ToDecimal( await command.ExecuteScalarAsync( cancellationToken ) );
    }

    #endregion Private Methods
}
