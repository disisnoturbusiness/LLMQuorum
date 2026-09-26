using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMQuorum.Core.Sweep;

namespace LLMQuorum.Tests;

/// <summary>
/// The paid lane (2026-09-18): real money, so every guard gets a test. A paid seat must be an approved model on its
/// own key, pinned to one endpoint, priced for the dollar cap; free seats must never carry paid-only settings.
/// </summary>
public sealed class PaidSeatTests
{
    #region Data Members

    private const string OPENROUTER = "https://openrouter.ai/api/v1/chat/completions";

    private static readonly Dictionary<string, string> _noHeaders = new( StringComparer.OrdinalIgnoreCase );

    #endregion Data Members

    #region Public Methods

    /// <summary>A correctly built paid memory seat and web seat both load.</summary>
    [Fact]
    public void ValidPaidSeats_Load()
    {
        ProfileCatalog.Validate( Memory() );
        ProfileCatalog.Validate( Web() );
    }

    /// <summary>Each broken paid seat is refused at load, before any call.</summary>
    [Theory]
    [InlineData( "notApproved" )]
    [InlineData( "freeKey" )]
    [InlineData( "noPrice" )]
    [InlineData( "noPin" )]
    [InlineData( "fallbacks" )]
    [InlineData( "notPaid" )]
    [InlineData( "memoryWithTools" )]
    [InlineData( "webWithoutTool" )]
    [InlineData( "webWithoutBound" )]
    [InlineData( "overridesModel" )]
    [InlineData( "serviceTier" )]
    [InlineData( "wrongHost" )]
    [InlineData( "webWithoutMaxCost" )]
    [InlineData( "reasoningSetsModel" )]
    public void BrokenPaidSeats_AreRefused( string broken )
    {
        var seat = broken switch
        {
            "notApproved" => Memory( modelId: "openai/gpt-5.5" ),
            "freeKey" => Memory( keyFile: "openrouter.key" ),
            "noPrice" => Memory( priceIn: null ),
            "noPin" => Memory( pinned: null ),
            "fallbacks" => Memory( extra: new JsonObject { ["provider"] = new JsonObject { ["only"] = new JsonArray( "openai" ), ["allow_fallbacks"] = true } } ),
            "notPaid" => Memory( paid: false ),
            "memoryWithTools" => Memory( extra: WebExtra() ),
            "webWithoutTool" => Web( extra: Pin() ),
            "webWithoutBound" => Web( bound: 0 ),
            "overridesModel" => Memory( extra: new JsonObject { ["provider"] = PinNode(), ["model"] = "openai/gpt-6-astra" } ),
            "serviceTier" => Memory( extra: new JsonObject { ["provider"] = PinNode(), ["service_tier"] = "priority" } ),
            "wrongHost" => Memory( endpoint: "https://api.groq.com/openai/v1/chat/completions" ),
            "webWithoutMaxCost" => Web( extra: new JsonObject { ["provider"] = PinNode(), ["tools"] = Tools() } ),
            "reasoningSetsModel" => Memory( reasoning: new JsonObject { ["model"] = "openai/gpt-5.5-pro" } ),
            _ => throw new ArgumentOutOfRangeException( nameof( broken ) )
        };

        Assert.Throws<InvalidDataException>( () => ProfileCatalog.Validate( seat ) );
    }

    /// <summary>
    /// Paid-only settings on a free seat, the paid key under a free label, a free label calling OpenRouter, a reasoning
    /// fragment that swaps the model, and unknown providers are all refused. A clean free seat still loads.
    /// </summary>
    [Fact]
    public void FreeSeats_CannotCarryPaidSettings()
    {
        ProfileCatalog.Validate( Free() );

        Assert.Throws<InvalidDataException>( () => ProfileCatalog.Validate( Free( paid: true ) ) );
        Assert.Throws<InvalidDataException>( () => ProfileCatalog.Validate( Free( extra: Pin() ) ) );
        Assert.Throws<InvalidDataException>( () => ProfileCatalog.Validate( Free( keyFile: ModelProfile.PAID_KEY_FILE ) ) );
        Assert.Throws<InvalidDataException>( () => ProfileCatalog.Validate(
            Free( reasoning: new JsonObject { ["model"] = "openai/gpt-6-astra", ["plugins"] = new JsonArray( new JsonObject { ["id"] = "web" } ) } ) ) );
        Assert.Throws<InvalidDataException>( () => ProfileCatalog.Validate(
            new ModelProfile { Provider = "groq", ModelId = "openai/gpt-6-astra", BaseModel = "a", Endpoint = OPENROUTER, KeyFile = ModelProfile.PAID_KEY_FILE } ) );
        Assert.Throws<InvalidDataException>( () => ProfileCatalog.Validate(
            new ModelProfile { Provider = "somethingnew", ModelId = "m", BaseModel = "m", Endpoint = OPENROUTER, KeyFile = "openrouter.key" } ) );
    }

    /// <summary>The checked-in profiles.json loads, with the 21 approved paid seats and nothing else paid.</summary>
    [Fact]
    public void CheckedInProfiles_LoadWithTwentyOnePaidSeats()
    {
        var path = Path.GetFullPath( Path.Combine( AppContext.BaseDirectory, "..", "..", "..", "..", "profiles.json" ) );
        var profiles = ProfileCatalog.Load( path );
        var paid = profiles.Where( p => p.Paid ).ToList();

        Assert.Equal( 21, paid.Count );
        Assert.All( paid, p => Assert.Equal( ModelProfile.PAID_PROVIDER, p.Provider ) );
        Assert.Single( paid, p => p.ModelId == "perplexity/sonar" );
        Assert.DoesNotContain( profiles, p => !p.Paid && p.Provider == ModelProfile.PAID_PROVIDER );
    }

    /// <summary>
    /// The estimate prices each turn on the tier its prompt lands in (2026-09-20: OpenAI doubles at 272,000 prompt
    /// tokens, Google and xAI at 200,000), takes the full output cap, and for web seats the larger of the input bound
    /// plus every search and the request's own max_cost stop plus one more turn. The input bound never exceeds the
    /// model's own context window. The exact figures are in the asserts below.
    /// </summary>
    [Fact]
    public void WorstCase_CoversLongContextAndTheToolLoop()
    {
        // Web astra: 150,000 x $10/M + 16,384 x $50/M + 5 searches x $0.01 = $2.3692, over the $0.50 stop plus a turn.
        Assert.Equal( 2.3692m, SpendGate.WorstCaseUsd( Web( modelId: "openai/gpt-6-astra", priceIn: 10m, priceOut: 50m ), 40, 16384 ) );
        Assert.Equal( 0.8196m, SpendGate.WorstCaseUsd( Memory( modelId: "openai/gpt-6-astra", priceIn: 10m, priceOut: 50m ), 40, 16384 ) );
        // Luna web: the searched figure ($0.0996608) is under its own max_cost stop plus one full turn, so that wins.
        Assert.Equal( 0.5196688m, SpendGate.WorstCaseUsd( Web(), 40, 16384 ) );

        // A bound past the long-context threshold is priced at the long-context rate, not the base one.
        var longAstra = Web( modelId: "openai/gpt-6-astra", priceIn: 10m, priceOut: 50m, bound: 300_000,
                             longThreshold: 272_000, longIn: 20m, longOut: 75m );

        // 300,000 x $20/M + 16,384 x $75/M + $0.05 = $7.2788.
        Assert.Equal( 7.2788m, SpendGate.WorstCaseUsd( longAstra, 40, 16384 ) );

        // A prompt under the threshold still bills at the base rate.
        Assert.Equal( 0.8196m, SpendGate.WorstCaseUsd(
            Memory( modelId: "openai/gpt-6-astra", priceIn: 10m, priceOut: 50m, longThreshold: 272_000, longIn: 20m, longOut: 75m ), 40, 16384 ) );
    }

    /// <summary>Actual cost takes the larger of usage.cost and tokens plus searches, so a missing search fee is still counted.</summary>
    [Fact]
    public void ActualCost_CountsSearchFeesEitherWay()
    {
        var web = Web( modelId: "openai/gpt-5.6-sol", priceIn: 2m, priceOut: 10m );
        var withoutFees = new ExtractedResponse( "Answered", "a", null, "a", "stop", 10_000, 1_000, null, false, false, null, null, null, 0.03m )
            { WebSearchRequests = 2 };

        // tokens: 10,000 x $2/M + 1,000 x $10/M = $0.03; plus 2 searches x $0.01 = $0.05 > usage.cost $0.03.
        Assert.Equal( 0.05m, SpendGate.ActualUsd( web, withoutFees ) );
        Assert.Equal( 0.05m, SpendGate.ActualUsd( web, withoutFees with { Cost = 0.05m } ) );
    }

    /// <summary>Inline citations are removed from web answers before grading; RawContent keeps them.</summary>
    [Theory]
    [InlineData( "16100 ([irs.gov](https://www.irs.gov/newsroom/x))", "16100" )]
    [InlineData( "The deduction is $16,100 ([IRS](https://irs.gov/a), [SSA](https://ssa.gov/b)).", "The deduction is $16,100." )]
    [InlineData( "See [IRS Publication 15](https://www.irs.gov/p15) for 16100.", "See IRS Publication 15 for 16100." )]
    [InlineData( "Single[1]|Head of Household[1][2]", "Single|Head of Household" )]
    [InlineData( "C# 14 [3].", "C# 14." )]
    [InlineData( "72.5 [irs.gov](https://www.irs.gov/a)", "72.5" )]
    [InlineData( "72.5 [[1]](https://x.com/a)", "72.5" )]
    [InlineData( "72.5 ([Wikipedia](https://en.wikipedia.org/wiki/Mileage_(tax)))", "72.5" )]
    [InlineData( "72.5 (https://en.wikipedia.org/wiki/Mileage_(tax))", "72.5" )]
    public void WebAnswers_LoseInlineCitations( string raw, string expected )
    {
        var content = raw.Replace( "|", "\n" );
        var x = ResponseExtractor.Extract( Web(), Body( content ), 16384 );

        Assert.Equal( expected.Replace( "|", "\n" ), x.Answer );
        Assert.Equal( content, x.RawContent );
    }

    /// <summary>Memory answers are left alone: a bracketed number there is content, not a citation.</summary>
    [Fact]
    public void MemoryAnswers_KeepBrackets()
    {
        Assert.Equal( "x[1] is the second element", ResponseExtractor.Extract( Memory(), Body( "x[1] is the second element" ), 1000 ).Answer );
    }

    /// <summary>The extractor reads the served provider, generation id and search count OpenRouter returns.</summary>
    [Fact]
    public void Extractor_ReadsServedProviderAndSearches()
    {
        const string BODY = @"{""id"":""gen-123"",""provider"":""OpenAI"",""choices"":[{""message"":{""content"":""16100""},""finish_reason"":""stop""}],
""usage"":{""prompt_tokens"":900,""completion_tokens"":40,""cost"":0.012,""server_tool_use"":{""web_search_requests"":1}}}";
        var x = ResponseExtractor.Extract( Web(), Encoding.UTF8.GetBytes( BODY ), 16384 );

        Assert.Equal( "Answered", x.Status );
        Assert.Equal( "OpenAI", x.ServedProvider );
        Assert.Equal( "gen-123", x.GenerationId );
        Assert.Equal( 1, x.WebSearchRequests );
        Assert.Equal( 0.012m, x.Cost );
    }

    /// <summary>The search count is read from server_tool_use_details (the chat-completions schema name) as well.</summary>
    [Fact]
    public void Extractor_ReadsServerToolUseDetails()
    {
        const string BODY = @"{""id"":""gen-2"",""provider"":""xAI"",""choices"":[{""message"":{""content"":""7.25""},""finish_reason"":""stop""}],
""usage"":{""prompt_tokens"":900,""completion_tokens"":40,""cost"":0.01,""server_tool_use_details"":{""web_search_requests"":3,""tool_calls_executed"":3}}}";
        Assert.Equal( 3, ResponseExtractor.Extract( Web(), Encoding.UTF8.GetBytes( BODY ), 16384 ).WebSearchRequests );
    }

    /// <summary>An OpenRouter error body inside HTTP 200 keeps its generation id, so the paid lane can settle from it.</summary>
    [Fact]
    public void ErrorBody_KeepsGenerationId()
    {
        const string BODY = @"{""id"":""gen-err-1"",""provider"":""OpenAI"",""error"":{""code"":502,""message"":""Upstream error""}}";
        var x = ResponseExtractor.Extract( Web(), Encoding.UTF8.GetBytes( BODY ), 100 );

        Assert.Equal( "Error", x.Status );
        Assert.Equal( "gen-err-1", x.GenerationId );
    }

    /// <summary>
    /// Paid errors, one attempt each: a 402 (credits, key limit, in-flight budget) stops the lane for the run; 429, 5xx
    /// and a context error are recorded and never retried; gated or an unmet pin blocks the seat.
    /// </summary>
    [Theory]
    [InlineData( 402, @"{""error"":{""code"":402,""message"":""Insufficient credits"",""metadata"":{""limit_source"":""openrouter_key_limit""}}}", ErrorAction.StopProvider )]
    [InlineData( 429, @"{""error"":{""code"":429,""message"":""Rate limit exceeded""}}", ErrorAction.GiveUp )]
    [InlineData( 429, @"{""error"":{""code"":429,""message"":""Provider returned error"",""metadata"":{""raw"":""temporarily rate-limited upstream"",""limit_source"":""upstream_provider_shared_pool""}}}", ErrorAction.BlockModel )]
    [InlineData( 400, @"{""error"":{""code"":400,""message"":""maximum context length is 32768 tokens""}}", ErrorAction.GiveUp )]
    [InlineData( 503, @"{""error"":{""code"":503,""message"":""No endpoints found that can handle the requested parameters and routing""}}", ErrorAction.BlockModel )]
    [InlineData( 502, @"{""error"":{""code"":502,""message"":""Upstream error""}}", ErrorAction.GiveUp )]
    [InlineData( 404, @"{""error"":{""code"":404,""message"":""No allowed providers are available for the selected model.""}}", ErrorAction.BlockModel )]
    [InlineData( 402, @"{""error"":{""code"":402,""message"":""Request exceeds in-flight budget"",""metadata"":{""reason"":""in_flight_budget_exhausted"",""limit_source"":""openrouter_in_flight_budget""}}}", ErrorAction.StopProvider )]
    [InlineData( 402, @"{""error"":{""code"":402,""message"":""Request too large"",""metadata"":{""reason"":""weight_exceeds_budget"",""limit_source"":""openrouter_credits""}}}", ErrorAction.BlockModel )]
    public void PaidErrors_AreClassifiedForMoney( int status, string body, ErrorAction expected )
    {
        var x = ResponseExtractor.Extract( Web(), Encoding.UTF8.GetBytes( body ), 100 );
        Assert.Equal( expected, ErrorClassifier.Decide( ModelProfile.PAID_PROVIDER, status, _noHeaders, x ).Action );
    }

    /// <summary>A 402 on the free lane (negative shared balance) stops it instead of failing seat by seat.</summary>
    [Fact]
    public void Free402_StopsProvider()
    {
        var x = ResponseExtractor.Extract( Web(), Encoding.UTF8.GetBytes( @"{""error"":{""code"":402,""message"":""Insufficient credits""}}" ), 100 );
        Assert.Equal( ErrorAction.StopProvider, ErrorClassifier.Decide( "openrouter", 402, _noHeaders, x ).Action );
    }

    /// <summary>A seat the spend ledger shows as asked is final, answered or not; the item is done and the note says why.</summary>
    [Fact]
    public void PaidItems_AskedSeatsAreFinal()
    {
        var eval = PlanRules.Evaluate( new[]
        {
            new SeatOutcome( "a", ModelProfile.PAID_PROVIDER, "Answered", null, null ),
            new SeatOutcome( "c", ModelProfile.PAID_PROVIDER, "Timeout", "no response in 420s", null ),
            new SeatOutcome( "e", ModelProfile.PAID_PROVIDER, "Error", "GiveUp: 502 Upstream error", null )
        }, ModelProfile.PAID_PROVIDER, 0, Noon(), Array.Empty<string>() );

        Assert.Equal( "done", eval.Status );
        Assert.Null( eval.NotBeforeUtc );
        Assert.Empty( eval.RetrySeats );
        Assert.Contains( "1 answered", eval.Note );
        Assert.Contains( "asked and billed, no answer, never re-asked by itself: c, e", eval.Note );
    }

    /// <summary>
    /// Seats the lane never sent (balance unreadable, a stop earlier in the run, a voided 402, no outcome at all) are
    /// tried again in 6 hours; seats already asked are not.
    /// </summary>
    [Fact]
    public void PaidItems_LaneRefusals_TryAgainLater()
    {
        var eval = PlanRules.Evaluate( new[]
        {
            new SeatOutcome( "a", ModelProfile.PAID_PROVIDER, "BudgetSkipped", "account balance unreadable (HTTP error); refusing to spend", null ),
            new SeatOutcome( "b", ModelProfile.PAID_PROVIDER, "AlreadyAsked", "already asked", null ),
            new SeatOutcome( "d", ModelProfile.PAID_PROVIDER, "Error", "StopProvider: 402 credits or key limit", null ),
            new SeatOutcome( "f", ModelProfile.PAID_PROVIDER, "ProviderStopped", "402 credits or key limit", null )
        }, ModelProfile.PAID_PROVIDER, 0, Noon(), new[] { "a", "d", "f", "g" } );

        Assert.Equal( "cut-off", eval.Status );
        Assert.Equal( Noon().AddHours( 6 ), eval.NotBeforeUtc );
        Assert.Contains( "trying again in 360 minutes: a, d, f, g", eval.Note );
    }

    /// <summary>
    /// A seat refused by the dollar cap or the key limit is left unasked for good (so it never holds later questions),
    /// and one rejected before routing (unmet pin) waits for a fix; neither loops.
    /// </summary>
    [Fact]
    public void PaidItems_CapAndPreRoutingRejections_AreFinal()
    {
        var eval = PlanRules.Evaluate( new[]
        {
            new SeatOutcome( "a", ModelProfile.PAID_PROVIDER, "Answered", null, null ),
            new SeatOutcome( "b", ModelProfile.PAID_PROVIDER, "BudgetSkipped", "dollar cap: $24.9000 spent or held", null ),
            new SeatOutcome( "h", ModelProfile.PAID_PROVIDER, "BudgetSkipped", "OpenRouter key limit: $0.1000 left", null ),
            new SeatOutcome( "e", ModelProfile.PAID_PROVIDER, "Error", "BlockModel: pin unmet", null )
        }, ModelProfile.PAID_PROVIDER, 0, Noon(), new[] { "b", "h", "e" }, new[] { "k" } );

        Assert.Equal( "done", eval.Status );
        Assert.Contains( "needs a fix: e", eval.Note );
        // Seats the plan runner never sent for want of dollars are listed with the ones the gate refused.
        Assert.Contains( "not asked, more than the dollars left: k, b, h", eval.Note );
    }

    /// <summary>
    /// Only rejections OpenRouter documents as coming before any provider ran are voided (seat may be asked again):
    /// never a 429, a 5xx, a timeout, or anything that reported usage.
    /// </summary>
    [Theory]
    [InlineData( 404, @"{""error"":{""code"":404,""message"":""No allowed providers are available for the selected model.""}}", true )]
    [InlineData( 401, @"{""error"":{""code"":401,""message"":""User not found.""}}", true )]
    [InlineData( 402, @"{""error"":{""code"":402,""message"":""Key limit exceeded"",""metadata"":{""limit_source"":""openrouter_key_limit""}}}", true )]
    [InlineData( 402, @"{""error"":{""code"":402,""message"":""Insufficient credits""}}", false )]
    [InlineData( 429, @"{""error"":{""code"":429,""message"":""Rate limit exceeded""}}", false )]
    [InlineData( 429, @"{""error"":{""code"":429,""message"":""Provider returned error"",""metadata"":{""limit_source"":""upstream_provider_shared_pool""}}}", true )]
    [InlineData( 502, @"{""error"":{""code"":502,""message"":""Upstream error""}}", false )]
    [InlineData( 400, @"{""error"":{""code"":400,""message"":""bad""},""usage"":{""prompt_tokens"":900,""completion_tokens"":0}}", false )]
    public void PreRoutingRejection_IsNarrow( int status, string body, bool expected )
    {
        var x = ResponseExtractor.Extract( Web(), Encoding.UTF8.GetBytes( body ), 100 );
        var attempt = new CallAttempt { Profile = Web(), ReasoningModeUsed = "default", HttpStatus = status, Extracted = x, Status = "Error" };
        Assert.Equal( expected, SpendGate.IsPreRoutingRejection( attempt ) );
    }

    /// <summary>An unmet pin is voided whichever status OpenRouter returns for it, but a plain 503 is not.</summary>
    [Theory]
    [InlineData( @"{""error"":{""code"":503,""message"":""No endpoints found that can handle the requested parameters and routing""}}", true )]
    [InlineData( @"{""error"":{""code"":503,""message"":""Service temporarily unavailable""}}", false )]
    public void UnmetPin503_IsVoided( string body, bool expected )
    {
        var x = ResponseExtractor.Extract( Web(), Encoding.UTF8.GetBytes( body ), 100 );
        var attempt = new CallAttempt { Profile = Web(), ReasoningModeUsed = "default", HttpStatus = 503, Extracted = x, Status = "Error" };
        Assert.Equal( expected, SpendGate.IsPreRoutingRejection( attempt ) );
    }

    /// <summary>
    /// A provider that fails after the response started arrives as HTTP 200 with finish_reason "error" and partial
    /// text. MEASURED against OpenRouter's error documentation 2026-09-20: it used to be graded as a real answer.
    /// </summary>
    [Fact]
    public void PartialErrorResponse_IsNotAnAnswer()
    {
        const string BODY = @"{""id"":""gen-p1"",""provider"":""OpenAI"",""choices"":[{""message"":{""content"":""The 2026 limit is ""},
""finish_reason"":""error"",""error"":{""code"":500,""message"":""Provider disconnected""}}],""usage"":{""prompt_tokens"":900,""completion_tokens"":12,""cost"":0.02}}";

        var x = ResponseExtractor.Extract( Web(), Encoding.UTF8.GetBytes( BODY ), 1000 );

        Assert.Equal( "Error", x.Status );
        Assert.Null( x.Answer );
        Assert.Equal( "The 2026 limit is ", x.RawContent );
        // The usage is kept, so the paid ledger still settles what the failed call billed.
        Assert.Equal( 900, x.PromptTokens );
        Assert.Equal( 0.02m, x.Cost );
        Assert.Equal( "gen-p1", x.GenerationId );

        var attempt = new CallAttempt { Profile = Web(), ReasoningModeUsed = "default", HttpStatus = 200, Extracted = x, Status = "Error" };
        Assert.False( SpendGate.IsPreRoutingRejection( attempt ) );
    }

    /// <summary>A web seat can never read past its own context window, whatever its input bound says.</summary>
    [Fact]
    public void WebInputBound_NeverExceedsTheContextWindow()
    {
        // Sonar: bound 150,000 against a 127,072-token window and a 4,096 cap, so the priced input is 122,976.
        var sonar = new ModelProfile
        {
            Provider = ModelProfile.PAID_PROVIDER, ModelId = "perplexity/sonar", BaseModel = "sonar", Group = "web-search",
            Endpoint = OPENROUTER, KeyFile = ModelProfile.PAID_KEY_FILE, Paid = true, PriceInUsdPerM = 1m, PriceOutUsdPerM = 1m,
            PinnedProvider = "Perplexity", SearchFeeUsd = 0.005m, MaxSearchesPerCall = 1, WebInputBoundTokens = 150_000,
            MaxOutput = 4096, ContextWindow = 127_072,
            ExtraBody = new JsonObject { ["provider"] = new JsonObject { ["only"] = new JsonArray( "perplexity" ), ["allow_fallbacks"] = false } }
        };

        Assert.Equal( 0.132072m, SpendGate.WorstCaseUsd( sonar, 64, 4096 ) );
    }

    /// <summary>A JSON null "error" member is not a provider failure; only an object or a string is.</summary>
    [Fact]
    public void NullErrorMember_IsStillAnAnswer()
    {
        const string BODY = @"{""id"":""gen-n1"",""provider"":""OpenAI"",""choices"":[{""message"":{""content"":""15750""},""finish_reason"":""stop"",""error"":null}],
""usage"":{""prompt_tokens"":900,""completion_tokens"":4}}";

        var x = ResponseExtractor.Extract( Web(), Encoding.UTF8.GetBytes( BODY ), 1000 );

        Assert.Equal( "Answered", x.Status );
        Assert.Equal( "15750", x.Answer );
    }

    /// <summary>
    /// Gemini through Vertex wrote its thinking into content behind an "llm:thought" line and glued the answer to the
    /// end of it. MEASURED 2026-09-20 on question 19; the body below is the one that was stored.
    /// </summary>
    [Fact]
    public void LeakedThought_YieldsTheAnswerNotTheThinking()
    {
        const string BODY = @"{""id"":""gen-1"",""provider"":""Google"",""choices"":[{""finish_reason"":""stop"",""message"":{""content"":
""llm:thought\nThe user wants to know the basic standard deduction for a single filer for the tax year 2026.\nThe user requested *only* the dollar amount.\nI will output just \""$16,100\"".$16,100""}}],""usage"":{""prompt_tokens"":900,""completion_tokens"":233}}";

        var x = ResponseExtractor.Extract( Web( modelId: "google/gemini-3.1-pro-preview", priceIn: 2m, priceOut: 12m ), Encoding.UTF8.GetBytes( BODY ), 1000 );

        Assert.Equal( "Answered", x.Status );
        Assert.Equal( "$16,100", x.Answer );
    }

    /// <summary>An answer that never carried the marker is untouched, marker-shaped text and all.</summary>
    [Fact]
    public void ContentWithoutTheMarker_IsUntouched()
    {
        Assert.Equal( "The model said llm:thought once. $16,100",
            ResponseExtractor.Extract( Web(), Body( "The model said llm:thought once. $16,100" ), 1000 ).Answer );
    }

    /// <summary>A timeout has no status and no body: never voided.</summary>
    [Fact]
    public void PreRoutingRejection_NeverForTimeout()
    {
        var attempt = new CallAttempt { Profile = Web(), ReasoningModeUsed = "default", Status = "Timeout", Decision = "no response in 420s" };
        Assert.False( SpendGate.IsPreRoutingRejection( attempt ) );
    }

    /// <summary>Citation stripping never empties an answer, and it is applied to paid web seats only.</summary>
    [Fact]
    public void CitationStripping_KeepsBareListsAndSparesFreeSeats()
    {
        Assert.Equal( "[2, 3, 5]", ResponseExtractor.Extract( Web(), Body( "[2, 3, 5]" ), 1000 ).Answer );

        var freeWeb = new ModelProfile { Provider = "groq", ModelId = "groq/compound-mini", BaseModel = "c", Group = "web-search" };
        Assert.Equal( "16100 [1]", ResponseExtractor.Extract( freeWeb, Body( "16100 [1]" ), 1000 ).Answer );
    }

    /// <summary>The Sonar seat, which searches on its own, may not carry tools.</summary>
    [Fact]
    public void SearchNativeSeat_TakesNoTools()
    {
        var sonar = new ModelProfile
        {
            Provider = ModelProfile.PAID_PROVIDER, ModelId = "perplexity/sonar", BaseModel = "sonar", Group = "web-search", Endpoint = OPENROUTER,
            KeyFile = ModelProfile.PAID_KEY_FILE, Paid = true, PriceInUsdPerM = 1m, PriceOutUsdPerM = 1m, PinnedProvider = "Perplexity",
            SearchFeeUsd = 0.005m, MaxSearchesPerCall = 1, WebInputBoundTokens = 150_000, MaxOutput = 4096, ContextWindow = 127_072,
            ExtraBody = new JsonObject { ["provider"] = new JsonObject { ["only"] = new JsonArray( "perplexity" ), ["allow_fallbacks"] = false } }
        };

        ProfileCatalog.Validate( sonar );

        var withTools = new ModelProfile
        {
            Provider = sonar.Provider, ModelId = sonar.ModelId, BaseModel = sonar.BaseModel, Group = sonar.Group, Endpoint = sonar.Endpoint,
            KeyFile = sonar.KeyFile, Paid = true, PriceInUsdPerM = 1m, PriceOutUsdPerM = 1m, PinnedProvider = "Perplexity",
            SearchFeeUsd = 0.005m, MaxSearchesPerCall = 1, WebInputBoundTokens = 150_000, MaxOutput = 4096, ContextWindow = 127_072,
            ExtraBody = new JsonObject
            {
                ["provider"] = new JsonObject { ["only"] = new JsonArray( "perplexity" ), ["allow_fallbacks"] = false },
                ["tools"] = Tools()
            }
        };

        Assert.Throws<InvalidDataException>( () => ProfileCatalog.Validate( withTools ) );
    }

    #endregion Public Methods

    #region Private Methods

    private static DateTime Noon() => new( 2026, 9, 18, 12, 0, 0, DateTimeKind.Utc );

    private static byte[] Body( string content ) => Encoding.UTF8.GetBytes(
        $@"{{""choices"":[{{""message"":{{""content"":{JsonSerializer.Serialize( content )}}},""finish_reason"":""stop""}}],""usage"":{{""prompt_tokens"":9,""completion_tokens"":9}}}}" );

    private static JsonObject PinNode() => new() { ["only"] = new JsonArray( "openai" ), ["allow_fallbacks"] = false };

    private static JsonObject Pin() => new() { ["provider"] = PinNode() };

    private static JsonArray Tools() =>
        new( new JsonObject { ["type"] = "openrouter:web_search", ["parameters"] = new JsonObject { ["engine"] = "native" } } );

    private static JsonObject WebExtra() => new()
    {
        ["provider"] = PinNode(),
        ["tools"] = Tools(),
        ["stop_server_tools_when"] = new JsonArray(
            new JsonObject { ["type"] = "step_count_is", ["step_count"] = 5 },
            new JsonObject { ["type"] = "max_cost", ["max_cost_in_dollars"] = 0.5 } )
    };

    private static ModelProfile Free( bool paid = false, JsonObject? extra = null, string keyFile = "openrouter.key", JsonObject? reasoning = null ) => new()
    {
        Provider = "openrouter", ModelId = "x/y:free", BaseModel = "y", Endpoint = OPENROUTER, KeyFile = keyFile,
        Paid = paid, ExtraBody = extra, ReasoningBody = reasoning
    };

    private static ModelProfile Memory( string modelId = "openai/gpt-5.6-luna", string keyFile = ModelProfile.PAID_KEY_FILE,
                                        decimal? priceIn = 0.2m, decimal? priceOut = 1.2m, string? pinned = "OpenAI", bool paid = true,
                                        JsonObject? extra = null, string endpoint = OPENROUTER, JsonObject? reasoning = null,
                                        int? longThreshold = null, decimal? longIn = null, decimal? longOut = null ) => new()
    {
        Provider = ModelProfile.PAID_PROVIDER, ModelId = modelId, BaseModel = modelId, Endpoint = endpoint, KeyFile = keyFile, Paid = paid,
        PriceInUsdPerM = priceIn, PriceOutUsdPerM = priceOut, PinnedProvider = pinned, ExtraBody = extra ?? Pin(), ReasoningBody = reasoning,
        LongPromptThresholdTokens = longThreshold, PriceInLongUsdPerM = longIn, PriceOutLongUsdPerM = longOut,
        MaxOutput = 16384, ContextWindow = 1_050_000, SendTemperature = false, ReasoningMode = "default"
    };

    private static ModelProfile Web( string modelId = "openai/gpt-5.6-luna", decimal? priceIn = 0.2m, decimal? priceOut = 1.2m,
                                     int bound = 150_000, JsonObject? extra = null,
                                     int? longThreshold = null, decimal? longIn = null, decimal? longOut = null ) => new()
    {
        Provider = ModelProfile.PAID_PROVIDER, ModelId = modelId, BaseModel = modelId, Group = "web-search", Endpoint = OPENROUTER,
        KeyFile = ModelProfile.PAID_KEY_FILE, Paid = true, PriceInUsdPerM = priceIn, PriceOutUsdPerM = priceOut, PinnedProvider = "OpenAI",
        ExtraBody = extra ?? WebExtra(), SearchFeeUsd = 0.01m, MaxSearchesPerCall = 5, WebInputBoundTokens = bound,
        LongPromptThresholdTokens = longThreshold, PriceInLongUsdPerM = longIn, PriceOutLongUsdPerM = longOut,
        MaxOutput = 16384, ContextWindow = 1_050_000, SendTemperature = false, ReasoningMode = "default", Think = ThinkLocation.SeparateField
    };

    #endregion Private Methods
}
