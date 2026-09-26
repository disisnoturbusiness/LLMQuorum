using Hangfire;
using LLMQuorum.Core.Engine;

namespace LLMQuorum.Web.Jobs;

/// <summary>
/// The Hangfire entry point for a sweep. Kept deliberately thin: it owns
/// scheduling concerns only and delegates every decision to QuorumRunner.
///
/// Automatic retries are DISABLED. Hangfire's default is to retry a failed job
/// ten times, which on this workload would re-ask providers that already
/// answered and burn quota that cannot be recovered - one seat on this panel
/// allows a thousand calls a MONTH. Answers are persisted per escalation batch,
/// so the correct recovery is to inspect what landed and resume, not to replay
/// the whole sweep blindly.
/// </summary>
public sealed class SweepJob
{
    #region Data Members

    private readonly QuorumRunner _runner;
    private readonly ILogger<SweepJob> _logger;

    #endregion Data Members

    #region Constructor

    /// <summary>Builds the job with the runner and a logger supplied by DI.</summary>
    /// <param name="runner">Executes the question set against the ladder.</param>
    /// <param name="logger">Receives per-question progress so a long sweep is observable.</param>
    public SweepJob( QuorumRunner runner, ILogger<SweepJob> logger )
    {
        _runner = runner;
        _logger = logger;
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>
    /// Runs one question set end to end. Progress is logged per question rather
    /// than buffered, because a sweep across six providers and fifty questions
    /// runs for many minutes and silent progress is indistinguishable from a hang.
    /// </summary>
    /// <param name="questionSetId">Set to execute.</param>
    /// <param name="label">Optional human label recorded against the run.</param>
    /// <param name="cancellationToken">Supplied by Hangfire when the job is aborted.</param>
    [AutomaticRetry( Attempts = 0 )]
    [JobDisplayName( "LLMQuorum sweep: set {0}" )]
    public async Task ExecuteAsync( int questionSetId, string? label, CancellationToken cancellationToken )
    {
        var progress = new Progress<QuestionProgress>( report =>
            _logger.LogInformation(
                "Q{Ordinal}/{Total} -> {Outcome} [{Signature}] via {Path}",
                report.Ordinal,
                report.Total,
                report.Verdict.Outcome,
                report.Verdict.PartitionSignature,
                string.Join( " > ", report.Verdict.EscalationPath ) ) );

        var result = await _runner.RunSetAsync( questionSetId, label, progress, cancellationToken );

        if( result.ProviderFailures.Count > 0 )
        {
            // A shrunken panel is never allowed to look like a full one.
            _logger.LogWarning( "Providers unavailable this run: {Failures}",
                                string.Join( "; ", result.ProviderFailures ) );
        }

        _logger.LogInformation( "Run {RunId} complete: {Count} question(s).", result.RunId, result.Verdicts.Count );
    }

    #endregion Public Methods
}
