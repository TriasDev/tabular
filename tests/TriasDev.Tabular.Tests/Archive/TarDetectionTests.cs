using System.Formats.Tar;
using System.Text;

using TriasDev.Tabular.Archive;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Archive;

/// <summary>A tar is known by its bytes, raw or gzipped, and goes all the way through.</summary>
public sealed class TarDetectionTests
{
    private static readonly TextImportField Name = ImportField.Text("name");

    private static readonly ImportSchema Schema = new() { Fields = [Name] };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static ITabularCursor Open(byte[] content, string name) =>
        TabularFile.Open(new MemoryStream(content, writable: false), name, cancellationToken: Token);

    [Fact]
    public void OpensATarAndAGzippedTarAsArchivesWhateverTheyAreCalled()
    {
        byte[] tar = TarArchive.Of(TarEntryFormat.Pax, ("orders.csv", "name\nx\n"));
        byte[] tarGz = GzipFile.Of(tar);

        Assert.Equal(TabularFormat.Tar, TabularFile.Detect(new MemoryStream(tar)));
        Assert.Equal(TabularFormat.Tar, TabularFile.Detect(new MemoryStream(tarGz)));

        using ITabularCursor plain = Open(tar, "upload.bin");
        using ITabularCursor compressed = Open(tarGz, "upload.csv");

        Assert.IsType<ArchiveCursor>(plain);
        Assert.IsType<ArchiveCursor>(compressed);
        Assert.Equal("orders.csv", Assert.Single(compressed.Sheets).Source);
    }

    [Fact]
    public void KeepsAGzippedCsvAGzip() =>
        Assert.Equal(TabularFormat.Gzip, TabularFile.Detect(new MemoryStream(GzipFile.Of("a;b\n1;2\n"))));

    [Fact]
    public void KeepsTextThatSpellsTheMagicACsv()
    {
        byte[] text = Encoding.ASCII.GetBytes(new string('a', 257) + "ustar\0" + "00" + new string('b', 300) + "\n");

        Assert.Equal(TabularFormat.Csv, TabularFile.Detect(new MemoryStream(text)));
    }

    [Fact]
    public void GzipCursorNamesAGzippedTarWhenItRefusesIt()
    {
        byte[] tarGz = GzipFile.Of(TarArchive.Of(TarEntryFormat.Pax, ("a.csv", "a\n1\n")));

        TabularFormatException error = Assert.Throws<TabularFormatException>(() =>
            new GzipCursor(new MemoryStream(tarGz), "a.tar.gz", cancellationToken: Token));

        Assert.Equal(TabularFormatException.Unsupported, error.Code);
        Assert.Contains("tar", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportsASheetOfAGzippedTarThroughAPlanMadeFromItsProfile()
    {
        string rows = string.Concat(Enumerable.Range(0, 20_000).Select(i => $"n{i}\n"));
        byte[] archive = GzipFile.Of(TarArchive.Of(TarEntryFormat.Pax, ("first.csv", "other\nq\n"), ("second.csv", "name\n" + rows)));

        FileProfile profile;

        using (ITabularCursor cursor = Open(archive, "upload.tgz"))
        {
            profile = TabularAnalyzer.Analyze(cursor, cancellationToken: Token);
        }

        Assert.Equal(TabularFormat.Tar, profile.Format);
        MappingPlan plan = MappingPlan.ByHeader(profile.Sheets[1], Schema);
        Assert.Equal("second.csv", plan.SheetSource);

        TrackedStream stream = new(archive);

        using (ImportRun<string?> run = TabularImporter.Import(stream, "blob", plan, Schema, row => row[Name], cancellationToken: Token))
        {
            IReadOnlyList<string?> names = run.ReadAll(cancellationToken: Token).Items;
            Assert.Equal(20_000, names.Count);
            Assert.Equal("n19999", names[^1]);
        }

        Assert.True(stream.IsDisposed);
    }
}
