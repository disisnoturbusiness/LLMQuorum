/* ============================================================================
   Starter question set.

   Every answer key below was verified against a primary source during the
   research session that produced this tool, so this set can score itself on
   day one. It is deliberately small and deliberately HARD: twelve models across
   eight labs were asked question 1 and produced nine different wrong answers,
   none of them colliding. If the panel reports quorum on that question, the
   quorum is wrong, and that is the single most useful thing this set can show.
   ============================================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

USE [LLMQuorum];
GO

IF NOT EXISTS ( SELECT 1 FROM quorum.QuestionSet WHERE Name = N'Payroll withholding v1' )
    INSERT INTO quorum.QuestionSet ( Name, Notes )
    VALUES ( N'Payroll withholding v1',
             N'Answer keys verified against state .gov primary sources 2026-09-10/12.' );
GO

DECLARE @SetId INT = ( SELECT QuestionSetId FROM quorum.QuestionSet WHERE Name = N'Payroll withholding v1' );

;WITH seed AS
(
    SELECT * FROM ( VALUES
        ( 1, 'settled-fact', 'text',
          N'Name the current Colorado employee state income tax withholding certificate form number, and say whether it has a marital/filing status field. One short sentence. No caveats.',
          N'DR 0004; it has no marital/filing status field',
          N'https://tax.colorado.gov/DR0004' ),

        ( 2, 'settled-fact', 'scalar',
          N'How many marital status letter options are printed on the current Georgia Form G-4 employee withholding certificate? Answer with a single number only.',
          N'4',
          N'https://dor.georgia.gov/g-4-employee-withholding' ),

        ( 3, 'settled-fact', 'bool',
          N'Does Pennsylvania have a state employee income tax withholding allowance or exemption certificate equivalent to the federal Form W-4? Answer yes or no.',
          N'No',
          N'https://www.pa.gov/en/agencies/revenue/resources/tax-types-and-information/employer-withholding.html' ),

        ( 4, 'closed-set', 'set',
          N'List every withholding percentage option printed on the current Arizona Form A-4. Numbers only, comma separated.',
          N'0.5, 1.0, 1.5, 2.0, 2.5, 3.0, 3.5',
          N'https://azdor.gov/forms/withholding-forms/employees-arizona-withholding-election' ),

        ( 5, 'closed-set', 'set',
          N'List every US state that has no personal income tax and therefore no employer wage withholding. State names only, comma separated.',
          N'Alaska, Florida, Nevada, New Hampshire, South Dakota, Tennessee, Texas, Washington, Wyoming',
          N'https://taxadmin.org/tax-rates/' ),

        ( 6, 'settled-fact', 'text',
          N'What is the form number of the Colorado individual income tax RETURN? Answer with the form number only.',
          N'DR 0104',
          N'https://tax.colorado.gov/individual-income-tax-forms' ),

        ( 7, 'volatile-fact', 'scalar',
          N'What is the Georgia flat individual income tax rate for tax year 2026? Answer with a single percentage number only.',
          N'4.99',
          N'https://dor.georgia.gov/taxes/important-tax-updates' ),

        ( 8, 'settled-fact', 'text',
          N'Which US state employee withholding certificate uses a single letter code from A through F and has no filing status field at all? Name the state and the form number.',
          N'Connecticut, Form CT-W4',
          N'https://portal.ct.gov/drs/drs-forms/current-year-forms/withholding-forms' )
    ) AS v ( Ordinal, Category, Shape, Prompt, ExpectedAnswer, ExpectedSource )
)
MERGE quorum.Question AS target
USING ( SELECT @SetId AS QuestionSetId, * FROM seed ) AS source
   ON target.QuestionSetId = source.QuestionSetId AND target.Ordinal = source.Ordinal
WHEN MATCHED THEN
    UPDATE SET Category = source.Category, Prompt = source.Prompt, AnswerShape = source.Shape,
               ExpectedAnswer = source.ExpectedAnswer, ExpectedSource = source.ExpectedSource
WHEN NOT MATCHED THEN
    INSERT ( QuestionSetId, Ordinal, Category, Prompt, AnswerShape, ExpectedAnswer, ExpectedSource )
    VALUES ( source.QuestionSetId, source.Ordinal, source.Category, source.Prompt,
             source.Shape, source.ExpectedAnswer, source.ExpectedSource );
GO

SELECT QuestionSetId, COUNT(*) AS Questions
  FROM quorum.Question
 GROUP BY QuestionSetId;
GO
