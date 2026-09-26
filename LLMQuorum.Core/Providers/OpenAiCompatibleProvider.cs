using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using LLMQuorum.Core.Models;

namespace LLMQuorum.Core.Providers;

/// <summary>
/// Speaks the OpenAI chat-completions shape, which covers five of the six
/// configured platforms: Groq, OpenRouter, Cohere, Cloudflare Workers AI and
/// Z.ai. They differ only in base URL, model id and optional body extras, all
/// of which come from config, so one client serves all of them.
/// Why one class instead of five: five near-identical clients drift apart, and
/// a drifted client produces answer differences that look like model
/// disagreement. The whole tool measures disagreement, so that bug would be
/// indistinguishable from a finding.
/// </summary>
public sealed class OpenAiCompatibleProvider : IQuorumProvider
{
    #region Data Members

    /// <summary>
    /// Sent on every request. Several providers sit behind bot protection that
    /// rejects requests with no user agent: a bare client gets HTTP 403 from
    /// Groq while curl succeeds against the identical URL and key.
    /// </summary>
    private const string USER_AGENT = "LLMQuorum/1.0";

    /// <summary>Deterministic sampling. Comparing seats only means something if each seat is itself repeatable.</summary>
    private const double TEMPERATURE = 0.0;

    private readonly HttpClient _httpClient;
    private readonly ProviderDefinition _definition;
    private readonly string _apiKey;

    #endregion Data Members

    #region Constructor

    /// <summary>
    /// Builds a client for one provider.
    /// </summary>
    /// <param name="httpClient">Shared client; timeout is applied per request via a linked token, not here.</param>
    /// <param name="definition">Provider configuration including endpoint, model id and body extras.</param>
    /// <param name="apiKey">Resolved credential. Never logged, never persisted.</param>
    public OpenAiCompatibleProvider( HttpClient httpClient, ProviderDefinition definition, string apiKey )
    {
        _httpClient = httpClient;
        _definition = definition;
        _apiKey = apiKey;
    }

    #endregion Constructor

    #region Public Methods

    /// <inheritdoc />
    public ProviderDefinition Definition => _definition;

    /// <inheritdoc />
    public async Task<ProviderAnswer> AskAsync(
        string prompt,
        int rank,
        CancellationToken cancellationToken = default )
    {
        var stopwatch = Stopwatch.StartNew();

        // The OpenRouter account holds real credit since 2026-09-18. This legacy panel path has no dollar gate, so
        // it may only call ":free" OpenRouter models, with no extras that could add a paid model, plugin or tool.
        if( RefusesPaidOpenRouter() is string refusal )
        {
            return BuildFailure( rank, 0, CallErrorClass.Auth, refusal, null, null );
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
        timeoutSource.CancelAfter( TimeSpan.FromSeconds( _definition.TimeoutSeconds ) );

        try
        {
            using var request = BuildRequest( prompt );
            using var response = await _httpClient.SendAsync( request, timeoutSource.Token );

            var bytes = await response.Content.ReadAsByteArrayAsync( timeoutSource.Token );
            stopwatch.Stop();

            return response.IsSuccessStatusCode
                ? ParseSuccess( bytes, rank, (int)response.StatusCode, (int)stopwatch.ElapsedMilliseconds )
                : BuildFailure( rank, (int)stopwatch.ElapsedMilliseconds, ClassifyHttp( response.StatusCode ),
                                Truncate( Encoding.UTF8.GetString( bytes ) ), bytes, (int)response.StatusCode );
        }
        catch( OperationCanceledException ) when( !cancellationToken.IsCancellationRequested )
        {
            stopwatch.Stop();
            return BuildFailure( rank, (int)stopwatch.ElapsedMilliseconds, CallErrorClass.Timeout,
                                 $"No response within {_definition.TimeoutSeconds}s.", null, null );
        }
        catch( HttpRequestException ex )
        {
            stopwatch.Stop();
            return BuildFailure( rank, (int)stopwatch.ElapsedMilliseconds, CallErrorClass.Network,
                                 Truncate( ex.Message ), null, null );
        }
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>Why this call must not go out: a non-free OpenRouter model, or extras that could reach one.</summary>
    private string? RefusesPaidOpenRouter()
    {
        if( !Uri.TryCreate( _definition.Endpoint, UriKind.Absolute, out var uri ) ||
            !uri.Host.EndsWith( "openrouter.ai", StringComparison.OrdinalIgnoreCase ) )
        {
            return null;
        }

        if( !_definition.ModelId.EndsWith( ":free", StringComparison.Ordinal ) )
        {
            return $"Refused: the panel path only calls ':free' OpenRouter models, not '{_definition.ModelId}'.";
        }

        var risky = _definition.ExtraBody?.Keys.FirstOrDefault( k => k is "model" or "models" or "plugins" or "tools" or "route" or "service_tier" );
        return risky is null ? null : $"Refused: panel extras may not set '{risky}' on OpenRouter.";
    }

    /// <summary>
    /// Assembles the HTTP request, merging any provider-specific extras from
    /// config. Extras exist for things like OpenRouter's provider-pinning block,
    /// which is what stops a router silently substituting a different model and
    /// turning two seats into one.
    /// </summary>
    /// <param name="prompt">Question text sent verbatim.</param>
    /// <returns>A ready-to-send request with auth and headers applied.</returns>
    private HttpRequestMessage BuildRequest( string prompt )
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = _definition.ModelId,
            ["temperature"] = TEMPERATURE,
            ["max_tokens"] = _definition.MaxTokens,
            ["messages"] = new[] { new { role = "user", content = prompt } }
        };

        if( _definition.ExtraBody is not null )
        {
            foreach( var pair in _definition.ExtraBody )
            {
                body[pair.Key] = pair.Value;
            }
        }

        var request = new HttpRequestMessage( HttpMethod.Post, _definition.Endpoint )
        {
            Content = new StringContent( JsonSerializer.Serialize( body ), Encoding.UTF8, "application/json" )
        };

        request.Headers.TryAddWithoutValidation( "Authorization", $"Bearer {_apiKey}" );
        request.Headers.TryAddWithoutValidation( "User-Agent", USER_AGENT );
        return request;
    }

    /// <summary>
    /// Extracts the assistant message from an OpenAI-shaped envelope. A 200 with
    /// empty content is treated as a failure, not an empty answer: reasoning
    /// models routinely spend the entire token budget thinking and return
    /// nothing, and counting that as a vote would corrupt the tally.
    /// </summary>
    /// <param name="bytes">Raw response body, preserved verbatim on the result.</param>
    /// <param name="rank">Consultation order for this provider.</param>
    /// <param name="status">HTTP status code.</param>
    /// <param name="latencyMs">Round-trip time.</param>
    /// <returns>A success record, or an Empty/Parse failure carrying the same bytes.</returns>
    private ProviderAnswer ParseSuccess( byte[] bytes, int rank, int status, int latencyMs )
    {
        try
        {
            using var document = JsonDocument.Parse( bytes );

            // Routers report upstream failures as HTTP 200 with an error body.
            // MEASURED: OpenRouter returned {"error":{"code":502,"message":"Upstream
            // error from Nvidia: Service temporarily overloaded"}} with status 200.
            // Without this check that lands as a parse failure and the profile
            // blames the model for what was platform congestion.
            if( document.RootElement.TryGetProperty( "error", out var error ) )
            {
                return BuildEmbeddedErrorFailure( error, bytes, rank, status, latencyMs );
            }

            var choice = document.RootElement.GetProperty( "choices" )[0];

            var text = choice.TryGetProperty( "message", out var message ) &&
                       message.TryGetProperty( "content", out var content )
                ? content.GetString()
                : null;

            var finishReason = choice.TryGetProperty( "finish_reason", out var finish )
                ? finish.GetString()
                : null;

            if( string.IsNullOrWhiteSpace( text ) )
            {
                return BuildFailure( rank, latencyMs, CallErrorClass.Empty,
                                     $"200 with no content (finish_reason={finishReason ?? "none"}).",
                                     bytes, status, finishReason );
            }

            var (promptTokens, completionTokens) = ReadUsage( document.RootElement );

            return new ProviderAnswer
            {
                Provider = _definition,
                Rank = rank,
                IsSuccess = true,
                AnswerText = text.Trim(),
                ResponseBytes = bytes,
                HttpStatus = status,
                LatencyMs = latencyMs,
                FinishReason = finishReason,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens
            };
        }
        catch( Exception ex ) when( ex is JsonException or KeyNotFoundException or IndexOutOfRangeException )
        {
            return BuildFailure( rank, latencyMs, CallErrorClass.Parse, Truncate( ex.Message ), bytes, status );
        }
    }

    /// <summary>
    /// Classifies an error envelope that arrived inside a successful HTTP
    /// response, using the embedded code so rate limits and capacity problems
    /// are bucketed the same way as their non-200 equivalents.
    /// </summary>
    /// <param name="error">The "error" element from the response body.</param>
    /// <param name="bytes">Raw response body, preserved on the failure record.</param>
    /// <param name="rank">Consultation order for this provider.</param>
    /// <param name="status">HTTP status as sent, usually 200.</param>
    /// <param name="latencyMs">Round-trip time.</param>
    /// <returns>A failure record bucketed by the embedded error code.</returns>
    private ProviderAnswer BuildEmbeddedErrorFailure(
        JsonElement error, byte[] bytes, int rank, int status, int latencyMs )
    {
        var code = error.TryGetProperty( "code", out var codeElement ) &&
                   codeElement.ValueKind == JsonValueKind.Number
            ? codeElement.GetInt32()
            : 0;

        var message = error.TryGetProperty( "message", out var messageElement )
            ? messageElement.GetString() ?? "error body with no message"
            : "error body with no message";

        var errorClass = code switch
        {
            429 => CallErrorClass.RateLimit,
            401 or 403 => CallErrorClass.Auth,
            408 => CallErrorClass.Timeout,
            >= 500 and < 600 => CallErrorClass.Capacity,
            _ => CallErrorClass.Other
        };

        return BuildFailure( rank, latencyMs, errorClass, Truncate( $"[{code}] {message}" ), bytes, status );
    }

    /// <summary>
    /// Reads token usage when the provider reports it. Several free tiers omit
    /// the block entirely, so absence is normal and must not fail the parse.
    /// </summary>
    /// <param name="root">Root element of the response envelope.</param>
    /// <returns>Prompt and completion token counts, each null when unreported.</returns>
    private static (int? Prompt, int? Completion) ReadUsage( JsonElement root )
    {
        if( !root.TryGetProperty( "usage", out var usage ) )
        {
            return (null, null);
        }

        int? prompt = usage.TryGetProperty( "prompt_tokens", out var p ) ? p.GetInt32() : null;
        int? completion = usage.TryGetProperty( "completion_tokens", out var c ) ? c.GetInt32() : null;
        return (prompt, completion);
    }

    /// <summary>
    /// Maps an HTTP status to a failure bucket so the characterization matrix can
    /// separate platform busyness from model unreliability. A 429 says nothing
    /// about answer quality; an unparseable body does.
    /// </summary>
    /// <param name="status">Status code returned by the provider.</param>
    /// <returns>The bucket this failure belongs in.</returns>
    private static CallErrorClass ClassifyHttp( HttpStatusCode status )
    {
        return status switch
        {
            HttpStatusCode.TooManyRequests => CallErrorClass.RateLimit,
            HttpStatusCode.Unauthorized => CallErrorClass.Auth,
            HttpStatusCode.Forbidden => CallErrorClass.Auth,
            HttpStatusCode.RequestTimeout => CallErrorClass.Timeout,
            HttpStatusCode.ServiceUnavailable => CallErrorClass.Capacity,
            HttpStatusCode.BadGateway => CallErrorClass.Capacity,
            HttpStatusCode.GatewayTimeout => CallErrorClass.Capacity,
            _ => CallErrorClass.Other
        };
    }

    /// <summary>Builds a failure record, preserving response bytes when any arrived.</summary>
    /// <param name="rank">Consultation order.</param>
    /// <param name="latencyMs">Elapsed time before the failure.</param>
    /// <param name="errorClass">Failure bucket.</param>
    /// <param name="errorText">Truncated diagnostic detail.</param>
    /// <param name="bytes">Raw body if one was received.</param>
    /// <param name="status">HTTP status if the request reached that stage.</param>
    /// <param name="finishReason">Provider stop reason when one was reported, which distinguishes
    /// a truncated answer from a model that simply returned nothing.</param>
    /// <returns>A ProviderAnswer with IsSuccess false.</returns>
    private ProviderAnswer BuildFailure(
        int rank, int latencyMs, CallErrorClass errorClass, string errorText, byte[]? bytes, int? status,
        string? finishReason = null )
    {
        return new ProviderAnswer
        {
            Provider = _definition,
            Rank = rank,
            IsSuccess = false,
            ResponseBytes = bytes,
            HttpStatus = status,
            LatencyMs = latencyMs,
            ErrorClass = errorClass,
            ErrorText = errorText,
            FinishReason = finishReason
        };
    }

    /// <summary>Clips diagnostic text to fit the ErrorText column without truncating mid-surrogate.</summary>
    /// <param name="value">Raw diagnostic text.</param>
    /// <returns>Text no longer than the storage limit.</returns>
    private static string Truncate( string value )
    {
        const int MAX_ERROR_LENGTH = 1900;
        return value.Length <= MAX_ERROR_LENGTH ? value : value[..MAX_ERROR_LENGTH];
    }

    #endregion Private Methods
}
