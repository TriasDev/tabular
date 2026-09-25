using System.Text;

using TriasDev.Tabular.Abstractions;
using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Import;
using TriasDev.Tabular.Mapping;
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Analysis;

/// <summary>
/// A timestamp that carries a zone is read as the clock time it states, on every server alike.
/// </summary>
/// <remarks>
/// Parsed with default styles, <c>10:00+05:00</c> was converted to the host's local time, so the same
/// file imported different dates depending on where it ran — and a timestamp near midnight moved to
/// another day. Dates here are wall-clock values (<see cref="DateTimeKind.Unspecified"/>), so the
/// time is kept as written and the offset set aside.
/// </remarks>
public sealed class ZonedDateTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    [Theory]
    [InlineData("2024-01-15T10:00:00Z")]
    [InlineData("2024-01-15T10:00:00+05:00")]
    [InlineData("2024-01-15T10:00:00-08:00")]
    [InlineData("2024-01-15T10:00:00")]
    public void ImportsAZonedTimestampFromCsvAsTheClockTimeItStates(string written)
    {
        DateField when = ImportField.Date("when");
        using CsvCursor cursor = new(new MemoryStream(Utf8NoBom.GetBytes($"when;x\n{written};a\n"), writable: false), "t.csv");

        using ImportRun<DateTime?> run = TabularImporter.Import(
            cursor,
            new MappingPlan { Bindings = [new ColumnBinding { SourceColumnIndex = 0, SourceHeader = "when", TargetFieldName = "when" }] },
            new TargetSchema { Fields = [when] },
            row => row[when],
            cancellationToken: TestContext.Current.CancellationToken);

        DateTime value = Assert.Single(run).Value!.Value;

        Assert.Equal(new DateTime(2024, 1, 15, 10, 0, 0, DateTimeKind.Unspecified), value);
        Assert.Equal(DateTimeKind.Unspecified, value.Kind);
    }

    [Theory]
    [InlineData("2024-01-15T23:30:00Z")]
    [InlineData("2024-01-15T23:30:00+05:00")]
    public void ReadsAWorkbooksIsoDateCellAsTheClockTimeItStates(string written)
    {
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", $"""<row r="1"><c r="A1" t="d"><v>{written}</v></c></row>""")
            .Build();

        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        RawCell cell = cursor.CurrentRow[0];

        Assert.Equal(RawCellKind.Date, cell.Kind);
        Assert.Equal(new DateTime(2024, 1, 15, 23, 30, 0, DateTimeKind.Unspecified), cell.Date);
    }
}
