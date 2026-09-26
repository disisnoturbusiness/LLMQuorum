/* ============================================================================
   Paid lane, round-4 redesign (2026-09-18). Four review rounds kept finding
   ways that "settled at $0" re-opened a seat that may have billed. The rule is
   now simpler and absolute:
   - Any reservation for (QuestionId, SeatId) means the seat was ASKED, forever,
     whatever it settled at. Nothing re-asks it automatically.
   - The only exception is state 'voided': a rejection OpenRouter documents as
     happening before any provider ran (auth, bad request, unmet pin, key limit,
     credits), set by the code, or a row the owner voids by hand after checking
     OpenRouter's activity log. A voided row does not mark the seat asked and
     counts $0.
   ============================================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
GO

USE [LLMQuorum];
GO

ALTER TABLE quorum.SpendLedger DROP CONSTRAINT CK_SpendLedger_State;
ALTER TABLE quorum.SpendLedger ADD CONSTRAINT CK_SpendLedger_State CHECK ( State IN ( 'reserved', 'settled', 'unknown', 'voided' ) );
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

    /* Asked before: any row for this question and seat that was not voided. */
    IF EXISTS ( SELECT 1 FROM quorum.SpendLedger
                 WHERE CapName = @CapName AND QuestionId = @QuestionId AND SeatId = @SeatId AND State <> 'voided' )
    BEGIN
        SET @Duplicate = 1;
        COMMIT;
        RETURN;
    END

    DECLARE @local DECIMAL(18,8), @open DECIMAL(18,8);

    /* Settled rows count at their cost. Unknown rows count at their estimate for 30 minutes, reserved rows for 2 hours
       (a paid send is never cancelled and times out well inside that); after that the key's live usage, which the
       caller passes in, carries whatever they really billed. */
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

SELECT definition FROM sys.check_constraints WHERE name = 'CK_SpendLedger_State';
GO
