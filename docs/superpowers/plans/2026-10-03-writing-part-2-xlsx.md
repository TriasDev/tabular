# Writing — part 2: the zip writer and xlsx — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `TabularWriter.Create(stream, TabularFormat.Xlsx)` writes a streaming xlsx workbook — several sheets, typed cells, flat memory — that the library's own import reads back as the same values with the same types, and that a strict OOXML validator accepts.

**Architecture:** A new internal `ZipWriter` writes the package itself (`ZipArchive` cannot: LibreOffice refuses its streamed output, and with seek it buffers whole entries): small parts stored with sizes in the local header, each sheet streamed through `DeflateStream` with a data descriptor, zip64 when sizes or offsets pass 4 GB, its own CRC-32. `XlsxSheetWriter` implements the existing internal `ISheetWriter`: each sheet is one deflated entry written row by row, inline strings, four fixed styles; the workbook, relationships, content types and styles are written at `Complete`. A shared `RowText` buffer (extracted from the csv writer) formats each row without allocating.

**Tech Stack:** .NET 8 + 10, base class library only (`System.IO.Compression.DeflateStream`, `System.Buffers`), xunit v4 on Microsoft.Testing.Platform; the test project gains `DocumentFormat.OpenXml` for validation only.

**Spec:** `docs/superpowers/specs/2026-10-03-writing-design.md` — delivery step 2 of 5 (issue #71). Part 1 (csv, `TabularWriter`) is merged (#77); read its plan `docs/superpowers/plans/2026-10-03-writing-part-1-csv.md` only for history.

## Global Constraints

- No package references in the library; only the base class library (`TabularIndependenceTests`). The test project may reference `DocumentFormat.OpenXml` (3.5.1, the version the comparison project already uses).
- The writer never writes to, flushes or disposes the target stream synchronously; everything the format writers produce goes into `SpillBuffer`.
- No allocation per row or per cell on the write path. Per-sheet and per-file allocations are fine.
- Error codes, never messages: value problems are `ErrorCodes.Write.*` codes returned by the sheet writer and raised by `TabularWriter` as `TabularWriteException`; programmer errors are `ArgumentException` / `InvalidOperationException`.
- Options are checked where they are handed over (`TabularWriter.Create`).
- Public API changes go in `src/TriasDev.Tabular/PublicAPI.Unshipped.txt` (RS0016 messages are authoritative).
- `dotnet format TriasDev.Tabular.slnx --verify-no-changes` must pass before every commit — CI runs it (part 1's PR failed on it once).
- Strings holding unpaired surrogates never go into attribute arguments (`[InlineData]`): the compiler stores them as UTF-8 and they become U+FFFD. Build such strings in method bodies.
- Tests use the public API, except the internal units `Crc32`, `ZipWriter`, `RowText`, `SheetNames` (InternalsVisibleTo is set).
- Nothing product-specific in code, docs or commits.

## Rulings made while planning (deviations from or additions to the spec)

- **Small parts are stored (method 0), not deflated in memory.** The spec allowed either; content types, relationships, workbook and styles are a few kilobytes, and storing them removes a second deflate path. Cost: a workbook a few kilobytes larger.
- **A fourth style, `numFmtId 1` (`0`), for integers.** The spec named three (General, date, date-time). In General, Excel shows an integer of 12 or more digits — an id — as `1.23457E+11`. The reader treats format 1 as a number, so the round trip is unchanged.
- **Streamed entries carry no zip64 extra in the local header**; their data descriptor uses 8-byte sizes once a size passes the zip64 threshold, and the central directory carries the zip64 extra. Whether Excel and LibreOffice accept that past 4 GB is tested in part 5, as the spec says.
- **`ZipWriter` takes its zip64 threshold as a constructor parameter** (default `uint.MaxValue`), so tests exercise every zip64 structure with small files.
- **Fixed entry timestamp, 1980-01-01 00:00** (DOS date `0x0021`, time `0`): output is byte-for-byte reproducible.
- **Default compression `CompressionLevel.Fastest`**, provisionally — the library's goal is speed; part 5 benchmarks `Fastest` against `Optimal` and settles the default.
- **Sheet names are checked by `TabularWriter`** for every format that names sheets (new `ISheetWriter.NamesSheets`), with Excel's rules from the spec, so part 3 (ods) inherits them.
- **`RowText`** is extracted from `CsvSheetWriter` into `Writing/` before the xlsx writer is built, so the two writers share one row buffer rather than duplicate it.

## Review Focus

1. **A sheet name with XML's special characters** (`R&D <2026>`): must be escaped in `workbook.xml`, and the cursor must give it back. → Task 5, `NamesSheetsAsWrittenEvenWithXmlCharacters`.
2. **A workbook abandoned mid-write after a flush** (client went away): the bytes already sent must not open as a valid workbook. → Task 5, `AWorkbookDisposedBeforeCompletingDoesNotOpen`.
3. **Text with `_x` in it** (a path `C:\_x86_\`, a literal `_x0041_`): must come back as written, not decoded. → Task 5, `WritesUnderscoreXTextAsWritten`.
4. **A sheet of a million rows written with flushes**: memory stays bounded and the cursor reads the last row. → Task 7, `AMillionRowSheetReadsBackToItsLastRow` (one column, to `WriteTarget` with flush-on-recommend).
5. **Dates either side of Excel's 1900 leap-year bug** (1900-02-28 23:59:59.999, 1900-03-01 00:00): exact both ways. → Task 6, `ReadsBackDatesAcrossTheLeapYearBug`.

---

### Task 1: CRC-32

**Files:**
- Create: `src/TriasDev.Tabular/Writing/Crc32.cs`
- Test: `tests/TriasDev.Tabular.Tests/Writing/Crc32Tests.cs`

**Interfaces:**
- Produces (internal, namespace `TriasDev.Tabular`): `static class Crc32` with `uint Compute(ReadOnlySpan<byte> data)` and `uint Update(uint crc, ReadOnlySpan<byte> data)` — `Update(Update(0, a), b) == Compute(a ++ b)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/TriasDev.Tabular.Tests/Writing/Crc32Tests.cs`:

```csharp
using System.Text;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>The zip checksum, checked against the standard's vectors and a bitwise reference.</summary>
public sealed class Crc32Tests
{
    /// <summary>The textbook bit-at-a-time CRC-32, independent of the table-driven one under test.</summary>
    private static uint Reference(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;

        foreach (byte b in data)
        {
            crc ^= b;

            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
        }

        return ~crc;
    }

    [Theory]
    [InlineData("", 0x00000000u)]
    [InlineData("a", 0xE8B7BE43u)]
    [InlineData("123456789", 0xCBF43926u)]
    [InlineData("The quick brown fox jumps over the lazy dog", 0x414FA339u)]
    public void MatchesTheStandardVectors(string text, uint expected)
    {
        Assert.Equal(expected, Crc32.Compute(Encoding.ASCII.GetBytes(text)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(4_096)]
    [InlineData(100_003)]
    public void AgreesWithTheBitwiseReference(int length)
    {
        byte[] data = new byte[length];
        new Random(length).NextBytes(data);

        Assert.Equal(Reference(data), Crc32.Compute(data));
    }

    [Fact]
    public void UpdatesInPiecesAsInOne()
    {
        byte[] data = new byte[10_000];
        new Random(71).NextBytes(data);

        uint pieces = 0;
        int offset = 0;

        // Uneven pieces, so the eight-byte steps start mid-word in some of them.
        foreach (int size in new[] { 1, 3, 8, 15, 1000, 8973 })
        {
            pieces = Crc32.Update(pieces, data.AsSpan(offset, size));
            offset += size;
        }

        Assert.Equal(data.Length, offset);
        Assert.Equal(Crc32.Compute(data), pieces);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~Crc32Tests"`
Expected: build FAIL — `Crc32` does not exist.

- [ ] **Step 3: Implement**

Create `src/TriasDev.Tabular/Writing/Crc32.cs`:

```csharp
using System.Buffers.Binary;

namespace TriasDev.Tabular;

/// <summary>
/// The CRC-32 a zip entry carries (IEEE 802.3, reflected, polynomial 0xEDB88320).
/// </summary>
/// <remarks>
/// Our own, because <c>System.IO.Hashing</c> is a package and the library takes none. Slicing by
/// eight — eight bytes per step through eight tables — so that checksumming a sheet keeps up with
/// deflating it.
/// </remarks>
internal static class Crc32
{
    private static readonly uint[] Table = Build();

    /// <summary>The checksum of the data.</summary>
    public static uint Compute(ReadOnlySpan<byte> data) => Update(0, data);

    /// <summary>The checksum of what <paramref name="crc"/> covered followed by the data.</summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        uint[] t = Table;
        crc = ~crc;

        while (data.Length >= 8)
        {
            uint one = BinaryPrimitives.ReadUInt32LittleEndian(data) ^ crc;
            uint two = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);

            crc = t[(7 * 256) + (one & 0xFF)]
                ^ t[(6 * 256) + ((one >> 8) & 0xFF)]
                ^ t[(5 * 256) + ((one >> 16) & 0xFF)]
                ^ t[(4 * 256) + (one >> 24)]
                ^ t[(3 * 256) + (two & 0xFF)]
                ^ t[(2 * 256) + ((two >> 8) & 0xFF)]
                ^ t[256 + ((two >> 16) & 0xFF)]
                ^ t[two >> 24];

            data = data[8..];
        }

        foreach (byte b in data)
        {
            crc = t[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] Build()
    {
        uint[] table = new uint[8 * 256];

        for (uint i = 0; i < 256; i++)
        {
            uint c = i;

            for (int bit = 0; bit < 8; bit++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        for (int i = 0; i < 256; i++)
        {
            for (int slice = 1; slice < 8; slice++)
            {
                uint previous = table[((slice - 1) * 256) + i];
                table[(slice * 256) + i] = (previous >> 8) ^ table[previous & 0xFF];
            }
        }

        return table;
    }
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~Crc32Tests"`
Expected: PASS on net8.0 and net10.0.

- [ ] **Step 5: Commit**

```bash
dotnet format TriasDev.Tabular.slnx --verify-no-changes
git add src/TriasDev.Tabular/Writing/Crc32.cs tests/TriasDev.Tabular.Tests/Writing/Crc32Tests.cs
git commit -m "feat(write): a slicing-by-8 CRC-32 for zip entries"
```

---

### Task 2: `ZipWriter`

**Files:**
- Create: `src/TriasDev.Tabular/Writing/ZipWriter.cs`
- Test: `tests/TriasDev.Tabular.Tests/Writing/ZipWriterTests.cs`

**Interfaces:**
- Consumes: `SpillBuffer` (`Write(ReadOnlySpan<byte>)`, `TotalWritten`, `DrainToAsync`), `Crc32` (Task 1).
- Produces (internal, namespace `TriasDev.Tabular`): `sealed class ZipWriter(SpillBuffer output, CompressionLevel level, long zip64Threshold = uint.MaxValue)` with `void AddStored(string name, ReadOnlySpan<byte> content)`, `Stream BeginDeflated(string name)` (returns a write-only stream; write the entry's uncompressed bytes into it), `void EndEntry()`, `void Complete()`. Entry names are ASCII. One streamed entry may be open at a time; `AddStored` and `Complete` refuse while one is (`InvalidOperationException`).

- [ ] **Step 1: Write the failing tests**

Create `tests/TriasDev.Tabular.Tests/Writing/ZipWriterTests.cs`:

```csharp
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>
/// The zip the workbook writers stream: what any zip reader opens, laid out as LibreOffice and Excel
/// need it.
/// </summary>
public sealed class ZipWriterTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<byte[]> Build(Action<ZipWriter> write, long zip64Threshold = uint.MaxValue)
    {
        using SpillBuffer buffer = new();
        ZipWriter zip = new(buffer, CompressionLevel.Fastest, zip64Threshold);
        write(zip);

        using MemoryStream target = new();
        await buffer.DrainToAsync(target, Token);
        return target.ToArray();
    }

    private static Dictionary<string, byte[]> Read(byte[] file)
    {
        using ZipArchive archive = new(new MemoryStream(file, writable: false), ZipArchiveMode.Read);
        Dictionary<string, byte[]> entries = [];

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            using Stream stream = entry.Open();
            using MemoryStream content = new();
            stream.CopyTo(content);
            entries[entry.FullName] = content.ToArray();
        }

        return entries;
    }

    private static void Deflated(ZipWriter zip, string name, byte[] content)
    {
        Stream entry = zip.BeginDeflated(name);
        entry.Write(content);
        zip.EndEntry();
    }

    [Fact]
    public async Task WritesEntriesAnyZipReaderOpens()
    {
        byte[] large = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 50_000).Select(i => $"<row r=\"{i}\"/>")));

        byte[] file = await Build(zip =>
        {
            zip.AddStored("mimetype", "application/vnd.oasis.opendocument.spreadsheet"u8);
            Deflated(zip, "content.xml", large);
            zip.AddStored("META-INF/manifest.xml", "<manifest/>"u8);
            zip.Complete();
        });

        Dictionary<string, byte[]> entries = Read(file);

        Assert.Equal(["mimetype", "content.xml", "META-INF/manifest.xml"], entries.Keys);
        Assert.Equal("application/vnd.oasis.opendocument.spreadsheet"u8.ToArray(), entries["mimetype"]);
        Assert.Equal(large, entries["content.xml"]);
        Assert.Equal("<manifest/>"u8.ToArray(), entries["META-INF/manifest.xml"]);
        Assert.True(file.Length < large.Length / 4);
    }

    [Fact]
    public async Task StoresASmallPartWithItsSizesUpFrontAndNoDescriptor()
    {
        byte[] file = await Build(zip =>
        {
            zip.AddStored("mimetype", "application/vnd.oasis.opendocument.spreadsheet"u8);
            zip.Complete();
        });

        // The ODF rule a stored mimetype must meet: name at 30, content at 38, no descriptor flag.
        Assert.Equal(0x04034b50u, BinaryPrimitives.ReadUInt32LittleEndian(file));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(6)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(8)));
        Assert.Equal(Crc32.Compute("application/vnd.oasis.opendocument.spreadsheet"u8), BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(14)));
        Assert.Equal(46u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(18)));
        Assert.Equal(46u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(22)));
        Assert.Equal("mimetype", Encoding.ASCII.GetString(file, 30, 8));
        Assert.Equal("application/vnd.oasis.opendocument.spreadsheet", Encoding.ASCII.GetString(file, 38, 46));
    }

    [Fact]
    public async Task StreamsALargePartWithADataDescriptor()
    {
        byte[] content = Encoding.ASCII.GetBytes(new string('x', 10_000));

        byte[] file = await Build(zip =>
        {
            Deflated(zip, "sheet.xml", content);
            zip.Complete();
        });

        Assert.Equal(0x0008, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(6)) & 0x0008);
        Assert.Equal(8, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(8)));
        Assert.Equal(content, Read(file)["sheet.xml"]);

        int descriptor = file.AsSpan().IndexOf([0x50, 0x4B, 0x07, 0x08]);
        Assert.True(descriptor > 0);
        Assert.Equal(Crc32.Compute(content), BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(descriptor + 4)));
        Assert.Equal((uint)content.Length, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(descriptor + 12)));
    }

    [Fact]
    public async Task WritesZip64StructuresPastTheThreshold()
    {
        byte[] content = new byte[5_000];
        new Random(64).NextBytes(content);

        byte[] file = await Build(
            zip =>
            {
                zip.AddStored("small", "tiny"u8);
                Deflated(zip, "big", content);
                zip.Complete();
            },
            zip64Threshold: 100);

        Assert.True(file.AsSpan().IndexOf([0x50, 0x4B, 0x06, 0x06]) > 0, "a zip64 end of central directory record");
        Assert.True(file.AsSpan().IndexOf([0x50, 0x4B, 0x06, 0x07]) > 0, "a zip64 end of central directory locator");

        Dictionary<string, byte[]> entries = Read(file);
        Assert.Equal("tiny"u8.ToArray(), entries["small"]);
        Assert.Equal(content, entries["big"]);
    }

    [Fact]
    public async Task IsReproducibleByteForByte()
    {
        static void Write(ZipWriter zip)
        {
            zip.AddStored("a", "1"u8);
            Deflated(zip, "b", Encoding.ASCII.GetBytes("<x/>"));
            zip.Complete();
        }

        Assert.Equal(await Build(Write), await Build(Write));
    }

    [Fact]
    public void RefusesAnotherEntryWhileOneIsOpen()
    {
        using SpillBuffer buffer = new();
        ZipWriter zip = new(buffer, CompressionLevel.Fastest);
        zip.BeginDeflated("open");

        Assert.Throws<InvalidOperationException>(() => zip.AddStored("other", "x"u8));
        Assert.Throws<InvalidOperationException>(() => zip.BeginDeflated("other"));
        Assert.Throws<InvalidOperationException>(() => zip.Complete());
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~ZipWriterTests"`
Expected: build FAIL — `ZipWriter` does not exist.

- [ ] **Step 3: Implement**

Create `src/TriasDev.Tabular/Writing/ZipWriter.cs`:

```csharp
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace TriasDev.Tabular;

/// <summary>
/// Writes a zip package forward into the spill buffer: the container of xlsx and ods.
/// </summary>
/// <remarks>
/// <para>
/// Our own because <see cref="ZipArchive"/> does not fit, measured while planning the write side:
/// written to a stream that cannot seek it puts a data descriptor on every entry, the stored ODF
/// <c>mimetype</c> too, and LibreOffice refuses that file; given one that can seek, it seeks back to
/// patch each local header, so a sheet of a million rows stays in memory until it closes.
/// </para>
/// <para>
/// Two kinds of entry. A small part is stored with its checksum and sizes in the local header and no
/// descriptor — what the ODF <c>mimetype</c> requires. A large part is deflated as it is written, its
/// checksum and sizes following it in a data descriptor. Zip64 records are written once a size, an
/// offset or the directory passes <c>zip64Threshold</c>, four gigabytes unless a test lowers it.
/// </para>
/// </remarks>
internal sealed class ZipWriter
{
    private const uint LocalHeaderSignature = 0x04034b50;
    private const uint DescriptorSignature = 0x08074b50;
    private const uint CentralHeaderSignature = 0x02014b50;
    private const uint EndSignature = 0x06054b50;
    private const uint Zip64EndSignature = 0x06064b50;
    private const uint Zip64LocatorSignature = 0x07064b50;
    private const ushort Stored = 0;
    private const ushort Deflated = 8;
    private const ushort DescriptorFlag = 0x0008;
    private const ushort Version20 = 20;
    private const ushort Version45 = 45;
    private const ushort DosTime = 0;
    private const ushort DosDate = 0x0021;    // 1980-01-01: every package is byte-for-byte reproducible
    private const ushort Zip64ExtraId = 0x0001;

    private readonly SpillBuffer _out;
    private readonly CompressionLevel _level;
    private readonly long _zip64Threshold;
    private readonly List<Entry> _entries = [];
    private EntryStream? _open;

    public ZipWriter(SpillBuffer output, CompressionLevel level, long zip64Threshold = uint.MaxValue)
    {
        _out = output;
        _level = level;
        _zip64Threshold = zip64Threshold;
    }

    /// <summary>Writes a small part stored, its checksum and sizes in the local header.</summary>
    public void AddStored(string name, ReadOnlySpan<byte> content)
    {
        ExpectNoOpenEntry();
        byte[] encodedName = Encoding.ASCII.GetBytes(name);
        uint crc = Crc32.Compute(content);
        long offset = _out.TotalWritten;

        WriteLocalHeader(encodedName, Stored, flags: 0, crc, content.Length);
        _out.Write(content);
        _entries.Add(new Entry(encodedName, Stored, 0, crc, content.Length, content.Length, offset));
    }

    /// <summary>Begins a large part: write its bytes into the stream returned, then call <see cref="EndEntry"/>.</summary>
    public Stream BeginDeflated(string name)
    {
        ExpectNoOpenEntry();
        byte[] encodedName = Encoding.ASCII.GetBytes(name);
        long offset = _out.TotalWritten;

        WriteLocalHeader(encodedName, Deflated, DescriptorFlag, crc: 0, size: 0);
        _open = new EntryStream(encodedName, offset, _out, _level);
        return _open;
    }

    /// <summary>Ends the open large part: the last deflate block, then its data descriptor.</summary>
    public void EndEntry()
    {
        EntryStream entry = _open ?? throw new InvalidOperationException("No entry is open.");
        entry.FinishDeflate();

        long compressed = _out.TotalWritten - entry.DataStart;
        bool zip64 = compressed >= _zip64Threshold || entry.Size >= _zip64Threshold;
        Span<byte> descriptor = stackalloc byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor, DescriptorSignature);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[4..], entry.Crc);

        if (zip64)
        {
            BinaryPrimitives.WriteInt64LittleEndian(descriptor[8..], compressed);
            BinaryPrimitives.WriteInt64LittleEndian(descriptor[16..], entry.Size);
            _out.Write(descriptor);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor[8..], (uint)compressed);
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor[12..], (uint)entry.Size);
            _out.Write(descriptor[..16]);
        }

        _entries.Add(new Entry(entry.Name, Deflated, DescriptorFlag, entry.Crc, compressed, entry.Size, entry.Offset));
        _open = null;
    }

    /// <summary>Writes the central directory and the end records. Nothing may follow.</summary>
    public void Complete()
    {
        ExpectNoOpenEntry();
        long directoryStart = _out.TotalWritten;

        foreach (Entry entry in _entries)
        {
            WriteCentralHeader(entry);
        }

        long directorySize = _out.TotalWritten - directoryStart;
        bool zip64 = _entries.Count >= ushort.MaxValue
            || directoryStart >= _zip64Threshold
            || directorySize >= _zip64Threshold
            || _entries.Exists(NeedsZip64);

        if (zip64)
        {
            WriteZip64End(directoryStart, directorySize);
        }

        Span<byte> end = stackalloc byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(end, EndSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(end[4..], 0);                                    // this disk
        BinaryPrimitives.WriteUInt16LittleEndian(end[6..], 0);                                    // directory's disk
        BinaryPrimitives.WriteUInt16LittleEndian(end[8..], zip64 ? ushort.MaxValue : (ushort)_entries.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(end[10..], zip64 ? ushort.MaxValue : (ushort)_entries.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(end[12..], zip64 ? uint.MaxValue : (uint)directorySize);
        BinaryPrimitives.WriteUInt32LittleEndian(end[16..], zip64 ? uint.MaxValue : (uint)directoryStart);
        BinaryPrimitives.WriteUInt16LittleEndian(end[20..], 0);                                   // comment length
        _out.Write(end);
    }

    private bool NeedsZip64(Entry entry) =>
        entry.CompressedSize >= _zip64Threshold || entry.Size >= _zip64Threshold || entry.Offset >= _zip64Threshold;

    private void ExpectNoOpenEntry()
    {
        if (_open is not null)
        {
            throw new InvalidOperationException("An entry is still open; end it first.");
        }
    }

    private void WriteLocalHeader(byte[] name, ushort method, ushort flags, uint crc, long size)
    {
        Span<byte> header = stackalloc byte[30];
        BinaryPrimitives.WriteUInt32LittleEndian(header, LocalHeaderSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], Version20);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], flags);
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..], method);
        BinaryPrimitives.WriteUInt16LittleEndian(header[10..], DosTime);
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], DosDate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[14..], crc);
        BinaryPrimitives.WriteUInt32LittleEndian(header[18..], (uint)size);                       // compressed = size when stored
        BinaryPrimitives.WriteUInt32LittleEndian(header[22..], (uint)size);
        BinaryPrimitives.WriteUInt16LittleEndian(header[26..], (ushort)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header[28..], 0);                                // extra length
        _out.Write(header);
        _out.Write(name);
    }

    private void WriteCentralHeader(Entry entry)
    {
        bool zip64 = NeedsZip64(entry);
        Span<byte> header = stackalloc byte[46];
        BinaryPrimitives.WriteUInt32LittleEndian(header, CentralHeaderSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], zip64 ? Version45 : Version20);    // made by: MS-DOS host
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], zip64 ? Version45 : Version20);    // needed to extract
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..], entry.Flags);
        BinaryPrimitives.WriteUInt16LittleEndian(header[10..], entry.Method);
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], DosTime);
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], DosDate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], entry.Crc);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], zip64 ? uint.MaxValue : (uint)entry.CompressedSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], zip64 ? uint.MaxValue : (uint)entry.Size);
        BinaryPrimitives.WriteUInt16LittleEndian(header[28..], (ushort)entry.Name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header[30..], zip64 ? (ushort)28 : (ushort)0);  // extra length
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], 0);                                // comment length
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 0);                                // disk
        BinaryPrimitives.WriteUInt16LittleEndian(header[36..], 0);                                // internal attributes
        BinaryPrimitives.WriteUInt32LittleEndian(header[38..], 0);                                // external attributes
        BinaryPrimitives.WriteUInt32LittleEndian(header[42..], zip64 ? uint.MaxValue : (uint)entry.Offset);
        _out.Write(header);
        _out.Write(entry.Name);

        if (zip64)
        {
            Span<byte> extra = stackalloc byte[28];
            BinaryPrimitives.WriteUInt16LittleEndian(extra, Zip64ExtraId);
            BinaryPrimitives.WriteUInt16LittleEndian(extra[2..], 24);
            BinaryPrimitives.WriteInt64LittleEndian(extra[4..], entry.Size);
            BinaryPrimitives.WriteInt64LittleEndian(extra[12..], entry.CompressedSize);
            BinaryPrimitives.WriteInt64LittleEndian(extra[20..], entry.Offset);
            _out.Write(extra);
        }
    }

    private void WriteZip64End(long directoryStart, long directorySize)
    {
        long recordStart = _out.TotalWritten;
        Span<byte> record = stackalloc byte[56];
        BinaryPrimitives.WriteUInt32LittleEndian(record, Zip64EndSignature);
        BinaryPrimitives.WriteInt64LittleEndian(record[4..], 44);                                 // size of the rest of the record
        BinaryPrimitives.WriteUInt16LittleEndian(record[12..], Version45);
        BinaryPrimitives.WriteUInt16LittleEndian(record[14..], Version45);
        BinaryPrimitives.WriteUInt32LittleEndian(record[16..], 0);                                // this disk
        BinaryPrimitives.WriteUInt32LittleEndian(record[20..], 0);                                // directory's disk
        BinaryPrimitives.WriteInt64LittleEndian(record[24..], _entries.Count);
        BinaryPrimitives.WriteInt64LittleEndian(record[32..], _entries.Count);
        BinaryPrimitives.WriteInt64LittleEndian(record[40..], directorySize);
        BinaryPrimitives.WriteInt64LittleEndian(record[48..], directoryStart);
        _out.Write(record);

        Span<byte> locator = stackalloc byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(locator, Zip64LocatorSignature);
        BinaryPrimitives.WriteUInt32LittleEndian(locator[4..], 0);                                // the record's disk
        BinaryPrimitives.WriteInt64LittleEndian(locator[8..], recordStart);
        BinaryPrimitives.WriteUInt32LittleEndian(locator[16..], 1);                               // total disks
        _out.Write(locator);
    }

    private sealed record Entry(byte[] Name, ushort Method, ushort Flags, uint Crc, long CompressedSize, long Size, long Offset);

    /// <summary>The open large part: checksums and counts what is written, and deflates it into the buffer.</summary>
    private sealed class EntryStream : Stream
    {
        private readonly DeflateStream _deflate;

        public EntryStream(byte[] name, long offset, SpillBuffer output, CompressionLevel level)
        {
            Name = name;
            Offset = offset;
            DataStart = output.TotalWritten;
            _deflate = new DeflateStream(output, level, leaveOpen: true);
        }

        public byte[] Name { get; }

        public long Offset { get; }

        public long DataStart { get; }

        public uint Crc { get; private set; }

        public long Size { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Crc = Crc32.Update(Crc, buffer);
            Size += buffer.Length;
            _deflate.Write(buffer);
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Flush()
        {
            // Deliberately nothing: a deflate flush ends a block early and costs compression. The
            // entry's last block is written once, by FinishDeflate.
        }

        public void FinishDeflate() => _deflate.Dispose();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _deflate.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~ZipWriterTests"`
Expected: PASS on both targets. If `ZipArchive` refuses the zip64 test's archive, compare the record layouts with APPNOTE.TXT 4.3.14–4.3.16 and 4.5.3 and fix the writer — never lower what the test checks.

- [ ] **Step 5: Commit**

```bash
dotnet format TriasDev.Tabular.slnx --verify-no-changes
git add src/TriasDev.Tabular/Writing/ZipWriter.cs tests/TriasDev.Tabular.Tests/Writing/ZipWriterTests.cs
git commit -m "feat(write): a forward-only zip writer — stored parts without descriptors, streamed deflated parts, zip64"
```

---

### Task 3: `RowText`, shared by the sheet writers

**Files:**
- Create: `src/TriasDev.Tabular/Writing/RowText.cs`
- Modify: `src/TriasDev.Tabular/Csv/CsvSheetWriter.cs` (replace `_row`, `_length`, `Append`, `Reserve`, the buffer part of `AppendFormatted`, the encoding in `EndRow`, `RowBufferLength`)
- Test: `tests/TriasDev.Tabular.Tests/Writing/RowTextTests.cs`

**Interfaces:**
- Produces (internal, namespace `TriasDev.Tabular`): `sealed class RowText` with `int Length`, `ReadOnlySpan<char> Written`, `int Capacity`, `void Clear()` (empties; lets an oversized buffer go), `void Append(char)`, `void Append(ReadOnlySpan<char>)`, `int AppendFormatted<T>(T value, ReadOnlySpan<char> format, IFormatProvider? provider) where T : ISpanFormattable` (returns where the value starts), `void Truncate(int length)`, `void WriteUtf8To(IBufferWriter<byte> output)`.
- Consumes: nothing new. `CsvSheetWriter`'s behaviour must not change: every existing csv test stays green unmodified.

- [ ] **Step 1: Write the failing tests**

Create `tests/TriasDev.Tabular.Tests/Writing/RowTextTests.cs`:

```csharp
using System.Buffers;
using System.Globalization;
using System.Text;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>The row buffer every sheet writer formats into before encoding it.</summary>
public sealed class RowTextTests
{
    [Fact]
    public void AppendsCharactersTextAndFormattedValues()
    {
        RowText row = new();
        row.Append('<');
        row.Append("c r=\"A1\">");
        int start = row.AppendFormatted(1234.5m, default, CultureInfo.InvariantCulture);
        row.Append('>');

        Assert.Equal("<c r=\"A1\">1234.5>", row.Written.ToString());
        Assert.Equal("1234.5", row.Written[start..^1].ToString());
    }

    [Fact]
    public void GrowsForAValueLongerThanItsBuffer()
    {
        RowText row = new();
        string text = new('x', 100_000);
        row.Append(text);
        row.AppendFormatted(new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Unspecified), "yyyy'-'MM'-'dd", CultureInfo.InvariantCulture);

        Assert.Equal(100_010, row.Length);
        Assert.EndsWith("2026-10-03", row.Written.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TruncatesToWhereAValueStarted()
    {
        RowText row = new();
        row.Append("a,");
        int start = row.AppendFormatted(-2.5, "R", CultureInfo.InvariantCulture);
        row.Truncate(start);

        Assert.Equal("a,", row.Written.ToString());
    }

    [Fact]
    public void EncodesAsUtf8()
    {
        RowText row = new();
        row.Append("Grüße 👍");
        ArrayBufferWriter<byte> output = new();

        row.WriteUtf8To(output);

        Assert.Equal(Encoding.UTF8.GetBytes("Grüße 👍"), output.WrittenSpan.ToArray());
    }

    [Fact]
    public void LetsAHugeBufferGoWhenCleared()
    {
        RowText row = new();
        row.Append(new string('x', 10_000_000));

        row.Clear();

        Assert.Equal(0, row.Length);
        Assert.True(row.Capacity <= 4 * 1024);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~RowTextTests"`
Expected: build FAIL — `RowText` does not exist.

- [ ] **Step 3: Implement `RowText`**

Create `src/TriasDev.Tabular/Writing/RowText.cs`:

```csharp
using System.Buffers;
using System.Text;

namespace TriasDev.Tabular;

/// <summary>
/// The characters of one row as a sheet writer builds it, before it is encoded: reused from row to
/// row, so a sheet of millions of rows allocates nothing per row.
/// </summary>
/// <remarks>
/// Numbers and dates are formatted straight into it with <see cref="ISpanFormattable.TryFormat"/>.
/// A row larger than <see cref="RetainedChars"/> grows the buffer for its own sake only:
/// <see cref="Clear"/> lets it go, rather than keep a one-off note's buffer for the rest of the file.
/// </remarks>
internal sealed class RowText
{
    private const int InitialChars = 4 * 1024;

    private const int RetainedChars = 1024 * 1024;

    private char[] _chars = new char[InitialChars];

    /// <summary>How many characters the row holds.</summary>
    public int Length { get; private set; }

    /// <summary>The row so far.</summary>
    public ReadOnlySpan<char> Written => _chars.AsSpan(0, Length);

    /// <summary>The buffer's size, for the test that a huge row does not keep it.</summary>
    public int Capacity => _chars.Length;

    /// <summary>Empties the row, and lets a buffer grown past the retained size go.</summary>
    public void Clear()
    {
        Length = 0;

        if (_chars.Length > RetainedChars)
        {
            _chars = new char[InitialChars];
        }
    }

    public void Append(char value)
    {
        Reserve(1);
        _chars[Length++] = value;
    }

    public void Append(ReadOnlySpan<char> value)
    {
        Reserve(value.Length);
        value.CopyTo(_chars.AsSpan(Length));
        Length += value.Length;
    }

    /// <summary>Formats a value into the row, growing it until the value fits; returns where it starts.</summary>
    public int AppendFormatted<T>(T value, ReadOnlySpan<char> format, IFormatProvider? provider)
        where T : ISpanFormattable
    {
        Reserve(64);
        int start = Length;
        int written;

        while (!value.TryFormat(_chars.AsSpan(start), out written, format, provider))
        {
            Array.Resize(ref _chars, _chars.Length * 2);
        }

        Length += written;
        return start;
    }

    /// <summary>Drops everything from <paramref name="length"/> on.</summary>
    public void Truncate(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, Length);
        Length = length;
    }

    /// <summary>Encodes the row as UTF-8 into the output.</summary>
    public void WriteUtf8To(IBufferWriter<byte> output)
    {
        Span<byte> target = output.GetSpan(Encoding.UTF8.GetMaxByteCount(Length));
        output.Advance(Encoding.UTF8.GetBytes(Written, target));
    }

    private void Reserve(int extra)
    {
        if (Length + extra > _chars.Length)
        {
            Array.Resize(ref _chars, Math.Max(_chars.Length * 2, Length + extra));
        }
    }
}
```

- [ ] **Step 4: Move `CsvSheetWriter` onto `RowText`**

In `src/TriasDev.Tabular/Csv/CsvSheetWriter.cs`:
- Remove the fields `_row` (char[]), `_length`, the constants `InitialRowChars` and `RetainedRowChars`, and the private methods `Append(char)`, `Append(ReadOnlySpan<char>)` and `Reserve`. Add `private readonly RowText _row = new();`.
- Every `Append(x)` call becomes `_row.Append(x)`.
- `BeginRow`: replace `_length = 0;` with `_row.Clear();`.
- `RowBufferLength` becomes `internal int RowBufferLength => _row.Capacity;`.
- `EndRow` becomes:

```csharp
    public void EndRow()
    {
        _row.Append("\r\n");
        _row.WriteUtf8To(_out);
        _row.Clear();
    }
```

- `AppendFormatted<T>` becomes:

```csharp
    private void AppendFormatted<T>(T value, ReadOnlySpan<char> format)
        where T : ISpanFormattable
    {
        int start = _row.AppendFormatted(value, format, _format.Culture);
        ReadOnlySpan<char> formatted = _row.Written[start..];

        if (formatted.IndexOfAny(NeedsQuotes) < 0)
        {
            return;
        }

        // Copied out first: the quoted value is written over the place it was formatted into.
        int written = formatted.Length;
        Span<char> copy = written <= FormattedOnStack ? stackalloc char[FormattedOnStack] : new char[written];
        formatted.CopyTo(copy);
        _row.Truncate(start);
        AppendQuoted(copy[..written], guard: false);
    }
```

Keep every doc comment that still describes what stays; move the "a huge row does not keep its buffer" remark to `RowText` (done above). `SpillBuffer` implements `IBufferWriter<byte>`, so `_row.WriteUtf8To(_out)` compiles as is.

- [ ] **Step 5: Run the tests**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~Writing"`
Expected: PASS — `RowTextTests` and every existing csv test, none of them edited.

- [ ] **Step 6: Commit**

```bash
dotnet format TriasDev.Tabular.slnx --verify-no-changes
git add src/TriasDev.Tabular/Writing/RowText.cs src/TriasDev.Tabular/Csv/CsvSheetWriter.cs tests/TriasDev.Tabular.Tests/Writing/RowTextTests.cs
git commit -m "refactor(write): one row buffer for every sheet writer"
```

---

### Task 4: xlsx options, and sheet names a workbook accepts

**Files:**
- Create: `src/TriasDev.Tabular/Xlsx/XlsxWriterOptions.cs`
- Create: `src/TriasDev.Tabular/Writing/SheetNames.cs`
- Modify: `src/TriasDev.Tabular/Writing/TabularWriterOptions.cs` (add `Xlsx`)
- Modify: `src/TriasDev.Tabular/Writing/ISheetWriter.cs` (add `bool NamesSheets { get; }`)
- Modify: `src/TriasDev.Tabular/Csv/CsvSheetWriter.cs` (`public bool NamesSheets => false;`)
- Modify: `src/TriasDev.Tabular/Writing/TabularWriter.cs` (`BeginSheet` checks the name when the format names sheets)
- Modify: `src/TriasDev.Tabular/PublicAPI.Unshipped.txt`
- Test: `tests/TriasDev.Tabular.Tests/Writing/SheetNamesTests.cs`, `tests/TriasDev.Tabular.Tests/Writing/XlsxWriterOptionsTests.cs`

**Interfaces:**
- Produces: `public sealed record XlsxWriterOptions { static Default; CompressionLevel CompressionLevel = CompressionLevel.Fastest; internal XlsxWriterOptions Checked() }` (namespace `TriasDev.Tabular.Xlsx`); `TabularWriterOptions.Xlsx` (`XlsxWriterOptions`, default `XlsxWriterOptions.Default`); internal `static class SheetNames` with `string? Problem(string name, ISet<string> taken)` (null when acceptable; otherwise the reason, in English); `ISheetWriter.NamesSheets`. Task 5 wires `Create` for xlsx and relies on `BeginSheet` having checked the name.

- [ ] **Step 1: Write the failing tests**

Create `tests/TriasDev.Tabular.Tests/Writing/SheetNamesTests.cs`:

```csharp
using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>Excel's rules for a sheet name, which both workbook formats follow.</summary>
public sealed class SheetNamesTests
{
    [Theory]
    [InlineData("Portfolios")]
    [InlineData("R&D <2026>")]
    [InlineData("a")]
    [InlineData("1234567890123456789012345678901")]
    [InlineData("Grüße 'quoted' inside")]
    public void AcceptsANameExcelAccepts(string name)
    {
        Assert.Null(SheetNames.Problem(name, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345678901234567890123456789012")]
    [InlineData("a[b")]
    [InlineData("a]b")]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("'leading")]
    [InlineData("trailing'")]
    [InlineData("History")]
    [InlineData("history")]
    [InlineData("bad\u0001")]
    public void RefusesANameExcelRefuses(string name)
    {
        Assert.NotNull(SheetNames.Problem(name, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public void RefusesANameAlreadyTakenIgnoringCase()
    {
        HashSet<string> taken = new(StringComparer.OrdinalIgnoreCase) { "Portfolios" };

        Assert.NotNull(SheetNames.Problem("PORTFOLIOS", taken));
    }
}
```

Create `tests/TriasDev.Tabular.Tests/Writing/XlsxWriterOptionsTests.cs`:

```csharp
using System.IO.Compression;

using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>The xlsx options ship their defaults and refuse what cannot work, where they are handed over.</summary>
public sealed class XlsxWriterOptionsTests
{
    [Fact]
    public void DefaultsToTheFastestCompression()
    {
        Assert.Equal(CompressionLevel.Fastest, XlsxWriterOptions.Default.CompressionLevel);
        Assert.Same(XlsxWriterOptions.Default, TabularWriterOptions.Default.Xlsx);
    }

    [Fact]
    public void RefusesACompressionLevelThatDoesNotExist()
    {
        ArgumentException refused = Assert.ThrowsAny<ArgumentException>(() => new XlsxWriterOptions { CompressionLevel = (CompressionLevel)42 }.Checked());

        Assert.Contains(nameof(XlsxWriterOptions.CompressionLevel), refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CompressionLevel.Optimal)]
    [InlineData(CompressionLevel.Fastest)]
    [InlineData(CompressionLevel.NoCompression)]
    [InlineData(CompressionLevel.SmallestSize)]
    public void TakesEveryCompressionLevel(CompressionLevel level)
    {
        Assert.Equal(level, new XlsxWriterOptions { CompressionLevel = level }.Checked().CompressionLevel);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~SheetNamesTests|FullyQualifiedName~XlsxWriterOptionsTests"`
Expected: build FAIL — `SheetNames` and `XlsxWriterOptions` do not exist.

- [ ] **Step 3: Implement `SheetNames`**

Create `src/TriasDev.Tabular/Writing/SheetNames.cs`:

```csharp
using System.Buffers;

namespace TriasDev.Tabular;

/// <summary>The rules a sheet name meets in a workbook — Excel's, which LibreOffice also keeps.</summary>
internal static class SheetNames
{
    /// <summary>Excel's limit on a sheet name's length.</summary>
    public const int MaxLength = 31;

    private static readonly SearchValues<char> Forbidden = SearchValues.Create("[]:*?/\\");

    /// <summary>Why the name cannot be used, or null when it can. <paramref name="taken"/> compares ignoring case.</summary>
    public static string? Problem(string name, ISet<string> taken)
    {
        if (name.Length is 0 or > MaxLength)
        {
            return $"A sheet name has 1 to {MaxLength} characters.";
        }

        if (name.AsSpan().IndexOfAny(Forbidden) >= 0)
        {
            return "A sheet name holds none of [ ] : * ? / \\.";
        }

        if (name[0] == '\'' || name[^1] == '\'')
        {
            return "A sheet name neither starts nor ends with an apostrophe.";
        }

        if (string.Equals(name, "History", StringComparison.OrdinalIgnoreCase))
        {
            return "\"History\" is reserved by Excel.";
        }

        if (TextRules.Check(name) is not null)
        {
            return "A sheet name holds a character XML cannot carry.";
        }

        return taken.Contains(name) ? $"The workbook already has a sheet named \"{name}\", ignoring case." : null;
    }
}
```

- [ ] **Step 4: Implement `XlsxWriterOptions` and add it to `TabularWriterOptions`**

Create `src/TriasDev.Tabular/Xlsx/XlsxWriterOptions.cs`:

```csharp
using System.IO.Compression;

namespace TriasDev.Tabular.Xlsx;

/// <summary>Knobs for writing an xlsx workbook.</summary>
public sealed record XlsxWriterOptions
{
    /// <summary>The defaults: the fastest compression.</summary>
    public static XlsxWriterOptions Default { get; } = new();

    /// <summary>
    /// How hard each sheet is compressed: <see cref="CompressionLevel.Fastest"/> by default, since
    /// writing speed is the point; <see cref="CompressionLevel.Optimal"/> writes a smaller file more
    /// slowly.
    /// </summary>
    public CompressionLevel CompressionLevel { get; init; } = CompressionLevel.Fastest;

    internal XlsxWriterOptions Checked()
    {
        if (!Enum.IsDefined(CompressionLevel))
        {
            throw new ArgumentOutOfRangeException(
                nameof(CompressionLevel),
                CompressionLevel,
                $"{nameof(XlsxWriterOptions)}.{nameof(CompressionLevel)} is not a compression level.");
        }

        return this;
    }
}
```

In `src/TriasDev.Tabular/Writing/TabularWriterOptions.cs`, add `using TriasDev.Tabular.Xlsx;` and after `Csv`:

```csharp
    /// <summary>How an xlsx workbook is written.</summary>
    public XlsxWriterOptions Xlsx { get; init; } = XlsxWriterOptions.Default;
```

If the analyzer asks for a pragma on the `ArgumentOutOfRangeException` parameter name (S3928), use the same `#pragma warning disable S3928` with justification that `CsvWriterOptions` uses.

- [ ] **Step 5: `NamesSheets`, and `BeginSheet` checking the name**

In `src/TriasDev.Tabular/Writing/ISheetWriter.cs`, after `AllowsSeveralSheets`:

```csharp
    /// <summary>Whether the format stores sheet names, which must then meet <see cref="SheetNames"/>' rules.</summary>
    bool NamesSheets { get; }
```

In `src/TriasDev.Tabular/Csv/CsvSheetWriter.cs`, after `AllowsSeveralSheets`:

```csharp
    public bool NamesSheets => false;
```

In `src/TriasDev.Tabular/Writing/TabularWriter.cs`, add a field `private readonly HashSet<string> _sheetNames = new(StringComparer.OrdinalIgnoreCase);` and in `BeginSheet`, directly after the `name is null` check:

```csharp
        if (_sheet.NamesSheets && SheetNames.Problem(name, _sheetNames) is { } problem)
        {
            throw Faulting(new ArgumentException(problem, nameof(name)));
        }
```

and directly after `_sheetName = name;`:

```csharp
        _sheetNames.Add(name);
```

Update the `name` parameter's doc on `BeginSheet` to: `The sheet's name. A workbook's names are 1 to 31 characters, none of [ ] : * ? / \, no apostrophe at either end, not "History", unique ignoring case. A csv file has one sheet, whose name is not written.`

- [ ] **Step 6: Record the public API, run the tests**

Append the RS0016 lines for `XlsxWriterOptions` and `TabularWriterOptions.Xlsx` to `PublicAPI.Unshipped.txt`.

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~Writing|FullyQualifiedName~OptionDefaultsTests|FullyQualifiedName~NullArgumentTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
dotnet format TriasDev.Tabular.slnx --verify-no-changes
git add src/TriasDev.Tabular tests/TriasDev.Tabular.Tests
git commit -m "feat(write): xlsx options, and sheet names checked by Excel's rules for formats that name sheets"
```

---

### Task 5: The xlsx sheet writer — the package, text, booleans, empty cells

**Files:**
- Create: `src/TriasDev.Tabular/Xlsx/XlsxSheetWriter.cs`
- Create: `src/TriasDev.Tabular/Xlsx/XlsxParts.cs`
- Modify: `src/TriasDev.Tabular/Writing/TabularWriter.cs` (`Create` handles `TabularFormat.Xlsx`)
- Modify: `tests/TriasDev.Tabular.Tests/Writing/TabularWriterTests.cs` (`RefusesAFormatItCannotWriteYet` drops `Xlsx`)
- Modify: `tests/TriasDev.Tabular.Tests/TriasDev.Tabular.Tests.csproj` (add `DocumentFormat.OpenXml`)
- Create: `tests/TriasDev.Tabular.Tests/Fixtures/OoxmlValidation.cs`
- Test: `tests/TriasDev.Tabular.Tests/Writing/XlsxWriterTests.cs`

**Interfaces:**
- Consumes: `ZipWriter` (Task 2), `RowText` (Task 3), `XlsxWriterOptions`, `SheetNames`, `NamesSheets` (Task 4), `ISheetWriter`, `SpillBuffer`, `TextRules` (part 1), `XlsxCursor` (reader, for tests).
- Produces: internal `XlsxSheetWriter : ISheetWriter` — this task implements `BeginSheet`, `BeginRow`, `WriteHeader`, `WriteText`, `WriteBoolean`, `WriteEmpty`, `EndRow`, `Complete`; the four number and date members throw `NotSupportedException` until Task 6 replaces them (Step 4 gives the exact interim bodies; no test in this task reaches them). Also internal `static class XlsxParts` (the package's other parts, `ColumnName`, `SheetPath`), `XlsxSheetWriter.MaxTextChars = 32_767`, and the test fixture `OoxmlValidation.Errors(byte[] xlsx) → IReadOnlyList<string>`.

- [ ] **Step 1: Add the validator package and fixture**

In `tests/TriasDev.Tabular.Tests/TriasDev.Tabular.Tests.csproj`, add to the `ProjectReference` item group's sibling a new item group:

```xml
  <!-- Validation only: the writer's output is checked against the OOXML schema. The library itself takes no package. -->
  <ItemGroup>
    <PackageReference Include="DocumentFormat.OpenXml" Version="3.5.1" />
  </ItemGroup>
```

Create `tests/TriasDev.Tabular.Tests/Fixtures/OoxmlValidation.cs`:

```csharp
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

namespace TriasDev.Tabular.Tests.Fixtures;

/// <summary>What the Open XML SDK's schema validator finds wrong with a workbook — nothing, for a good one.</summary>
public static class OoxmlValidation
{
    public static IReadOnlyList<string> Errors(byte[] xlsx)
    {
        using MemoryStream stream = new(xlsx, writable: false);
        using SpreadsheetDocument document = SpreadsheetDocument.Open(stream, isEditable: false);

        return [.. new OpenXmlValidator(FileFormatVersions.Office2019)
            .Validate(document)
            .Select(error => $"{error.Part?.Uri} {error.Path?.XPath}: {error.Description}")];
    }
}
```

Run `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~TabularIndependenceTests"` — it must still pass (it inspects the library's dependency graph only).

- [ ] **Step 2: Write the failing tests**

Create `tests/TriasDev.Tabular.Tests/Writing/XlsxWriterTests.cs`:

```csharp
using System.IO.Compression;
using System.Text;

using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>
/// The workbook the xlsx writer streams: valid to the schema, and read by the library's own cursor
/// as written.
/// </summary>
public sealed class XlsxWriterTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<byte[]> Workbook(Action<TabularWriter> write)
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Xlsx))
        {
            write(writer);
            await writer.CompleteAsync(Token);
        }

        return target.ToArray();
    }

    private static List<RawCell[]> Rows(byte[] xlsx, int sheet = 0)
    {
        using XlsxCursor cursor = new(new MemoryStream(xlsx, writable: false), cancellationToken: Token);
        Assert.True(cursor.MoveToSheet(sheet, Token));

        List<RawCell[]> rows = [];

        while (cursor.ReadRow(Token))
        {
            rows.Add(cursor.CurrentRow.ToArray());
        }

        return rows;
    }

    private static string Part(byte[] xlsx, string name)
    {
        using ZipArchive archive = new(new MemoryStream(xlsx, writable: false), ZipArchiveMode.Read);
        using StreamReader reader = new(archive.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task WritesAWorkbookTheSchemaAcceptsAndTheCursorReads()
    {
        byte[] xlsx = await Workbook(writer =>
        {
            writer.BeginSheet("Portfolios", [new("Id"), new("Name"), new("Active")]);
            writer.BeginRow();
            writer.Write("P-1");
            writer.Write("Alpha");
            writer.Write(true);
            writer.EndRow();
            writer.BeginRow();
            writer.WriteEmpty();
            writer.Write("Beta");
            writer.Write(false);
            writer.EndRow();
        });

        Assert.Empty(OoxmlValidation.Errors(xlsx));
        Assert.Equal(TabularFormat.Xlsx, TabularFile.Detect(new MemoryStream(xlsx)));

        List<RawCell[]> rows = Rows(xlsx);
        Assert.Equal(3, rows.Count);
        Assert.Equal(["Id", "Name", "Active"], rows[0].Select(c => c.Text));
        Assert.Equal(RawCell.FromText("P-1"), rows[1][0]);
        Assert.Equal(RawCell.FromBoolean(true), rows[1][2]);
        Assert.True(rows[2][0].IsEmpty);
        Assert.Equal(RawCell.FromText("Beta"), rows[2][1]);
        Assert.Equal(RawCell.FromBoolean(false), rows[2][2]);
    }

    [Fact]
    public async Task WritesSeveralSheetsInOrder()
    {
        byte[] xlsx = await Workbook(writer =>
        {
            writer.BeginSheet("First", [new("a")]);
            writer.BeginRow();
            writer.Write("1");
            writer.EndRow();
            writer.BeginSheet("Second", [new("b")]);
            writer.BeginRow();
            writer.Write("2");
            writer.EndRow();
        });

        Assert.Empty(OoxmlValidation.Errors(xlsx));

        using XlsxCursor cursor = new(new MemoryStream(xlsx, writable: false), cancellationToken: Token);
        Assert.Equal(["First", "Second"], cursor.Sheets.Select(s => s.Name));
        Assert.Equal(RawCell.FromText("2"), Rows(xlsx, 1)[1][0]);
    }

    [Fact]
    public async Task NamesSheetsAsWrittenEvenWithXmlCharacters()
    {
        byte[] xlsx = await Workbook(writer => writer.BeginSheet("R&D <2026> \"q\"", [new("a")]));

        Assert.Empty(OoxmlValidation.Errors(xlsx));

        using XlsxCursor cursor = new(new MemoryStream(xlsx, writable: false), cancellationToken: Token);
        Assert.Equal("R&D <2026> \"q\"", Assert.Single(cursor.Sheets).Name);
    }

    [Fact]
    public async Task NumbersEveryRowAndEveryCell()
    {
        byte[] xlsx = await Workbook(writer =>
        {
            writer.BeginSheet("data", [new("a"), new("b")]);
            writer.BeginRow();
            writer.EndRow();
            writer.BeginRow();
            writer.WriteEmpty();
            writer.Write("x");
            writer.EndRow();
        });

        string sheet = Part(xlsx, "xl/worksheets/sheet1.xml");

        Assert.Contains("<row r=\"1\">", sheet, StringComparison.Ordinal);
        Assert.Contains("<row r=\"2\">", sheet, StringComparison.Ordinal);
        Assert.Contains("<row r=\"3\"><c r=\"B3\"", sheet, StringComparison.Ordinal);
        Assert.Equal(RawCell.FromText("x"), Rows(xlsx)[2][1]);
    }

    [Fact]
    public async Task WritesColumnWidthsBeforeTheData()
    {
        byte[] xlsx = await Workbook(writer => writer.BeginSheet("data", [new("a", 12.5), new("b"), new("c", 30)]));

        string sheet = Part(xlsx, "xl/worksheets/sheet1.xml");

        Assert.Contains("<cols><col min=\"1\" max=\"1\" width=\"12.5\" customWidth=\"1\"/><col min=\"3\" max=\"3\" width=\"30\" customWidth=\"1\"/></cols><sheetData>", sheet, StringComparison.Ordinal);
        Assert.Empty(OoxmlValidation.Errors(xlsx));
    }

    [Fact]
    public async Task WritesTextXmlCannotCarryLiterally()
    {
        string[] values = ["a & b < c > d", "two\nlines", "carriage\rreturn", "windows\r\nline", "tab\there", "Grüße 👍", "  padded  "];

        byte[] xlsx = await Workbook(writer =>
        {
            writer.BeginSheet("data", [new("v")]);

            foreach (string value in values)
            {
                writer.BeginRow();
                writer.Write(value);
                writer.EndRow();
            }
        });

        Assert.Empty(OoxmlValidation.Errors(xlsx));
        Assert.Equal(values.Select(v => RawCell.FromText(v)), Rows(xlsx).Skip(1).Select(r => r[0]));
    }

    [Fact]
    public async Task WritesUnderscoreXTextAsWritten()
    {
        string[] values = ["_x0041_", "C:\\_x86_\\", "_x", "a_x0001_b", "__x0041__"];

        byte[] xlsx = await Workbook(writer =>
        {
            writer.BeginSheet("data", [new("v")]);

            foreach (string value in values)
            {
                writer.BeginRow();
                writer.Write(value);
                writer.EndRow();
            }
        });

        Assert.Equal(values.Select(v => RawCell.FromText(v)), Rows(xlsx).Skip(1).Select(r => r[0]));
    }

    [Fact]
    public async Task RefusesTextLongerThanACellHolds()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);
        writer.BeginSheet("data", [new("v")]);
        writer.BeginRow();

        Assert.Equal(ErrorCodes.Write.TextTooLong, Assert.Throws<TabularWriteException>(() => writer.Write(new string('x', 32_768))).Code);
    }

    [Fact]
    public async Task TakesTextExactlyAsLongAsACellHolds()
    {
        string longest = new('x', 32_767);
        byte[] xlsx = await Workbook(writer =>
        {
            writer.BeginSheet("data", [new("v")]);
            writer.BeginRow();
            writer.Write(longest);
            writer.EndRow();
        });

        Assert.Equal(longest, Rows(xlsx)[1][0].Text);
    }

    [Theory]
    [InlineData("History")]
    [InlineData("a/b")]
    [InlineData("12345678901234567890123456789012")]
    public async Task RefusesASheetNameExcelRefuses(string name)
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);

        Assert.Throws<ArgumentException>(() => writer.BeginSheet(name, [new("a")]));
    }

    [Fact]
    public async Task RefusesTwoSheetsOfTheSameNameIgnoringCase()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);
        writer.BeginSheet("Data", [new("a")]);

        Assert.Throws<ArgumentException>(() => writer.BeginSheet("DATA", [new("a")]));
    }

    [Fact]
    public async Task AWorkbookDisposedBeforeCompletingDoesNotOpen()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Xlsx))
        {
            writer.BeginSheet("data", [new("v")]);

            for (int i = 0; i < 50_000; i++)
            {
                writer.BeginRow();
                writer.Write("a value long enough that a flush writes part of the sheet");
                writer.EndRow();
            }

            await writer.FlushAsync(Token);
        }

        byte[] partial = target.ToArray();
        Assert.NotEmpty(partial);

        // No central directory: the workbook reader refuses it rather than read a valid-looking part.
        TabularFormatException refused = Assert.Throws<TabularFormatException>(() => new XlsxCursor(new MemoryStream(partial, writable: false), cancellationToken: Token));
        Assert.Contains(refused.Code, new[] { ErrorCodes.Format.Corrupt, ErrorCodes.Format.Truncated });
    }

    [Fact]
    public async Task TouchesTheTargetOnlyAsynchronously()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Xlsx))
        {
            writer.BeginSheet("data", [new("v")]);

            for (int i = 0; i < 100_000; i++)
            {
                writer.BeginRow();
                writer.Write("row value");
                writer.EndRow();

                if (writer.FlushRecommended)
                {
                    await writer.FlushAsync(Token);
                }
            }

            await writer.CompleteAsync(Token);
        }

        Assert.False(target.DisposedSynchronously);
        Assert.Equal(100_001, Rows(target.ToArray()).Count);
    }

    [Fact]
    public void CompressesAsTheOptionsSay()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TabularWriter.Create(
            new MemoryStream(),
            TabularFormat.Xlsx,
            new TabularWriterOptions { Xlsx = new XlsxWriterOptions { CompressionLevel = (CompressionLevel)42 } }));
    }
}
```

In `tests/TriasDev.Tabular.Tests/Writing/TabularWriterTests.cs`, remove the `[InlineData(TabularFormat.Xlsx)]` row from `RefusesAFormatItCannotWriteYet`.

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~XlsxWriterTests"`
Expected: FAIL — `Create` refuses `TabularFormat.Xlsx`.

- [ ] **Step 4: Implement the package parts and the sheet writer**

Create `src/TriasDev.Tabular/Xlsx/XlsxParts.cs`:

```csharp
using System.Globalization;
using System.Text;

namespace TriasDev.Tabular.Xlsx;

/// <summary>The parts of a workbook package besides its sheets — small, and written once at the end.</summary>
internal static class XlsxParts
{
    public const string WorksheetStart =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
        + "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" "
        + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">";

    public const string WorksheetEnd = "</sheetData></worksheet>";

    /// <summary>
    /// Four cell formats, by index: 0 General, 1 a date, 2 a date and time, 3 an integer (format
    /// <c>0</c>, so an id of twelve digits does not show as <c>1.23457E+11</c>). The reader takes
    /// formats 14 and 164 for dates, 0 and 1 for numbers.
    /// </summary>
    public static readonly byte[] Styles = Encoding.UTF8.GetBytes(
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
        + "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
        + "<numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"yyyy\\-mm\\-dd\\ hh:mm:ss\"/></numFmts>"
        + "<fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>"
        + "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>"
        + "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>"
        + "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>"
        + "<cellXfs count=\"4\">"
        + "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>"
        + "<xf numFmtId=\"14\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"
        + "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"
        + "<xf numFmtId=\"1\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"
        + "</cellXfs>"
        + "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>"
        + "</styleSheet>");

    public static readonly byte[] PackageRelationships = Encoding.UTF8.GetBytes(
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
        + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
        + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>"
        + "</Relationships>");

    public static string SheetPath(int number) => string.Create(CultureInfo.InvariantCulture, $"xl/worksheets/sheet{number}.xml");

    public static byte[] ContentTypes(int sheets)
    {
        StringBuilder xml = new(
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
            + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
            + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
            + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
            + "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>"
            + "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");

        for (int i = 1; i <= sheets; i++)
        {
            xml.Append(CultureInfo.InvariantCulture, $"<Override PartName=\"/{SheetPath(i)}\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        }

        return Encoding.UTF8.GetBytes(xml.Append("</Types>").ToString());
    }

    public static byte[] Workbook(IReadOnlyList<string> sheetNames)
    {
        StringBuilder xml = new(
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
            + "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" "
            + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");

        for (int i = 0; i < sheetNames.Count; i++)
        {
            xml.Append("<sheet name=\"");
            AppendEscaped(xml, sheetNames[i]);
            xml.Append(CultureInfo.InvariantCulture, $"\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
        }

        return Encoding.UTF8.GetBytes(xml.Append("</sheets></workbook>").ToString());
    }

    public static byte[] WorkbookRelationships(int sheets)
    {
        StringBuilder xml = new(
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
            + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");

        for (int i = 1; i <= sheets; i++)
        {
            xml.Append(CultureInfo.InvariantCulture, $"<Relationship Id=\"rId{i}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i}.xml\"/>");
        }

        xml.Append(CultureInfo.InvariantCulture, $"<Relationship Id=\"rId{sheets + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
        return Encoding.UTF8.GetBytes(xml.Append("</Relationships>").ToString());
    }

    /// <summary>The column's letters: 0 → A, 25 → Z, 26 → AA, 16,383 → XFD.</summary>
    public static string ColumnName(int index)
    {
        Span<char> letters = stackalloc char[3];
        int at = letters.Length;

        for (int n = index + 1; n > 0; n = (n - 1) / 26)
        {
            letters[--at] = (char)('A' + ((n - 1) % 26));
        }

        return new string(letters[at..]);
    }

    private static void AppendEscaped(StringBuilder xml, string value)
    {
        foreach (char c in value)
        {
            _ = c switch
            {
                '&' => xml.Append("&amp;"),
                '<' => xml.Append("&lt;"),
                '>' => xml.Append("&gt;"),
                '"' => xml.Append("&quot;"),
                _ => xml.Append(c),
            };
        }
    }
}
```

Create `src/TriasDev.Tabular/Xlsx/XlsxSheetWriter.cs`:

```csharp
using System.Buffers;
using System.Globalization;

namespace TriasDev.Tabular.Xlsx;

/// <summary>
/// Writes an xlsx workbook: each sheet one deflated zip entry written row by row, the workbook's
/// other parts once at the end.
/// </summary>
/// <remarks>
/// <para>
/// Text is written inline (<c>t="inlineStr"</c>), never into a shared-string table, which would hold
/// every distinct string in memory until the end. Every row and every cell carries its reference,
/// so an empty cell is simply left out and an empty row still has its number.
/// </para>
/// <para>
/// A carriage return is written as <c>_x000D_</c>: the reader turns a literal one into a line feed,
/// as XML does. An underscore that starts <c>_x</c> is written as <c>_x005F_</c>, so text that
/// happens to look like an escape is not decoded into something else.
/// </para>
/// </remarks>
internal sealed class XlsxSheetWriter : ISheetWriter
{
    /// <summary>The most characters a cell holds.</summary>
    public const int MaxTextChars = 32_767;

    private const int RetainedBytes = 1024 * 1024;

    private static readonly SearchValues<char> NeedsEscape = SearchValues.Create("&<>\r_");

    private readonly ZipWriter _zip;
    private readonly List<string> _sheetNames = [];
    private readonly RowText _row = new();
    private ArrayBufferWriter<byte> _bytes = new(16 * 1024);
    private Stream? _sheet;
    private string[] _columnNames = [];
    private long _rowNumber;
    private int _column;

    public XlsxSheetWriter(SpillBuffer output, XlsxWriterOptions options)
    {
        _zip = new ZipWriter(output, options.CompressionLevel);
    }

    public long MaxRows => 1_048_576;

    public bool AllowsSeveralSheets => true;

    public bool NamesSheets => true;

    public void BeginSheet(string name, ReadOnlySpan<WriteColumn> columns)
    {
        CloseSheet();
        _sheetNames.Add(name);
        _sheet = _zip.BeginDeflated(XlsxParts.SheetPath(_sheetNames.Count));
        _columnNames = new string[columns.Length];

        for (int i = 0; i < columns.Length; i++)
        {
            _columnNames[i] = XlsxParts.ColumnName(i);
        }

        _rowNumber = 0;
        _row.Clear();
        _row.Append(XlsxParts.WorksheetStart);
        AppendWidths(columns);
        _row.Append("<sheetData>");
        Emit();
    }

    public void BeginRow()
    {
        _rowNumber++;
        _column = 0;
        _row.Clear();
        _row.Append("<row r=\"");
        _row.AppendFormatted(_rowNumber, default, CultureInfo.InvariantCulture);
        _row.Append("\">");
    }

    public string? WriteHeader(string value) => WriteInline(value);

    public string? WriteText(string value, int column) => WriteInline(value);

    public string? WriteLong(long value) => throw new NotSupportedException("Numbers arrive with the next change.");

    public string? WriteDecimal(decimal value) => throw new NotSupportedException("Numbers arrive with the next change.");

    public string? WriteDouble(double value) => throw new NotSupportedException("Numbers arrive with the next change.");

    public string? WriteDate(DateTime value, bool hasTime) => throw new NotSupportedException("Dates arrive with the next change.");

    public void WriteBoolean(bool value)
    {
        StartCell();
        _row.Append(" t=\"b\"><v>");
        _row.Append(value ? '1' : '0');
        _row.Append("</v></c>");
    }

    public void WriteEmpty() => _column++;

    public void EndRow()
    {
        _row.Append("</row>");
        Emit();
    }

    public void Complete()
    {
        CloseSheet();
        _zip.AddStored("[Content_Types].xml", XlsxParts.ContentTypes(_sheetNames.Count));
        _zip.AddStored("_rels/.rels", XlsxParts.PackageRelationships);
        _zip.AddStored("xl/workbook.xml", XlsxParts.Workbook(_sheetNames));
        _zip.AddStored("xl/_rels/workbook.xml.rels", XlsxParts.WorkbookRelationships(_sheetNames.Count));
        _zip.AddStored("xl/styles.xml", XlsxParts.Styles);
        _zip.Complete();
    }

    private string? WriteInline(string value)
    {
        if (value.Length > MaxTextChars)
        {
            return ErrorCodes.Write.TextTooLong;
        }

        StartCell();
        _row.Append(" t=\"inlineStr\"><is><t");

        if (value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])))
        {
            _row.Append(" xml:space=\"preserve\"");
        }

        _row.Append('>');
        AppendEscaped(value);
        _row.Append("</t></is></c>");
        return null;
    }

    /// <summary>Opens a cell at the current column, with its reference; the caller writes the rest.</summary>
    private void StartCell()
    {
        _row.Append("<c r=\"");
        _row.Append(_columnNames[_column]);
        _row.AppendFormatted(_rowNumber, default, CultureInfo.InvariantCulture);
        _row.Append('"');
        _column++;
    }

    private void AppendEscaped(ReadOnlySpan<char> text)
    {
        while (true)
        {
            int at = text.IndexOfAny(NeedsEscape);

            if (at < 0)
            {
                _row.Append(text);
                return;
            }

            _row.Append(text[..at]);

            switch (text[at])
            {
                case '&':
                    _row.Append("&amp;");
                    break;
                case '<':
                    _row.Append("&lt;");
                    break;
                case '>':
                    _row.Append("&gt;");
                    break;
                case '\r':
                    _row.Append("_x000D_");
                    break;
                default:
                    // An underscore: escaped only where it starts "_x", which a reader would decode.
                    _row.Append(at + 1 < text.Length && text[at + 1] == 'x' ? "_x005F_" : "_");
                    break;
            }

            text = text[(at + 1)..];
        }
    }

    private void AppendWidths(ReadOnlySpan<WriteColumn> columns)
    {
        bool any = false;

        for (int i = 0; i < columns.Length; i++)
        {
            if (columns[i].Width is not { } width)
            {
                continue;
            }

            if (!any)
            {
                _row.Append("<cols>");
                any = true;
            }

            _row.Append("<col min=\"");
            _row.AppendFormatted(i + 1, default, CultureInfo.InvariantCulture);
            _row.Append("\" max=\"");
            _row.AppendFormatted(i + 1, default, CultureInfo.InvariantCulture);
            _row.Append("\" width=\"");
            _row.AppendFormatted(width, "R", CultureInfo.InvariantCulture);
            _row.Append("\" customWidth=\"1\"/>");
        }

        if (any)
        {
            _row.Append("</cols>");
        }
    }

    /// <summary>Encodes what the row buffer holds into the open sheet entry, and empties it.</summary>
    private void Emit()
    {
        _row.WriteUtf8To(_bytes);
        _sheet!.Write(_bytes.WrittenSpan);
        _bytes.ResetWrittenCount();
        _row.Clear();

        if (_bytes.Capacity > RetainedBytes)
        {
            _bytes = new ArrayBufferWriter<byte>(16 * 1024);
        }
    }

    private void CloseSheet()
    {
        if (_sheet is null)
        {
            return;
        }

        _row.Clear();
        _row.Append(XlsxParts.WorksheetEnd);
        Emit();
        _zip.EndEntry();
        _sheet = null;
    }
}
```

The four `NotSupportedException` members exist only between this commit and Task 6's; Task 6 replaces them. No test in this task calls them.

- [ ] **Step 5: Let `Create` build an xlsx writer**

In `src/TriasDev.Tabular/Writing/TabularWriter.cs`, add `using TriasDev.Tabular.Xlsx;`. In `Create`, after the `effective.Csv is null` check, add the same check for `effective.Xlsx` (`ArgumentNullException` naming `TabularWriterOptions.Xlsx`, with the same S3928 pragma), and in the `switch` before `default:`:

```csharp
                case TabularFormat.Xlsx:
                    XlsxWriterOptions xlsx = effective.Xlsx.Checked();
                    SpillBuffer workbook = new();
                    return new TabularWriter(stream, format, workbook, new XlsxSheetWriter(workbook, xlsx), effective.LeaveOpen);
```

Update `Create`'s `<param name="format">` doc to: `The format to write: csv or xlsx in this version.`

- [ ] **Step 6: Run the tests**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~Writing|FullyQualifiedName~TabularIndependenceTests"`
Expected: PASS. If `OoxmlValidation.Errors` reports an error, fix the part it names — every element order in `XlsxParts` follows the schema's sequence (`numFmts, fonts, fills, borders, cellStyleXfs, cellXfs, cellStyles` in styles; `cols` before `sheetData`); never filter validator errors out.

- [ ] **Step 7: Commit**

```bash
dotnet format TriasDev.Tabular.slnx --verify-no-changes
git add src/TriasDev.Tabular tests/TriasDev.Tabular.Tests
git commit -m "feat(write): stream xlsx workbooks — inline text, booleans, several sheets, widths"
```

---

### Task 6: Numbers and dates in xlsx

**Files:**
- Modify: `src/TriasDev.Tabular/Xlsx/XlsxSheetWriter.cs` (replace the four interim members; add `WriteNumber` and `Serial`)
- Test: `tests/TriasDev.Tabular.Tests/Writing/XlsxValueTests.cs`

**Interfaces:**
- Consumes: `XlsxSheetWriter` (Task 5), `ValueChecks` (part 1: `TabularWriter` has already refused non-finite and >15-digit doubles and truncated dates to the millisecond before calling the sheet writer).
- Produces: the xlsx value rules of the spec — `write.precision-loss` for a `long` `double` cannot hold exactly and for a `decimal` with `(decimal)(double)v != v`; `write.date-out-of-range` before 1900-01-01; styles 1 (date), 2 (date-time), 3 (integer).

- [ ] **Step 1: Write the failing tests**

Create `tests/TriasDev.Tabular.Tests/Writing/XlsxValueTests.cs`:

```csharp
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>How an xlsx writer writes numbers and dates, and which it refuses.</summary>
public sealed class XlsxValueTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<RawCell> One(Action<TabularWriter> write)
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Xlsx))
        {
            writer.BeginSheet("data", [new("v")]);
            writer.BeginRow();
            write(writer);
            writer.EndRow();
            await writer.CompleteAsync(Token);
        }

        byte[] xlsx = target.ToArray();
        Assert.Empty(OoxmlValidation.Errors(xlsx));

        using XlsxCursor cursor = new(new MemoryStream(xlsx, writable: false), cancellationToken: Token);
        Assert.True(cursor.ReadRow(Token));
        Assert.True(cursor.ReadRow(Token));
        return cursor.CurrentRow[0];
    }

    private static DateTime At(int year, int month, int day, int hour = 0, int minute = 0, int second = 0, int millisecond = 0) =>
        new(year, month, day, hour, minute, second, millisecond, DateTimeKind.Unspecified);

    public static TheoryData<string, Action<TabularWriter>, RawCell> Written => new()
    {
        { "long", w => w.Write(42L), RawCell.FromNumber(42) },
        { "2^53", w => w.Write(9_007_199_254_740_992L), RawCell.FromNumber(9_007_199_254_740_992d) },
        { "2^60, exact in a double", w => w.Write(1L << 60), RawCell.FromNumber(1L << 60) },
        { "long.MinValue", w => w.Write(long.MinValue), RawCell.FromNumber(long.MinValue) },
        { "decimal", w => w.Write(1234.5m), RawCell.FromNumber(1234.5) },
        { "decimal of 15 digits", w => w.Write(123_456_789_012.345m), RawCell.FromNumber(123_456_789_012.345) },
        { "double", w => w.Write(-0.1), RawCell.FromNumber(-0.1) },
        { "date", w => w.Write(At(2026, 10, 3)), RawCell.FromDate(At(2026, 10, 3)) },
        { "date-time", w => w.Write(At(2026, 10, 3, 14, 5, 6, 789)), RawCell.FromDate(At(2026, 10, 3, 14, 5, 6, 789)) },
        { "16:00, a fraction stored a hair low", w => w.Write(At(2026, 10, 3, 16, 0, 0)), RawCell.FromDate(At(2026, 10, 3, 16, 0, 0)) },
        { "first day", w => w.Write(At(1900, 1, 1)), RawCell.FromDate(At(1900, 1, 1)) },
        { "max value", w => w.Write(DateTime.MaxValue), RawCell.FromDate(At(9999, 12, 31, 23, 59, 59, 999)) },
        { "date only", w => w.Write(new DateOnly(2026, 10, 3)), RawCell.FromDate(At(2026, 10, 3)) },
    };

    [Theory]
    [MemberData(nameof(Written))]
    public async Task WritesEachKindOfValueAsTheCursorReadsIt(string kind, Action<TabularWriter> write, RawCell expected)
    {
        Assert.NotEmpty(kind);
        Assert.Equal(expected, await One(write));
    }

    [Fact]
    public async Task ReadsBackDatesAcrossTheLeapYearBug()
    {
        DateTime[] dates =
        [
            At(1900, 2, 28),
            At(1900, 2, 28, 23, 59, 59, 999),
            At(1900, 3, 1),
            At(1900, 3, 1, 0, 0, 0, 1),
            At(1904, 1, 1),
        ];

        foreach (DateTime date in dates)
        {
            Assert.Equal(RawCell.FromDate(date), await One(w => w.Write(date)));
        }
    }

    public static TheoryData<string, Action<TabularWriter>, string> Refused => new()
    {
        { "2^53 + 1", w => w.Write(9_007_199_254_740_993L), ErrorCodes.Write.PrecisionLoss },
        { "long.MaxValue", w => w.Write(long.MaxValue), ErrorCodes.Write.PrecisionLoss },
        { "16 significant digits", w => w.Write(1_234_567_890_123.456m), ErrorCodes.Write.PrecisionLoss },
        { "decimal.MaxValue", w => w.Write(decimal.MaxValue), ErrorCodes.Write.PrecisionLoss },
        { "before 1900", w => w.Write(At(1899, 12, 31)), ErrorCodes.Write.DateOutOfRange },
        { "date only before 1900", w => w.Write(new DateOnly(1899, 12, 31)), ErrorCodes.Write.DateOutOfRange },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task RefusesAValueAWorkbookWouldNotGiveBack(string kind, Action<TabularWriter> write, string code)
    {
        Assert.NotEmpty(kind);
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);
        writer.BeginSheet("data", [new("Id"), new("Amount")]);
        writer.BeginRow();
        writer.Write("x");

        TabularWriteException refused = Assert.Throws<TabularWriteException>(() => write(writer));

        Assert.Equal(code, refused.Code);
        Assert.Equal(1, refused.ColumnIndex);
        Assert.Equal(2, refused.RowNumber);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~XlsxValueTests"`
Expected: FAIL with `NotSupportedException`.

- [ ] **Step 3: Implement**

In `src/TriasDev.Tabular/Xlsx/XlsxSheetWriter.cs`, add the constants and fields:

```csharp
    private const int DateStyle = 1;
    private const int DateTimeStyle = 2;
    private const int IntegerStyle = 3;

    /// <summary>The first day a workbook holds, as the reader reads serials.</summary>
    private static readonly DateTime FirstDay = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    /// <summary>The first day after Excel's phantom 29 February 1900, from which serials count from 30 December 1899.</summary>
    private static readonly DateTime AfterLeapBug = new(1900, 3, 1, 0, 0, 0, DateTimeKind.Unspecified);

    private static readonly long EarlyEpochTicks = new DateTime(1899, 12, 31, 0, 0, 0, DateTimeKind.Unspecified).Ticks;

    private static readonly long EpochTicks = new DateTime(1899, 12, 30, 0, 0, 0, DateTimeKind.Unspecified).Ticks;
```

Replace the four interim members with:

```csharp
    public string? WriteLong(long value)
    {
        // The reader parses a number cell as a double: a long that a double cannot hold exactly would
        // come back as its neighbour. 2^63 itself is out of long's range, hence the first test.
        double asDouble = value;

        if (asDouble >= 9.2233720368547758E18 || (long)asDouble != value)
        {
            return ErrorCodes.Write.PrecisionLoss;
        }

        WriteNumber(IntegerStyle);
        _row.AppendFormatted(value, default, CultureInfo.InvariantCulture);
        _row.Append("</v></c>");
        return null;
    }

    public string? WriteDecimal(decimal value)
    {
        // The import reads the cell's double back into a decimal by this same cast.
        if ((decimal)(double)value != value)
        {
            return ErrorCodes.Write.PrecisionLoss;
        }

        WriteNumber(style: 0);
        _row.AppendFormatted(value, default, CultureInfo.InvariantCulture);
        _row.Append("</v></c>");
        return null;
    }

    public string? WriteDouble(double value)
    {
        WriteNumber(style: 0);
        _row.AppendFormatted(value, "R", CultureInfo.InvariantCulture);
        _row.Append("</v></c>");
        return null;
    }

    public string? WriteDate(DateTime value, bool hasTime)
    {
        if (value < FirstDay)
        {
            return ErrorCodes.Write.DateOutOfRange;
        }

        WriteNumber(hasTime ? DateTimeStyle : DateStyle);
        _row.AppendFormatted(Serial(value), "R", CultureInfo.InvariantCulture);
        _row.Append("</v></c>");
        return null;
    }
```

and add:

```csharp
    /// <summary>Opens a number cell in a style, up to its value.</summary>
    private void WriteNumber(int style)
    {
        StartCell();

        if (style != 0)
        {
            _row.Append(" s=\"");
            _row.Append((char)('0' + style));
            _row.Append('"');
        }

        _row.Append("><v>");
    }

    /// <summary>
    /// The workbook serial of a date already truncated to the millisecond, counted as the reader
    /// counts it: from 31 December 1899 before Excel's phantom leap day, from 30 December after it.
    /// </summary>
    /// <remarks>
    /// Milliseconds over milliseconds per day: the reader multiplies back and rounds to the
    /// millisecond, and at 2.6 × 10^14 milliseconds for 9999-12-31 the double's precision leaves
    /// that rounding exact.
    /// </remarks>
    private static double Serial(DateTime value)
    {
        long epoch = value < AfterLeapBug ? EarlyEpochTicks : EpochTicks;
        long milliseconds = (value.Ticks - epoch) / TimeSpan.TicksPerMillisecond;
        return milliseconds / 86_400_000d;
    }
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~XlsxValueTests|FullyQualifiedName~XlsxWriterTests"`
Expected: PASS. If a date in `ReadsBackDatesAcrossTheLeapYearBug` comes back off by a day or a millisecond, compare `Serial` with `XlsxCursor.TryFromSerial` (src/TriasDev.Tabular/Xlsx/XlsxCursor.cs) — the writer must invert exactly what the reader does; never change the reader.

- [ ] **Step 5: Commit**

```bash
dotnet format TriasDev.Tabular.slnx --verify-no-changes
git add src/TriasDev.Tabular/Xlsx/XlsxSheetWriter.cs tests/TriasDev.Tabular.Tests/Writing/XlsxValueTests.cs
git commit -m "feat(write): numbers and dates in xlsx, refusing what a workbook would not give back"
```

---

### Task 7: The xlsx round trip, the row limit, and the docs

**Files:**
- Test: `tests/TriasDev.Tabular.Tests/Writing/XlsxRoundTripTests.cs`
- Modify: `docs/error-codes.md` (the `write.*` row names xlsx's causes)
- Modify: `docs/KNOWN-ISSUES.md` (the writing section: xlsx has no exception beyond the shared ones — say so in one line)

**Interfaces:**
- Consumes: everything above; `TabularImporter.Import<T>(Stream, string, MappingPlan, ImportSchema, TabularRowMapper<T>, ImportOptions?, CancellationToken)`, `ImportRun<T>.ReadRows`, the typed `ImportRow` indexers.
- Produces: the proof that xlsx meets the spec's round-trip rule; nothing new in `src/`.

- [ ] **Step 1: Write the tests**

Create `tests/TriasDev.Tabular.Tests/Writing/XlsxRoundTripTests.cs`:

```csharp
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>What the xlsx writer writes, the import reads back as the same value of the same type.</summary>
public sealed class XlsxRoundTripTests
{
    private static readonly TextImportField NameField = ImportField.Text("Name");
    private static readonly IntegerImportField CountField = ImportField.Integer("Count");
    private static readonly DecimalImportField AmountField = ImportField.Decimal("Amount");
    private static readonly DateImportField StartField = ImportField.Date("Start");
    private static readonly BooleanImportField ActiveField = ImportField.Boolean("Active");

    private static readonly ImportSchema Schema = new() { Fields = [NameField, CountField, AmountField, StartField, ActiveField] };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed record Row(string? Name, long? Count, decimal? Amount, DateTime? Start, bool? Active);

    private static DateTime At(int year, int month, int day, int hour = 0, int minute = 0, int second = 0, int millisecond = 0) =>
        new(year, month, day, hour, minute, second, millisecond, DateTimeKind.Unspecified);

    private static async Task<byte[]> WriteAsync(Action<TabularWriter> rows, string sheet = "data")
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Xlsx))
        {
            writer.BeginSheet(sheet, [.. Schema.Fields.Select(f => new WriteColumn(f.Name))]);
            rows(writer);
            await writer.CompleteAsync(Token);
        }

        return target.ToArray();
    }

    private static List<ImportOutcome<Row>> Import(byte[] file, int sheetIndex = 0)
    {
        MappingPlan plan = new()
        {
            SheetIndex = sheetIndex,
            Bindings = [.. Schema.Fields.Select((f, i) => new ColumnBinding { ColumnIndex = i, Header = f.Name, FieldName = f.Name })],
        };

        using ImportRun<Row> run = TabularImporter.Import(
            new MemoryStream(file, writable: false),
            "t.xlsx",
            plan,
            Schema,
            row => new Row(row[NameField], row[CountField], row[AmountField], row[StartField], row[ActiveField]),
            cancellationToken: Token);

        return [.. run.ReadRows(Token)];
    }

    private static void WriteRow(TabularWriter writer, Row row)
    {
        writer.BeginRow();
        writer.Write(row.Name);
        writer.Write(row.Count!.Value);
        writer.Write(row.Amount!.Value);
        writer.Write(row.Start!.Value);
        writer.Write(row.Active!.Value);
        writer.EndRow();
    }

    [Fact]
    public async Task ReadsBackEveryKindOfValue()
    {
        Row[] written =
        [
            new("Alpha", 0, 0.1m, At(2026, 10, 3), true),
            new("Grüße, \"quoted\"; | & <tag>", -1, -1_234_567.891m, At(2026, 10, 3, 14, 5, 6, 789), false),
            new("007", long.MinValue, 123_456_789_012.345m, At(1900, 1, 1), true),
            new("two\nlines and\r\nwindows and a lone\rreturn", 9_007_199_254_740_992, 1.50m, At(1900, 3, 1, 0, 0, 0, 1), false),
            new("emoji 👍 and _x0001_ and tab\there", 1L << 60, 0m, At(9999, 12, 31, 23, 59, 59, 999), true),
            new("a   run   of   spaces", 42, -0.000001m, At(1900, 2, 28, 23, 59, 59, 999), false),
        ];

        byte[] file = await WriteAsync(writer =>
        {
            foreach (Row row in written)
            {
                WriteRow(writer, row);
            }
        });

        Assert.Empty(OoxmlValidation.Errors(file));

        List<ImportOutcome<Row>> read = Import(file);

        Assert.All(read, outcome => Assert.False(outcome.HasErrors, string.Join(", ", outcome.Errors.Select(e => e.Code))));
        Assert.Equal(written, read.Select(outcome => outcome.Value));
    }

    [Fact]
    public async Task ReadsBackADoubleAsTheDecimalItsDigitsSay()
    {
        double[] values = [0.1, -2.5, 1e20, 1e-7, 123_456_789.012345];

        byte[] file = await WriteAsync(writer =>
        {
            foreach (double value in values)
            {
                writer.BeginRow();
                writer.Write("x");
                writer.WriteEmpty();
                writer.Write(value);
                writer.EndRow();
            }
        });

        Assert.Equal(values.Select(v => (decimal?)(decimal)v), Import(file).Select(outcome => outcome.Value!.Amount));
    }

    [Fact]
    public async Task ReadsBackEmptyAndWhitespaceTextAsNoValueAndSkipsAnEmptyRow()
    {
        byte[] file = await WriteAsync(writer =>
        {
            foreach (string? text in new[] { null, "", "   ", " padded " })
            {
                writer.BeginRow();
                writer.Write(text);
                writer.Write(1L);
                writer.EndRow();
            }

            writer.BeginRow();
            writer.EndRow();
        });

        MappingPlan plan = new() { Bindings = [.. Schema.Fields.Select((f, i) => new ColumnBinding { ColumnIndex = i, Header = f.Name, FieldName = f.Name })] };
        using ImportRun<string?> run = TabularImporter.Import(new MemoryStream(file, writable: false), "t.xlsx", plan, Schema, row => row[NameField], cancellationToken: Token);

        Assert.Equal(new string?[] { null, null, null, "padded" }, run.ReadRows(Token).Select(outcome => outcome.Value));
        Assert.Equal(1, run.Summary.RowsSkipped);
    }

    [Fact]
    public async Task ImportsEachSheetOfAWorkbook()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Xlsx))
        {
            foreach (string sheet in new[] { "First", "Second" })
            {
                writer.BeginSheet(sheet, [.. Schema.Fields.Select(f => new WriteColumn(f.Name))]);
                WriteRow(writer, new Row(sheet, 1, 1m, At(2026, 1, 1), true));
            }

            await writer.CompleteAsync(Token);
        }

        Assert.Equal("First", Assert.Single(Import(target.ToArray(), 0)).Value!.Name);
        Assert.Equal("Second", Assert.Single(Import(target.ToArray(), 1)).Value!.Name);
    }

    [Fact]
    public async Task AMillionRowSheetReadsBackToItsLastRow()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Xlsx))
        {
            writer.BeginSheet("data", [new("n")]);

            for (long n = 1; n < 1_048_576; n++)
            {
                writer.BeginRow();
                writer.Write(n);
                writer.EndRow();

                if (writer.FlushRecommended)
                {
                    await writer.FlushAsync(Token);
                }
            }

            Assert.Throws<TabularLimitException>(() => writer.BeginRow());
        }

        // The writer is faulted by the refusal, so the file is incomplete by design; write it again,
        // completed, without the refused row.
        target = new WriteTarget();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Xlsx))
        {
            writer.BeginSheet("data", [new("n")]);

            for (long n = 1; n < 1_048_576; n++)
            {
                writer.BeginRow();
                writer.Write(n);
                writer.EndRow();

                if (writer.FlushRecommended)
                {
                    await writer.FlushAsync(Token);
                }
            }

            await writer.CompleteAsync(Token);
        }

        using XlsxCursor cursor = new(new MemoryStream(target.ToArray(), writable: false), cancellationToken: Token);
        RawCell last = default;
        int rows = 0;

        while (cursor.ReadRow(Token))
        {
            rows++;
            last = cursor.CurrentRow[0];
        }

        Assert.Equal(1_048_576, rows);
        Assert.Equal(RawCell.FromNumber(1_048_575), last);
    }

    [Theory]
    [InlineData(67)]
    [InlineData(71)]
    [InlineData(2026)]
    public async Task WhatTheWriterAcceptsTheImportReadsBack(int seed)
    {
        Random random = new(seed);
        const string alphabet = "abcXYZ019 ,;\t|\"\n<>&_xüß👍\r";
        List<Row> accepted = [];

        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Xlsx))
        {
            writer.BeginSheet("data", [.. Schema.Fields.Select(f => new WriteColumn(f.Name))]);

            for (int i = 0; i < 300; i++)
            {
                string text = new([.. Enumerable.Range(0, random.Next(1, 40)).Select(_ => alphabet[random.Next(alphabet.Length)])]);

                // An emoji is two chars; a draw that split it is not text anyone writes.
                if (TextRules.Check(text) is not null)
                {
                    continue;
                }

                Row row = new(
                    text,
                    random.NextInt64(-(1L << 53), 1L << 53),
                    Math.Round((decimal)(random.NextDouble() * 2_000_000 - 1_000_000), random.Next(0, 6)),
                    At(random.Next(1900, 10_000), random.Next(1, 13), random.Next(1, 29), random.Next(0, 24), random.Next(0, 60), random.Next(0, 60), random.Next(0, 1000)),
                    random.Next(2) == 0);

                WriteRow(writer, row);
                accepted.Add(row with { Name = string.IsNullOrWhiteSpace(row.Name) ? null : row.Name.Trim() });
            }

            await writer.CompleteAsync(Token);
        }

        List<ImportOutcome<Row>> read = Import(target.ToArray());

        Assert.Equal(accepted.Count, read.Count);

        for (int i = 0; i < accepted.Count; i++)
        {
            Assert.False(read[i].HasErrors, $"seed {seed}, row {i}: {string.Join(", ", read[i].Errors.Select(e => e.Code))}");
            Assert.True(accepted[i] == read[i].Value, $"seed {seed}, row {i}: wrote {accepted[i]}, read {read[i].Value}");
        }
    }
}
```

The fuzz uses `TextRules.Check`, which is internal — allowed (InternalsVisibleTo).

- [ ] **Step 2: Run them**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~XlsxRoundTripTests"`
Expected: PASS. These tests prove earlier tasks, so a failure is a finding: **stop and report which value, what was written (the sheet XML of that row: unzip `xl/worksheets/sheet1.xml`) and what came back.** Do not loosen an expectation. `AMillionRowSheetReadsBackToItsLastRow` takes a few seconds; that is expected.

- [ ] **Step 3: Update the docs**

In `docs/error-codes.md`, extend the `write.*` row's meaning so it names the xlsx causes; append to that cell: `In xlsx: a long beyond 2^53 or a decimal past 15 significant digits (precision-loss), a date before 1900-01-01 (date-out-of-range), text over 32,767 characters (text-too-long).`

In `docs/KNOWN-ISSUES.md`, under the writing section added in part 1, append one line: `xlsx follows the same rules; it adds no exception of its own.`

- [ ] **Step 4: Run the whole suite and the Release build**

Run: `dotnet test --solution TriasDev.Tabular.slnx`, then `dotnet build TriasDev.Tabular.slnx -c Release`, then `dotnet format TriasDev.Tabular.slnx --verify-no-changes`.
Expected: all green, 0 warnings, no format changes.

- [ ] **Step 5: Commit**

```bash
git add tests docs/error-codes.md docs/KNOWN-ISSUES.md
git commit -m "test(write): xlsx round-trips through the import, to the last of a million rows"
```

---

## After the last task

One pull request for part 2 (`feat(write): write xlsx through TabularWriter, with our own streaming zip writer`), body referencing #71 and #67; merge when CI is green. Before the release (not in this PR): open one written workbook in Excel by hand, as the spec says. Then the part 3 plan (ods) builds on `ZipWriter.AddStored` (the `mimetype`), `ZipWriter.BeginDeflated` (`content.xml`), `RowText`, `SheetNames` and `NamesSheets`.
