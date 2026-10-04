using System.Text;

using TriasDev.Tabular.Archive;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Archive;

/// <summary>A gzip file is known by its bytes and goes all the way through: profile, plan, import.</summary>
public sealed class GzipDetectionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly TextImportField Name = ImportField.Text("name");

    private static readonly ImportSchema Schema = new() { Fields = [Name] };

    private static ITabularCursor Open(byte[] content, string name) =>
        TabularFile.Open(new MemoryStream(content, writable: false), name, cancellationToken: Token);

    [Fact]
    public void OpensAGzipFileByItsBytesWhateverItIsCalled()
    {
        byte[] file = GzipFile.Of("name;qty\nx;1\n");

        Assert.Equal(TabularFormat.Gzip, TabularFile.Detect(new MemoryStream(file)));
        using ITabularCursor cursor = Open(file, "export.csv");

        Assert.IsType<GzipCursor>(cursor);
        Assert.Equal("export.csv", Assert.Single(cursor.Sheets).Name);
    }

    [Theory]
    [InlineData(new byte[] { 0x1F, (byte)'a', (byte)';', (byte)'b', (byte)'\n' })]
    [InlineData(new byte[] { 0x1F, 0x8B, (byte)'a', (byte)'\n' })]
    public void KeepsAFileThatOnlyStartsLikeGzipForCsv(byte[] content) =>
        Assert.Equal(TabularFormat.Csv, TabularFile.Detect(new MemoryStream(content)));

    [Fact]
    public void ProfilesACompressedCsvAsTheFileItself()
    {
        const string Csv = "name;qty\nx;1\ny;2\n";

        FileProfile plain, compressed;

        using (ITabularCursor cursor = Open(Encoding.UTF8.GetBytes(Csv), "orders.csv"))
        {
            plain = TabularAnalyzer.Analyze(cursor, cancellationToken: Token);
        }

        using (ITabularCursor cursor = Open(GzipFile.Of(Csv), "orders.csv.gz"))
        {
            compressed = TabularAnalyzer.Analyze(cursor, cancellationToken: Token);
        }

        Assert.Equal(TabularFormat.Gzip, compressed.Format);
        Assert.Equal(TabularFormat.Csv, compressed.Sheets[0].Format);
        Assert.Equal(plain.Sheets[0].RowCount, compressed.Sheets[0].RowCount);
        Assert.Equal(plain.Sheets[0].Columns, compressed.Sheets[0].Columns);
        Assert.Equal(';', compressed.Sheets[0].Dialect!.Delimiter);
    }

    [Fact]
    public void ImportsThroughAPlanWhateverTheFileIsCalled()
    {
        // Analysed under the upload's name, imported under a storage key: a gzip file without a
        // stored name must not be refused by its own plan.
        string rows = string.Concat(Enumerable.Range(0, 20_000).Select(i => $"n{i}\n"));
        byte[] file = GzipFile.Of("name\n" + rows);

        FileProfile profile;

        using (ITabularCursor cursor = Open(file, "orders.csv.gz"))
        {
            profile = TabularAnalyzer.Analyze(cursor, cancellationToken: Token);
        }

        MappingPlan plan = MappingPlan.ByHeader(profile.Sheets[0], Schema);
        TrackedStream stream = new(file);

        using (ImportRun<string?> run = TabularImporter.Import(stream, "blob-4711", plan, Schema, row => row[Name], cancellationToken: Token))
        {
            IReadOnlyList<string?> names = run.ReadAll(cancellationToken: Token).Items;
            Assert.Equal(20_000, names.Count);
            Assert.Equal("n19999", names[^1]);
        }

        Assert.True(stream.IsDisposed);
    }

    [Fact]
    public void RefusesAPlanWhenTheStoredNameChanged()
    {
        FileProfile profile;

        using (ITabularCursor cursor = Open(GzipFile.Of("name\nx\n", "march.csv"), "upload.gz"))
        {
            profile = TabularAnalyzer.Analyze(cursor, cancellationToken: Token);
        }

        MappingPlan plan = MappingPlan.ByHeader(profile.Sheets[0], Schema);

        TabularStructureException error = Assert.Throws<TabularStructureException>(() => TabularImporter.Import(
            new MemoryStream(GzipFile.Of("name\nx\n", "april.csv")), "upload.gz", plan, Schema, row => row[Name], cancellationToken: Token));

        Assert.Equal(TabularStructureException.SheetChanged, error.Code);
    }
}
