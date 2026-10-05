using System.Formats.Tar;

using TriasDev.Tabular.Archive;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Archive;

/// <summary>A compressed tar read as one workbook, through one pass and as few more as moving back needs.</summary>
public sealed class ArchiveCursorTarGzTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static ArchiveCursor Open(byte[] archive, TabularOpenOptions? options = null) =>
        new(new MemoryStream(archive, writable: false), options, Token);

    private static List<string?[]> ReadAll(ITabularCursor cursor)
    {
        List<string?[]> rows = [];

        while (cursor.ReadRow(Token))
        {
            string?[] row = new string?[cursor.CurrentRow.Length];

            for (int i = 0; i < row.Length; i++)
            {
                row[i] = cursor.CurrentRow[i].AsText();
            }

            rows.Add(row);
        }

        return rows;
    }

    private static byte[] Workbook(string sheet, string text) =>
        new XlsxPackage().WithSheet(sheet, $"""<row r="1"><c t="inlineStr"><is><t>{text}</t></is></c></row>""").Build();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SkipsAFileWhosePathRepeatsAnEarlierOneInTheArchivesOrder(bool compressed)
    {
        // A tar appends a newer copy under the same path; the sheets of both could not be told apart.
        // The first in the archive's order is read, whether the archive is read with a directory
        // (a plain tar) or as a stream (compressed).
        byte[] tar = TarArchive.Of(TarEntryFormat.Pax, ("z.csv", "h\nfirst\n"), ("a.csv", "h\na\n"), ("z.csv", "h\nsecond\n"));
        using ArchiveCursor cursor = Open(compressed ? GzipFile.Of(tar) : tar);

        Assert.Equal(["a.csv", "z.csv"], cursor.Sheets.Select(s => s.Source));
        Assert.True(cursor.MoveToSheet(1, Token));
        Assert.Equal([["h"], ["first"]], ReadAll(cursor));
        Assert.Equal([new SkippedEntry { Path = "z.csv", Reason = SkippedEntryReason.DuplicatePath }], cursor.SkippedEntries);
    }

    [Fact]
    public void RefusesAnArchiveThatNoLongerHoldsWhatItsListingFound()
    {
        // Moving back decompresses the file again. A source that serves other bytes the second time —
        // a file replaced while it was read — must not hand out another entry's rows under the name
        // the listing gave, nor end the sheet quietly.
        byte[] listed = GzipFile.Of(TarArchive.Of(TarEntryFormat.Pax, ("a.csv", "h\na\n"), ("b.csv", "h\nb\n")));
        byte[] replaced = GzipFile.Of(TarArchive.Of(TarEntryFormat.Pax, ("a.csv", "h\na\n")));
        ReplaceableStream stream = new(listed);
        using ArchiveCursor cursor = new(stream, cancellationToken: Token);

        Assert.True(cursor.MoveToSheet(1, Token));
        Assert.Equal([["h"], ["b"]], ReadAll(cursor));

        stream.Replace(replaced);

        TabularFormatException changed = Assert.Throws<TabularFormatException>(() => cursor.MoveToSheet(1, Token));
        Assert.Equal(TabularFormatException.Corrupt, changed.Code);
    }

    /// <summary>A readable, seekable stream whose content can be swapped for other bytes between reads.</summary>
    private sealed class ReplaceableStream(byte[] content) : Stream
    {
        private MemoryStream _inner = new(content, writable: false);

        public void Replace(byte[] next)
        {
            long position = _inner.Position;
            _inner = new MemoryStream(next, writable: false) { Position = Math.Min(position, next.Length) };
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void Flush()
        {
            // Read-only: nothing to flush.
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void ReadsAGzippedTarAsATar()
    {
        byte[] archive = GzipFile.Of(TarArchive.Of(TarEntryFormat.Pax, ("export/orders.csv", "id;city\n1;Köln\n")));
        using ArchiveCursor cursor = Open(archive);

        Assert.Equal(TabularFormat.Tar, cursor.Format);
        Assert.Equal("export/orders.csv", Assert.Single(cursor.Sheets).Source);
        string?[][] expected = [["id", "city"], ["1", "Köln"]];
        Assert.Equal(expected, ReadAll(cursor));
    }

    [Fact]
    public void ReadsEverySheetOfAShuffledArchiveWhereverTheCursorMoves()
    {
        // Stored z, m, a, x.xlsx — listed a, m, x.xlsx, z: every move back starts the pass again.
        byte[] archive = GzipFile.Of(TarArchive.Of(TarEntryFormat.Pax,
            TarArchive.File(TarEntryFormat.Pax, "z.csv", "z\n26\n"),
            TarArchive.File(TarEntryFormat.Pax, "m.csv", "m\n13\n"),
            TarArchive.File(TarEntryFormat.Pax, "a.csv", "a\n1\n"),
            TarArchive.File(TarEntryFormat.Pax, "x.xlsx", Workbook("X", "24"))));

        using ArchiveCursor cursor = Open(archive);

        Assert.Equal(["a.csv", "m.csv", "x.xlsx", "z.csv"], cursor.Sheets.Select(s => s.Source));
        string?[][][] expected = [[["a"], ["1"]], [["m"], ["13"]], [["24"]], [["z"], ["26"]]];

        foreach (int index in new[] { 0, 1, 2, 3, 3, 1, 0, 2, 0 })
        {
            Assert.True(cursor.MoveToSheet(index, Token));
            Assert.Equal(expected[index], ReadAll(cursor));
        }
    }

    [Fact]
    public void LeavesOutWhatIsNotAFileAndSkipsWhatIsNotATable()
    {
        byte[] archive = GzipFile.Of(TarArchive.Of(TarEntryFormat.Pax,
            TarArchive.Entry(TarEntryFormat.Pax, TarEntryType.Directory, "d/"),
            TarArchive.File(TarEntryFormat.Pax, "d/scan.pdf", [0x25, 0x50, 0x44, 0x46, .. new byte[600]]),
            TarArchive.File(TarEntryFormat.Pax, "d/a.csv", "a\n1\n")));

        using ArchiveCursor cursor = Open(archive);

        Assert.Equal("d/a.csv", Assert.Single(cursor.Sheets).Source);
        Assert.Equal(new SkippedEntry { Path = "d/scan.pdf", Reason = SkippedEntryReason.Binary }, Assert.Single(cursor.SkippedEntries));
    }

    [Fact]
    public void KeepsThePathAPaxEntryKeepsInTheUstarPrefix()
    {
        string prefix = "export/" + new string('a', 120);
        using ArchiveCursor cursor = Open(GzipFile.Of(ArchiveCursorTarTests.BsdtarStyle(prefix, "items.csv", "sku,count\nA,1\n")));

        Assert.Equal(prefix + "/items.csv", Assert.Single(cursor.Sheets).Source);
    }

    [Fact]
    public void RefusesEveryCutOfAGzippedTar()
    {
        byte[] archive = GzipFile.Of(TarArchive.Of(TarEntryFormat.Pax, ("a.csv", "id\n" + string.Concat(Enumerable.Range(0, 500).Select(i => $"{i}\n"))), ("b.csv", "x\n1\n")));

        for (int cut = 10; cut < archive.Length; cut += 13)
        {
            TabularFormatException error = Assert.Throws<TabularFormatException>(() =>
            {
                using ArchiveCursor cursor = Open(archive[..cut]);

                for (int s = 0; s < cursor.Sheets.Count && cursor.MoveToSheet(s, Token); s++)
                {
                    ReadAll(cursor);
                }
            });

            Assert.True(error.Code is TabularFormatException.Truncated or TabularFormatException.Corrupt, $"cut {cut}: {error.Code}");
        }
    }

    [Fact]
    public void RefusesAnArchiveWithoutItsEndMarker()
    {
        byte[] tar = TarArchive.Of(TarEntryFormat.Pax, ("a.csv", "a\n1\n"));

        Assert.Equal(TabularFormatException.Truncated, Assert.Throws<TabularFormatException>(() => Open(GzipFile.Of(tar[..^1024]))).Code);
    }

    [Fact]
    public void HoldsAGzippedTarToTheBoundsAsItGoes()
    {
        byte[] archive = GzipFile.Of(TarArchive.Of(TarEntryFormat.Pax, ("a.csv", "a\n1\n"), ("b.csv", "b\n2\n"), ("c.csv", "c\n3\n")));

        Assert.Equal(nameof(ArchiveCursorOptions.MaxEntries), Assert.Throws<TabularLimitException>(() =>
            Open(archive, new TabularOpenOptions { Archive = new ArchiveCursorOptions { MaxEntries = 2 } })).Limit);
        Assert.Equal(nameof(ArchiveCursorOptions.MaxUncompressedBytes), Assert.Throws<TabularLimitException>(() =>
            Open(archive, new TabularOpenOptions { Archive = new ArchiveCursorOptions { MaxUncompressedBytes = 600 } })).Limit);
    }

    [Fact]
    public void ProfilesTheSameFilesAlikeAsZipAsTarAndAsTarGz()
    {
        (string Path, string Content)[] files = [("orders.csv", "id;qty\n1;2\n3;4\n"), ("stock/items.csv", "sku,count\nA,1\n")];
        byte[] zip = files.Aggregate(new ZipArchiveBuilder(), (b, f) => b.With(f.Path, f.Content)).Build();
        byte[] tarGz = GzipFile.Of(TarArchive.Of(TarEntryFormat.Gnu, files));

        FileProfile fromZip, fromTarGz;

        using (ArchiveCursor cursor = Open(zip))
        {
            fromZip = TabularAnalyzer.Analyze(cursor, cancellationToken: Token);
        }

        using (ArchiveCursor cursor = Open(tarGz))
        {
            fromTarGz = TabularAnalyzer.Analyze(cursor, cancellationToken: Token);
        }

        Assert.Equal(fromZip.Sheets.Select(s => (s.Source, s.RowCount)), fromTarGz.Sheets.Select(s => (s.Source, s.RowCount)));
        Assert.Equal(fromZip.Sheets.Select(s => s.Columns), fromTarGz.Sheets.Select(s => s.Columns));
    }
}
