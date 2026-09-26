/* ============================================================================
   Paid lane money path, version 2 (2026-09-18), after an adversarial review of
   script 12. Native web search cannot be capped per call, so the cap now rests on
   OpenRouter's own numbers, and every paid send leaves a durable mark first.
   - SpendLedger gets QuestionId, SweepId and GenerationId. The reservation row is
     written BEFORE the HTTP send, so a crash, restart or timeout still leaves a
     record that the seat was asked. quorum.ReserveSpend refuses a second
     reservation for the same (QuestionId, SeatId) unless every earlier one
     settled at exactly $0: no seat is ever billed twice for one question.
   - Spent = the larger of the OpenRouter key's own usage (passed in, read live)
     and the local settled total plus recent unknown holds, plus every open
     reservation. Unknown holds older than 30 minutes stop counting locally
     because the key's usage already includes them if they billed.
   - The local cap drops to $25.00. The key carries an OpenRouter-side $28 limit;
     the account holds $29.94. The gaps absorb one call running past its estimate.
   ============================================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
GO

USE [LLMQuorum];
GO

BEGIN TRAN;

IF COL_LENGTH( 'quorum.SpendLedger', 'QuestionId' ) IS NULL
    ALTER TABLE quorum.SpendLedger ADD
        QuestionId   INT          NULL,
        SweepId      BIGINT       NULL,
        GenerationId VARCHAR(100) NULL;

UPDATE quorum.SpendCap
   SET LimitUsd = 25.00,
       Note = N'Local cap $25. OpenRouter key limit $28. Account $29.94. Gaps absorb one call running past its estimate; a negative balance would stop the free models.',
       UpdatedUtc = SYSUTCDATETIME()
 WHERE CapName = 'openrouter-paid';

COMMIT;
GO

IF NOT EXISTS ( SELECT 1 FROM sys.indexes WHERE name = 'IX_SpendLedger_QuestionSeat' AND object_id = OBJECT_ID( 'quorum.SpendLedger' ) )
    CREATE INDEX IX_SpendLedger_QuestionSeat ON quorum.SpendLedger ( QuestionId, SeatId ) INCLUDE ( State, ActualUsd );
GO

CREATE OR ALTER PROCEDURE quorum.ReserveSpend
    @CapName       VARCHAR(40),
    @SeatId        VARCHAR(200),
    @QuestionId    INT,
    @SweepId       BIGINT,
    @ReserveUsd    DECIMAL(18,8),
    @ServerUsedUsd DECIMAL(18,8),
    @SpendId       BIGINT        OUTPUT,
    @SpentUsd      DECIMAL(18,8) OUTPUT,
    @LimitUsd      DECIMAL(10,4) OUTPUT,
    @Duplicate     BIT           OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET @SpendId = NULL;
    SET @Duplicate = 0;

    BEGIN TRAN;

    DECLARE @lock INT;
    DECLARE @resource NVARCHAR(255) = N'LLMQuorum.SpendCap.' + @CapName;
    EXEC @lock = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 30000;

    IF @lock < 0
    BEGIN
        ROLLBACK;
        THROW 51000, 'Could not lock the spend cap.', 1;
    END

    SELECT @LimitUsd = LimitUsd FROM quorum.SpendCap WHERE CapName = @CapName;

    IF @LimitUsd IS NULL
    BEGIN
        ROLLBACK;
        THROW 51001, 'Unknown spend cap.', 1;
    END

    /* Asked before: any open, unknown or billed attempt for this question and seat. */
    IF EXISTS ( SELECT 1 FROM quorum.SpendLedger
                 WHERE CapName = @CapName AND QuestionId = @QuestionId AND SeatId = @SeatId
                   AND ( State IN ( 'reserved', 'unknown' ) OR ( State = 'settled' AND ActualUsd > 0 ) ) )
    BEGIN
        SET @Duplicate = 1;
        SET @SpentUsd = 0;
        COMMIT;
        RETURN;
    END

    DECLARE @local DECIMAL(18,8), @open DECIMAL(18,8);

    /* A 'reserved' row older than 2 hours was orphaned by a crash (a paid send is never cancelled and times out
       well inside that). It still marks the seat asked, but its money is left to the key's live usage. */
    SELECT @local = ISNULL( SUM( CASE WHEN State = 'settled' THEN ActualUsd
                                      WHEN State = 'unknown' AND CreatedUtc > DATEADD( MINUTE, -30, SYSUTCDATETIME() ) THEN ReservedUsd
                                      ELSE 0 END ), 0 ),
           @open  = ISNULL( SUM( CASE WHEN State = 'reserved' AND CreatedUtc > DATEADD( HOUR, -2, SYSUTCDATETIME() ) THEN ReservedUsd ELSE 0 END ), 0 )
      FROM quorum.SpendLedger
     WHERE CapName = @CapName;

    SET @SpentUsd = CASE WHEN @ServerUsedUsd > @local THEN @ServerUsedUsd ELSE @local END + @open;

    IF @SpentUsd + @ReserveUsd <= @LimitUsd
    BEGIN
        INSERT INTO quorum.SpendLedger ( CapName, SeatId, QuestionId, SweepId, ReservedUsd, State )
        VALUES ( @CapName, @SeatId, @QuestionId, @SweepId, @ReserveUsd, 'reserved' );

        SET @SpendId = SCOPE_IDENTITY();
    END

    COMMIT;
END
GO

SELECT CapName, LimitUsd FROM quorum.SpendCap;
SELECT name FROM sys.columns WHERE object_id = OBJECT_ID( 'quorum.SpendLedger' ) ORDER BY column_id;
GO
