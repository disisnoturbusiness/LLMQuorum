using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using LLMQuorum.Core.Models;

namespace LLMQuorum.Core.Providers;

/// <summary>
/// Speaks the Anthropic Messages API, which is the one platform in the panel
/// that is not OpenAI-shaped. It differs in three ways that all matter:
/// authentication is x-api-key rather than Bearer, a version header is
/// mandatory, and the response is a content BLOCK ARRAY rather than a single
/// message string.
/// Why it gets its own class: current Claude models REJECT the temperature
/// parameter with HTTP 400. Trying to force this into the OpenAI client would
/// mean a conditional that silently drops a field, which is exactly the kind of
/// per-provider drift that would show up later as fake model disagreement.
/// </summary>
public sealed class AnthropicProvider : IQuorumProvider
{
    #region Data Members

    /// <summary>Required on every request; the API rejects calls without it.</summary>
    private const string ANTHROPIC_VERSION = "2023-06-01";

    /// <summary>Sent for parity with the other clients and to survive bot filtering.</summary>
    private const string USER_AGENT = "LLMQuorum/1.0";

    /// <summary>
    /// Thinking blocks carry no readable text under the default display setting,
    /// so they are skipped when extracting the answer. Only text blocks count.
    /// </summary>
    private const string TEXT_BLOCK_TYPE = "text";

    private readonly HttpClient _httpClient;
    private readonly ProviderDefinition _definition;
    private readonly string _apiKey;

    #endregion Data Members

    #region Constructor

    /// <summary>Builds a client for the Anthropic seat.</summary>
    /// <param name="httpClient">Shared client; per-request timeout is applied via a linked token.</param>
    /// <param name="definition">Provider configuration including endpoint and model id.</param>
    /// <param name="apiKey">Resolved credential. Never logged, never persisted.</param>
    public AnthropicProvider( HttpClient httpClient, ProviderDefinition definition, string apiKey )
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

    /// <summary>
    /// Builds the Messages API request. Deliberately omits temperature: current
    /// Claude models return HTTP 400 when it is supplied, so determinism here
    /// comes from the model's own defaults rather than from sampling controls.
    /// </summary>
    /// <param name="prompt">Question text sent verbatim.</param>
    /// <returns>A ready-to-send request with auth and version headers applied.</returns>
    private HttpRequestMessage BuildRequest( string prompt )
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = _definition.ModelId,
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

        request.Headers.TryAddWithoutValidation( "x-api-key", _apiKey );
        request.Headers.TryAddWithoutValidation( "anthropic-version", ANTHROPIC_VERSION );
        request.Headers.TryAddWithoutValidation( "User-Agent", USER_AGENT );
        return request;
    }

    /// <summary>
    /// Walks the content block array and concatenates text blocks. Thinking
    /// blocks are skipped because their text is empty under the default display
    /// setting; treating them as content would yield a blank answer that looks
    /// like a real vote.
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
            var root = document.RootElement;

            var builder = new StringBuilder();

            foreach( var block in root.GetProperty( "content" ).EnumerateArray() )
            {
                if( block.TryGetProperty( "type", out var type ) && type.GetString() == TEXT_BLOCK_TYPE &&
                    block.TryGetProperty( "text", out var text ) )
                {
                    builder.Append( text.GetString() );
                }
            }

            var stopReason = root.TryGetProperty( "stop_reason", out var stop ) ? stop.GetString() : null;
            var answer = builder.ToString().Trim();

            if( answer.Length == 0 )
            {
                return BuildFailure( rank, latencyMs, CallErrorClass.Empty,
                                     $"200 with no text block (stop_reason={stopReason ?? "none"}).",
                                     bytes, status, stopReason );
            }

            var (promptTokens, completionTokens) = ReadUsage( root );

            return new ProviderAnswer
            {
                Provider = _definition,
                Rank = rank,
                IsSuccess = true,
                AnswerText = answer,
                ResponseBytes = bytes,
                HttpStatus = status,
                LatencyMs = latencyMs,
                FinishReason = stopReason,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens
            };
        }
        catch( Exception ex ) when( ex is JsonException or KeyNotFoundException )
        {
            return BuildFailure( rank, latencyMs, CallErrorClass.Parse, Truncate( ex.Message ), bytes, status );
        }
    }

    /// <summary>Reads Anthropic's usage block, which names its fields differently from the OpenAI shape.</summary>
    /// <param name="root">Root element of the response envelope.</param>
    /// <returns>Input and output token counts, each null when unreported.</returns>
    private static (int? Prompt, int? Completion) ReadUsage( JsonElement root )
    {
        if( !root.TryGetProperty( "usage", out var usage ) )
        {
            return (null, null);
        }

        int? prompt = usage.TryGetProperty( "input_tokens", out var i ) ? i.GetInt32() : null;
        int? completion = usage.TryGetProperty( "output_tokens", out var o ) ? o.GetInt32() : null;
        return (prompt, completion);
    }

    /// <summary>Maps an HTTP status to a failure bucket, matching the OpenAI client's taxonomy.</summary>
    /// <param name="status">Status code returned by the provider.</param>
    /// <returns>The bucket this failure belongs in.</returns>
    private static CallErrorClass ClassifyHttp( HttpStatusCode status )
    {
        return status switch
        {
            HttpStatusCode.TooManyRequests => CallErrorClass.RateLimit,
            HttpStatusCode.Unauthorized => CallErrorClass.Auth,
            HttpStatusCode.Forbidden => CallErrorClass.Auth,
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
    /// a truncated answer from a model that spent its whole budget thinking.</param>
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

    /// <summary>Clips diagnostic text to fit the ErrorText column.</summary>
    /// <param name="value">Raw diagnostic text.</param>
    /// <returns>Text no longer than the storage limit.</returns>
    private static string Truncate( string value )
    {
        const int MAX_ERROR_LENGTH = 1900;
        return value.Length <= MAX_ERROR_LENGTH ? value : value[..MAX_ERROR_LENGTH];
    }

    #endregion Private Methods
}
