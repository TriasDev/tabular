using TriasDev.Tabular.Ods;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>What the ods writer writes, the import reads back as the same value of the same type.</summary>
public sealed class OdsRoundTripTests
{
    private static readonly TextImportField NameField = ImportField.Text("Name");
    private static readonly IntegerImportField CountField = ImportField.Integer("Count");
    private static readonly DecimalImportField AmountField = ImportField.Decimal("Amount");
    private static readonly DateImportField StartField = ImportField.Date("Start");
    private static readonly BooleanImportField ActiveField = ImportField.Boolean("Active");

    private static readonly ImportSchema Schema = new() { Fields = [NameField, CountField, AmountField, StartField, ActiveField] };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed record Row(string? Name, long? Count, decimal? Amount, DateTime? Start, bool? Active);

    private static DateTime At(int year, int month, int day, int hour = 0, int minute = 0, int second = 0, int millisecond = 0) =>
        new(year, month, day, hour, minute, second, millisecond, DateTimeKind.Unspecified);

    private static MappingPlan Plan(int sheetIndex = 0) => new()
    {
        SheetIndex = sheetIndex,
        Bindings = [.. Schema.Fields.Select((f, i) => new ColumnBinding { ColumnIndex = i, Header = f.Name, FieldName = f.Name })],
    };

    private static async Task<byte[]> WriteAsync(Action<TabularWriter> rows)
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Ods))
        {
            writer.BeginSheet("data", [.. Schema.Fields.Select(f => new WriteColumn(f.Name))]);
            rows(writer);
            await writer.CompleteAsync(Token);
        }

        return target.ToArray();
    }

    private static List<ImportOutcome<Row>> Import(byte[] file, int sheetIndex = 0)
    {
        using ImportRun<Row> run = TabularImporter.Import(
            new MemoryStream(file, writable: false),
            "t.ods",
            Plan(sheetIndex),
            Schema,
            row => new Row(row[NameField], row[CountField], row[AmountField], row[StartField], row[ActiveField]),
            cancellationToken: Token);

        return [.. run.ReadRows(Token)];
    }

    private static void WriteRow(TabularWriter writer, Row row)
    {
        writer.BeginRow();
        writer.Write(row.Name);
        writer.Write(row.Count!.Value);
        writer.Write(row.Amount!.Value);
        writer.Write(row.Start!.Value);
        writer.Write(row.Active!.Value);
        writer.EndRow();
    }

    [Fact]
    public async Task ReadsBackEveryKindOfValue()
    {
        Row[] written =
        [
            new("Alpha", 0, 0.1m, At(2026, 10, 3), true),
            new("Grüße, \"quoted\"; | & <tag>", -1, -1_234_567.891m, At(2026, 10, 3, 14, 5, 6, 789), false),
            new("007", long.MinValue, 123_456_789_012.345m, At(1066, 10, 14), true),
            new("two\nlines and\r\nwindows and a lone\rreturn", 9_007_199_254_740_992, 1.50m, At(1, 1, 1), false),
            new("emoji 👍 and _x0001_ and tab\there", 1L << 60, 0m, At(9999, 12, 31, 23, 59, 59, 999), true),
            new("a   run   of   spaces and\n\nan empty line", 42, -0.000001m, At(1899, 12, 31, 23, 59, 59, 999), false),
        ];

        byte[] file = await WriteAsync(writer =>
        {
            foreach (Row row in written)
            {
                WriteRow(writer, row);
            }
        });

        List<ImportOutcome<Row>> read = Import(file);

        Assert.All(read, outcome => Assert.False(outcome.HasErrors, string.Join(", ", outcome.Errors.Select(e => e.Code))));
        Assert.Equal(written, read.Select(outcome => outcome.Value));
    }

    [Fact]
    public async Task ReadsBackADoubleAsTheDecimalItsDigitsSay()
    {
        double[] values = [0.1, -2.5, 1e20, 1e-7, 123_456_789.012345];

        byte[] file = await WriteAsync(writer =>
        {
            foreach (double value in values)
            {
                writer.BeginRow();
                writer.Write("x");
                writer.WriteEmpty();
                writer.Write(value);
                writer.EndRow();
            }
        });

        Assert.Equal(values.Select(v => (decimal?)(decimal)v), Import(file).Select(outcome => outcome.Value!.Amount));
    }

    [Fact]
    public async Task ReadsEmptyAndWhitespaceTextAsNoValueAndPassesOverAnEmptyRow()
    {
        byte[] file = await WriteAsync(writer =>
        {
            foreach (string? text in new[] { null, "", "   ", " padded " })
            {
                writer.BeginRow();
                writer.Write(text);
                writer.Write(1L);
                writer.EndRow();

                if (text is null)
                {
                    // Between data rows. The ods cursor passes over an all-empty row without handing it out, so the import never counts it — but the rows after it keep their numbers.
                    writer.BeginRow();
                    writer.EndRow();
                }
            }
        });

        using ImportRun<string?> run = TabularImporter.Import(new MemoryStream(file, writable: false), "t.ods", Plan(), Schema, row => row[NameField], cancellationToken: Token);

        List<ImportOutcome<string?>> outcomes = [.. run.ReadRows(Token)];

        Assert.Equal(new string?[] { null, null, null, "padded" }, outcomes.Select(outcome => outcome.Value));
        Assert.Equal(new[] { 2, 4, 5, 6 }, outcomes.Select(outcome => outcome.RowNumber));
        Assert.Equal(0, run.Summary.RowsSkipped);
    }

    [Fact]
    public async Task ImportsEachSheet()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Ods))
        {
            foreach (string sheet in new[] { "First", "Second" })
            {
                writer.BeginSheet(sheet, [.. Schema.Fields.Select(f => new WriteColumn(f.Name))]);
                WriteRow(writer, new Row(sheet, 1, 1m, At(2026, 1, 1), true));
            }

            await writer.CompleteAsync(Token);
        }

        Assert.Equal("First", Assert.Single(Import(target.ToArray(), 0)).Value!.Name);
        Assert.Equal("Second", Assert.Single(Import(target.ToArray(), 1)).Value!.Name);
    }

    [Fact]
    public async Task AMillionRowSheetReadsBackToItsLastRowAndTheNextIsRefused()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Ods))
        {
            writer.BeginSheet("data", [new("n")]);

            for (long n = 1; n < 1_048_576; n++)
            {
                writer.BeginRow();
                writer.Write(n);
                writer.EndRow();

                if (writer.FlushRecommended)
                {
                    await writer.FlushAsync(Token);
                }
            }

            await writer.CompleteAsync(Token);
        }

        using OdsCursor cursor = new(new MemoryStream(target.ToArray(), writable: false), cancellationToken: Token);
        RawCell last = default;
        int rows = 0;

        while (cursor.ReadRow(Token))
        {
            rows++;
            last = cursor.CurrentRow[0];
        }

        Assert.Equal(1_048_576, rows);
        Assert.Equal(RawCell.FromNumber(1_048_575), last);

        await using TabularWriter full = TabularWriter.Create(new WriteTarget(), TabularFormat.Ods);
        full.BeginSheet("data", [new("n")], SheetOptions.Default);

        for (long n = 1; n < 1_048_576; n++)
        {
            full.BeginRow();
            full.EndRow();
        }

        TabularWriteException refused = Assert.Throws<TabularWriteException>(() => full.BeginRow());
        Assert.Equal(ErrorCodes.Write.TooManyRows, refused.Code);
        Assert.Equal(1_048_576, refused.RowNumber);
    }

    [Theory]
    [InlineData(67)]
    [InlineData(72)]
    [InlineData(2026)]
    public async Task WhatTheWriterAcceptsTheImportReadsBack(int seed)
    {
        Random random = new(seed);
        string[] alphabet = ["a", "b", "c", "X", "Y", "Z", "0", "1", "9", " ", ",", ";", "\t", "|", "\"", "\n", "<", ">", "&", "_", "x", "ü", "ß", "👍", "\r"];
        List<Row> accepted = [];

        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Ods))
        {
            writer.BeginSheet("data", [.. Schema.Fields.Select(f => new WriteColumn(f.Name))]);

            for (int i = 0; i < 300; i++)
            {
                string text = string.Concat(Enumerable.Range(0, random.Next(1, 40)).Select(_ => alphabet[random.Next(alphabet.Length)]));
                decimal amount = random.Next(2) == 0
                    ? Math.Round((decimal)(random.NextDouble() * 2_000_000 - 1_000_000), random.Next(0, 6))
                    : decimal.Round(random.Next(-999_999_999, 999_999_999) + (decimal)random.NextDouble(), random.Next(4, 7));

                if (ValueChecks.DecimalInDouble(amount) is not null)
                {
                    amount = decimal.Round(amount, 2);
                }

                Row row = new(
                    text,
                    random.NextInt64(-(1L << 53), 1L << 53),
                    amount,
                    At(random.Next(1, 10_000), random.Next(1, 13), random.Next(1, 29), random.Next(0, 24), random.Next(0, 60), random.Next(0, 60), random.Next(0, 1000)),
                    random.Next(2) == 0);

                WriteRow(writer, row);
                accepted.Add(row with { Name = string.IsNullOrWhiteSpace(row.Name) ? null : row.Name.Trim() });
            }

            await writer.CompleteAsync(Token);
        }

        List<ImportOutcome<Row>> read = Import(target.ToArray());

        Assert.Equal(300, accepted.Count);
        Assert.Equal(accepted.Count, read.Count);

        for (int i = 0; i < accepted.Count; i++)
        {
            Assert.False(read[i].HasErrors, $"seed {seed}, row {i}: {string.Join(", ", read[i].Errors.Select(e => e.Code))}");
            Assert.True(accepted[i] == read[i].Value, $"seed {seed}, row {i}: wrote {accepted[i]}, read {read[i].Value}");
        }
    }
}
