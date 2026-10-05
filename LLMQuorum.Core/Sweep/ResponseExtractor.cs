using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LLMQuorum.Core.Sweep;

/// <summary>What one provider response turned out to be, derived only from the bytes.</summary>
/// <param name="Status">Answered, Truncated, ThinkingExhausted, Empty, Filtered, or Error.</param>
/// <param name="RawContent">The content field exactly as returned, before any cleaning.</param>
/// <param name="ReasoningText">Reasoning text, stored for inspection and never graded.</param>
/// <param name="Answer">Cleaned final answer. Null unless Answered or Truncated.</param>
/// <param name="FinishReason">Provider stop reason as a string.</param>
/// <param name="PromptTokens">Prompt tokens reported.</param>
/// <param name="CompletionTokens">Completion tokens reported.</param>
/// <param name="ReasoningTokens">Reasoning tokens reported.</param>
/// <param name="ReasoningEvidence">True when the model reasoned, whether or not reasoning was requested off.</param>
/// <param name="IsRefusal">Heuristic: the answer declines rather than answers.</param>
/// <param name="ErrorCode">Error code from any envelope shape, as a string.</param>
/// <param name="ErrorMessage">Error message from any envelope shape.</param>
/// <param name="ErrorMetadata">Raw error metadata JSON, used by the classifier (e.g. OpenRouter limit_source).</param>
/// <param name="Cost">OpenRouter usage.cost when present; must be zero on free models.</param>
public sealed record ExtractedResponse(
    string Status, string? RawContent, string? ReasoningText, string? Answer, string? FinishReason,
    int? PromptTokens, int? CompletionTokens, int? ReasoningTokens, bool ReasoningEvidence, bool IsRefusal,
    string? ErrorCode, string? ErrorMessage, string? ErrorMetadata, decimal? Cost )
{
    /// <summary>OpenRouter: provider_name that actually served the call, checked against a paid seat's pin.</summary>
    public string? ServedProvider { get; init; }

    /// <summary>OpenRouter generation id (gen-...), for auditing a call against GET /api/v1/generation.</summary>
    public string? GenerationId { get; init; }

    /// <summary>OpenRouter usage.server_tool_use.web_search_requests: searches the call made.</summary>
    public int? WebSearchRequests { get; init; }
}

/// <summary>
/// Pure, versioned interpretation of a provider response. It is a function of the bytes alone, so it
/// can be re-run over stored responses with zero API calls. In the live harness ProfileCaller is its
/// only caller, at call time; re-running it across history is a deliberate manual step, not something
/// that happens on its own when a rule here changes.
///
/// Each rule exists because a live response broke the previous harness:
///   * content may be a string, JSON null (Cloudflare on truncation) or an array of blocks
///     (Cohere v2); reading it as a string threw and crashed the call;
///   * error envelopes differ: error.code int (OpenRouter), error.code string (Z.ai "1305"),
///     errors[0].code (Cloudflare), top-level error_type (Cohere);
///   * truncation is finish_reason "length", or MAX_TOKENS, or any non-COMPLETE Cohere v2 finish,
///     or a normal "stop" at exactly the cap (Cloudflare qwq-32b, r1-distill);
///   * reasoning may be written into content ending with a closing think tag and no opening tag.
/// </summary>
public static partial class ResponseExtractor
{
    #region Data Members

    /// <summary>Bump when any rule changes so stored rows record which rules produced them.</summary>
    public const string VERSION = "extract-v4";

    /// <summary>Gemini's leaked-thought marker, written as the first line of content.</summary>
    private const string THOUGHT_MARKER = "llm:thought";

    private const string CLOSE_TAG = "</think>";
    private const string OPEN_TAG = "<think>";

    #endregion Data Members

    #region Public Methods

    /// <summary>Interprets one response body.</summary>
    /// <param name="profile">Profile the request was built from.</param>
    /// <param name="body">Raw response bytes.</param>
    /// <param name="sentCap">Output token cap that was sent.</param>
    /// <returns>The interpretation.</returns>
    public static ExtractedResponse Extract( ModelProfile profile, byte[] body, int sentCap )
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse( body );
        }
        catch( JsonException )
        {
            var text = Encoding.UTF8.GetString( body );
            return ErrorResult( null, Clip( text ), null );
        }

        using( document )
        {
            var root = document.RootElement;
            var (errorCode, errorMessage, errorMetadata) = ReadError( root );
            var hasPayload = root.ValueKind == JsonValueKind.Object &&
                             ( root.TryGetProperty( "choices", out _ ) || root.TryGetProperty( "message", out _ ) );

            if( errorCode is not null && !hasPayload )
            {
                // Keep OpenRouter's generation id and any usage on an error body: the paid lane settles the cost from
                // them, and an error that reports usage is never treated as a free pre-routing rejection.
                var errorUsage = ReadUsage( root );
                return ErrorResult( errorCode, errorMessage, errorMetadata ) with
                {
                    GenerationId = ReadString( root, "id" ), ServedProvider = ReadString( root, "provider" ),
                    PromptTokens = errorUsage.Prompt, CompletionTokens = errorUsage.Completion, Cost = errorUsage.Cost,
                    WebSearchRequests = errorUsage.Searches
                };
            }

            return profile.Shape == ApiShape.CohereV2
                ? ExtractCohereV2( profile, root, sentCap )
                : ExtractOpenAiChat( profile, root, sentCap );
        }
    }

    /// <summary>
    /// Separates the answer from reasoning written into content.
    /// The text after the LAST closing tag wins, which also handles qwq-32b's closing-only tag.
    /// A think-in-content model with no closing tag is still thinking, so there is no answer.
    /// </summary>
    /// <param name="content">Raw content.</param>
    /// <param name="location">Where the profile says reasoning appears.</param>
    /// <returns>Answer text (null when none) and whether reasoning was left unfinished.</returns>
    public static (string? Answer, bool Unclosed) StripThink( string? content, ThinkLocation location )
    {
        if( content is null )
        {
            return (null, false);
        }

        var close = content.LastIndexOf( CLOSE_TAG, StringComparison.OrdinalIgnoreCase );

        if( close >= 0 )
        {
            return (content[( close + CLOSE_TAG.Length )..], false);
        }

        if( location == ThinkLocation.InContentClosedTag )
        {
            return (null, true);
        }

        var open = content.IndexOf( OPEN_TAG, StringComparison.OrdinalIgnoreCase );
        return open >= 0 ? ( open == 0 ? null : content[..open], true ) : (content, false);
    }

    /// <summary>Trims each line's trailing whitespace (markdown hard breaks), drops blank lines, trims the whole.</summary>
    /// <param name="answer">Answer text.</param>
    /// <returns>Normalised text, empty when nothing remains.</returns>
    public static string Normalize( string? answer )
    {
        if( string.IsNullOrWhiteSpace( answer ) )
        {
            return string.Empty;
        }

        // MEASURED: web-search seats append a "Sources:" block after the answer. It is citation, not
        // answer, and left in place it makes a correct list fail comparison. RawContent keeps it.
        var lines = answer.Replace( "\r\n", "\n" ).Split( '\n' )
            .TakeWhile( line => !SourcesHeader().IsMatch( line ) )
            .Select( line => line.TrimEnd() )
            .Where( line => line.Trim().Length > 0 );

        return string.Join( "\n", lines ).Trim().Normalize( NormalizationForm.FormC );
    }

    /// <summary>
    /// Removes inline citations that web-search answers carry, so "16100 ([irs.gov](https://...))" or "Single[1][2]"
    /// grades as "16100" and "Single". Markdown links keep their text; groups made only of links, bare parenthesised
    /// URLs and numeric markers are dropped. RawContent keeps the original.
    /// </summary>
    /// <param name="answer">Answer text.</param>
    /// <returns>The answer without citations.</returns>
    public static string? StripCitations( string? answer )
    {
        if( string.IsNullOrEmpty( answer ) )
        {
            return answer;
        }

        var text = LinkOnlyGroup().Replace( answer, string.Empty );
        text = CitationOnlyLink().Replace( text, string.Empty );
        text = MarkdownLink().Replace( text, "$1" );
        text = BareUrlGroup().Replace( text, string.Empty );
        text = NumericMarker().Replace( text, string.Empty );
        text = SpaceBeforePunctuation().Replace( text, "$1" );
        text = DoubleSpace().Replace( text, " " );

        // Never strip an answer to nothing: "[2, 3, 5]" may be the answer itself.
        return string.IsNullOrWhiteSpace( text ) ? answer : text;
    }

    /// <summary>Heuristic refusal check shared by the HTTP and CLI paths so both grade the same way.</summary>
    /// <param name="normalized">Normalised answer text.</param>
    /// <returns>True when the answer declines rather than answers.</returns>
    public static bool IsRefusal( string normalized ) => RefusalPattern().IsMatch( normalized );

    #endregion Public Methods

    #region Private Methods

    /// <summary>Reads an OpenAI-shaped response.</summary>
    private static ExtractedResponse ExtractOpenAiChat( ModelProfile profile, JsonElement root, int sentCap )
    {
        if( !root.TryGetProperty( "choices", out var choices ) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0 )
        {
            return ErrorResult( "no-choices", "Response had no choices.", null );
        }

        var choice = choices[0];
        var message = choice.TryGetProperty( "message", out var m ) ? m : default;
        var raw = StripLeakedThought( message.ValueKind == JsonValueKind.Object ? ReadContent( message ) : null );

        var reasoning = message.ValueKind == JsonValueKind.Object
            ? ReadString( message, "reasoning" ) ?? ReadString( message, "reasoning_content" )
            : null;

        var finish = choice.TryGetProperty( "finish_reason", out var f ) ? ScalarToString( f ) : null;
        var usage = ReadUsage( root );

        // OpenRouter documents a provider failure after the stream started as HTTP 200 with finish_reason "error",
        // the error inside the choice, and whatever text the model had produced. Graded as an answer that would be
        // a truncated answer counted as real, so it is an error here, with its usage kept for the paid ledger.
        var hasChoiceError = choice.TryGetProperty( "error", out var choiceError )
                             && choiceError.ValueKind is JsonValueKind.Object or JsonValueKind.String;

        if( finish == "error" || hasChoiceError )
        {
            var inChoice = hasChoiceError ? choiceError : default;
            var code = inChoice.ValueKind == JsonValueKind.Object && inChoice.TryGetProperty( "code", out var cc ) ? ScalarToString( cc ) : "error";

            var metadata = inChoice.ValueKind == JsonValueKind.Object && inChoice.TryGetProperty( "metadata", out var md ) ? md.GetRawText() : null;

            return ErrorResult( code ?? "error", ReadString( inChoice, "message" ) ?? "Provider failed after the response started.", metadata ) with
            {
                RawContent = raw, ReasoningText = reasoning, FinishReason = finish,
                ServedProvider = ReadString( root, "provider" ), GenerationId = ReadString( root, "id" ),
                PromptTokens = usage.Prompt, CompletionTokens = usage.Completion, Cost = usage.Cost,
                WebSearchRequests = usage.Searches
            };
        }

        var truncatedByFinish = IsTruncationFinish( finish ) || ( raw is not null && reasoning is not null && raw == reasoning );

        return Classify( profile, raw, reasoning, finish, usage.Prompt, usage.Completion, usage.Reasoning, usage.Cost, sentCap, truncatedByFinish ) with
        {
            ServedProvider = ReadString( root, "provider" ),
            GenerationId = ReadString( root, "id" ),
            WebSearchRequests = usage.Searches
        };
    }

    /// <summary>The usage block of an OpenAI-shaped body: tokens, OpenRouter's usage.cost, and web searches made.</summary>
    private static (int? Prompt, int? Completion, int? Reasoning, decimal? Cost, int? Searches) ReadUsage( JsonElement root )
    {
        var usage = root.ValueKind == JsonValueKind.Object && root.TryGetProperty( "usage", out var u ) ? u : default;

        if( usage.ValueKind != JsonValueKind.Object )
        {
            return (null, null, null, null, null);
        }

        var reasoningTokens = usage.TryGetProperty( "completion_tokens_details", out var d ) ? ReadInt( d, "reasoning_tokens" ) : null;
        var cost = usage.TryGetProperty( "cost", out var c ) && c.ValueKind == JsonValueKind.Number ? c.GetDecimal() : (decimal?)null;

        // The chat-completions schema names it server_tool_use_details; the overview names it server_tool_use.
        var searches = usage.TryGetProperty( "server_tool_use_details", out var details ) && ReadInt( details, "web_search_requests" ) is int fromDetails ? fromDetails
            : usage.TryGetProperty( "server_tool_use", out var tools ) ? ReadInt( tools, "web_search_requests" )
            : null;

        return (ReadInt( usage, "prompt_tokens" ), ReadInt( usage, "completion_tokens" ), reasoningTokens, cost, searches);
    }

    /// <summary>Reads a Cohere native v2 response, whose content is an array of text and thinking blocks.</summary>
    private static ExtractedResponse ExtractCohereV2( ModelProfile profile, JsonElement root, int sentCap )
    {
        var message = root.TryGetProperty( "message", out var m ) ? m : default;
        var text = new StringBuilder();
        var thinking = new StringBuilder();

        if( message.ValueKind == JsonValueKind.Object && message.TryGetProperty( "content", out var blocks ) &&
            blocks.ValueKind == JsonValueKind.Array )
        {
            foreach( var block in blocks.EnumerateArray() )
            {
                var type = ReadString( block, "type" );
                if( type == "text" ) { text.Append( ReadString( block, "text" ) ); }
                else if( type == "thinking" ) { thinking.Append( ReadString( block, "thinking" ) ); }
            }
        }

        var finish = root.TryGetProperty( "finish_reason", out var f ) ? ScalarToString( f ) : null;
        var tokens = root.TryGetProperty( "usage", out var u ) && u.TryGetProperty( "tokens", out var t ) ? t : default;
        var raw = text.Length > 0 ? text.ToString() : null;
        var reasoning = thinking.Length > 0 ? thinking.ToString() : null;

        return Classify( profile, raw, reasoning, finish, ReadInt( tokens, "input_tokens" ), ReadInt( tokens, "output_tokens" ),
                         ReadInt( tokens, "reasoning_tokens" ), null, sentCap, finish is not null && finish != "COMPLETE" );
    }

    /// <summary>Applies the status rules in order. Only Answered is ever graded.</summary>
    private static ExtractedResponse Classify(
        ModelProfile profile, string? raw, string? reasoning, string? finish, int? prompt, int? completion,
        int? reasoningTokens, decimal? cost, int sentCap, bool truncatedByFinish )
    {
        var (answer, unclosed) = StripThink( raw, profile.Think );
        // Paid web seats only: the free web seat keeps its history's grading, and Claude's CLI path is untouched.
        var normalized = Normalize( profile.Paid && profile.Group == "web-search" ? StripCitations( answer ) : answer );

        var truncated = truncatedByFinish || unclosed ||
                        ( completion is int used && used >= sentCap ) ||
                        ( profile.FinishReasonLiesAtCap && completion is int atCap && atCap >= sentCap );

        var evidence = !string.IsNullOrWhiteSpace( reasoning ) || reasoningTokens > 0 ||
                       ( raw?.Contains( CLOSE_TAG, StringComparison.OrdinalIgnoreCase ) ?? false );

        var filtered = finish is "sensitive" or "content_filter";

        var status = filtered ? "Filtered"
            : truncated && normalized.Length == 0 ? "ThinkingExhausted"
            : truncated ? "Truncated"
            : normalized.Length == 0 ? "Empty"
            : "Answered";

        var refusal = status == "Answered" && RefusalPattern().IsMatch( normalized );

        return new ExtractedResponse( status, raw, reasoning, normalized.Length > 0 ? normalized : null, finish,
                                      prompt, completion, reasoningTokens, evidence, refusal, null, null, null, cost );
    }

    /// <summary>Finds an error in any of the four envelope shapes seen live.</summary>
    private static (string? Code, string? Message, string? Metadata) ReadError( JsonElement root )
    {
        if( root.ValueKind != JsonValueKind.Object )
        {
            return (null, null, null);
        }

        if( root.TryGetProperty( "error", out var error ) )
        {
            if( error.ValueKind == JsonValueKind.String )
            {
                return ("error", error.GetString(), null);
            }

            if( error.ValueKind == JsonValueKind.Object )
            {
                var code = error.TryGetProperty( "code", out var c ) ? ScalarToString( c ) : ReadString( error, "type" );
                var metadata = error.TryGetProperty( "metadata", out var md ) ? md.GetRawText() : null;
                return (code ?? "error", ReadString( error, "message" ), metadata);
            }
        }

        if( root.TryGetProperty( "success", out var success ) && success.ValueKind == JsonValueKind.False &&
            root.TryGetProperty( "errors", out var errors ) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0 )
        {
            var first = errors[0];
            return (first.TryGetProperty( "code", out var c ) ? ScalarToString( c ) : "error", ReadString( first, "message" ), null);
        }

        return root.TryGetProperty( "error_type", out var errorType )
            ? (ScalarToString( errorType ), ReadString( root, "message" ), null)
            : (null, null, null);
    }

    /// <summary>
    /// Reads message.content whatever its JSON kind. MEASURED 2026-09-17 (sweep 7): Cloudflare's
    /// qwen2.5-coder answered a numeric question with "content":17.31, a JSON number, and it was
    /// recorded as Empty. Numbers and booleans are now read as their literal text.
    /// </summary>
    private static string? ReadContent( JsonElement message )
    {
        if( !message.TryGetProperty( "content", out var content ) )
        {
            return null;
        }

        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => ScalarToString( content ),
            JsonValueKind.Array => string.Concat( content.EnumerateArray()
                .Where( b => ReadString( b, "type" ) == "text" )
                .Select( b => ReadString( b, "text" ) ) ),
            _ => null
        };
    }

    /// <summary>True for every spelling of "the output limit stopped generation".</summary>
    private static bool IsTruncationFinish( string? finish )
    {
        return finish is not null &&
               ( finish.Equals( "length", StringComparison.OrdinalIgnoreCase ) ||
                 finish.Equals( "max_tokens", StringComparison.OrdinalIgnoreCase ) ||
                 finish.Equals( "model_context_window_exceeded", StringComparison.OrdinalIgnoreCase ) );
    }

    private static ExtractedResponse ErrorResult( string? code, string? message, string? metadata ) =>
        new( "Error", null, null, null, null, null, null, null, false, false, code ?? "unparseable", message, metadata, null );

    private static string? ReadString( JsonElement element, string name ) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty( name, out var value ) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt( JsonElement element, string name ) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty( name, out var value ) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32( out var number )
            ? number
            : null;

    /// <summary>Glm on Cloudflare returns stop_reason as an integer, so scalars are read as strings.</summary>
    private static string? ScalarToString( JsonElement element ) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null
    };

    private static string Clip( string value ) => value.Length <= 1900 ? value : value[..1900];

    /// <summary>
    /// MEASURED 2026-09-20, google/gemini-3.1-pro-preview through Vertex: the model wrote its thinking into content
    /// behind a literal "llm:thought" line and then appended the real answer to the end of the last thought sentence
    /// with no separator ( ... I will output just "$16,100".$16,100 ). Graded as it stood, a right answer read as
    /// rubbish. The answer is recovered in the order it can be trusted: a value the thought quoted that the line also
    /// ends with, then a short tail after the last sentence mark, then the last line as it is. Content without the
    /// marker is returned untouched, so no other provider is affected.
    /// </summary>
    /// <param name="content">Raw content.</param>
    /// <returns>The answer, or the content unchanged.</returns>
    private static string? StripLeakedThought( string? content )
    {
        if( content is null || !content.StartsWith( THOUGHT_MARKER, StringComparison.OrdinalIgnoreCase ) )
        {
            return content;
        }

        var lines = content[THOUGHT_MARKER.Length..].Replace( "\r\n", "\n" ).Split( '\n' );
        var lastLine = lines[^1].Trim();

        foreach( Match quoted in QuotedValue().Matches( lastLine ) )
        {
            var value = quoted.Groups[1].Value.Trim();

            if( value.Length > 0 && lastLine.EndsWith( value, StringComparison.Ordinal ) )
            {
                return value;
            }
        }

        var cut = lastLine.LastIndexOfAny( new[] { '.', '!', '?', ':' } );
        var tail = cut >= 0 && cut + 1 < lastLine.Length ? lastLine[( cut + 1 )..].Trim() : string.Empty;
        return tail.Length is > 0 and <= 80 ? tail : lastLine;
    }

    /// <summary>
    /// A parenthesised group made only of markdown links: " ([irs.gov](https://...), [ssa.gov](https://...))". URLs may
    /// hold one level of balanced parentheses, e.g. https://en.wikipedia.org/wiki/Mileage_(tax).
    /// </summary>
    [GeneratedRegex( @"[ \t]*\((?:\s*\[[^\]\r\n]*\]\((?:[^()\s]|\([^()\s]*\))+\)\s*[,;]?)+\s*\)" )]
    private static partial Regex LinkOnlyGroup();

    /// <summary>A double-quoted value inside a leaked thought, e.g. I will output just "$16,100".</summary>
    [GeneratedRegex( "\"([^\"]{1,80})\"" )]
    private static partial Regex QuotedValue();

    /// <summary>
    /// A link that is only a citation: its text is a domain ("[irs.gov](...)") or a number ("[1](...)", "[[1]](...)").
    /// Dropped whole, with the space before it.
    /// </summary>
    [GeneratedRegex( @"[ \t]*\[(?:\[?\d{1,3}\]?|(?:www\.)?[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+)\]\(https?://(?:[^()\s]|\([^()\s]*\))+\)" )]
    private static partial Regex CitationOnlyLink();

    /// <summary>A descriptive markdown link "[text](http...)": keep the text.</summary>
    [GeneratedRegex( @"\[([^\]\r\n]+)\]\(https?://(?:[^()\s]|\([^()\s]*\))+\)" )]
    private static partial Regex MarkdownLink();

    /// <summary>A parenthesised bare URL.</summary>
    [GeneratedRegex( @"[ \t]*\(https?://(?:[^()\s]|\([^()\s]*\))+\)" )]
    private static partial Regex BareUrlGroup();

    /// <summary>Numeric citation markers: [1], [2][3], [1, 2], [1-3].</summary>
    [GeneratedRegex( @"\[\d{1,3}(?:\s*[,\u2013-]\s*\d{1,3})*\]" )]
    private static partial Regex NumericMarker();

    /// <summary>A space left before punctuation after a citation was removed.</summary>
    [GeneratedRegex( @"[ \t]+([.,;:!?])" )]
    private static partial Regex SpaceBeforePunctuation();

    /// <summary>Runs of spaces or tabs left after removal.</summary>
    [GeneratedRegex( @"[ \t]{2,}" )]
    private static partial Regex DoubleSpace();

    /// <summary>A line that starts a citation block: "Sources:", "**References**", or inline "Sources: [DOR](...)".</summary>
    [GeneratedRegex( @"^\s*[*_#]*\s*(sources?|references?|citations?)\s*[*_]*\s*(:.*|[*_]*\s*)$", RegexOptions.IgnoreCase )]
    private static partial Regex SourcesHeader();

    /// <summary>First-person refusal phrasing. Heuristic, labelled as such wherever it is reported.</summary>
    [GeneratedRegex(
        @"\bI\s*(?:'m|am)?\s*(?:do not|don't|cannot|can't|unable to|not able to)\s+(?:have\s+)?(?:access|provide|verify|confirm|reliably)|\bI'?m sorry,? but I can'?t|\bI cannot verify",
        RegexOptions.IgnoreCase )]
    private static partial Regex RefusalPattern();

    #endregion Private Methods
}
