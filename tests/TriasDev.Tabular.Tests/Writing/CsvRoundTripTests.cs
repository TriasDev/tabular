using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>
/// The rule the writer exists for: what it writes, the import reads back as the same value of the
/// same type — under the invariant culture and the ones a person opens csv files in.
/// </summary>
public sealed class CsvRoundTripTests
{
    private static readonly TextImportField NameField = ImportField.Text("Name");
    private static readonly IntegerImportField CountField = ImportField.Integer("Count");
    private static readonly DecimalImportField AmountField = ImportField.Decimal("Amount");
    private static readonly DateImportField StartField = ImportField.Date("Start");
    private static readonly BooleanImportField ActiveField = ImportField.Boolean("Active");

    private static readonly ImportSchema Schema = new() { Fields = [NameField, CountField, AmountField, StartField, ActiveField] };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<string> Cultures => ["", "de-DE", "en-US"];

    private sealed record Row(string? Name, long? Count, decimal? Amount, DateTime? Start, bool? Active);

    private static async Task<byte[]> WriteAsync(string culture, Action<TabularWriter> rows, bool formulaGuard = false)
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(
            target,
            TabularFormat.Csv,
            new TabularWriterOptions { Csv = new CsvWriterOptions { Culture = culture, FormulaGuard = formulaGuard } }))
        {
            writer.BeginSheet("data", [.. Schema.Fields.Select(f => new WriteColumn(f.Name))]);
            rows(writer);
            await writer.CompleteAsync(Token);
        }

        return target.ToArray();
    }

    private static (List<ImportOutcome<Row>> Rows, int Skipped) Import(byte[] file, string culture)
    {
        MappingPlan plan = new()
        {
            Culture = culture,
            Bindings = [.. Schema.Fields.Select((f, i) => new ColumnBinding { ColumnIndex = i, Header = f.Name, FieldName = f.Name })],
        };

        using ImportRun<Row> run = TabularImporter.Import(
            new MemoryStream(file, writable: false),
            "t.csv",
            plan,
            Schema,
            row => new Row(row[NameField], row[CountField], row[AmountField], row[StartField], row[ActiveField]),
            cancellationToken: Token);

        List<ImportOutcome<Row>> rows = [.. run.ReadRows(Token)];
        return (rows, run.Summary.RowsSkipped);
    }

    private static void WriteRow(TabularWriter writer, string? name, long count, decimal amount, DateTime start, bool active)
    {
        writer.BeginRow();
        writer.Write(name);
        writer.Write(count);
        writer.Write(amount);
        writer.Write(start);
        writer.Write(active);
        writer.EndRow();
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task ReadsBackEveryKindOfValue(string culture)
    {
        Row[] written =
        [
            new("Alpha", 0, 0.1m, new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Unspecified), true),
            new("Grüße, \"quoted\"; | piped", -1, -1_234_567.891m, new DateTime(2026, 10, 3, 14, 5, 6, 789, DateTimeKind.Unspecified), false),
            new("007", long.MinValue, 123_456_789_012_345.6789m, new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Unspecified), true),
            new("two\nlines and\r\nwindows", long.MaxValue, 1.50m, new DateTime(1900, 3, 1, 0, 0, 0, 1, DateTimeKind.Unspecified), false),
            new("emoji 👍 and _x0001_ and tab\there", 9_007_199_254_740_993, 0m, new DateTime(9999, 12, 31, 23, 59, 59, 999, DateTimeKind.Unspecified), true),
            new("a   run   of   spaces", 42, 79_228_162_514_264_337_593_543_950_335m, new DateTime(2, 1, 1, 0, 0, 0, DateTimeKind.Unspecified), false),
        ];

        byte[] file = await WriteAsync(culture, writer =>
        {
            foreach (Row row in written)
            {
                WriteRow(writer, row.Name, row.Count!.Value, row.Amount!.Value, row.Start!.Value, row.Active!.Value);
            }
        });

        (List<ImportOutcome<Row>> read, _) = Import(file, culture);

        Assert.All(read, outcome => Assert.False(outcome.HasErrors, string.Join(", ", outcome.Errors.Select(e => e.Code))));
        Assert.Equal(written, read.Select(outcome => outcome.Value));
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task ReadsBackADoubleAsTheDecimalItsDigitsSay(string culture)
    {
        double[] values = [0.1, -2.5, 1e20, 1e-7, 123_456_789.012345];

        byte[] file = await WriteAsync(culture, writer =>
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

        (List<ImportOutcome<Row>> read, _) = Import(file, culture);

        Assert.Equal(values.Select(v => (decimal?)(decimal)v), read.Select(outcome => outcome.Value!.Amount));
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task ReadsBackDatesAsWrittenToTheMillisecond(string culture)
    {
        DateTime[] values =
        [
            new DateTime(2026, 10, 3, 23, 59, 59, 999, DateTimeKind.Unspecified).AddTicks(9_999),
            DateTime.MaxValue,
            new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Local),
        ];

        byte[] file = await WriteAsync(culture, writer =>
        {
            foreach (DateTime value in values)
            {
                writer.BeginRow();
                writer.Write("x");
                writer.WriteEmpty();
                writer.WriteEmpty();
                writer.Write(value);
                writer.EndRow();
            }

            writer.BeginRow();
            writer.Write("x");
            writer.WriteEmpty();
            writer.WriteEmpty();
            writer.Write(new DateOnly(2026, 10, 3));
            writer.EndRow();
        });

        (List<ImportOutcome<Row>> read, _) = Import(file, culture);

        DateTime[] expected =
        [
            new(2026, 10, 3, 23, 59, 59, 999, DateTimeKind.Unspecified),
            new(9999, 12, 31, 23, 59, 59, 999, DateTimeKind.Unspecified),
            new(2026, 10, 3, 12, 0, 0, DateTimeKind.Unspecified),
            new(2026, 10, 3, 12, 0, 0, DateTimeKind.Unspecified),
            new(2026, 10, 3, 0, 0, 0, DateTimeKind.Unspecified),
        ];

        Assert.Equal(expected.Select(d => (DateTime?)d), read.Select(outcome => outcome.Value!.Start));
        Assert.All(read, outcome => Assert.Equal(DateTimeKind.Unspecified, outcome.Value!.Start!.Value.Kind));
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task ReadsEmptyAndWhitespaceTextAsNoValueAndSkipsAnEmptyRow(string culture)
    {
        byte[] file = await WriteAsync(culture, writer =>
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

        (List<ImportOutcome<Row>> read, int skipped) = Import(file, culture);

        Assert.Equal(new string?[] { null, null, null, "padded" }, read.Select(outcome => outcome.Value!.Name));
        Assert.Equal(1, skipped);
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task ReadsBackAFieldWithAsManyLineBreaksAsAllowed(string culture)
    {
        string note = string.Join('\n', Enumerable.Repeat("line", 101));

        byte[] file = await WriteAsync(culture, writer =>
        {
            writer.BeginRow();
            writer.Write(note);
            writer.EndRow();
            writer.BeginRow();
            writer.Write("after");
            writer.EndRow();
        });

        (List<ImportOutcome<Row>> read, _) = Import(file, culture);

        Assert.Equal(new[] { note, "after" }, read.Select(outcome => outcome.Value!.Name));
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task ReadsBackASingleColumnFile(string culture)
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(
            target,
            TabularFormat.Csv,
            new TabularWriterOptions { Csv = new CsvWriterOptions { Culture = culture } }))
        {
            writer.BeginSheet("data", [new("Amount")]);

            foreach (decimal amount in new[] { 1.5m, -2.25m, 1000m })
            {
                writer.BeginRow();
                writer.Write(amount);
                writer.EndRow();
            }

            await writer.CompleteAsync(Token);
        }

        ImportSchema schema = new() { Fields = [AmountField] };
        MappingPlan plan = new() { Culture = culture, Bindings = [new ColumnBinding { ColumnIndex = 0, Header = "Amount", FieldName = "Amount" }] };

        using ImportRun<decimal?> run = TabularImporter.Import(new MemoryStream(target.ToArray(), writable: false), "t.csv", plan, schema, row => row[AmountField], cancellationToken: Token);

        Assert.Equal(new decimal?[] { 1.5m, -2.25m, 1000m }, run.ReadRows(Token).Select(outcome => outcome.Value));
    }

    [Fact]
    public async Task TheFormulaGuardIsWhatTheImportReadsBack()
    {
        byte[] file = await WriteAsync("", writer =>
        {
            writer.BeginRow();
            writer.Write("=1+2");
            writer.Write(-5L);
            writer.EndRow();
        }, formulaGuard: true);

        (List<ImportOutcome<Row>> read, _) = Import(file, "");

        Row row = Assert.Single(read).Value!;
        Assert.Equal("'=1+2", row.Name);
        Assert.Equal(-5, row.Count);
    }
    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task ReadsBackASingleColumnOfFractionalDecimals(string culture)
    {
        // de-DE writes 1,5 2,5 3,5: unquoted, a comma on every line is what the reader takes for the delimiter.
        decimal[] values = [1.5m, 2.5m, 3.5m, -0.25m, 4.75m];

        byte[] file = await CsvRoundTrip.WriteAsync(culture, ["Amount"], writer =>
        {
            foreach (decimal value in values)
            {
                writer.BeginRow();
                writer.Write(value);
                writer.EndRow();
            }
        }, Token);

        (List<object?[]> rows, List<string> errors, _) = CsvRoundTrip.Import(file, culture, [AmountField], Token);

        Assert.Empty(errors);
        Assert.Equal(values.Select(v => (object?)v), rows.Select(row => row[0]));
    }

    public static TheoryData<string, char> CulturesAndCandidates => new()
    {
        { "", ';' }, { "", ',' }, { "", '\t' }, { "", '|' },
        { "de-DE", ';' }, { "de-DE", ',' }, { "de-DE", '\t' }, { "de-DE", '|' },
    };

    [Theory]
    [MemberData(nameof(CulturesAndCandidates))]
    public async Task ReadsBackASingleTextColumnWhoseEveryValueHoldsADelimiterCandidate(string culture, char candidate)
    {
        string[] values = [$"a{candidate}b", $"c{candidate}d", $"e{candidate}f"];

        byte[] file = await CsvRoundTrip.WriteAsync(culture, ["Name"], writer =>
        {
            foreach (string value in values)
            {
                writer.BeginRow();
                writer.Write(value);
                writer.EndRow();
            }
        }, Token);

        (List<object?[]> rows, List<string> errors, _) = CsvRoundTrip.Import(file, culture, [NameField], Token);

        Assert.Empty(errors);
        Assert.Equal(values, rows.Select(row => (string?)row[0]));
    }

    [Fact]
    public async Task ReadsBackACommaFileWhoseHeaderAndRowsAllHoldASemicolon()
    {
        // Comma and semicolon would both divide every line evenly: a tie the reader must not be offered.
        TextImportField first = ImportField.Text("Code;Kind");
        TextImportField second = ImportField.Text("Note;More");
        string[][] values = [["a;b", "c;d"], ["e;f", "g;h"], ["i;j", "k;l"]];

        byte[] file = await CsvRoundTrip.WriteAsync("", [first.Name, second.Name], writer =>
        {
            foreach (string[] row in values)
            {
                writer.BeginRow();
                writer.Write(row[0]);
                writer.Write(row[1]);
                writer.EndRow();
            }
        }, Token);

        (List<object?[]> rows, List<string> errors, _) = CsvRoundTrip.Import(file, "", [first, second], Token);

        Assert.Empty(errors);
        Assert.Equal(values.Select(row => row.Cast<object?>().ToArray()), rows);
    }

    /// <summary>A note the reader takes for a stray quote and the records it swallowed, in the first of five columns.</summary>
    private const string AddressNote = "Street 1,\nCity, Region, Country, Planet";

    private static string[] Headers(int count) => [.. Enumerable.Range(0, count).Select(i => $"C{i}")];

    private static TextImportField[] TextFields(int count) => [.. Headers(count).Select(ImportField.Text)];

    [Fact]
    public async Task RefusesMultiLineTextTheReaderWouldSplitIntoRecords()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Csv);
        writer.BeginSheet("data", [.. Headers(5).Select(h => new WriteColumn(h))]);
        writer.BeginRow();

        TabularWriteException refused = Assert.Throws<TabularWriteException>(() => writer.Write(AddressNote));

        Assert.Equal(ErrorCodes.Write.AmbiguousLineBreaks, refused.Code);
        Assert.Equal(0, refused.ColumnIndex);
        Assert.Equal(2, refused.RowNumber);
    }

    [Theory]
    [InlineData(4, 0)]  // under five columns the reader never judges a quote stray
    [InlineData(5, 4)]  // the last column: four cells before it plus one comma overfill the line, so the quote is syntax
    public async Task ReadsBackMultiLineTextWhereTheReaderKeepsItAsOneValue(int columns, int column)
    {
        string[][] written =
        [
            [.. Enumerable.Range(0, columns).Select(i => i == column ? AddressNote : $"v{i}")],
            [.. Enumerable.Range(0, columns).Select(i => $"w{i}")],
        ];

        byte[] file = await CsvRoundTrip.WriteAsync("", Headers(columns), writer =>
        {
            foreach (string[] row in written)
            {
                writer.BeginRow();

                foreach (string value in row)
                {
                    writer.Write(value);
                }

                writer.EndRow();
            }
        }, Token);

        (List<object?[]> rows, List<string> errors, _) = CsvRoundTrip.Import(file, "", TextFields(columns), Token);

        Assert.Empty(errors);
        Assert.Equal(written.Select(row => row.Cast<object?>().ToArray()), rows);
    }

    [Fact]
    public async Task RefusesTheSameTextInAColumnWhereItsLineWouldStillFitARecord()
    {
        // Two cells before it plus one comma make three fields of five: read as stray, the line fits.
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Csv);
        writer.BeginSheet("data", [.. Headers(5).Select(h => new WriteColumn(h))]);
        writer.BeginRow();
        writer.Write("v0");
        writer.Write("v1");

        Assert.Equal(ErrorCodes.Write.AmbiguousLineBreaks, Assert.Throws<TabularWriteException>(() => writer.Write(AddressNote)).Code);
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task ReadsBackAMultiLineNoteWithFewerDelimitersThanARecordHolds(string culture)
    {
        // Six columns: a record's worth is five delimiters; the note has four, whatever the delimiter.
        string note = "Dear team,\nplease; check | the\r\nnumbers, thanks";
        string[] written = [note, "a", "b", "c", "d", "e"];

        byte[] file = await CsvRoundTrip.WriteAsync(culture, Headers(6), writer =>
        {
            writer.BeginRow();

            foreach (string value in written)
            {
                writer.Write(value);
            }

            writer.EndRow();
        }, Token);

        (List<object?[]> rows, List<string> errors, _) = CsvRoundTrip.Import(file, culture, TextFields(6), Token);

        Assert.Empty(errors);
        Assert.Equal(written, Assert.Single(rows).Cast<string?>());
    }

    [Fact]
    public async Task TheFormulaGuardLeavesHeadersAsWritten()
    {
        TextImportField minus = ImportField.Text("-A");
        TextImportField equals = ImportField.Text("=B");

        byte[] file = await CsvRoundTrip.WriteAsync("", [minus.Name, equals.Name], writer =>
        {
            writer.BeginRow();
            writer.Write("-1");
            writer.Write("x");
            writer.EndRow();
        }, Token, formulaGuard: true);

        Assert.StartsWith("\uFEFF-A,=B\r\n", System.Text.Encoding.UTF8.GetString(file), StringComparison.Ordinal);

        (List<object?[]> rows, List<string> errors, _) = CsvRoundTrip.Import(file, "", [minus, equals], Token);

        Assert.Empty(errors);
        Assert.Equal(new object?[] { "'-1", "x" }, Assert.Single(rows));
    }
}
