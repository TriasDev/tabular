using System.IO.Compression;

using TriasDev.Tabular.Archive;
using TriasDev.Tabular.Ods;
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Archive;

/// <summary>A zip is opened as what its directory says it holds, and an archive goes all the way through.</summary>
public sealed class ArchiveDetectionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly TextField Name = ImportField.Text("name");

    private static readonly TargetSchema Schema = new() { Fields = [Name] };

    private static ITabularCursor Open(byte[] content, string name = "upload") =>
        TabularFile.Open(new MemoryStream(content, writable: false), name, cancellationToken: Token);

    private static byte[] Workbook() =>
        new XlsxPackage().WithSheet("S", """<row r="1"><c t="inlineStr"><is><t>a</t></is></c></row>""").Build();

    [Fact]
    public void OpensAZipOfFilesAsAnArchive()
    {
        byte[] archive = new ZipArchiveBuilder().With("orders.csv", "name\nx\n").Build();

        Assert.Equal(TabularFormat.Zip, TabularFile.Detect(new MemoryStream(archive)));
        using ITabularCursor cursor = Open(archive, "export.zip");

        Assert.IsType<ArchiveCursor>(cursor);
        Assert.Equal("orders.csv", Assert.Single(cursor.Sheets).Source);
    }

    [Fact]
    public void OpensAWorkbookAsAWorkbookWhateverItIsCalled()
    {
        using ITabularCursor cursor = Open(Workbook(), "report.zip");

        Assert.IsType<XlsxCursor>(cursor);
    }

    [Fact]
    public void KnowsAWorkbookWithoutContentTypesByItsRelationships()
    {
        // A writer that leaves [Content_Types].xml out still writes the package relationships.
        byte[] trimmed = Without(Workbook(), "[Content_Types].xml");

        Assert.Equal(TabularFormat.Xlsx, TabularFile.Detect(new MemoryStream(trimmed)));
    }

    [Fact]
    public void KnowsAWorkbookWhosePartNamesAWriterSpelledItsOwnWay()
    {
        // The workbook reader matches part names without regard to case or slash direction; so must
        // the detection that sends a file to it, or a file it reads is refused as an empty archive.
        byte[] odd = Renamed(Workbook(), ("[Content_Types].xml", "[content_types].xml"), ("_rels/.rels", "_rels\\.rels"));

        Assert.Equal(TabularFormat.Xlsx, TabularFile.Detect(new MemoryStream(odd)));
        using ITabularCursor cursor = Open(odd);
        Assert.IsType<XlsxCursor>(cursor);
    }

    [Fact]
    public void KnowsASpreadsheetWhoseMimetypeIsNotFirst()
    {
        byte[] ods = new OdsPackage()
            .WithTable("T", "<table:table-row><table:table-cell office:value-type=\"string\"><text:p>v</text:p></table:table-cell></table:table-row>")
            .WithMimetypeLast()
            .Build();

        Assert.Equal(TabularFormat.Ods, TabularFile.Detect(new MemoryStream(ods)));
        using ITabularCursor cursor = Open(ods);
        Assert.IsType<OdsCursor>(cursor);
    }

    [Fact]
    public void RefusesAnOpenDocumentFileThatIsNotASpreadsheet()
    {
        byte[] odt = new ZipArchiveBuilder()
            .With("mimetype", "application/vnd.oasis.opendocument.text")
            .With("content.xml", "<office:document-content/>")
            .Build();

        TabularFormatException error = Assert.Throws<TabularFormatException>(() => Open(odt, "letter.odt"));

        Assert.Equal(TabularFormatException.Unsupported, error.Code);
        Assert.Contains("not a spreadsheet", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfilesEverySheetOfAnArchiveWithItsOwnSourceAndSaysWhatItSkipped()
    {
        byte[] archive = new ZipArchiveBuilder()
            .With("a/orders.csv", "name;qty\nx;1\n")
            .With("b/stock.xlsx", Workbook())
            .With("scan.pdf", [0x25, 0x50, 0x44, 0x46, .. Enumerable.Repeat((byte)0, 600)])
            .Build();

        using ITabularCursor cursor = Open(archive);
        FileProfile profile = new TabularAnalyzer().Analyze(cursor, cancellationToken: Token);

        Assert.Equal(TabularFormat.Zip, profile.Format);
        Assert.Equal(["a/orders.csv", "b/stock.xlsx"], profile.Sheets.Select(s => s.Source));
        Assert.Equal([TabularFormat.Csv, TabularFormat.Xlsx], profile.Sheets.Select(s => s.Format));
        Assert.Equal(';', profile.Sheets[0].Dialect!.Delimiter);
        Assert.Null(profile.Sheets[1].Dialect);
        Assert.Equal(new SkippedEntry { Path = "scan.pdf", Reason = SkippedEntryReason.Binary }, Assert.Single(profile.SkippedEntries));
    }

    [Fact]
    public void ImportsASheetOfAnArchiveThroughAPlanMadeFromItsProfile()
    {
        string rows = string.Concat(Enumerable.Range(0, 20_000).Select(i => $"n{i}\n"));
        byte[] archive = new ZipArchiveBuilder().With("first.csv", "other\nq\n").With("second.csv", "name\n" + rows).Build();

        FileProfile profile;

        using (ITabularCursor cursor = Open(archive))
        {
            profile = new TabularAnalyzer().Analyze(cursor, cancellationToken: Token);
        }

        MappingPlan plan = MappingPlan.ByHeader(profile.Sheets[1], Schema);
        Assert.Equal("second.csv", plan.SheetSource);

        TrackedStream stream = new(archive);

        using (ImportRun<string?> run = TabularImporter.Import(stream, "upload.zip", plan, Schema, row => row[Name], cancellationToken: Token))
        {
            IReadOnlyList<string?> names = run.All(cancellationToken: Token).Items;
            Assert.Equal(20_000, names.Count);
            Assert.Equal("n19999", names[^1]);
        }

        Assert.True(stream.IsDisposed);
    }

    [Fact]
    public void RefusesAPlanWhenAnotherFileNowStandsAtItsSheet()
    {
        // The plan was made for the sheet at index 0; a file sorting before it was added since.
        byte[] before = new ZipArchiveBuilder().With("m.csv", "name\nx\n").Build();
        byte[] after = new ZipArchiveBuilder().With("a.csv", "name\ny\n").With("m.csv", "name\nx\n").Build();

        FileProfile profile;

        using (ITabularCursor cursor = Open(before))
        {
            profile = new TabularAnalyzer().Analyze(cursor, cancellationToken: Token);
        }

        MappingPlan plan = MappingPlan.ByHeader(profile.Sheets[0], Schema);

        TabularStructureException error = Assert.Throws<TabularStructureException>(() => TabularImporter.Import(
            new MemoryStream(after), "upload.zip", plan, Schema, row => row[Name], cancellationToken: Token));

        Assert.Equal(TabularStructureException.SheetChanged, error.Code);
    }

    private static byte[] Renamed(byte[] zip, params (string From, string To)[] renames)
    {
        ZipArchiveBuilder builder = new();

        using ZipArchive source = new(new MemoryStream(zip), ZipArchiveMode.Read);

        foreach (ZipArchiveEntry entry in source.Entries)
        {
            using Stream content = entry.Open();
            using MemoryStream copy = new();
            content.CopyTo(copy);
            string name = renames.FirstOrDefault(r => r.From == entry.FullName).To ?? entry.FullName;
            builder.With(name, copy.ToArray());
        }

        return builder.Build();
    }

    private static byte[] Without(byte[] zip, string entryName)
    {
        ZipArchiveBuilder builder = new();

        using ZipArchive source = new(new MemoryStream(zip), ZipArchiveMode.Read);

        foreach (ZipArchiveEntry entry in source.Entries.Where(e => e.FullName != entryName))
        {
            using Stream content = entry.Open();
            using MemoryStream copy = new();
            content.CopyTo(copy);
            builder.With(entry.FullName, copy.ToArray());
        }

        return builder.Build();
    }

    private sealed class TrackedStream(byte[] content) : MemoryStream(content, writable: false)
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
