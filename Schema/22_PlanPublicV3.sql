/* ============================================================================
   The plan for Public set v3 (2026-09-24). Same shape as public-v2: one item per
   question per provider group, worked through slowly and in order by the
   recurring plan runner.

   The free groups start now. The paid group is seeded but held back (status
   'blocked') until the owner tops up OpenRouter: 54 questions at the measured
   $0.38 average is about $20, and these questions lean toward the expensive kind
   (agency documents), so the honest range is $20 to $40 against a $21.59 balance.
   Clearing it is one UPDATE, below.
   ============================================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
GO

USE [LLMQuorum];
GO

DELETE FROM quorum.SweepPlan WHERE PlanName = 'public-v3';
GO

INSERT INTO quorum.SweepPlan ( PlanName, QuestionId, Ordinal, ProviderGroup, Round, Status, NotBeforeUtc, Note )
SELECT 'public-v3', q.QuestionId, q.Ordinal, g.ProviderGroup, 0,
       CASE WHEN g.ProviderGroup = 'openrouter-paid' THEN 'cut-off' ELSE 'pending' END,
       CASE WHEN g.ProviderGroup = 'openrouter-paid' THEN CONVERT( DATETIME2(3), '2099-01-01' ) END,
       CASE WHEN g.ProviderGroup = 'openrouter-paid'
            THEN N'paid lane, held until the account is topped up'
            ELSE N'public set v3, seeded 2026-09-24' END
  FROM quorum.Question q
  JOIN quorum.QuestionSet s ON s.QuestionSetId = q.QuestionSetId
 CROSS JOIN ( VALUES ( 'cloudflare' ), ( 'groq' ), ( 'cohere' ), ( 'zai' ), ( 'openrouter' ), ( 'others' ), ( 'claude-sub' ), ( 'openrouter-paid' ) ) AS g ( ProviderGroup )
 WHERE s.Name = 'Public set v3' AND q.IsEnabled = 1;
GO

/* To release the paid lane once the money is there:
   UPDATE quorum.SweepPlan SET Status = 'pending', NotBeforeUtc = NULL WHERE PlanName = 'public-v3' AND ProviderGroup = 'openrouter-paid'; */

SELECT ProviderGroup, Status, COUNT(*) AS items
  FROM quorum.SweepPlan WHERE PlanName = 'public-v3'
 GROUP BY ProviderGroup, Status ORDER BY ProviderGroup;
GO
