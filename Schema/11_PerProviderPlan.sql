/* ============================================================================
   Per-provider plan queues (2026-09-18, owner: "do more than that ... the
   threshold, I want for all").
   - Each provider gets its own queue so a provider waiting on its daily reset
     (Cloudflare, OpenRouter) no longer holds back Groq, Cohere, Z.ai or Claude.
   - Open "others" items are split into one item per provider. For the question
     that was cut off only by Cloudflare (ordinal 15), the other providers had
     already answered, so their items are done; their transient failures from
     that run get a retry item.
   - The OpenRouter retry item that named only the three never-responsive seats
     is removed (the seats are disabled in profiles.json).
   - The laptop's duplicate run spent 28 OpenRouter requests today by its own
     ledger; that is recorded here so today's remaining budget is honest.
   ============================================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
GO

USE [LLMQuorum];
GO

BEGIN TRAN;

ALTER TABLE quorum.SweepPlan DROP CONSTRAINT CK_SweepPlan_Group;
ALTER TABLE quorum.SweepPlan ADD CONSTRAINT CK_SweepPlan_Group
    CHECK ( ProviderGroup IN ( 'others', 'openrouter', 'cloudflare', 'groq', 'cohere', 'zai', 'claude-sub' ) );

DELETE FROM quorum.SweepPlan
 WHERE PlanName = 'public-v2' AND ProviderGroup = 'openrouter' AND Round > 0 AND Status <> 'done'
   AND SeatFilter = N'["openrouter|google/gemma-4-26b-a4b-it:free|memory|off","openrouter|google/gemma-4-31b-it:free|memory|off","openrouter|z-ai/glm-5.2:free|memory|off"]';

DECLARE @open TABLE ( PlanItemId INT, QuestionId INT, Ordinal INT, Status VARCHAR(20), NotBeforeUtc DATETIME2(3), LastSweepId BIGINT );
INSERT INTO @open
SELECT PlanItemId, QuestionId, Ordinal, Status, NotBeforeUtc, LastSweepId
  FROM quorum.SweepPlan WHERE PlanName = 'public-v2' AND ProviderGroup = 'others' AND Round = 0 AND Status <> 'done';

INSERT INTO quorum.SweepPlan ( PlanName, QuestionId, Ordinal, ProviderGroup, Round, Status, NotBeforeUtc, LastSweepId, Note )
SELECT 'public-v2', o.QuestionId, o.Ordinal, p.Provider, 0,
       CASE WHEN o.Status = 'cut-off' AND p.Provider = 'cloudflare' THEN 'cut-off'
            WHEN o.Status = 'cut-off' THEN 'done'
            ELSE 'pending' END,
       CASE WHEN o.Status = 'cut-off' AND p.Provider = 'cloudflare' THEN o.NotBeforeUtc END,
       CASE WHEN o.Status = 'cut-off' THEN o.LastSweepId END,
       CONCAT( N'split from others item ', o.PlanItemId, N' on 2026-09-18' )
  FROM @open o
 CROSS JOIN ( VALUES ( 'cloudflare' ), ( 'groq' ), ( 'cohere' ), ( 'zai' ), ( 'claude-sub' ) ) AS p ( Provider );

/* Transient failures of non-Cloudflare seats in the cut-off question's last run get a retry item each. */
INSERT INTO quorum.SweepPlan ( PlanName, QuestionId, Ordinal, ProviderGroup, Round, SeatFilter, Status, NotBeforeUtc, Note )
SELECT 'public-v2', o.QuestionId, o.Ordinal, f.Platform, 1,
       CONCAT( N'["', f.SeatId, N'"]' ), 'pending', NULL, CONCAT( N'transient failure in sweep ', o.LastSweepId, N' (split 2026-09-18)' )
  FROM @open o
 CROSS APPLY (
    SELECT sc.SeatId, sc.Platform, sc.Status, sc.Decision,
           ROW_NUMBER() OVER ( PARTITION BY sc.SeatId ORDER BY sc.AttemptNo DESC, sc.SweepCallId DESC ) AS rn
      FROM quorum.SweepCall sc WHERE sc.SweepId = o.LastSweepId ) f
 WHERE o.Status = 'cut-off' AND f.rn = 1 AND f.Platform NOT IN ( 'cloudflare', 'openrouter' )
   AND ( f.Status IN ( 'Timeout', 'Network' ) OR ( f.Status = 'Error' AND f.Decision LIKE 'RetryAfterWait%' ) );

DELETE FROM quorum.SweepPlan WHERE PlanItemId IN ( SELECT PlanItemId FROM @open );

UPDATE quorum.SweepPlan
   SET ProviderGroup = LEFT( JSON_VALUE( SeatFilter, '$[0]' ), CHARINDEX( '|', JSON_VALUE( SeatFilter, '$[0]' ) ) - 1 ),
       UpdatedUtc = SYSUTCDATETIME()
 WHERE PlanName = 'public-v2' AND ProviderGroup = 'others' AND Round > 0 AND Status <> 'done';

IF NOT EXISTS ( SELECT 1 FROM quorum.QuotaLedger WHERE Source LIKE N'laptop duplicate run%' AND WindowKey = '2026-09-18' )
    INSERT INTO quorum.QuotaLedger ( Provider, ModelId, Unit, Amount, WindowKey, Source )
    VALUES ( 'openrouter', NULL, 'request', 28, '2026-09-18', N'laptop duplicate run (its own ledger showed 22 of 50 left)' );

COMMIT;
GO

SELECT ProviderGroup, Round, Status, COUNT(*) AS n, MIN( Ordinal ) AS first_ord, MAX( Ordinal ) AS last_ord
  FROM quorum.SweepPlan WHERE PlanName = 'public-v2' AND Status <> 'done'
 GROUP BY ProviderGroup, Round, Status ORDER BY ProviderGroup, Round, Status;
GO
