using System.Text;
using LLMQuorum.Core.Models;
using LLMQuorum.Core.Providers;

namespace LLMQuorum.Tests;

/// <summary>
/// Covers how Claude CLI output becomes a panel answer. The CLI reports most
/// failures with exit code 0 and the reason in the result field, so these cases
/// guard against an error message being clustered as if it were an answer.
/// Output shapes are taken from real calls made on 2026-09-16.
/// </summary>
public sealed class ClaudeCliProviderTests
{
    #region Data Members

    private static readonly ProviderDefinition _definition = new()
    {
        Key = "anthropic",
        DisplayName = "Claude",
        ModelId = "claude-opus-5",
        Lab = "Anthropic",
        Endpoint = "auto",
        ApiKeyFile = string.Empty,
        Protocol = ProviderProtocol.ClaudeCli
    };

    #endregion Data Members

    #region Public Methods

    /// <summary>A normal result is a success, with cached prompt tokens counted as prompt tokens.</summary>
    [Fact]
    public void Success_ParsesTextAndTokens()
    {
        var answer = Parse( 0, @"{""type"":""result"",""subtype"":""success"",""is_error"":false,
            ""result"":""Single\nHead of Household"",
            ""usage"":{""input_tokens"":2,""cache_creation_input_tokens"":2007,""cache_read_input_tokens"":0,""output_tokens"":638}}" );

        Assert.True( answer.IsSuccess );
        Assert.Equal( "Single\nHead of Household", answer.AnswerText );
        Assert.Equal( 2009, answer.PromptTokens );
        Assert.Equal( 638, answer.CompletionTokens );
    }

    /// <summary>
    /// MEASURED failure shape: exit 0, subtype "success", is_error true, reason in
    /// result. It must be an Auth failure, never an answer.
    /// </summary>
    [Fact]
    public void ExpiredLogin_IsAuthFailure()
    {
        var answer = Parse( 0, @"{""type"":""result"",""subtype"":""success"",""is_error"":true,
            ""result"":""Failed to authenticate: OAuth session expired and could not be refreshed""}" );

        Assert.False( answer.IsSuccess );
        Assert.Equal( CallErrorClass.Auth, answer.ErrorClass );
        Assert.Null( answer.AnswerText );
    }

    /// <summary>Hitting the plan's usage limit is a rate limit, so the profile does not blame the model.</summary>
    [Fact]
    public void UsageLimit_IsRateLimit()
    {
        var answer = Parse( 0, @"{""is_error"":true,""result"":""Claude usage limit reached. Your limit will reset at 5pm.""}" );

        Assert.Equal( CallErrorClass.RateLimit, answer.ErrorClass );
    }

    /// <summary>
    /// MEASURED shape: a web-search seat out of turns has no result, subtype error_max_turns and the
    /// reason only in errors[]. It is a limit hit (Truncated) with the reason preserved.
    /// </summary>
    [Fact]
    public void MaxTurns_IsTruncatedWithReason()
    {
        var answer = Parse( 0, @"{""type"":""result"",""subtype"":""error_max_turns"",""is_error"":true,""num_turns"":13,
            ""errors"":[""Reached maximum number of turns (12)""],""terminal_reason"":""max_turns""}" );

        Assert.Equal( CallErrorClass.Truncated, answer.ErrorClass );
        Assert.Contains( "Reached maximum number of turns", answer.ErrorText );
    }

    /// <summary>A blank result is Empty rather than a zero-length vote.</summary>
    [Fact]
    public void BlankResult_IsEmpty()
    {
        var answer = Parse( 0, @"{""is_error"":false,""result"":""   ""}" );

        Assert.False( answer.IsSuccess );
        Assert.Equal( CallErrorClass.Empty, answer.ErrorClass );
    }

    /// <summary>Non-JSON output, such as a CLI argument error, is a failure with the text kept for diagnosis.</summary>
    [Fact]
    public void NonJsonOutput_IsFailure()
    {
        var answer = Parse( 1, "error: option '--tools <tools...>' argument missing" );

        Assert.False( answer.IsSuccess );
        Assert.Contains( "argument missing", answer.ErrorText );
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>Runs the parser over a scripted CLI output.</summary>
    /// <param name="exitCode">Scripted process exit code.</param>
    /// <param name="stdout">Scripted standard output.</param>
    /// <returns>The parsed answer.</returns>
    private static ProviderAnswer Parse( int exitCode, string stdout )
    {
        return ClaudeCliProvider.ParseCliOutput( _definition, 6, 1000, exitCode, Encoding.UTF8.GetBytes( stdout ), string.Empty );
    }

    #endregion Private Methods
}
