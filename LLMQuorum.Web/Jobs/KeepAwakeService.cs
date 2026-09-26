using System.Runtime.InteropServices;
using LLMQuorum.Core.Configuration;
using Microsoft.Data.SqlClient;

namespace LLMQuorum.Web.Jobs;

/// <summary>
/// Asks Windows not to idle-sleep while the sweep plan still has open items, the way a media player keeps a
/// PC awake during playback. It changes no power setting: the request lives only while this process runs and
/// is withdrawn when the plan is finished or the app stops. A closed lid set to sleep, or a manual sleep, still
/// sleeps. MEASURED 2026-09-18: the machine running the plan (DESKTOP-FT7TA0B, a laptop) idle-sleeps after
/// 10 minutes on AC, which would stall a multi-day plan overnight.
/// </summary>
public sealed class KeepAwakeService : IHostedService, IDisposable
{
    #region Data Members

    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;
    private static readonly TimeSpan _interval = TimeSpan.FromSeconds( 60 );

    private readonly QuorumConfig _config;
    private readonly ILogger<KeepAwakeService> _logger;
    private readonly ManualResetEventSlim _stop = new( false );
    private Thread? _thread;
    private bool? _lastRequested;

    #endregion Data Members

    #region Constructor

    /// <summary>Builds the service.</summary>
    public KeepAwakeService( QuorumConfig config, ILogger<KeepAwakeService> logger )
    {
        _config = config;
        _logger = logger;
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>
    /// Starts a dedicated thread. SetThreadExecutionState with ES_CONTINUOUS belongs to the calling thread,
    /// so it must be one long-lived thread, not async continuations that hop between pool threads.
    /// </summary>
    public Task StartAsync( CancellationToken cancellationToken )
    {
        if( OperatingSystem.IsWindows() )
        {
            _thread = new Thread( Loop ) { IsBackground = true, Name = "LLMQuorum keep-awake" };
            _thread.Start();
        }

        return Task.CompletedTask;
    }

    /// <summary>Withdraws the request and stops the thread.</summary>
    public Task StopAsync( CancellationToken cancellationToken )
    {
        _stop.Set();
        _thread?.Join( TimeSpan.FromSeconds( 5 ) );
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => _stop.Dispose();

    #endregion Public Methods

    #region Private Methods

    private void Loop()
    {
        do
        {
            var openItems = CountOpenPlanItems();
            var wanted = openItems != 0;
            var previous = SetThreadExecutionState( wanted ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED : ES_CONTINUOUS );

            if( wanted != _lastRequested )
            {
                _logger.LogInformation( "Keep-awake {State}: {Open} open plan items (API {Result})",
                                        wanted ? "ON" : "OFF", openItems < 0 ? "unknown" : openItems, previous == 0 ? "FAILED" : "ok" );
                _lastRequested = wanted;
            }
        }
        while( !_stop.Wait( _interval ) );

        SetThreadExecutionState( ES_CONTINUOUS );
        _logger.LogInformation( "Keep-awake OFF: app stopping" );
    }

    /// <summary>Open plan items; -1 when the database cannot be read, which keeps the machine awake to be safe.</summary>
    private int CountOpenPlanItems()
    {
        try
        {
            using var connection = new SqlConnection( _config.ConnectionString );
            connection.Open();
            using var command = new SqlCommand( "SELECT COUNT(*) FROM quorum.SweepPlan WHERE Status <> 'done';", connection );
            return Convert.ToInt32( command.ExecuteScalar() );
        }
        catch( SqlException ex )
        {
            _logger.LogWarning( ex, "Keep-awake could not read the plan; staying awake" );
            return -1;
        }
    }

    [DllImport( "kernel32.dll", SetLastError = true )]
    private static extern uint SetThreadExecutionState( uint esFlags );

    #endregion Private Methods
}
