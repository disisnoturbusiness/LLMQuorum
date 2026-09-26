using System.Text.RegularExpressions;

namespace LLMQuorum.Core.Sweep;

/// <summary>One queued unit of work: one question for one provider group, main pass or a retry round.</summary>
public sealed record PlanItem( int PlanItemId, string PlanName, int QuestionId, int Ordinal, string ProviderGroup, int Round,
                               IReadOnlyList<string>? Seats, string Status, DateTime? NotBeforeUtc, int Runs, DateTime UpdatedUtc );

/// <summary>The final attempt of one seat in a sweep, as the plan needs to judge completeness.</summary>
public sealed record SeatOutcome( string SeatId, string Platform, string Status, string? Decision, string? ErrorText );

/// <summary>What happened to a plan item after its sweep.</summary>
/// <param name="Status">"done" or "cut-off".</param>
/// <param name="NotBeforeUtc">When a cut-off item may run again.</param>
/// <param name="RetrySeats">Seats that failed transiently and get a later retry round.</param>
/// <param name="RetryNotBeforeUtc">When that retry round may run.</param>
/// <param name="Note">Human-readable reason.</param>
public sealed record PlanEvaluation( string Status, DateTime? NotBeforeUtc, IReadOnlyList<string> RetrySeats, DateTime? RetryNotBeforeUtc, string Note );

/// <summary>
/// The owner's rule, 2026-09-17: "slow and steady, steady and slow". Questions run strictly in order per
/// provider group. When a daily cap or usage limit stops a group partway through a question, that
/// question is redone whole for that group once the limit resets, and nothing later starts first. Seats
/// that fail for transient reasons (upstream overload, provider timeout) get up to two later retry rounds
/// so a real answer is not lost to a bad minute. Pure functions, so the rules are unit-tested.
/// </summary>
public static partial class PlanRules
{
    #region Data Members

    /// <summary>Later retry rounds for transient seat failures.</summary>
    public const int MAX_RETRY_ROUNDS = 2;

    /// <summary>Wait after a Claude usage limit before trying the question again.</summary>
    public static readonly TimeSpan ClaudeLimitWait = TimeSpan.FromMinutes( 60 );

    /// <summary>Wait before a paid item tries its unasked seats again after a money reason (cap, balance, key).</summary>
    public static readonly TimeSpan PaidMoneyWait = TimeSpan.FromHours( 6 );

    /// <summary>
    /// Wait after the paid lane stopped for a passing reason. MEASURED 2026-09-20 on the first paid run: Google AI
    /// Studio answered one web seat with HTTP 429 and no usage, which stops the lane by design; six hours is the
    /// wrong answer to a minute of upstream capacity.
    /// </summary>
    public static readonly TimeSpan PaidTransientWait = TimeSpan.FromMinutes( 30 );

    /// <summary>A "running" item older than this is treated as abandoned (app restarted mid-sweep).</summary>
    public static readonly TimeSpan StaleRunning = TimeSpan.FromHours( 3 );

    #endregion Data Members

    #region Public Methods

    /// <summary>
    /// Next item for a group: the first main-pass question not yet done, if it is eligible now (never skipping
    /// ahead of it); once every main item is done, the earliest eligible retry round.
    /// </summary>
    /// <param name="items">All items of the plan.</param>
    /// <param name="group">"openrouter" or "others".</param>
    /// <param name="nowUtc">Current time.</param>
    /// <returns>The item to run, or null.</returns>
    public static PlanItem? NextItem( IReadOnlyList<PlanItem> items, string group, DateTime nowUtc )
    {
        var groupItems = items.Where( i => i.ProviderGroup == group ).ToList();
        var firstOpen = groupItems.Where( i => i.Round == 0 && i.Status != "done" ).OrderBy( i => i.Ordinal ).FirstOrDefault();

        if( firstOpen is not null )
        {
            return IsEligible( firstOpen, nowUtc ) ? firstOpen : null;
        }

        return groupItems.Where( i => i.Round > 0 && i.Status != "done" && IsEligible( i, nowUtc ) )
                         .OrderBy( i => i.Round ).ThenBy( i => i.Ordinal ).FirstOrDefault();
    }

    /// <summary>Decides whether a sweep completed its item, and what to retry.</summary>
    /// <param name="outcomes">Final attempt per seat in the sweep.</param>
    /// <param name="group">Provider group.</param>
    /// <param name="round">Round of the item that ran.</param>
    /// <param name="nowUtc">Current time.</param>
    /// <param name="unaskedPaidSeats">Paid items only: the item's seats the spend ledger still shows as never asked.</param>
    /// <param name="unaffordablePaidSeats">Paid items only: seats left out before the sweep because the dollars left did not cover them.</param>
    /// <returns>The evaluation.</returns>
    public static PlanEvaluation Evaluate( IReadOnlyList<SeatOutcome> outcomes, string group, int round, DateTime nowUtc,
                                           IReadOnlyCollection<string>? unaskedPaidSeats = null,
                                           IReadOnlyCollection<string>? unaffordablePaidSeats = null )
    {
        if( group == ModelProfile.PAID_PROVIDER )
        {
            return EvaluatePaid( outcomes, unaskedPaidSeats ?? Array.Empty<string>(), unaffordablePaidSeats ?? Array.Empty<string>(), nowUtc );
        }

        var cut = outcomes.Where( IsCutOff ).ToList();

        if( cut.Count > 0 )
        {
            var claudeOnly = cut.All( o => o.Platform == "claude-sub" );
            var notBefore = claudeOnly ? nowUtc + ClaudeLimitWait : NextUtcReset( nowUtc );
            var note = $"cut off by a limit on {string.Join( ", ", cut.Select( o => o.Platform ).Distinct() )} " +
                       $"({cut.Count} seats); redo the whole question after {notBefore:yyyy-MM-dd HH:mm} UTC";
            return new PlanEvaluation( "cut-off", notBefore, Array.Empty<string>(), null, note );
        }

        var transient = outcomes.Where( IsTransient ).Select( o => o.SeatId ).ToList();

        if( transient.Count == 0 || round >= MAX_RETRY_ROUNDS )
        {
            var tail = transient.Count == 0 ? "" : $"; {transient.Count} seats still failing after {round} retry rounds";
            return new PlanEvaluation( "done", null, Array.Empty<string>(), null, $"complete: {outcomes.Count} seats{tail}" );
        }

        var retryAt = group is "openrouter" or "cloudflare" ? NextUtcReset( nowUtc ) : nowUtc.AddHours( 6 );
        return new PlanEvaluation( "done", null, transient, retryAt, $"complete: {outcomes.Count} seats; {transient.Count} transient failures queued for retry round {round + 1}" );
    }

    /// <summary>A seat that never got a fair ask because a daily cap, monthly cap or usage limit was hit.</summary>
    /// <param name="o">Seat outcome.</param>
    /// <returns>True when the question must be redone for this group.</returns>
    public static bool IsCutOff( SeatOutcome o ) =>
        o.Status is "BudgetSkipped" or "ProviderStopped"
        || ( o.Decision?.StartsWith( "StopProvider", StringComparison.Ordinal ) ?? false )
        || ( o.Platform == "claude-sub" && o.Status == "Error"
             && ( ClaudeLimit().IsMatch( o.ErrorText ?? string.Empty ) || ClaudeUnavailable().IsMatch( o.ErrorText ?? string.Empty ) ) );

    /// <summary>
    /// A seat that failed for a reason that may clear later: upstream overload, provider timeout, or a network
    /// failure on our side (MEASURED 2026-09-18: "No such host is known (api.cohere.ai)" after a reboot).
    /// </summary>
    /// <param name="o">Seat outcome.</param>
    /// <returns>True when a later retry is worthwhile.</returns>
    public static bool IsTransient( SeatOutcome o ) =>
        !IsCutOff( o ) && ( o.Status is "Timeout" or "Network"
                            || ( o.Status == "Error" && ( o.Decision?.StartsWith( "RetryAfterWait", StringComparison.Ordinal ) ?? false ) ) );

    /// <summary>Five minutes after the next 00:00 UTC, when daily free allowances reset.</summary>
    /// <param name="nowUtc">Current time.</param>
    /// <returns>Reset time.</returns>
    public static DateTime NextUtcReset( DateTime nowUtc ) => nowUtc.Date.AddDays( 1 ).AddMinutes( 5 );

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// Paid questions. A seat the spend ledger shows as asked is never asked again automatically, whatever happened.
    /// A seat still unasked because the lane refused or stopped before sending it (cap, key, balance, halt, a stop
    /// earlier in the run, or no outcome at all) is tried again in 6 hours; the rerun sends only unasked seats. A seat
    /// whose own call was rejected before routing (gated, unmet pin, bad request) is not retried: that needs a fix.
    /// </summary>
    private static PlanEvaluation EvaluatePaid( IReadOnlyList<SeatOutcome> outcomes, IReadOnlyCollection<string> unasked,
                                                IReadOnlyCollection<string> unaffordable, DateTime nowUtc )
    {
        var bySeat = outcomes.GroupBy( o => o.SeatId ).ToDictionary( g => g.Key, g => g.Last() );
        // Seats the plan runner left out for money are final for this question: they were never sent, and waiting for
        // the same dollars to stretch further only holds every later question behind them.
        var capped = unaffordable.Concat( unasked.Where( s => bySeat.TryGetValue( s, out var o ) && IsCapRefusal( o ) ) ).Distinct().ToList();
        var retry = unasked.Where( s => !capped.Contains( s )
                                        && ( !bySeat.TryGetValue( s, out var o ) || IsLaneRefusal( o ) || IsPassingStop( o.Decision ?? "" ) ) ).ToList();
        var rejected = unasked.Except( retry ).Except( capped ).ToList();
        var answered = outcomes.Count( o => o.Status is "Answered" or "Truncated" or "ThinkingExhausted" or "Empty" or "Filtered" );
        var noAnswer = outcomes.Where( o => o.Status is not ( "Answered" or "Truncated" or "ThinkingExhausted" or "Empty" or "Filtered"
                                                            or "BudgetSkipped" or "ProviderStopped" or "AlreadyAsked" or "Blocked" )
                                            && !unasked.Contains( o.SeatId ) )
                               .Select( o => o.SeatId ).ToList();

        var note = $"paid: {answered} answered" +
                   ( noAnswer.Count == 0 ? "" : $"; asked and billed, no answer, never re-asked by itself: {string.Join( ", ", noAnswer )}" ) +
                   ( rejected.Count == 0 ? "" : $"; rejected before routing, not billed, needs a fix: {string.Join( ", ", rejected )}" ) +
                   ( capped.Count == 0 ? "" : $"; not asked, more than the dollars left: {string.Join( ", ", capped )}" );

        var wait = PaidMoneyWait;

        if( retry.Count > 0 )
        {
            var why = retry.Select( s => bySeat.TryGetValue( s, out var o ) ? o.Decision : null ).FirstOrDefault( d => d is not null ) ?? "not reached";
            wait = IsPassingStop( why ) ? PaidTransientWait : PaidMoneyWait;
            note = $"{note}; not sent yet ({why}), trying again in {wait.TotalMinutes:0} minutes: {string.Join( ", ", retry )}";
        }

        // SweepPlan.Note is NVARCHAR(2000).
        note = note.Length > 1900 ? note[..1900] : note;
        return retry.Count > 0
            ? new PlanEvaluation( "cut-off", nowUtc + wait, Array.Empty<string>(), null, note )
            : new PlanEvaluation( "done", null, Array.Empty<string>(), null, note );
    }

    /// <summary>
    /// The paid lane stopped for something that passes on its own (upstream capacity, a timeout, an unpriced failure)
    /// rather than for money, which needs the owner.
    /// </summary>
    /// <param name="why">The stop reason recorded against the first unsent seat.</param>
    /// <returns>True when waiting minutes is the right answer, not hours.</returns>
    private static bool IsPassingStop( string why ) =>
        why.Contains( "429", StringComparison.Ordinal )
        || why.Contains( "capacity", StringComparison.OrdinalIgnoreCase )
        || why.Contains( "Timeout", StringComparison.OrdinalIgnoreCase )
        || why.Contains( "Network", StringComparison.OrdinalIgnoreCase )
        || why.Contains( "no usage reported", StringComparison.OrdinalIgnoreCase )
        || why.Contains( "in-flight", StringComparison.OrdinalIgnoreCase )
        || why.Contains( "502", StringComparison.Ordinal )
        || why.Contains( "503", StringComparison.Ordinal )
        || why.Contains( "504", StringComparison.Ordinal );

    /// <summary>
    /// Refused by the local dollar cap or the key's OpenRouter limit. Neither clears with time, so the seat is left
    /// unasked for this question rather than holding every later question behind it.
    /// </summary>
    private static bool IsCapRefusal( SeatOutcome o ) =>
        o.Status == "BudgetSkipped"
        && ( ( o.Decision?.StartsWith( "dollar cap", StringComparison.Ordinal ) ?? false )
             || ( o.Decision?.StartsWith( "OpenRouter key limit", StringComparison.Ordinal ) ?? false ) );

    /// <summary>The lane, not the seat, kept this call from going out, or refused it for a reason that clears.</summary>
    private static bool IsLaneRefusal( SeatOutcome o ) =>
        o.Status is "BudgetSkipped" or "ProviderStopped" or "Blocked"
        || ( o.Status == "Error" && ( o.Decision?.StartsWith( "StopProvider", StringComparison.Ordinal ) ?? false ) );

    private static bool IsEligible( PlanItem item, DateTime nowUtc ) =>
        ( item.NotBeforeUtc is null || item.NotBeforeUtc <= nowUtc )
        && ( item.Status != "running" || nowUtc - item.UpdatedUtc > StaleRunning );

    [GeneratedRegex( @"usage limit|rate limit|limit reached|limit will reset|\b429\b", RegexOptions.IgnoreCase )]
    private static partial Regex ClaudeLimit();

    /// <summary>
    /// The CLI could not be started at all. MEASURED 2026-09-17 after the move to the desktop: every Claude
    /// seat returned "Could not start Claude CLI: No Claude CLI build found" for twelve questions in a row,
    /// the plan counted those questions complete, and they were graded with no Claude answers. A missing or
    /// half-installed CLI is our problem, not the model's, so it cuts the question off for a later redo.
    /// </summary>
    [GeneratedRegex( @"could not start claude cli|no claude cli build found|authenticate|oauth", RegexOptions.IgnoreCase )]
    private static partial Regex ClaudeUnavailable();

    #endregion Private Methods
}
