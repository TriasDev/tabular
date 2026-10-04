using System.Globalization;

namespace TriasDev.Tabular.WriteComparison;

/// <summary>
/// Reads a written file back with <see cref="TabularFile"/> and checks it against the dataset: the row
/// count, the header, and the cells of rows 1, 2, the middle one and the last. Not timed.
/// </summary>
/// <remarks>
/// A csv (or a zip of csv) holds text, so its cells are parsed back to the dataset's types and compared
/// by value; a workbook's cells are typed, so they are compared as typed. Either way an output that
/// does not read back as the dataset is a failure, never a time.
/// </remarks>
internal static class Verifier
{
    /// <summary>
    /// Rows (1-based, after the header) that are always checked: the first two, and the first of each row that
    /// the narrow dataset gives text needing quotes (row % 7 == 3: a comma; row % 11 == 5: a quote; countries 10
    /// and 11, at rows 10 and 11: a delimiter, a quote), taking the row after the data row's index.
    /// </summary>
    private static readonly long[] SampledRows = [1, 2, 4, 6, 11, 12];

    /// <summary>Null when the file holds the scenario's data; otherwise what is wrong.</summary>
    public static string? Check(Scenario scenario, string path)
    {
        long expectedRows = scenario.Rows + 1;
        long[] sample = [.. SampledRows.Where(r => r >= 1 && r <= scenario.Rows).Concat([(scenario.Rows / 2) + 1, scenario.Rows]).Distinct()];
        bool textual = scenario.Kind is FileKind.Csv or FileKind.Zip;
        DataColumn[] columns = scenario.Dataset.Columns;
        Datum[] expected = new Datum[columns.Length];

        using FileStream stream = File.OpenRead(path);
        using ITabularCursor cursor = TabularFile.Open(stream, Path.GetFileName(path));

        if (!cursor.MoveToSheet(0))
        {
            return "the file has no sheet";
        }

        long rows = 0;
        string? problem = null;

        while (problem is null && cursor.ReadRow())
        {
            if (rows == 0)
            {
                problem = CheckHeader(cursor.CurrentRow, columns);
            }
            else if (sample.Contains(rows))
            {
                problem = CheckRow(cursor.CurrentRow, scenario, rows - 1, expected, textual);
            }

            rows++;
        }

        if (problem is not null)
        {
            return problem;
        }

        if (rows != expectedRows)
        {
            return $"read back {rows:N0} rows, expected {expectedRows:N0}";
        }

        return StyleVerifier.Check(scenario, path);
    }

    private static string? CheckHeader(ReadOnlySpan<RawCell> row, DataColumn[] columns)
    {
        for (int c = 0; c < columns.Length; c++)
        {
            string? text = c < row.Length ? row[c].AsText() : null;

            if (text != columns[c].Header)
            {
                return $"header column {c}: expected \"{columns[c].Header}\", read \"{text}\"";
            }
        }

        return null;
    }

    private static string? CheckRow(ReadOnlySpan<RawCell> row, Scenario scenario, long dataRow, Datum[] expected, bool textual)
    {
        scenario.Dataset.Fill(dataRow, expected);
        DataColumn[] columns = scenario.Dataset.Columns;

        for (int c = 0; c < columns.Length; c++)
        {
            RawCell cell = c < row.Length ? row[c] : RawCell.Empty;

            if (!(textual ? MatchesText(in expected[c], cell) : MatchesTyped(in expected[c], cell)))
            {
                return $"row {dataRow} column {c} ({columns[c].Header}): expected {Describe(in expected[c])}, read {cell.Kind} \"{cell.AsText()}\"";
            }
        }

        return null;
    }

    /// <summary>Bit-for-bit: what a writer wrote must read back as the very double that went in.</summary>
    private static bool Same(double read, double written) => BitConverter.DoubleToInt64Bits(read) == BitConverter.DoubleToInt64Bits(written);

    private static string Describe(in Datum value) => value.Kind switch
    {
        ValueKind.Text => $"\"{value.Text}\"",
        ValueKind.Long => value.Long.ToString(CultureInfo.InvariantCulture),
        ValueKind.Double => value.Double.ToString("R", CultureInfo.InvariantCulture),
        ValueKind.Decimal => value.Decimal.ToString(CultureInfo.InvariantCulture),
        ValueKind.Date or ValueKind.DateTime => value.Date.ToString("O", CultureInfo.InvariantCulture),
        ValueKind.Boolean => value.Boolean ? "true" : "false",
        _ => "empty",
    };

    private static bool MatchesText(in Datum value, RawCell cell)
    {
        if (value.Kind == ValueKind.Empty)
        {
            return cell.IsEmpty;
        }

        string? text = cell.AsText();

        if (text is null)
        {
            return false;
        }

        return value.Kind switch
        {
            ValueKind.Text => text == value.Text,
            ValueKind.Long => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l) && l == value.Long,
            ValueKind.Double => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && Same(d, value.Double),
            ValueKind.Decimal => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal m) && m == value.Decimal,
            ValueKind.Date => DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime day) && day.Date == value.Date.Date,
            ValueKind.DateTime => DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime at) && Math.Abs((at - value.Date).TotalSeconds) < 1,
            ValueKind.Boolean => bool.TryParse(text, out bool b) && b == value.Boolean,
            _ => false,
        };
    }

    private static bool MatchesTyped(in Datum value, RawCell cell) => value.Kind switch
    {
        ValueKind.Empty => cell.IsEmpty,
        ValueKind.Text => cell.Kind == RawCellKind.Text && cell.Text == value.Text,
        ValueKind.Long => cell.Kind == RawCellKind.Number && Same(cell.Number, value.Long),
        ValueKind.Double => cell.Kind == RawCellKind.Number && Same(cell.Number, value.Double),
        ValueKind.Decimal => cell.Kind == RawCellKind.Number && Math.Abs(cell.Number - (double)value.Decimal) < 1e-6,
        ValueKind.Date => cell.Kind == RawCellKind.Date && cell.Date == value.Date,
        ValueKind.DateTime => cell.Kind == RawCellKind.Date && Math.Abs((cell.Date - value.Date).TotalMilliseconds) < 1,
        ValueKind.Boolean => cell.Kind == RawCellKind.Boolean && cell.Boolean == value.Boolean,
        _ => false,
    };
}
