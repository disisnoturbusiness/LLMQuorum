/* ============================================================================
   The cap row's own note was still quoting the old figures ($25 cap, $28 key,
   $29.94 account) after version 5 raised them. That row is where the owner
   looks first, so it says the real numbers and how to clear a halt.
   ============================================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
GO

USE [LLMQuorum];
GO

UPDATE quorum.SpendCap
   SET Note = N'Local cap $32. OpenRouter key limit $40 (key llmquorum-paid, fixed, no reset). Account $69.94 on 2026-09-20. '
            + N'Expected cost of the whole 22-question run: about $25. A halt is stored BOTH here and in keys\paid-halt.txt; '
            + N'clearing it means clearing both (SpendGate.ClearHaltAsync does both).'
 WHERE CapName = 'openrouter-paid';
GO

SELECT CapName, LimitUsd, HaltedReason, Note FROM quorum.SpendCap WHERE CapName = 'openrouter-paid';
GO
