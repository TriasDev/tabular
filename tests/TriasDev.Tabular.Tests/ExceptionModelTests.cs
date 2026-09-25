using System.Text;

using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests;

/// <summary>
/// One base type, a code on every instance, and the payload a host needs to answer without parsing
/// English.
/// </summary>
public sealed class ExceptionModelTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly TextField Name = ImportField.Text("name");

    [Fact]
    public void NamesTheBoundAFileExceeded()
    {
        byte[] content = new XlsxPackage()
            .WithSharedStrings("""<si><t>a</t></si><si><t>b</t></si><si><t>c</t></si>""")
            .WithSheet("Sheet1", """<row r="1"><c r="A1" t="s"><v>0</v></c></row>""")
            .Build();

        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream, new XlsxCursorOptions { MaxSharedStrings = 2 });

        TabularLimitException error = Assert.Throws<TabularLimitException>(() => cursor.ReadRow(TestContext.Current.CancellationToken));

        Assert.Equal(TabularLimitException.Exceeded, error.Code);
        Assert.Equal(nameof(XlsxCursorOptions.MaxSharedStrings), error.Limit);
        Assert.Equal(2, error.Maximum);
    }

    [Fact]
    public void ReportsADamagedZipAsACorruptFileOfTheLibrarysOwnType()
    {
        // A zip signature followed by nothing a zip reader can use. The BCL's InvalidDataException is
        // kept as the inner exception rather than escaping.
        byte[] content = [0x50, 0x4B, 0x03, 0x04, .. new byte[64]];

        using MemoryStream stream = new(content, writable: false);

        TabularFormatException error = Assert.Throws<TabularFormatException>(() => new XlsxCursor(stream));

        Assert.Equal(TabularFormatException.Corrupt, error.Code);
        Assert.IsType<InvalidDataException>(error.InnerException);
    }

    [Fact]
    public void CarriesBothHeadersWhenTheFileIsNotTheOneMapped()
    {
        using CsvCursor cursor = new(new MemoryStream(Utf8NoBom.GetBytes("renamed;x\nv;a\n"), writable: false), "t.csv");

        TabularStructureException error = Assert.Throws<TabularStructureException>(() => TabularImporter.Import(
            cursor,
            new MappingPlan { Bindings = [new ColumnBinding { SourceColumnIndex = 0, SourceHeader = "name", TargetFieldName = "name" }] },
            new TargetSchema { Fields = [Name] },
            row => row[Name],
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(TabularStructureException.HeaderChanged, error.Code);
        Assert.Equal(0, error.SourceColumnIndex);
        Assert.Equal("name", error.ExpectedHeader);
        Assert.Equal("renamed", error.ActualHeader);
    }

    [Fact]
    public void RefusesAPlanThatDoesNotFitItsSchemaTheSameWayThroughEveryEntryPoint()
    {
        // The same fault was ArgumentException through the extractor and TabularStructureException
        // through the importer's stream overload.
        MappingPlan plan = new() { Bindings = [new ColumnBinding { SourceColumnIndex = 0, SourceHeader = "x", TargetFieldName = "nope" }] };
        TargetSchema schema = new() { Fields = [Name] };

        using CsvCursor cursor = new(new MemoryStream(Utf8NoBom.GetBytes("x\n1\n"), writable: false), "t.csv");
        using MemoryStream stream = new(Utf8NoBom.GetBytes("x\n1\n"), writable: false);

        MappingPlanException viaExtractor = Assert.Throws<MappingPlanException>(
            () => TabularExtractor.Start(cursor, plan, schema, cancellationToken: TestContext.Current.CancellationToken));
        MappingPlanException viaImporter = Assert.Throws<MappingPlanException>(
            () => TabularImporter.Import(stream, "t.csv", plan, schema, row => row[Name], cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(MappingPlanException.Invalid, viaExtractor.Code);
        Assert.Equal(viaExtractor.Faults.Select(f => f.Code), viaImporter.Faults.Select(f => f.Code));
        Assert.Contains(viaExtractor.Faults, f => f.Code == "mapping.unknown-field");
    }

    [Fact]
    public void DerivesEveryFileAndPlanFaultFromOneBaseType()
    {
        Type[] types = [typeof(TabularFormatException), typeof(TabularLimitException), typeof(TabularStructureException), typeof(MappingPlanException)];

        Assert.All(types, t => Assert.True(t.IsSubclassOf(typeof(TabularException))));
    }
}
