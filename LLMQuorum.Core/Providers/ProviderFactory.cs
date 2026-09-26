using LLMQuorum.Core.Configuration;
using LLMQuorum.Core.Models;

namespace LLMQuorum.Core.Providers;

/// <summary>
/// Builds a client for each configured provider and resolves its credential at
/// construction time, so a missing key fails loudly at startup rather than
/// silently producing an auth failure mid-run that looks like a model problem.
/// </summary>
public sealed class ProviderFactory
{
    #region Data Members

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly QuorumConfig _config;

    #endregion Data Members

    #region Constructor

    /// <summary>Builds a factory over the application's HTTP client factory and config.</summary>
    /// <param name="httpClientFactory">Supplies pooled clients so sockets are not exhausted across a long sweep.</param>
    /// <param name="config">Resolved configuration including the ladder and key locations.</param>
    public ProviderFactory( IHttpClientFactory httpClientFactory, QuorumConfig config )
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>
    /// Creates a client for one provider, selecting the wire protocol from
    /// config. Anthropic is the only non-OpenAI shape in the panel.
    /// </summary>
    /// <param name="definition">Provider to build a client for.</param>
    /// <returns>A ready client with its credential already resolved.</returns>
    public IQuorumProvider Create( ProviderDefinition definition )
    {
        // The CLI seat authenticates with the subscription login stored by
        // `claude auth login`. There is no key file to resolve.
        if( definition.Protocol == ProviderProtocol.ClaudeCli )
        {
            return new ClaudeCliProvider( definition, _config.ConfigDirectory );
        }

        var apiKey = _config.ResolveApiKey( definition );
        var httpClient = _httpClientFactory.CreateClient( definition.Key );

        return definition.Protocol switch
        {
            ProviderProtocol.Anthropic => new AnthropicProvider( httpClient, definition, apiKey ),
            _ => new OpenAiCompatibleProvider( httpClient, definition, apiKey )
        };
    }

    /// <summary>
    /// Creates clients for the whole enabled ladder, in ask order. Any provider
    /// whose credential cannot be resolved is reported rather than skipped,
    /// because a quietly absent seat would shrink the panel without anyone
    /// noticing the verdict was decided by fewer voices than intended.
    /// </summary>
    /// <param name="failures">Receives provider keys that could not be built, with the reason.</param>
    /// <returns>Clients for every provider that could be constructed, in ladder order.</returns>
    public List<IQuorumProvider> CreateLadder( out List<string> failures )
    {
        var clients = new List<IQuorumProvider>();
        failures = new List<string>();

        foreach( var definition in _config.GetLadder() )
        {
            try
            {
                clients.Add( Create( definition ) );
            }
            catch( Exception ex )
            {
                failures.Add( $"{definition.Key}: {ex.Message}" );
            }
        }

        return clients;
    }

    #endregion Public Methods
}
