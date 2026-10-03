using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>What the xlsx writer writes, the import reads back as the same value of the same type.</summary>
public sealed class XlsxRoundTripTests
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

    private static async Task<byte[]> WriteAsync(Action<TabularWriter> rows, string sheet = "data")
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Xlsx))
        {
            writer.BeginSheet(sheet, [.. Schema.Fields.Select(f => new WriteColumn(f.Name))]);
            rows(writer);
            await writer.CompleteAsync(Token);
        }

        return target.ToArray();
    }

    private static List<ImportOutcome<Row>> Import(byte[] file, int sheetIndex = 0)
    {
        MappingPlan plan = new()
        {
            SheetIndex = sheetIndex,
            Bindings = [.. Schema.Fields.Select((f, i) => new ColumnBinding { ColumnIndex = i, Header = f.Name, FieldName = f.Name })],
        };

        using ImportRun<Row> run = TabularImporter.Import(
            new MemoryStream(file, writable: false),
            "t.xlsx",
            plan,
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
            new("007", long.MinValue, 123_456_789_012.345m, At(1900, 1, 1), true),
            new("two\nlines and\r\nwindows and a lone\rreturn", 9_007_199_254_740_992, 1.50m, At(1900, 3, 1, 0, 0, 0, 1), false),
            new("emoji 👍 and _x0001_ and tab\there", 1L << 60, 0m, At(9999, 12, 31, 23, 59, 59, 999), true),
            new("a   run   of   spaces", 42, -0.000001m, At(1900, 2, 28, 23, 59, 59, 999), false),
        ];

        byte[] file = await WriteAsync(writer =>
        {
            foreach (Row row in written)
            {
                WriteRow(writer, row);
            }
        });

        Assert.Empty(OoxmlValidation.Errors(file));

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
    public async Task ReadsBackEmptyAndWhitespaceTextAsNoValueAndSkipsAnEmptyRow()
    {
        byte[] file = await WriteAsync(writer =>
        {
            foreach (string? text in new[] { null, "", "   ", " padded " })
            {
                writer.BeginRow();
                writer.Write(text);
                writer.Write(1L);
                writer.EndRow();
            }

            writer.BeginRow();
            writer.EndRow();
        });

        MappingPlan plan = new() { Bindings = [.. Schema.Fields.Select((f, i) => new ColumnBinding { ColumnIndex = i, Header = f.Name, FieldName = f.Name })] };
        using ImportRun<string?> run = TabularImporter.Import(new MemoryStream(file, writable: false), "t.xlsx", plan, Schema, row => row[NameField], cancellationToken: Token);

        Assert.Equal(new string?[] { null, null, null, "padded" }, run.ReadRows(Token).Select(outcome => outcome.Value));
        Assert.Equal(1, run.Summary.RowsSkipped);
    }

    [Fact]
    public async Task ImportsEachSheetOfAWorkbook()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Xlsx))
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
    public async Task AMillionRowSheetReadsBackToItsLastRow()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Xlsx))
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

            Assert.Throws<TabularLimitException>(() => writer.BeginRow());
        }

        // The writer is faulted by the refusal, so the file is incomplete by design; write it again,
        // completed, without the refused row.
        target = new WriteTarget();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Xlsx))
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

        using XlsxCursor cursor = new(new MemoryStream(target.ToArray(), writable: false), cancellationToken: Token);
        RawCell last = default;
        int rows = 0;

        while (cursor.ReadRow(Token))
        {
            rows++;
            last = cursor.CurrentRow[0];
        }

        Assert.Equal(1_048_576, rows);
        Assert.Equal(RawCell.FromNumber(1_048_575), last);
    }

    [Theory]
    [InlineData(67)]
    [InlineData(71)]
    [InlineData(2026)]
    public async Task WhatTheWriterAcceptsTheImportReadsBack(int seed)
    {
        Random random = new(seed);
        const string alphabet = "abcXYZ019 ,;\t|\"\n<>&_xüß👍\r";
        List<Row> accepted = [];

        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Xlsx))
        {
            writer.BeginSheet("data", [.. Schema.Fields.Select(f => new WriteColumn(f.Name))]);

            for (int i = 0; i < 300; i++)
            {
                string text = new([.. Enumerable.Range(0, random.Next(1, 40)).Select(_ => alphabet[random.Next(alphabet.Length)])]);

                // An emoji is two chars; a draw that split it is not text anyone writes.
                if (TextRules.Check(text) is not null)
                {
                    continue;
                }

                Row row = new(
                    text,
                    random.NextInt64(-(1L << 53), 1L << 53),
                    Math.Round((decimal)(random.NextDouble() * 2_000_000 - 1_000_000), random.Next(0, 6)),
                    At(random.Next(1900, 10_000), random.Next(1, 13), random.Next(1, 29), random.Next(0, 24), random.Next(0, 60), random.Next(0, 60), random.Next(0, 1000)),
                    random.Next(2) == 0);

                WriteRow(writer, row);
                accepted.Add(row with { Name = string.IsNullOrWhiteSpace(row.Name) ? null : row.Name.Trim() });
            }

            await writer.CompleteAsync(Token);
        }

        List<ImportOutcome<Row>> read = Import(target.ToArray());

        Assert.Equal(accepted.Count, read.Count);

        for (int i = 0; i < accepted.Count; i++)
        {
            Assert.False(read[i].HasErrors, $"seed {seed}, row {i}: {string.Join(", ", read[i].Errors.Select(e => e.Code))}");
            Assert.True(accepted[i] == read[i].Value, $"seed {seed}, row {i}: wrote {accepted[i]}, read {read[i].Value}");
        }
    }
}
