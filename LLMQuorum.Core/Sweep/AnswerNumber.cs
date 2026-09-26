using System.Globalization;
using System.Text.RegularExpressions;

namespace LLMQuorum.Core.Sweep;

/// <summary>One acceptable value from a numeric answer key and the precision it was written to.</summary>
/// <param name="Value">The value.</param>
/// <param name="Decimals">Significant decimal places as written (trailing zeros ignored).</param>
public sealed record KeyNumber( decimal Value, int Decimals );

/// <summary>
/// Rule grading for numeric answers. A key lists acceptable values separated by ';' ("67.3077; 67.31").
/// A bare number is correct when, at every precision the key states that the answer is at least as
/// precise as, it rounds to the key's value. So 67.31, 67.3077, 67.308 and 67.30769 are correct;
/// 67.3 is not precise enough; 67.3149 rounds to 67.31 but contradicts 67.3077, so it is wrong.
/// Anything that is not a bare number (work shown, several numbers, prose) goes to judges.
/// </summary>
public static partial class AnswerNumber
{
    #region Public Methods

    /// <summary>Parses a numeric key. Returns an empty list when any part is not a plain number.</summary>
    /// <param name="key">Key text such as "67.3077; 67.31".</param>
    /// <returns>Acceptable values, or empty when the key is not numeric.</returns>
    public static IReadOnlyList<KeyNumber> ParseKey( string key )
    {
        var values = new List<KeyNumber>();

        foreach( var part in key.Split( ';' ).Select( p => p.Trim() ).Where( p => p.Length > 0 ) )
        {
            if( !TryParseBare( part, out var value, out var decimals ) )
            {
                return Array.Empty<KeyNumber>();
            }

            values.Add( new KeyNumber( value, decimals ) );
        }

        return values;
    }

    /// <summary>
    /// Reads an answer that is only a number: optional $, thousands commas, optional "/hr", "per hour",
    /// markdown bold, trailing period. Multi-line answers and anything with other words are rejected.
    /// </summary>
    /// <param name="answer">Answer text.</param>
    /// <param name="value">Parsed value.</param>
    /// <param name="decimals">Significant decimal places as written.</param>
    /// <returns>True when the answer is a bare number.</returns>
    public static bool TryParseBare( string answer, out decimal value, out int decimals )
    {
        value = 0;
        decimals = 0;
        var match = BareNumber().Match( answer.Trim() );

        if( !match.Success )
        {
            return false;
        }

        var digits = match.Groups["n"].Value.Replace( ",", string.Empty );

        if( !decimal.TryParse( digits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value ) )
        {
            return false;
        }

        var dot = digits.IndexOf( '.' );
        decimals = dot < 0 ? 0 : digits[( dot + 1 )..].TrimEnd( '0' ).Length;
        return true;
    }

    /// <summary>True when the value agrees with every key value it is at least as precise as, and there is one.</summary>
    /// <param name="value">Answer value.</param>
    /// <param name="decimals">Answer precision.</param>
    /// <param name="key">Acceptable values.</param>
    /// <returns>True when correct.</returns>
    public static bool Matches( decimal value, int decimals, IReadOnlyList<KeyNumber> key )
    {
        var applicable = key.Where( k => k.Decimals <= decimals ).ToList();
        return applicable.Count > 0
               && applicable.All( k => Math.Round( value, k.Decimals, MidpointRounding.AwayFromZero ) == k.Value );
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// Optional $, the number, then at most one unit: %, ¢, "cents" (optionally "per mile"), "/hr", "per hour",
    /// "an hour", "hourly". Units never change the value; a percentage key is written without the sign.
    /// </summary>
    [GeneratedRegex( @"^[*_\s]*\$?\s*(?<n>\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d+(?:\.\d+)?)\s*(?:%|¢|cents?(?:\s+(?:per|a|/)\s*mile)?|(?:/|per|an|a)\s*(?:hr|hour|h|mile)\b\.?|hourly)?[*_\s]*\.?[*_\s]*$",
                     RegexOptions.IgnoreCase )]
    private static partial Regex BareNumber();

    #endregion Private Methods
}
