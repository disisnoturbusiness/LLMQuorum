/* ============================================================================
   Paid seats (2026-09-18, owner: "gives me every model thats my point").
   Eleven paid models run through OpenRouter under their own provider key,
   'openrouter-paid', with their own OpenRouter API key and a hard dollar cap.
   - SweepCall gets the dollar cost, the provider that actually served the call,
     the OpenRouter generation id and the number of web searches, so every paid
     answer can be audited against the pin and the bill.
   - SpendCap / SpendLedger hold the cap. Every paid attempt reserves its worst
     case before it is sent (under an app lock, so two callers cannot both take
     the last dollar) and settles to the real cost after. An attempt with no
     reported cost (timeout, network) keeps its full reservation as spent.
   - CK_SweepPlan_Group widens to the new group.
   Additive only: no existing row or column changes.
   ============================================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
GO

USE [LLMQuorum];
GO

BEGIN TRAN;

IF COL_LENGTH( 'quorum.SweepCall', 'CostUsd' ) IS NULL
    ALTER TABLE quorum.SweepCall ADD
        CostUsd           DECIMAL(18,8) NULL,
        ServedProvider    NVARCHAR(100) NULL,
        GenerationId      VARCHAR(100)  NULL,
        WebSearchRequests INT           NULL;

IF OBJECT_ID( 'quorum.SpendCap' ) IS NULL
    CREATE TABLE quorum.SpendCap
    (
        CapName    VARCHAR(40)   NOT NULL CONSTRAINT PK_SpendCap PRIMARY KEY,
        LimitUsd   DECIMAL(10,4) NOT NULL CONSTRAINT CK_SpendCap_Limit CHECK ( LimitUsd >= 0 ),
        Note       NVARCHAR(400) NULL,
        UpdatedUtc DATETIME2(3)  NOT NULL CONSTRAINT DF_SpendCap_Updated DEFAULT SYSUTCDATETIME()
    );

IF OBJECT_ID( 'quorum.SpendLedger' ) IS NULL
    CREATE TABLE quorum.SpendLedger
    (
        SpendId     BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SpendLedger PRIMARY KEY,
        CapName     VARCHAR(40)   NOT NULL CONSTRAINT FK_SpendLedger_Cap REFERENCES quorum.SpendCap ( CapName ),
        SeatId      VARCHAR(200)  NOT NULL,
        ReservedUsd DECIMAL(18,8) NOT NULL,
        ActualUsd   DECIMAL(18,8) NULL,
        State       VARCHAR(12)   NOT NULL CONSTRAINT CK_SpendLedger_State CHECK ( State IN ( 'reserved', 'settled', 'unknown' ) ),
        Note        NVARCHAR(400) NULL,
        CreatedUtc  DATETIME2(3)  NOT NULL CONSTRAINT DF_SpendLedger_Created DEFAULT SYSUTCDATETIME(),
        SettledUtc  DATETIME2(3)  NULL
    );

/* $29.94 is on the account. Stop at $28.00 so paid spend can never push the shared balance negative:
   a negative balance makes OpenRouter refuse the free models too. */
IF NOT EXISTS ( SELECT 1 FROM quorum.SpendCap WHERE CapName = 'openrouter-paid' )
    INSERT INTO quorum.SpendCap ( CapName, LimitUsd, Note )
    VALUES ( 'openrouter-paid', 28.00, N'OpenRouter balance $29.94 on 2026-09-18; $1.94 kept back so free models never see a negative balance.' );

ALTER TABLE quorum.SweepPlan DROP CONSTRAINT CK_SweepPlan_Group;
ALTER TABLE quorum.SweepPlan ADD CONSTRAINT CK_SweepPlan_Group
    CHECK ( ProviderGroup IN ( 'others', 'openrouter', 'openrouter-paid', 'cloudflare', 'groq', 'cohere', 'zai', 'claude-sub' ) );

COMMIT;
GO

/* Reserve: counts settled spend at its actual cost and every open or unknown attempt at its full
   reservation, and inserts the new reservation only if it still fits under the cap. The app lock
   serialises reservations for one cap across connections and processes on this server. */
CREATE OR ALTER PROCEDURE quorum.ReserveSpend
    @CapName    VARCHAR(40),
    @SeatId     VARCHAR(200),
    @ReserveUsd DECIMAL(18,8),
    @SpendId    BIGINT        OUTPUT,
    @SpentUsd   DECIMAL(18,8) OUTPUT,
    @LimitUsd   DECIMAL(10,4) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET @SpendId = NULL;

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

    SELECT @SpentUsd = ISNULL( SUM( CASE WHEN State = 'settled' THEN ActualUsd ELSE ReservedUsd END ), 0 )
      FROM quorum.SpendLedger
     WHERE CapName = @CapName;

    IF @SpentUsd + @ReserveUsd <= @LimitUsd
    BEGIN
        INSERT INTO quorum.SpendLedger ( CapName, SeatId, ReservedUsd, State )
        VALUES ( @CapName, @SeatId, @ReserveUsd, 'reserved' );

        SET @SpendId = SCOPE_IDENTITY();
    END

    COMMIT;
END
GO

SELECT CapName, LimitUsd, Note FROM quorum.SpendCap;
SELECT name FROM sys.columns WHERE object_id = OBJECT_ID( 'quorum.SweepCall' ) AND name IN ( 'CostUsd', 'ServedProvider', 'GenerationId', 'WebSearchRequests' );
GO
