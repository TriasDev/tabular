using System.Globalization;

using TriasDev.Tabular.Abstractions;
using TriasDev.Tabular.Analysis;

namespace TriasDev.Tabular.Mapping;

/// <summary>
/// Turns a cell and its text into the value a field would hold, by the one rule both halves use.
/// </summary>
/// <remarks>
/// <para>
/// Shared because the precheck and the extractor were judging different strings. A constraint is
/// applied to the value the import <em>produces</em>, and for anything but text that is a
/// re-rendering rather than the file's own characters: a date written <c>1/2/2024</c> is eight
/// characters in the file and ten as <c>yyyy-MM-dd</c>, and <c>007</c> read as a whole number becomes
/// <c>7</c>. So a pattern, a length rule or an allowed-value set judged against the file's text
/// answers a question nobody asked.
/// </para>
/// <para>
/// The precheck runs this over a column's distinct values, once each; the extractor runs it over
/// every row. Same rule, different frequency — which is the whole reason a precheck can be honest
/// without being expensive.
/// </para>
/// </remarks>
internal static class ValueReading
{
    /// <summary>
    /// Reads a value as the target field's type.
    /// </summary>
    /// <remarks>
    /// A cell the file already typed is taken at its word rather than rendered to text and parsed
    /// back: a workbook's date is a date, and running it through a culture would only introduce a way
    /// for it to come out wrong.
    /// </remarks>
    public static bool TryRead(
        in RawCell cell,
        string text,
        ColumnType type,
        CultureInfo culture,
        out MappedValue value)
    {
        switch (type)
        {
            case ColumnType.Text:
                value = MappedValue.FromText(text);
                return true;

            case ColumnType.Integer:
                // The range check is not decoration: `(long)1e30` does not throw, it saturates, and
                // the row would then be written out carrying long.MaxValue as though the file had
                // said so.
                if (cell.Kind == RawCellKind.Number
                    && double.IsInteger(cell.Number)
                    && cell.Number >= long.MinValue
                    && cell.Number <= long.MaxValue)
                {
                    value = MappedValue.FromInteger((long)cell.Number);
                    return true;
                }

                if (long.TryParse(text, NumberStyles.Integer | NumberStyles.AllowThousands, culture, out long whole))
                {
                    value = MappedValue.FromInteger(whole);
                    return true;
                }

                break;

            case ColumnType.Decimal:
                // A value the file declares as a number can still be outside decimal's range, or not
                // a number at all. The profiler already guards this cast; the extractor did not, so
                // analysis survived such a file and extraction died on it.
                if (cell.Kind == RawCellKind.Number)
                {
                    if (!TryToDecimal(cell.Number, out decimal declared))
                    {
                        break;
                    }

                    value = MappedValue.FromDecimal(declared);
                    return true;
                }

                if (decimal.TryParse(text, NumberStyles.Number, culture, out decimal fraction))
                {
                    value = MappedValue.FromDecimal(fraction);
                    return true;
                }

                break;

            case ColumnType.Date:
                if (cell.Kind == RawCellKind.Date)
                {
                    value = MappedValue.FromDate(cell.Date);
                    return true;
                }

                // A value has to name a date completely. The parser fills in whatever the text
                // leaves out from the clock — a bare time becomes today, a day and month become this
                // year — so a row would import carrying a date the file never contained, and one that
                // depends on when the import ran. The profiler applies the same rule.
                if (DateReading.TryRead(text, culture, out DateTime date))
                {
                    value = MappedValue.FromDate(date);
                    return true;
                }

                break;

            case ColumnType.Boolean:
                if (cell.Kind == RawCellKind.Boolean)
                {
                    value = MappedValue.FromBoolean(cell.Boolean);
                    return true;
                }

                if (bool.TryParse(text, out bool flag))
                {
                    value = MappedValue.FromBoolean(flag);
                    return true;
                }

                break;
        }

        value = MappedValue.Absent;
        return false;
    }

    /// <summary>Converts a file-supplied number, or reports that it will not fit.</summary>
    private static bool TryToDecimal(double value, out decimal converted)
    {
        converted = 0;

        if (!double.IsFinite(value) || value < (double)decimal.MinValue || value > (double)decimal.MaxValue)
        {
            return false;
        }

        try
        {
            converted = (decimal)value;
            return true;
        }
        catch (OverflowException)
        {
            // The comparisons above use the nearest double to decimal's limits, which is not exactly
            // that limit. The edge between them belongs to the cast.
            return false;
        }
    }
}
