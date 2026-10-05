using System.Globalization;

using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Tests.Fuzz;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>
/// Random workbooks, seeded and so repeatable, written as xlsx and ods through <see cref="TabularWriter"/>:
/// every value the writer accepts, the import reads back as written, on the sheet it was written to,
/// under the name it was given — or the writer refused it with a code. Nothing in between.
/// </summary>
/// <remarks>
/// Each case draws its sheets (names with every character XML must escape), columns of every value
/// type, styles with number and date formats applied to any cell, a header style, frozen panes and
/// filters. The documented exceptions are the import's own: text comes back trimmed, and empty or
/// whitespace-only text comes back as no value (a row of nothing is skipped). The cases are
/// <see cref="FuzzCases"/>' — <c>TABULAR_FUZZ_CASES</c> for more, <c>TABULAR_FUZZ_SEED</c> to replay one,
/// <c>TABULAR_FUZZ_DUMP</c> to keep its file.
/// </remarks>
public sealed class WorkbookWriteFuzzTests
{
    /// <summary>Few by default: each case writes a whole workbook, and every row alone before it.</summary>
    private const int DefaultCases = 40;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Text pieces: letters, digits, runs of spaces and tabs (markup in ods), line breaks of every kind,
    /// what XML escapes, what xlsx escapes itself (<c>_x0041_</c>), CDATA's end, umlauts, emoji — and a
    /// control character and a lone surrogate, which no XML can carry and the writer must refuse.
    /// </summary>
    private static readonly string[] Alphabet =
    [
        "a", "b", "Z", "x", "0", "7", " ", " ", "  ", "   ", "\t", "\n", "\r\n", "\r", "&", "<", ">", "\"", "'", "]]>",
        "_x0041_", "_x", "ä", "Ö", "ß", "👍", "🎉", " ",
    ];

    private static readonly string[] Hostile = ["\u0001", "\ud800", "￿"];

    private static readonly string[] NamePieces = ["a", "B", "7", " ", "&", "<", ">", "\"", "'", "ä", "👍", "-", "_", "."];

    private static readonly string[] NumberCodes = ["0", "0.00", "#,##0.00", "0.0%", "\"EUR \"0", "\"R&D <\"0"];

    private static readonly string[] DateCodes = ["dd.mm.yyyy", "dd/mm/yyyy", "dd.mm.yyyy hh:mm", "mm/dd hh:mm", "yyyy-mm-dd"];

    private enum Kind
    {
        Text,
        Integer,
        Decimal,
        Double,
        Date,
        DateTime,
        DateOnly,
        Boolean,
    }

    private sealed record Sheet(string Name, Kind[] Kinds, SheetOptions Options, List<(object?[] Values, int[] Styles)> Rows);

    [Theory]
    [InlineData(TabularFormat.Xlsx)]
    [InlineData(TabularFormat.Ods)]
    public async Task WhatTheWriterAcceptsTheImportReadsBack(TabularFormat format)
    {
        string extension = format == TabularFormat.Xlsx ? ".xlsx" : ".ods";
        int refusals = 0;

        foreach ((int seed, Random random) in FuzzCases.Generate(DefaultCases))
        {
            CellStyle[] styles = [.. Enumerable.Range(0, random.Next(0, 5)).Select(_ => Style(random))];
            List<Sheet> workbook = [];
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

            for (int s = random.Next(1, 4); s > 0; s--)
            {
                Kind[] kinds = [.. Enumerable.Range(0, random.Next(1, 7)).Select(_ => (Kind)random.Next(8))];
                Sheet sheet = new(DrawName(random, names), kinds, DrawOptions(random, styles, kinds.Length), []);

                for (int r = random.Next(0, 25); r > 0; r--)
                {
                    object?[] values = [.. kinds.Select(kind => Draw(random, kind))];
                    int[] cellStyles = [.. kinds.Select(_ => styles.Length > 0 && random.Chance(0.4) ? random.Next(styles.Length) + 1 : 0)];

                    if (await RefusedAlone(format, styles, sheet, values, cellStyles) is { } code)
                    {
                        Assert.True(code.StartsWith("write.", StringComparison.Ordinal), $"seed {seed}: refused with {code}");
                        refusals++;
                    }
                    else
                    {
                        sheet.Rows.Add((values, cellStyles));
                    }
                }

                workbook.Add(sheet);
            }

            byte[] file = await Write(format, styles, workbook);
            FuzzCases.Keep(seed, extension, file);

            AssertReadsBack(file, workbook, seed, format);
        }

        // Proof the draw reaches the writer's refusals, rather than passing by never meeting them.
        if (Environment.GetEnvironmentVariable("TABULAR_FUZZ_SEED") is null)
        {
            Assert.True(refusals > 0, "no case drew a value the writer refuses");
        }
    }

    private static void AssertReadsBack(byte[] file, List<Sheet> workbook, int seed, TabularFormat format)
    {
        using (ITabularCursor cursor = TabularFile.Open(new MemoryStream(file, writable: false), "file", cancellationToken: Token))
        {
            Assert.True(
                workbook.Select(s => s.Name).SequenceEqual(cursor.Sheets.Select(s => s.Name)),
                $"seed {seed}, {format}: sheets [{string.Join(" | ", workbook.Select(s => s.Name))}] read as [{string.Join(" | ", cursor.Sheets.Select(s => s.Name))}]");
        }

        for (int s = 0; s < workbook.Count; s++)
        {
            Sheet sheet = workbook[s];
            ImportField[] fields = [.. sheet.Kinds.Select((kind, i) => Field(kind, Header(i, kind)))];
            (List<object?[]> read, List<string> errors, int skipped) = CsvRoundTrip.Import(file, "", fields, Token, sheetIndex: s);

            Assert.True(errors.Count == 0, $"seed {seed}, {format}, sheet \"{sheet.Name}\": {string.Join("; ", errors)}");

            List<object?[]> expected = [.. sheet.Rows.Select(row => row.Values.Select(Expected).ToArray()).Where(values => values.Any(v => v is not null))];

            for (int i = 0; i < Math.Min(expected.Count, read.Count); i++)
            {
                Assert.True(
                    expected[i].SequenceEqual(read[i]),
                    $"seed {seed}, {format}, sheet \"{sheet.Name}\", data row {i + 1}: expected [{string.Join(" | ", expected[i].Select(Show))}], read [{string.Join(" | ", read[i].Select(Show))}]");
            }

            Assert.True(expected.Count == read.Count, $"seed {seed}, {format}, sheet \"{sheet.Name}\": {expected.Count} rows written, {read.Count} read");
            // A row of nothing is read and skipped from xlsx; the ods reader hands out no empty row at
            // all — LibreOffice pads a sheet with repeated empty rows — so none is counted there.
            int blank = sheet.Rows.Count - expected.Count;
            Assert.True(format == TabularFormat.Ods ? skipped == 0 : skipped == blank, $"seed {seed}, {format}, sheet \"{sheet.Name}\": {blank} rows of nothing written, {skipped} skipped");
        }
    }

    private static async Task<byte[]> Write(TabularFormat format, CellStyle[] styles, List<Sheet> workbook)
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, format))
        {
            StyleId[] ids = Register(writer, styles);

            foreach (Sheet sheet in workbook)
            {
                writer.BeginSheet(sheet.Name, Columns(sheet.Kinds), sheet.Options);

                foreach ((object?[] values, int[] cellStyles) in sheet.Rows)
                {
                    WriteRow(writer, ids, values, cellStyles);
                }
            }

            await writer.CompleteAsync(Token);
        }

        return target.ToArray();
    }

    /// <summary>
    /// Writes the row alone into a sheet of its own: the code the writer refused it with, or null. A refused
    /// value faults its writer, so it is found here and left out of the workbook; the writer is never
    /// completed, as a refusal comes at the call that writes the value.
    /// </summary>
    private static async Task<string?> RefusedAlone(TabularFormat format, CellStyle[] styles, Sheet sheet, object?[] values, int[] cellStyles)
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), format);
        StyleId[] ids = Register(writer, styles);
        writer.BeginSheet(sheet.Name, Columns(sheet.Kinds), sheet.Options);

        try
        {
            WriteRow(writer, ids, values, cellStyles);
            return null;
        }
        catch (TabularWriteException refused)
        {
            return refused.Code;
        }
    }

    private static StyleId[] Register(TabularWriter writer, CellStyle[] styles) => [default, .. styles.Select(writer.RegisterStyle)];

    private static WriteColumn[] Columns(Kind[] kinds) => [.. kinds.Select((kind, i) => new WriteColumn(Header(i, kind)))];

    /// <summary>A header with markup in it, which must reach the import intact for the plan to bind it.</summary>
    private static string Header(int index, Kind kind) => $"{kind} {index} & <\"x\">";

    private static void WriteRow(TabularWriter writer, StyleId[] ids, object?[] values, int[] cellStyles)
    {
        writer.BeginRow();

        for (int i = 0; i < values.Length; i++)
        {
            StyleId style = ids[cellStyles[i]];

            switch (values[i])
            {
                case string text:
                    writer.Write(text, style);
                    break;
                case long integer:
                    writer.Write(integer, style);
                    break;
                case decimal number:
                    writer.Write(number, style);
                    break;
                case double number:
                    writer.Write(number, style);
                    break;
                case DateTime date:
                    writer.Write(date, style);
                    break;
                case DateOnly day:
                    writer.Write(day, style);
                    break;
                case bool flag:
                    writer.Write(flag, style);
                    break;
                default:
                    writer.WriteEmpty(style);
                    break;
            }
        }

        writer.EndRow();
    }

    private static string DrawName(Random random, HashSet<string> taken)
    {
        string name = string.Concat(Enumerable.Range(0, random.Next(1, 12)).Select(_ => random.Pick(NamePieces))).Trim('\'');

        if (name.Length == 0 || string.Equals(name, "History", StringComparison.OrdinalIgnoreCase) || name.Length > 31)
        {
            name = "Sheet";
        }

        string unique = name;
        int n = 1;

        while (!taken.Add(unique))
        {
            unique = $"{name} {++n}";
        }

        return unique;
    }

    private static CellStyle Style(Random random) => new()
    {
        Fill = random.Chance(0.5) ? CellColor.FromRgb(random.Next(0x1000000)) : null,
        Font = random.Chance(0.5) ? new CellFont { Bold = random.Chance(0.5), Italic = random.Chance(0.5) } : null,
        NumberFormat = random.Chance(0.5) ? NumberFormat.Parse(random.Pick(NumberCodes)) : null,
        DateFormat = random.Chance(0.5) ? DateFormat.Parse(random.Pick(DateCodes)) : null,
        Wrap = random.Chance(0.3),
    };

    private static SheetOptions DrawOptions(Random random, CellStyle[] styles, int columns) => new()
    {
        HeaderStyle = styles.Length > 0 && random.Chance(0.5) ? random.Pick(styles) : null,
        FreezeRows = random.Chance(0.4) ? random.Next(1, 3) : 0,
        FreezeColumns = random.Chance(0.3) ? random.Next(1, columns + 1) : 0,
        AutoFilter = random.Chance(0.4),
    };

    private static object? Draw(Random random, Kind kind)
    {
        if (random.Next(10) == 0)
        {
            return null;
        }

        return kind switch
        {
            Kind.Text => random.Chance(0.02)
                ? "x" + random.Pick(Hostile)
                : string.Concat(Enumerable.Range(0, random.Next(0, 16)).Select(_ => random.Pick(Alphabet))),
            // Past 2^53 a workbook's double loses the integer: refused, now and then.
            Kind.Integer => random.Next(20) == 0 ? random.NextInt64(long.MinValue, long.MaxValue) : random.NextInt64(-9_007_199_254_740_991, 9_007_199_254_740_991),
            Kind.Decimal => new decimal(random.NextInt64(-10_000_000_000, 10_000_000_000)) / Pow10(random.Next(0, 8)),
            Kind.Double => random.Next(30) == 0
                ? random.Pick(double.NaN, double.PositiveInfinity, 1e300, -1e-300)
                : random.NextInt64(-10_000_000, 10_000_000) / Math.Pow(10, random.Next(0, 6)),
            // Workbooks count days from 1900 (1899-12-30 in ods): earlier dates are refused by xlsx.
            Kind.Date => new DateTime(random.Next(1890, 10_000), random.Next(1, 13), random.Next(1, 29), 0, 0, 0, DateTimeKind.Unspecified),
            Kind.DateTime => new DateTime(random.Next(1890, 2200), random.Next(1, 13), random.Next(1, 29), random.Next(24), random.Next(60), random.Next(60), random.Next(1000), DateTimeKind.Unspecified),
            Kind.DateOnly => new DateOnly(random.Next(1890, 10_000), random.Next(1, 13), random.Next(1, 29)),
            Kind.Boolean => random.Chance(0.5),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static decimal Pow10(int exponent)
    {
        decimal result = 1m;

        for (int i = 0; i < exponent; i++)
        {
            result *= 10m;
        }

        return result;
    }

    /// <summary>What the import returns for a value written: text trimmed, blank text absent, a double as the decimal its digits say, a day as its midnight.</summary>
    private static object? Expected(object? value) => value switch
    {
        string text => string.IsNullOrWhiteSpace(text) ? null : text.Trim(),
        double number => (decimal)number,
        DateOnly day => day.ToDateTime(TimeOnly.MinValue),
        _ => value,
    };

    private static ImportField Field(Kind kind, string name) => kind switch
    {
        Kind.Text => ImportField.Text(name),
        Kind.Integer => ImportField.Integer(name),
        Kind.Decimal or Kind.Double => ImportField.Decimal(name),
        Kind.Date or Kind.DateTime or Kind.DateOnly => ImportField.Date(name),
        Kind.Boolean => ImportField.Boolean(name),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string Show(object? value) => value is null
        ? "null"
        : (Convert.ToString(value, CultureInfo.InvariantCulture) ?? "")
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
}
