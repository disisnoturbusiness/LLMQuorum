using LLMQuorum.Core.Models;

namespace LLMQuorum.Core.Providers;

/// <summary>
/// Asks one provider one question and reports what came back, including the
/// exact wire bytes. Implementations never throw for provider-side problems;
/// a failure is returned as a ProviderAnswer with IsSuccess false.
/// Why failures are values rather than exceptions: a silently missing voice
/// must never be counted as agreement, so every attempt has to produce a row.
/// </summary>
public interface IQuorumProvider
{
    /// <summary>The provider this instance speaks to.</summary>
    ProviderDefinition Definition { get; }

    /// <summary>
    /// Sends the prompt verbatim and returns the outcome. The prompt is not
    /// modified, decorated, or wrapped, because identical input across seats is
    /// the only thing that makes their answers comparable.
    /// </summary>
    /// <param name="prompt">Exact question text, identical for every provider in the panel.</param>
    /// <param name="rank">1-based order in which this provider was consulted for this question.</param>
    /// <param name="cancellationToken">Cancels an in-flight request.</param>
    /// <returns>The answer, or a populated failure record. Never null, never throws for provider errors.</returns>
    Task<ProviderAnswer> AskAsync( string prompt, int rank, CancellationToken cancellationToken = default );
}
