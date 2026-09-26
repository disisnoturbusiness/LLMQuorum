using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LLMQuorum.Core.Models;

namespace LLMQuorum.Core.Matching;

/// <summary>
/// Deterministic, shape-aware comparison. Every decision here is arithmetic or
/// string handling, so it cannot be quietly wrong the way a classifier can, and
/// it costs nothing to replay over stored answers.
/// Why deterministic first: the observed failure mode is models returning the
/// same fact in different prose. Normalizing to a comparison key catches that
/// without a judge. A model-backed matcher can implement this same interface
/// later for genuinely prose-shaped questions; it is not needed for scalars,
/// enumerations, booleans or identifier-style answers, which is most of what
/// the panel is good for.
/// </summary>
public sealed partial class RuleBasedMatcher : IAnswerMatcher
{
    #region Data Members

    /// <summary>Relative tolerance applied to Scalar comparisons, e.g. 0.10 accepts values within ten percent.</summary>
    private readonly double _scalarTolerance;

    /// <summary>Version string recorded on every verdict so rule changes are diffable.</summary>
    private readonly string _version;

    /// <summary>Words that carry no meaning for sameness and only vary by model verbosity.</summary>
    private static readonly HashSet<string> _noiseWords = new( StringComparer.OrdinalIgnoreCase )
    {
        "the", "a", "an", "is", "are", "current", "currently", "form", "number", "it", "and", "for", "of", "to"
    };

    /// <summary>Tokens meaning yes, used to collapse Bool answers onto a single key.</summary>
    private static readonly HashSet<string> _affirmative = new( StringComparer.OrdinalIgnoreCase )
    {
        "yes", "true", "y", "affirmative", "correct", "does", "has", "is", "includes", "contains"
    };

    /// <summary>
    /// Tokens meaning no. Scanned BEFORE affirmatives because negation usually
    /// arrives as a modifier on an affirmative verb: "it does not" contains
    /// "does", and a first-match-wins scan over the whole sentence would read
    /// that as yes. Negation-first is the safe default here, since a false
    /// "yes" silently manufactures agreement between opposite answers.
    /// </summary>
    private static readonly HashSet<string> _negative = new( StringComparer.OrdinalIgnoreCase )
    {
        "no", "not", "false", "n", "negative", "incorrect", "none", "never", "cannot", "without", "lacks"
    };

    #endregion Data Members

    #region Constructor

    /// <summary>Builds a matcher bound to one rule configuration.</summary>
    /// <param name="version">Rule set identifier recorded on every verdict.</param>
    /// <param name="scalarTolerance">Relative tolerance for numeric comparison.</param>
    public RuleBasedMatcher( string version, double scalarTolerance )
    {
        _version = version;
        _scalarTolerance = scalarTolerance;
    }

    #endregion Constructor

    #region Public Methods

    /// <inheritdoc />
    public string Version => _version;

    /// <inheritdoc />
    public string? BuildKey( string answerText, AnswerShape shape )
    {
        if( string.IsNullOrWhiteSpace( answerText ) )
        {
            return null;
        }

        answerText = NormalizeUnicode( answerText );

        // A refusal is not an answer and must never become a vote.
        // MEASURED: Cohere replied "I don't have access to the current Georgia
        // Form G-4, so I cannot provide the exact labels" and it was clustered
        // as a one-vote answer, inflating the spread with a non-answer.
        if( IsRefusal( answerText ) )
        {
            return null;
        }

        return shape switch
        {
            AnswerShape.Scalar => BuildScalarKey( answerText ),
            AnswerShape.Bool => BuildBoolKey( answerText ),
            AnswerShape.Set => BuildSetKey( answerText ),
            _ => BuildTextKey( answerText )
        };
    }

    /// <summary>The panel's refusal test, with Unicode folding, for callers outside the panel (sweep grading).</summary>
    /// <param name="text">Raw answer text.</param>
    /// <returns>True when the text reads as a first-person refusal or inability statement.</returns>
    public static bool IsRefusalText( string text ) => IsRefusal( NormalizeUnicode( text ) );

    /// <inheritdoc />
    public List<AnswerCluster> Cluster( IReadOnlyList<ProviderAnswer> answers, AnswerShape shape )
    {
        // Scalars cannot be clustered by key. Any fixed bucketing splits some
        // pairs that ARE within tolerance whenever they straddle a bucket edge:
        // at ten percent, 5000 and 5200 differ by four percent yet fall either
        // side of a boundary. Grouping over the sorted values avoids that.
        if( shape == AnswerShape.Scalar )
        {
            return ClusterScalars( answers );
        }

        var buckets = new Dictionary<string, (string Representative, List<string> Providers, int FirstRank)>(
            StringComparer.Ordinal );

        foreach( var answer in answers.Where( a => a.IsSuccess && a.AnswerText is not null )
                                      .OrderBy( a => a.Rank ) )
        {
            var key = BuildKey( answer.AnswerText!, shape );

            if( key is null )
            {
                continue;
            }

            if( buckets.TryGetValue( key, out var existing ) )
            {
                existing.Providers.Add( answer.Provider.Key );
            }
            else
            {
                buckets[key] = ( answer.AnswerText!.Trim(), new List<string> { answer.Provider.Key }, answer.Rank );
            }
        }

        return buckets.Values
            .OrderByDescending( b => b.Providers.Count )
            .ThenBy( b => b.FirstRank )
            .Select( b => new AnswerCluster { Answer = b.Representative, ProviderKeys = b.Providers } )
            .ToList();
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// Normalizes prose to a comparison key. Identifier-style tokens such as
    /// "DR 0004" are preserved and given priority, because on identifier
    /// questions that token IS the answer and the surrounding sentence is noise
    /// that varies purely by model verbosity.
    /// </summary>
    /// <param name="text">Raw answer text.</param>
    /// <returns>A normalized key suitable for equality comparison.</returns>
    private static string BuildTextKey( string text )
    {
        var identifiers = IdentifierPattern().Matches( text )
            .Select( m => NormalizeIdentifier( m.Value ) )
            .Distinct( StringComparer.Ordinal )
            .OrderBy( v => v, StringComparer.Ordinal )
            .ToList();

        if( identifiers.Count > 0 )
        {
            return "ID:" + string.Join( "|", identifiers );
        }

        var words = WordPattern().Matches( text.ToLowerInvariant() )
            .Select( m => m.Value )
            .Where( w => !_noiseWords.Contains( w ) )
            .ToList();

        return "TX:" + string.Join( " ", words );
    }

    /// <summary>
    /// Collapses identifier spelling differences so "DR 0004", "DR-0004" and
    /// "dr0004" compare equal. Leading zeros are preserved because they are
    /// meaningful on real form numbers: DR 0004 and DR 004 are different forms.
    /// </summary>
    /// <param name="value">Matched identifier token.</param>
    /// <returns>Uppercased token with separators removed.</returns>
    private static string NormalizeIdentifier( string value )
    {
        var builder = new StringBuilder( value.Length );

        foreach( var character in value )
        {
            if( char.IsLetterOrDigit( character ) )
            {
                builder.Append( char.ToUpperInvariant( character ) );
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Reduces a numeric answer to its exact parsed value. Tolerance is applied
    /// during clustering rather than here, because a key is an equality test and
    /// equality cannot express "close enough" without splitting pairs at the
    /// bucket boundary.
    /// </summary>
    /// <param name="text">Raw answer text containing a number.</param>
    /// <returns>The exact value as a key, or null when no number is present.</returns>
    private static string? BuildScalarKey( string text )
    {
        var value = ParseNumber( text );
        return value is null ? null : "NUM:" + value.Value.ToString( "R", CultureInfo.InvariantCulture );
    }

    /// <summary>
    /// Groups numeric answers by walking them in ascending order and starting a
    /// new cluster whenever the next value is further than the tolerance from
    /// the cluster's anchor. Anchoring on the first member rather than a running
    /// mean keeps the grouping deterministic and stops a long chain of small
    /// steps from silently merging two genuinely different magnitudes.
    /// </summary>
    /// <param name="answers">Every answer collected so far for one question.</param>
    /// <returns>Clusters ordered by vote count descending, then by first ask order.</returns>
    private List<AnswerCluster> ClusterScalars( IReadOnlyList<ProviderAnswer> answers )
    {
        var parsed = answers
            .Where( a => a.IsSuccess && a.AnswerText is not null )
            .Where( a => !IsRefusal( NormalizeUnicode( a.AnswerText! ) ) )
            .Select( a => ( Answer: a, Value: ParseNumber( a.AnswerText! ) ) )
            .Where( x => x.Value.HasValue )
            .OrderBy( x => x.Value!.Value )
            .ToList();

        var groups = new List<(double Anchor, string Representative, List<string> Providers, int FirstRank)>();

        foreach( var (answer, value) in parsed )
        {
            var current = groups.Count > 0 ? groups[^1] : default;

            var fits = groups.Count > 0 &&
                       Math.Abs( value!.Value - current.Anchor ) <= Math.Abs( current.Anchor ) * _scalarTolerance;

            if( fits )
            {
                current.Providers.Add( answer.Provider.Key );
            }
            else
            {
                groups.Add( ( value!.Value, answer.AnswerText!.Trim(),
                              new List<string> { answer.Provider.Key }, answer.Rank ) );
            }
        }

        return groups
            .OrderByDescending( g => g.Providers.Count )
            .ThenBy( g => g.FirstRank )
            .Select( g => new AnswerCluster { Answer = g.Representative, ProviderKeys = g.Providers } )
            .ToList();
    }

    /// <summary>Extracts the first number from an answer, ignoring thousands separators.</summary>
    /// <param name="text">Raw answer text.</param>
    /// <returns>The parsed value, or null when the answer contains no number.</returns>
    private static double? ParseNumber( string text )
    {
        var match = NumberPattern().Match( text.Replace( ",", string.Empty ) );

        return match.Success &&
               double.TryParse( match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value )
            ? value
            : null;
    }

    /// <summary>
    /// Maps an answer onto yes, no, or unknown by finding the first decisive
    /// token. Models wrap booleans in sentences, so a bare equality test would
    /// split identical answers across clusters.
    /// </summary>
    /// <param name="text">Raw answer text.</param>
    /// <returns>A boolean key, or null when neither sense is expressed.</returns>
    private static string? BuildBoolKey( string text )
    {
        var words = WordPattern().Matches( text.ToLowerInvariant() ).Select( m => m.Value ).ToList();

        // Negation wins. "It does not" contains an affirmative verb, so scanning
        // affirmatives first would read a denial as an agreement.
        if( words.Any( _negative.Contains ) )
        {
            return "BOOL:NO";
        }

        return words.Any( _affirmative.Contains ) ? "BOOL:YES" : null;
    }

    /// <summary>
    /// Treats the answer as an unordered collection. Sorting before joining
    /// means two models listing the same items in different order compare equal,
    /// which is the whole point of declaring a question as Set-shaped.
    /// </summary>
    /// <param name="text">Raw answer text containing delimited items.</param>
    /// <returns>A sorted, normalized set key.</returns>
    private static string BuildSetKey( string text )
    {
        var items = text
            .Split( new[] { ',', ';', '\n', '\r', '|', '/' }, StringSplitOptions.RemoveEmptyEntries )
            .Select( item => NormalizeIdentifier( item ) )
            .Where( item => item.Length > 0 )
            .Distinct( StringComparer.Ordinal )
            .OrderBy( item => item, StringComparer.Ordinal );

        return "SET:" + string.Join( "|", items );
    }

    /// <summary>
    /// Folds the Unicode punctuation models actually emit down to ASCII before
    /// any pattern runs.
    /// MEASURED, not theoretical: on the first live sweep one provider returned
    /// "DR&#8209;0004" using U+2011 NON-BREAKING HYPHEN while another returned
    /// "DR 0004". They are the same form number, but the Unicode dash did not
    /// match the identifier pattern, so one answer fell through to the prose
    /// path and the two were reported as disagreeing. A matcher that
    /// manufactures disagreement is worse than no matcher, because disagreement
    /// is the signal this whole tool produces.
    /// </summary>
    /// <param name="text">Raw answer text straight off the wire.</param>
    /// <returns>The same text with dash and quote variants folded to ASCII.</returns>
    private static string NormalizeUnicode( string text )
    {
        var builder = new StringBuilder( text.Length );

        foreach( var character in text )
        {
            builder.Append( character switch
            {
                '‐' or '‑' or '‒' or '–' or '—' or '―'
                    or '−' or '﹘' or '﹣' or '－' => '-',
                '‘' or '’' or '‛' or 'ʼ' => '\'',
                '“' or '”' or '‟' => '"',
                ' ' or ' ' or ' ' or ' ' => ' ',
                _ => character
            } );
        }

        return builder.ToString();
    }

    /// <summary>
    /// Detects first-person refusals and inability statements. Anchored on "I"
    /// so a legitimate answer such as "the form cannot be used for pensions" is
    /// not mistaken for a refusal.
    /// </summary>
    /// <param name="text">Answer text after Unicode folding.</param>
    /// <returns>True when the answer declines rather than answers.</returns>
    private static bool IsRefusal( string text )
    {
        return RefusalPattern().IsMatch( text );
    }

    /// <summary>First-person inability or refusal phrasing, plus the stock "as an AI" disclaimer.</summary>
    [GeneratedRegex(
        @"\bI\s*(?:'m|am)?\s*(?:do not|don't|dont|cannot|can't|cant|am unable to|unable to|not able to|have no)\s+" +
        @"(?:have\s+)?(?:access|provide|browse|verify|confirm|know|retrieve|look up|real-time|current)|\bas an AI\b",
        RegexOptions.IgnoreCase )]
    private static partial Regex RefusalPattern();

    /// <summary>Matches identifier-style tokens: one to four letters, optional separator, then digits.</summary>
    [GeneratedRegex( @"\b[A-Za-z]{1,4}[\s\-]?\d{1,6}\b" )]
    private static partial Regex IdentifierPattern();

    /// <summary>Matches word characters for prose normalization.</summary>
    [GeneratedRegex( @"[a-z0-9]+" )]
    private static partial Regex WordPattern();

    /// <summary>Matches the first signed decimal number in a string.</summary>
    [GeneratedRegex( @"-?\d+(\.\d+)?" )]
    private static partial Regex NumberPattern();

    #endregion Private Methods
}
