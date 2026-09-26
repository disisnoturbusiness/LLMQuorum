/* ============================================================================
   Sweep exclusions: rows that are kept (append-only) but must not be graded.
   MEASURED 2026-09-16: until --safe-mode was added, every Claude CLI seat
   received the owner's user-level CLAUDE.md (style rules and personal facts).
   A leak prompt quoted it back, and one seat answered in the file's wording.
   Those Claude rows stay in quorum.SweepCall as evidence but are excluded from
   the per-question sheet.
   ============================================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

USE [LLMQuorum];
GO

IF OBJECT_ID( N'quorum.SweepExclusion', N'U' ) IS NULL
BEGIN
    CREATE TABLE quorum.SweepExclusion
    (
        SweepExclusionId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SweepExclusion PRIMARY KEY,
        SweepId          BIGINT        NOT NULL CONSTRAINT FK_SweepExclusion_Sweep REFERENCES quorum.Sweep( SweepId ),
        Platform         VARCHAR(40)   NOT NULL,
        Reason           NVARCHAR(400) NOT NULL,
        RecordedUtc      DATETIME2(3)  NOT NULL CONSTRAINT DF_SweepExclusion_Recorded DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_SweepExclusion UNIQUE ( SweepId, Platform )
    );
END
GO

INSERT INTO quorum.SweepExclusion ( SweepId, Platform, Reason )
SELECT s.SweepId, 'claude-sub', N'Claude CLI seat ran without --safe-mode; user CLAUDE.md reached the model.'
  FROM quorum.Sweep s
 WHERE s.SweepId IN ( 1, 2, 3 )
   AND NOT EXISTS ( SELECT 1 FROM quorum.SweepExclusion x WHERE x.SweepId = s.SweepId AND x.Platform = 'claude-sub' );
GO
