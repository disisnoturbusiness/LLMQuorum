/* ============================================================================
   Model sweep support + truncation as a first-class failure.

   Why a separate pair of tables instead of reusing Run/ProviderCall: a sweep asks
   ONE question to EVERY model a platform offers, while the panel ladder asks one
   model per platform in escalation order. Provider.LadderPosition is unique, so
   forcing dozens of models into it would corrupt the ladder.

   Why 'truncated': the 2026-09-16 sweep graded cut-off answers as wrong because
   finish_reason was only checked when content was empty. A truncated answer is
   a harness/limit problem, not a model being wrong, and must never be scored or
   counted as a vote.
   ============================================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

USE [LLMQuorum];
GO

IF EXISTS ( SELECT 1 FROM sys.check_constraints WHERE name = N'CK_ProviderCall_ErrorClass' )
    ALTER TABLE quorum.ProviderCall DROP CONSTRAINT CK_ProviderCall_ErrorClass;
GO

ALTER TABLE quorum.ProviderCall WITH CHECK ADD CONSTRAINT CK_ProviderCall_ErrorClass CHECK
    ( ErrorClass IS NULL OR ErrorClass IN
      ( 'rate-limit', 'capacity', 'auth', 'timeout', 'empty', 'parse', 'network', 'truncated', 'gated', 'other' ) );
GO

IF OBJECT_ID( N'quorum.Sweep', N'U' ) IS NULL
CREATE TABLE quorum.Sweep
(
    SweepId         BIGINT           NOT NULL IDENTITY( 1, 1 ),
    QuestionId      INT              NOT NULL,
    Label           NVARCHAR( 200 )  NULL,
    HarnessVersion  VARCHAR( 40 )    NOT NULL,
    StartedUtc      DATETIME2( 3 )   NOT NULL CONSTRAINT DF_Sweep_StartedUtc DEFAULT( SYSUTCDATETIME() ),
    CompletedUtc    DATETIME2( 3 )   NULL,
    CONSTRAINT PK_Sweep PRIMARY KEY CLUSTERED ( SweepId ),
    CONSTRAINT FK_Sweep_Question FOREIGN KEY ( QuestionId ) REFERENCES quorum.Question ( QuestionId )
);
GO

/* ---------------------------------------------------------------------------
   SweepCall - APPEND ONLY, one row per model per attempt.
   RequestJson is the exact body sent (never contains a credential) so a limit
   dispute can be settled by reading what was actually asked for.
   MaxTokensSent / ContextWindow / PublishedMaxOutput record the limit decision
   and the catalog facts it was derived from, side by side.
   AnswerText is the cleaned final answer; ResponseBytes is the record.
   --------------------------------------------------------------------------- */
IF OBJECT_ID( N'quorum.SweepCall', N'U' ) IS NULL
CREATE TABLE quorum.SweepCall
(
    SweepCallId         BIGINT            NOT NULL IDENTITY( 1, 1 ),
    SweepId             BIGINT            NOT NULL,
    Platform            VARCHAR( 40 )     NOT NULL,
    ModelId             NVARCHAR( 200 )   NOT NULL,
    Mode                VARCHAR( 20 )     NOT NULL CONSTRAINT DF_SweepCall_Mode DEFAULT( 'memory' ),
    AttemptNo           TINYINT           NOT NULL CONSTRAINT DF_SweepCall_Attempt DEFAULT( 1 ),
    RequestJson         NVARCHAR( MAX )   NULL,
    MaxTokensSent       INT               NULL,
    ContextWindow       INT               NULL,
    PublishedMaxOutput  INT               NULL,
    RequestedUtc        DATETIME2( 3 )    NOT NULL CONSTRAINT DF_SweepCall_Requested DEFAULT( SYSUTCDATETIME() ),
    LatencyMs           INT               NULL,
    HttpStatus          INT               NULL,
    IsSuccess           BIT               NOT NULL CONSTRAINT DF_SweepCall_Success DEFAULT( 0 ),
    ErrorClass          VARCHAR( 40 )     NULL,
    ErrorText           NVARCHAR( 2000 )  NULL,
    FinishReason        VARCHAR( 40 )     NULL,
    PromptTokens        INT               NULL,
    CompletionTokens    INT               NULL,
    ReasoningTokens     INT               NULL,
    ReasoningStripped   BIT               NOT NULL CONSTRAINT DF_SweepCall_Stripped DEFAULT( 0 ),
    ResponseBytes       VARBINARY( MAX )  NULL,
    AnswerText          NVARCHAR( MAX )   NULL,
    CONSTRAINT PK_SweepCall PRIMARY KEY CLUSTERED ( SweepCallId ),
    CONSTRAINT FK_SweepCall_Sweep FOREIGN KEY ( SweepId ) REFERENCES quorum.Sweep ( SweepId ),
    CONSTRAINT CK_SweepCall_Mode CHECK ( Mode IN ( 'memory', 'web-search' ) ),
    CONSTRAINT CK_SweepCall_ErrorClass CHECK ( ErrorClass IS NULL OR ErrorClass IN
        ( 'rate-limit', 'capacity', 'auth', 'timeout', 'empty', 'parse', 'network', 'truncated', 'gated', 'other' ) )
);
GO

IF NOT EXISTS ( SELECT 1 FROM sys.indexes WHERE name = N'IX_SweepCall_Sweep' )
    CREATE NONCLUSTERED INDEX IX_SweepCall_Sweep ON quorum.SweepCall ( SweepId, Platform, ModelId ) INCLUDE ( IsSuccess, ErrorClass );
GO
