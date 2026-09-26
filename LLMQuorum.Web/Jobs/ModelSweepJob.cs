using Hangfire;
using LLMQuorum.Core.Configuration;
using LLMQuorum.Core.Sweep;

namespace LLMQuorum.Web.Jobs;

/// <summary>
/// Runs one question across every enabled seat in profiles.json and writes the graded report.
/// Retries are disabled for the same reason as the panel sweep: a blind replay re-asks seats
/// that already answered and spends allowances that reset only daily or monthly.
/// </summary>
public sealed class ModelSweepJob
{
    #region Data Members

    private readonly QuorumConfig _config;
    private readonly ILogger<ModelSweepJob> _logger;

    #endregion Data Members

    #region Constructor

    /// <summary>Builds the job.</summary>
    /// <param name="config">Resolved configuration.</param>
    /// <param name="logger">Receives one line per attempt.</param>
    public ModelSweepJob( QuorumConfig config, ILogger<ModelSweepJob> logger )
    {
        _config = config;
        _logger = logger;
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>Runs the sweep, writes sweeps\sweep-{id}.md, and refreshes sweeps\question-{questionId}.xlsx.</summary>
    /// <param name="questionId">Question to ask.</param>
    /// <param name="label">Optional label.</param>
    /// <param name="providers">Optional provider keys to restrict to; empty means all.</param>
    /// <param name="seats">Optional seat id fragments to restrict to; empty means all.</param>
    /// <param name="cancellationToken">Supplied by Hangfire on abort.</param>
    [AutomaticRetry( Attempts = 0 )]
    [JobDisplayName( "LLMQuorum model sweep: question {0}" )]
    public async Task ExecuteAsync(
        int questionId, string? label, string[]? providers, string[]? seats, CancellationToken cancellationToken )
    {
        await RunAsync( questionId, label, providers, seats, cancellationToken );
    }

    /// <summary>Runs the sweep, writes the report and sheet, and returns the sweep id (used by the plan runner).</summary>
    /// <param name="questionId">Question to ask.</param>
    /// <param name="label">Optional label.</param>
    /// <param name="providers">Optional provider keys to restrict to.</param>
    /// <param name="seats">Optional seat id fragments to restrict to.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The sweep id.</returns>
    public async Task<long> RunAsync(
        int questionId, string? label, string[]? providers, string[]? seats, CancellationToken cancellationToken )
    {
        var profiles = ProfileCatalog.Load( _config.ResolvePath( _config.ProfilesPath ) );
        var ledger = new QuotaLedger( _config.ConnectionString );
        var caller = new ProfileCaller( _config.KeysDirectory, _config.ConfigDirectory, ledger );
        var runner = new SweepRunner( _config.ConnectionString, caller );
        var progress = new Progress<string>( line => _logger.LogInformation( "{Line}", line ) );

        var sweepId = await runner.RunAsync( questionId, profiles, label, providers, seats, progress, cancellationToken );

        var directory = _config.ResolvePath( _config.SweepReportDirectory );
        Directory.CreateDirectory( directory );
        var path = Path.Combine( directory, $"sweep-{sweepId}.md" );
        await new SweepReport( _config.ConnectionString ).WriteAsync( sweepId, path, cancellationToken );

        var sheetPath = Path.Combine( directory, $"question-{questionId}.xlsx" );
        var sheet = await QuestionSheetFactory.Create( _config ).WriteAsync( questionId, sheetPath, cancellationToken );

        _logger.LogInformation( "Sweep {SweepId} complete, report {Path}, sheet {SheetPath}", sweepId, path, sheet.Path );
        return sweepId;
    }

    #endregion Public Methods
}
