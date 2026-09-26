/* ============================================================================
   Sized for the refund (2026-09-20). The pilot measured the real thing: one
   whole question, 21 seats, $0.4682 on OpenRouter's own meter. Twenty-one
   questions are left, so about $10 to finish, and the owner is taking the
   spare $40 back off the account.

   Cap $20 locally against a $25 hard limit on the key and a balance of about
   $29.94 after the refund. Every brake stays below the money that is there.
   ============================================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
GO

USE [LLMQuorum];
GO

UPDATE quorum.SpendCap
   SET LimitUsd = 20.00,
       Note = N'Local cap $20. Key llmquorum-paid: $25 fixed, no reset. Account about $29.94 after the 2026-09-20 refund. '
            + N'MEASURED: question 19 cost $0.4682 for all 21 seats, so the remaining 21 questions should cost about $10. '
            + N'A halt is stored here AND in keys\paid-halt.txt; clearing it means clearing both (SpendGate.ClearHaltAsync).'
 WHERE CapName = 'openrouter-paid';
GO

SELECT CapName, LimitUsd, HaltedReason FROM quorum.SpendCap WHERE CapName = 'openrouter-paid';
GO
