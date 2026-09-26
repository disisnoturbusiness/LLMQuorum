using LLMQuorum.Core.Models;

namespace LLMQuorum.Core.Matching;

/// <summary>
/// Decides whether two answers say the same thing. This runs INLINE, inside the
/// escalation loop, because the decision to consult another provider depends on
/// how the current answers cluster.
/// Why it is an interface: the comparison rules are the part most likely to
/// change, and every change needs to be replayable over stored responses rather
/// than re-run against live quota. Swapping the implementation and replaying is
/// the whole point.
/// A matcher decides SAMENESS ONLY. It never decides correctness, and it never
/// sees the answer key. Correctness is the offline scorer's job.
/// </summary>
public interface IAnswerMatcher
{
    /// <summary>Identifies this rule set so verdicts produced under different rules can be diffed.</summary>
    string Version { get; }

    /// <summary>
    /// Reduces an answer to a comparison key. Two answers are the same when
    /// their keys are equal, which keeps clustering transitive. Deriving a key
    /// rather than comparing pairwise avoids the ambiguity where A matches B and
    /// B matches C but A does not match C.
    /// </summary>
    /// <param name="answerText">Raw assistant text as returned by the provider.</param>
    /// <param name="shape">Declared answer shape for the question being compared.</param>
    /// <returns>A normalized key, or null when the answer carries nothing comparable.</returns>
    string? BuildKey( string answerText, AnswerShape shape );

    /// <summary>
    /// Groups successful answers into clusters of equal keys, largest first.
    /// Failed calls are excluded here so a silently missing voice can never be
    /// counted toward agreement.
    /// </summary>
    /// <param name="answers">Every answer collected so far for one question.</param>
    /// <param name="shape">Declared answer shape for the question being compared.</param>
    /// <returns>Clusters ordered by vote count descending, then by first ask order.</returns>
    List<AnswerCluster> Cluster( IReadOnlyList<ProviderAnswer> answers, AnswerShape shape );
}
