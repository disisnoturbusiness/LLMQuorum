using LLMQuorum.Core.Matching;
using LLMQuorum.Core.Models;

namespace LLMQuorum.Tests;

/// <summary>
/// Covers the comparison rules. The cases here are drawn from real measured
/// provider output, not invented: twelve models were asked which form is
/// Colorado's employee withholding certificate and produced nine different
/// answers wrapped in nine different sentences. The matcher has to see through
/// the sentence to the identifier, and must NOT collapse genuinely different
/// form numbers into one cluster.
/// </summary>
public sealed class RuleBasedMatcherTests
{
    #region Data Members

    private static readonly RuleBasedMatcher _matcher = new( "test-v1", 0.10 );

    #endregion Data Members

    #region Public Methods

    /// <summary>Same identifier wrapped in different prose must cluster together.</summary>
    [Theory]
    [InlineData( "The current form is DR 0004.", "Colorado uses Form DR 0004 for withholding." )]
    [InlineData( "DR 0004", "dr-0004" )]
    [InlineData( "It is DR0004.", "The answer is DR 0004" )]
    public void SameIdentifier_DifferentProse_MatchesKeys( string first, string second )
    {
        Assert.Equal(
            _matcher.BuildKey( first, AnswerShape.Text ),
            _matcher.BuildKey( second, AnswerShape.Text ) );
    }

    /// <summary>
    /// Different form numbers must NOT collapse. Leading zeros are meaningful:
    /// DR 0004 and DR 0104 are different Colorado forms, and merging them would
    /// manufacture agreement that does not exist.
    /// </summary>
    [Theory]
    [InlineData( "DR 0004", "DR 0104" )]
    [InlineData( "DR 0004", "DR 004" )]
    [InlineData( "Form DR 0145", "Form DR 1214" )]
    [InlineData( "CR-5", "CO-4" )]
    public void DifferentIdentifiers_DoNotMatch( string first, string second )
    {
        Assert.NotEqual(
            _matcher.BuildKey( first, AnswerShape.Text ),
            _matcher.BuildKey( second, AnswerShape.Text ) );
    }

    /// <summary>
    /// Numbers within tolerance land in one cluster. Tolerance lives in the
    /// clustering pass rather than the key, because an equality key cannot
    /// express "close enough" without splitting pairs at a bucket boundary.
    /// 5,000 and 5,200 differ by four percent and must not be separated by a
    /// ten percent rule.
    /// </summary>
    [Fact]
    public void ScalarsWithinTolerance_ClusterTogether()
    {
        var clusters = _matcher.Cluster( new[]
        {
            MakeAnswer( "alpha", 1, "about 5,000 dollars" ),
            MakeAnswer( "bravo", 2, "5200" )
        }, AnswerShape.Scalar );

        Assert.Single( clusters );
        Assert.Equal( 2, clusters[0].Votes );
    }

    /// <summary>Numbers beyond tolerance stay in separate clusters.</summary>
    [Fact]
    public void ScalarsOutsideTolerance_StaySeparate()
    {
        var clusters = _matcher.Cluster( new[]
        {
            MakeAnswer( "alpha", 1, "3000" ),
            MakeAnswer( "bravo", 2, "5000" )
        }, AnswerShape.Scalar );

        Assert.Equal( 2, clusters.Count );
    }

    /// <summary>
    /// Anchoring each cluster on its first member stops a chain of small steps
    /// from merging genuinely different magnitudes: 100, 109 and 119 must not
    /// all collapse into one answer just because each neighbour is within ten
    /// percent of the last.
    /// </summary>
    [Fact]
    public void ScalarChain_DoesNotMergeAcrossAnchor()
    {
        var clusters = _matcher.Cluster( new[]
        {
            MakeAnswer( "alpha", 1, "100" ),
            MakeAnswer( "bravo", 2, "109" ),
            MakeAnswer( "charlie", 3, "119" )
        }, AnswerShape.Scalar );

        Assert.Equal( 2, clusters.Count );
        Assert.Equal( 2, clusters[0].Votes );
    }

    /// <summary>Booleans buried in a sentence still resolve to the same key.</summary>
    [Theory]
    [InlineData( "Yes, it has a marital status field.", "It does have one." )]
    [InlineData( "No, there is no such field.", "It does not." )]
    public void BooleansInProse_Normalize( string first, string second )
    {
        Assert.Equal(
            _matcher.BuildKey( first, AnswerShape.Bool ),
            _matcher.BuildKey( second, AnswerShape.Bool ) );
    }

    /// <summary>Opposite booleans must not match, which is the case that actually matters.</summary>
    [Fact]
    public void OppositeBooleans_DoNotMatch()
    {
        Assert.NotEqual(
            _matcher.BuildKey( "Yes it does.", AnswerShape.Bool ),
            _matcher.BuildKey( "No it does not.", AnswerShape.Bool ) );
    }

    /// <summary>Sets are unordered, so listing the same items differently still clusters.</summary>
    [Fact]
    public void SetsIgnoreOrder()
    {
        Assert.Equal(
            _matcher.BuildKey( "Single, Married, Head of Household", AnswerShape.Set ),
            _matcher.BuildKey( "Head of Household; Married; Single", AnswerShape.Set ) );
    }

    /// <summary>Clusters come back largest first so the leader is always at index zero.</summary>
    [Fact]
    public void Clusters_AreOrderedByVotesDescending()
    {
        var answers = new[]
        {
            MakeAnswer( "alpha", 1, "DR 0104" ),
            MakeAnswer( "bravo", 2, "DR 0004" ),
            MakeAnswer( "charlie", 3, "The form is DR 0004." ),
            MakeAnswer( "delta", 4, "Form DR-0004 applies." )
        };

        var clusters = _matcher.Cluster( answers, AnswerShape.Text );

        Assert.Equal( 2, clusters.Count );
        Assert.Equal( 3, clusters[0].Votes );
        Assert.Equal( new[] { "bravo", "charlie", "delta" }, clusters[0].ProviderKeys );
    }

    /// <summary>An unusable answer produces no key and therefore cannot become a vote.</summary>
    [Fact]
    public void EmptyAnswer_YieldsNoKey()
    {
        Assert.Null( _matcher.BuildKey( "   ", AnswerShape.Text ) );
    }


    /// <summary>
    /// Regression for a bug found on the first live sweep: one provider returned
    /// the form number using U+2011 NON-BREAKING HYPHEN and was reported as
    /// disagreeing with a provider that used a plain space. Same form, fake
    /// disagreement.
    /// </summary>
    [Theory]
    [InlineData( "Colorado Form DR‑0004, and it does not include a status field.", "Form DR 0004" )]
    [InlineData( "DR–0004", "DR-0004" )]
    [InlineData( "DR—0004", "DR 0004" )]
    public void UnicodeDashes_FoldToAscii( string first, string second )
    {
        Assert.Equal(
            _matcher.BuildKey( first, AnswerShape.Text ),
            _matcher.BuildKey( second, AnswerShape.Text ) );
    }

    /// <summary>
    /// Regression from the Georgia G-4 test run: a provider declined with "I don't
    /// have access" and was counted as a one-vote answer. Refusals produce no key.
    /// </summary>
    [Theory]
    [InlineData( "I don't have access to the current Georgia Form G-4, so I cannot provide the exact marital status labels as printed on the form." )]
    [InlineData( "I am unable to verify the current form." )]
    [InlineData( "As an AI, I cannot browse the web." )]
    public void Refusals_YieldNoKey( string refusal )
    {
        Assert.Null( _matcher.BuildKey( refusal, AnswerShape.Set ) );
        Assert.Null( _matcher.BuildKey( refusal, AnswerShape.Text ) );
    }

    /// <summary>A factual answer containing "cannot" is not a refusal; the pattern is anchored on first person.</summary>
    [Fact]
    public void FactualCannot_IsNotARefusal()
    {
        Assert.NotNull( _matcher.BuildKey( "No, the federal W-4 cannot be used for Oregon withholding.", AnswerShape.Bool ) );
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>Builds a successful answer for clustering tests.</summary>
    /// <param name="providerKey">Seat identity used in cluster attribution.</param>
    /// <param name="rank">Ask order, which breaks ties between equal-sized clusters.</param>
    /// <param name="text">The scripted answer text.</param>
    /// <returns>A populated success record.</returns>
    private static ProviderAnswer MakeAnswer( string providerKey, int rank, string text )
    {
        return new ProviderAnswer
        {
            Provider = new ProviderDefinition
            {
                Key = providerKey,
                DisplayName = providerKey,
                ModelId = "m",
                Lab = "l",
                Endpoint = "https://example.invalid",
                ApiKeyFile = "k.key"
            },
            Rank = rank,
            IsSuccess = true,
            AnswerText = text
        };
    }

    #endregion Private Methods
}
