using LLMQuorum.Core.Matching;
using LLMQuorum.Core.Models;

namespace LLMQuorum.Core.Sweep;

/// <summary>Sheet buckets in display order. Wrong, Unclassified and Ungraded appear only when used.</summary>
public enum SheetGrade
{
    Correct,
    Truncated,
    Refusal,
    Outdated,
    Hallucinated,
    Wrong,
    Unclassified,
    Error,
    Ungraded
}

/// <summary>A verified answer that is not the key but has a name: an older version of the same thing, or another form's answer.</summary>
/// <param name="Kind">"outdated" (was once correct for this exact question) or "other-form" (correct for something else).</param>
/// <param name="Label">Name shown on the sheet, e.g. "Georgia G-4 Rev. 7/14".</param>
/// <param name="Answer">Answer in key format: ';' between list items, or a plain number.</param>
public sealed record KnownAnswer( string Kind, string Label, string Answer );

/// <summary>What grading needs to know about a question.</summary>
/// <param name="Expected">Answer key.</param>
/// <param name="Shape">Answer shape.</param>
/// <param name="Category">Question category; "arithmetic" questions have no outdated answers, so a miss is Wrong.</param>
/// <param name="Known">Named non-key answers; null disables Outdated/Hallucinated classification (plain WRONG).</param>
/// <param name="JudgeNonExact">True for questions whose correct wording varies: rules only accept exact matches, judges grade the rest.</param>
public sealed record QuestionContext( string? Expected, AnswerShape Shape, string Category, IReadOnlyList<KnownAnswer>? Known,
                                      bool JudgeNonExact = false );

/// <summary>Result of the rule tier: a grade, or a request for judges with the reason.</summary>
/// <param name="Grade">Final grade string, or null when judges must decide.</param>
/// <param name="Route">Why this grade or why judges are needed.</param>
/// <param name="Note">Version or form the answer matched, when any.</param>
public sealed record RuleGrade( string? Grade, string Route, string? Note = null )
{
    /// <summary>True when the rule tier could not grade safely.</summary>
    public bool NeedsJudge => Grade is null;
}

/// <summary>
/// Rule tier of sweep grading. Grades failed calls by status and plain lists or bare numbers by exact
/// comparison; prose, refusal wording, hedges and ambiguous one-line lists go to judges.
/// A wrong answer is split, per the owner (2026-09-17): "outdated and hallucinated are 2 different
/// things". Outdated means every item or value it commits to comes from a verified older version of
/// this same answer (or the current key) and at least one is from the old version. Anything else that
/// is wrong is Hallucinated, with the other form named when it reproduces one. Arithmetic misses are Wrong.
/// </summary>
public sealed class SweepGrader
{
    #region Data Members

    /// <summary>Rule tier version, recorded with judgements.</summary>
    public const string VERSION = "grader-v3";

    private readonly RuleBasedMatcher _matcher = new( "report-v1", 0.10 );

    #endregion Data Members

    #region Public Methods

    /// <summary>Grades one final attempt by rule, or says judges are needed.</summary>
    /// <param name="status">Stored status.</param>
    /// <param name="storedRefusal">Refusal flag stored at extraction time.</param>
    /// <param name="answer">Normalised answer text.</param>
    /// <param name="question">Key, shape, category and known answers.</param>
    /// <returns>The rule grade.</returns>
    public RuleGrade Grade( string status, bool storedRefusal, string? answer, QuestionContext question )
    {
        if( status != "Answered" )
        {
            return new RuleGrade( status.ToUpperInvariant(), "call status" );
        }

        if( question.Expected is null )
        {
            return new RuleGrade( "UNGRADED", "question has no answer key" );
        }

        var text = answer ?? string.Empty;

        if( storedRefusal || RuleBasedMatcher.IsRefusalText( text ) )
        {
            return new RuleGrade( null, "refusal wording; a hedge may still carry an answer" );
        }

        var graded = question.Shape switch
        {
            AnswerShape.Set => GradeList( text, question ),
            AnswerShape.Scalar when AnswerNumber.ParseKey( question.Expected ).Count > 0 => GradeNumber( text, question ),
            _ => GradeOther( text, question )
        };

        // Judge-mode questions (e.g. Schedule 1-A, where the official line names and the slogan names are
        // both right) never take a rule miss: only an exact match is decided by rule.
        return question.JudgeNonExact && graded.Grade is not null and not ( "CORRECT" or "CORRECT-LOOSE" )
            ? new RuleGrade( null, $"judge-mode question; rule said {graded.Grade}" )
            : graded;
    }

    /// <summary>
    /// Turns a judged WRONG into Outdated, Hallucinated, Wrong or Unclassified using the known answer the
    /// judges named. Judges that disagree about which version it matches leave it Unclassified rather than
    /// accuse a model of making something up.
    /// </summary>
    /// <param name="matchLabel">Known-answer label the judges agreed on, or null for none.</param>
    /// <param name="judgesAgree">False when judges named different labels.</param>
    /// <param name="question">Question context.</param>
    /// <returns>Grade and note.</returns>
    public static (string Grade, string? Note) ClassifyJudgedWrong( string? matchLabel, bool judgesAgree, QuestionContext question )
    {
        if( question.Known is null )
        {
            return ("WRONG", null);
        }

        if( !judgesAgree )
        {
            return ("UNCLASSIFIED", "judges disagree on which version it matches");
        }

        var known = question.Known.FirstOrDefault( k => string.Equals( k.Label, matchLabel, StringComparison.OrdinalIgnoreCase ) );
        return known is null ? (MissGrade( question ), null) : ClassifyKnown( known, question );
    }

    /// <summary>Maps a grade string to its sheet bucket.</summary>
    /// <param name="grade">Final grade.</param>
    /// <returns>The bucket.</returns>
    public static SheetGrade Bucket( string grade ) => grade switch
    {
        "CORRECT" or "CORRECT-LOOSE" => SheetGrade.Correct,
        "TRUNCATED" or "THINKINGEXHAUSTED" => SheetGrade.Truncated,
        "REFUSAL" => SheetGrade.Refusal,
        "OUTDATED" => SheetGrade.Outdated,
        "HALLUCINATED" => SheetGrade.Hallucinated,
        "WRONG" => SheetGrade.Wrong,
        "UNCLASSIFIED" => SheetGrade.Unclassified,
        "UNGRADED" => SheetGrade.Ungraded,
        _ => SheetGrade.Error
    };

    /// <summary>Key used to count seats that gave the same answer.</summary>
    /// <param name="answer">Normalised answer text.</param>
    /// <param name="shape">Answer shape.</param>
    /// <returns>The key.</returns>
    public string ClusterKey( string answer, AnswerShape shape ) => shape switch
    {
        AnswerShape.Set => "L:" + string.Join( "|", AnswerList.ParseAnswer( answer ).Items.OrderBy( i => i, StringComparer.Ordinal ) ),
        AnswerShape.Scalar when AnswerNumber.TryParseBare( answer, out var value, out _ ) =>
            "N:" + value.ToString( "0.############", System.Globalization.CultureInfo.InvariantCulture ),
        _ => _matcher.BuildKey( answer, shape ) ?? answer
    };

    #endregion Public Methods

    #region Private Methods

    /// <summary>Exact label comparison, trusted only on plain lists.</summary>
    private static RuleGrade GradeList( string answer, QuestionContext question )
    {
        var key = AnswerList.ParseKey( question.Expected! );
        var parsed = AnswerList.ParseAnswer( answer );

        // Line-for-line the key is correct even when a label contains words the plain-list guard distrusts
        // ("No tax on tips" contains "no"). Only exact equality skips the guard; anything else still goes to judges.
        if( key.Count > 1 && SameSet( parsed.Items, key, loose: false ) )
        {
            return new RuleGrade( "CORRECT", "labels match" );
        }

        if( !parsed.IsPlainList )
        {
            return new RuleGrade( null, parsed.Reason );
        }

        var keyIsNone = key.Count == 1 && AnswerList.IsNone( key[0] );

        if( keyIsNone && parsed.Items.All( AnswerList.IsNone ) )
        {
            return new RuleGrade( "CORRECT", "said none" );
        }

        if( keyIsNone && parsed.Items.Any( AnswerList.IsNone ) )
        {
            return new RuleGrade( null, "mixes none with labels" );
        }

        if( !keyIsNone && SameSet( parsed.Items, key, loose: false ) )
        {
            return new RuleGrade( "CORRECT", "labels match" );
        }

        if( !keyIsNone && SameSet( parsed.Items, key, loose: true ) )
        {
            return new RuleGrade( "CORRECT-LOOSE", "labels match apart from -ly forms" );
        }

        return ClassifyListMiss( parsed.Items, keyIsNone ? Array.Empty<string>() : key, question );
    }

    /// <summary>Outdated when every label is from an old version or the key and one is old-only; else hallucinated.</summary>
    private static RuleGrade ClassifyListMiss( IReadOnlyList<string> items, IReadOnlyList<string> key, QuestionContext question )
    {
        if( question.Known is null )
        {
            return new RuleGrade( "WRONG", "labels differ" );
        }

        foreach( var known in question.Known.Where( k => k.Kind == "outdated" ) )
        {
            var old = AnswerList.ParseKey( known.Answer ).Select( AnswerList.Loosen ).ToHashSet( StringComparer.Ordinal );
            var current = key.Select( AnswerList.Loosen ).ToHashSet( StringComparer.Ordinal );
            var given = items.Select( AnswerList.Loosen ).ToList();

            if( given.All( i => old.Contains( i ) || current.Contains( i ) ) && given.Any( i => old.Contains( i ) && !current.Contains( i ) ) )
            {
                var whole = old.SetEquals( given );
                return new RuleGrade( "OUTDATED", whole ? "matches an older version" : "only labels from an older version", known.Label );
            }
        }

        var otherForm = question.Known.FirstOrDefault( k => k.Kind == "other-form" &&
            SameSet( items, AnswerList.ParseKey( k.Answer ), loose: true ) );

        return otherForm is null
            ? new RuleGrade( MissGrade( question ), "matches no verified version" )
            : ToRule( ClassifyKnown( otherForm, question ), "labels from a different form" );
    }

    /// <summary>Bare numbers by rule at the precision written; misses classified against known values.</summary>
    private static RuleGrade GradeNumber( string answer, QuestionContext question )
    {
        if( !AnswerNumber.TryParseBare( answer, out var value, out var decimals ) )
        {
            return new RuleGrade( null, "not a bare number" );
        }

        if( AnswerNumber.Matches( value, decimals, AnswerNumber.ParseKey( question.Expected! ) ) )
        {
            return new RuleGrade( "CORRECT", "number matches key" );
        }

        if( question.Known is null )
        {
            return new RuleGrade( "WRONG", "number differs from key or is less precise" );
        }

        var known = question.Known.FirstOrDefault( k => AnswerNumber.ParseKey( k.Answer ) is { Count: > 0 } values &&
                                                        AnswerNumber.Matches( value, decimals, values ) );

        return known is null
            ? new RuleGrade( MissGrade( question ), "number matches no verified value" )
            : ToRule( ClassifyKnown( known, question ), "number matches a known value" );
    }

    /// <summary>Other shapes: exact key match by rule, everything else by judges.</summary>
    private RuleGrade GradeOther( string answer, QuestionContext question )
    {
        var answerKey = _matcher.BuildKey( answer, question.Shape );
        return answerKey is not null && answerKey == _matcher.BuildKey( question.Expected!, question.Shape )
            ? new RuleGrade( "CORRECT", "key match" )
            : new RuleGrade( null, "not an exact key match" );
    }

    private static (string Grade, string? Note) ClassifyKnown( KnownAnswer known, QuestionContext question ) =>
        known.Kind == "outdated" ? ("OUTDATED", known.Label) : ("HALLUCINATED", $"from {known.Label}");

    private static RuleGrade ToRule( (string Grade, string? Note) classified, string route ) =>
        new( classified.Grade, route, classified.Note );

    private static string MissGrade( QuestionContext question ) =>
        string.Equals( question.Category, "arithmetic", StringComparison.OrdinalIgnoreCase ) ? "WRONG" : "HALLUCINATED";

    private static bool SameSet( IEnumerable<string> a, IEnumerable<string> b, bool loose )
    {
        var left = loose ? a.Select( AnswerList.Loosen ) : a;
        var right = loose ? b.Select( AnswerList.Loosen ) : b;
        return left.ToHashSet( StringComparer.Ordinal ).SetEquals( right.ToHashSet( StringComparer.Ordinal ) );
    }

    #endregion Private Methods
}
