using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace LLMQuorum.Core.Sweep;

/// <summary>Cell styles the sheet uses. Values are indexes into the stylesheet's cell formats.</summary>
public enum XStyle : uint
{
    Normal = 0,
    Bold = 1,
    Header = 2,
    Wrap = 3,
    Correct = 4,
    Truncated = 5,
    Refusal = 6,
    Wrong = 7,
    Error = 8,
    Title = 9,
    Outdated = 10
}

/// <summary>One cell: a string, a number, or empty.</summary>
/// <param name="Value">string, int, long, double, decimal, or null.</param>
/// <param name="Style">Cell style.</param>
public sealed record XCell( object? Value, XStyle Style = XStyle.Normal );

/// <summary>
/// Writes a single-worksheet .xlsx with inline strings and a fixed stylesheet. Kept to the
/// Open XML SDK (Microsoft, MIT) so no third-party spreadsheet licence comes along.
/// </summary>
public static class XlsxWriter
{
    #region Public Methods

    /// <summary>Writes the workbook.</summary>
    /// <param name="path">Output .xlsx path; overwritten.</param>
    /// <param name="sheetName">Worksheet tab name.</param>
    /// <param name="rows">Rows top to bottom; null entries are blank rows.</param>
    /// <param name="columnWidths">Width per column, in characters.</param>
    /// <param name="autoFilterRef">Filter range such as "A14:M72", or null.</param>
    public static void Write( string path, string sheetName, IReadOnlyList<IReadOnlyList<XCell>?> rows,
                              IReadOnlyList<double> columnWidths, string? autoFilterRef )
    {
        using var document = SpreadsheetDocument.Create( path, SpreadsheetDocumentType.Workbook );
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();

        var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = BuildStylesheet();

        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var worksheet = new Worksheet();
        worksheet.Append( BuildColumns( columnWidths ) );
        worksheet.Append( BuildSheetData( rows ) );

        if( autoFilterRef is not null )
        {
            worksheet.Append( new AutoFilter { Reference = autoFilterRef } );
        }

        worksheetPart.Worksheet = worksheet;

        var sheets = workbookPart.Workbook.AppendChild( new Sheets() );
        sheets.Append( new Sheet { Id = workbookPart.GetIdOfPart( worksheetPart ), SheetId = 1, Name = sheetName } );

        if( autoFilterRef is not null )
        {
            // Excel expects the hidden filter name to exist alongside an AutoFilter.
            workbookPart.Workbook.Append( new DefinedNames( new DefinedName( $"'{sheetName}'!{AbsoluteRef( autoFilterRef )}" )
            {
                Name = "_xlnm._FilterDatabase", LocalSheetId = 0, Hidden = true
            } ) );
        }

        workbookPart.Workbook.Save();
    }

    /// <summary>Spreadsheet column letters for a zero-based index (0 = A, 26 = AA).</summary>
    /// <param name="index">Zero-based column index.</param>
    /// <returns>Column letters.</returns>
    public static string ColumnName( int index )
    {
        var name = string.Empty;

        for( var n = index + 1; n > 0; n = ( n - 1 ) / 26 )
        {
            name = (char)( 'A' + ( n - 1 ) % 26 ) + name;
        }

        return name;
    }

    #endregion Public Methods

    #region Private Methods

    private static Columns BuildColumns( IReadOnlyList<double> widths )
    {
        var columns = new Columns();

        for( var i = 0; i < widths.Count; i++ )
        {
            columns.Append( new Column { Min = (uint)( i + 1 ), Max = (uint)( i + 1 ), Width = widths[i], CustomWidth = true } );
        }

        return columns;
    }

    private static SheetData BuildSheetData( IReadOnlyList<IReadOnlyList<XCell>?> rows )
    {
        var data = new SheetData();

        for( var r = 0; r < rows.Count; r++ )
        {
            var row = new Row { RowIndex = (uint)( r + 1 ) };
            var cells = rows[r] ?? Array.Empty<XCell>();

            for( var c = 0; c < cells.Count; c++ )
            {
                row.Append( BuildCell( cells[c], $"{ColumnName( c )}{r + 1}" ) );
            }

            data.Append( row );
        }

        return data;
    }

    private static Cell BuildCell( XCell source, string reference )
    {
        var cell = new Cell { CellReference = reference, StyleIndex = (uint)source.Style };

        switch( source.Value )
        {
            case null:
                break;
            case int or long or double or decimal:
                cell.DataType = CellValues.Number;
                cell.CellValue = new CellValue( Convert.ToString( source.Value, CultureInfo.InvariantCulture )! );
                break;
            default:
                cell.DataType = CellValues.InlineString;
                cell.InlineString = new InlineString( new Text( CleanText( source.Value.ToString()! ) ) { Space = SpaceProcessingModeValues.Preserve } );
                break;
        }

        return cell;
    }

    /// <summary>
    /// Drops characters XML 1.0 forbids (including unpaired surrogates) and clips to Excel's per-cell
    /// limit without splitting a surrogate pair. MEASURED by review: a clip landing inside an emoji
    /// left a lone high surrogate and the workbook failed to save.
    /// </summary>
    private static string CleanText( string text )
    {
        const int LIMIT = 32000;
        var source = text.Replace( "\r\n", "\n" );
        var builder = new StringBuilder( Math.Min( source.Length, LIMIT ) );

        for( var i = 0; i < source.Length && builder.Length < LIMIT; i++ )
        {
            var ch = source[i];

            if( char.IsHighSurrogate( ch ) )
            {
                if( i + 1 < source.Length && char.IsLowSurrogate( source[i + 1] ) && builder.Length + 2 <= LIMIT )
                {
                    builder.Append( ch ).Append( source[++i] );
                }

                continue;
            }

            if( ch is '\t' or '\n' || ( ch >= 0x20 && !char.IsLowSurrogate( ch ) && ch != 0xFFFE && ch != 0xFFFF ) )
            {
                builder.Append( ch );
            }
        }

        return builder.ToString();
    }

    private static string AbsoluteRef( string range ) =>
        string.Join( ":", range.Split( ':' ).Select( part =>
        {
            var letters = new string( part.TakeWhile( char.IsLetter ).ToArray() );
            return $"${letters}${part[letters.Length..]}";
        } ) );

    /// <summary>Fonts: normal, bold, bold 14. Fills: none, gray125 (both required), then grade colours.</summary>
    private static Stylesheet BuildStylesheet()
    {
        var fonts = new Fonts(
            new Font( new FontSize { Val = 11 }, new FontName { Val = "Calibri" } ),
            new Font( new Bold(), new FontSize { Val = 11 }, new FontName { Val = "Calibri" } ),
            new Font( new Bold(), new FontSize { Val = 14 }, new FontName { Val = "Calibri" } ) );

        var fills = new Fills(
            new Fill( new PatternFill { PatternType = PatternValues.None } ),
            new Fill( new PatternFill { PatternType = PatternValues.Gray125 } ),
            SolidFill( "FFD9D9D9" ),   // 2 header
            SolidFill( "FFC6EFCE" ),   // 3 correct
            SolidFill( "FFFFEB9C" ),   // 4 truncated
            SolidFill( "FFDDEBF7" ),   // 5 refusal
            SolidFill( "FFFFC7CE" ),   // 6 wrong
            SolidFill( "FFE7E6E6" ),   // 7 error
            SolidFill( "FFF8CBAD" ) ); // 8 outdated

        var formats = new CellFormats(
            Format( 0, 0, false ),     // Normal
            Format( 1, 0, false ),     // Bold
            Format( 1, 2, true ),      // Header
            Format( 0, 0, true ),      // Wrap
            Format( 1, 3, true ),      // Correct
            Format( 1, 4, true ),      // Truncated
            Format( 1, 5, true ),      // Refusal
            Format( 1, 6, true ),      // Wrong
            Format( 1, 7, true ),      // Error
            Format( 2, 0, false ),     // Title
            Format( 1, 8, true ) );    // Outdated

        return new Stylesheet( fonts, fills, new Borders( new Border() ), new CellStyleFormats( new CellFormat() ), formats );
    }

    private static Fill SolidFill( string argb ) =>
        new( new PatternFill( new ForegroundColor { Rgb = argb } ) { PatternType = PatternValues.Solid } );

    private static CellFormat Format( uint fontId, uint fillId, bool wrap ) =>
        new( new Alignment { WrapText = wrap, Vertical = VerticalAlignmentValues.Top } )
        {
            FontId = fontId, FillId = fillId, BorderId = 0, ApplyFont = fontId != 0, ApplyFill = fillId != 0, ApplyAlignment = true
        };

    #endregion Private Methods
}
