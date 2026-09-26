using System.Text.Json;
using LLMQuorum.Core.Models;
using Microsoft.Data.SqlClient;

namespace LLMQuorum.Core.Matching;

/// <summary>
/// Re-computes verdicts for a completed run from the STORED provider answers,
/// spending no API quota.
///
/// This is why raw responses are persisted rather than just the verdict. The
/// comparison rules are the part most likely to change, and on this panel the
/// scarcest seat allows a thousand calls a MONTH. Re-asking the providers to
/// evaluate a rule tweak would burn a week of budget to answer a question the
/// stored bytes already contain.
///
/// Old verdicts are never mutated. Each replay inserts a row under its own
/// matcher version, so two rule sets can be diffed against the same answers.
/// </summary>
public sealed class VerdictReplayer
{
    #region Data Members

    /// <summary>Serializer settings matching those used on the original write.</summary>
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = false };

    private readonly string _connectionString;

    #endregion Data Members

    #region Constructor

    /// <summary>Builds a replayer over one database.</summary>
    /// <param name="connectionString">ADO.NET connection string for the LLMQuorum database.</param>
    public VerdictReplayer( string connectionString )
    {
        _connectionString = connectionString;
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>
    /// Replays every question in a run under a new matcher and stores the
    /// results. The escalation ladder is NOT re-simulated: the answers that were
    /// actually collected are the answers available, and pretending the run
    /// would have stopped earlier under different rules would invent data.
    /// What this measures is how the same evidence clusters under new rules.
    /// </summary>
    /// <param name="runId">Completed run to replay.</param>
    /// <param name="matcher">Rule set to apply. Its Version must differ from any already stored.</param>
    /// <param name="cancellationToken">Cancels the replay.</param>
    /// <returns>How many question verdicts were written, and how many changed outcome.</returns>
    public async Task<ReplaySummary> ReplayRunAsync(
        long runId,
        IAnswerMatcher matcher,
        CancellationToken cancellationToken = default )
    {
        var questions = await LoadStoredAnswersAsync( runId, cancellationToken );
        var written = 0;
        var changed = 0;

        foreach( var stored in questions )
        {
            var clusters = matcher.Cluster( stored.Answers, stored.Shape );
            var signature = clusters.Count > 0 ? string.Join( "-", clusters.Select( c => c.Votes ) ) : "0";

            // Outcome under replay reflects the evidence on hand, not a re-walked ladder.
            var outcome = clusters.Count > 0 && clusters[0].Votes >= stored.QuorumSize
                ? QuorumOutcome.Quorum
                : stored.Answers.Count( a => a.IsSuccess ) < 2
                    ? QuorumOutcome.Failed
                    : QuorumOutcome.Unresolved;

            await WriteVerdictAsync( stored.QuestionRunId, matcher.Version, outcome, clusters,
                                     signature, cancellationToken );

            written++;

            if( !string.Equals( outcome.ToString().ToUpperInvariant(), stored.PreviousOutcome, StringComparison.Ordinal ) ||
                !string.Equals( signature, stored.PreviousSignature, StringComparison.Ordinal ) )
            {
                changed++;
            }
        }

        return new ReplaySummary { QuestionsReplayed = written, OutcomesChanged = changed };
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// Loads every stored answer for a run, grouped by question. AnswerText is
    /// read rather than the raw bytes because it is the projection the matcher
    /// consumes; the bytes remain available for a deeper reparse if a future
    /// rule needs something the projection dropped.
    /// </summary>
    /// <param name="runId">Run to load.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One entry per question, carrying its answers and prior verdict.</returns>
    private async Task<List<StoredQuestion>> LoadStoredAnswersAsync(
        long runId, CancellationToken cancellationToken )
    {
        const string SQL = @"
SELECT qr.QuestionRunId, q.AnswerShape, r.QuorumSize,
       p.ProviderKey, p.ModelId, p.Lab,
       pc.Rank, pc.IsSuccess, pc.AnswerText,
       v.Outcome, v.PartitionSig
  FROM quorum.QuestionRun qr
       JOIN quorum.Run          r  ON r.RunId          = qr.RunId
       JOIN quorum.Question     q  ON q.QuestionId     = qr.QuestionId
       JOIN quorum.ProviderCall pc ON pc.QuestionRunId = qr.QuestionRunId
       JOIN quorum.Provider     p  ON p.ProviderId     = pc.ProviderId
       LEFT JOIN quorum.Verdict v  ON v.QuestionRunId  = qr.QuestionRunId
 WHERE qr.RunId = @RunId
 ORDER BY qr.QuestionRunId, pc.Rank;";

        var byQuestion = new Dictionary<long, StoredQuestion>();

        await using var connection = new SqlConnection( _connectionString );
        await connection.OpenAsync( cancellationToken );

        await using var command = new SqlCommand( SQL, connection );
        command.Parameters.AddWithValue( "@RunId", runId );

        await using var reader = await command.ExecuteReaderAsync( cancellationToken );

        while( await reader.ReadAsync( cancellationToken ) )
        {
            var questionRunId = reader.GetInt64( 0 );

            if( !byQuestion.TryGetValue( questionRunId, out var entry ) )
            {
                entry = new StoredQuestion
                {
                    QuestionRunId = questionRunId,
                    Shape = Enum.Parse<AnswerShape>( reader.GetString( 1 ), ignoreCase: true ),
                    QuorumSize = reader.GetByte( 2 ),
                    PreviousOutcome = reader.IsDBNull( 9 ) ? null : reader.GetString( 9 ),
                    PreviousSignature = reader.IsDBNull( 10 ) ? null : reader.GetString( 10 ),
                    Answers = new List<ProviderAnswer>()
                };

                byQuestion[questionRunId] = entry;
            }

            entry.Answers.Add( new ProviderAnswer
            {
                Provider = new ProviderDefinition
                {
                    Key = reader.GetString( 3 ),
                    DisplayName = reader.GetString( 3 ),
                    ModelId = reader.GetString( 4 ),
                    Lab = reader.GetString( 5 ),
                    Endpoint = string.Empty,
                    ApiKeyFile = string.Empty
                },
                Rank = reader.GetByte( 6 ),
                IsSuccess = reader.GetBoolean( 7 ),
                AnswerText = reader.IsDBNull( 8 ) ? null : reader.GetString( 8 )
            } );
        }

        return byQuestion.Values.ToList();
    }

    /// <summary>Inserts a replayed verdict under its own matcher version, leaving prior verdicts intact.</summary>
    /// <param name="questionRunId">Question being re-judged.</param>
    /// <param name="matcherVersion">Rule set identifier for the new verdict.</param>
    /// <param name="outcome">Recomputed outcome.</param>
    /// <param name="clusters">Recomputed clusters, largest first.</param>
    /// <param name="signature">Recomputed partition signature.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    private async Task WriteVerdictAsync(
        long questionRunId,
        string matcherVersion,
        QuorumOutcome outcome,
        List<AnswerCluster> clusters,
        string signature,
        CancellationToken cancellationToken )
    {
        const string SQL = @"
DELETE FROM quorum.Verdict WHERE QuestionRunId = @QuestionRunId AND MatcherVersion = @MatcherVersion;
INSERT INTO quorum.Verdict
    ( QuestionRunId, MatcherVersion, Outcome, WinningAnswer, WinningVotes,
      ClusterCount, PartitionSig, ClustersJson )
VALUES
    ( @QuestionRunId, @MatcherVersion, @Outcome, @WinningAnswer, @WinningVotes,
      @ClusterCount, @PartitionSig, @ClustersJson );";

        await using var connection = new SqlConnection( _connectionString );
        await connection.OpenAsync( cancellationToken );

        await using var command = new SqlCommand( SQL, connection );
        command.Parameters.AddWithValue( "@QuestionRunId", questionRunId );
        command.Parameters.AddWithValue( "@MatcherVersion", matcherVersion );
        command.Parameters.AddWithValue( "@Outcome", outcome.ToString().ToUpperInvariant() );
        command.Parameters.AddWithValue( "@WinningAnswer",
            outcome == QuorumOutcome.Quorum && clusters.Count > 0 ? clusters[0].Answer : (object)DBNull.Value );
        command.Parameters.AddWithValue( "@WinningVotes", clusters.Count > 0 ? clusters[0].Votes : 0 );
        command.Parameters.AddWithValue( "@ClusterCount", clusters.Count );
        command.Parameters.AddWithValue( "@PartitionSig", signature );
        command.Parameters.AddWithValue( "@ClustersJson", JsonSerializer.Serialize( clusters, _jsonOptions ) );

        await command.ExecuteNonQueryAsync( cancellationToken );
    }

    #endregion Private Methods
}

/// <summary>One question's stored evidence, used to re-judge without re-asking.</summary>
internal sealed class StoredQuestion
{
    /// <summary>Database identity of the question run.</summary>
    public long QuestionRunId { get; init; }

    /// <summary>Declared comparison shape for this question.</summary>
    public AnswerShape Shape { get; init; }

    /// <summary>Votes required to win, taken from the original run's snapshot.</summary>
    public int QuorumSize { get; init; }

    /// <summary>Outcome recorded by the original matcher, used to count changes.</summary>
    public string? PreviousOutcome { get; init; }

    /// <summary>Partition signature recorded by the original matcher.</summary>
    public string? PreviousSignature { get; init; }

    /// <summary>Every stored answer for this question, in ask order.</summary>
    public required List<ProviderAnswer> Answers { get; init; }
}

/// <summary>How a replay went.</summary>
public sealed class ReplaySummary
{
    /// <summary>How many question verdicts were written.</summary>
    public int QuestionsReplayed { get; init; }

    /// <summary>How many changed outcome or partition versus the previous verdict. This is the signal.</summary>
    public int OutcomesChanged { get; init; }
}
