/* ============================================================================
   Known answers: verified answers that are not the key but have a name.
   The owner's rule (2026-09-17): an OUTDATED answer and a HALLUCINATED answer are
   two different things. Outdated = an older official version of this same answer
   (it was true once). Hallucinated = never the answer here; when it reproduces a
   different form's answer, that form is named. Known answers are verified from
   primary sources exactly like keys, because calling a model "outdated" in public
   is also a claim.
   ============================================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

USE [LLMQuorum];
GO

IF OBJECT_ID( N'quorum.QuestionKnownAnswer', N'U' ) IS NULL
BEGIN
    CREATE TABLE quorum.QuestionKnownAnswer
    (
        KnownAnswerId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_QuestionKnownAnswer PRIMARY KEY,
        QuestionId    INT            NOT NULL CONSTRAINT FK_QuestionKnownAnswer_Question REFERENCES quorum.Question( QuestionId ),
        Kind          VARCHAR(20)    NOT NULL CONSTRAINT CK_QuestionKnownAnswer_Kind CHECK ( Kind IN ( 'outdated', 'other-form' ) ),
        Label         NVARCHAR(200)  NOT NULL,
        Answer        NVARCHAR(MAX)  NOT NULL,
        Source        NVARCHAR(1000) NOT NULL,
        CreatedUtc    DATETIME2(3)   NOT NULL CONSTRAINT DF_QuestionKnownAnswer_Created DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_QuestionKnownAnswer UNIQUE ( QuestionId, Label )
    );
END
GO

IF COL_LENGTH( N'quorum.SweepJudgement', N'MatchLabel' ) IS NULL
    ALTER TABLE quorum.SweepJudgement ADD MatchLabel NVARCHAR(200) NULL;
GO

/* Seed: Georgia G-4 (Q9) and Indiana WH-4 (Q10). Labels verified 2026-09-17 by text extraction. */
MERGE quorum.QuestionKnownAnswer AS t
USING ( VALUES
    ( 9,  'outdated',   N'Georgia G-4 Rev. 7/14',
          N'Single; Married Filing Joint, both spouses working; Married Filing Joint, one spouse working; Married Filing Separate; Head of Household',
          N'Form G-4 (Rev. 7/14), line 3 A-E, hosted copy https://www.fultoncountyga.gov/-/media/Forms/Human-Resources-Forms/Human-Resources--W4-and-Financial-Docv2-Final.pdf (pages 5-6), text extracted 2026-09-17' ),
    ( 9,  'other-form', N'federal Form W-4 (2019 and earlier)',
          N'Single; Married; Married, but withhold at higher Single rate',
          N'https://www.irs.gov/pub/irs-prior/fw4--2019.pdf line 3, text extracted 2026-09-17' ),
    ( 9,  'other-form', N'federal Form W-4 (2020 and later)',
          N'Single or Married filing separately; Married filing jointly or Qualifying surviving spouse; Head of household',
          N'https://www.irs.gov/pub/irs-pdf/fw4.pdf Form W-4 (2026) Step 1(c), text extracted 2026-09-17' ),
    ( 9,  'other-form', N'federal Form 1040 filing statuses',
          N'Single; Married filing jointly; Married filing separately; Head of household; Qualifying surviving spouse',
          N'https://www.irs.gov/pub/irs-pdf/f1040.pdf (2025) Filing Status, text extracted 2026-09-17' ),
    ( 10, 'other-form', N'federal Form W-4 (2019 and earlier)',
          N'Single; Married; Married, but withhold at higher Single rate',
          N'https://www.irs.gov/pub/irs-prior/fw4--2019.pdf line 3, text extracted 2026-09-17' ),
    ( 10, 'other-form', N'federal Form W-4 (2020 and later)',
          N'Single or Married filing separately; Married filing jointly or Qualifying surviving spouse; Head of household',
          N'https://www.irs.gov/pub/irs-pdf/fw4.pdf Form W-4 (2026) Step 1(c), text extracted 2026-09-17' ),
    ( 10, 'other-form', N'federal Form 1040 filing statuses',
          N'Single; Married filing jointly; Married filing separately; Head of household; Qualifying surviving spouse',
          N'https://www.irs.gov/pub/irs-pdf/f1040.pdf (2025) Filing Status, text extracted 2026-09-17' )
) AS s ( QuestionId, Kind, Label, Answer, Source )
ON t.QuestionId = s.QuestionId AND t.Label = s.Label
WHEN NOT MATCHED THEN INSERT ( QuestionId, Kind, Label, Answer, Source ) VALUES ( s.QuestionId, s.Kind, s.Label, s.Answer, s.Source );
GO
