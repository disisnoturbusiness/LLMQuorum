using System.Text;
using LLMQuorum.Core.Sweep;

namespace LLMQuorum.Tests;

/// <summary>
/// Extraction and retry rules, each built from a response shape captured live on 2026-09-16.
/// These are the rules whose absence let truncated answers be graded as wrong and let
/// unretryable 429s burn daily allowances.
/// </summary>
public sealed class SweepHarnessTests
{
    #region Data Members

    private static readonly ModelProfile _openAi = new() { Provider = "groq", ModelId = "m", BaseModel = "m", Think = ThinkLocation.SeparateField };

    private static readonly ModelProfile _qwq = new()
    {
        Provider = "cloudflare", ModelId = "@cf/qwen/qwq-32b", BaseModel = "qwq-32b",
        Think = ThinkLocation.InContentClosedTag, FinishReasonLiesAtCap = true
    };

    private static readonly ModelProfile _cohereV2 = new() { Provider = "cohere", ModelId = "c", BaseModel = "c", Shape = ApiShape.CohereV2 };

    private static readonly Dictionary<string, string> _noHeaders = new( StringComparer.OrdinalIgnoreCase );

    #endregion Data Members

    #region Public Methods

    /// <summary>Groq gpt-oss with effort omitted: reasoning filled the cap, content empty, finish length.</summary>
    [Fact]
    public void LengthWithEmptyContent_IsThinkingExhausted()
    {
        var x = Extract( _openAi, @"{""choices"":[{""message"":{""content"":"""",""reasoning"":""We need to...""},""finish_reason"":""length""}],""usage"":{""completion_tokens"":1000}}", 1000 );
        Assert.Equal( "ThinkingExhausted", x.Status );
        Assert.True( x.ReasoningEvidence );
    }

    /// <summary>Groq qwen at a tiny cap: a partial list with finish length is Truncated, never Answered.</summary>
    [Fact]
    public void LengthWithPartialContent_IsTruncated()
    {
        var x = Extract( _openAi, @"{""choices"":[{""message"":{""content"":""Single\nMarried\n""},""finish_reason"":""length""}]}", 5 );
        Assert.Equal( "Truncated", x.Status );
        Assert.Equal( "Single\nMarried", x.Answer );
    }

    /// <summary>Cloudflare vLLM on truncation returns content as JSON null, which used to crash the parser.</summary>
    [Fact]
    public void NullContent_DoesNotThrow()
    {
        var x = Extract( _openAi, @"{""choices"":[{""message"":{""content"":null,""reasoning_content"":""thinking""},""finish_reason"":""length""}]}", 60 );
        Assert.Equal( "ThinkingExhausted", x.Status );
    }

    /// <summary>qwq-32b: reasoning, a closing tag only, then the answer.</summary>
    [Fact]
    public void ClosingTagOnly_KeepsTextAfterIt()
    {
        var x = Extract( _qwq, @"{""choices"":[{""message"":{""content"":""Okay, so I need to...\n</think>\n\nSingle  \nMarried""},""finish_reason"":""stop""}],""usage"":{""completion_tokens"":1287}}", 8000 );
        Assert.Equal( "Answered", x.Status );
        Assert.Equal( "Single\nMarried", x.Answer );
    }

    /// <summary>qwq-32b reports stop at exactly the cap with no closing tag: still thinking, no answer.</summary>
    [Fact]
    public void StopAtCapWithoutCloseTag_IsThinkingExhausted()
    {
        var x = Extract( _qwq, @"{""choices"":[{""message"":{""content"":""Okay, so I need to figure out""},""finish_reason"":""stop""}],""usage"":{""completion_tokens"":30}}", 30 );
        Assert.Equal( "ThinkingExhausted", x.Status );
    }

    /// <summary>Cohere v2: answer is the text blocks; thinking blocks are stored but not graded.</summary>
    [Fact]
    public void CohereV2_TextBlocksOnly()
    {
        var x = Extract( _cohereV2, @"{""message"":{""role"":""assistant"",""content"":[{""type"":""thinking"",""thinking"":""hmm""},{""type"":""text"",""text"":""Single\nHead of Household""}]},""finish_reason"":""COMPLETE"",""usage"":{""tokens"":{""input_tokens"":65,""output_tokens"":129,""reasoning_tokens"":100}}}", 4096 );
        Assert.Equal( "Answered", x.Status );
        Assert.Equal( "Single\nHead of Household", x.Answer );
        Assert.Equal( 100, x.ReasoningTokens );
    }

    /// <summary>Cohere v2 MAX_TOKENS is truncation.</summary>
    [Fact]
    public void CohereV2_MaxTokens_IsTruncated()
    {
        var x = Extract( _cohereV2, @"{""message"":{""content"":[{""type"":""text"",""text"":""Single""}]},""finish_reason"":""MAX_TOKENS""}", 8 );
        Assert.Equal( "Truncated", x.Status );
    }

    /// <summary>Z.ai error code arrives as a string.</summary>
    [Fact]
    public void ZaiStringErrorCode_IsRead()
    {
        var x = Extract( _openAi, @"{""error"":{""code"":""1305"",""message"":""The service may be temporarily overloaded""}}", 100 );
        Assert.Equal( "Error", x.Status );
        Assert.Equal( "1305", x.ErrorCode );
    }

    /// <summary>Cloudflare errors[0].code shape.</summary>
    [Fact]
    public void CloudflareErrorsArray_IsRead()
    {
        var x = Extract( _openAi, @"{""success"":false,""errors"":[{""code"":3036,""message"":""daily free allocation exceeded""}]}", 100 );
        Assert.Equal( "3036", x.ErrorCode );
    }

    /// <summary>MEASURED sweep 7: Cloudflare qwen2.5-coder returned the content as a JSON number.</summary>
    [Fact]
    public void NumericContent_IsAnswered()
    {
        var x = Extract( _openAi, @"{""choices"":[{""message"":{""role"":""assistant"",""content"":17.31},""finish_reason"":""stop""}],""usage"":{""completion_tokens"":6}}", 1024 );
        Assert.Equal( "Answered", x.Status );
        Assert.Equal( "17.31", x.Answer );
    }

    /// <summary>Refusals are answered but flagged so they never count as a vote.</summary>
    [Fact]
    public void Refusal_IsFlagged()
    {
        var x = Extract( _openAi, @"{""choices"":[{""message"":{""content"":""I'm sorry, but I can't provide that information.""},""finish_reason"":""stop""}]}", 4096 );
        Assert.True( x.IsRefusal );
    }

    /// <summary>MEASURED: a web-search seat appended a Sources block. It is cut before grading.</summary>
    [Fact]
    public void SourcesBlock_IsCutFromAnswer()
    {
        var normalized = ResponseExtractor.Normalize( "Single\nHead of Household\n\nSources:\n- [Form G-4](https://example.gov/g4)" );
        Assert.Equal( "Single\nHead of Household", normalized );
    }

    /// <summary>MEASURED sweep 3: sonnet web-search put the citations inline on one "Sources:" line.</summary>
    [Fact]
    public void InlineSourcesLine_IsCutFromAnswer()
    {
        var normalized = ResponseExtractor.Normalize( "Single\nHead of Household\nSources: [DOR Form G-4](https://example.gov/g4), [HR](https://example.edu)" );
        Assert.Equal( "Single\nHead of Household", normalized );
    }

    /// <summary>MEASURED sweep 3: the CLI path used its own narrower check and graded this as a wrong answer.</summary>
    [Fact]
    public void CliStyleDecline_IsRefusal()
    {
        Assert.True( ResponseExtractor.IsRefusal( "I can't access the current Georgia Form G-4 directly to verify the exact labels as printed." ) );
    }

    /// <summary>Groq OTPM rejection where the request exceeds the limit is a cap fix, not a wait.</summary>
    [Fact]
    public void GroqOtpmOverLimit_RetriesWithCap()
    {
        var d = Decide( "groq", 429, @"{""error"":{""message"":""Request too large for model on output tokens per minute (OTPM): Limit 1000, Requested 1001. reduce max_tokens"",""code"":""rate_limit_exceeded""}}" );
        Assert.Equal( ErrorAction.RetryWithCap, d.Action );
        Assert.Equal( 1000, d.NewCap );
    }

    /// <summary>Cloudflare 3036 stops the provider for the day instead of retrying.</summary>
    [Fact]
    public void Cloudflare3036_StopsForDay()
    {
        var d = Decide( "cloudflare", 429, @"{""success"":false,""errors"":[{""code"":3036,""message"":""You have used up your daily free allocation of 10,000 neurons""}]}" );
        Assert.Equal( ErrorAction.StopProviderForDay, d.Action );
    }

    /// <summary>OpenRouter's own 429 is the free daily cap; an upstream provider 429 is worth waiting on.</summary>
    [Theory]
    [InlineData( @"{""error"":{""message"":""Rate limit exceeded: free-models-per-day"",""code"":429}}", ErrorAction.StopProviderForDay )]
    [InlineData( @"{""error"":{""message"":""Provider returned error"",""code"":429,""metadata"":{""raw"":""upstream rate limited""}}}", ErrorAction.RetryAfterWait )]
    [InlineData( @"{""error"":{""message"":""only available on agentic harnesses"",""code"":403}}", ErrorAction.BlockModel )]
    [InlineData( @"{""error"":{""message"":""Rate limit exceeded: free-models-per-min"",""code"":429}}", ErrorAction.RetryAfterWait )]
    public void OpenRouter429Kinds( string body, ErrorAction expected )
    {
        Assert.Equal( expected, Decide( "openrouter", 200, body ).Action );
    }

    /// <summary>
    /// OpenRouter's own 429 names the limit it hit in X-RateLimit-Limit: the 20-per-minute free limit waits for
    /// the reset, the 1,000-per-day cap stops for the day. A per-minute hit must never cost a day.
    /// </summary>
    [Theory]
    [InlineData( "20", ErrorAction.RetryAfterWait )]
    [InlineData( "1000", ErrorAction.StopProviderForDay )]
    public void OpenRouterOwn429_UsesLimitHeader( string limit, ErrorAction expected )
    {
        var headers = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase )
        {
            ["x-ratelimit-limit"] = limit,
            ["x-ratelimit-reset"] = DateTimeOffset.UtcNow.AddSeconds( 30 ).ToUnixTimeMilliseconds().ToString( System.Globalization.CultureInfo.InvariantCulture )
        };
        var body = @"{""error"":{""code"":429,""message"":""Rate limit exceeded"",""metadata"":{""error_type"":""rate_limit_exceeded""}}}";
        var decision = ErrorClassifier.Decide( "openrouter", 429, headers, ResponseExtractor.Extract( _openAi, Encoding.UTF8.GetBytes( body ), 100 ) );

        Assert.Equal( expected, decision.Action );
        if( expected == ErrorAction.RetryAfterWait )
        {
            Assert.InRange( decision.WaitSeconds, 10, 120 );
        }
    }

    /// <summary>Z.ai 1210 carries the real max_tokens ceiling in the message.</summary>
    [Fact]
    public void Zai1210_ParsesCeiling()
    {
        var d = Decide( "zai", 400, "{\"error\":{\"code\":\"1210\",\"message\":\"The max_tokens parameter is illegal.：限制数值范围[1,98304]\"}}" );
        Assert.Equal( ErrorAction.RetryWithCap, d.Action );
        Assert.Equal( 98304, d.NewCap );
    }

    /// <summary>The loader refuses paid Z.ai models and non-free OpenRouter ids.</summary>
    [Theory]
    [InlineData( "zai", "glm-5.3" )]
    [InlineData( "openrouter", "z-ai/glm-5.2" )]
    [InlineData( "cloudflare", "@cf/zai-org/glm-5.3" )]
    public void PaidModels_RefusedAtLoad( string provider, string model )
    {
        Assert.Throws<InvalidDataException>( () => ProfileCatalog.Validate( new ModelProfile { Provider = provider, ModelId = model, BaseModel = model } ) );
    }

    #endregion Public Methods

    #region Private Methods

    private static ExtractedResponse Extract( ModelProfile profile, string json, int cap ) =>
        ResponseExtractor.Extract( profile, Encoding.UTF8.GetBytes( json ), cap );

    private static ErrorDecision Decide( string provider, int status, string json ) =>
        ErrorClassifier.Decide( provider, status, _noHeaders, ResponseExtractor.Extract( _openAi, Encoding.UTF8.GetBytes( json ), 100 ) );

    #endregion Private Methods
}
