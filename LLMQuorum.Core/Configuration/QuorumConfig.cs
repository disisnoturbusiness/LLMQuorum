using System.Text.Json;
using System.Text.Json.Serialization;
using LLMQuorum.Core.Models;

namespace LLMQuorum.Core.Configuration;

/// <summary>
/// Loads and resolves all LLMQuorum configuration. Credentials are NEVER stored
/// in this file or in source: the config names a key FILE or an environment
/// variable, and the secret is read at call time.
/// Why a dedicated loader rather than IConfiguration: the panel order and the
/// escalation arithmetic have to be snapshotted verbatim into every run so a
/// result stays reproducible after the ladder is reordered.
/// </summary>
public sealed class QuorumConfig
{
    #region Data Members

    /// <summary>Environment variable that overrides the config file location.</summary>
    private const string CONFIG_PATH_ENV = "LLMQUORUM_CONFIG";

    /// <summary>Default config file name, resolved relative to the process directory.</summary>
    private const string DEFAULT_CONFIG_FILE = "llmquorum.json";

    /// <summary>Prefix for per-provider credential environment variables, e.g. LLMQUORUM_GROQ_API_KEY.</summary>
    private const string KEY_ENV_PREFIX = "LLMQUORUM_";

    /// <summary>Serializer settings shared by load and snapshot so a round trip is stable.</summary>
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    #endregion Data Members

    #region Public Methods

    /// <summary>Directory holding credential files, resolved relative to the config file.</summary>
    public string KeysDirectory { get; set; } = "keys";

    /// <summary>
    /// Directory the config file was loaded from. Relative paths in provider
    /// definitions resolve against this, so launch location never changes behaviour.
    /// </summary>
    [JsonIgnore]
    public string ConfigDirectory { get; private set; } = AppContext.BaseDirectory;

    /// <summary>Seat profiles for model sweeps, relative to the config file.</summary>
    public string ProfilesPath { get; set; } = "profiles.json";

    /// <summary>Directory sweep reports are written to, relative to the config file.</summary>
    public string SweepReportDirectory { get; set; } = "sweeps";

    /// <summary>Seat ids from profiles.json that judge answers the rule tier will not grade. Use different labs.</summary>
    public List<string> SheetJudges { get; set; } = new();

    /// <summary>Resolves a config-relative path to an absolute one.</summary>
    /// <param name="path">Absolute or config-relative path.</param>
    /// <returns>Absolute path.</returns>
    public string ResolvePath( string path ) =>
        Path.IsPathRooted( path ) ? path : Path.GetFullPath( Path.Combine( ConfigDirectory, path ) );

    /// <summary>ADO.NET connection string for the LLMQuorum database.</summary>
    public string ConnectionString { get; set; } =
        "Server=localhost;Database=LLMQuorum;Trusted_Connection=True;TrustServerCertificate=True;";

    /// <summary>Panel behaviour: how many to ask first and how many votes constitute a quorum.</summary>
    public PanelSettings Panel { get; set; } = new();

    /// <summary>Every configured provider. Order in this list is irrelevant; LadderPosition decides.</summary>
    public List<ProviderDefinition> Providers { get; set; } = new();

    /// <summary>
    /// Reads config from the path in LLMQUORUM_CONFIG, or llmquorum.json beside
    /// the executable. Throws rather than falling back to defaults, because a
    /// silently defaulted panel order would produce runs that look valid and
    /// are not comparable to anything.
    /// </summary>
    /// <param name="explicitPath">Optional path overriding both the env var and the default.</param>
    /// <returns>The fully deserialized configuration.</returns>
    public static QuorumConfig Load( string? explicitPath = null )
    {
        var path = explicitPath
                   ?? Environment.GetEnvironmentVariable( CONFIG_PATH_ENV )
                   ?? Path.Combine( AppContext.BaseDirectory, DEFAULT_CONFIG_FILE );

        if( !File.Exists( path ) )
        {
            throw new FileNotFoundException( $"LLMQuorum config not found at '{path}'.", path );
        }

        var json = File.ReadAllText( path );
        var config = JsonSerializer.Deserialize<QuorumConfig>( json, _jsonOptions )
                     ?? throw new InvalidDataException( $"Config at '{path}' deserialized to null." );

        config.ResolveKeysDirectory( path );
        return config;
    }

    /// <summary>
    /// Returns the enabled providers in ask order. Disabled entries are dropped
    /// without renumbering, so turning one off does not silently promote another
    /// into the base trio and change what a run means.
    /// </summary>
    /// <returns>Enabled providers sorted ascending by ladder position.</returns>
    public List<ProviderDefinition> GetLadder()
    {
        return Providers
            .Where( p => p.IsEnabled )
            .OrderBy( p => p.LadderPosition )
            .ToList();
    }

    /// <summary>
    /// Resolves a provider's secret. Environment variable wins over the key file
    /// so a container or CI run never needs the file on disk.
    /// Why the env var is checked first: it is the only mechanism that works in
    /// a deployment where the filesystem is read-only or ephemeral.
    /// </summary>
    /// <param name="provider">Provider whose credential is required.</param>
    /// <returns>The secret, trimmed of whitespace and line endings.</returns>
    public string ResolveApiKey( ProviderDefinition provider )
    {
        var envName = $"{KEY_ENV_PREFIX}{provider.Key.ToUpperInvariant()}_API_KEY";
        var fromEnv = Environment.GetEnvironmentVariable( envName );

        if( !string.IsNullOrWhiteSpace( fromEnv ) )
        {
            return fromEnv.Trim();
        }

        var keyPath = Path.IsPathRooted( provider.ApiKeyFile )
            ? provider.ApiKeyFile
            : Path.Combine( KeysDirectory, provider.ApiKeyFile );

        if( !File.Exists( keyPath ) )
        {
            throw new FileNotFoundException(
                $"No credential for provider '{provider.Key}'. Set {envName} or create '{keyPath}'.", keyPath );
        }

        return File.ReadAllText( keyPath ).Trim();
    }

    /// <summary>
    /// Serializes the resolved panel configuration for storage against a run.
    /// Credentials are not present in this object graph, so the snapshot is safe
    /// to persist and to show in the portal.
    /// </summary>
    /// <returns>Indented JSON describing the panel and the ladder.</returns>
    public string ToSnapshotJson()
    {
        var snapshot = new
        {
            Panel,
            Ladder = GetLadder().Select( p => new
            {
                p.Key,
                p.DisplayName,
                p.ModelId,
                p.Lab,
                p.LadderPosition,
                p.Protocol
            } )
        };

        return JsonSerializer.Serialize( snapshot, _jsonOptions );
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// Rebases a relative keys directory onto the config file's own directory so
    /// the app works the same whether launched from Visual Studio, a service
    /// host, or a scheduled task with an unrelated working directory.
    /// </summary>
    /// <param name="configPath">Full path to the config file that was loaded.</param>
    private void ResolveKeysDirectory( string configPath )
    {
        var configDir = Path.GetDirectoryName( Path.GetFullPath( configPath ) ) ?? AppContext.BaseDirectory;
        ConfigDirectory = configDir;

        if( Path.IsPathRooted( KeysDirectory ) )
        {
            return;
        }

        KeysDirectory = Path.Combine( configDir, KeysDirectory );
    }

    #endregion Private Methods
}

/// <summary>
/// The escalation arithmetic. Held separately so it can be snapshotted whole
/// into a run and diffed between runs.
/// </summary>
public sealed class PanelSettings
{
    /// <summary>How many providers are asked before any escalation decision is made.</summary>
    public int BaseCount { get; set; } = 3;

    /// <summary>
    /// Votes an answer needs to win. The engine escalates until some answer
    /// reaches this, or the ladder runs out. Escalation width is derived as
    /// (QuorumSize - leading vote count), which reproduces the intended
    /// behaviour at every split without a lookup table.
    /// </summary>
    public int QuorumSize { get; set; } = 3;

    /// <summary>Minimum successful answers required to render any verdict at all.</summary>
    public int MinimumAnswers { get; set; } = 2;

    /// <summary>Identifies the active comparison rules so verdicts can be diffed across rule changes.</summary>
    public string MatcherVersion { get; set; } = "rules-v1";

    /// <summary>Relative tolerance for Scalar comparisons, e.g. 0.10 accepts values within ten percent.</summary>
    public double ScalarTolerance { get; set; } = 0.10;
}
