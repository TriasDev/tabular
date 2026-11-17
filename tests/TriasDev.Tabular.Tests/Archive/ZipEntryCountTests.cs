using System.Buffers.Binary;

using TriasDev.Tabular.Archive;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Archive;

/// <summary>
/// A zip's entry count is bounded from its end record, before its directory is walked: a hostile
/// directory used to be read in full once to tell what the zip held, and again by the reader that
/// took it, before any entry bound applied.
/// </summary>
public sealed class ZipEntryCountTests
{
    private const int EndLength = 22;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static ITabularCursor Open(byte[] content, TabularOpenOptions? options = null) =>
        TabularFile.Open(new MemoryStream(content, writable: false), "upload", options, Token);

    private static byte[] Zip(int files)
    {
        ZipArchiveBuilder builder = new();

        for (int i = 0; i < files; i++)
        {
            builder.With($"{i}.csv", "h\n1\n");
        }

        return builder.Build();
    }

    /// <summary>A zip whose end record claims more entries than its directory holds — and so than any walk would find.</summary>
    private static byte[] Claiming(byte[] zip, ushort entries)
    {
        byte[] patched = (byte[])zip.Clone();
        Span<byte> end = patched.AsSpan(patched.Length - EndLength);
        Assert.Equal(0x06054b50u, BinaryPrimitives.ReadUInt32LittleEndian(end));
        BinaryPrimitives.WriteUInt16LittleEndian(end[8..], entries);
        BinaryPrimitives.WriteUInt16LittleEndian(end[10..], entries);
        return patched;
    }

    /// <summary>The same zip, with a comment after its end record, so that the record has to be searched for.</summary>
    private static byte[] WithComment(byte[] zip, int length)
    {
        byte[] commented = [.. zip, .. Enumerable.Repeat((byte)'c', length)];
        BinaryPrimitives.WriteUInt16LittleEndian(commented.AsSpan(zip.Length - EndLength + 20), (ushort)length);
        return commented;
    }

    /// <summary>
    /// A zip64 end record and its locator put before the end record, whose every field is then
    /// saturated, as our own writer and Go's do once anything in the zip passes four gigabytes.
    /// </summary>
    private static byte[] WithZip64Count(byte[] zip, ulong entries)
    {
        int endAt = zip.Length - EndLength;
        byte[] record = new byte[56];
        BinaryPrimitives.WriteUInt32LittleEndian(record, 0x06064b50);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(4), 44);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(12), 45);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(14), 45);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(24), entries);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(32), entries);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(40), BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(endAt + 12)));
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(48), BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(endAt + 16)));

        byte[] locator = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(locator, 0x07064b50);
        BinaryPrimitives.WriteUInt64LittleEndian(locator.AsSpan(8), (ulong)endAt);
        BinaryPrimitives.WriteUInt32LittleEndian(locator.AsSpan(16), 1);

        byte[] result = [.. zip.AsSpan(0, endAt), .. record, .. locator, .. zip.AsSpan(endAt)];
        Span<byte> end = result.AsSpan(result.Length - EndLength);
        BinaryPrimitives.WriteUInt16LittleEndian(end[8..], ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(end[10..], ushort.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(end[12..], uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(end[16..], uint.MaxValue);
        return result;
    }

    [Fact]
    public void RefusesAZipWhoseEndRecordDeclaresMoreEntriesThanAnyReaderAllows()
    {
        // The directory holds one entry, so a walk finds no more than that: the refusal comes from the
        // record. Before, the zip reader found the count wrong, the zip was called a workbook and the
        // workbook reader refused it as corrupt.
        TabularLimitException error = Assert.Throws<TabularLimitException>(() => Open(Claiming(Zip(1), 60_000)));

        Assert.Equal(nameof(ArchiveCursorOptions.MaxEntries), error.Limit);
        Assert.Equal(16_384, error.Maximum);
    }

    [Fact]
    public void FindsTheEndRecordBehindAComment()
    {
        TabularLimitException error = Assert.Throws<TabularLimitException>(() => Open(WithComment(Claiming(Zip(1), 60_000), 300)));

        Assert.Equal(16_384, error.Maximum);

        // An honest zip with a comment still opens.
        using ITabularCursor cursor = Open(WithComment(Zip(2), 300));
        Assert.Equal(2, cursor.Sheets.Count);
    }

    [Fact]
    public void FindsTheEndRecordBehindTheLongestCommentAZipCanHave()
    {
        // A comment of 65,535 bytes puts the record at the very start of the tail searched for it.
        TabularLimitException error = Assert.Throws<TabularLimitException>(() => Open(WithComment(Claiming(Zip(1), 60_000), ushort.MaxValue)));

        Assert.Equal(16_384, error.Maximum);
    }

    private static long? Declared(byte[] zip) => ZipEndRecord.DeclaredEntries(new MemoryStream(zip, writable: false));

    /// <summary>The zip's end record with its entry counts set, and nothing else changed.</summary>
    private static byte[] Counting(byte[] zip, ushort entries)
    {
        byte[] patched = (byte[])zip.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(patched.AsSpan(patched.Length - EndLength + 8), entries);
        BinaryPrimitives.WriteUInt16LittleEndian(patched.AsSpan(patched.Length - EndLength + 10), entries);
        return patched;
    }

    [Fact]
    public void TakesTheLargerCountWhenAPlainCountContradictsTheZip64Record()
    {
        Assert.Equal(100_000, Declared(Counting(WithZip64Count(Zip(1), 100_000), 2)));
        Assert.Equal(7, Declared(Counting(WithZip64Count(Zip(1), 2), 7)));
    }

    [Fact]
    public void SaturatesAZip64CountPastTheLargestLong()
    {
        Assert.Equal(long.MaxValue, Declared(WithZip64Count(Zip(1), ulong.MaxValue)));
    }

    [Fact]
    public void IgnoresAZip64LocatorThatPointsPastTheLargestLong()
    {
        // Room enough before the end record for a zip64 record, and a locator whose offset, read as a
        // signed position, is negative: the plain count stands.
        byte[] zip = Counting(WithZip64Count(Zip(1), 5), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(zip.AsSpan(zip.Length - EndLength - 20 + 8), 0x8000000000000010UL);

        Assert.Equal(1, Declared(zip));
    }

    [Fact]
    public void IgnoresALocatorThatPointsAtNoZip64Record()
    {
        byte[] zip = Counting(WithZip64Count(Zip(1), 1_000_000), 1);
        zip[zip.Length - EndLength - 20 - 56] = 0;

        Assert.Equal(1, Declared(zip));
    }

    [Fact]
    public void IgnoresAZip64RecordThatWouldOverlapItsLocator()
    {
        // A forged record 40 bytes before the end record: its count would be read from the locator's
        // own bytes. A record and its locator take 76 bytes, so a record that starts later is no record.
        const int End = 200;
        byte[] file = new byte[End + EndLength];
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(End - 40), 0x06064b50);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(End - 20), 0x07064b50);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(End - 20 + 8), End - 40);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(End - 20 + 16), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(End), 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(End + 8), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(End + 10), 3);

        Assert.Equal(3, Declared(file));
    }

    [Fact]
    public void ReadsTheCountOfAZip64EndRecord()
    {
        TabularLimitException error = Assert.Throws<TabularLimitException>(() => Open(WithZip64Count(Zip(1), 1_000_000)));

        Assert.Equal(nameof(ArchiveCursorOptions.MaxEntries), error.Limit);
    }

    [Fact]
    public void OpensAnHonestZip64WhoseEndRecordIsSaturated()
    {
        // The saturated count is the sentinel for "see the zip64 record", not a count: taking the larger
        // of the two refused every zip64 file as declaring 65,535 entries.
        using ITabularCursor cursor = Open(WithZip64Count(Zip(2), 2));

        Assert.Equal(2, cursor.Sheets.Count);
    }

    [Fact]
    public async Task ReadsBackAZip64FileOurOwnWriterWrote()
    {
        // The writer goes zip64 past four gigabytes; lowered, the threshold makes a small file take the
        // same form — a zip64 record, and an end record with every field saturated.
        using SpillBuffer buffer = new();
        ZipWriter zip = new(buffer, System.IO.Compression.CompressionLevel.Fastest, zip64Threshold: 10);
        zip.AddStored("a.csv", "h\nfirst\n"u8);

        Stream entry = zip.BeginDeflated("b.csv");
        entry.Write("h\nsecond row, deflated\n"u8);
        zip.EndEntry();
        zip.Complete();

        using MemoryStream file = new();
        await buffer.DrainToAsync(file, Token);
        byte[] bytes = file.ToArray();
        Assert.Equal(ushort.MaxValue, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(bytes.Length - EndLength + 10)));

        using ITabularCursor cursor = Open(bytes);
        Assert.Equal(["a.csv", "b.csv"], cursor.Sheets.Select(s => s.Source));
        Assert.True(cursor.MoveToSheet(1, Token));
        Assert.True(cursor.ReadRow(Token));
        Assert.True(cursor.ReadRow(Token));
        Assert.Equal("second row, deflated", cursor.CurrentRow[0].AsText());
    }

    [Fact]
    public void RefusesATinyFileWithAForgedZip64LocatorAsATabularException()
    {
        // 46 bytes: a zip signature, a zip64 locator pointing past 2^63, an end record. The end record
        // starts too early for a zip64 record to stand before it, and the locator's offset once became
        // a negative position — an ArgumentOutOfRangeException out of the library.
        byte[] file = new byte[4 + 20 + EndLength];
        "PK\u0003\u0004"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 0x07064b50);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(12), 0x8000000000000010UL);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(24), 0x06054b50);

        Assert.ThrowsAny<TabularException>(() => Open(file));
    }

    [Fact]
    public void BoundsAZipByTheLargestBoundOfTheReadersThatCouldTakeIt()
    {
        byte[] archive = Zip(5);
        TabularOpenOptions tight = new()
        {
            Archive = new ArchiveCursorOptions { MaxEntries = 3 },
            Xlsx = new XlsxCursorOptions { MaxPackageEntries = 3 },
            Ods = new OdsCursorOptions { MaxPackageEntries = 4 },
        };

        // Five entries are more than any of the three readers takes, so none walks the directory.
        TabularLimitException error = Assert.Throws<TabularLimitException>(() => Open(archive, tight));
        Assert.Equal(nameof(OdsCursorOptions.MaxPackageEntries), error.Limit);
        Assert.Equal(4, error.Maximum);

        // An archive of five files is allowed once the archive's own bound allows it, whatever the
        // workbook readers' bounds are.
        using ITabularCursor cursor = Open(archive, tight with { Archive = new ArchiveCursorOptions { MaxEntries = 5 } });
        Assert.Equal(5, cursor.Sheets.Count);
    }

    [Fact]
    public void RefusesAnArchiveOpenedDirectlyWhoseEndRecordDeclaresTooManyFiles()
    {
        TabularLimitException error = Assert.Throws<TabularLimitException>(
            () => new ArchiveCursor(new MemoryStream(Claiming(Zip(1), 60_000), writable: false), null, Token));

        Assert.Equal(nameof(ArchiveCursorOptions.MaxEntries), error.Limit);
    }

    [Fact]
    public void RefusesAZipInsideAnArchiveWhoseEndRecordDeclaresTooManyEntries()
    {
        // A bound a file inside exceeds fails the archive, as every bound does; it is never a skip.
        byte[] archive = new ZipArchiveBuilder().With("a.csv", "h\n1\n").With("inner.zip", Claiming(Zip(1), 60_000)).Build();

        TabularLimitException error = Assert.Throws<TabularLimitException>(() => Open(archive));
        Assert.Equal(16_384, error.Maximum);
    }

    /// <summary>
    /// While an archive is listed its own limits apply (#184): a plain zip inside it with more entries
    /// than a workbook may hold, but within the archive's raised bound, is classified, found to be an
    /// archive and skipped as one — not refused under a workbook's bound it never had to meet.
    /// </summary>
    [Fact]
    public void ClassifiesAZipInsideAnArchiveUnderTheArchivesOwnBound()
    {
        byte[] archive = new ZipArchiveBuilder().With("a.csv", "h\n1\n").With("inner.zip", Zip(5)).Build();
        TabularOpenOptions options = new()
        {
            Archive = new ArchiveCursorOptions { MaxEntries = 10 },
            Xlsx = new XlsxCursorOptions { MaxPackageEntries = 3 },
            Ods = new OdsCursorOptions { MaxPackageEntries = 3 },
        };

        using ITabularCursor cursor = Open(archive, options);

        Assert.Equal(["a.csv"], cursor.Sheets.Select(s => s.Source));
        Assert.Equal(new SkippedEntry { Path = "inner.zip", Reason = SkippedEntryReason.NestedArchive }, Assert.Single(cursor.SkippedEntries));
        Assert.True(cursor.ReadRow(Token));
        Assert.True(cursor.ReadRow(Token));
        Assert.Equal("1", cursor.CurrentRow[0].AsText());
    }

    /// <summary>
    /// The other half of the same decision: a zip inside an archive that turns out a workbook is held to
    /// its own format's <c>MaxPackageEntries</c> once it is opened, however far the archive's bound was
    /// raised — and a bound fails the archive, never a skip.
    /// </summary>
    [Fact]
    public void HoldsAWorkbookInsideAnArchiveToItsOwnPackageBoundOnceOpened()
    {
        byte[] xlsx = new XlsxPackage().WithSheet("S", "<row r=\"1\"><c t=\"inlineStr\"><is><t>h</t></is></c></row>").Build();
        byte[] ods = new OdsPackage().WithTable("S", "<table:table-row><table:table-cell office:value-type=\"string\"><text:p>h</text:p></table:table-cell></table:table-row>").Build();

        foreach ((string path, byte[] workbook, string limit) in new[]
        {
            ("book.xlsx", xlsx, nameof(XlsxCursorOptions.MaxPackageEntries)),
            ("table.ods", ods, nameof(OdsCursorOptions.MaxPackageEntries)),
        })
        {
            int parts = EntryCount(workbook);
            byte[] archive = new ZipArchiveBuilder().With("a.csv", "h\n1\n").With(path, workbook).Build();
            TabularOpenOptions options = new()
            {
                Archive = new ArchiveCursorOptions { MaxEntries = 10 * parts },
                Xlsx = new XlsxCursorOptions { MaxPackageEntries = parts - 1 },
                Ods = new OdsCursorOptions { MaxPackageEntries = parts - 1 },
            };

            TabularLimitException error = Assert.Throws<TabularLimitException>(() => Open(archive, options));
            Assert.Equal(limit, error.Limit);
            Assert.Equal(parts - 1, error.Maximum);

            // At its own bound, the same workbook is read.
            using ITabularCursor cursor = Open(archive, options with
            {
                Xlsx = new XlsxCursorOptions { MaxPackageEntries = parts },
                Ods = new OdsCursorOptions { MaxPackageEntries = parts },
            });
            Assert.Equal(["a.csv", path], cursor.Sheets.Select(s => s.Source));
        }
    }

    private static int EntryCount(byte[] zip)
    {
        using System.IO.Compression.ZipArchive archive = new(new MemoryStream(zip, writable: false), System.IO.Compression.ZipArchiveMode.Read);
        return archive.Entries.Count;
    }

    [Fact]
    public void RefusesAZipInsideAGzipFileWhoseEndRecordDeclaresTooManyEntries()
    {
        // A gzip file holds a workbook or nothing readable, so the archive's bound plays no part.
        TabularLimitException error = Assert.Throws<TabularLimitException>(
            () => Open(GzipFile.Of(Claiming(Zip(1), 60_000)), new TabularOpenOptions { Archive = new ArchiveCursorOptions { MaxEntries = 100_000 } }));

        Assert.Equal(nameof(XlsxCursorOptions.MaxPackageEntries), error.Limit);
    }
}
