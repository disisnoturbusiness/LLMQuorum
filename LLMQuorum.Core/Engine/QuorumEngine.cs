using LLMQuorum.Core.Configuration;
using LLMQuorum.Core.Matching;
using LLMQuorum.Core.Models;

namespace LLMQuorum.Core.Engine;

/// <summary>
/// Runs one question through the escalation ladder and produces a verdict.
///
/// THE RULE, in one line: keep consulting providers until some answer reaches
/// the quorum size, or the ladder runs out.
///
/// Escalation WIDTH is derived, never looked up:
///     needed = QuorumSize - (votes held by the current leader)
/// clamped to however many providers remain. That single expression reproduces
/// every case the ladder is meant to handle:
///     2-1     leader has 2, needs 1  -> consult 1 more
///     1-1-1   leader has 1, needs 2  -> consult 2 more
///     2-1-1   leader has 2, needs 1  -> consult 1 more
///     2-2     leaders have 2, need 1 -> consult 1 more
///     2-2-1   leader has 2, needs 1  -> consult 1 more
///     2-2-2   ladder exhausted       -> UNRESOLVED, show all three
/// Why derived rather than a table: a table has to be re-derived by hand every
/// time the quorum size or ladder depth changes, and a stale cell would silently
/// stop escalating at the wrong moment.
///
/// The base round is unanimous-or-escalate on purpose. Two out of three is not
/// good enough to stop, matching the rule that one dissenting check kills the
/// result rather than being outvoted.
/// </summary>
public sealed class QuorumEngine
{
    #region Data Members

    private readonly PanelSettings _settings;
    private readonly IAnswerMatcher _matcher;

    #endregion Data Members

    #region Constructor

    /// <summary>Builds an engine bound to one panel configuration and rule set.</summary>
    /// <param name="settings">Base count, quorum size and minimum-answer threshold.</param>
    /// <param name="matcher">Comparison rules used to cluster answers.</param>
    public QuorumEngine( PanelSettings settings, IAnswerMatcher matcher )
    {
        _settings = settings;
        _matcher = matcher;
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>
    /// Consults providers in ladder order until quorum or exhaustion, invoking
    /// the supplied callback for each batch so the caller can persist answers as
    /// they arrive rather than only at the end. Persisting per batch is what
    /// makes a crashed run resumable and keeps spent quota from being wasted.
    /// </summary>
    /// <param name="question">The question, including its declared answer shape.</param>
    /// <param name="ladder">Providers in ask order. Position in this list is the ask order.</param>
    /// <param name="askAsync">Invoked per provider; returns that provider's answer or failure.</param>
    /// <param name="onBatchComplete">Invoked after each batch with the answers from that batch.</param>
    /// <param name="cancellationToken">Cancels the run between and during batches.</param>
    /// <returns>The verdict plus every answer collected, in ask order.</returns>
    public async Task<QuorumRunResult> RunAsync(
        QuestionItem question,
        IReadOnlyList<ProviderDefinition> ladder,
        Func<ProviderDefinition, int, CancellationToken, Task<ProviderAnswer>> askAsync,
        Func<IReadOnlyList<ProviderAnswer>, Task>? onBatchComplete = null,
        CancellationToken cancellationToken = default )
    {
        var collected = new List<ProviderAnswer>();
        var nextIndex = 0;
        var batchSize = Math.Min( _settings.BaseCount, ladder.Count );

        while( batchSize > 0 && nextIndex < ladder.Count )
        {
            var batch = await AskBatchAsync( ladder, nextIndex, batchSize, collected.Count, askAsync, cancellationToken );
            collected.AddRange( batch );
            nextIndex += batchSize;

            if( onBatchComplete is not null )
            {
                await onBatchComplete( batch );
            }

            var clusters = _matcher.Cluster( collected, question.Shape );

            if( clusters.Count > 0 && clusters[0].Votes >= _settings.QuorumSize )
            {
                return Build( QuorumOutcome.Quorum, clusters, collected );
            }

            batchSize = CalculateEscalation( clusters, ladder.Count - nextIndex );
        }

        var finalClusters = _matcher.Cluster( collected, question.Shape );
        var successes = collected.Count( a => a.IsSuccess );

        var outcome = successes < _settings.MinimumAnswers
            ? QuorumOutcome.Failed
            : QuorumOutcome.Unresolved;

        return Build( outcome, finalClusters, collected );
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// Consults one batch of providers concurrently. Concurrency is safe and
    /// correct here because seats are independent by construction: no provider
    /// sees another's answer, which is what makes their agreement meaningful.
    /// </summary>
    /// <param name="ladder">Full ladder in ask order.</param>
    /// <param name="startIndex">Index of the first provider in this batch.</param>
    /// <param name="count">How many providers to consult.</param>
    /// <param name="ranksAlreadyAsked">Answers already collected, used to continue rank numbering.</param>
    /// <param name="askAsync">Per-provider call delegate.</param>
    /// <param name="cancellationToken">Cancels in-flight calls.</param>
    /// <returns>Answers in ladder order, one per provider consulted.</returns>
    private static async Task<List<ProviderAnswer>> AskBatchAsync(
        IReadOnlyList<ProviderDefinition> ladder,
        int startIndex,
        int count,
        int ranksAlreadyAsked,
        Func<ProviderDefinition, int, CancellationToken, Task<ProviderAnswer>> askAsync,
        CancellationToken cancellationToken )
    {
        var tasks = new List<Task<ProviderAnswer>>( count );

        for( var offset = 0; offset < count && startIndex + offset < ladder.Count; offset++ )
        {
            var provider = ladder[startIndex + offset];
            var rank = ranksAlreadyAsked + offset + 1;
            tasks.Add( askAsync( provider, rank, cancellationToken ) );
        }

        var answers = await Task.WhenAll( tasks );
        return answers.OrderBy( a => a.Rank ).ToList();
    }

    /// <summary>
    /// Derives how many more providers to consult. Returns zero when the ladder
    /// is exhausted, which is the only stopping condition besides reaching
    /// quorum.
    /// </summary>
    /// <param name="clusters">Current clusters, largest first.</param>
    /// <param name="providersRemaining">How many ladder entries are still unconsulted.</param>
    /// <returns>Batch size for the next round; zero to stop.</returns>
    private int CalculateEscalation( IReadOnlyList<AnswerCluster> clusters, int providersRemaining )
    {
        if( providersRemaining <= 0 )
        {
            return 0;
        }

        var leaderVotes = clusters.Count > 0 ? clusters[0].Votes : 0;
        var needed = _settings.QuorumSize - leaderVotes;

        // A leader already at quorum is handled by the caller; defensively treat
        // a non-positive requirement as "one more" so the loop always advances.
        return Math.Min( Math.Max( needed, 1 ), providersRemaining );
    }

    /// <summary>Assembles the verdict, including the partition signature used for eyeball auditing.</summary>
    /// <param name="outcome">Quorum, unresolved, or failed.</param>
    /// <param name="clusters">Final clusters, largest first.</param>
    /// <param name="answers">Every answer collected, in ask order.</param>
    /// <returns>The completed run result.</returns>
    private QuorumRunResult Build(
        QuorumOutcome outcome,
        List<AnswerCluster> clusters,
        List<ProviderAnswer> answers )
    {
        var signature = clusters.Count > 0
            ? string.Join( "-", clusters.Select( c => c.Votes ) )
            : "0";

        var verdict = new QuorumVerdict
        {
            Outcome = outcome,
            WinningAnswer = outcome == QuorumOutcome.Quorum && clusters.Count > 0 ? clusters[0].Answer : null,
            WinningVotes = clusters.Count > 0 ? clusters[0].Votes : 0,
            Clusters = clusters,
            PartitionSignature = signature,
            EscalationPath = answers.OrderBy( a => a.Rank ).Select( a => a.Provider.Key ).ToList(),
            MatcherVersion = _matcher.Version
        };

        return new QuorumRunResult { Verdict = verdict, Answers = answers };
    }

    #endregion Private Methods
}

/// <summary>
/// Everything one question produced: the verdict and every raw answer behind it.
/// Both are returned together so the caller can persist the answers and the
/// verdict in a single transaction, keeping them consistent.
/// </summary>
public sealed class QuorumRunResult
{
    /// <summary>The matcher's decision for this question.</summary>
    public required QuorumVerdict Verdict { get; init; }

    /// <summary>Every provider answer collected, in ask order, successes and failures alike.</summary>
    public required List<ProviderAnswer> Answers { get; init; }
}
