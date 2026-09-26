namespace LLMQuorum.Core.Sweep;

/// <summary>Built sheet rows and where the detail block starts.</summary>
/// <param name="Rows">Rows top to bottom; null is a blank row.</param>
/// <param name="DetailHeaderRow">1-based row of the detail header.</param>
/// <param name="FilterRange">AutoFilter range over the detail block.</param>
public sealed record SheetLayout( List<IReadOnlyList<XCell>?> Rows, int DetailHeaderRow, string FilterRange );

/// <summary>
/// Cell layout of the per-question sheet: a header block, a grade summary split by memory vs web search,
/// then one row per seat. Kept apart from data loading so it can be tested without a database.
/// </summary>
public static class QuestionSheetLayout
{
    #region Data Members

    private static readonly string[] _detailHeaders =
    {
        "Grade", "Provider", "Model", "Base model", "Mode", "Reasoning", "Cap sent", "Answer, or why there is none",
        "How it was graded", "Seats with this answer", "Distinct models with this answer", "Seconds", "Out tokens",
        "Reasoning tokens", "Attempts", "Sweep", "Runs"
    };

    /// <summary>Column widths in characters, matching the detail headers.</summary>
    public static readonly double[] ColumnWidths = { 34, 12, 44, 28, 11, 16, 9, 70, 50, 10, 10, 9, 9, 10, 9, 7, 6 };

    /// <summary>Summary rows always shown, in order; Wrong, Unclassified and Ungraded are added only when used.</summary>
    private static readonly SheetGrade[] _alwaysShown =
    {
        SheetGrade.Correct, SheetGrade.Truncated, SheetGrade.Refusal, SheetGrade.Outdated, SheetGrade.Hallucinated, SheetGrade.Error
    };

    #endregion Data Members

    #region Public Methods

    /// <summary>Builds every row of the sheet.</summary>
    /// <param name="question">Question with key and known answers.</param>
    /// <param name="sweepIds">Sweeps that contributed candidates.</param>
    /// <param name="exclusions">Excluded rows and retired seats, as display lines.</param>
    /// <param name="seats">Graded seats in sheet order.</param>
    /// <returns>The layout.</returns>
    public static SheetLayout Build( SheetQuestion question, IReadOnlyList<long> sweepIds, IReadOnlyList<string> exclusions,
                                     IReadOnlyList<SheetSeat> seats )
    {
        var rows = new List<IReadOnlyList<XCell>?>
        {
            new[] { new XCell( $"LLMQuorum: question {question.Id}", XStyle.Title ) },
            Labelled( "Question", question.Prompt ),
            Labelled( "Answer key", question.Expected ?? "(none, so nothing is graded)" ),
            Labelled( "Key source", question.Source ?? "" ),
            Labelled( "Known older or other-form answers", question.Known.Count == 0 ? "none recorded"
                : string.Join( "; ", question.Known.Select( k => $"{k.Label} ({k.Kind})" ) ) ),
            Labelled( "Built from", $"sweeps {string.Join( ", ", sweepIds )}; each seat's newest result that reached the model. " +
                                    $"Rules grade plain lists and bare numbers; two judges from different labs grade prose ({JudgePanel.RUBRIC_VERSION}). " +
                                    "Outdated = an older official version of this same answer. Hallucinated = never the answer here." ),
            Labelled( "Excluded", exclusions.Count == 0 ? "nothing" : string.Join( "; ", exclusions ) ),
            null,
            new[] { new XCell( "Grade", XStyle.Header ), new XCell( "From memory", XStyle.Header ), new XCell( "Web search", XStyle.Header ), new XCell( "Total", XStyle.Header ) }
        };

        foreach( var bucket in Enum.GetValues<SheetGrade>() )
        {
            var inBucket = seats.Where( s => s.Bucket == bucket ).ToList();

            if( _alwaysShown.Contains( bucket ) || inBucket.Count > 0 )
            {
                rows.Add( SummaryRow( bucket.ToString(), StyleFor( bucket ), inBucket ) );
            }
        }

        rows.Add( SummaryRow( "Total", XStyle.Bold, seats.ToList() ) );
        rows.Add( null );
        rows.Add( _detailHeaders.Select( h => new XCell( h, XStyle.Header ) ).ToList() );

        var headerRow = rows.Count;
        rows.AddRange( seats.Select( DetailRow ) );
        var filter = $"A{headerRow}:{XlsxWriter.ColumnName( _detailHeaders.Length - 1 )}{headerRow + Math.Max( seats.Count, 1 )}";
        return new SheetLayout( rows, headerRow, filter );
    }

    /// <summary>Grade cell text: the bucket, then how it was reached and which version it matched.</summary>
    /// <param name="seat">Graded seat.</param>
    /// <returns>Display text such as "Outdated (judged; Georgia G-4 Rev. 7/14)".</returns>
    public static string GradeText( SheetSeat seat )
    {
        var parts = new[] { seat.GradeNote, seat.MatchNote }.Where( p => !string.IsNullOrEmpty( p ) ).ToList();
        return parts.Count == 0 ? seat.Bucket.ToString() : $"{seat.Bucket} ({string.Join( "; ", parts )})";
    }

    /// <summary>What the answer column shows: the answer, or the status and reason when there is none.</summary>
    /// <param name="seat">Graded seat.</param>
    /// <returns>Display text.</returns>
    public static string AnswerText( SheetSeat seat )
    {
        if( seat.Status == "Answered" )
        {
            return seat.Answer ?? "";
        }

        var reason = seat.Status == "ThinkingExhausted"
            ? $"spent the whole {seat.MaxTokensSent} token cap reasoning, no answer"
            : seat.ErrorText ?? seat.Decision ?? "";
        var partial = string.IsNullOrWhiteSpace( seat.Answer ) ? "" : $"\npartial: {seat.Answer}";
        return $"[{seat.Status}] {reason}{partial}";
    }

    /// <summary>Rule route, or each judge's verdict, named match and reason.</summary>
    /// <param name="seat">Graded seat.</param>
    /// <returns>Display text.</returns>
    public static string HowGraded( SheetSeat seat ) => seat.Verdicts.Count == 0
        ? $"rule: {seat.Route}"
        : string.Join( "\n", seat.Verdicts.Select( v =>
            $"{ShortSeat( v.JudgeSeat )}: {v.Verdict ?? "no verdict"}{( v.MatchLabel is null ? "" : $" ({v.MatchLabel})" )}. {v.Reason}" ) );

    #endregion Public Methods

    #region Private Methods

    private static string ShortSeat( string seatId )
    {
        var parts = seatId.Split( '|' );
        return parts.Length > 1 ? parts[1] : seatId;
    }

    private static XCell[] Labelled( string label, string value ) => new[] { new XCell( label, XStyle.Bold ), new XCell( value ) };

    private static XCell[] SummaryRow( string label, XStyle style, List<SheetSeat> seats ) => new[]
    {
        new XCell( label, style ),
        new XCell( seats.Count( s => s.Group != "web-search" ) ),
        new XCell( seats.Count( s => s.Group == "web-search" ) ),
        new XCell( seats.Count )
    };

    private static XStyle StyleFor( SheetGrade bucket ) => bucket switch
    {
        SheetGrade.Correct => XStyle.Correct,
        SheetGrade.Truncated => XStyle.Truncated,
        SheetGrade.Refusal => XStyle.Refusal,
        SheetGrade.Outdated => XStyle.Outdated,
        SheetGrade.Hallucinated or SheetGrade.Wrong => XStyle.Wrong,
        SheetGrade.Error => XStyle.Error,
        _ => XStyle.Bold
    };

    private static IReadOnlyList<XCell>? DetailRow( SheetSeat s ) => new[]
    {
        new XCell( GradeText( s ), StyleFor( s.Bucket ) ),
        new XCell( s.Provider ),
        new XCell( s.ModelId ),
        new XCell( s.BaseModel ),
        new XCell( s.Group ),
        new XCell( s.ReasoningEvidence ? $"{s.ReasoningModeUsed} (reasoned)" : s.ReasoningModeUsed ),
        new XCell( s.MaxTokensSent ),
        new XCell( AnswerText( s ), XStyle.Wrap ),
        new XCell( HowGraded( s ), XStyle.Wrap ),
        new XCell( s.ClusterKey is null ? null : s.SameAnswerSeats ),
        new XCell( s.ClusterKey is null ? null : s.SameAnswerModels ),
        new XCell( Math.Round( s.LatencyMs / 1000.0, 1 ) ),
        new XCell( s.CompletionTokens ),
        new XCell( s.ReasoningTokens ),
        new XCell( s.Attempts ),
        new XCell( s.SweepId ),
        new XCell( s.Runs )
    };

    #endregion Private Methods
}
