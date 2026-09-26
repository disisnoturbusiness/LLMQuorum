/* ============================================================================
   Public set infrastructure (2026-09-17).
   - Question.Topic / GradeMode: display category for the site; 'judge' mode sends
     every non-exact answer to judges (for questions whose correct wording varies).
   - KeyEvidence: the source, exact quote and checker behind every key and every
     known (outdated / other-form) answer. The site links these.
   - SeatGrade: the graded result per seat, rewritten each time a question sheet is
     built. Derived data; the raw record stays in SweepCall / SweepJudgement.
   - SweepPlan: the slow-and-steady queue. Questions run strictly in order per
     provider group; a group cut off by a daily cap or usage limit is redone whole
     for that question once the limit resets; transient seat failures get up to two
     later retry rounds.
   ============================================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

USE [LLMQuorum];
GO

IF COL_LENGTH( N'quorum.Question', N'Topic' ) IS NULL
    ALTER TABLE quorum.Question ADD Topic NVARCHAR(100) NULL;
GO

IF COL_LENGTH( N'quorum.Question', N'GradeMode' ) IS NULL
    ALTER TABLE quorum.Question ADD GradeMode VARCHAR(10) NOT NULL
        CONSTRAINT DF_Question_GradeMode DEFAULT 'rule'
        CONSTRAINT CK_Question_GradeMode CHECK ( GradeMode IN ( 'rule', 'judge' ) );
GO

IF OBJECT_ID( N'quorum.KeyEvidence', N'U' ) IS NULL
BEGIN
    CREATE TABLE quorum.KeyEvidence
    (
        EvidenceId    INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_KeyEvidence PRIMARY KEY,
        QuestionId    INT            NOT NULL CONSTRAINT FK_KeyEvidence_Question REFERENCES quorum.Question( QuestionId ),
        KnownAnswerId INT            NULL     CONSTRAINT FK_KeyEvidence_Known REFERENCES quorum.QuestionKnownAnswer( KnownAnswerId ),
        Role          VARCHAR(10)    NOT NULL CONSTRAINT CK_KeyEvidence_Role CHECK ( Role IN ( 'key', 'known' ) ),
        Url           NVARCHAR(1000) NOT NULL,
        Document      NVARCHAR(500)  NOT NULL,
        Quote         NVARCHAR(MAX)  NOT NULL,
        ReadHow       VARCHAR(40)    NOT NULL,
        CheckedBy     VARCHAR(40)    NOT NULL,   -- research-agent | refute-agent | engine-test | claude-direct
        Verdict       VARCHAR(20)    NOT NULL,   -- confirmed | refuted | inferred
        Notes         NVARCHAR(MAX)  NULL,
        WorkflowRunId VARCHAR(60)    NULL,
        CheckedUtc    DATETIME2(3)   NOT NULL CONSTRAINT DF_KeyEvidence_Checked DEFAULT SYSUTCDATETIME()
    );
END
GO

IF OBJECT_ID( N'quorum.SeatGrade', N'U' ) IS NULL
BEGIN
    CREATE TABLE quorum.SeatGrade
    (
        SeatGradeId   BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SeatGrade PRIMARY KEY,
        QuestionId    INT            NOT NULL CONSTRAINT FK_SeatGrade_Question REFERENCES quorum.Question( QuestionId ),
        SeatId        VARCHAR(300)   NOT NULL,
        SweepId       BIGINT         NOT NULL,
        SweepCallId   BIGINT         NOT NULL CONSTRAINT FK_SeatGrade_Call REFERENCES quorum.SweepCall( SweepCallId ),
        Provider      VARCHAR(40)    NOT NULL,
        ModelId       NVARCHAR(400)  NOT NULL,
        BaseModel     VARCHAR(120)   NOT NULL,
        Mode          VARCHAR(20)    NOT NULL,
        Grade         VARCHAR(20)    NOT NULL,
        Bucket        VARCHAR(20)    NOT NULL,
        GradeText     NVARCHAR(400)  NOT NULL,
        MatchNote     NVARCHAR(200)  NULL,
        HowGraded     NVARCHAR(MAX)  NOT NULL,
        AnswerText    NVARCHAR(MAX)  NULL,
        SameAnswerSeats  INT         NULL,
        SameAnswerModels INT         NULL,
        LatencyMs     INT            NOT NULL,
        Runs          INT            NOT NULL,
        GraderVersion VARCHAR(40)    NOT NULL,
        GradedUtc     DATETIME2(3)   NOT NULL CONSTRAINT DF_SeatGrade_Graded DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_SeatGrade UNIQUE ( QuestionId, SeatId )
    );
END
GO

IF OBJECT_ID( N'quorum.SweepPlan', N'U' ) IS NULL
BEGIN
    CREATE TABLE quorum.SweepPlan
    (
        PlanItemId    INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SweepPlan PRIMARY KEY,
        PlanName      VARCHAR(40)    NOT NULL,
        QuestionId    INT            NOT NULL CONSTRAINT FK_SweepPlan_Question REFERENCES quorum.Question( QuestionId ),
        Ordinal       INT            NOT NULL,
        ProviderGroup VARCHAR(20)    NOT NULL CONSTRAINT CK_SweepPlan_Group CHECK ( ProviderGroup IN ( 'openrouter', 'others' ) ),
        Round         INT            NOT NULL CONSTRAINT DF_SweepPlan_Round DEFAULT 0,
        SeatFilter    NVARCHAR(MAX)  NULL,   -- JSON array of seat ids for retry rounds; NULL = the whole group
        Status        VARCHAR(20)    NOT NULL CONSTRAINT DF_SweepPlan_Status DEFAULT 'pending'
                                     CONSTRAINT CK_SweepPlan_Status CHECK ( Status IN ( 'pending', 'running', 'cut-off', 'done' ) ),
        NotBeforeUtc  DATETIME2(3)   NULL,
        LastSweepId   BIGINT         NULL,
        Runs          INT            NOT NULL CONSTRAINT DF_SweepPlan_Runs DEFAULT 0,
        Note          NVARCHAR(2000) NULL,
        UpdatedUtc    DATETIME2(3)   NOT NULL CONSTRAINT DF_SweepPlan_Updated DEFAULT SYSUTCDATETIME()
    );

    CREATE INDEX IX_SweepPlan_Next ON quorum.SweepPlan ( PlanName, ProviderGroup, Status, Round, Ordinal );
END
GO

IF NOT EXISTS ( SELECT 1 FROM quorum.QuestionSet WHERE Name = N'Public set v2' )
    INSERT INTO quorum.QuestionSet ( Name ) VALUES ( N'Public set v2' );
GO
