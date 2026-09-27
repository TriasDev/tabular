using System.Globalization;
using System.Text;

using Xunit;

namespace TriasDev.Tabular.Tests.Fuzz;

/// <summary>What a generated cell holds, before any format has written it.</summary>
internal enum FuzzKind
{
    Empty,
    Text,
    Number,
    Boolean,
    Date,
    Error,
}

/// <summary>A generated cell: its value, and the text a writer puts in the file for a number.</summary>
internal sealed record FuzzCell(FuzzKind Kind, string Text = "", double Number = 0, bool Boolean = false, DateTime Date = default)
{
    public RawCell Expected => Kind switch
    {
        FuzzKind.Text => RawCell.FromText(Text),
        FuzzKind.Number => RawCell.FromNumber(Number),
        FuzzKind.Boolean => RawCell.FromBoolean(Boolean),
        FuzzKind.Date => RawCell.FromDate(Date),
        FuzzKind.Error => RawCell.FromError(Text),
        _ => RawCell.Empty,
    };
}

/// <summary>A generated row, numbered as the sheet places it.</summary>
internal sealed record FuzzRow(int Number, FuzzCell[] Cells)
{
    public bool IsBlank => Cells.All(c => c.Expected.IsEmpty);
}

internal sealed record FuzzSheet(string Name, List<FuzzRow> Rows);

/// <summary>
/// Random workbooks as values, for the spreadsheet fuzz tests to write in their own format and read back.
/// </summary>
internal static class FuzzSheets
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly string[] TextPieces =
        ["a", "Z", "7", " ", "  ", "ä", "ß", "€", "😀", "<", ">", "&", "\"", "'", "\n", "\t", "\r", "]]>", "k.A.", "2024-01-15", "-1,5"];

    private static readonly string[] NamePieces = ["S", "x", "ä", "&", "<", "'", "\"", "1", " "];

    public static readonly string[] Errors = ["#N/A", "#DIV/0!", "#REF!", "#VALUE!", "#NAME?"];

    public static List<FuzzSheet> Workbook(Random random)
    {
        List<FuzzSheet> sheets = [];
        HashSet<string> names = [];

        for (int s = random.Next(1, 4); s > 0; s--)
        {
            string name;

            do
            {
                name = string.Concat(Enumerable.Range(0, random.Next(1, 6)).Select(_ => random.Pick(NamePieces))).Trim();
            }
            while (name.Length == 0 || !names.Add(name));

            sheets.Add(new FuzzSheet(name, Rows(random)));
        }

        return sheets;
    }

    private static List<FuzzRow> Rows(Random random)
    {
        List<FuzzRow> rows = [];
        int columns = random.Next(1, 9);
        int number = 0;

        for (int r = random.Chance(0.05) ? 0 : random.Next(1, 20); r > 0; r--)
        {
            number += random.Chance(0.2) ? random.Next(2, 6) : 1;

            // A repeat of the row above, which a writer may fold into one element.
            if (rows.Count > 0 && rows[^1].Number == number - 1 && random.Chance(0.1))
            {
                rows.Add(rows[^1] with { Number = number });
                continue;
            }

            rows.Add(new FuzzRow(number, [.. Enumerable.Range(0, columns).Select(_ => Cell(random))]));
        }

        return rows;
    }

    private static FuzzCell Cell(Random random)
    {
        double roll = random.NextDouble();

        return roll switch
        {
            < 0.2 => new FuzzCell(FuzzKind.Empty),
            < 0.6 => new FuzzCell(FuzzKind.Text, string.Concat(Enumerable.Range(0, random.Next(1, 7)).Select(_ => random.Pick(TextPieces)))),
            < 0.8 => new FuzzCell(FuzzKind.Number, Number: NumberValue(random)),
            < 0.87 => new FuzzCell(FuzzKind.Boolean, Boolean: random.Chance(0.5)),
            < 0.96 => new FuzzCell(FuzzKind.Date, Date: new DateTime(1900, 3, 1, 0, 0, 0, DateTimeKind.Unspecified).AddDays(random.Next(0, 80_000)).AddSeconds(random.Chance(0.5) ? 0 : random.Next(0, 86_400))),
            _ => new FuzzCell(FuzzKind.Error, random.Pick(Errors)),
        };
    }

    private static double NumberValue(Random random) => random.Next(5) switch
    {
        0 => random.Next(-1_000_000, 1_000_000),
        1 => Math.Round((random.NextDouble() * 2000) - 1000, random.Next(0, 6)),
        2 => random.NextDouble() * 1e20,
        3 => random.NextDouble() * 1e-7,
        _ => (random.NextDouble() - 0.5) * double.MaxValue,
    };

    public static string Invariant(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// Text as XML character data, each character spelt one of the ways XML allows — literal, as an
    /// entity or as a character reference — and now and then a run in a CDATA section.
    /// </summary>
    public static string XmlText(string text, Random random)
    {
        StringBuilder xml = new();
        int i = 0;

        while (i < text.Length)
        {
            if (random.Chance(0.05) && Cdata(text, i, random) is { } run)
            {
                xml.Append("<![CDATA[").Append(run).Append("]]>");
                i += run.Length;
                continue;
            }

            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length)
            {
                string pair = text.Substring(i, 2);
                xml.Append(random.Chance(0.3) ? $"&#x{char.ConvertToUtf32(pair, 0):X};" : pair);
                i += 2;
                continue;
            }

            // "]]>" is the one sequence character data may not hold as written.
            xml.Append(text[i] == '>' && i > 0 && text[i - 1] == ']' ? "&gt;" : XmlChar(text[i], random));
            i++;
        }

        return xml.ToString();
    }

    /// <summary>A run from <paramref name="start"/> that a CDATA section can hold as written, or null.</summary>
    private static string? Cdata(string text, int start, Random random)
    {
        string run = text.Substring(start, random.Next(1, text.Length - start + 1));

        return run.Contains("]]>", StringComparison.Ordinal) || run.Contains('\r', StringComparison.Ordinal)
            || char.IsLowSurrogate(run[0]) || char.IsHighSurrogate(run[^1])
                ? null
                : run;
    }

    private static string XmlChar(char c, Random random) => c switch
    {
        '&' => random.Pick("&amp;", "&#38;"),
        '<' => random.Pick("&lt;", "&#x3C;"),
        '>' => random.Pick(">", "&gt;"),
        '"' => random.Pick("\"", "&quot;"),
        '\'' => random.Pick("'", "&apos;"),
        '\r' => random.Pick("&#13;", "&#xD;"),
        '\n' => random.Pick("\n", "&#10;"),
        '\t' => random.Pick("\t", "&#9;"),
        _ when random.Chance(0.05) => $"&#{(int)c};",
        _ => c.ToString(),
    };

    /// <summary>Text as an XML attribute value, where line ends and tabs must be references to survive.</summary>
    public static string XmlAttribute(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal).Replace("\r", "&#13;", StringComparison.Ordinal)
            .Replace("\n", "&#10;", StringComparison.Ordinal).Replace("\t", "&#9;", StringComparison.Ordinal);

    /// <summary>Reads every sheet of the workbook and compares it, row number and cell by cell, with what was written.</summary>
    public static void AssertReadsAs(List<FuzzSheet> workbook, ITabularCursor cursor, int seed, bool blankRowsAreRead)
    {
        Assert.True(workbook.Count == cursor.Sheets.Count, $"seed {seed}: {cursor.Sheets.Count} sheets for {workbook.Count}");

        for (int s = 0; s < workbook.Count; s++)
        {
            Assert.True(workbook[s].Name == cursor.Sheets[s].Name, $"seed {seed}: sheet {s + 1} read as [{cursor.Sheets[s].Name}] for [{workbook[s].Name}]");
            Assert.True(cursor.MoveToSheet(s, Token), $"seed {seed}: cannot move to sheet {s + 1}");

            foreach (FuzzRow row in workbook[s].Rows.Where(r => blankRowsAreRead || !r.IsBlank))
            {
                string where = $"seed {seed}: sheet {s + 1}, row {row.Number}";
                Assert.True(cursor.ReadRow(Token), $"{where} is missing");
                Assert.True(row.Number == cursor.CurrentRowNumber, $"{where} read as row {cursor.CurrentRowNumber}");

                RawCell[] read = cursor.CurrentRow.ToArray();

                for (int c = 0; c < Math.Max(read.Length, row.Cells.Length); c++)
                {
                    RawCell expected = c < row.Cells.Length ? row.Cells[c].Expected : RawCell.Empty;
                    RawCell actual = c < read.Length ? read[c] : RawCell.Empty;

                    Assert.True(expected.Equals(actual), $"{where}, column {c + 1}: expected {Show(expected)}, read {Show(actual)}");
                }
            }

            Assert.False(cursor.ReadRow(Token), $"seed {seed}: sheet {s + 1} has a row more than was written");
        }
    }

    private static string Show(RawCell cell) =>
        $"{cell.Kind} [{(cell.AsText() ?? "(empty)").Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)}]";
}
