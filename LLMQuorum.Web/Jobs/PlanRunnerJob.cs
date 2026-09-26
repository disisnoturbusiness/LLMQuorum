using Hangfire;
using LLMQuorum.Core.Configuration;
using LLMQuorum.Core.Sweep;

namespace LLMQuorum.Web.Jobs;

/// <summary>
/// Works through a sweep plan slowly and in order. Each provider has its own queue (since 2026-09-18), so a
/// provider waiting on its daily reset never holds back the others. Each run takes at most one item per
/// provider group, and only when that provider's remaining allowance covers what the question actually costs
/// (<see cref="PlanBudget"/>). Runs every 15 minutes from a Hangfire recurring job, so the plan keeps moving for
/// as many days as it needs without anyone watching it.
/// </summary>
public sealed class PlanRunnerJob
{
    #region Data Members

    /// <summary>What a paid item will ask now, and the unasked seats its dollars do not cover.</summary>
    /// <param name="Seats">Seats to send.</param>
    /// <param name="Unaffordable">Seat ids left unasked for this question because the money is not there.</param>
    private sealed record PaidStep( List<ModelProfile> Seats, IReadOnlyList<string> Unaffordable );

    /// <summary>Fast queues first, the slow Claude web-search queue last.</summary>
    private static readonly string[] _groupOrder = { "cloudflare", "groq", "cohere", "zai", "openrouter", "others", "claude-sub", ModelProfile.PAID_PROVIDER };

    private readonly QuorumConfig _config;
    private readonly ModelSweepJob _sweepJob;
    private readonly ILogger<PlanRunnerJob> _logger;

    #endregion Data Members

    #region Constructor

    /// <summary>Builds the job.</summary>
    public PlanRunnerJob( QuorumConfig config, ModelSweepJob sweepJob, ILogger<PlanRunnerJob> logger )
    {
        _config = config;
        _sweepJob = sweepJob;
        _logger = logger;
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>Runs the next eligible item for each provider group that can afford it.</summary>
    /// <param name="planName">Plan to advance.</param>
    /// <param name="cancellationToken">Supplied by Hangfire on abort.</param>
    [AutomaticRetry( Attempts = 0 )]
    [DisableConcurrentExecution( timeoutInSeconds: 30 )]
    [JobDisplayName( "LLMQuorum plan runner: {0}" )]
    public async Task RunAsync( string planName, CancellationToken cancellationToken )
    {
        var planner = new SweepPlanner( _config.ConnectionString );
        var budget = new PlanBudget( _config.ConnectionString, new QuotaLedger( _config.ConnectionString ), _config.KeysDirectory );
        var enabled = ProfileCatalog.Load( _config.ResolvePath( _config.ProfilesPath ) ).Where( p => p.Enabled ).ToList();

        var openGroups = ( await planner.LoadAsync( planName, cancellationToken ) )
            .Where( i => i.Status != "done" ).Select( i => i.ProviderGroup ).Distinct()
            .OrderBy( GroupRank ).ToList();

        foreach( var group in openGroups )
        {
            try
            {
                await RunGroupAsync( planner, budget, planName, group, enabled, cancellationToken );
            }
            catch( Exception ex ) when( ex is not OperationCanceledException )
            {
                // One broken group (a bad plan row, a provider outage in the checks) must not stop the others.
                _logger.LogError( ex, "Plan {Plan}, group {Group}: failed; the other groups carry on", planName, group );
            }
        }
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>Runs the group's next eligible item, if its allowance (or, for the paid lane, its dollars) covers it.</summary>
    private async Task RunGroupAsync( SweepPlanner planner, PlanBudget budget, string planName, string group,
                                      List<ModelProfile> enabled, CancellationToken cancellationToken )
    {
        var items = await planner.LoadAsync( planName, cancellationToken );
        var next = PlanRules.NextItem( items, group, DateTime.UtcNow );

        if( next is null )
        {
            return;
        }

        var seats = SeatsFor( group, next, enabled );
        var unaffordable = Array.Empty<string>() as IReadOnlyList<string>;

        if( group == ModelProfile.PAID_PROVIDER )
        {
            var paidStep = await PreparePaidAsync( planner, next, seats, enabled.Count( p => p.Paid ), cancellationToken );

            if( paidStep is null )
            {
                return;
            }

            seats = paidStep.Seats;
            unaffordable = paidStep.Unaffordable;
        }

        if( seats.Count == 0 )
        {
            var note = unaffordable.Count > 0
                ? $"not asked, more than the dollars left: {string.Join( ", ", unaffordable )}"
                : group == ModelProfile.PAID_PROVIDER
                    ? "every paid seat of this question was already asked; paid seats are never asked twice"
                    : "no enabled seats left for this item";
            await planner.CompleteAsync( next, 0, new PlanEvaluation( "done", null, Array.Empty<string>(), null, note ), cancellationToken );
            return;
        }

        var check = await budget.CheckAsync( group, seats, cancellationToken );

        if( !check.CanStart )
        {
            _logger.LogInformation( "Plan item {Id} ({Group}, question {Q}): {Check}; waiting for the reset", next.PlanItemId, group, next.QuestionId, check );
            return;
        }

        await RunItemAsync( planner, next, group, seats, check, unaffordable, cancellationToken );
    }

    /// <summary>
    /// Every money question is settled here, BEFORE the sweep, so no paid seat is ever sent only to be refused (each
    /// refusal used to cut the item off and retry it six hours later, forever). The lane waits (null) while it cannot
    /// spend at all: no paid seat enabled, no key file, a halt, a key that is unreadable or not limited the way the
    /// lane needs, a balance that cannot be read, or not enough money for even the cheapest unasked seat. Otherwise it
    /// returns the unasked seats the dollars cover, with the ones they do not as Unaffordable.
    /// </summary>
    private async Task<PaidStep?> PreparePaidAsync(
        SweepPlanner planner, PlanItem item, List<ModelProfile> seats, int paidEnabled, CancellationToken cancellationToken )
    {
        var gate = new SpendGate( _config.ConnectionString, _config.KeysDirectory );

        if( paidEnabled == 0 || !gate.PaidKeyPresent() )
        {
            return Wait( item, paidEnabled == 0 ? "no paid seats enabled" : "no paid key file" );
        }

        var status = await gate.StatusAsync( cancellationToken );

        if( status.HaltedReason is not null )
        {
            _logger.LogWarning( "Plan item {Id} (paid): lane halted ({Why}); waiting for the owner to clear it", item.PlanItemId, status.HaltedReason );
            return null;
        }

        var asked = await gate.AskedSeatsAsync( item.QuestionId, cancellationToken );
        var unasked = seats.Where( s => s.Paid && !asked.Contains( s.SeatId ) ).ToList();

        if( unasked.Count == 0 )
        {
            return new PaidStep( unasked, Array.Empty<string>() );
        }

        var key = await gate.ReadPaidKeyAsync( cancellationToken );

        if( key.Error is not null || key.Remaining is null )
        {
            return Wait( item, $"paid key: {key.Error ?? "no limit"}" );
        }

        var (balance, balanceError) = await gate.ReadAccountBalanceAsync( cancellationToken );

        if( balanceError is not null )
        {
            return Wait( item, $"account balance unreadable ({balanceError})" );
        }

        // Same three brakes the gate applies per call, applied once here so the answer is "run these seats" rather
        // than "send them all and let most be refused": the local cap over real spend, the key's own remaining
        // limit, and a balance that could absorb twice the estimate.
        var left = Math.Min( status.Limit - Math.Max( status.Spent, key.Usage ?? 0m ), key.Remaining.Value );
        var affordable = unasked.Where( s => Estimate( s ) <= left && balance - 2 * Estimate( s ) >= SpendGate.ACCOUNT_MARGIN_USD ).ToList();

        if( affordable.Count == 0 )
        {
            return Wait( item, $"${left:0.00} left under the cap and ${balance:0.00} on the account; the cheapest unasked seat needs ${unasked.Min( Estimate ):0.00}" );
        }

        return new PaidStep( affordable, unasked.Except( affordable ).Select( s => s.SeatId ).ToList() );
    }

    /// <summary>What a seat could cost at worst, as the gate will estimate it.</summary>
    private static decimal Estimate( ModelProfile seat ) => SpendGate.WorstCaseUsd( seat, 64, seat.MaxOutput );

    /// <summary>Logs why the paid lane is standing still and leaves the item untouched for a later run.</summary>
    private PaidStep? Wait( PlanItem item, string why )
    {
        _logger.LogInformation( "Plan item {Id} (paid, question {Q}): {Why}; waiting", item.PlanItemId, item.QuestionId, why );
        return null;
    }

    private static int GroupRank( string group )
    {
        var at = Array.IndexOf( _groupOrder, group );
        return at < 0 ? int.MaxValue : at;
    }

    /// <summary>
    /// Enabled seats an item will ask: its retry seats, or every enabled seat of the group's provider. Disabled
    /// seats are never asked, even when a retry item still names them.
    /// </summary>
    private static List<ModelProfile> SeatsFor( string group, PlanItem item, IReadOnlyList<ModelProfile> enabled )
    {
        if( item.Seats is not null )
        {
            // A named seat runs only in its own lane: a paid seat id filed under a free group (or the reverse) is ignored.
            return enabled.Where( p => item.Seats.Contains( p.SeatId ) && p.Paid == ( group == ModelProfile.PAID_PROVIDER ) ).ToList();
        }

        // Paid seats never join a free group, and "others" never includes either OpenRouter lane.
        return group == "others"
            ? enabled.Where( p => !p.Paid && p.Provider != "openrouter" && p.Provider != ModelProfile.PAID_PROVIDER ).ToList()
            : enabled.Where( p => p.Provider == group && ( group == ModelProfile.PAID_PROVIDER ) == p.Paid ).ToList();
    }

    private async Task RunItemAsync( SweepPlanner planner, PlanItem item, string group, List<ModelProfile> seats, BudgetCheck check,
                                     IReadOnlyList<string> unaffordable, CancellationToken cancellationToken )
    {
        await planner.MarkRunningAsync( item.PlanItemId, cancellationToken );
        var label = $"plan {item.PlanName} #{item.Ordinal} {group} round {item.Round}";

        // Whole-provider items go by provider key; retry items by exact seat ids so disabled seats stay out.
        // Paid items always go by exact seat ids: paid seats run only when named, and only the ones not yet asked.
        var exact = item.Seats is not null || group == ModelProfile.PAID_PROVIDER;
        var providers = exact ? null : seats.Select( s => s.Provider ).Distinct().ToArray();
        var seatIds = exact ? seats.Select( s => s.SeatId ).ToArray() : null;

        var sweepId = await _sweepJob.RunAsync( item.QuestionId, label, providers, seatIds, cancellationToken );
        var outcomes = await planner.LoadOutcomesAsync( sweepId, cancellationToken );
        IReadOnlyCollection<string>? unasked = null;

        string? halted = null;

        if( group == ModelProfile.PAID_PROVIDER )
        {
            // The spend ledger, not the outcomes, says which seats were really asked.
            var gate = new SpendGate( _config.ConnectionString, _config.KeysDirectory );
            var asked = await gate.AskedSeatsAsync( item.QuestionId, CancellationToken.None );
            unasked = seats.Select( s => s.SeatId ).Where( s => !asked.Contains( s ) ).ToList();
            halted = ( await gate.StatusAsync( CancellationToken.None ) ).HaltedReason;
        }

        var evaluation = PlanRules.Evaluate( outcomes, group, item.Round, DateTime.UtcNow, unasked, unaffordable );

        // A halted lane will not retry anything in six hours, whatever the rule worked out, so the row says so.
        var note = halted is null
            ? $"{evaluation.Note} (started with {check})"
            : $"{evaluation.Note} (started with {check}) -- PAID LANE HALTED: {halted}. Nothing is re-asked until it is cleared.";

        await planner.CompleteAsync( item, sweepId, evaluation with { Note = note.Length > 1900 ? note[..1900] : note }, cancellationToken );

        _logger.LogInformation( "Plan item {Id} (question {Q}, {Group}, round {Round}) -> {Status}: {Note}",
                                item.PlanItemId, item.QuestionId, group, item.Round, evaluation.Status, evaluation.Note );
    }

    #endregion Private Methods
}
