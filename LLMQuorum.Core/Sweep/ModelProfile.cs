using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace LLMQuorum.Core.Sweep;

/// <summary>Request/response family a profile speaks.</summary>
public enum ApiShape
{
    /// <summary>OpenAI chat-completions shape: Groq, OpenRouter, Cloudflare, Z.ai, Cohere compatibility API.</summary>
    OpenAiChat,

    /// <summary>Cohere native v2 chat: content is an array of text and thinking blocks.</summary>
    CohereV2,

    /// <summary>Claude Code CLI in print mode on the user's subscription.</summary>
    ClaudeCli
}

/// <summary>Where a model's reasoning text arrives, which decides how the answer is separated from it.</summary>
public enum ThinkLocation
{
    /// <summary>Model does not reason, or reasoning never appears in the payload.</summary>
    None,

    /// <summary>Reasoning arrives in a separate field (message.reasoning, reasoning_content, thinking blocks).</summary>
    SeparateField,

    /// <summary>
    /// Reasoning is written into message.content and closed with a think tag.
    /// MEASURED: Cloudflare qwq-32b and deepseek-r1-distill. No closing tag means no answer.
    /// </summary>
    InContentClosedTag
}

/// <summary>
/// Everything needed to ask one model on one provider a question correctly, keyed by
/// (Provider, ModelId, ReasoningMode) and never by model family.
///
/// Why per provider and model: the 2026-09-16 verification found the SAME weights
/// behaving oppositely on different hosts. qwen3.8-27b does not think when effort is
/// omitted on Groq but defaults to maximum effort on Cloudflare; glm-4.7-flash turns
/// thinking off with chat_template_kwargs on Cloudflare and with thinking.type on Z.ai.
/// A family-keyed profile sends the wrong body on one of the two.
///
/// Profiles are checked in, not discovered: only request shapes verified by a live call
/// belong here.
/// </summary>
public sealed class ModelProfile
{
    #region Constants

    /// <summary>Provider key of the paid lane.</summary>
    public const string PAID_PROVIDER = "openrouter-paid";

    /// <summary>Key file for the paid lane; the free OpenRouter key is never allowed on a paid seat.</summary>
    public const string PAID_KEY_FILE = "openrouter-paid.key";

    #endregion Constants

    #region Public Methods

    /// <summary>Provider key: groq, openrouter, cloudflare, cohere, zai, claude-sub.</summary>
    public required string Provider { get; init; }

    /// <summary>Exact model id sent on the wire.</summary>
    public required string ModelId { get; init; }

    /// <summary>
    /// Identity of the underlying weights, used to stop duplicate weights hosted on two
    /// providers from counting as two independent voters.
    /// </summary>
    public required string BaseModel { get; init; }

    /// <summary>"memory" when the model answers from training, "web-search" when it can look things up.</summary>
    public string Group { get; init; } = "memory";

    /// <summary>Request/response family.</summary>
    public ApiShape Shape { get; init; } = ApiShape.OpenAiChat;

    /// <summary>Chat endpoint. Ignored for ClaudeCli.</summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>Credential file name under the keys directory. Empty for ClaudeCli.</summary>
    public string KeyFile { get; init; } = string.Empty;

    /// <summary>The single output-limit parameter name this model honours: max_tokens or max_completion_tokens.</summary>
    public string TokenParam { get; init; } = "max_tokens";

    /// <summary>Output cap to send, already reduced to the verified free-tier ceiling for this model.</summary>
    public int MaxOutput { get; init; } = 1024;

    /// <summary>Published context window, so the cap never exceeds it once the prompt is added.</summary>
    public int ContextWindow { get; init; } = 32768;

    /// <summary>Label of the reasoning setting sent, stored with every answer: off, low, default, budget:2048, none.</summary>
    public string ReasoningMode { get; init; } = "none";

    /// <summary>Exact body fragment merged into the request to set reasoning. Always sent explicitly when present.</summary>
    public JsonObject? ReasoningBody { get; init; }

    /// <summary>Where reasoning arrives.</summary>
    public ThinkLocation Think { get; init; } = ThinkLocation.None;

    /// <summary>True when the provider reports a normal stop even though the token cap cut the output.</summary>
    public bool FinishReasonLiesAtCap { get; init; }

    /// <summary>
    /// List price in USD per million input tokens. Cloudflare: converted to neurons for the daily guard.
    /// Paid seats: the pinned endpoint's price, used to reserve each attempt's worst case against the dollar cap.
    /// </summary>
    public decimal? PriceInUsdPerM { get; init; }

    /// <summary>List price in USD per million output tokens (Cloudflare neurons; paid-seat worst case).</summary>
    public decimal? PriceOutUsdPerM { get; init; }

    /// <summary>
    /// Paid seats: prompt tokens at or above which the pinned endpoint charges its long-context prices. MEASURED
    /// 2026-09-20 against OpenRouter's endpoints API: 272,000 on the OpenAI endpoints, 200,000 on Google and xAI.
    /// Null when the endpoint has one price. A web search easily crosses it, so the estimate must know about it.
    /// </summary>
    public int? LongPromptThresholdTokens { get; init; }

    /// <summary>Paid seats: input price per million at or above the threshold.</summary>
    public decimal? PriceInLongUsdPerM { get; init; }

    /// <summary>Paid seats: output price per million at or above the threshold.</summary>
    public decimal? PriceOutLongUsdPerM { get; init; }

    /// <summary>
    /// True for a seat that spends real money. Paid seats run only when named by exact seat id, go through the
    /// dollar cap, and never share a provider key or API key with the free seats.
    /// </summary>
    public bool Paid { get; init; }

    /// <summary>
    /// Body fragment merged into every request for this seat, independent of reasoning, so it survives a
    /// reasoning fallback: provider pin, web search tool, tool-loop stop conditions.
    /// </summary>
    public JsonObject? ExtraBody { get; init; }

    /// <summary>Paid seats: USD per web search call (or per request for search-native models such as Sonar).</summary>
    public decimal? SearchFeeUsd { get; init; }

    /// <summary>Paid web seats: most searches one request may make, used for the worst-case reservation.</summary>
    public int MaxSearchesPerCall { get; init; }

    /// <summary>
    /// Paid web seats: input tokens reserved per attempt for the prompt plus everything search pulls in. Search
    /// content cannot be bounded exactly, so this is a planning figure, set per vendor from what they document and
    /// what we have measured (2026-09-20): OpenAI caps its search context at 128k tokens; Google does not bill
    /// grounding content as input at all; xAI publishes no ceiling, so its seats carry the measured p95. The key's
    /// own OpenRouter limit, not this number, is what stops a call that goes past it.
    /// </summary>
    public int WebInputBoundTokens { get; init; }

    /// <summary>Paid seats: the provider_name OpenRouter must report for the served call (e.g. "OpenAI").</summary>
    public string? PinnedProvider { get; init; }

    /// <summary>Whether temperature 0 is sent. Some models reject sampling parameters.</summary>
    public bool SendTemperature { get; init; } = true;

    /// <summary>Per-request timeout. OpenRouter free thinking models took over 100 s in verification.</summary>
    public int TimeoutSeconds { get; init; } = 120;

    /// <summary>Minimum pause after this call before the next call to the same provider.</summary>
    public int SpacingMs { get; init; } = 1500;

    /// <summary>Daily request ceiling for this model, from provider headers, when the provider meters per model.</summary>
    public int? DailyRequestLimit { get; init; }

    /// <summary>Disabled profiles stay documented but are never called.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Claude CLI tools, e.g. "WebSearch,WebFetch". Empty string disables all tools.</summary>
    public string? CliTools { get; init; }

    /// <summary>Claude CLI max turns; web search needs several.</summary>
    public int CliMaxTurns { get; init; } = 1;

    /// <summary>Why this profile looks the way it does, with the verification evidence.</summary>
    public string? Notes { get; init; }

    /// <summary>Stable seat identity: provider, model and reasoning mode.</summary>
    [JsonIgnore]
    public string SeatId => $"{Provider}|{ModelId}|{Group}|{ReasoningMode}";

    #endregion Public Methods
}

/// <summary>
/// Loads profiles.json and refuses to start when a profile could spend money or send a
/// malformed body. Guards are applied at load time so a bad edit fails loudly before
/// any call is made.
/// </summary>
public static class ProfileCatalog
{
    #region Data Members

    /// <summary>The only Z.ai models that are free. glm-4.5/4.6/4.7 and every glm-5 are paid.</summary>
    private static readonly HashSet<string> _zaiFreeModels = new( StringComparer.Ordinal )
    {
        "glm-4.7-flash", "glm-4.5-flash", "glm-4.6v-flash"
    };

    /// <summary>
    /// The only paid models, owner-approved 2026-09-18, all through OpenRouter. Exact ids: a typo cannot load and
    /// spend on an unverified model.
    /// </summary>
    private static readonly HashSet<string> _openRouterPaidModels = new( StringComparer.Ordinal )
    {
        "openai/gpt-5.6-luna", "openai/gpt-5.6-terra", "openai/gpt-5.6-sol", "openai/gpt-6-astra", "openai/gpt-chat-latest",
        "google/gemini-3.8-flash", "google/gemini-3.1-pro-preview", "google/gemini-3.5-flash-lite",
        "x-ai/grok-4.3", "x-ai/grok-4.6", "perplexity/sonar"
    };

    /// <summary>Providers whose every model is free to this account; anything not listed must be named explicitly.</summary>
    private static readonly HashSet<string> _alwaysFreeProviders = new( StringComparer.Ordinal ) { "groq", "cohere", "claude-sub" };

    /// <summary>
    /// Each provider key is bound to its own endpoint host(s) and key file, so a seat cannot be labelled with a free
    /// provider while calling OpenRouter, or use the funded paid key under a free label.
    /// </summary>
    private static readonly Dictionary<string, (string[] Hosts, string KeyFile)> _providerBindings = new( StringComparer.Ordinal )
    {
        ["groq"] = (new[] { "api.groq.com" }, "groq.key"),
        ["cloudflare"] = (new[] { "api.cloudflare.com" }, "cloudflare.key"),
        ["openrouter"] = (new[] { "openrouter.ai" }, "openrouter.key"),
        ["zai"] = (new[] { "api.z.ai" }, "zai.key"),
        ["cohere"] = (new[] { "api.cohere.ai", "api.cohere.com" }, "cohere.key"),
        [ModelProfile.PAID_PROVIDER] = (new[] { "openrouter.ai" }, ModelProfile.PAID_KEY_FILE)
    };

    /// <summary>The only keys a ReasoningBody may set: reasoning controls, never the model, tools, routing or plugins.</summary>
    private static readonly HashSet<string> _reasoningKeys = new( StringComparer.Ordinal )
    {
        "reasoning", "reasoning_effort", "include_reasoning", "thinking", "chat_template_kwargs"
    };

    /// <summary>The only keys a paid seat's ExtraBody may set.</summary>
    private static readonly HashSet<string> _paidExtraKeys = new( StringComparer.Ordinal ) { "provider", "tools", "stop_server_tools_when" };

    /// <summary>Cloudflare models flagged require_workers_paid in the live catalog on 2026-09-16.</summary>
    private static readonly HashSet<string> _cloudflarePaidOnly = new( StringComparer.Ordinal )
    {
        "@cf/moonshotai/kimi-k2.6", "@cf/moonshotai/kimi-k2.7-code", "@cf/zai-org/glm-5.2",
        "@cf/zai-org/glm-5.3", "@cf/zai-org/glm-5.3-flash",
        "@cf/deepseek-ai/deepseek-v4-flash-0731", "@cf/deepseek-ai/deepseek-v4-pro-0813"
    };

    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    #endregion Data Members

    #region Public Methods

    /// <summary>Reads and validates every profile.</summary>
    /// <param name="path">Path to profiles.json.</param>
    /// <returns>All profiles, enabled or not, in file order.</returns>
    public static List<ModelProfile> Load( string path )
    {
        var profiles = JsonSerializer.Deserialize<List<ModelProfile>>( File.ReadAllText( path ), _options )
                       ?? throw new InvalidDataException( $"'{path}' contained no profiles." );

        foreach( var profile in profiles )
        {
            Validate( profile );
        }

        var duplicates = profiles.GroupBy( p => p.SeatId ).Where( g => g.Count() > 1 ).Select( g => g.Key ).ToList();

        if( duplicates.Count > 0 )
        {
            throw new InvalidDataException( $"Duplicate seat ids in '{path}': {string.Join( ", ", duplicates )}" );
        }

        return profiles;
    }

    /// <summary>
    /// Rejects any profile that could bill or send an ambiguous body.
    /// Two token parameter names in one body is ambiguous: on Groq, max_completion_tokens
    /// slips past the free-tier output pre-check that max_tokens triggers.
    /// </summary>
    /// <param name="profile">Profile to check.</param>
    public static void Validate( ModelProfile profile )
    {
        if( profile.TokenParam is not ( "max_tokens" or "max_completion_tokens" ) )
        {
            throw new InvalidDataException( $"{profile.SeatId}: TokenParam must be max_tokens or max_completion_tokens." );
        }

        if( profile.ReasoningBody?.Select( kv => kv.Key ).FirstOrDefault( k => !_reasoningKeys.Contains( k ) ) is string badKey )
        {
            throw new InvalidDataException( $"{profile.SeatId}: ReasoningBody may only set reasoning controls, not '{badKey}'." );
        }

        ValidateBinding( profile );

        if( profile.Provider == ModelProfile.PAID_PROVIDER )
        {
            ValidatePaid( profile );
            return;
        }

        if( profile.Paid )
        {
            throw new InvalidDataException( $"{profile.SeatId}: Paid is only allowed on provider {ModelProfile.PAID_PROVIDER}." );
        }

        if( profile.ExtraBody is not null )
        {
            throw new InvalidDataException( $"{profile.SeatId}: ExtraBody (pins, tools) is only allowed on paid seats." );
        }

        var free = profile.Provider switch
        {
            "openrouter" => profile.ModelId.EndsWith( ":free", StringComparison.Ordinal ) && profile.KeyFile == "openrouter.key",
            "zai" => _zaiFreeModels.Contains( profile.ModelId ),
            "cloudflare" => !_cloudflarePaidOnly.Contains( profile.ModelId ),
            _ => _alwaysFreeProviders.Contains( profile.Provider )
        };

        if( !free )
        {
            throw new InvalidDataException( $"{profile.SeatId}: not on the free allowlist for {profile.Provider}; refusing to load." );
        }
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// A paid seat must be an approved model on its own key, carry the prices the dollar cap needs, and be pinned to
    /// exactly one endpoint with fallbacks off. A web seat must bound its searches; a memory seat must have no tools.
    /// </summary>
    private static void ValidatePaid( ModelProfile profile )
    {
        void Fail( string why ) => throw new InvalidDataException( $"{profile.SeatId}: {why}; refusing to load." );

        if( !profile.Paid ) { Fail( "a seat on the paid provider must say Paid: true" ); }
        if( !_openRouterPaidModels.Contains( profile.ModelId ) ) { Fail( "not on the approved paid model list" ); }
        if( profile.KeyFile != ModelProfile.PAID_KEY_FILE ) { Fail( $"paid seats must use {ModelProfile.PAID_KEY_FILE}" ); }
        if( profile.PriceInUsdPerM is not > 0 || profile.PriceOutUsdPerM is not > 0 ) { Fail( "paid seats need input and output prices" ); }

        if( profile.LongPromptThresholdTokens is not null
            && ( profile.PriceInLongUsdPerM is not > 0 || profile.PriceOutLongUsdPerM is not > 0
                 || profile.PriceInLongUsdPerM < profile.PriceInUsdPerM || profile.PriceOutLongUsdPerM < profile.PriceOutUsdPerM ) )
        {
            Fail( "a long-context threshold needs long-context prices at least as high as the base prices" );
        }
        if( string.IsNullOrWhiteSpace( profile.PinnedProvider ) ) { Fail( "paid seats need PinnedProvider" ); }

        if( profile.ExtraBody?.Select( kv => kv.Key ).FirstOrDefault( k => !_paidExtraKeys.Contains( k ) ) is string extraKey )
        {
            Fail( $"ExtraBody may only set provider, tools and stop_server_tools_when, not '{extraKey}'" );
        }

        var provider = profile.ExtraBody?["provider"] as JsonObject;
        var only = provider?["only"] as JsonArray;

        if( only is null || only.Count != 1 || provider?["allow_fallbacks"]?.GetValue<bool>() != false )
        {
            Fail( "paid seats must pin exactly one provider (provider.only) with allow_fallbacks false" );
        }

        var tools = profile.ExtraBody?["tools"] as JsonArray;
        var searchNative = profile.ModelId.StartsWith( "perplexity/", StringComparison.Ordinal );

        if( profile.Group == "web-search" )
        {
            if( profile.SearchFeeUsd is not > 0 || profile.MaxSearchesPerCall < 1 || profile.WebInputBoundTokens < 1000 )
            {
                Fail( "paid web seats need SearchFeeUsd, MaxSearchesPerCall and WebInputBoundTokens" );
            }

            if( !searchNative && ( tools is null || tools.Count != 1 || tools[0]?["type"]?.GetValue<string>() != "openrouter:web_search" ) )
            {
                Fail( "paid web seats need exactly one tool, openrouter:web_search" );
            }

            var stops = profile.ExtraBody?["stop_server_tools_when"] as JsonArray;

            // The stop bounds OpenRouter's own tool loop (and an Exa fallback); native search runs inside one upstream
            // call and ignores it. The estimate includes it either way.
            if( !searchNative && ( stops is null || !stops.Any( st => st?["type"]?.GetValue<string>() == "max_cost" ) ) )
            {
                Fail( "paid web seats need a max_cost stop, which the estimate includes" );
            }

            if( searchNative && ( tools is not null || stops is not null ) )
            {
                Fail( "a search-native model searches on its own; it takes no tools and no tool stops" );
            }
        }
        else if( tools is not null || profile.ExtraBody?.ContainsKey( "stop_server_tools_when" ) == true || searchNative )
        {
            Fail( "a memory seat must not have tools or tool stops, and a search-native model cannot be a memory seat" );
        }
    }

    /// <summary>A seat's endpoint host and key file must be the ones its provider key is bound to.</summary>
    private static void ValidateBinding( ModelProfile profile )
    {
        if( profile.Provider == "claude-sub" )
        {
            if( profile.Shape != ApiShape.ClaudeCli || profile.KeyFile.Length > 0 )
            {
                throw new InvalidDataException( $"{profile.SeatId}: claude-sub seats must be ClaudeCli with no key file." );
            }

            return;
        }

        if( !_providerBindings.TryGetValue( profile.Provider, out var binding ) )
        {
            throw new InvalidDataException( $"{profile.SeatId}: unknown provider '{profile.Provider}'; refusing to load." );
        }

        var host = Uri.TryCreate( profile.Endpoint, UriKind.Absolute, out var uri ) ? uri.Host : string.Empty;

        if( uri?.Scheme != Uri.UriSchemeHttps || !binding.Hosts.Contains( host, StringComparer.OrdinalIgnoreCase ) || profile.KeyFile != binding.KeyFile )
        {
            throw new InvalidDataException(
                $"{profile.SeatId}: provider {profile.Provider} must call https://{string.Join( " or ", binding.Hosts )} with {binding.KeyFile}; refusing to load." );
        }
    }

    #endregion Private Methods
}
