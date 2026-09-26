using System.IO.Compression;
using System.Text;

using TriasDev.Tabular.Archive;
using TriasDev.Tabular.Ods;
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Archive;

/// <summary>Workbooks inside a zip archive: their sheets join the archive's, each under its file's path.</summary>
public sealed class ArchiveCursorWorkbookTests
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

    private static string InlineRow(int number, params string[] values) =>
        $"<row r=\"{number}\">" + string.Concat(values.Select(v => $"<c t=\"inlineStr\"><is><t>{v}</t></is></c>")) + "</row>";

    private static byte[] Workbook(params (string Sheet, string Rows)[] sheets)
    {
        XlsxPackage package = new();

        foreach ((string sheet, string rows) in sheets)
        {
            package.WithSheet(sheet, rows);
        }

        return package.Build();
    }

    private static byte[] Spreadsheet(string table, string text) =>
        new OdsPackage().WithTable(table,
            $"<table:table-row><table:table-cell office:value-type=\"string\"><text:p>{text}</text:p></table:table-cell></table:table-row>").Build();

    [Fact]
    public void ListsTheSheetsOfEveryFileUnderItsPath()
    {
        byte[] xlsx = Workbook(("S1", InlineRow(1, "x1")), ("S2", InlineRow(1, "x2") + InlineRow(2, "y2")));
        byte[] ods = Spreadsheet("T1", "o1");

        using ArchiveCursor cursor = Open(new ZipArchiveBuilder()
            .With("c.ods", ods)
            .With("b.xlsx", xlsx)
            .With("a.csv", "h\nc1\n")
            .Build());

        Assert.Equal(["a", "S1", "S2", "T1"], cursor.Sheets.Select(s => s.Name));
        Assert.Equal(["a.csv", "b.xlsx", "b.xlsx", "c.ods"], cursor.Sheets.Select(s => s.Source));
        Assert.Equal([TabularFormat.Csv, TabularFormat.Xlsx, TabularFormat.Xlsx, TabularFormat.Ods], cursor.Sheets.Select(s => s.Format));

        using XlsxCursor plain = new(new MemoryStream(xlsx));
        Assert.True(plain.MoveToSheet(1));

        Assert.True(cursor.MoveToSheet(2));
        Assert.Null(cursor.Dialect);
        Assert.Equal(ReadAll(plain), ReadAll(cursor));

        Assert.True(cursor.MoveToSheet(3));
        Assert.Equal("o1", Assert.Single(ReadAll(cursor))[0]);
    }

    [Fact]
    public void KeepsTwoSheetsOfTheSameNameApartByTheirFiles()
    {
        using ArchiveCursor cursor = Open(new ZipArchiveBuilder()
            .With("2025/report.xlsx", Workbook(("Sheet1", InlineRow(1, "old"))))
            .With("2026/report.xlsx", Workbook(("Sheet1", InlineRow(1, "new"))))
            .Build());

        Assert.Equal(["Sheet1", "Sheet1"], cursor.Sheets.Select(s => s.Name));
        Assert.Equal(["2025/report.xlsx", "2026/report.xlsx"], cursor.Sheets.Select(s => s.Source));
        Assert.Equal("old", Assert.Single(ReadAll(cursor))[0]);
        Assert.True(cursor.MoveToSheet(1));
        Assert.Equal("new", Assert.Single(ReadAll(cursor))[0]);
    }

    [Fact]
    public void ReadsAWorkbookLargerThanOnePieceOfItsBuffer()
    {
        // Values that do not compress, so the workbook itself is well over a megabyte and its
        // directory, at the end, lies in another piece of the buffer than its first parts.
        Random random = new(7);
        StringBuilder rows = new();

        for (int r = 1; r <= 450; r++)
        {
            string value = new([.. Enumerable.Range(0, 6000).Select(_ => (char)('a' + random.Next(26)))]);
            rows.Append(InlineRow(r, value, r.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        byte[] xlsx = Workbook(("Big", rows.ToString()));
        Assert.True(xlsx.Length > (1024 * 1024) + 200_000, $"The fixture is {xlsx.Length} bytes.");

        using ArchiveCursor cursor = Open(new ZipArchiveBuilder().With("big.xlsx", xlsx, CompressionLevel.NoCompression).Build());
        using XlsxCursor plain = new(new MemoryStream(xlsx));

        Assert.Equal(ReadAll(plain), ReadAll(cursor));
    }

    [Fact]
    public void RefusesAWorkbookLargerThanItsBudgetAndClosesTheStream()
    {
        byte[] archive = new ZipArchiveBuilder().With("a.csv", "h\n1\n").With("b.xlsx", Workbook(("S", InlineRow(1, "x")))).Build();
        TrackingStream stream = new(archive);

        TabularLimitException error = Assert.Throws<TabularLimitException>(() => new ArchiveCursor(
            stream, new TabularOpenOptions { Archive = new ArchiveCursorOptions { MaxEmbeddedWorkbookBytes = 200 } }, Token));

        Assert.Equal(nameof(ArchiveCursorOptions.MaxEmbeddedWorkbookBytes), error.Limit);
        Assert.True(stream.WasDisposed);
    }

    [Fact]
    public void ReadsAWorkbookBudgetPastWhatOneArrayHolds()
    {
        // The budget is a long; setting it beyond two gigabytes is legal and reads as usual.
        using ArchiveCursor cursor = Open(
            new ZipArchiveBuilder().With("b.xlsx", Workbook(("S", InlineRow(1, "x")))).Build(),
            new TabularOpenOptions { Archive = new ArchiveCursorOptions { MaxEmbeddedWorkbookBytes = 10L * 1024 * 1024 * 1024 } });

        Assert.Equal("x", Assert.Single(ReadAll(cursor))[0]);
    }

    [Fact]
    public void SkipsWorkbooksItCannotReadAndSaysWhy()
    {
        byte[] odt = Zip(("mimetype", "application/vnd.oasis.opendocument.text"), ("content.xml", "<office:document-content/>"));
        byte[] corrupt = Zip(("[Content_Types].xml", "<Types/>"));
        byte[] xlsb = Zip(
            ("[Content_Types].xml", "<Types/>"),
            ("_rels/.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.bin"/></Relationships>"""),
            ("xl/workbook.bin", "\u0083\u0001\u0000"));

        using ArchiveCursor cursor = Open(new ZipArchiveBuilder()
            .With("notes.odt", odt)
            .With("broken.xlsx", corrupt)
            .With("binary.xlsb", xlsb)
            .With("data.csv", "h\n1\n")
            .Build());

        Assert.Equal("data.csv", Assert.Single(cursor.Sheets).Source);
        Assert.Equal(
            [
                new SkippedEntry { Path = "binary.xlsb", Reason = SkippedEntryReason.Unsupported },
                new SkippedEntry { Path = "broken.xlsx", Reason = SkippedEntryReason.Unreadable },
                new SkippedEntry { Path = "notes.odt", Reason = SkippedEntryReason.OtherDocument },
            ],
            cursor.SkippedEntries);
    }

    [Fact]
    public void SkipsAWorkbookDamagedPastItsHeadInsteadOfFailingTheArchive()
    {
        // A deflate stream written by hand, so the damage is exact: two stored blocks the head is read
        // from, then a block of the reserved type, which every inflater refuses. The damage is met
        // only when the whole workbook is copied.
        byte[] first = new byte[65_535];
        "PK\u0003\u0004"u8.CopyTo(first);
        byte[] deflate = [.. StoredBlock(first), .. StoredBlock(new byte[10_000]), 0x07];

        byte[] archive = new ZipArchiveBuilder()
            .With("big.xlsx", new byte[deflate.Length], CompressionLevel.NoCompression)
            .With("data.csv", "h\n1\n")
            .Build();
        ReplaceWithDeflate(archive, "big.xlsx", deflate, uncompressedSize: 80_000);

        using ArchiveCursor cursor = Open(archive);

        Assert.Equal("data.csv", Assert.Single(cursor.Sheets).Source);
        Assert.Equal(new SkippedEntry { Path = "big.xlsx", Reason = SkippedEntryReason.Unreadable }, Assert.Single(cursor.SkippedEntries));
    }

    private static byte[] StoredBlock(byte[] content)
    {
        ushort length = (ushort)content.Length;
        return [0x00, (byte)length, (byte)(length >> 8), (byte)~length, (byte)(~length >> 8), .. content];
    }

    /// <summary>
    /// Turns a stored entry into a deflated one with the given data, which must be as long as the
    /// stored content was: the method and the uncompressed size change in both headers.
    /// </summary>
    private static void ReplaceWithDeflate(byte[] zip, string name, byte[] deflate, int uncompressedSize)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);

        for (int i = 0; i + 46 <= zip.Length; i++)
        {
            if (zip.AsSpan(i, 4).SequenceEqual("PK\u0003\u0004"u8) && zip.AsSpan(i + 30, nameBytes.Length).SequenceEqual(nameBytes))
            {
                zip[i + 8] = 8;
                BitConverter.GetBytes(uncompressedSize).CopyTo(zip, i + 22);
                int dataAt = i + 30 + nameBytes.Length + (zip[i + 28] | (zip[i + 29] << 8));
                deflate.CopyTo(zip, dataAt);
            }
            else if (zip.AsSpan(i, 4).SequenceEqual("PK\u0001\u0002"u8) && zip.AsSpan(i + 46, nameBytes.Length).SequenceEqual(nameBytes))
            {
                zip[i + 10] = 8;
                BitConverter.GetBytes(uncompressedSize).CopyTo(zip, i + 24);
            }
        }
    }

    [Fact]
    public void MovesBetweenTheSheetsOfOneWorkbookAndAwayAndBack()
    {
        byte[] xlsx = Workbook(("S1", InlineRow(1, "a") + InlineRow(2, "b")), ("S2", InlineRow(1, "c")));

        using ArchiveCursor cursor = Open(new ZipArchiveBuilder().With("a.csv", "h\n1\n").With("b.xlsx", xlsx).Build());

        Assert.True(cursor.MoveToSheet(1));
        Assert.True(cursor.ReadRow(Token));
        Assert.True(cursor.MoveToSheet(2));
        Assert.Equal("c", Assert.Single(ReadAll(cursor))[0]);
        Assert.True(cursor.MoveToSheet(0));
        Assert.Equal(2, ReadAll(cursor).Count);
        Assert.True(cursor.MoveToSheet(1));
        Assert.Equal([["a"], ["b"]], ReadAll(cursor));
        Assert.Equal(1, cursor.CurrentSheetIndex);
    }

    [Fact]
    public void RefusesMoreSheetsInItsWorkbooksThanTheCeiling()
    {
        byte[] xlsx = Workbook(("S1", InlineRow(1, "a")), ("S2", InlineRow(1, "b")), ("S3", InlineRow(1, "c")));

        TabularLimitException error = Assert.Throws<TabularLimitException>(() => Open(
            new ZipArchiveBuilder().With("a.csv", "h\n1\n").With("b.xlsx", xlsx).Build(),
            new TabularOpenOptions { Xlsx = new XlsxCursorOptions { MaxSheets = 3 } }));

        Assert.Equal("MaxSheets", error.Limit);
    }

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        ZipArchiveBuilder builder = new();

        foreach ((string name, string content) in entries)
        {
            builder.With(name, content);
        }

        return builder.Build();
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
