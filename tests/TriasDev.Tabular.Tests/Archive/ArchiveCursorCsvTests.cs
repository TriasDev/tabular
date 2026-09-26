using System.Text;

using TriasDev.Tabular.Archive;
using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Archive;

/// <summary>The csv files in a zip archive, read as the sheets of one workbook.</summary>
public sealed class ArchiveCursorCsvTests
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

    [Fact]
    public void ReadsOneZippedCsvAsTheSameRowsAsTheFileItself()
    {
        const string Csv = "id;city\n1;Köln\n2;\"Bonn; am Rhein\"\n";
        using ArchiveCursor cursor = Open(new ZipArchiveBuilder().With("export/orders.csv", Csv).Build());
        using CsvCursor plain = new(new MemoryStream(Encoding.UTF8.GetBytes(Csv)), "orders");

        SheetInfo sheet = Assert.Single(cursor.Sheets);
        Assert.Equal("orders", sheet.Name);
        Assert.Equal("export/orders.csv", sheet.Source);
        Assert.Equal(TabularFormat.Csv, sheet.Format);
        Assert.Equal(TabularFormat.Zip, cursor.Format);
        Assert.Equal(ReadAll(plain), ReadAll(cursor));
        Assert.Equal(';', cursor.Dialect!.Delimiter);
    }

    [Fact]
    public void OrdersTheSheetsByPathAndReadsEachInItsOwnDialect()
    {
        // Written in reverse order, each in another encoding and delimiter: the order is the paths',
        // and the dialect is taken from each file's own head — the head read at listing is only a
        // probe, and the file read afterwards must follow it byte for byte.
        byte[] utf16 = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("name\tcity\nÄrger\tMünchen\n")];
        byte[] ansi = Encoding.Latin1.GetBytes("name,city\nMüller,Köln\n");
        byte[] bom = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("name;city\nÖzil;Gelsenkirchen\n")];

        using ArchiveCursor cursor = Open(new ZipArchiveBuilder()
            .With("c.tsv", utf16)
            .With("b.csv", ansi)
            .With("a.csv", bom)
            .Build());

        Assert.Equal(["a", "b", "c"], cursor.Sheets.Select(s => s.Name));
        Assert.Equal([0, 1, 2], cursor.Sheets.Select(s => s.Index));

        string?[][] expected = [["Özil", "Gelsenkirchen"], ["Müller", "Köln"], ["Ärger", "München"]];
        char[] delimiters = [';', ',', '\t'];

        for (int i = 0; i < 3; i++)
        {
            Assert.True(cursor.MoveToSheet(i));
            Assert.Equal(delimiters[i], cursor.Dialect!.Delimiter);
            List<string?[]> rows = ReadAll(cursor);
            Assert.Equal(2, rows.Count);
            Assert.Equal(expected[i], rows[1]);
        }
    }

    [Fact]
    public void LeavesOutDirectoriesHiddenFilesAndMacResourceForks()
    {
        using ArchiveCursor cursor = Open(new ZipArchiveBuilder()
            .WithDirectory("export")
            .With("__MACOSX/export/._orders.csv", "junk")
            .With("export/.DS_Store", "junk")
            .With(".hidden/notes.csv", "a;b\n")
            .With("export/orders.csv", "a;b\n1;2\n")
            .Build());

        Assert.Equal("export/orders.csv", Assert.Single(cursor.Sheets).Source);
        Assert.Empty(cursor.SkippedEntries);
    }

    [Fact]
    public void NamesEachFileItSkipsAndWhy()
    {
        byte[] ole2 = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, .. new byte[512]];
        byte[] binary = [0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37, .. Enumerable.Repeat((byte)0, 600)];
        byte[] nested = new ZipArchiveBuilder().With("inner.csv", "a;b\n").Build();

        using ArchiveCursor cursor = Open(new ZipArchiveBuilder()
            .With("data.csv", "a;b\n1;2\n")
            .With("legacy.xls", ole2)
            .With("scan.pdf", binary)
            .With("flat.fods", "<?xml version=\"1.0\"?><office:document/>")
            .With("more.zip", nested)
            .WithEncrypted("secret.csv", "a;b\n1;2\n")
            .Build());

        Assert.Equal("data.csv", Assert.Single(cursor.Sheets).Source);
        Assert.Equal(
            [
                new SkippedEntry { Path = "flat.fods", Reason = SkippedEntryReason.XmlDocument },
                new SkippedEntry { Path = "legacy.xls", Reason = SkippedEntryReason.LegacyWorkbook },
                new SkippedEntry { Path = "more.zip", Reason = SkippedEntryReason.NestedArchive },
                new SkippedEntry { Path = "scan.pdf", Reason = SkippedEntryReason.Binary },
                new SkippedEntry { Path = "secret.csv", Reason = SkippedEntryReason.Encrypted },
            ],
            cursor.SkippedEntries);
    }

    [Fact]
    public void ReadsASheetAgainFromItsFirstRowAndCountsItsRepairsOnce()
    {
        // Moving back reopens the file; the repairs of a sheet read twice are counted twice, as they
        // were made twice, but a sheet read once is never counted again by moving past it.
        const string Broken = "a;b\n1;\"unterminated\n2;x\n";
        using ArchiveCursor cursor = Open(new ZipArchiveBuilder().With("one.csv", Broken).With("two.csv", Broken).Build());

        List<string?[]> first = ReadAll(cursor);
        Assert.Equal(1, cursor.Diagnostics.RecoveredUnterminatedQuotes);

        Assert.True(cursor.MoveToSheet(1));
        ReadAll(cursor);
        Assert.Equal(2, cursor.Diagnostics.RecoveredUnterminatedQuotes);

        Assert.True(cursor.MoveToSheet(0));
        Assert.Equal(2, cursor.Diagnostics.RecoveredUnterminatedQuotes);
        Assert.Equal(first, ReadAll(cursor));
        Assert.Equal(3, cursor.Diagnostics.RecoveredUnterminatedQuotes);

        // Moving to the sheet already being read starts it over too.
        Assert.True(cursor.MoveToSheet(0));
        Assert.Equal(first, ReadAll(cursor));
        Assert.False(cursor.MoveToSheet(2));
    }

    [Fact]
    public void RefusesMoreEntriesOrMoreBytesThanItsBudgetAndClosesTheStream()
    {
        byte[] archive = new ZipArchiveBuilder().With("a.csv", "a\n1\n").With("b.csv", "a\n1\n").With("c.csv", new string('x', 5000)).Build();

        TrackingStream entries = new(archive);
        TabularLimitException tooMany = Assert.Throws<TabularLimitException>(() => new ArchiveCursor(
            entries, new TabularOpenOptions { Archive = new ArchiveCursorOptions { MaxEntries = 2 } }, Token));
        Assert.Equal(nameof(ArchiveCursorOptions.MaxEntries), tooMany.Limit);
        Assert.True(entries.WasDisposed);

        TrackingStream bytes = new(archive);
        TabularLimitException tooLarge = Assert.Throws<TabularLimitException>(() => new ArchiveCursor(
            bytes, new TabularOpenOptions { Archive = new ArchiveCursorOptions { MaxUncompressedBytes = 4096 } }, Token));
        Assert.Equal(nameof(ArchiveCursorOptions.MaxUncompressedBytes), tooLarge.Limit);
        Assert.True(bytes.WasDisposed);
    }

    [Fact]
    public void RefusesAnArchiveWithNothingToReadAndClosesTheStream()
    {
        byte[] binary = [0x89, 0x50, 0x4E, 0x47, .. Enumerable.Repeat((byte)0, 600)];
        TrackingStream stream = new(new ZipArchiveBuilder().With("photo.png", binary).With(".DS_Store", "x").Build());

        TabularFormatException error = Assert.Throws<TabularFormatException>(() => new ArchiveCursor(stream, null, Token));

        Assert.Equal(TabularFormatException.Unsupported, error.Code);
        Assert.True(stream.WasDisposed);
    }

    [Fact]
    public void LeavesTheStreamOpenWhenAskedToOnFailureAndOnDispose()
    {
        TrackingStream failing = new(new ZipArchiveBuilder().With("photo.png", [0, 0, 0, 0, 0, 0, 0, 0]).Build());
        Assert.Throws<TabularFormatException>(() => new ArchiveCursor(failing, new TabularOpenOptions { LeaveOpen = true }, Token));
        Assert.False(failing.WasDisposed);

        TrackingStream kept = new(new ZipArchiveBuilder().With("a.csv", "a\n1\n").Build());
        new ArchiveCursor(kept, new TabularOpenOptions { LeaveOpen = true }, Token).Dispose();
        Assert.False(kept.WasDisposed);

        TrackingStream closed = new(new ZipArchiveBuilder().With("a.csv", "a\n1\n").Build());
        new ArchiveCursor(closed, null, Token).Dispose();
        Assert.True(closed.WasDisposed);
    }

    [Fact]
    public void RefusesMoreSheetsThanTheWorkbookCeiling()
    {
        ZipArchiveBuilder builder = new();

        for (int i = 0; i < 4; i++)
        {
            builder.With($"{i}.csv", "a\n1\n");
        }

        TabularLimitException error = Assert.Throws<TabularLimitException>(() => Open(
            builder.Build(), new TabularOpenOptions { Xlsx = new XlsxCursorOptions { MaxSheets = 3 } }));

        Assert.Equal("MaxSheets", error.Limit);
    }

    [Fact]
    public void ReportsProgressAcrossTheSheets()
    {
        string rows = string.Concat(Enumerable.Range(0, 2000).Select(i => $"{i};value {i}\n"));
        using ArchiveCursor cursor = Open(new ZipArchiveBuilder().With("a.csv", "n;v\n" + rows).With("b.csv", "n;v\n" + rows).Build());

        Assert.Equal(0d, cursor.ReadFraction);
        ReadAll(cursor);
        Assert.InRange(cursor.ReadFraction.GetValueOrDefault(), 0.45, 0.55);

        Assert.True(cursor.MoveToSheet(1));
        ReadAll(cursor);
        Assert.Equal(1d, cursor.ReadFraction.GetValueOrDefault(), 3);
    }

    [Fact]
    public void RefusesAnArchiveOptionThatCannotWork()
    {
        byte[] archive = new ZipArchiveBuilder().With("a.csv", "a\n1\n").Build();

        foreach ((ArchiveCursorOptions options, string name) in new (ArchiveCursorOptions, string)[]
        {
            (new ArchiveCursorOptions { MaxEntries = 0 }, nameof(ArchiveCursorOptions.MaxEntries)),
            (new ArchiveCursorOptions { MaxUncompressedBytes = 0 }, nameof(ArchiveCursorOptions.MaxUncompressedBytes)),
            (new ArchiveCursorOptions { MaxEmbeddedWorkbookBytes = 0 }, nameof(ArchiveCursorOptions.MaxEmbeddedWorkbookBytes)),
        })
        {
            ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
                () => Open(archive, new TabularOpenOptions { Archive = options }));
            Assert.Contains(name, error.Message, StringComparison.Ordinal);
        }
    }

    private sealed class TrackingStream(byte[] content) : MemoryStream(content, writable: false)
    {
        public bool WasDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}
