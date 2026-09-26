/* ============================================================================
   The paid pilot (2026-09-20). One real question, all 21 paid seats, its own
   plan so the recurring public-v2 job never touches it: that job advances only
   the plan it is named with, and this one is run by hand through
   POST /api/plans/paid-pilot/run.

   Question 19 is "the basic standard deduction for a single filer, tax year
   2026": a real question of the public set, with a verified known answer and
   middling web content, so what it costs reads across to the rest.

   Nothing here spends. The paid seats stay disabled in profiles.json until the
   run is deliberately started.
   ============================================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
GO

USE [LLMQuorum];
GO

DELETE FROM quorum.SweepPlan WHERE PlanName = 'paid-pilot';

INSERT INTO quorum.SweepPlan ( PlanName, QuestionId, Ordinal, ProviderGroup, Round, SeatFilter, Status, Note )
VALUES ( 'paid-pilot', 19, 1, 'openrouter-paid', 0,
N'["openrouter-paid|openai/gpt-5.6-luna|memory|default","openrouter-paid|openai/gpt-5.6-luna|web-search|default","openrouter-paid|openai/gpt-5.6-terra|memory|default","openrouter-paid|openai/gpt-5.6-terra|web-search|default","openrouter-paid|openai/gpt-5.6-sol|memory|default","openrouter-paid|openai/gpt-5.6-sol|web-search|default","openrouter-paid|openai/gpt-6-astra|memory|default","openrouter-paid|openai/gpt-6-astra|web-search|default","openrouter-paid|openai/gpt-chat-latest|memory|default","openrouter-paid|openai/gpt-chat-latest|web-search|default","openrouter-paid|google/gemini-3.8-flash|memory|default","openrouter-paid|google/gemini-3.8-flash|web-search|default","openrouter-paid|google/gemini-3.1-pro-preview|memory|default","openrouter-paid|google/gemini-3.1-pro-preview|web-search|default","openrouter-paid|google/gemini-3.5-flash-lite|memory|default","openrouter-paid|google/gemini-3.5-flash-lite|web-search|default","openrouter-paid|x-ai/grok-4.3|memory|default","openrouter-paid|x-ai/grok-4.3|web-search|default","openrouter-paid|x-ai/grok-4.6|memory|default","openrouter-paid|x-ai/grok-4.6|web-search|default","openrouter-paid|perplexity/sonar|web-search|default"]',
'pending', N'paid pilot: one real question, 21 seats, run by hand' );
GO

SELECT PlanItemId, PlanName, QuestionId, ProviderGroup, Status, Note FROM quorum.SweepPlan WHERE PlanName = 'paid-pilot';
GO
