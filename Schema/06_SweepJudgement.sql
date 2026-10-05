/* ============================================================================
   Judgements: when rules cannot grade an answer safely (prose, refusal wording,
   hedges, ambiguous one-line lists), independent judges grade it against the
   verified key. Every judge call is kept, successful or not, with the exact
   prompt and reply, so a grade can always be traced and a rubric change can be
   diffed against identical answers. Verdicts are reused per
   (SweepCallId, JudgeSeat, RubricVersion), so an answer is never judged twice
   under the same rubric. RubricVersion hashes the judge prompt AND the known
   answers, so changing either one is a new rubric and the judges are re-asked.
   ============================================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

USE [LLMQuorum];
GO

IF OBJECT_ID( N'quorum.SweepJudgement', N'U' ) IS NULL
BEGIN
    CREATE TABLE quorum.SweepJudgement
    (
        SweepJudgementId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SweepJudgement PRIMARY KEY,
        SweepCallId      BIGINT         NOT NULL CONSTRAINT FK_SweepJudgement_Call REFERENCES quorum.SweepCall( SweepCallId ),
        JudgeSeat        VARCHAR(300)   NOT NULL,
        RubricVersion    VARCHAR(40)    NOT NULL,
        GraderVersion    VARCHAR(40)    NOT NULL,
        Verdict          VARCHAR(20)    NULL CONSTRAINT CK_SweepJudgement_Verdict CHECK ( Verdict IN ( 'CORRECT', 'WRONG', 'REFUSAL' ) ),
        Reason           NVARCHAR(400)  NULL,
        IsSuccess        BIT            NOT NULL,
        ErrorText        NVARCHAR(2000) NULL,
        PromptText       NVARCHAR(MAX)  NOT NULL,
        ReplyText        NVARCHAR(MAX)  NULL,
        ResponseBytes    VARBINARY(MAX) NULL,
        LatencyMs        INT            NOT NULL,
        CreatedUtc       DATETIME2(3)   NOT NULL CONSTRAINT DF_SweepJudgement_Created DEFAULT SYSUTCDATETIME()
    );

    CREATE INDEX IX_SweepJudgement_Lookup ON quorum.SweepJudgement ( SweepCallId, JudgeSeat, RubricVersion ) INCLUDE ( Verdict );
END
GO
