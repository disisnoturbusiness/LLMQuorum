/* ============================================================================
   Paid lane, version 5 (2026-09-20). The owner chose each vendor's NATIVE web
   search over OpenRouter's Exa engine and funded the account to $69.94.

   Native search cannot be bounded inside a call: OpenRouter passes the cost
   through, ignores max_results and max_uses, and charges a request when it
   finishes. So the estimate is no longer treated as a limit. What holds instead:

   1. The key's fixed OpenRouter limit stops NEW requests (now $40 of the $70).
   2. RECONCILE: the key's own lifetime usage must never exceed what this ledger
      has recorded or is holding. If it does, something billed that we did not
      see (a timed-out call that finished upstream, a runaway search, a lost
      settle) and the lane halts until the owner clears it. This is the check
      that covers the cases an estimate cannot.
   3. Abandoned reservations (app killed mid-call) age into 'unknown' instead of
      holding their estimate forever.
   4. 'unknown' rows no longer hold their estimate for 30 minutes: rule 2 plus
      max( key usage, local ) covers them, and the hold was turning a temporary
      failure into a permanent "cap reached" refusal for later seats.
   ============================================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
GO

USE [LLMQuorum];
GO

/* The run needs about $26; the cap leaves room for variance and the key limit is the backstop. */
UPDATE quorum.SpendCap SET LimitUsd = 32.00 WHERE CapName = 'openrouter-paid';
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
    @HaltedReason  NVARCHAR(400) OUTPUT,
    @HeldUsd       DECIMAL(18,8) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET @SpendId = NULL;
    SET @Duplicate = 0;
    SET @SpentUsd = 0;
    SET @HeldUsd = 0;

    /* Rounding room only: every recorded figure is already the larger of usage.cost and our own pricing. */
    DECLARE @tolerance DECIMAL(18,8) = 0.02;

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

    /* A reservation older than two hours belongs to a call nobody is waiting for any more (the app was stopped, or
       Hangfire died mid-sweep). It stays asked, but it stops holding its estimate as if it were in flight. */
    UPDATE quorum.SpendLedger
       SET State = 'unknown', SettledUtc = SYSUTCDATETIME(),
           Note = ISNULL( Note, N'' ) + N'aged out of reserved: the call was abandoned; the key''s own usage carries its real cost'
     WHERE CapName = @CapName AND State = 'reserved' AND CreatedUtc <= DATEADD( HOUR, -2, SYSUTCDATETIME() );

    /* RECONCILE. Everything this ledger knows about, counted generously: settled rows at their cost, and every row
       that could have billed at its estimate. The key's lifetime usage must not exceed it. */
    DECLARE @recorded DECIMAL(18,8);

    SELECT @recorded = ISNULL( SUM( CASE WHEN State = 'settled' THEN ActualUsd ELSE ReservedUsd END ), 0 )
      FROM quorum.SpendLedger
     WHERE CapName = @CapName AND State <> 'voided';

    IF @ServerUsedUsd > @recorded + @tolerance
    BEGIN
        SET @HaltedReason = N'reconcile: OpenRouter says this key has used $'
                          + CONVERT( NVARCHAR(20), CAST( @ServerUsedUsd AS DECIMAL(10,4) ) )
                          + N' but the ledger only accounts for $'
                          + CONVERT( NVARCHAR(20), CAST( @recorded AS DECIMAL(10,4) ) )
                          + N'. Something billed that was never recorded. Check OpenRouter''s activity log.';

        UPDATE quorum.SpendCap SET HaltedReason = @HaltedReason, HaltedUtc = SYSUTCDATETIME()
         WHERE CapName = @CapName AND HaltedReason IS NULL;

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

    /* Settled rows count at their cost. 'unknown' rows count at nothing: rule 2 and the key's live usage cover them,
       and holding their estimate used to refuse later seats for a cap that was not really reached. Reservations that
       are still in flight (under two hours, thanks to the ageing above) count at their estimate. */
    SELECT @local = ISNULL( SUM( CASE WHEN State = 'settled' THEN ActualUsd ELSE 0 END ), 0 ),
           @open  = ISNULL( SUM( CASE WHEN State = 'reserved' THEN ReservedUsd ELSE 0 END ), 0 )
      FROM quorum.SpendLedger
     WHERE CapName = @CapName;

    SET @HeldUsd = @open;
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

SELECT CapName, LimitUsd, HaltedReason FROM quorum.SpendCap;
GO
