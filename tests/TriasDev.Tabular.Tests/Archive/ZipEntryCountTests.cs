using System.Buffers.Binary;

using TriasDev.Tabular.Archive;
using TriasDev.Tabular.Ods;
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

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

    /// <summary>A zip64 end record and its locator put before the end record, which saturates its count as a zip64 writer does.</summary>
    private static byte[] WithZip64Count(byte[] zip, ulong entries)
    {
        int endAt = zip.Length - EndLength;
        byte[] record = new byte[56];
        BinaryPrimitives.WriteUInt32LittleEndian(record, 0x06064b50);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(4), 44);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(24), entries);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(32), entries);

        byte[] locator = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(locator, 0x07064b50);
        BinaryPrimitives.WriteUInt64LittleEndian(locator.AsSpan(8), (ulong)endAt);
        BinaryPrimitives.WriteUInt32LittleEndian(locator.AsSpan(16), 1);

        byte[] result = [.. zip.AsSpan(0, endAt), .. record, .. locator, .. zip.AsSpan(endAt)];
        Span<byte> end = result.AsSpan(result.Length - EndLength);
        BinaryPrimitives.WriteUInt16LittleEndian(end[8..], ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(end[10..], ushort.MaxValue);
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
    public void ReadsTheCountOfAZip64EndRecord()
    {
        TabularLimitException error = Assert.Throws<TabularLimitException>(() => Open(WithZip64Count(Zip(1), 1_000_000)));

        Assert.Equal(nameof(ArchiveCursorOptions.MaxEntries), error.Limit);
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

    [Fact]
    public void RefusesAZipInsideAGzipFileWhoseEndRecordDeclaresTooManyEntries()
    {
        // A gzip file holds a workbook or nothing readable, so the archive's bound plays no part.
        TabularLimitException error = Assert.Throws<TabularLimitException>(
            () => Open(GzipFile.Of(Claiming(Zip(1), 60_000)), new TabularOpenOptions { Archive = new ArchiveCursorOptions { MaxEntries = 100_000 } }));

        Assert.Equal(nameof(XlsxCursorOptions.MaxPackageEntries), error.Limit);
    }
}
