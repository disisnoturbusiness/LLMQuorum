/* ============================================================================
   The paid lane over the whole public set (2026-09-20), after the pilot on
   question 19 answered 18 of 21 seats for $0.4188.

   One item per question in the existing public-v2 plan, so the recurring plan
   runner advances them in the same slow, in-order way as every other provider
   group: one question per run, paid seats last, and only what the dollars and
   the key's own limit allow. Question 19 is included; the seats it already
   asked are skipped by the ledger, so its item closes as soon as it is reached.

   Nothing here spends by itself. The guards that decide are quorum.SpendCap
   ($32), the key's fixed $40 OpenRouter limit, the balance margin, and the
   reconcile check in quorum.ReserveSpend.
   ============================================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
GO

USE [LLMQuorum];
GO

DELETE FROM quorum.SweepPlan WHERE PlanName = 'public-v2' AND ProviderGroup = 'openrouter-paid';

INSERT INTO quorum.SweepPlan ( PlanName, QuestionId, Ordinal, ProviderGroup, Round, Status, Note )
SELECT 'public-v2', q.QuestionId, q.Ordinal, 'openrouter-paid', 0, 'pending',
       N'paid lane, seeded 2026-09-20 after the pilot'
  FROM quorum.Question q
  JOIN quorum.QuestionSet s ON s.QuestionSetId = q.QuestionSetId
 WHERE s.Name = 'Public set v2' AND q.IsEnabled = 1;
GO

SELECT COUNT(*) AS paid_items, MIN(Ordinal) AS first_ordinal, MAX(Ordinal) AS last_ordinal
  FROM quorum.SweepPlan WHERE PlanName = 'public-v2' AND ProviderGroup = 'openrouter-paid';
GO
