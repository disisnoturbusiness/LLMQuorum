/* ============================================================================
   Public set v2: 22 questions, 11 topics, 2 each. Approved by the owner
   2026-09-17 ("slow and steady"). Keys verified from primary sources by research
   and refuter agents (workflows wf_f18ab8e4-7cc and wf_6281f330-fa0), mileage and
   SQL items also checked directly. ExpectedSource is shown to judges as KEY NOTES.
   Questions 9, 10 and 11 move into this set; their sweeps are unaffected.
   ============================================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
GO

USE [LLMQuorum];
GO

DECLARE @Set INT = ( SELECT QuestionSetId FROM quorum.QuestionSet WHERE Name = N'Public set v2' );

BEGIN TRAN;

UPDATE quorum.Question SET QuestionSetId = @Set, Ordinal = 1,  Topic = N'Payroll math'                  WHERE QuestionId = 11;
UPDATE quorum.Question SET QuestionSetId = @Set, Ordinal = 3,  Topic = N'State withholding certificates' WHERE QuestionId = 9;
UPDATE quorum.Question SET QuestionSetId = @Set, Ordinal = 4,  Topic = N'State withholding certificates' WHERE QuestionId = 10;

DECLARE @Q TABLE ( Ordinal INT, Topic NVARCHAR(100), Category VARCHAR(20), Shape VARCHAR(10), GradeMode VARCHAR(10),
                   Prompt NVARCHAR(MAX), Expected NVARCHAR(MAX), Source NVARCHAR(MAX) );

INSERT INTO @Q VALUES
( 2, N'Payroll math', 'arithmetic', 'scalar', 'rule',
  N'In 2026, a non-exempt employee is paid $28.50 an hour and worked 47 hours in one workweek. Under the federal FLSA overtime rule, what is their gross pay for that week? Give only the dollar amount, no other text.',
  N'1439.25',
  N'29 U.S.C. 207(a)(1): time and a half for hours over 40 in a workweek. 40 x 28.50 = 1140.00; 7 x 42.75 = 299.25; total 1439.25. Computed two ways.' ),
( 5, N'New federal deductions (One Big Beautiful Bill Act)', 'closed-set', 'set', 'judge',
  N'Schedule 1-A (Form 1040) was introduced for tax year 2025. List the deductions it is used to claim, one per line. No numbering, no other text.',
  N'No tax on tips; No tax on overtime; No tax on car loan interest; Enhanced deduction for seniors',
  N'Schedule 1-A (Form 1040) 2025, Parts II-V; IRS FS-2026-04. The official line names are equally correct: Qualified tips deduction; Qualified overtime compensation deduction; Qualified passenger vehicle loan interest deduction; Enhanced deduction for seniors. Part I (modified AGI) and Part VI (total) are not deductions.' ),
( 6, N'New federal deductions (One Big Beautiful Bill Act)', 'volatile-fact', 'scalar', 'rule',
  N'For tax year 2026, what is the maximum amount a single filer may deduct for qualified overtime compensation, before any income-based phaseout? Give only the dollar amount, no other text.',
  N'12500',
  N'26 U.S.C. 225(b)(1): $12,500 ($25,000 for a joint return), not indexed; IRS FS-2026-13 (August 2026).' ),
( 7, N'2026 payroll limits', 'volatile-fact', 'scalar', 'rule',
  N'What is the 2026 Social Security wage base (the maximum earnings subject to Social Security tax)? Give only the dollar amount, no other text.',
  N'184500',
  N'SSA, Cost-of-Living Increase and Other Determinations for 2026, 90 FR 49047 (Nov 3, 2025): "The OASDI contribution and benefit base is $184,500 for remuneration paid in 2026". IRS Pub 15 (2026) agrees.' ),
( 8, N'2026 payroll limits', 'volatile-fact', 'scalar', 'rule',
  N'What is the 2026 limit on employee elective deferrals to a 401(k) plan, not counting catch-up contributions? Give only the dollar amount, no other text.',
  N'24500',
  N'IRS Notice 2025-67 and IR-2025-111 (Nov 13, 2025): elective deferral limit $24,500 for 2026.' ),
( 9, N'2026 benefit limits', 'volatile-fact', 'scalar', 'rule',
  N'What is the 2026 HSA contribution limit for self-only coverage, not counting the age-55 catch-up? Give only the dollar amount, no other text.',
  N'4400',
  N'Rev. Proc. 2025-19: 2026 HSA limit $4,400 self-only ($8,750 family).' ),
( 10, N'2026 benefit limits', 'volatile-fact', 'scalar', 'rule',
  N'What is the annual limit for a dependent care FSA (dependent care assistance program) for tax year 2026, for a married couple filing jointly? Give only the dollar amount, no other text.',
  N'7500',
  N'Public Law 119-21 sec. 70404 amended IRC 129(a)(2)(A): $7,500 ($3,750 married filing separately) for taxable years beginning after Dec 31, 2025; it was $5,000 since 1986.' ),
( 11, N'2026 individual income tax', 'volatile-fact', 'scalar', 'rule',
  N'What is the basic standard deduction for a single filer for tax year 2026, not age 65 or blind? Give only the dollar amount, no other text.',
  N'16100',
  N'Rev. Proc. 2025-32: $16,100 single for 2026. 2026 Form 1040-ES and Pub 505 (2026) agree.' ),
( 12, N'2026 individual income tax', 'volatile-fact', 'scalar', 'rule',
  N'What is the top marginal rate in the regular federal individual income tax brackets for tax year 2026? Give only the percentage as a number, no other text.',
  N'37',
  N'Rev. Proc. 2025-32 rate tables: top rate 37%. The One Big Beautiful Bill Act made 37% permanent instead of reverting to 39.6%. The 3.8% net investment income tax is a separate tax, not a bracket.' ),
( 13, N'Minimum wage 2026', 'settled-fact', 'scalar', 'rule',
  N'What is the federal minimum wage under the Fair Labor Standards Act in 2026 (the standard hourly rate, not the tipped minimum)? Give only the dollar amount, no other text.',
  N'7.25',
  N'29 U.S.C. 206(a)(1)(C), current through Public Law 119-108 (Sept 2026): $7.25 an hour, unchanged since July 24, 2009.' ),
( 14, N'Minimum wage 2026', 'volatile-fact', 'scalar', 'rule',
  N'What is Washington State''s statewide minimum wage per hour for 2026, for workers 16 and older? Give only the dollar amount, no other text.',
  N'17.13',
  N'Washington L&I: "The 2026 minimum wage in the state of Washington is $17.13 per hour." Poster FY26-120. Workers 14-15 may be paid 85% ($14.56); city rates such as Seattle''s are separate.' ),
( 15, N'Michigan delinquent property tax', 'settled-fact', 'scalar', 'rule',
  N'In Michigan, when unpaid real property taxes are returned as delinquent to the county treasurer, what percentage county property tax administration fee is added? Give only the percentage as a number, no other text.',
  N'4',
  N'MCL 211.78a(3): "A county property tax administration fee of 4% ... shall be added to property returned as delinquent" (minimum $1.00), current through PA 91 of 2026. Separate items: the local unit''s fee of up to 1% (MCL 211.44(3)) and 1% per month interest.' ),
( 16, N'Michigan delinquent property tax', 'settled-fact', 'scalar', 'rule',
  N'In Michigan, when property is forfeited to the county treasurer for unpaid delinquent property taxes, what flat dollar fee is added to each parcel? Give only the dollar amount, no other text.',
  N'175',
  N'MCL 211.78g(1): "the county treasurer shall add a $175.00 fee to each property", current through PA 91 of 2026; Treasury''s timeline calls it a title fee. Separate items: the $15 fee added October 1 (MCL 211.78d) and extra redemption interest.' ),
( 17, N'Software (.NET)', 'settled-fact', 'scalar', 'rule',
  N'Which C# language version does the compiler use by default for a project targeting .NET 10 (TargetFramework net10.0) with no LangVersion set? Give only the version number, no other text.',
  N'14',
  N'Microsoft Learn, C# language versioning, defaults table: .NET 10.x maps to C# 14.' ),
( 18, N'Software (.NET)', 'settled-fact', 'text', 'rule',
  N'According to Microsoft''s .NET support policy, on what date does support for .NET 10 end? Give only the date as YYYY-MM-DD, no other text.',
  N'2028-11-14',
  N'.NET and .NET Core Official Support Policy: .NET 10 (LTS) end of support November 14, 2028. Any unambiguous way of writing that date is correct.' ),
( 19, N'Employer compliance 2026', 'volatile-fact', 'scalar', 'rule',
  N'What is the ACA employer affordability percentage for plan years beginning in 2026? Give only the percentage as a number, no other text.',
  N'9.96',
  N'Rev. Proc. 2025-25: 9.96% for plan years beginning in 2026. Rev. Proc. 2026-26 sets 10.22% for 2027, which does not change 2026.' ),
( 20, N'Employer compliance 2026', 'volatile-fact', 'scalar', 'rule',
  N'What is the IRS standard mileage rate for business use of a car for miles driven on or after July 1, 2026, in cents per mile? Give only the number, no other text.',
  N'76',
  N'IRS Announcement 2026-11 (Internal Revenue Bulletin 2026-29): "The revised standard mileage rates are: (1) Business 76 cents per mile", for expenses on or after July 1, 2026, raised mid-year for fuel prices. Jan 1-Jun 30, 2026 was 72.5 cents (IR-2025-128).' ),
( 21, N'SQL', 'settled-fact', 'scalar', 'rule',
  N'In SQL Server 2025, what is the maximum number of dimensions for a column of the native VECTOR data type using its default base type (float32)? Give only the number, no other text.',
  N'1998',
  N'Microsoft Learn vector data type: "The maximum number of dimensions supported is 1998." Engine test on SQL Server 2025 RTM 17.0.1000.7: VECTOR(1998) created; VECTOR(1999) failed "exceeds the maximum allowed (1998)". The float16 preview type allows 3996.' ),
( 22, N'SQL', 'arithmetic', 'scalar', 'rule',
  N'In SQL Server with default settings (ANSI_NULLS ON), table t has one INT column x holding the rows 1, 2 and NULL. How many rows does SELECT * FROM t WHERE x NOT IN (1, NULL) return? Give only the number, no other text.',
  N'0',
  N'Engine test on SQL Server 2025: returns 0 rows. x NOT IN (1, NULL) expands to x <> 1 AND x <> NULL; x <> NULL is UNKNOWN, so no row qualifies. Without the NULL in the list it returns 1 row.' );

INSERT INTO quorum.Question ( QuestionSetId, Ordinal, Category, Prompt, ExpectedAnswer, ExpectedSource, AnswerShape, IsEnabled, CreatedUtc, Topic, GradeMode )
SELECT @Set, q.Ordinal, q.Category, q.Prompt, q.Expected, q.Source, q.Shape, 1, SYSUTCDATETIME(), q.Topic, q.GradeMode
  FROM @Q q
 WHERE NOT EXISTS ( SELECT 1 FROM quorum.Question x WHERE x.QuestionSetId = @Set AND x.Ordinal = q.Ordinal );

/* Plan: everyone-but-OpenRouter for every question not yet asked of them; OpenRouter for every question
   it has not answered (question 9's OpenRouter run is complete; 10 and 11 were only scheduled). */
INSERT INTO quorum.SweepPlan ( PlanName, QuestionId, Ordinal, ProviderGroup )
SELECT 'public-v2', x.QuestionId, x.Ordinal, g.ProviderGroup
  FROM quorum.Question x
 CROSS JOIN ( VALUES ( 'others' ), ( 'openrouter' ) ) AS g ( ProviderGroup )
 WHERE x.QuestionSetId = @Set
   AND NOT ( g.ProviderGroup = 'others' AND x.QuestionId IN ( 9, 10, 11 ) )
   AND NOT ( g.ProviderGroup = 'openrouter' AND x.QuestionId = 9 )
   AND NOT EXISTS ( SELECT 1 FROM quorum.SweepPlan p WHERE p.PlanName = 'public-v2' AND p.QuestionId = x.QuestionId AND p.ProviderGroup = g.ProviderGroup AND p.Round = 0 );

COMMIT;
GO

SELECT q.Ordinal, q.QuestionId, q.Topic, q.AnswerShape, q.GradeMode, q.ExpectedAnswer,
       ( SELECT STRING_AGG( p.ProviderGroup, ',' ) FROM quorum.SweepPlan p WHERE p.QuestionId = q.QuestionId AND p.PlanName = 'public-v2' ) AS PlanGroups
  FROM quorum.Question q JOIN quorum.QuestionSet s ON s.QuestionSetId = q.QuestionSetId
 WHERE s.Name = N'Public set v2' ORDER BY q.Ordinal;
GO
