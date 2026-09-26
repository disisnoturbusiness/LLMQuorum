namespace LLMQuorum.Core.Models;

/// <summary>
/// Describes how a question's answer should be compared. The shape decides
/// which comparison rules the matcher is allowed to apply.
/// Why: comparing two prose paragraphs and comparing two dollar amounts are
/// different problems. Declaring the shape up front keeps the matcher
/// deterministic instead of letting it guess.
/// </summary>
public enum AnswerShape
{
    /// <summary>Free prose. Hardest to compare; normalization only.</summary>
    Text,

    /// <summary>A single number, optionally with a unit. Compared with tolerance.</summary>
    Scalar,

    /// <summary>One value from a closed set. Compared by exact match after normalization.</summary>
    Enum,

    /// <summary>Yes/no. Compared after mapping common affirmations and negations.</summary>
    Bool,

    /// <summary>An unordered collection of values. Compared as a set.</summary>
    Set
}

/// <summary>
/// The outcome of running one question through the escalation ladder.
/// Why three states rather than a boolean: "nobody agreed" is a real and
/// useful answer, distinct from "the machinery broke". Collapsing them would
/// hide the single most informative result this tool produces.
/// </summary>
public enum QuorumOutcome
{
    /// <summary>An answer reached the required vote count. Report it.</summary>
    Quorum,

    /// <summary>Providers exhausted with no answer reaching quorum. Report the spread.</summary>
    Unresolved,

    /// <summary>Too few providers returned a usable answer to decide anything.</summary>
    Failed
}

/// <summary>
/// How a provider call failed, bucketed so the characterization matrix can
/// separate "this model is unreliable" from "this platform was busy".
/// Why: a 429 says nothing about model quality, but an empty body or an
/// unparseable response does. Lumping them together would poison the profile.
/// </summary>
public enum CallErrorClass
{
    /// <summary>No error.</summary>
    None,

    /// <summary>HTTP 429 or an explicit quota message.</summary>
    RateLimit,

    /// <summary>Provider reported temporary capacity exhaustion.</summary>
    Capacity,

    /// <summary>401/403. Bad or revoked key, or a denied project.</summary>
    Auth,

    /// <summary>Request exceeded the configured timeout.</summary>
    Timeout,

    /// <summary>HTTP 200 but no usable content. Seen on reasoning models that spend the whole budget thinking.</summary>
    Empty,

    /// <summary>Response arrived but did not match the expected envelope.</summary>
    Parse,

    /// <summary>Transport failure below HTTP.</summary>
    Network,

    /// <summary>
    /// The output limit cut the response off (finish_reason "length" or equivalent),
    /// or reasoning started and never finished. A limit problem, not a wrong answer,
    /// so it is never scored and never counted as a vote.
    /// </summary>
    Truncated,

    /// <summary>The model refuses this caller by policy, e.g. OpenRouter free models restricted to agentic harnesses.</summary>
    Gated,

    /// <summary>Anything else.</summary>
    Other
}

/// <summary>
/// One configured platform on the escalation ladder. The ladder walks
/// PLATFORMS, not models, because independence is a property of the platform
/// and its lab. Two providers serving the same lab's weights produce
/// correlated votes that look independent, which is the failure this whole
/// tool exists to detect.
/// </summary>
public sealed class ProviderDefinition
{
    /// <summary>Stable key used in config and as the database natural key.</summary>
    public required string Key { get; init; }

    /// <summary>Human-facing name shown under an answer in the portal.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Exact model identifier sent on the wire. Must match the provider's catalog exactly.</summary>
    public required string ModelId { get; init; }

    /// <summary>Who trained the weights. Used to flag correlated seats.</summary>
    public required string Lab { get; init; }

    /// <summary>Chat-completions endpoint, fully qualified.</summary>
    public required string Endpoint { get; init; }

    /// <summary>Name of the file under the keys directory holding this provider's credential.</summary>
    public required string ApiKeyFile { get; init; }

    /// <summary>Wire protocol. Almost everything is OpenAI-compatible; Anthropic is not.</summary>
    public ProviderProtocol Protocol { get; init; } = ProviderProtocol.OpenAiCompatible;

    /// <summary>1-based position in the ask order. Lower is asked earlier.</summary>
    public int LadderPosition { get; init; }

    /// <summary>Disabled providers are skipped without shifting anyone else's position.</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>Upper bound on output tokens. Some free tiers reject requests that merely ASK for too many.</summary>
    public int MaxTokens { get; init; } = 800;

    /// <summary>Per-request timeout in seconds.</summary>
    public int TimeoutSeconds { get; init; } = 120;

    /// <summary>Optional provider-specific extras merged into the request body verbatim.</summary>
    public Dictionary<string, object>? ExtraBody { get; init; }

    /// <summary>
    /// Working directory for process-backed providers, relative paths resolved
    /// against the config file. The Claude CLI loads per-project auto-memory keyed
    /// on its working directory, so running it from an empty sandbox is what stops
    /// a seat from reading stored answer keys and voting with them.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Claude CLI tools to allow, e.g. "WebSearch,WebFetch". Null or empty disables every tool,
    /// which is the memory-only seat. A web-search seat is a different voter from a memory seat
    /// on the same model and must carry a different seat identity.
    /// </summary>
    public string? CliTools { get; init; }

    /// <summary>Claude CLI max turns. One for a memory answer; web search needs several.</summary>
    public int CliMaxTurns { get; init; } = 1;
}

/// <summary>
/// Wire protocol families. Kept deliberately small: five of the six configured
/// providers speak the OpenAI chat-completions shape, so one client covers
/// them and only Anthropic needs its own.
/// </summary>
public enum ProviderProtocol
{
    /// <summary>POST /chat/completions with an OpenAI-shaped body and Bearer auth.</summary>
    OpenAiCompatible,

    /// <summary>Anthropic Messages API: x-api-key header, anthropic-version, different envelope.</summary>
    Anthropic,

    /// <summary>
    /// Claude Code CLI in print mode, authenticated by the user's Claude
    /// subscription login rather than an API key. No per-call billing; usage
    /// draws from the plan's limits.
    /// </summary>
    ClaudeCli
}

/// <summary>
/// A single question and, optionally, the answer key for it.
/// Why ExpectedAnswer is optional: the matcher that drives escalation never
/// looks at it. Only the offline scorer does. A question with no key still
/// produces a useful consensus reading, it just cannot be graded.
/// </summary>
public sealed class QuestionItem
{
    /// <summary>Database identity. Zero for a question not yet persisted.</summary>
    public int QuestionId { get; init; }

    /// <summary>Position within its set. Drives run order.</summary>
    public int Ordinal { get; init; }

    /// <summary>Taxonomy bucket. Drives the per-category columns of the characterization matrix.</summary>
    public required string Category { get; init; }

    /// <summary>The prompt sent verbatim to every provider. Identical text for all seats is what makes them comparable.</summary>
    public required string Prompt { get; init; }

    /// <summary>Declared comparison shape.</summary>
    public AnswerShape Shape { get; init; } = AnswerShape.Text;

    /// <summary>The known-correct answer, if one exists. Scorer only.</summary>
    public string? ExpectedAnswer { get; init; }
}

/// <summary>
/// The result of asking one provider one question, including failures.
/// Failures are first-class rows rather than dropped, because a silently
/// missing voice must never be counted as agreement.
/// </summary>
public sealed class ProviderAnswer
{
    /// <summary>Which provider produced this.</summary>
    public required ProviderDefinition Provider { get; init; }

    /// <summary>1-based order in which this provider was consulted for this question.</summary>
    public int Rank { get; init; }

    /// <summary>True only when a usable, non-empty answer came back.</summary>
    public bool IsSuccess { get; init; }

    /// <summary>Extracted assistant text. Null on failure.</summary>
    public string? AnswerText { get; init; }

    /// <summary>The exact wire response body. This is the record; everything else is a projection.</summary>
    public byte[]? ResponseBytes { get; init; }

    /// <summary>HTTP status, when the request got that far.</summary>
    public int? HttpStatus { get; init; }

    /// <summary>Round-trip time in milliseconds.</summary>
    public int LatencyMs { get; init; }

    /// <summary>Failure bucket. None on success.</summary>
    public CallErrorClass ErrorClass { get; init; } = CallErrorClass.None;

    /// <summary>Truncated error detail for diagnosis.</summary>
    public string? ErrorText { get; init; }

    /// <summary>Provider-reported stop reason, useful for spotting truncation.</summary>
    public string? FinishReason { get; init; }

    /// <summary>Prompt token count when reported.</summary>
    public int? PromptTokens { get; init; }

    /// <summary>Completion token count when reported.</summary>
    public int? CompletionTokens { get; init; }
}

/// <summary>
/// A group of providers that gave the same answer under the active match rules.
/// This is what gets rendered under an answer in the portal.
/// </summary>
public sealed class AnswerCluster
{
    /// <summary>The representative answer text for this cluster.</summary>
    public required string Answer { get; init; }

    /// <summary>Provider keys that landed in this cluster, in ask order.</summary>
    public required List<string> ProviderKeys { get; init; }

    /// <summary>Cluster size. Convenience over ProviderKeys.Count.</summary>
    public int Votes => ProviderKeys.Count;
}

/// <summary>
/// The matcher's decision for one question, fully re-computable from stored
/// provider responses. Re-running the matcher with different rules produces a
/// new verdict without spending a single API call, which matters when one
/// provider allows a thousand calls a month.
/// </summary>
public sealed class QuorumVerdict
{
    /// <summary>Quorum reached, exhausted without agreement, or not enough answers to judge.</summary>
    public QuorumOutcome Outcome { get; init; }

    /// <summary>The answer that reached quorum. Null unless Outcome is Quorum.</summary>
    public string? WinningAnswer { get; init; }

    /// <summary>Vote count for the winner, or for the largest cluster when unresolved.</summary>
    public int WinningVotes { get; init; }

    /// <summary>Every cluster, largest first. Rendered in full when unresolved.</summary>
    public required List<AnswerCluster> Clusters { get; init; }

    /// <summary>Cluster sizes joined by dashes, e.g. "3-0-0" or "2-2-1". Auditable at a glance, no fake precision.</summary>
    public required string PartitionSignature { get; init; }

    /// <summary>Which providers were consulted, in order. Reconstructs the escalation path.</summary>
    public required List<string> EscalationPath { get; init; }

    /// <summary>Identifies the rule set that produced this verdict so rule changes can be diffed.</summary>
    public required string MatcherVersion { get; init; }
}
