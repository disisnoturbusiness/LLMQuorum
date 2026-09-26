using System.Data;
using System.Text.Json;
using LLMQuorum.Core.Models;
using Microsoft.Data.SqlClient;

namespace LLMQuorum.Core.Storage;

/// <summary>
/// All persistence for LLMQuorum. Plain ADO.NET rather than an ORM because the
/// write path is append-only and shaped by hand, and because the raw response
/// bytes must reach VARBINARY(MAX) untouched by any mapper.
///
/// The contract this class enforces: ProviderCall rows are written once and
/// never updated. Verdicts are versioned rather than overwritten, so re-running
/// the matcher under new rules adds a row and leaves the old decision intact
/// for comparison.
/// </summary>
public sealed class QuorumRepository
{
    #region Data Members

    /// <summary>Serializer settings for the cluster payload stored against a verdict.</summary>
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = false };

    private readonly string _connectionString;

    #endregion Data Members

    #region Constructor

    /// <summary>Builds a repository over one database.</summary>
    /// <param name="connectionString">ADO.NET connection string for the LLMQuorum database.</param>
    public QuorumRepository( string connectionString )
    {
        _connectionString = connectionString;
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>
    /// Reconciles the Provider table with the configured ladder. Providers are
    /// matched on their stable key so reordering the ladder updates positions in
    /// place rather than orphaning historical calls, which would break the
    /// characterization matrix.
    /// </summary>
    /// <param name="ladder">Enabled providers in ask order.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Provider key to database identity, for use when writing calls.</returns>
    public async Task<Dictionary<string, int>> SyncProvidersAsync(
        IReadOnlyList<ProviderDefinition> ladder,
        CancellationToken cancellationToken = default )
    {
        await using var connection = await OpenAsync( cancellationToken );

        // Clear positions first: LadderPosition is unique, so a reorder would
        // otherwise collide with a position still held by another provider.
        await using( var reset = new SqlCommand(
            "UPDATE quorum.Provider SET LadderPosition = -ProviderId;", connection ) )
        {
            await reset.ExecuteNonQueryAsync( cancellationToken );
        }

        foreach( var provider in ladder )
        {
            await using var command = new SqlCommand( @"
MERGE quorum.Provider AS target
USING ( SELECT @Key AS ProviderKey ) AS source
   ON target.ProviderKey = source.ProviderKey
WHEN MATCHED THEN
    UPDATE SET DisplayName = @DisplayName, ModelId = @ModelId, Lab = @Lab,
               BaseUrl = @BaseUrl, LadderPosition = @Position, IsEnabled = 1
WHEN NOT MATCHED THEN
    INSERT ( ProviderKey, DisplayName, ModelId, Lab, BaseUrl, LadderPosition, IsEnabled )
    VALUES ( @Key, @DisplayName, @ModelId, @Lab, @BaseUrl, @Position, 1 );", connection );

            command.Parameters.AddWithValue( "@Key", provider.Key );
            command.Parameters.AddWithValue( "@DisplayName", provider.DisplayName );
            command.Parameters.AddWithValue( "@ModelId", provider.ModelId );
            command.Parameters.AddWithValue( "@Lab", provider.Lab );
            command.Parameters.AddWithValue( "@BaseUrl", provider.Endpoint );
            command.Parameters.AddWithValue( "@Position", provider.LadderPosition );
            await command.ExecuteNonQueryAsync( cancellationToken );
        }

        return await LoadProviderIdsAsync( connection, cancellationToken );
    }

    /// <summary>
    /// Opens a run and snapshots the resolved panel config against it. Without
    /// the snapshot a result stops being reproducible the moment the ladder is
    /// reordered, and cross-run comparisons become meaningless.
    /// </summary>
    /// <param name="questionSetId">Set being executed.</param>
    /// <param name="label">Optional human label for the run.</param>
    /// <param name="configSnapshotJson">Serialized panel settings and ladder.</param>
    /// <param name="quorumSize">Votes required to win, recorded for reproducibility.</param>
    /// <param name="baseCount">Providers asked before any escalation.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The new run identity.</returns>
    public async Task<long> CreateRunAsync(
        int questionSetId,
        string? label,
        string configSnapshotJson,
        int quorumSize,
        int baseCount,
        CancellationToken cancellationToken = default )
    {
        const string SQL = @"
INSERT INTO quorum.Run ( QuestionSetId, Label, ConfigSnapshot, QuorumSize, BaseCount )
OUTPUT INSERTED.RunId
VALUES ( @QuestionSetId, @Label, @ConfigSnapshot, @QuorumSize, @BaseCount );";

        await using var connection = await OpenAsync( cancellationToken );
        await using var command = new SqlCommand( SQL, connection );
        command.Parameters.AddWithValue( "@QuestionSetId", questionSetId );
        command.Parameters.AddWithValue( "@Label", (object?)label ?? DBNull.Value );
        command.Parameters.AddWithValue( "@ConfigSnapshot", configSnapshotJson );
        command.Parameters.AddWithValue( "@QuorumSize", quorumSize );
        command.Parameters.AddWithValue( "@BaseCount", baseCount );

        return Convert.ToInt64( await command.ExecuteScalarAsync( cancellationToken ) );
    }

    /// <summary>
    /// Writes the open row for one question. CompletedUtc stays NULL until the
    /// engine finishes, so a row left open after a crash IS the crash signal and
    /// no separate status column can drift out of sync with reality.
    /// </summary>
    /// <param name="runId">Parent run.</param>
    /// <param name="questionId">Question being asked.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The new question-run identity.</returns>
    public async Task<long> OpenQuestionRunAsync(
        long runId, int questionId, CancellationToken cancellationToken = default )
    {
        const string SQL = @"
INSERT INTO quorum.QuestionRun ( RunId, QuestionId )
OUTPUT INSERTED.QuestionRunId
VALUES ( @RunId, @QuestionId );";

        await using var connection = await OpenAsync( cancellationToken );
        await using var command = new SqlCommand( SQL, connection );
        command.Parameters.AddWithValue( "@RunId", runId );
        command.Parameters.AddWithValue( "@QuestionId", questionId );

        return Convert.ToInt64( await command.ExecuteScalarAsync( cancellationToken ) );
    }

    /// <summary>
    /// Appends provider calls. Called once per escalation batch rather than once
    /// per run so that spent quota is durable even if the process dies mid-ladder.
    /// </summary>
    /// <param name="questionRunId">Parent question run.</param>
    /// <param name="providerIds">Provider key to database identity.</param>
    /// <param name="answers">Answers from one batch, successes and failures alike.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task SaveAnswersAsync(
        long questionRunId,
        IReadOnlyDictionary<string, int> providerIds,
        IReadOnlyList<ProviderAnswer> answers,
        CancellationToken cancellationToken = default )
    {
        const string SQL = @"
INSERT INTO quorum.ProviderCall
    ( QuestionRunId, ProviderId, Rank, LatencyMs, HttpStatus, IsSuccess,
      ErrorClass, ErrorText, FinishReason, PromptTokens, CompletionTokens,
      ResponseBytes, AnswerText )
VALUES
    ( @QuestionRunId, @ProviderId, @Rank, @LatencyMs, @HttpStatus, @IsSuccess,
      @ErrorClass, @ErrorText, @FinishReason, @PromptTokens, @CompletionTokens,
      @ResponseBytes, @AnswerText );";

        await using var connection = await OpenAsync( cancellationToken );

        foreach( var answer in answers )
        {
            await using var command = new SqlCommand( SQL, connection );
            command.Parameters.AddWithValue( "@QuestionRunId", questionRunId );
            command.Parameters.AddWithValue( "@ProviderId", providerIds[answer.Provider.Key] );
            command.Parameters.AddWithValue( "@Rank", answer.Rank );
            command.Parameters.AddWithValue( "@LatencyMs", answer.LatencyMs );
            command.Parameters.AddWithValue( "@HttpStatus", (object?)answer.HttpStatus ?? DBNull.Value );
            command.Parameters.AddWithValue( "@IsSuccess", answer.IsSuccess );
            command.Parameters.AddWithValue( "@ErrorClass", MapErrorClass( answer.ErrorClass ) );
            command.Parameters.AddWithValue( "@ErrorText", (object?)answer.ErrorText ?? DBNull.Value );
            command.Parameters.AddWithValue( "@FinishReason", (object?)answer.FinishReason ?? DBNull.Value );
            command.Parameters.AddWithValue( "@PromptTokens", (object?)answer.PromptTokens ?? DBNull.Value );
            command.Parameters.AddWithValue( "@CompletionTokens", (object?)answer.CompletionTokens ?? DBNull.Value );

            var bytesParameter = command.Parameters.Add( "@ResponseBytes", SqlDbType.VarBinary, -1 );
            bytesParameter.Value = (object?)answer.ResponseBytes ?? DBNull.Value;

            command.Parameters.AddWithValue( "@AnswerText", (object?)answer.AnswerText ?? DBNull.Value );
            await command.ExecuteNonQueryAsync( cancellationToken );
        }
    }

    /// <summary>
    /// Writes the verdict and closes the question run in one transaction, so a
    /// closed row always has a verdict beside it.
    /// </summary>
    /// <param name="questionRunId">Question run being completed.</param>
    /// <param name="verdict">Matcher output.</param>
    /// <param name="providersAsked">How many providers were consulted in total.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task CompleteQuestionRunAsync(
        long questionRunId,
        QuorumVerdict verdict,
        int providersAsked,
        CancellationToken cancellationToken = default )
    {
        await using var connection = await OpenAsync( cancellationToken );
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync( cancellationToken );

        await using( var insert = new SqlCommand( @"
INSERT INTO quorum.Verdict
    ( QuestionRunId, MatcherVersion, Outcome, WinningAnswer, WinningVotes,
      ClusterCount, PartitionSig, ClustersJson )
VALUES
    ( @QuestionRunId, @MatcherVersion, @Outcome, @WinningAnswer, @WinningVotes,
      @ClusterCount, @PartitionSig, @ClustersJson );", connection, transaction ) )
        {
            insert.Parameters.AddWithValue( "@QuestionRunId", questionRunId );
            insert.Parameters.AddWithValue( "@MatcherVersion", verdict.MatcherVersion );
            insert.Parameters.AddWithValue( "@Outcome", verdict.Outcome.ToString().ToUpperInvariant() );
            insert.Parameters.AddWithValue( "@WinningAnswer", (object?)verdict.WinningAnswer ?? DBNull.Value );
            insert.Parameters.AddWithValue( "@WinningVotes", verdict.WinningVotes );
            insert.Parameters.AddWithValue( "@ClusterCount", verdict.Clusters.Count );
            insert.Parameters.AddWithValue( "@PartitionSig", verdict.PartitionSignature );
            insert.Parameters.AddWithValue( "@ClustersJson", JsonSerializer.Serialize( verdict.Clusters, _jsonOptions ) );
            await insert.ExecuteNonQueryAsync( cancellationToken );
        }

        await using( var close = new SqlCommand( @"
UPDATE quorum.QuestionRun
   SET CompletedUtc = SYSUTCDATETIME(), ProvidersAsked = @Asked
 WHERE QuestionRunId = @QuestionRunId;", connection, transaction ) )
        {
            close.Parameters.AddWithValue( "@Asked", providersAsked );
            close.Parameters.AddWithValue( "@QuestionRunId", questionRunId );
            await close.ExecuteNonQueryAsync( cancellationToken );
        }

        await transaction.CommitAsync( cancellationToken );
    }

    /// <summary>Marks a run finished. Separate from question completion so a partial run is still readable.</summary>
    /// <param name="runId">Run to close.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task CompleteRunAsync( long runId, CancellationToken cancellationToken = default )
    {
        await using var connection = await OpenAsync( cancellationToken );
        await using var command = new SqlCommand(
            "UPDATE quorum.Run SET CompletedUtc = SYSUTCDATETIME() WHERE RunId = @RunId;", connection );
        command.Parameters.AddWithValue( "@RunId", runId );
        await command.ExecuteNonQueryAsync( cancellationToken );
    }

    /// <summary>Loads the enabled questions of a set in run order.</summary>
    /// <param name="questionSetId">Set to load.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Questions ordered by ordinal.</returns>
    public async Task<List<QuestionItem>> GetQuestionsAsync(
        int questionSetId, CancellationToken cancellationToken = default )
    {
        const string SQL = @"
SELECT QuestionId, Ordinal, Category, Prompt, AnswerShape, ExpectedAnswer
  FROM quorum.Question
 WHERE QuestionSetId = @SetId AND IsEnabled = 1
 ORDER BY Ordinal;";

        var results = new List<QuestionItem>();

        await using var connection = await OpenAsync( cancellationToken );
        await using var command = new SqlCommand( SQL, connection );
        command.Parameters.AddWithValue( "@SetId", questionSetId );

        await using var reader = await command.ExecuteReaderAsync( cancellationToken );

        while( await reader.ReadAsync( cancellationToken ) )
        {
            results.Add( new QuestionItem
            {
                QuestionId = reader.GetInt32( 0 ),
                Ordinal = reader.GetInt32( 1 ),
                Category = reader.GetString( 2 ),
                Prompt = reader.GetString( 3 ),
                Shape = Enum.Parse<AnswerShape>( reader.GetString( 4 ), ignoreCase: true ),
                ExpectedAnswer = reader.IsDBNull( 5 ) ? null : reader.GetString( 5 )
            } );
        }

        return results;
    }

    /// <summary>Creates a question set if the name is new, and returns its identity either way.</summary>
    /// <param name="name">Unique set name.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The set identity.</returns>
    public async Task<int> EnsureQuestionSetAsync( string name, CancellationToken cancellationToken = default )
    {
        const string SQL = @"
IF NOT EXISTS ( SELECT 1 FROM quorum.QuestionSet WHERE Name = @Name )
    INSERT INTO quorum.QuestionSet ( Name ) VALUES ( @Name );
SELECT QuestionSetId FROM quorum.QuestionSet WHERE Name = @Name;";

        await using var connection = await OpenAsync( cancellationToken );
        await using var command = new SqlCommand( SQL, connection );
        command.Parameters.AddWithValue( "@Name", name );

        return Convert.ToInt32( await command.ExecuteScalarAsync( cancellationToken ) );
    }

    /// <summary>Adds a question to a set, replacing any existing question at that ordinal.</summary>
    /// <param name="questionSetId">Owning set.</param>
    /// <param name="item">Question to store, including its optional answer key.</param>
    /// <param name="expectedSource">Primary-source URL backing the answer key.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task UpsertQuestionAsync(
        int questionSetId,
        QuestionItem item,
        string? expectedSource,
        CancellationToken cancellationToken = default )
    {
        const string SQL = @"
MERGE quorum.Question AS target
USING ( SELECT @SetId AS QuestionSetId, @Ordinal AS Ordinal ) AS source
   ON target.QuestionSetId = source.QuestionSetId AND target.Ordinal = source.Ordinal
WHEN MATCHED THEN
    UPDATE SET Category = @Category, Prompt = @Prompt, AnswerShape = @Shape,
               ExpectedAnswer = @Expected, ExpectedSource = @Source
WHEN NOT MATCHED THEN
    INSERT ( QuestionSetId, Ordinal, Category, Prompt, AnswerShape, ExpectedAnswer, ExpectedSource )
    VALUES ( @SetId, @Ordinal, @Category, @Prompt, @Shape, @Expected, @Source );";

        await using var connection = await OpenAsync( cancellationToken );
        await using var command = new SqlCommand( SQL, connection );
        command.Parameters.AddWithValue( "@SetId", questionSetId );
        command.Parameters.AddWithValue( "@Ordinal", item.Ordinal );
        command.Parameters.AddWithValue( "@Category", item.Category );
        command.Parameters.AddWithValue( "@Prompt", item.Prompt );
        command.Parameters.AddWithValue( "@Shape", item.Shape.ToString().ToLowerInvariant() );
        command.Parameters.AddWithValue( "@Expected", (object?)item.ExpectedAnswer ?? DBNull.Value );
        command.Parameters.AddWithValue( "@Source", (object?)expectedSource ?? DBNull.Value );
        await command.ExecuteNonQueryAsync( cancellationToken );
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>Opens a connection. Kept private so no caller can leak one outside an await using.</summary>
    /// <param name="cancellationToken">Cancels the connect.</param>
    /// <returns>An open connection.</returns>
    private async Task<SqlConnection> OpenAsync( CancellationToken cancellationToken )
    {
        var connection = new SqlConnection( _connectionString );
        await connection.OpenAsync( cancellationToken );
        return connection;
    }

    /// <summary>Reads provider identities keyed by their stable config key.</summary>
    /// <param name="connection">Open connection reused from the caller.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Provider key to database identity.</returns>
    private static async Task<Dictionary<string, int>> LoadProviderIdsAsync(
        SqlConnection connection, CancellationToken cancellationToken )
    {
        var map = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );

        await using var command = new SqlCommand( "SELECT ProviderKey, ProviderId FROM quorum.Provider;", connection );
        await using var reader = await command.ExecuteReaderAsync( cancellationToken );

        while( await reader.ReadAsync( cancellationToken ) )
        {
            map[reader.GetString( 0 )] = reader.GetInt32( 1 );
        }

        return map;
    }

    /// <summary>Maps the error enum onto the CHECK-constrained column values.</summary>
    /// <param name="errorClass">Failure bucket from the provider call.</param>
    /// <returns>The database token, or DBNull when the call succeeded.</returns>
    private static object MapErrorClass( CallErrorClass errorClass )
    {
        return errorClass switch
        {
            CallErrorClass.None => DBNull.Value,
            CallErrorClass.RateLimit => "rate-limit",
            CallErrorClass.Capacity => "capacity",
            CallErrorClass.Auth => "auth",
            CallErrorClass.Timeout => "timeout",
            CallErrorClass.Empty => "empty",
            CallErrorClass.Parse => "parse",
            CallErrorClass.Network => "network",
            CallErrorClass.Truncated => "truncated",
            CallErrorClass.Gated => "gated",
            _ => "other"
        };
    }

    #endregion Private Methods
}
