using System.Formats.Tar;

using TriasDev.Tabular.Archive;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Archive;

/// <summary>A tar read as one workbook: the zip model, with entries read in place.</summary>
public sealed class ArchiveCursorTarTests
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
    [InlineData(TarEntryFormat.Ustar)]
    [InlineData(TarEntryFormat.Pax)]
    [InlineData(TarEntryFormat.Gnu)]
    public void ReadsTheCsvFilesOfATarInPathOrder(TarEntryFormat format)
    {
        byte[] tar = TarArchive.Of(format, ("export/b.csv", "id;city\n2;Bonn\n"), ("export/a.csv", "id;city\n1;Köln\n"));
        using ArchiveCursor cursor = Open(tar);

        Assert.Equal(TabularFormat.Tar, cursor.Format);
        Assert.Equal(["export/a.csv", "export/b.csv"], cursor.Sheets.Select(s => s.Source));
        Assert.Equal(["a", "b"], cursor.Sheets.Select(s => s.Name));
        string?[][] first = [["id", "city"], ["1", "Köln"]];
        Assert.Equal(first, ReadAll(cursor));
        Assert.True(cursor.MoveToSheet(1, Token));
        string?[][] second = [["id", "city"], ["2", "Bonn"]];
        Assert.Equal(second, ReadAll(cursor));
        Assert.True(cursor.MoveToSheet(0, Token));
        Assert.Equal(first, ReadAll(cursor));
    }

    [Fact]
    public void KeepsALongPathWhole()
    {
        string path = "export/" + new string('a', 150) + "/orders.csv";
        using ArchiveCursor cursor = Open(TarArchive.Of(TarEntryFormat.Pax, (path, "a\n1\n")));

        Assert.Equal(path, Assert.Single(cursor.Sheets).Source);
    }

    [Fact]
    public void ReadsAWorkbookInsideATarInPlace()
    {
        byte[] xlsx = Workbook("Orders", "x");
        using ArchiveCursor cursor = Open(TarArchive.Of(TarEntryFormat.Pax,
            TarArchive.File(TarEntryFormat.Pax, "a.csv", "h\n1\n"),
            TarArchive.File(TarEntryFormat.Pax, "b/report.xlsx", xlsx)));

        Assert.Equal(["a.csv", "b/report.xlsx"], cursor.Sheets.Select(s => s.Source));
        Assert.True(cursor.MoveToSheet(1, Token));
        string?[][] expected = [["x"]];
        Assert.Equal(expected, ReadAll(cursor));
        Assert.True(cursor.MoveToSheet(0, Token));
        Assert.Equal(2, ReadAll(cursor).Count);
    }

    [Fact]
    public void LeavesOutWhatIsNotAFileAndSkipsWhatIsNotATable()
    {
        TarEntry link = TarArchive.Entry(TarEntryFormat.Pax, TarEntryType.SymbolicLink, "export/link.csv");
        link.LinkName = "a.csv";
        TarEntry hard = TarArchive.Entry(TarEntryFormat.Pax, TarEntryType.HardLink, "export/hard.csv");
        hard.LinkName = "export/a.csv";

        byte[] tar = TarArchive.Of(TarEntryFormat.Pax,
            TarArchive.Entry(TarEntryFormat.Pax, TarEntryType.Directory, "export/"),
            TarArchive.File(TarEntryFormat.Pax, "export/a.csv", "a\n1\n"),
            link,
            hard,
            TarArchive.Entry(TarEntryFormat.Pax, TarEntryType.Fifo, "export/pipe"),
            TarArchive.File(TarEntryFormat.Pax, "export/._a.csv", "mac resource fork"),
            TarArchive.File(TarEntryFormat.Pax, "export/data.csv.gz", GzipFile.Of("a\n1\n")),
            TarArchive.File(TarEntryFormat.Pax, "export/scan.pdf", [0x25, 0x50, 0x44, 0x46, .. new byte[600]]));

        using ArchiveCursor cursor = Open(tar);

        Assert.Equal("export/a.csv", Assert.Single(cursor.Sheets).Source);
        Assert.Equal(
            [
                new SkippedEntry { Path = "export/data.csv.gz", Reason = SkippedEntryReason.Compressed },
                new SkippedEntry { Path = "export/scan.pdf", Reason = SkippedEntryReason.Binary },
            ],
            cursor.SkippedEntries);
    }

    [Fact]
    public void RefusesATarHoldingASparseFileAsUnsupported()
    {
        // TarReader refuses a GNU sparse entry at its header, so nothing after it can be reached and
        // the entry cannot be skipped alone.
        byte[] tar = TarArchive.Of(TarEntryFormat.Gnu, ("a.csv", "a\n1\n"), ("b.csv", "b\n2\n"));
        // The second entry's header follows the first's 512-byte header and one 512-byte data block.
        byte[] sparse = TarArchive.Retyped(tar, 1024, 'S');

        TabularFormatException error = Assert.Throws<TabularFormatException>(() => Open(sparse));

        Assert.Equal(TabularFormatException.Unsupported, error.Code);
        Assert.Contains("sparse", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A PAX entry whose path fits the ustar fields, as bsdtar (macOS and Windows tar) writes it: an
    /// extended header without a path record, the path split between the ustar prefix and name.
    /// </summary>
    internal static byte[] BsdtarStyle(string prefix, string name, string content)
    {
        PaxTarEntry entry = new(TarEntryType.RegularFile, name, new Dictionary<string, string> { ["mtime"] = "1791060213.2" })
        {
            DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)),
        };
        byte[] tar = TarArchive.Of(TarEntryFormat.Pax, entry);

        // TarWriter always writes a path record; bsdtar does not when the path fits the ustar fields.
        // Overwrite it with a comment record of the same length, so the extended header's size holds.
        string extended = System.Text.Encoding.ASCII.GetString(tar, 512, 512);
        int record = extended.IndexOf(" path=", StringComparison.Ordinal);
        int start = extended.LastIndexOf('\n', record) + 1;
        int length = int.Parse(extended[start..record], System.Globalization.CultureInfo.InvariantCulture);
        string comment = $"{length} comment=";
        System.Text.Encoding.ASCII.GetBytes(comment + new string('x', length - comment.Length - 1) + "\n").CopyTo(tar, 512 + start);

        int header = Enumerable.Range(0, tar.Length / 512).Select(b => b * 512)
            .First(at => tar.AsSpan(at, name.Length).SequenceEqual(System.Text.Encoding.ASCII.GetBytes(name)));
        return TarArchive.Patched(tar, header, block => System.Text.Encoding.ASCII.GetBytes(prefix).CopyTo(block, 345));
    }

    [Fact]
    public void KeepsThePathAPaxEntryKeepsInTheUstarPrefix()
    {
        // TarReader (.NET 8 and 10) drops the prefix of a PAX entry that carries no path record.
        string prefix = "export/" + new string('a', 120);
        using ArchiveCursor cursor = Open(BsdtarStyle(prefix, "items.csv", "sku,count\nA,1\n"));

        Assert.Equal(prefix + "/items.csv", Assert.Single(cursor.Sheets).Source);
        Assert.Equal("items", cursor.Sheets[0].Name);
    }

    [Fact]
    public void NeverMakesASheetOfAnEntryWithoutAName()
    {
        // A damaged header can leave an entry without a name, as the TarReader of .NET 8 reads one.
        // A sheet cannot be named after nothing, and nobody archived a file without a name.
        byte[] tar = TarArchive.Of(TarEntryFormat.Ustar, ("a.csv", "a\n1\n"), ("b.csv", "b\n2\n"));
        byte[] nameless = TarArchive.Patched(tar, 1024, header => Array.Clear(header, 0, 100));

        Exception? error = Record.Exception(() =>
        {
            using ArchiveCursor cursor = Open(nameless);
            Assert.DoesNotContain(cursor.Sheets, s => string.IsNullOrEmpty(s.Name) || string.IsNullOrEmpty(s.Source));
        });

        Assert.True(error is null or TabularException, $"{error?.GetType().Name}: {error?.Message}");
    }

    [Fact]
    public void RefusesEveryCutOfATar()
    {
        byte[] tar = TarArchive.Of(TarEntryFormat.Pax, ("a.csv", "id;name\n" + string.Concat(Enumerable.Range(0, 200).Select(i => $"{i};n{i}\n"))), ("b.csv", "x\n1\n"));

        for (int cut = 512; cut < tar.Length - 512; cut += 97)
        {
            TabularFormatException error = Assert.Throws<TabularFormatException>(() =>
            {
                using ArchiveCursor cursor = Open(tar[..cut]);

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
        // TarWriter ends with two zero blocks; cut both — the file ends right after the last entry.
        byte[] cut = tar[..^1024];

        Assert.Equal(TabularFormatException.Truncated, Assert.Throws<TabularFormatException>(() => Open(cut)).Code);
    }

    [Fact]
    public void AcceptsASingleZeroBlockAsTheEnd()
    {
        byte[] tar = TarArchive.Of(TarEntryFormat.Pax, ("a.csv", "a\n1\n"));

        using ArchiveCursor cursor = Open(tar[..^512]);

        Assert.Single(cursor.Sheets);
    }

    [Fact]
    public void RefusesADamagedHeaderAsCorrupt()
    {
        byte[] tar = TarArchive.Of(TarEntryFormat.Pax, ("a.csv", "a\n1\n"), ("b.csv", "b\n2\n"));
        tar[1024 + 100] ^= 0x5A; // the second header's mode field: its checksum no longer matches

        Assert.Equal(TabularFormatException.Corrupt, Assert.Throws<TabularFormatException>(() => Open(tar)).Code);
    }

    [Fact]
    public void HoldsATarToTheEntryAndSizeBounds()
    {
        byte[] tar = TarArchive.Of(TarEntryFormat.Pax, ("a.csv", "a\n1\n"), ("b.csv", "b\n2\n"), ("c.csv", "c\n3\n"));

        TabularLimitException entries = Assert.Throws<TabularLimitException>(() =>
            Open(tar, new TabularOpenOptions { Archive = new ArchiveCursorOptions { MaxEntries = 2 } }));
        TabularLimitException size = Assert.Throws<TabularLimitException>(() =>
            Open(tar, new TabularOpenOptions { Archive = new ArchiveCursorOptions { MaxUncompressedBytes = 8 } }));

        Assert.Equal(nameof(ArchiveCursorOptions.MaxEntries), entries.Limit);
        Assert.Equal(nameof(ArchiveCursorOptions.MaxUncompressedBytes), size.Limit);
    }

    [Fact]
    public void HoldsAWorkbookReadInPlaceToTheWorkbookBound()
    {
        byte[] tar = TarArchive.Of(TarEntryFormat.Pax, TarArchive.File(TarEntryFormat.Pax, "r.xlsx", Workbook("S", "x")));

        TabularLimitException error = Assert.Throws<TabularLimitException>(() =>
            Open(tar, new TabularOpenOptions { Archive = new ArchiveCursorOptions { MaxEmbeddedWorkbookBytes = 200 } }));

        Assert.Equal(nameof(ArchiveCursorOptions.MaxEmbeddedWorkbookBytes), error.Limit);
    }

    [Fact]
    public void ProfilesTheSameFilesAlikeAsZipAndAsTar()
    {
        (string Path, string Content)[] files = [("orders.csv", "id;qty\n1;2\n3;4\n"), ("stock/items.csv", "sku,count\nA,1\n")];
        byte[] zip = files.Aggregate(new ZipArchiveBuilder(), (b, f) => b.With(f.Path, f.Content)).Build();
        byte[] tar = TarArchive.Of(TarEntryFormat.Pax, files);

        FileProfile fromZip, fromTar;

        using (ArchiveCursor cursor = Open(zip))
        {
            fromZip = TabularAnalyzer.Analyze(cursor, cancellationToken: Token);
        }

        using (ArchiveCursor cursor = Open(tar))
        {
            fromTar = TabularAnalyzer.Analyze(cursor, cancellationToken: Token);
        }

        Assert.Equal(TabularFormat.Tar, fromTar.Format);
        Assert.Equal(fromZip.Sheets.Select(s => (s.Source, s.RowCount)), fromTar.Sheets.Select(s => (s.Source, s.RowCount)));
        Assert.Equal(fromZip.Sheets.Select(s => s.Columns), fromTar.Sheets.Select(s => s.Columns));
    }
}
