using LLMQuorum.Core.Sweep;

namespace LLMQuorum.Tests;

/// <summary>
/// The slow-and-steady plan: strict order per provider group, a limit-cut question is redone whole after the
/// limit resets, transient seat failures get later retry rounds, and nothing skips ahead of an open question.
/// </summary>
public sealed class PlanRulesTests
{
    #region Data Members

    private static readonly DateTime _now = new( 2026, 9, 17, 15, 0, 0, DateTimeKind.Utc );

    #endregion Data Members

    #region Public Methods

    /// <summary>The first open main-pass question wins; later ones never start first.</summary>
    [Fact]
    public void NextItem_IsFirstOpenInOrder()
    {
        var items = new[] { Item( 1, 1, "done" ), Item( 2, 2, "pending" ), Item( 3, 3, "pending" ) };
        Assert.Equal( 2, PlanRules.NextItem( items, "openrouter", _now )!.PlanItemId );
    }

    /// <summary>A cut-off question waiting for its reset blocks the group; the next question does not jump ahead.</summary>
    [Fact]
    public void CutOffQuestion_BlocksLaterQuestions()
    {
        var items = new[] { Item( 1, 1, "cut-off", notBefore: _now.AddHours( 9 ) ), Item( 2, 2, "pending" ) };
        Assert.Null( PlanRules.NextItem( items, "openrouter", _now ) );
        Assert.Equal( 1, PlanRules.NextItem( items, "openrouter", _now.AddHours( 10 ) )!.PlanItemId );
    }

    /// <summary>Groups are independent: OpenRouter waiting does not hold back everyone else.</summary>
    [Fact]
    public void Groups_AreIndependent()
    {
        var items = new[] { Item( 1, 1, "cut-off", notBefore: _now.AddHours( 9 ) ), Item( 2, 1, "pending", group: "others" ) };
        Assert.Equal( 2, PlanRules.NextItem( items, "others", _now )!.PlanItemId );
    }

    /// <summary>Retry rounds run only after every main-pass question in the group is done.</summary>
    [Fact]
    public void Retries_WaitForMainPass()
    {
        var items = new[] { Item( 1, 1, "done" ), Item( 2, 2, "pending" ), Item( 3, 1, "pending", round: 1 ) };
        Assert.Equal( 2, PlanRules.NextItem( items, "openrouter", _now )!.PlanItemId );

        var finished = new[] { Item( 1, 1, "done" ), Item( 2, 2, "done" ), Item( 3, 1, "pending", round: 1 ) };
        Assert.Equal( 3, PlanRules.NextItem( finished, "openrouter", _now )!.PlanItemId );
    }

    /// <summary>A running item is left alone unless it is stale (the app restarted mid-sweep).</summary>
    [Fact]
    public void RunningItem_OnlyRetakenWhenStale()
    {
        Assert.Null( PlanRules.NextItem( new[] { Item( 1, 1, "running", updated: _now.AddMinutes( -20 ) ) }, "openrouter", _now ) );
        Assert.NotNull( PlanRules.NextItem( new[] { Item( 1, 1, "running", updated: _now.AddHours( -4 ) ) }, "openrouter", _now ) );
    }

    /// <summary>OpenRouter's daily cap mid-question: cut off, redo whole question after the next UTC reset.</summary>
    [Fact]
    public void DailyCap_CutsOffUntilReset()
    {
        var eval = PlanRules.Evaluate( new[]
        {
            Outcome( "a", "openrouter", "Answered" ),
            Outcome( "b", "openrouter", "Error", "StopProviderForDay: free daily cap" ),
            Outcome( "c", "openrouter", "ProviderStopped" )
        }, "openrouter", 0, _now );

        Assert.Equal( "cut-off", eval.Status );
        Assert.Equal( new DateTime( 2026, 9, 18, 0, 5, 0, DateTimeKind.Utc ), eval.NotBeforeUtc );
    }

    /// <summary>A Claude usage limit cuts off only for an hour, not a day.</summary>
    [Fact]
    public void ClaudeUsageLimit_WaitsAnHour()
    {
        var eval = PlanRules.Evaluate( new[]
        {
            Outcome( "a", "groq", "Answered" ),
            Outcome( "b", "claude-sub", "Error", errorText: "Claude usage limit reached. Your limit will reset at 5pm." )
        }, "others", 0, _now );

        Assert.Equal( "cut-off", eval.Status );
        Assert.Equal( _now.AddMinutes( 60 ), eval.NotBeforeUtc );
    }

    /// <summary>
    /// MEASURED 2026-09-17 after the machine move: the Claude CLI could not start, twelve questions were
    /// counted complete with no Claude answers at all. A CLI that will not start is our problem, so it cuts off.
    /// </summary>
    [Theory]
    [InlineData( "Could not start Claude CLI: No Claude CLI build found under 'C:\\Users\\Dan\\AppData\\Roaming\\Claude\\claude-code'." )]
    [InlineData( "Failed to authenticate: OAuth session expired and could not be refreshed" )]
    public void ClaudeCliUnavailable_CutsOff( string errorText )
    {
        var eval = PlanRules.Evaluate( new[]
        {
            Outcome( "a", "cloudflare", "Answered" ),
            Outcome( "b", "claude-sub", "Error", errorText: errorText )
        }, "others", 0, _now );

        Assert.Equal( "cut-off", eval.Status );
        Assert.Equal( _now.AddMinutes( 60 ), eval.NotBeforeUtc );
    }

    /// <summary>Upstream overload on a seat is not a cut-off: the question is done and the seat is queued to retry.</summary>
    [Fact]
    public void TransientFailure_QueuesRetry()
    {
        var eval = PlanRules.Evaluate( new[]
        {
            Outcome( "a", "openrouter", "Answered" ),
            Outcome( "g", "openrouter", "Error", "RetryAfterWait: upstream rate limit" ),
            Outcome( "u", "openrouter", "Error", "GiveUp: 404 Provider returned error" )
        }, "openrouter", 0, _now );

        Assert.Equal( "done", eval.Status );
        Assert.Equal( new[] { "g" }, eval.RetrySeats );
        Assert.Equal( PlanRules.NextUtcReset( _now ), eval.RetryNotBeforeUtc );
    }

    /// <summary>MEASURED 2026-09-18: DNS failed after a reboot. A network failure is retried, not recorded as a model error.</summary>
    [Fact]
    public void NetworkFailure_QueuesRetry()
    {
        var eval = PlanRules.Evaluate( new[] { Outcome( "c", "cohere", "Network", errorText: "No such host is known. (api.cohere.ai:443)" ) }, "cohere", 0, _now );
        Assert.Equal( new[] { "c" }, eval.RetrySeats );
        Assert.Equal( _now.AddHours( 6 ), eval.RetryNotBeforeUtc );
    }

    /// <summary>Daily-capped providers retry after the reset, not in six hours.</summary>
    [Fact]
    public void CloudflareRetry_WaitsForReset()
    {
        var eval = PlanRules.Evaluate( new[] { Outcome( "c", "cloudflare", "Timeout" ) }, "cloudflare", 0, _now );
        Assert.Equal( PlanRules.NextUtcReset( _now ), eval.RetryNotBeforeUtc );
    }

    /// <summary>His rule: the threshold is what the question costs, one request per seat, never more.</summary>
    [Theory]
    [InlineData( 14, 14, true )]
    [InlineData( 14, 13, false )]
    [InlineData( 14, 26, true )]
    [InlineData( 9, -3, false )]
    public void Threshold_IsExactlyTheCost( int seats, int remaining, bool canStart )
    {
        var check = PlanBudget.Requests( seats, remaining );
        Assert.Equal( seats, check.Needed );
        Assert.Equal( canStart, check.CanStart );
    }

    /// <summary>A provider with no known cap always starts; the cut-off rule catches a surprise limit.</summary>
    [Fact]
    public void NoCap_AlwaysStarts()
    {
        Assert.True( new BudgetCheck( 0, null, "none" ).CanStart );
    }

    /// <summary>After the last retry round a still-failing seat is left as an Error; no endless retries.</summary>
    [Fact]
    public void RetryRounds_AreCapped()
    {
        var eval = PlanRules.Evaluate( new[] { Outcome( "g", "openrouter", "Error", "RetryAfterWait: upstream rate limit" ) },
                                       "openrouter", PlanRules.MAX_RETRY_ROUNDS, _now );
        Assert.Equal( "done", eval.Status );
        Assert.Empty( eval.RetrySeats );
    }

    #endregion Public Methods

    #region Private Methods

    private static PlanItem Item( int id, int ordinal, string status, string group = "openrouter", int round = 0,
                                  DateTime? notBefore = null, DateTime? updated = null ) =>
        new( id, "p", 100 + ordinal, ordinal, group, round, null, status, notBefore, 0, updated ?? _now.AddDays( -1 ) );

    private static SeatOutcome Outcome( string seat, string platform, string status, string? decision = null, string? errorText = null ) =>
        new( seat, platform, status, decision, errorText );

    #endregion Private Methods
}
