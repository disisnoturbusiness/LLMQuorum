using LLMQuorum.Core.Matching;

namespace LLMQuorum.Tests;

/// <summary>
/// Reasoning-stripping rules, using the three shapes actually returned by models
/// on the 2026-09-16 Georgia G-4 sweep.
/// </summary>
public sealed class AnswerCleanerTests
{
    #region Public Methods

    /// <summary>DeepSeek-R1-distill shape: a balanced think block, then the answer.</summary>
    [Fact]
    public void BalancedBlock_IsRemoved()
    {
        var result = AnswerCleaner.Clean( "<think>\nOkay, so I need to figure out...\n</think>\n\nSingle\nMarried" );

        Assert.Equal( "Single\nMarried", result.Answer );
        Assert.True( result.ReasoningStripped );
        Assert.False( result.ReasoningUnfinished );
    }

    /// <summary>QwQ shape: reasoning with no opening tag, a stray closing tag, then the answer.</summary>
    [Fact]
    public void StrayClosingTag_KeepsOnlyTextAfterIt()
    {
        var result = AnswerCleaner.Clean( "Okay, so I need to find the options. Let me check...\n</think>\n\nSingle\nMarried" );

        Assert.Equal( "Single\nMarried", result.Answer );
        Assert.True( result.ReasoningStripped );
    }

    /// <summary>Budget ran out mid-thought: an opening tag that never closes leaves no answer and is flagged unfinished.</summary>
    [Fact]
    public void UnclosedOpeningTag_IsUnfinishedReasoning()
    {
        var result = AnswerCleaner.Clean( "<think>\nThe user asks for the exact labels. We need to" );

        Assert.Equal( string.Empty, result.Answer );
        Assert.True( result.ReasoningUnfinished );
    }

    /// <summary>A plain answer passes through untouched.</summary>
    [Fact]
    public void PlainAnswer_Unchanged()
    {
        var result = AnswerCleaner.Clean( "Single\nHead of Household" );

        Assert.Equal( "Single\nHead of Household", result.Answer );
        Assert.False( result.ReasoningStripped );
    }

    /// <summary>Every truncation spelling seen across providers is recognised; normal stops are not.</summary>
    [Theory]
    [InlineData( "length", true )]
    [InlineData( "MAX_TOKENS", true )]
    [InlineData( "max_output_tokens", true )]
    [InlineData( "stop", false )]
    [InlineData( "end_turn", false )]
    [InlineData( null, false )]
    public void Truncation_Detected( string? finishReason, bool expected )
    {
        Assert.Equal( expected, AnswerCleaner.IsTruncation( finishReason ) );
    }

    #endregion Public Methods
}
