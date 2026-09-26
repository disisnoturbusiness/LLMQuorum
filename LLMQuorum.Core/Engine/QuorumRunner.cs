using LLMQuorum.Core.Configuration;
using LLMQuorum.Core.Matching;
using LLMQuorum.Core.Models;
using LLMQuorum.Core.Providers;
using LLMQuorum.Core.Storage;

namespace LLMQuorum.Core.Engine;

/// <summary>
/// Ties the pieces together: resolve the ladder, walk a question set, persist
/// every answer as it arrives, and close each question with a verdict.
///
/// Persistence happens per escalation batch rather than at the end of a run.
/// That matters because one provider on this panel allows a thousand calls a
/// MONTH: if a sweep dies at question forty, the quota already spent on the
/// first thirty-nine must already be on disk and replayable.
/// </summary>
public sealed class QuorumRunner
{
    #region Data Members

    private readonly QuorumConfig _config;
    private readonly ProviderFactory _providerFactory;
    private readonly QuorumRepository _repository;

    #endregion Data Members

    #region Constructor

    /// <summary>Builds a runner over the configured panel and database.</summary>
    /// <param name="config">Resolved configuration.</param>
    /// <param name="providerFactory">Creates provider clients with credentials attached.</param>
    /// <param name="repository">Persistence for runs, answers and verdicts.</param>
    public QuorumRunner( QuorumConfig config, ProviderFactory providerFactory, QuorumRepository repository )
    {
        _config = config;
        _providerFactory = providerFactory;
        _repository = repository;
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>
    /// Executes every enabled question in a set and returns the verdicts.
    /// Questions run sequentially on purpose: providers are consulted
    /// concurrently WITHIN a question, but running whole questions in parallel
    /// would multiply the request rate and trip the tightest free tier, whose
    /// ceiling is measured in calls per day rather than per minute.
    /// </summary>
    /// <param name="questionSetId">Set to execute.</param>
    /// <param name="label">Optional human label recorded against the run.</param>
    /// <param name="progress">Reports each completed question for live portal updates.</param>
    /// <param name="cancellationToken">Cancels the sweep between questions.</param>
    /// <returns>The run identity and one verdict per question, in run order.</returns>
    public async Task<SweepResult> RunSetAsync(
        int questionSetId,
        string? label = null,
        IProgress<QuestionProgress>? progress = null,
        CancellationToken cancellationToken = default )
    {
        var clients = _providerFactory.CreateLadder( out var failures );

        if( clients.Count < _config.Panel.MinimumAnswers )
        {
            throw new InvalidOperationException(
                $"Only {clients.Count} provider(s) could be constructed; " +
                $"need at least {_config.Panel.MinimumAnswers}. Failures: {string.Join( "; ", failures )}" );
        }

        var ladder = clients.Select( c => c.Definition ).ToList();
        var clientsByKey = clients.ToDictionary( c => c.Definition.Key, StringComparer.OrdinalIgnoreCase );

        var providerIds = await _repository.SyncProvidersAsync( ladder, cancellationToken );
        var questions = await _repository.GetQuestionsAsync( questionSetId, cancellationToken );

        var runId = await _repository.CreateRunAsync(
            questionSetId, label, _config.ToSnapshotJson(),
            _config.Panel.QuorumSize, _config.Panel.BaseCount, cancellationToken );

        var matcher = new RuleBasedMatcher( _config.Panel.MatcherVersion, _config.Panel.ScalarTolerance );
        var engine = new QuorumEngine( _config.Panel, matcher );
        var verdicts = new List<QuorumVerdict>();

        foreach( var question in questions )
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await RunOneAsync(
                engine, question, ladder, clientsByKey, providerIds, runId, cancellationToken );

            verdicts.Add( result.Verdict );
            progress?.Report( new QuestionProgress
            {
                Ordinal = question.Ordinal,
                Total = questions.Count,
                Prompt = question.Prompt,
                Verdict = result.Verdict
            } );
        }

        await _repository.CompleteRunAsync( runId, cancellationToken );
        return new SweepResult { RunId = runId, Verdicts = verdicts, ProviderFailures = failures };
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// Runs one question end to end: open the row, walk the ladder persisting
    /// each batch, then write the verdict and close the row in one transaction.
    /// </summary>
    /// <param name="engine">Escalation state machine.</param>
    /// <param name="question">Question to ask.</param>
    /// <param name="ladder">Providers in ask order.</param>
    /// <param name="clientsByKey">Constructed clients indexed by provider key.</param>
    /// <param name="providerIds">Provider key to database identity.</param>
    /// <param name="runId">Parent run.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    /// <returns>The verdict and every answer behind it.</returns>
    private async Task<QuorumRunResult> RunOneAsync(
        QuorumEngine engine,
        QuestionItem question,
        IReadOnlyList<ProviderDefinition> ladder,
        IReadOnlyDictionary<string, IQuorumProvider> clientsByKey,
        IReadOnlyDictionary<string, int> providerIds,
        long runId,
        CancellationToken cancellationToken )
    {
        var questionRunId = await _repository.OpenQuestionRunAsync( runId, question.QuestionId, cancellationToken );

        var result = await engine.RunAsync(
            question,
            ladder,
            ( definition, rank, token ) => clientsByKey[definition.Key].AskAsync( question.Prompt, rank, token ),
            batch => _repository.SaveAnswersAsync( questionRunId, providerIds, batch, cancellationToken ),
            cancellationToken );

        await _repository.CompleteQuestionRunAsync(
            questionRunId, result.Verdict, result.Answers.Count, cancellationToken );

        return result;
    }

    #endregion Private Methods
}

/// <summary>Progress for one completed question, used to drive live portal updates.</summary>
public sealed class QuestionProgress
{
    /// <summary>Position of this question within its set.</summary>
    public int Ordinal { get; init; }

    /// <summary>Total enabled questions in the set.</summary>
    public int Total { get; init; }

    /// <summary>The question text, echoed for display.</summary>
    public required string Prompt { get; init; }

    /// <summary>What the panel decided.</summary>
    public required QuorumVerdict Verdict { get; init; }
}

/// <summary>The outcome of one full sweep over a question set.</summary>
public sealed class SweepResult
{
    /// <summary>Database identity of the run, for drilling into stored answers.</summary>
    public long RunId { get; init; }

    /// <summary>One verdict per question, in run order.</summary>
    public required List<QuorumVerdict> Verdicts { get; init; }

    /// <summary>
    /// Providers that could not be constructed, usually a missing credential.
    /// Surfaced rather than swallowed so a shrunken panel is never mistaken for
    /// a full one.
    /// </summary>
    public required List<string> ProviderFailures { get; init; }
}
