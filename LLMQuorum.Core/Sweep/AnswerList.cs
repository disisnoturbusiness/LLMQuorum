using System.Text;
using System.Text.RegularExpressions;

namespace LLMQuorum.Core.Sweep;

/// <summary>A parsed list answer: its items, and whether it is a plain list safe to grade by rule.</summary>
/// <param name="Items">Labels with list markers, markdown and trailing punctuation removed.</param>
/// <param name="IsPlainList">True only when every item is a short label with no prose, first person or negation.</param>
/// <param name="Reason">Why it is not a plain list, or "plain list".</param>
public sealed record ParsedList( IReadOnlyList<string> Items, bool IsPlainList, string Reason );

/// <summary>
/// Turns list answers and list answer keys into comparable labels. Rule grading is only trusted on
/// plain lists; anything with prose, first person, negation, or an ambiguous single comma line is
/// flagged so a judge decides instead.
/// MEASURED 2026-09-17 (adversarial review of grader v1): splitting on commas made "Married Filing
/// Joint, both spouses working" and "..., one spouse working" into shared fragments, so a list with
/// the qualifiers swapped graded CORRECT; and negation regexes over prose mis-graded in both
/// directions. Labels are therefore kept whole and prose is never graded by regex.
/// </summary>
public static partial class AnswerList
{
    #region Data Members

    private const int MAX_LABEL_WORDS = 12;

    private static readonly HashSet<string> _noneTokens = new( StringComparer.Ordinal ) { "none", "n a", "na", "not applicable" };

    #endregion Data Members

    #region Public Methods

    /// <summary>Parses an answer key. Keys are authored with ';' between labels, so commas stay inside labels.</summary>
    /// <param name="key">Answer key text.</param>
    /// <returns>Canonical labels.</returns>
    public static IReadOnlyList<string> ParseKey( string key ) =>
        key.Split( ';' ).Select( Canonical ).Where( item => item.Length > 0 ).Distinct( StringComparer.Ordinal ).ToList();

    /// <summary>Parses a model's answer into labels and decides whether rule grading can be trusted.</summary>
    /// <param name="answer">Normalised answer text.</param>
    /// <returns>The parsed list.</returns>
    public static ParsedList ParseAnswer( string answer )
    {
        var lines = Fold( answer ).Split( '\n' ).Select( line => line.Trim() ).Where( line => line.Length > 0 ).ToList();

        if( lines.Count == 0 )
        {
            return new ParsedList( Array.Empty<string>(), false, "empty" );
        }

        if( lines.Count == 1 )
        {
            if( lines[0].Contains( ';' ) )
            {
                lines = lines[0].Split( ';' ).Select( part => part.Trim() ).Where( part => part.Length > 0 ).ToList();
            }
            else if( lines[0].Contains( ',' ) )
            {
                return new ParsedList( new[] { Canonical( lines[0] ) }, false, "single line with commas is ambiguous" );
            }
        }

        var items = new List<string>();

        foreach( var line in lines )
        {
            var reason = WhyNotALabel( line );

            if( reason is not null )
            {
                return new ParsedList( lines.Select( Canonical ).ToList(), false, reason );
            }

            items.Add( Canonical( line ) );
        }

        return new ParsedList( items.Where( i => i.Length > 0 ).Distinct( StringComparer.Ordinal ).ToList(), true, "plain list" );
    }

    /// <summary>True when a canonical label means "there are none".</summary>
    /// <param name="canonical">Canonical label.</param>
    /// <returns>True for none, n/a, not applicable.</returns>
    public static bool IsNone( string canonical ) => _noneTokens.Contains( canonical );

    /// <summary>
    /// Comparable form of one label: Unicode folded, list marker and markdown removed, lower case,
    /// every run of non-alphanumerics collapsed to one space.
    /// </summary>
    /// <param name="label">One label.</param>
    /// <returns>Canonical label.</returns>
    public static string Canonical( string label )
    {
        var stripped = MarkerPrefix().Replace( Fold( label ).Trim(), string.Empty );
        return NonAlphanumeric().Replace( stripped.ToLowerInvariant(), " " ).Trim();
    }

    /// <summary>Tolerates -ly adverb forms (separately/separate, jointly/joint) that change no meaning.</summary>
    /// <param name="canonical">Canonical label.</param>
    /// <returns>The label with those two words shortened.</returns>
    public static string Loosen( string canonical ) =>
        LyWords().Replace( canonical, match => match.Value.ToLowerInvariant() == "separately" ? "separate" : "joint" );

    #endregion Public Methods

    #region Private Methods

    /// <summary>Returns why a line cannot be trusted as a bare label, or null when it can.</summary>
    private static string? WhyNotALabel( string line )
    {
        var body = MarkerPrefix().Replace( line, string.Empty ).Trim();
        var canonical = Canonical( line );

        if( IsNone( canonical ) )
        {
            return null;
        }

        if( line.TrimEnd( '*', '_', ' ' ).EndsWith( ':' ) )
        {
            return "introductory line";
        }

        if( canonical.Split( ' ', StringSplitOptions.RemoveEmptyEntries ).Length > MAX_LABEL_WORDS )
        {
            return "line too long to be a label";
        }

        if( SentenceBreak().IsMatch( body ) )
        {
            return "prose sentence";
        }

        if( FirstPerson().IsMatch( body ) )
        {
            return "first person";
        }

        return Negation().IsMatch( body ) ? "negation" : null;
    }

    /// <summary>Folds typographic dashes, quotes and spaces to ASCII so patterns see one spelling.</summary>
    private static string Fold( string text )
    {
        var builder = new StringBuilder( text.Length );

        foreach( var ch in text.Replace( "\r\n", "\n" ) )
        {
            builder.Append( ch switch
            {
                '‐' or '‑' or '‒' or '–' or '—' or '―' or '−' => '-',
                '‘' or '’' or '‛' or 'ʼ' => '\'',
                '“' or '”' or '‟' => '"',
                ' ' or ' ' or ' ' or ' ' => ' ',
                _ => ch
            } );
        }

        return builder.ToString();
    }

    /// <summary>Leading bullets, numbering ("1.", "(2)", "a)"), markdown emphasis and quotes; trailing punctuation.</summary>
    [GeneratedRegex( @"^(?:[\s\-*+•·▪◦‣>#]+|\(?\d{1,2}[.)]\s*|\(?[A-Za-z][.)]\s+)*[*_`""']*|[*_`""'.,;:]+$" )]
    private static partial Regex MarkerPrefix();

    [GeneratedRegex( @"[^a-z0-9]+" )]
    private static partial Regex NonAlphanumeric();

    [GeneratedRegex( @"\b(separately|jointly)\b", RegexOptions.IgnoreCase )]
    private static partial Regex LyWords();

    [GeneratedRegex( @"[.!?]\s+\S" )]
    private static partial Regex SentenceBreak();

    [GeneratedRegex( @"\bI\b|\bI'(m|ve|d|ll)\b|(?i:\b(me|my|we|our)\b)" )]
    private static partial Regex FirstPerson();

    [GeneratedRegex( @"\b(no|not|none|never|without|cannot)\b|n't\b", RegexOptions.IgnoreCase )]
    private static partial Regex Negation();

    #endregion Private Methods
}
