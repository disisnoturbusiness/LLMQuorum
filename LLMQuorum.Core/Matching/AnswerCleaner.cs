using System.Text.RegularExpressions;

namespace LLMQuorum.Core.Matching;

/// <summary>
/// Separates a model's final answer from reasoning text that some models write
/// straight into message.content instead of a separate reasoning field.
///
/// MEASURED on 2026-09-16:
///   * deepseek-r1-distill-qwen-32b returned "&lt;think&gt; ... &lt;/think&gt;" then the answer.
///   * qwq-32b returned its reasoning with NO opening tag, then "&lt;/think&gt;", then the answer.
///   * a model that runs out of budget mid-thought leaves an opening tag with no close.
/// Grading any of those as the answer compares a monologue against a four-line
/// list, which is a harness error dressed up as a model error.
/// </summary>
public static partial class AnswerCleaner
{
    #region Public Methods

    /// <summary>
    /// Returns the final answer with reasoning removed, and reports what was removed.
    /// Rules, applied in order:
    ///   1. every balanced think/thinking block is removed;
    ///   2. a stray closing tag means everything before it was reasoning, so only
    ///      the text after the LAST closing tag is kept;
    ///   3. an opening tag that never closes means the model ran out of budget while
    ///      still thinking, so only the text before it is kept and the result is
    ///      flagged as unfinished reasoning.
    /// </summary>
    /// <param name="content">Raw message content as returned by the provider.</param>
    /// <returns>The cleaned answer, whether reasoning was stripped, and whether reasoning was left unfinished.</returns>
    public static CleanedAnswer Clean( string? content )
    {
        if( string.IsNullOrWhiteSpace( content ) )
        {
            return new CleanedAnswer( string.Empty, false, false );
        }

        var text = content;
        var stripped = false;

        var withoutBlocks = BalancedBlock().Replace( text, string.Empty );
        stripped |= withoutBlocks.Length != text.Length;
        text = withoutBlocks;

        var lastClose = LastCloseTag().Match( text );
        if( lastClose.Success )
        {
            text = text[( lastClose.Index + lastClose.Length )..];
            stripped = true;
        }

        var unclosed = OpenTag().Match( text );
        var unfinished = false;
        if( unclosed.Success )
        {
            text = text[..unclosed.Index];
            stripped = true;
            unfinished = true;
        }

        return new CleanedAnswer( text.Trim(), stripped, unfinished );
    }

    /// <summary>
    /// True when a provider's stop reason means the output limit cut the response
    /// off. Covers the spellings seen across OpenAI-compatible providers, Cohere
    /// and Anthropic.
    /// </summary>
    /// <param name="finishReason">Raw finish or stop reason, may be null.</param>
    /// <returns>True when the response was truncated by the token limit.</returns>
    public static bool IsTruncation( string? finishReason )
    {
        return finishReason is not null &&
               ( finishReason.Equals( "length", StringComparison.OrdinalIgnoreCase ) ||
                 finishReason.Equals( "max_tokens", StringComparison.OrdinalIgnoreCase ) ||
                 finishReason.Equals( "max_output_tokens", StringComparison.OrdinalIgnoreCase ) );
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>A complete think or thinking block, across lines.</summary>
    [GeneratedRegex( @"<(think|thinking)>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline )]
    private static partial Regex BalancedBlock();

    /// <summary>The last stray closing tag left after balanced blocks are removed.</summary>
    [GeneratedRegex( @"</(think|thinking)>", RegexOptions.IgnoreCase | RegexOptions.RightToLeft )]
    private static partial Regex LastCloseTag();

    /// <summary>An opening tag with no matching close.</summary>
    [GeneratedRegex( @"<(think|thinking)>", RegexOptions.IgnoreCase )]
    private static partial Regex OpenTag();

    #endregion Private Methods
}

/// <summary>Result of separating an answer from reasoning text.</summary>
/// <param name="Answer">Final answer text, trimmed. Empty when nothing but reasoning was returned.</param>
/// <param name="ReasoningStripped">True when any reasoning text was removed.</param>
/// <param name="ReasoningUnfinished">True when reasoning started but never closed, i.e. the budget ran out mid-thought.</param>
public readonly record struct CleanedAnswer( string Answer, bool ReasoningStripped, bool ReasoningUnfinished );
