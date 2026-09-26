/* ============================================================================
   Sweep harness v2: record exactly what was sent and received, the status the
   extractor assigned, and a local quota ledger that gates calls BEFORE they are
   made. Provider allowances cannot be read reliably (Cloudflare refuses the
   token on usage APIs; OpenRouter does not expose the free daily count), so the
   only trustworthy count is one kept here.
   ============================================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

USE [LLMQuorum];
GO

IF COL_LENGTH( 'quorum.SweepCall', 'SeatId' ) IS NULL
ALTER TABLE quorum.SweepCall ADD
    SeatId             VARCHAR( 300 )    NULL,
    BaseModel          VARCHAR( 120 )    NULL,
    ReasoningModeUsed  VARCHAR( 40 )     NULL,
    Status             VARCHAR( 30 )     NULL,
    IsRefusal          BIT               NOT NULL CONSTRAINT DF_SweepCall_IsRefusal DEFAULT( 0 ),
    ReasoningEvidence  BIT               NOT NULL CONSTRAINT DF_SweepCall_ReasoningEvidence DEFAULT( 0 ),
    RequestBytes       VARBINARY( MAX )  NULL,
    ResponseHeaders    NVARCHAR( MAX )   NULL,
    RawContent         NVARCHAR( MAX )   NULL,
    ReasoningText      NVARCHAR( MAX )   NULL,
    Neurons            DECIMAL( 12, 3 )  NULL,
    Decision           VARCHAR( 200 )    NULL,
    ExtractorVersion   VARCHAR( 20 )     NULL;
GO

/* ---------------------------------------------------------------------------
   QuotaLedger - one row per attempt or metered unit. WindowKey is the UTC day
   (yyyy-MM-dd) for daily allowances and the UTC month (yyyy-MM) for monthly ones,
   so a gate is a single SUM. Every attempt is counted, including failures,
   because OpenRouter and Groq count failed requests against the allowance too.
   --------------------------------------------------------------------------- */
IF OBJECT_ID( N'quorum.QuotaLedger', N'U' ) IS NULL
CREATE TABLE quorum.QuotaLedger
(
    LedgerId     BIGINT            NOT NULL IDENTITY( 1, 1 ),
    Provider     VARCHAR( 40 )     NOT NULL,
    ModelId      NVARCHAR( 200 )   NULL,
    Unit         VARCHAR( 20 )     NOT NULL,
    Amount       DECIMAL( 14, 3 )  NOT NULL,
    WindowKey    VARCHAR( 10 )     NOT NULL,
    Source       VARCHAR( 60 )     NOT NULL,
    RecordedUtc  DATETIME2( 3 )    NOT NULL CONSTRAINT DF_QuotaLedger_Recorded DEFAULT( SYSUTCDATETIME() ),
    CONSTRAINT PK_QuotaLedger PRIMARY KEY CLUSTERED ( LedgerId ),
    CONSTRAINT CK_QuotaLedger_Unit CHECK ( Unit IN ( 'request', 'neuron' ) )
);
GO

IF NOT EXISTS ( SELECT 1 FROM sys.indexes WHERE name = N'IX_QuotaLedger_Window' )
    CREATE NONCLUSTERED INDEX IX_QuotaLedger_Window ON quorum.QuotaLedger ( Provider, Unit, WindowKey ) INCLUDE ( Amount, ModelId );
GO

/* Cohere's monthly allowance has already been used this month by earlier tests and
   the verification pass. A baseline row stops the gate from believing it has all 1000.
   Counted from session records: ~9/12 tests 3, 9/16 panel runs 2, sweep 9, verification 8. */
IF NOT EXISTS ( SELECT 1 FROM quorum.QuotaLedger WHERE Provider = 'cohere' AND Source = 'baseline-estimate' AND WindowKey = '2026-09' )
    INSERT INTO quorum.QuotaLedger ( Provider, ModelId, Unit, Amount, WindowKey, Source )
    VALUES ( 'cohere', NULL, 'request', 30, '2026-09', 'baseline-estimate' );
GO
