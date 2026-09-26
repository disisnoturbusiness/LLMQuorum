using LLMQuorum.Core.Configuration;
using LLMQuorum.Core.Engine;
using LLMQuorum.Core.Matching;
using LLMQuorum.Core.Models;

namespace LLMQuorum.Tests;

/// <summary>
/// Proves the escalation ladder against every split that was specified, by
/// scripting what each seat returns and asserting both the outcome and how many
/// providers were actually consulted.
///
/// The consultation count is asserted as hard as the outcome. Escalating one
/// provider too far silently spends quota on a panel whose tightest seat allows
/// a thousand calls a month; escalating one too few decides a question with
/// fewer voices than the rule requires.
/// </summary>
public sealed class QuorumEngineTests
{
    #region Data Members

    /// <summary>Six-deep ladder mirroring the real panel, so exhaustion behaviour is exercised.</summary>
    private static readonly string[] _ladderKeys =
        { "cloudflare", "groq", "openrouter", "zai", "cohere", "anthropic" };

    #endregion Data Members

    #region Public Methods

    /// <summary>Unanimous base round stops immediately. This is the only "solid" result.</summary>
    [Fact]
    public async Task ThreeOfThree_StopsAtThree()
    {
        var result = await RunAsync( "A", "A", "A" );

        Assert.Equal( QuorumOutcome.Quorum, result.Verdict.Outcome );
        Assert.Equal( "3", result.Verdict.PartitionSignature );
        Assert.Equal( 3, result.Answers.Count );
    }

    /// <summary>Two of three is NOT good enough. One dissenter pulls in a fourth seat.</summary>
    [Fact]
    public async Task TwoOfThree_EscalatesByOne()
    {
        var result = await RunAsync( "A", "A", "B", "A" );

        Assert.Equal( QuorumOutcome.Quorum, result.Verdict.Outcome );
        Assert.Equal( 4, result.Answers.Count );
        Assert.Equal( "A", result.Verdict.WinningAnswer );
        Assert.Equal( 3, result.Verdict.WinningVotes );
    }

    /// <summary>Three distinct answers means the leader needs two more, so two are consulted at once.</summary>
    [Fact]
    public async Task AllDistinct_EscalatesByTwo()
    {
        var result = await RunAsync( "A", "B", "C", "A", "A" );

        Assert.Equal( QuorumOutcome.Quorum, result.Verdict.Outcome );
        Assert.Equal( 5, result.Answers.Count );
        Assert.Equal( "A", result.Verdict.WinningAnswer );
    }

    /// <summary>A fourth seat bringing a brand-new answer leaves the leader on two, so a fifth is consulted.</summary>
    [Fact]
    public async Task TwoOneOne_EscalatesToFive()
    {
        var result = await RunAsync( "A", "A", "B", "C", "A" );

        Assert.Equal( QuorumOutcome.Quorum, result.Verdict.Outcome );
        Assert.Equal( 5, result.Answers.Count );
    }

    /// <summary>Two pairs at five seats pulls in the sixth, which is the last one available.</summary>
    [Fact]
    public async Task TwoTwoOne_EscalatesToSix()
    {
        var result = await RunAsync( "A", "B", "C", "A", "B", "A" );

        Assert.Equal( QuorumOutcome.Quorum, result.Verdict.Outcome );
        Assert.Equal( 6, result.Answers.Count );
        Assert.Equal( "A", result.Verdict.WinningAnswer );
    }

    /// <summary>
    /// Three pairs across a fully consulted ladder is the exhaustion case. It
    /// reports UNRESOLVED and keeps all three clusters, because a two-vote
    /// plurality is not a finding and collapsing it to a winner would invent
    /// confidence the panel never produced.
    /// </summary>
    [Fact]
    public async Task TwoTwoTwo_ExhaustsAndReportsUnresolved()
    {
        var result = await RunAsync( "A", "B", "C", "A", "B", "C" );

        Assert.Equal( QuorumOutcome.Unresolved, result.Verdict.Outcome );
        Assert.Equal( 6, result.Answers.Count );
        Assert.Equal( 3, result.Verdict.Clusters.Count );
        Assert.Equal( "2-2-2", result.Verdict.PartitionSignature );
        Assert.Null( result.Verdict.WinningAnswer );
        Assert.All( result.Verdict.Clusters, c => Assert.Equal( 2, c.Votes ) );
    }

    /// <summary>A three-vote leader at four seats stops without consulting a fifth.</summary>
    [Fact]
    public async Task ThreeOfFour_StopsWithoutFifth()
    {
        var result = await RunAsync( "A", "A", "B", "A", "SHOULD-NOT-BE-ASKED" );

        Assert.Equal( QuorumOutcome.Quorum, result.Verdict.Outcome );
        Assert.Equal( 4, result.Answers.Count );
        Assert.DoesNotContain( result.Answers, a => a.AnswerText == "SHOULD-NOT-BE-ASKED" );
    }

    /// <summary>
    /// Failed calls never count toward agreement. Two successes that agree plus
    /// four failures is not a quorum of three, and must not be reported as one.
    /// </summary>
    [Fact]
    public async Task FailedCallsDoNotCountAsVotes()
    {
        var result = await RunAsync( "A", "A", null, null, null, null );

        Assert.Equal( QuorumOutcome.Unresolved, result.Verdict.Outcome );
        Assert.Equal( 2, result.Verdict.WinningVotes );
        Assert.Single( result.Verdict.Clusters );
    }

    /// <summary>Too few usable answers is FAILED, which is distinct from nobody agreeing.</summary>
    [Fact]
    public async Task TooFewAnswers_ReportsFailed()
    {
        var result = await RunAsync( "A", null, null, null, null, null );

        Assert.Equal( QuorumOutcome.Failed, result.Verdict.Outcome );
    }

    /// <summary>The escalation path records ask order so the ladder walk is reconstructable.</summary>
    [Fact]
    public async Task EscalationPath_RecordsAskOrder()
    {
        var result = await RunAsync( "A", "A", "B", "A" );

        Assert.Equal( new[] { "cloudflare", "groq", "openrouter", "zai" }, result.Verdict.EscalationPath );
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// Runs the engine against scripted seat responses. A null entry scripts a
    /// failed call so failure handling is exercised alongside the vote counting.
    /// </summary>
    /// <param name="scriptedAnswers">One entry per ladder position, in ask order. Null means the call failed.</param>
    /// <returns>The verdict and every answer the engine collected.</returns>
    private static async Task<QuorumRunResult> RunAsync( params string?[] scriptedAnswers )
    {
        var settings = new PanelSettings { BaseCount = 3, QuorumSize = 3, MinimumAnswers = 2 };
        var engine = new QuorumEngine( settings, new RuleBasedMatcher( "test-v1", 0.10 ) );

        var ladder = _ladderKeys.Select( ( key, index ) => new ProviderDefinition
        {
            Key = key,
            DisplayName = key,
            ModelId = "model-" + key,
            Lab = "lab-" + key,
            Endpoint = "https://example.invalid/" + key,
            ApiKeyFile = key + ".key",
            LadderPosition = index + 1
        } ).ToList();

        var question = new QuestionItem
        {
            QuestionId = 1,
            Ordinal = 1,
            Category = "settled-fact",
            Prompt = "scripted",
            Shape = AnswerShape.Text
        };

        return await engine.RunAsync( question, ladder, ( definition, rank, _ ) =>
        {
            var index = ladder.FindIndex( p => p.Key == definition.Key );
            var scripted = index < scriptedAnswers.Length ? scriptedAnswers[index] : null;

            return Task.FromResult( scripted is null
                ? new ProviderAnswer
                {
                    Provider = definition,
                    Rank = rank,
                    IsSuccess = false,
                    ErrorClass = CallErrorClass.RateLimit,
                    ErrorText = "scripted failure"
                }
                : new ProviderAnswer
                {
                    Provider = definition,
                    Rank = rank,
                    IsSuccess = true,
                    AnswerText = scripted
                } );
        } );
    }

    #endregion Private Methods
}
