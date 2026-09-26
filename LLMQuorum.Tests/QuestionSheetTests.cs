using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using LLMQuorum.Core.Models;
using LLMQuorum.Core.Sweep;

namespace LLMQuorum.Tests;

/// <summary>
/// The per-question sheet: which result per seat is shown, how rules and judges grade it (Correct,
/// Outdated, Hallucinated, Wrong for math), and that the .xlsx is a valid workbook. Several inputs are
/// the exact cases an adversarial review used to break grader v1 on 2026-09-17.
/// </summary>
public sealed class QuestionSheetTests
{
    #region Data Members

    private const string G4_KEY = "Single; Married Filing Separate or Married Filing Joint, both spouses working; Married Filing Joint, one spouse working; Head of Household";

    private static readonly KnownAnswer[] _georgiaKnown =
    {
        new( "outdated", "Georgia G-4 Rev. 7/14", "Single; Married Filing Joint, both spouses working; Married Filing Joint, one spouse working; Married Filing Separate; Head of Household" ),
        new( "other-form", "federal Form W-4 (2019 and earlier)", "Single; Married; Married, but withhold at higher Single rate" )
    };

    #endregion Data Members

    #region Public Methods

    /// <summary>Buckets, including statuses not literally named that.</summary>
    [Theory]
    [InlineData( "CORRECT", SheetGrade.Correct )]
    [InlineData( "CORRECT-LOOSE", SheetGrade.Correct )]
    [InlineData( "TRUNCATED", SheetGrade.Truncated )]
    [InlineData( "THINKINGEXHAUSTED", SheetGrade.Truncated )]
    [InlineData( "REFUSAL", SheetGrade.Refusal )]
    [InlineData( "OUTDATED", SheetGrade.Outdated )]
    [InlineData( "HALLUCINATED", SheetGrade.Hallucinated )]
    [InlineData( "WRONG", SheetGrade.Wrong )]
    [InlineData( "UNCLASSIFIED", SheetGrade.Unclassified )]
    [InlineData( "ERROR", SheetGrade.Error )]
    [InlineData( "TIMEOUT", SheetGrade.Error )]
    [InlineData( "EMPTY", SheetGrade.Error )]
    [InlineData( "UNGRADED", SheetGrade.Ungraded )]
    public void Buckets( string grade, SheetGrade expected )
    {
        Assert.Equal( expected, SweepGrader.Bucket( grade ) );
    }

    /// <summary>Plain lists by rule, markers ignored, labels kept whole (no classification when Known is null).</summary>
    [Theory]
    [InlineData( "Single\nMarried Filing Separate or Married Filing Joint, both spouses working\nMarried Filing Joint, one spouse working\nHead of Household", "CORRECT" )]
    [InlineData( "1. **Single**\n2. **Married Filing Separate or Married Filing Joint, both spouses working**\n3. Married Filing Joint, one spouse working\n4. Head of Household", "CORRECT" )]
    [InlineData( "- Single\n- Married Filing Separate or Married Filing Jointly, both spouses working\n- Married Filing Jointly, one spouse working\n- Head of Household", "CORRECT-LOOSE" )]
    [InlineData( "Single\nMarried Filing Separate or Married Filing Joint, one spouse working\nMarried Filing Joint, both spouses working\nHead of Household", "WRONG" )]
    [InlineData( "Single\nMarried Filing Separate or Married Filing Joint\nBoth spouses working\nMarried Filing Joint\nOne spouse working\nHead of Household", "WRONG" )]
    public void GeorgiaLists_ByRule( string answer, string expected )
    {
        Assert.Equal( expected, Grade( answer, G4_KEY, AnswerShape.Set, known: null ).Grade );
    }

    /// <summary>His rule: the old Georgia list is Outdated; a list that was never on any G-4 is Hallucinated.</summary>
    [Theory]
    [InlineData( "Single\nMarried Filing Joint, both spouses working\nMarried Filing Joint, one spouse working\nMarried Filing Separate\nHead of Household", "OUTDATED", "Georgia G-4 Rev. 7/14" )]
    [InlineData( "Single\nMarried Filing Separate\nHead of Household", "OUTDATED", "Georgia G-4 Rev. 7/14" )]
    [InlineData( "Single\nMarried\nMarried, but withhold at higher Single rate", "HALLUCINATED", "from federal Form W-4 (2019 and earlier)" )]
    [InlineData( "Single\nMarried\nHead of Household", "HALLUCINATED", null )]
    [InlineData( "Single\nMarried Filing Separate\nExempt", "HALLUCINATED", null )]
    public void GeorgiaMisses_Classified( string answer, string grade, string? note )
    {
        var result = Grade( answer, G4_KEY, AnswerShape.Set, _georgiaKnown );
        Assert.Equal( grade, result.Grade );
        Assert.Equal( note, result.Note );
    }

    /// <summary>For a None key, rules decide bare none tokens and bare invented lists.</summary>
    [Theory]
    [InlineData( "None", "CORRECT" )]
    [InlineData( "**N/A**", "CORRECT" )]
    [InlineData( "- Not applicable.", "CORRECT" )]
    [InlineData( "Single\nMarried\nHead of Household", "HALLUCINATED" )]
    public void NoneKey_ByRule( string answer, string expected )
    {
        Assert.Equal( expected, Grade( answer, "None", AnswerShape.Set, Array.Empty<KnownAnswer>() ).Grade );
    }

    /// <summary>Everything the review used to fool v1 goes to judges instead of being guessed.</summary>
    [Theory]
    [InlineData( "Indiana’s WH-4 doesn’t ask for marital status." )]
    [InlineData( "There aren't any marital status options on Indiana Form WH-4." )]
    [InlineData( "None. The WH-4 only collects exemptions and county." )]
    [InlineData( "None\nI cannot verify the current revision." )]
    [InlineData( "1. **Single**\n2. **Married**\nIndiana has no filing status for withholding." )]
    [InlineData( "\"Single\"\n\"Married\"\nIndiana has no filing status for withholding." )]
    [InlineData( "Single, Married (Indiana does not use filing status for rates)." )]
    [InlineData( "Single.\nMarried.\nIndiana has no filing status for withholding." )]
    [InlineData( "I can’t access the current Georgia Form G-4." )]
    [InlineData( "Marital Status options printed on the form are:\n1. Married\n2. Separated" )]
    [InlineData( "None\nSingle" )]
    public void Prose_GoesToJudges( string answer )
    {
        Assert.True( Grade( answer, "None", AnswerShape.Set, Array.Empty<KnownAnswer>() ).NeedsJudge );
    }

    /// <summary>A stored refusal flag never short-circuits to REFUSAL; judges decide whether a hedge still answered.</summary>
    [Fact]
    public void StoredRefusal_GoesToJudges()
    {
        var context = new QuestionContext( "None", AnswerShape.Set, "settled-fact", Array.Empty<KnownAnswer>() );
        Assert.True( new SweepGrader().Grade( "Answered", true, "None - I can't verify, but the WH-4 has no marital status field.", context ).NeedsJudge );
    }

    /// <summary>Hourly-rate key: bare numbers by rule at the precision written; arithmetic misses are Wrong, not Hallucinated.</summary>
    [Theory]
    [InlineData( "67.31", "CORRECT" )]
    [InlineData( "$67.3077", "CORRECT" )]
    [InlineData( "**$67.31/hr**", "CORRECT" )]
    [InlineData( "67.30769 per hour", "CORRECT" )]
    [InlineData( "67.308", "CORRECT" )]
    [InlineData( "67.3100", "CORRECT" )]
    [InlineData( "67.3", "WRONG" )]
    [InlineData( "67", "WRONG" )]
    [InlineData( "67.3149", "WRONG" )]
    [InlineData( "$5,384.62", "WRONG" )]
    public void HourlyRate_ByRule( string answer, string expected )
    {
        Assert.Equal( expected, Grade( answer, "67.3077; 67.31", AnswerShape.Scalar, Array.Empty<KnownAnswer>(), "arithmetic" ).Grade );
    }

    /// <summary>A 2026 figure question answered with last year's official figure is Outdated; an unknown figure is Hallucinated.</summary>
    [Theory]
    [InlineData( "$176,100", "OUTDATED", "2025 wage base" )]
    [InlineData( "184500", "CORRECT", null )]
    [InlineData( "180000", "HALLUCINATED", null )]
    public void NumericOutdated_ByRule( string answer, string grade, string? note )
    {
        var known = new[] { new KnownAnswer( "outdated", "2025 wage base", "176100" ) };
        var result = Grade( answer, "184500", AnswerShape.Scalar, known );
        Assert.Equal( grade, result.Grade );
        Assert.Equal( note, result.Note );
    }

    /// <summary>Shown work goes to judges, who get a numeric key header.</summary>
    [Fact]
    public void ShownWork_GoesToJudges()
    {
        Assert.True( Grade( "$140,000 / 2,080 hours = $67.31 per hour", "67.3077; 67.31", AnswerShape.Scalar, Array.Empty<KnownAnswer>(), "arithmetic" ).NeedsJudge );
        Assert.Contains( "acceptable values", JudgePanel.BuildPrompt( "Q", "67.3077; 67.31", null, "a", AnswerShape.Scalar, Array.Empty<KnownAnswer>() ) );
        Assert.Contains( "Georgia G-4 Rev. 7/14 | an older version", JudgePanel.BuildPrompt( "Q", G4_KEY, null, "a", AnswerShape.Set, _georgiaKnown ) );
        Assert.Equal( "rubric-v2-scalar", JudgePanel.RubricVersionFor( AnswerShape.Scalar, Array.Empty<KnownAnswer>() ) );
    }

    /// <summary>Adding or changing a known answer changes the rubric version, so older verdicts are not reused.</summary>
    [Fact]
    public void RubricVersion_ChangesWithKnownAnswers()
    {
        var none = JudgePanel.RubricVersionFor( AnswerShape.Set, Array.Empty<KnownAnswer>() );
        var one = JudgePanel.RubricVersionFor( AnswerShape.Set, _georgiaKnown.Take( 1 ).ToList() );
        var two = JudgePanel.RubricVersionFor( AnswerShape.Set, _georgiaKnown );

        Assert.Equal( "rubric-v2", none );
        Assert.StartsWith( "rubric-v2-k", one );
        Assert.NotEqual( one, two );
        Assert.Equal( two, JudgePanel.RubricVersionFor( AnswerShape.Set, _georgiaKnown.Reverse().ToList() ) );
    }

    /// <summary>Units on bare numbers for the new questions: percent, cents, per mile.</summary>
    [Theory]
    [InlineData( "37%", "37", "CORRECT" )]
    [InlineData( "9.96%", "9.96", "CORRECT" )]
    [InlineData( "76 cents per mile", "76", "CORRECT" )]
    [InlineData( "76¢", "76", "CORRECT" )]
    [InlineData( "72.5 cents", "76", "OUTDATED" )]
    public void Units_ByRule( string answer, string key, string expected )
    {
        var known = new[] { new KnownAnswer( "outdated", "Jan-Jun 2026 rate", "72.5" ) };
        Assert.Equal( expected, Grade( answer, key, AnswerShape.Scalar, known ).Grade );
    }

    /// <summary>Judge-mode (Schedule 1-A): an exact list is still decided by rule; any other plain list goes to judges.</summary>
    [Fact]
    public void JudgeMode_OnlyExactByRule()
    {
        const string KEY = "No tax on tips; No tax on overtime; No tax on car loan interest; Enhanced deduction for seniors";
        var context = new QuestionContext( KEY, AnswerShape.Set, "closed-set", Array.Empty<KnownAnswer>(), JudgeNonExact: true );
        var grader = new SweepGrader();

        Assert.Equal( "CORRECT", grader.Grade( "Answered", false, "No tax on tips\nNo tax on overtime\nNo tax on car loan interest\nEnhanced deduction for seniors", context ).Grade );
        Assert.True( grader.Grade( "Answered", false, "Qualified tips deduction\nQualified overtime compensation deduction\nQualified passenger vehicle loan interest deduction\nEnhanced deduction for seniors", context ).NeedsJudge );
    }

    /// <summary>Judge replies: last JSON object with a valid verdict wins; "matches" must be a label shown to the judge.</summary>
    [Theory]
    [InlineData( "{\"verdict\":\"CORRECT\",\"reason\":\"says none\"}", "CORRECT", null )]
    [InlineData( "Thinking...\n{\"verdict\":\"wrong\",\"matches\":\"Georgia G-4 Rev. 7/14\",\"reason\":\"old list\"}", "WRONG", "Georgia G-4 Rev. 7/14" )]
    [InlineData( "{\"verdict\":\"WRONG\",\"matches\":\"some form I made up\"}", "WRONG", null )]
    [InlineData( "{\"verdict\":\"CORRECT\",\"matches\":\"Georgia G-4 Rev. 7/14\"}", "CORRECT", null )]
    [InlineData( "CORRECT", null, null )]
    public void ParseVerdict( string reply, string? verdict, string? label )
    {
        var parsed = JudgePanel.ParseVerdict( "j", reply, _georgiaKnown.Select( k => k.Label ).ToList() );
        Assert.Equal( verdict, parsed.Verdict );
        Assert.Equal( label, parsed.MatchLabel );
    }

    /// <summary>Agreement is the grade; right-vs-wrong splits are Unclassified; missing judges are labelled.</summary>
    [Theory]
    [InlineData( "CORRECT", "CORRECT", "CORRECT", "judged" )]
    [InlineData( "CORRECT", "WRONG", "UNCLASSIFIED", "judges split" )]
    [InlineData( "CORRECT", "REFUSAL", "REFUSAL", "judges split" )]
    [InlineData( "REFUSAL", "WRONG", "UNCLASSIFIED", "judges split" )]
    [InlineData( "CORRECT", null, "CORRECT", "1 judge" )]
    [InlineData( null, null, "UNGRADED", "judges unavailable" )]
    public void Combine( string? a, string? b, string grade, string note )
    {
        var result = JudgePanel.Combine( new[] { new JudgeVerdict( "a", a, null ), new JudgeVerdict( "b", b, null ) } );
        Assert.Equal( grade, result.Grade );
        Assert.Equal( note, result.Note );
    }

    /// <summary>Judged WRONG: the same named old version is Outdated; different names are Unclassified; none is Hallucinated or, for math, Wrong.</summary>
    [Fact]
    public void JudgedWrong_Classified()
    {
        var both = JudgePanel.Combine( new[] { new JudgeVerdict( "a", "WRONG", null, "Georgia G-4 Rev. 7/14" ), new JudgeVerdict( "b", "WRONG", null, "Georgia G-4 Rev. 7/14" ) } );
        var split = JudgePanel.Combine( new[] { new JudgeVerdict( "a", "WRONG", null, "Georgia G-4 Rev. 7/14" ), new JudgeVerdict( "b", "WRONG", null, null ) } );
        var context = new QuestionContext( G4_KEY, AnswerShape.Set, "settled-fact", _georgiaKnown );

        Assert.Equal( ("OUTDATED", "Georgia G-4 Rev. 7/14"), SweepGrader.ClassifyJudgedWrong( both.MatchLabel, both.MatchAgreed, context ) );
        Assert.Equal( "UNCLASSIFIED", SweepGrader.ClassifyJudgedWrong( split.MatchLabel, split.MatchAgreed, context ).Grade );
        Assert.Equal( "HALLUCINATED", SweepGrader.ClassifyJudgedWrong( null, true, context ).Grade );
        Assert.Equal( "WRONG", SweepGrader.ClassifyJudgedWrong( null, true, context with { Category = "arithmetic" } ).Grade );
    }

    /// <summary>Stored verdicts are reused; only judges without one are asked, and every call is saved.</summary>
    [Fact]
    public async Task Panel_ReusesStoredVerdicts()
    {
        var store = new MemoryStore();
        store.Saved[(7, "a")] = new JudgeVerdict( "a", "CORRECT", "stored" );
        var judgeA = new FakeJudge( "a", "{\"verdict\":\"WRONG\"}" );
        var judgeB = new FakeJudge( "b", "{\"verdict\":\"CORRECT\",\"reason\":\"fresh\"}" );
        var context = new QuestionContext( "None", AnswerShape.Set, "settled-fact", Array.Empty<KnownAnswer>() );

        var grade = await new JudgePanel( new IAnswerJudge[] { judgeA, judgeB }, store ).GradeAsync( 7, "Q", null, "no options", context, CancellationToken.None );

        Assert.Equal( "CORRECT", grade.Grade );
        Assert.Equal( 0, judgeA.Calls );
        Assert.Equal( 1, judgeB.Calls );
        Assert.True( store.Saved.ContainsKey( (7, "b") ) );
    }

    /// <summary>A later upstream error must not hide an earlier real answer from the same seat.</summary>
    [Fact]
    public void NewerError_DoesNotHideOlderAnswer()
    {
        var picked = QuestionSheet.PickLatestUsable( new[] { Seat( 3, "s", "Answered", "Single" ), Seat( 5, "s", "Error", null ) }, null );
        Assert.Single( picked );
        Assert.Equal( 3, picked[0].SweepId );
        Assert.Equal( 2, picked[0].Runs );
    }

    /// <summary>MEASURED: a profile change retires the old seat id; it is not listed twice.</summary>
    [Fact]
    public void RetiredSeat_Dropped()
    {
        var current = new HashSet<string> { "openrouter|liquid|memory|default" };
        var picked = QuestionSheet.PickLatestUsable( new[]
        {
            Seat( 3, "openrouter|liquid|memory|off", "Error", null ),
            Seat( 5, "openrouter|liquid|memory|default", "Answered", "Single" )
        }, current );

        Assert.Single( picked );
        Assert.Equal( "openrouter|liquid|memory|default", picked[0].SeatId );
    }

    /// <summary>Review case: a side-by-side seat missing from the newest sweep keeps its own last real answer.</summary>
    [Fact]
    public void SideBySideSeat_NotInNewestSweep_Kept()
    {
        var current = new HashSet<string> { "groq|oss|memory|low", "groq|oss|memory|high" };
        var picked = QuestionSheet.PickLatestUsable( new[]
        {
            Seat( 5, "groq|oss|memory|low", "Answered", "Single" ),
            Seat( 6, "groq|oss|memory|low", "Answered", "Single" ),
            Seat( 5, "groq|oss|memory|high", "Answered", "Married" )
        }, current );

        Assert.Equal( 2, picked.Count );
        Assert.Equal( 5, picked.Single( p => p.SeatId.EndsWith( "high" ) ).SweepId );
    }

    /// <summary>Review case: a clip landing inside an emoji must still produce a valid workbook.</summary>
    [Fact]
    public void LongAnswerWithEmojiAtLimit_WritesValidWorkbook()
    {
        var answer = new string( 'x', 31999 ) + "\U0001F600" + "tail";
        WithWorkbook( new List<IReadOnlyList<XCell>?> { new[] { new XCell( answer ) } }, null, document =>
        {
            var text = document.WorkbookPart!.WorksheetParts.Single().Worksheet!.Descendants<Cell>().Single().InlineString!.InnerText;
            Assert.True( text.Length <= 32000 );
            Assert.False( char.IsHighSurrogate( text[^1] ) );
        } );
    }

    /// <summary>MEASURED: the sheet was open in Excel; the refresh must land beside it instead of failing.</summary>
    [Fact]
    public void LockedTarget_WritesTimestampedSibling()
    {
        var dir = Directory.CreateTempSubdirectory( "llmquorum-place-" ).FullName;
        var target = Path.Combine( dir, "question-9.xlsx" );
        var temp = Path.Combine( dir, ".tmp" );
        File.WriteAllText( target, "old" );
        File.WriteAllText( temp, "new" );

        try
        {
            string placed;

            using( new FileStream( target, FileMode.Open, FileAccess.Read, FileShare.None ) )
            {
                placed = QuestionSheet.PlaceFile( temp, target );
            }

            Assert.NotEqual( target, placed );
            Assert.StartsWith( Path.Combine( dir, "question-9-" ), placed );
            Assert.Equal( "new", File.ReadAllText( placed ) );
            Assert.Equal( "old", File.ReadAllText( target ) );
        }
        finally
        {
            Directory.Delete( dir, true );
        }
    }

    /// <summary>End to end without a database: rules plus fake judges, valid workbook, summary rows by bucket name.</summary>
    [Fact]
    public async Task Workbook_RoundTrips()
    {
        var sheet = new QuestionSheet( "unused", new JudgePanel( new IAnswerJudge[]
        {
            new FakeJudge( "claude|sonnet", "{\"verdict\":\"CORRECT\",\"matches\":\"none\",\"reason\":\"says none\"}" ),
            new FakeJudge( "groq|oss", "{\"verdict\":\"CORRECT\",\"matches\":\"none\",\"reason\":\"no options\"}" )
        }, new MemoryStore() ), null );

        var question = new SheetQuestion( 99, "Q?", "None", "https://example.gov", AnswerShape.Set, "settled-fact", Array.Empty<KnownAnswer>() );
        var seats = new List<SheetSeat>
        {
            Seat( 3, "a", "Answered", "Single\nMarried" ),
            Seat( 3, "b", "Answered", "Single\nMarried" ),
            Seat( 4, "c", "Answered", "The WH-4 has no marital status options.", "web-search" ),
            Seat( 3, "d", "Truncated", null ),
            Seat( 3, "e", "Error", null )
        };

        await sheet.GradeAsync( seats, question, CancellationToken.None );
        var layout = QuestionSheetLayout.Build( question, new long[] { 3, 4 }, new List<string>(), sheet.Order( seats, AnswerShape.Set ) );

        WithWorkbook( layout.Rows, layout.FilterRange, document =>
        {
            var cells = document.WorkbookPart!.WorksheetParts.Single().Worksheet!.Descendants<Cell>()
                .ToDictionary( c => c.CellReference!.Value!, c => c.InlineString?.InnerText ?? c.CellValue?.Text );
            var summary = Enumerable.Range( 1, layout.DetailHeaderRow - 1 )
                .Where( r => cells.ContainsKey( $"A{r}" ) && cells.ContainsKey( $"D{r}" ) )
                .ToDictionary( r => cells[$"A{r}"]!, r => r );
            var d = layout.DetailHeaderRow;

            Assert.Equal( "1", cells[$"C{summary["Correct"]}"] );
            Assert.Equal( "2", cells[$"B{summary["Hallucinated"]}"] );
            Assert.Equal( "0", cells[$"D{summary["Outdated"]}"] );
            Assert.Equal( "5", cells[$"D{summary["Total"]}"] );
            Assert.False( summary.ContainsKey( "Wrong" ) );
            Assert.Equal( "Correct (judged)", cells[$"A{d + 1}"] );
            Assert.Contains( "sonnet: CORRECT", cells[$"I{d + 1}"] );
            Assert.Equal( "Truncated", cells[$"A{d + 2}"] );
            Assert.Equal( "Hallucinated", cells[$"A{d + 3}"] );
            Assert.Equal( "2", cells[$"J{d + 3}"] );
            Assert.Equal( "Error", cells[$"A{d + 5}"] );
        } );
    }

    #endregion Public Methods

    #region Private Methods

    private static RuleGrade Grade( string answer, string key, AnswerShape shape, IReadOnlyList<KnownAnswer>? known, string category = "settled-fact" ) =>
        new SweepGrader().Grade( "Answered", false, answer, new QuestionContext( key, shape, category, known ) );

    private static SheetSeat Seat( long sweepId, string seatId, string status, string? answer, string group = "memory" ) => new()
    {
        SweepId = sweepId, SweepCallId = sweepId * 100 + seatId.Length, SeatId = seatId, Provider = "p", ModelId = seatId,
        BaseModel = seatId, Group = group, Status = status, Answer = answer, LatencyMs = 1000, Attempts = 1
    };

    /// <summary>Writes rows to a temp workbook, validates it against the Open XML schema, and hands it to the assertions.</summary>
    private static void WithWorkbook( List<IReadOnlyList<XCell>?> rows, string? filter, Action<SpreadsheetDocument> assert )
    {
        var path = Path.Combine( Path.GetTempPath(), $"llmquorum-sheet-test-{Guid.NewGuid():N}.xlsx" );

        try
        {
            XlsxWriter.Write( path, "Question 99", rows, QuestionSheetLayout.ColumnWidths, filter );
            using var document = SpreadsheetDocument.Open( path, false );
            var errors = new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate( document ).Select( e => e.Description ).ToList();
            Assert.Empty( errors );
            Assert.Single( document.WorkbookPart!.Workbook!.Sheets!.Elements<Sheet>() );
            assert( document );
        }
        finally
        {
            File.Delete( path );
        }
    }

    private sealed class FakeJudge : IAnswerJudge
    {
        private readonly string _reply;

        public FakeJudge( string seat, string reply )
        {
            JudgeSeat = seat;
            _reply = reply;
        }

        public string JudgeSeat { get; }

        public int Calls { get; private set; }

        public Task<JudgeCall> AskAsync( string prompt, CancellationToken cancellationToken )
        {
            Calls++;
            return Task.FromResult( new JudgeCall( true, _reply, null, 5, null ) );
        }
    }

    private sealed class MemoryStore : IJudgementStore
    {
        public Dictionary<(long, string), JudgeVerdict> Saved { get; } = new();

        public Task<JudgeVerdict?> FindAsync( long sweepCallId, string judgeSeat, string rubricVersion, CancellationToken cancellationToken ) =>
            Task.FromResult( Saved.TryGetValue( (sweepCallId, judgeSeat), out var v ) && v.Verdict is not null ? v : null );

        public Task SaveAsync( long sweepCallId, string rubricVersion, string prompt, JudgeCall call, JudgeVerdict verdict, CancellationToken cancellationToken )
        {
            lock( Saved )
            {
                Saved[(sweepCallId, verdict.JudgeSeat)] = verdict;
            }

            return Task.CompletedTask;
        }
    }

    #endregion Private Methods
}
