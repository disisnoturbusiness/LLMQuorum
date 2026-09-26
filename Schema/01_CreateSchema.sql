/* ============================================================================
   LLMQuorum schema - SQL Server 2025
   ----------------------------------------------------------------------------
   Design rules baked in here, each one learned the hard way:

   1. QUOTED_IDENTIFIER / ANSI_NULLS are set explicitly. Without them any table
      carrying a PERSISTED computed column fails with Msg 1934 under sqlcmd,
      Invoke-Sqlcmd, bcp and every automated deploy path. SSMS defaults them ON,
      which is why that bug only shows up in CI.

   2. NO VECTOR INDEX. CREATE VECTOR INDEX is preview and makes the table
      READ ONLY after creation. Every table here is append-only by design, so a
      vector index would brick ingestion. VECTOR_DISTANCE (GA, exact kNN) is the
      supported path and Microsoft's own guidance is exact search under 50,000
      vectors, which this corpus will not approach for years.

   3. RAW BYTES ARE THE RECORD. ProviderCall.ResponseBytes stores the exact
      wire response. Parsed columns are a query index over those bytes, never
      the source of truth. This makes the matcher re-runnable offline: change a
      match rule, replay it over stored bytes, spend zero API calls. That
      matters when one provider allows 1,000 calls a MONTH.

   4. NO QUADRATIC STORAGE. A run never copies the question text or config; it
      references them. Config is snapshotted once per Run, not per call.
   ============================================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF DB_ID( N'LLMQuorum' ) IS NULL
    CREATE DATABASE [LLMQuorum];
GO

USE [LLMQuorum];
GO

IF SCHEMA_ID( N'quorum' ) IS NULL
    EXEC( N'CREATE SCHEMA [quorum];' );
GO

/* ---------------------------------------------------------------------------
   Provider - one row per configured platform (not per model).
   Independence is a property of the PLATFORM, so this is the unit that the
   escalation ladder walks. Lab is recorded separately because two providers
   can serve the same lab's weights, which would make them correlated votes
   dressed up as independent ones.
   --------------------------------------------------------------------------- */
IF OBJECT_ID( N'quorum.Provider', N'U' ) IS NULL
CREATE TABLE quorum.Provider
(
    ProviderId        INT              NOT NULL IDENTITY( 1, 1 ),
    ProviderKey       VARCHAR( 40 )    NOT NULL,   -- 'groq', 'cloudflare', matches config
    DisplayName       NVARCHAR( 100 )  NOT NULL,
    ModelId           NVARCHAR( 200 )  NOT NULL,   -- exact wire model id
    Lab               NVARCHAR( 60 )   NOT NULL,   -- who trained the weights
    BaseUrl           NVARCHAR( 400 )  NOT NULL,
    LadderPosition    INT              NOT NULL,   -- 1..N, drives ask order
    IsEnabled         BIT              NOT NULL CONSTRAINT DF_Provider_IsEnabled DEFAULT( 1 ),
    DailyCallBudget   INT              NULL,       -- NULL = unmetered
    MonthlyCallBudget INT              NULL,
    CreatedUtc        DATETIME2( 3 )   NOT NULL CONSTRAINT DF_Provider_CreatedUtc DEFAULT( SYSUTCDATETIME() ),
    CONSTRAINT PK_Provider PRIMARY KEY CLUSTERED ( ProviderId ),
    CONSTRAINT UQ_Provider_Key UNIQUE ( ProviderKey ),
    CONSTRAINT UQ_Provider_Ladder UNIQUE ( LadderPosition )
);
GO

/* ---------------------------------------------------------------------------
   QuestionSet / Question - the golden set.
   ExpectedAnswer is NULLABLE on purpose. The matcher (which drives escalation)
   never reads it; only the offline scorer does. A question with no expected
   answer still produces a valid consensus verdict, it just cannot be scored.
   --------------------------------------------------------------------------- */
IF OBJECT_ID( N'quorum.QuestionSet', N'U' ) IS NULL
CREATE TABLE quorum.QuestionSet
(
    QuestionSetId  INT             NOT NULL IDENTITY( 1, 1 ),
    Name           NVARCHAR( 200 ) NOT NULL,
    Notes          NVARCHAR( MAX ) NULL,
    CreatedUtc     DATETIME2( 3 )  NOT NULL CONSTRAINT DF_QuestionSet_CreatedUtc DEFAULT( SYSUTCDATETIME() ),
    CONSTRAINT PK_QuestionSet PRIMARY KEY CLUSTERED ( QuestionSetId ),
    CONSTRAINT UQ_QuestionSet_Name UNIQUE ( Name )
);
GO

IF OBJECT_ID( N'quorum.Question', N'U' ) IS NULL
CREATE TABLE quorum.Question
(
    QuestionId      INT              NOT NULL IDENTITY( 1, 1 ),
    QuestionSetId   INT              NOT NULL,
    Ordinal         INT              NOT NULL,
    Category        VARCHAR( 40 )    NOT NULL,   -- settled-fact | volatile-fact | closed-set | judgment | arithmetic
    Prompt          NVARCHAR( MAX )  NOT NULL,
    ExpectedAnswer  NVARCHAR( MAX )  NULL,       -- the answer key; scorer only
    ExpectedSource  NVARCHAR( 600 )  NULL,       -- primary-source URL backing the key
    AnswerShape     VARCHAR( 20 )    NOT NULL CONSTRAINT DF_Question_Shape DEFAULT( 'text' ),
    IsEnabled       BIT              NOT NULL CONSTRAINT DF_Question_IsEnabled DEFAULT( 1 ),
    CreatedUtc      DATETIME2( 3 )   NOT NULL CONSTRAINT DF_Question_CreatedUtc DEFAULT( SYSUTCDATETIME() ),
    CONSTRAINT PK_Question PRIMARY KEY CLUSTERED ( QuestionId ),
    CONSTRAINT FK_Question_Set FOREIGN KEY ( QuestionSetId ) REFERENCES quorum.QuestionSet ( QuestionSetId ),
    CONSTRAINT UQ_Question_SetOrdinal UNIQUE ( QuestionSetId, Ordinal ),
    CONSTRAINT CK_Question_Shape CHECK ( AnswerShape IN ( 'text', 'scalar', 'enum', 'bool', 'set' ) ),
    CONSTRAINT CK_Question_Category CHECK ( Category IN
        ( 'settled-fact', 'volatile-fact', 'closed-set', 'judgment', 'arithmetic', 'other' ) )
);
GO

/* ---------------------------------------------------------------------------
   Run - one execution of a question set. ConfigSnapshot is the full resolved
   panel config as JSON, captured ONCE here rather than per call. Without this
   a run is not reproducible six months later when the ladder has been reordered.
   --------------------------------------------------------------------------- */
IF OBJECT_ID( N'quorum.Run', N'U' ) IS NULL
CREATE TABLE quorum.Run
(
    RunId           BIGINT           NOT NULL IDENTITY( 1, 1 ),
    QuestionSetId   INT              NOT NULL,
    Label           NVARCHAR( 200 )  NULL,
    ConfigSnapshot  NVARCHAR( MAX )  NOT NULL,
    QuorumSize      TINYINT          NOT NULL CONSTRAINT DF_Run_QuorumSize DEFAULT( 3 ),
    BaseCount       TINYINT          NOT NULL CONSTRAINT DF_Run_BaseCount DEFAULT( 3 ),
    StartedUtc      DATETIME2( 3 )   NOT NULL CONSTRAINT DF_Run_StartedUtc DEFAULT( SYSUTCDATETIME() ),
    CompletedUtc    DATETIME2( 3 )   NULL,
    CONSTRAINT PK_Run PRIMARY KEY CLUSTERED ( RunId ),
    CONSTRAINT FK_Run_Set FOREIGN KEY ( QuestionSetId ) REFERENCES quorum.QuestionSet ( QuestionSetId )
);
GO

/* ---------------------------------------------------------------------------
   QuestionRun - open row / close row. The open row is written before any
   provider is called; CompletedUtc stays NULL until the engine finishes.
   A row with StartedUtc set and CompletedUtc NULL after the process died IS
   the crash signal - no separate status flag can drift out of sync with it.
   --------------------------------------------------------------------------- */
IF OBJECT_ID( N'quorum.QuestionRun', N'U' ) IS NULL
CREATE TABLE quorum.QuestionRun
(
    QuestionRunId   BIGINT           NOT NULL IDENTITY( 1, 1 ),
    RunId           BIGINT           NOT NULL,
    QuestionId      INT              NOT NULL,
    StartedUtc      DATETIME2( 3 )   NOT NULL CONSTRAINT DF_QuestionRun_StartedUtc DEFAULT( SYSUTCDATETIME() ),
    CompletedUtc    DATETIME2( 3 )   NULL,
    ProvidersAsked  TINYINT          NOT NULL CONSTRAINT DF_QuestionRun_Asked DEFAULT( 0 ),
    CONSTRAINT PK_QuestionRun PRIMARY KEY CLUSTERED ( QuestionRunId ),
    CONSTRAINT FK_QuestionRun_Run FOREIGN KEY ( RunId ) REFERENCES quorum.Run ( RunId ),
    CONSTRAINT FK_QuestionRun_Question FOREIGN KEY ( QuestionId ) REFERENCES quorum.Question ( QuestionId ),
    CONSTRAINT UQ_QuestionRun UNIQUE ( RunId, QuestionId )
);
GO

/* ---------------------------------------------------------------------------
   ProviderCall - APPEND ONLY. One row per HTTP attempt, including failures.
   ResponseBytes holds the exact wire body so the matcher can be replayed
   without re-spending quota. AnswerText is a convenience projection, NOT the
   record; if a parse changes, reparse from bytes.
   Rank is the order this provider was consulted within the question, so the
   escalation path is reconstructable.
   --------------------------------------------------------------------------- */
IF OBJECT_ID( N'quorum.ProviderCall', N'U' ) IS NULL
CREATE TABLE quorum.ProviderCall
(
    ProviderCallId   BIGINT           NOT NULL IDENTITY( 1, 1 ),
    QuestionRunId    BIGINT           NOT NULL,
    ProviderId       INT              NOT NULL,
    Rank             TINYINT          NOT NULL,   -- 1 = first asked
    AttemptNo        TINYINT          NOT NULL CONSTRAINT DF_ProviderCall_Attempt DEFAULT( 1 ),
    RequestedUtc     DATETIME2( 3 )   NOT NULL CONSTRAINT DF_ProviderCall_Requested DEFAULT( SYSUTCDATETIME() ),
    LatencyMs        INT              NULL,
    HttpStatus       INT              NULL,
    IsSuccess        BIT              NOT NULL CONSTRAINT DF_ProviderCall_Success DEFAULT( 0 ),
    ErrorClass       VARCHAR( 40 )    NULL,       -- rate-limit | capacity | auth | timeout | empty | parse | other
    ErrorText        NVARCHAR( 2000 ) NULL,
    FinishReason     VARCHAR( 40 )    NULL,
    PromptTokens     INT              NULL,
    CompletionTokens INT              NULL,
    ResponseBytes    VARBINARY( MAX ) NULL,       -- THE RECORD
    AnswerText       NVARCHAR( MAX )  NULL,       -- projection over the bytes
    ByteLength       AS ( DATALENGTH( ResponseBytes ) ) PERSISTED,
    CONSTRAINT PK_ProviderCall PRIMARY KEY CLUSTERED ( ProviderCallId ),
    CONSTRAINT FK_ProviderCall_QuestionRun FOREIGN KEY ( QuestionRunId ) REFERENCES quorum.QuestionRun ( QuestionRunId ),
    CONSTRAINT FK_ProviderCall_Provider FOREIGN KEY ( ProviderId ) REFERENCES quorum.Provider ( ProviderId ),
    CONSTRAINT CK_ProviderCall_ErrorClass CHECK ( ErrorClass IS NULL OR ErrorClass IN
        ( 'rate-limit', 'capacity', 'auth', 'timeout', 'empty', 'parse', 'network', 'other' ) )
);
GO

CREATE NONCLUSTERED INDEX IX_ProviderCall_QuestionRun
    ON quorum.ProviderCall ( QuestionRunId, Rank )
    INCLUDE ( ProviderId, IsSuccess );
GO

CREATE NONCLUSTERED INDEX IX_ProviderCall_Errors
    ON quorum.ProviderCall ( ProviderId, ErrorClass )
    WHERE ErrorClass IS NOT NULL;
GO

/* ---------------------------------------------------------------------------
   Verdict - the matcher's output. Fully RE-COMPUTABLE from ProviderCall rows,
   which is the point: change the match ruleset, replay, write a new Verdict
   row with a new MatcherVersion. Old verdicts are never mutated so you can
   diff rule changes against each other.
   Outcome: QUORUM = an answer reached the quorum size.
            UNRESOLVED = providers exhausted with no quorum.
            FAILED = too few providers answered to decide anything.
   --------------------------------------------------------------------------- */
IF OBJECT_ID( N'quorum.Verdict', N'U' ) IS NULL
CREATE TABLE quorum.Verdict
(
    VerdictId       BIGINT           NOT NULL IDENTITY( 1, 1 ),
    QuestionRunId   BIGINT           NOT NULL,
    MatcherVersion  VARCHAR( 40 )    NOT NULL,
    Outcome         VARCHAR( 20 )    NOT NULL,
    WinningAnswer   NVARCHAR( MAX )  NULL,
    WinningVotes    TINYINT          NOT NULL CONSTRAINT DF_Verdict_WinningVotes DEFAULT( 0 ),
    ClusterCount    TINYINT          NOT NULL CONSTRAINT DF_Verdict_ClusterCount DEFAULT( 0 ),
    PartitionSig    VARCHAR( 40 )    NOT NULL,   -- '3-0-0', '2-2-1' - auditable by eye
    ClustersJson    NVARCHAR( MAX )  NOT NULL,   -- [{answer, providers[]}] for display
    ComputedUtc     DATETIME2( 3 )   NOT NULL CONSTRAINT DF_Verdict_ComputedUtc DEFAULT( SYSUTCDATETIME() ),
    CONSTRAINT PK_Verdict PRIMARY KEY CLUSTERED ( VerdictId ),
    CONSTRAINT FK_Verdict_QuestionRun FOREIGN KEY ( QuestionRunId ) REFERENCES quorum.QuestionRun ( QuestionRunId ),
    CONSTRAINT UQ_Verdict_Version UNIQUE ( QuestionRunId, MatcherVersion ),
    CONSTRAINT CK_Verdict_Outcome CHECK ( Outcome IN ( 'QUORUM', 'UNRESOLVED', 'FAILED' ) )
);
GO

/* ---------------------------------------------------------------------------
   Score - the OFFLINE pass. Separate table because it needs the answer key,
   runs on a different cadence than the matcher, and a question may be scored
   many times as the key is refined. No row means not scored, which is a
   different thing from scored-and-wrong.
   --------------------------------------------------------------------------- */
IF OBJECT_ID( N'quorum.Score', N'U' ) IS NULL
CREATE TABLE quorum.Score
(
    ScoreId        BIGINT          NOT NULL IDENTITY( 1, 1 ),
    VerdictId      BIGINT          NOT NULL,
    ScorerVersion  VARCHAR( 40 )   NOT NULL,
    IsCorrect      BIT             NULL,       -- NULL = could not judge
    Notes          NVARCHAR( MAX ) NULL,
    ScoredUtc      DATETIME2( 3 )  NOT NULL CONSTRAINT DF_Score_ScoredUtc DEFAULT( SYSUTCDATETIME() ),
    CONSTRAINT PK_Score PRIMARY KEY CLUSTERED ( ScoreId ),
    CONSTRAINT FK_Score_Verdict FOREIGN KEY ( VerdictId ) REFERENCES quorum.Verdict ( VerdictId ),
    CONSTRAINT UQ_Score_Version UNIQUE ( VerdictId, ScorerVersion )
);
GO

/* ---------------------------------------------------------------------------
   ProviderAnswerScore - the characterization matrix. This is the actual
   deliverable: per provider, per category, how often was it right, how often
   did it join the winning cluster, how often was it the lone dissenter.
   Derived, so it is a view rather than a table.
   --------------------------------------------------------------------------- */
GO
CREATE OR ALTER VIEW quorum.vProviderProfile
AS
SELECT
    p.ProviderKey,
    p.Lab,
    q.Category,
    COUNT_BIG( * )                                                   AS CallsAnswered,
    SUM( CASE WHEN pc.IsSuccess = 1 THEN 1 ELSE 0 END )              AS Successes,
    SUM( CASE WHEN pc.ErrorClass = 'rate-limit' THEN 1 ELSE 0 END )  AS RateLimited,
    AVG( CAST( pc.LatencyMs AS BIGINT ) )                            AS AvgLatencyMs,
    SUM( CASE WHEN v.Outcome = 'QUORUM'
               AND pc.AnswerText IS NOT NULL
               AND v.WinningAnswer IS NOT NULL
               AND pc.AnswerText = v.WinningAnswer
              THEN 1 ELSE 0 END )                                    AS JoinedWinningCluster
FROM quorum.ProviderCall AS pc
     INNER JOIN quorum.Provider     AS p  ON p.ProviderId    = pc.ProviderId
     INNER JOIN quorum.QuestionRun  AS qr ON qr.QuestionRunId = pc.QuestionRunId
     INNER JOIN quorum.Question     AS q  ON q.QuestionId     = qr.QuestionId
     LEFT  JOIN quorum.Verdict      AS v  ON v.QuestionRunId  = qr.QuestionRunId
GROUP BY p.ProviderKey, p.Lab, q.Category;
GO
