using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Fuzz;

/// <summary>
/// Random tables written as correct csv — every quoting, delimiter, line ending and encoding the
/// format allows — read back exactly; and plain tables have their dialect found.
/// </summary>
public sealed class CsvRoundTripFuzzTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly string[] Pieces =
        ["a", "Z", "7", "0", " ", "ä", "ß", "€", "😀", ";", ",", "\t", "|", "\"", "\n", "\r\n", "\r", "k.A.", "-1,5", "2024-01-15"];

    private static readonly string[] PlainPieces = ["a", "Z", "7", " ", "ä", "ß", "€", "x1", "Köln", "2024"];

    [Fact]
    public void ReadsBackEveryTableItWrote()
    {
        foreach ((int seed, Random random) in FuzzCases.Generate(300))
        {
            char delimiter = random.Pick(';', ',', '\t', '|');
            string lineEnd = random.Pick("\n", "\r\n");
            Encoding encoding = random.Pick<Encoding>(new UTF8Encoding(false), new UTF8Encoding(true), new UnicodeEncoding(false, true));
            string[][] table = OutsideTheStrayQuoteRule(Table(random, Pieces), delimiter);
            byte[] file = Write(table, delimiter, lineEnd, encoding, random, quoteAlways: random.Chance(0.2));

            CsvDialect dialect = new()
            {
                Encoding = encoding,
                EncodingSource = DialectSource.Specified,
                Delimiter = delimiter,
                DelimiterSource = DialectSource.Specified,
                Quote = '"',
            };

            FuzzCases.Keep(seed, ".csv", file);
            using CsvCursor cursor = new(new MemoryStream(file), "fuzz.csv", new CsvCursorOptions { Dialect = dialect });
            AssertReadsAs(table, cursor, seed);
            Assert.True(cursor.Diagnostics.IsClean, $"seed {seed}: a correct file needed repairs");
        }
    }

    [Fact]
    public void FindsTheDialectOfPlainTables()
    {
        foreach ((int seed, Random random) in FuzzCases.Generate(200))
        {
            char delimiter = random.Pick(';', ',', '\t', '|');
            Encoding encoding = random.Pick<Encoding>(new UTF8Encoding(false), new UTF8Encoding(true), new UnicodeEncoding(false, true));
            string[][] table = Table(random, PlainPieces, minColumns: 2, minRows: 3);
            byte[] file = Write(table, delimiter, random.Pick("\n", "\r\n"), encoding, random, quoteAlways: false);

            FuzzCases.Keep(seed, ".plain.csv", file);
            using CsvCursor cursor = new(new MemoryStream(file), "fuzz.csv");

            Assert.True(delimiter == cursor.Dialect.Delimiter, $"seed {seed}: found '{cursor.Dialect.Delimiter}' for '{delimiter}'");
            Assert.True(encoding.WebName == cursor.Dialect.Encoding.WebName, $"seed {seed}: found {cursor.Dialect.Encoding.WebName} for {encoding.WebName}");
            AssertReadsAs(table, cursor, seed);
        }
    }

    /// <summary>A rectangular table whose every row holds a value somewhere, so none is a blank row.</summary>
    private static string[][] Table(Random random, string[] pieces, int minColumns = 1, int minRows = 1)
    {
        int columns = random.Next(minColumns, 9);
        int rows = random.Next(minRows, 25);
        string[][] table = new string[rows][];

        for (int r = 0; r < rows; r++)
        {
            table[r] = [.. Enumerable.Range(0, columns).Select(_ => Value(random, pieces))];

            if (table[r].All(string.IsNullOrWhiteSpace))
            {
                table[r][random.Next(columns)] = "v";
            }
        }

        return table;
    }

    /// <summary>
    /// Takes the delimiters out of every value the reader deliberately reads as a stray quote: from five
    /// columns on, a value that crosses a line ending and holds a record's worth of delimiters, where the
    /// line it opens on would still fit the record. Such a value is byte for byte a quote that swallowed
    /// the next record (#10, #20), and the reader chooses that reading on purpose.
    /// </summary>
    private static string[][] OutsideTheStrayQuoteRule(string[][] table, char delimiter)
    {
        int columns = table[0].Length;

        foreach (string[] row in table)
        {
            for (int c = 0; columns >= 5 && c < row.Length; c++)
            {
                string value = row[c];
                int lineEnd = value.IndexOfAny(['\r', '\n']);

                if (lineEnd >= 0 && value.Count(ch => ch == delimiter) >= columns - 1
                    && c + value[..lineEnd].Count(ch => ch == delimiter) < columns)
                {
                    row[c] = value.Replace(delimiter, 'x');
                }
            }
        }

        return table;
    }

    private static string Value(Random random, string[] pieces)
    {
        if (random.Chance(0.15))
        {
            return string.Empty;
        }

        StringBuilder value = new();

        for (int i = random.Next(1, 7); i > 0; i--)
        {
            value.Append(random.Pick(pieces));
        }

        return value.ToString();
    }

    private static byte[] Write(string[][] table, char delimiter, string lineEnd, Encoding encoding, Random random, bool quoteAlways)
    {
        StringBuilder csv = new();

        for (int r = 0; r < table.Length; r++)
        {
            csv.Append(string.Join(delimiter, table[r].Select(v => Field(v, delimiter, quoteAlways || random.Chance(0.1)))));

            if (r < table.Length - 1 || random.Chance(0.5))
            {
                csv.Append(lineEnd);
            }
        }

        return [.. encoding.GetPreamble(), .. encoding.GetBytes(csv.ToString())];
    }

    private static string Field(string value, char delimiter, bool quote)
    {
        bool needs = value.Contains(delimiter, StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal)
            || value.Contains('\r', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal);

        return needs || quote ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
    }

    private static void AssertReadsAs(string[][] table, CsvCursor cursor, int seed)
    {
        for (int r = 0; r < table.Length; r++)
        {
            Assert.True(cursor.ReadRow(Token), $"seed {seed}: row {r + 1} of {table.Length} is missing");

            RawCell[] read = cursor.CurrentRow.ToArray();

            for (int c = 0; c < Math.Max(read.Length, table[r].Length); c++)
            {
                RawCell expected = c < table[r].Length ? RawCell.FromText(table[r][c]) : RawCell.Empty;
                RawCell actual = c < read.Length ? read[c] : RawCell.Empty;

                Assert.True(expected.Equals(actual), $"seed {seed}: row {r + 1}, column {c + 1}: expected [{Show(expected)}], read [{Show(actual)}]");
            }
        }

        Assert.False(cursor.ReadRow(Token), $"seed {seed}: a row more than was written");
    }

    private static string Show(RawCell cell) =>
        (cell.AsText() ?? "(empty)").Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
}
