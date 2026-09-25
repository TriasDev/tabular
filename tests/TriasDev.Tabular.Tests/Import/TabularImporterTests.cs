using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Import;

/// <summary>
/// Pins the shape a consuming domain actually writes against.
/// </summary>
/// <remarks>
/// The measure of this layer is how little a caller has to know. It declares its fields, writes one
/// expression turning a row into its own type, and reads the result — without knowing the order the
/// schema declares, how a row is stored, or that a cursor exists.
/// </remarks>
public sealed class TabularImporterTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The fields, declared once and used both to build the schema and to read a row.</summary>
    private static class Fields
    {
        public static readonly TextField Code = ImportField.Text("code").Require().ExactLength(3);

        public static readonly TextField Name = ImportField.Text("name").Require().MaxLength(50);

        public static readonly DecimalField Amount = ImportField.Decimal("amount");

        public static readonly DateField Signed = ImportField.Date("signed");

        public static readonly IntegerField Count = ImportField.Integer("count");

        public static readonly BooleanField Active = ImportField.Boolean("active");
    }

    private static TargetSchema Schema { get; } = new()
    {
        Fields = [Fields.Code, Fields.Name, Fields.Amount, Fields.Signed, Fields.Count, Fields.Active],
    };

    private sealed record Company(string Code, string Name, decimal? Amount, DateTime? Signed, long? Count, bool? Active);

    private static Company Build(ImportRow row) =>
        new(row[Fields.Code]!, row[Fields.Name]!, row[Fields.Amount], row[Fields.Signed], row[Fields.Count], row[Fields.Active]);

    private static MappingPlan Plan(params string[] headers) =>
        new()
        {
            Culture = "en-US",
            Bindings =
            [
                new ColumnBinding { SourceColumnIndex = 0, SourceHeader = headers[0], TargetFieldName = "code" },
                new ColumnBinding { SourceColumnIndex = 1, SourceHeader = headers[1], TargetFieldName = "name" },
                new ColumnBinding { SourceColumnIndex = 2, SourceHeader = headers[2], TargetFieldName = "amount" },
                new ColumnBinding { SourceColumnIndex = 3, SourceHeader = headers[3], TargetFieldName = "signed" },
                new ColumnBinding { SourceColumnIndex = 4, SourceHeader = headers[4], TargetFieldName = "count" },
                new ColumnBinding { SourceColumnIndex = 5, SourceHeader = headers[5], TargetFieldName = "active" },
            ],
        };

    private const string Headers = "code;name;amount;signed;count;active";

    private static ImportRun<Company> Run(string csv, out CsvCursor cursor)
    {
        MemoryStream stream = new(Utf8NoBom.GetBytes(csv), writable: false);
        cursor = new CsvCursor(stream, "test.csv");

        return TabularImporter.Import(cursor, Plan("code", "name", "amount", "signed", "count", "active"), Schema, Build);
    }

    [Fact]
    public void BuildsAnItemFromEveryRowThatFits()
    {
        using ImportRun<Company> run = Run(
            $"{Headers}\nDEU;Acme;1234.56;2023-01-15;42;true\nAUT;Beta;7.5;2024-06-30;7;false\n",
            out CsvCursor cursor);

        using (cursor)
        {
            ImportResult<Company> result = run.All(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(2, result.Items.Count);
            Assert.Empty(result.Errors);

            Company first = result.Items[0];

            Assert.Equal("DEU", first.Code);
            Assert.Equal("Acme", first.Name);
            Assert.Equal(1234.56m, first.Amount);
            Assert.Equal(new DateTime(2023, 1, 15, 0, 0, 0, DateTimeKind.Unspecified), first.Signed);
            Assert.Equal(42, first.Count);
            Assert.True(first.Active);
        }
    }

    [Fact]
    public void ReadsAValueByItsFieldRatherThanItsPosition()
    {
        // The schema declares code first and amount third. A mapper reading by position would break
        // silently the day someone reorders the declaration; reading by field cannot.
        TargetSchema reordered = new()
        {
            Fields = [Fields.Amount, Fields.Signed, Fields.Active, Fields.Count, Fields.Name, Fields.Code],
        };

        using MemoryStream stream = new(
            Utf8NoBom.GetBytes($"{Headers}\nDEU;Acme;1234.56;2023-01-15;42;true\n"),
            writable: false);
        using CsvCursor cursor = new(stream, "test.csv");
        using ImportRun<Company> run = TabularImporter.Import(
            cursor,
            Plan("code", "name", "amount", "signed", "count", "active"),
            reordered,
            Build, cancellationToken: TestContext.Current.CancellationToken);

        Company only = Assert.Single(run.All(cancellationToken: TestContext.Current.CancellationToken).Items);

        Assert.Equal("DEU", only.Code);
        Assert.Equal(1234.56m, only.Amount);
    }

    [Fact]
    public void RefusesAFieldTheSchemaDoesNotDeclare()
    {
        // A field belonging to another target, or one removed from the schema, is a defect in the
        // caller — not a value that happens to be missing.
        TextField stranger = ImportField.Text("nonsense");

        using MemoryStream stream = new(Utf8NoBom.GetBytes($"{Headers}\nDEU;Acme;1;2023-01-15;1;true\n"), writable: false);
        using CsvCursor cursor = new(stream, "test.csv");
        using ImportRun<string> run = TabularImporter.Import(
            cursor,
            Plan("code", "name", "amount", "signed", "count", "active"),
            Schema,
            row => row[stranger]!, cancellationToken: TestContext.Current.CancellationToken);

        ArgumentException error = Assert.Throws<ArgumentException>(() => run.All(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("nonsense", error.Message, StringComparison.Ordinal);
        Assert.Contains("code", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsARowThatFailsInsteadOfBuildingIt()
    {
        using ImportRun<Company> run = Run(
            $"{Headers}\nDEU;Acme;1;2023-01-15;1;true\nDEUX;Beta;2;2024-01-01;2;false\n",
            out CsvCursor cursor);

        using (cursor)
        {
            List<ImportOutcome<Company>> outcomes = [.. run];

            Assert.Equal(2, outcomes.Count);
            Assert.False(outcomes[0].HasErrors);
            Assert.True(outcomes[1].HasErrors);
            Assert.Null(outcomes[1].Value);
            Assert.Equal("value.exact-length", Assert.Single(outcomes[1].Errors).Code);
            Assert.Equal(3, outcomes[1].RowNumber);
        }
    }

    [Fact]
    public void LeavesAnAbsentOptionalValueNull()
    {
        using ImportRun<Company> run = Run($"{Headers}\nDEU;Acme;;;;\n", out CsvCursor cursor);

        using (cursor)
        {
            Company only = Assert.Single(run.All(cancellationToken: TestContext.Current.CancellationToken).Items);

            Assert.Null(only.Amount);
            Assert.Null(only.Signed);
            Assert.Null(only.Count);
            Assert.Null(only.Active);
        }
    }

    [Fact]
    public void ReadsInBatchesOfTheRequestedSize()
    {
        StringBuilder csv = new($"{Headers}\n");

        for (int i = 1; i <= 250; i++)
        {
            csv.Append("DEU;Firm ").Append(i).Append(";1;2023-01-15;1;true\n");
        }

        using ImportRun<Company> run = Run(csv.ToString(), out CsvCursor cursor);

        using (cursor)
        {
            List<ImportChunk<Company>> chunks = [.. run.InChunks(100, TestContext.Current.CancellationToken)];

            Assert.Equal(3, chunks.Count);
            Assert.Equal(100, chunks[0].Items.Count);
            Assert.Equal(100, chunks[1].Items.Count);
            Assert.Equal(50, chunks[2].Items.Count);
            Assert.Equal(2, chunks[0].FirstRowNumber);
            Assert.Equal(251, chunks[^1].LastRowNumber);
        }
    }

    [Fact]
    public void ReportsAFailureInTheBatchItBelongsTo()
    {
        StringBuilder csv = new($"{Headers}\n");

        for (int i = 1; i <= 20; i++)
        {
            csv.Append(i == 15 ? "DEUX" : "DEU").Append(";Firm ").Append(i).Append(";1;2023-01-15;1;true\n");
        }

        using ImportRun<Company> run = Run(csv.ToString(), out CsvCursor cursor);

        using (cursor)
        {
            List<ImportChunk<Company>> chunks = [.. run.InChunks(10, TestContext.Current.CancellationToken)];

            Assert.Contains(chunks, c => c.Errors.Count == 1);
            Assert.Equal(19, chunks.Sum(c => c.Items.Count));
        }
    }

    [Fact]
    public void ReportsFailuresEvenWhenNothingWasBuilt()
    {
        using ImportRun<Company> run = Run($"{Headers}\nDEUX;Acme;1;2023-01-15;1;true\n", out CsvCursor cursor);

        using (cursor)
        {
            ImportChunk<Company> only = Assert.Single(run.InChunks(100, TestContext.Current.CancellationToken));

            Assert.Empty(only.Items);
            Assert.Single(only.Errors);
        }
    }

    [Fact]
    public void RefusesToHoldMoreThanTheCallerAllowed()
    {
        StringBuilder csv = new($"{Headers}\n");

        for (int i = 1; i <= 50; i++)
        {
            csv.Append("DEU;Firm ").Append(i).Append(";1;2023-01-15;1;true\n");
        }

        using ImportRun<Company> run = Run(csv.ToString(), out CsvCursor cursor);

        using (cursor)
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => run.All(limit: 10, cancellationToken: TestContext.Current.CancellationToken));

            Assert.Contains("InChunks", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CarriesTheSummaryOfTheWholeRun()
    {
        using ImportRun<Company> run = Run(
            $"{Headers}\nDEU;Acme;1;2023-01-15;1;true\nDEUX;Beta;2;2024-01-01;2;false\n;;;;;\n",
            out CsvCursor cursor);

        using (cursor)
        {
            ImportResult<Company> result = run.All(cancellationToken: TestContext.Current.CancellationToken);
            ExtractionSummary summary = result.Summary;

            Assert.Equal(3, summary.RowsRead);
            Assert.Equal(1, summary.RowsProduced);
            Assert.Equal(1, summary.RowsFailed);
            Assert.Equal(1, summary.RowsSkipped);
        }
    }

    [Fact]
    public void IsReadOnce()
    {
        using ImportRun<Company> run = Run($"{Headers}\nDEU;Acme;1;2023-01-15;1;true\n", out CsvCursor cursor);

        using (cursor)
        {
            run.All(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Throws<InvalidOperationException>(() => run.All(cancellationToken: TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public void OpensAndClosesTheFileItself()
    {
        // What a caller normally wants. Constructing a cursor only to hand it over untouched is
        // ceremony, and the run can close what it opened.
        using MemoryStream stream = new(
            Utf8NoBom.GetBytes($"{Headers}\nDEU;Acme;1;2023-01-15;1;true\n"),
            writable: false);

        using ImportRun<Company> run = TabularImporter.Import(
            stream,
            "test.csv",
            Plan("code", "name", "amount", "signed", "count", "active"),
            Schema,
            Build, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Acme", Assert.Single(run.All(cancellationToken: TestContext.Current.CancellationToken).Items).Name);
    }

    [Fact]
    public void ReadsACsvThatWasNamedXlsx()
    {
        // The format is decided by the bytes. Somebody renames an export, or a browser guesses a
        // content type, and a reader that trusts the name fails with a message about a corrupt
        // archive — which sends whoever reads it looking in entirely the wrong place.
        using MemoryStream stream = new(
            Utf8NoBom.GetBytes($"{Headers}\nDEU;Acme;1;2023-01-15;1;true\n"),
            writable: false);

        using ImportRun<Company> run = TabularImporter.Import(
            stream,
            "misnamed.xlsx",
            Plan("code", "name", "amount", "signed", "count", "active"),
            Schema,
            Build, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Acme", Assert.Single(run.All(cancellationToken: TestContext.Current.CancellationToken).Items).Name);
    }

    [Fact]
    public void CountsHowMuchOfEachFieldTheFileFilled()
    {
        // The check an error list cannot make: a field bound to the wrong column is not invalid, it
        // is empty, so it passes every rule and shows up only here.
        using ImportRun<Company> run = Run(
            $"{Headers}\nDEU;Acme;1;2023-01-15;1;true\nAUT;Beta;;;;\n",
            out CsvCursor cursor);

        using (cursor)
        {
            run.All(cancellationToken: TestContext.Current.CancellationToken);

            FieldCoverage name = run.Coverage.Single(c => c.Field == "name");
            FieldCoverage amount = run.Coverage.Single(c => c.Field == "amount");

            Assert.Equal(2, name.Filled);
            Assert.Equal(0, name.Empty);
            Assert.Equal(1, name.Share);
            Assert.True(name.Required);

            Assert.Equal(1, amount.Filled);
            Assert.Equal(1, amount.Empty);
            Assert.Equal(0.5, amount.Share);
        }
    }

    [Fact]
    public void KeepsNoPreviewUnlessAskedFor()
    {
        using ImportRun<Company> run = Run($"{Headers}\nDEU;Acme;1;2023-01-15;1;true\n", out CsvCursor cursor);

        using (cursor)
        {
            run.All(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Empty(run.Preview);
        }
    }

    [Fact]
    public void KeepsAsManyPreviewRowsAsWereAskedFor()
    {
        StringBuilder csv = new($"{Headers}\n");

        for (int i = 1; i <= 20; i++)
        {
            csv.Append("DEU;Firm ").Append(i).Append(";1.5;2023-01-15;7;true\n");
        }

        using MemoryStream stream = new(Utf8NoBom.GetBytes(csv.ToString()), writable: false);
        using CsvCursor cursor = new(stream, "test.csv");
        using ImportRun<Company> run = TabularImporter.Import(
            cursor,
            Plan("code", "name", "amount", "signed", "count", "active"),
            Schema,
            Build,
            new ImportOptions { PreviewRows = 3 }, cancellationToken: TestContext.Current.CancellationToken);

        run.All(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, run.Preview.Count);
        Assert.Equal(2, run.Preview[0].RowNumber);
        Assert.Equal("Firm 1", run.Preview[0].Values["name"]);
        Assert.Equal("2023-01-15", run.Preview[0].Values["signed"]);
    }

    [Fact]
    public void LetsAMappersOwnDefectSurfaceRatherThanFilingItUnderTheFile()
    {
        // A null reference in a mapper is the caller's bug. Recording it as a row error would put it
        // in the list a user reads to fix their spreadsheet, where nobody can act on it.
        using MemoryStream stream = new(Utf8NoBom.GetBytes($"{Headers}\nDEU;Acme;1;2023-01-15;1;true\n"), writable: false);
        using CsvCursor cursor = new(stream, "test.csv");
        using ImportRun<string> run = TabularImporter.Import<string>(
            cursor,
            Plan("code", "name", "amount", "signed", "count", "active"),
            Schema,
            _ => throw new InvalidOperationException("the mapper is wrong"),
            cancellationToken: TestContext.Current.CancellationToken);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => run.All(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("the mapper is wrong", error.Message);
    }
}
