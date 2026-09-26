using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace LLMQuorum.Core.Sweep;

/// <summary>SQL access for quorum.SweepPlan. Decisions live in <see cref="PlanRules"/>.</summary>
public sealed class SweepPlanner
{
    #region Data Members

    private readonly string _connectionString;

    #endregion Data Members

    #region Constructor

    /// <summary>Builds the planner.</summary>
    /// <param name="connectionString">LLMQuorum database.</param>
    public SweepPlanner( string connectionString )
    {
        _connectionString = connectionString;
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>Every item of a plan.</summary>
    /// <param name="planName">Plan name.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Items.</returns>
    public async Task<List<PlanItem>> LoadAsync( string planName, CancellationToken cancellationToken )
    {
        const string SQL = @"
SELECT PlanItemId, PlanName, QuestionId, Ordinal, ProviderGroup, Round, SeatFilter, Status, NotBeforeUtc, Runs, UpdatedUtc
  FROM quorum.SweepPlan WHERE PlanName = @Plan ORDER BY ProviderGroup, Round, Ordinal;";

        var items = new List<PlanItem>();
        await using var connection = await OpenAsync( cancellationToken );
        await using var command = new SqlCommand( SQL, connection );
        command.Parameters.Add( "@Plan", SqlDbType.VarChar, 40 ).Value = planName;
        await using var reader = await command.ExecuteReaderAsync( cancellationToken );

        while( await reader.ReadAsync( cancellationToken ) )
        {
            var seats = reader.IsDBNull( 6 ) ? null : JsonSerializer.Deserialize<List<string>>( reader.GetString( 6 ) );
            items.Add( new PlanItem( reader.GetInt32( 0 ), reader.GetString( 1 ), reader.GetInt32( 2 ), reader.GetInt32( 3 ), reader.GetString( 4 ),
                                     reader.GetInt32( 5 ), seats, reader.GetString( 7 ), reader.IsDBNull( 8 ) ? null : reader.GetDateTime( 8 ),
                                     reader.GetInt32( 9 ), reader.GetDateTime( 10 ) ) );
        }

        return items;
    }

    /// <summary>Marks an item running so a restart can tell it was interrupted.</summary>
    public async Task MarkRunningAsync( int planItemId, CancellationToken cancellationToken )
    {
        await ExecuteAsync( "UPDATE quorum.SweepPlan SET Status = 'running', UpdatedUtc = SYSUTCDATETIME() WHERE PlanItemId = @Id;",
                            cancellationToken, ( "@Id", planItemId ) );
    }

    /// <summary>Records an item's result and queues its retry round when there is one.</summary>
    /// <param name="item">Item that ran.</param>
    /// <param name="sweepId">Sweep it produced.</param>
    /// <param name="evaluation">Result.</param>
    /// <param name="cancellationToken">Cancels the update.</param>
    public async Task CompleteAsync( PlanItem item, long sweepId, PlanEvaluation evaluation, CancellationToken cancellationToken )
    {
        await ExecuteAsync( @"
UPDATE quorum.SweepPlan SET Status = @Status, NotBeforeUtc = @NotBefore, LastSweepId = @Sweep, Runs = Runs + 1,
       Note = @Note, UpdatedUtc = SYSUTCDATETIME() WHERE PlanItemId = @Id;",
            cancellationToken, ( "@Status", evaluation.Status ), ( "@NotBefore", (object?)evaluation.NotBeforeUtc ?? DBNull.Value ),
            ( "@Sweep", sweepId ), ( "@Note", evaluation.Note ), ( "@Id", item.PlanItemId ) );

        if( evaluation.RetrySeats.Count > 0 )
        {
            await ExecuteAsync( @"
INSERT INTO quorum.SweepPlan ( PlanName, QuestionId, Ordinal, ProviderGroup, Round, SeatFilter, Status, NotBeforeUtc, Note )
VALUES ( @Plan, @Q, @Ordinal, @Group, @Round, @Seats, 'pending', @NotBefore, @Note );",
                cancellationToken, ( "@Plan", item.PlanName ), ( "@Q", item.QuestionId ), ( "@Ordinal", item.Ordinal ),
                ( "@Group", item.ProviderGroup ), ( "@Round", item.Round + 1 ), ( "@Seats", JsonSerializer.Serialize( evaluation.RetrySeats ) ),
                ( "@NotBefore", (object?)evaluation.RetryNotBeforeUtc ?? DBNull.Value ), ( "@Note", $"retry of item {item.PlanItemId}" ) );
        }
    }

    /// <summary>Final attempt per seat in a sweep.</summary>
    /// <param name="sweepId">Sweep.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Outcomes.</returns>
    public async Task<List<SeatOutcome>> LoadOutcomesAsync( long sweepId, CancellationToken cancellationToken )
    {
        const string SQL = @"
WITH ranked AS (
    SELECT SeatId, Platform, Status, Decision, ErrorText,
           ROW_NUMBER() OVER ( PARTITION BY SeatId ORDER BY AttemptNo DESC, SweepCallId DESC ) AS rn
      FROM quorum.SweepCall WHERE SweepId = @Sweep AND SeatId IS NOT NULL )
SELECT SeatId, Platform, ISNULL( Status, 'Unknown' ), Decision, ErrorText FROM ranked WHERE rn = 1;";

        var outcomes = new List<SeatOutcome>();
        await using var connection = await OpenAsync( cancellationToken );
        await using var command = new SqlCommand( SQL, connection );
        command.Parameters.Add( "@Sweep", SqlDbType.BigInt ).Value = sweepId;
        await using var reader = await command.ExecuteReaderAsync( cancellationToken );

        while( await reader.ReadAsync( cancellationToken ) )
        {
            outcomes.Add( new SeatOutcome( reader.GetString( 0 ), reader.GetString( 1 ), reader.GetString( 2 ),
                                           reader.IsDBNull( 3 ) ? null : reader.GetString( 3 ), reader.IsDBNull( 4 ) ? null : reader.GetString( 4 ) ) );
        }

        return outcomes;
    }

    /// <summary>OpenRouter requests recorded in the quota ledger for the current UTC day.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Requests used.</returns>
    public async Task<int> OpenRouterUsedTodayAsync( CancellationToken cancellationToken )
    {
        await using var connection = await OpenAsync( cancellationToken );
        await using var command = new SqlCommand( @"
SELECT ISNULL( SUM( Amount ), 0 ) FROM quorum.QuotaLedger
 WHERE Provider = 'openrouter' AND Unit = 'request' AND WindowKey = @Day;", connection );
        command.Parameters.Add( "@Day", SqlDbType.VarChar, 10 ).Value = DateTime.UtcNow.ToString( "yyyy-MM-dd" );
        return Convert.ToInt32( await command.ExecuteScalarAsync( cancellationToken ) );
    }

    #endregion Public Methods

    #region Private Methods

    private async Task<SqlConnection> OpenAsync( CancellationToken cancellationToken )
    {
        var connection = new SqlConnection( _connectionString );
        await connection.OpenAsync( cancellationToken );
        return connection;
    }

    private async Task ExecuteAsync( string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters )
    {
        await using var connection = await OpenAsync( cancellationToken );
        await using var command = new SqlCommand( sql, connection );

        foreach( var (name, value) in parameters )
        {
            command.Parameters.AddWithValue( name, value );
        }

        await command.ExecuteNonQueryAsync( cancellationToken );
    }

    #endregion Private Methods
}
