/* ============================================================================
   Paid lane, round-3 review fixes (2026-09-18).
   - A halt (a paid call served by the wrong provider, or one that cost more than
     twice its estimate) is stored on quorum.SpendCap and refuses every later
     reservation until the owner clears it. Before this it lasted one sweep.
   - quorum.ReserveSpend reports the halt reason and the reservation keeps the
     key's usage read just before the send, so an attempt that comes back with
     no usage can be settled from the key's usage before and after the call.
   ============================================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
GO

USE [LLMQuorum];
GO

IF COL_LENGTH( 'quorum.SpendCap', 'HaltedReason' ) IS NULL
    ALTER TABLE quorum.SpendCap ADD HaltedReason NVARCHAR(400) NULL, HaltedUtc DATETIME2(3) NULL;
GO

IF COL_LENGTH( 'quorum.SpendLedger', 'KeyUsageBefore' ) IS NULL
    ALTER TABLE quorum.SpendLedger ADD KeyUsageBefore DECIMAL(18,8) NULL;
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
    @Duplicate     BIT           OUTPUT,
    @HaltedReason  NVARCHAR(400) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET @SpendId = NULL;
    SET @Duplicate = 0;
    SET @SpentUsd = 0;

    BEGIN TRAN;

    DECLARE @lock INT;
    DECLARE @resource NVARCHAR(255) = N'LLMQuorum.SpendCap.' + @CapName;
    EXEC @lock = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 30000;

    IF @lock < 0
    BEGIN
        ROLLBACK;
        THROW 51000, 'Could not lock the spend cap.', 1;
    END

    SELECT @LimitUsd = LimitUsd, @HaltedReason = HaltedReason FROM quorum.SpendCap WHERE CapName = @CapName;

    IF @LimitUsd IS NULL
    BEGIN
        ROLLBACK;
        THROW 51001, 'Unknown spend cap.', 1;
    END

    IF @HaltedReason IS NOT NULL
    BEGIN
        COMMIT;
        RETURN;
    END

    /* Asked before: any open, unknown or billed attempt for this question and seat. */
    IF EXISTS ( SELECT 1 FROM quorum.SpendLedger
                 WHERE CapName = @CapName AND QuestionId = @QuestionId AND SeatId = @SeatId
                   AND ( State IN ( 'reserved', 'unknown' ) OR ( State = 'settled' AND ActualUsd > 0 ) ) )
    BEGIN
        SET @Duplicate = 1;
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
        INSERT INTO quorum.SpendLedger ( CapName, SeatId, QuestionId, SweepId, ReservedUsd, State, KeyUsageBefore )
        VALUES ( @CapName, @SeatId, @QuestionId, @SweepId, @ReserveUsd, 'reserved', @ServerUsedUsd );

        SET @SpendId = SCOPE_IDENTITY();
    END

    COMMIT;
END
GO

SELECT name FROM sys.columns WHERE object_id = OBJECT_ID( 'quorum.SpendCap' ) ORDER BY column_id;
GO
