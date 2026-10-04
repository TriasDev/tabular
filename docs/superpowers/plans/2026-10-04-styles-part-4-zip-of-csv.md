# Styled export, part 4 — a zip of csv sheets Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `TabularWriter.Create(stream, TabularFormat.Zip)` writes every sheet as `<sheet name>.csv` inside one zip, streamed and asynchronous, so a multi-sheet export past the xlsx row limit (or simply as csv) is one file that `TabularFile.Open` reads back sheet for sheet.

**Architecture:** The csv writer writes into an `IBufferWriter<byte>` instead of the `SpillBuffer` type, so the same `CsvSheetWriter` can write into a zip entry: a small `StreamBufferWriter` adapter hands it a reused byte buffer and pushes every committed span into the entry's deflate stream. `ZipCsvSheetWriter` (an `ISheetWriter`) opens one deflated entry per sheet on our `ZipWriter` and delegates every cell to a `CsvSheetWriter`; csv's rules (quoting, BOM, `FormulaGuard`, culture, delimiter, merges as value + empty fields, styles ignored) apply unchanged. Only csv goes into the zip — no other entry kinds, no other archive formats.

**Tech Stack:** C# / .NET (net8.0 + net10.0), BCL only; xunit v3 on Microsoft.Testing.Platform; `System.IO.Compression.ZipArchive` in tests only, as an independent reader.

**Spec:** `docs/superpowers/specs/2026-10-04-styled-columnar-export-design.md` §4 (parts 1–3 merged). Issue #89. Decided with the user (2026-10-04): zip is written for csv sheets only.

## Global Constraints

- One package, no dependencies; public types in `TriasDev.Tabular` (format options in their format namespace, like `TriasDev.Tabular.Xlsx.XlsxWriterOptions`); every public member in `PublicAPI.Unshipped.txt`.
- Analyzers warnings-as-errors (Sonar S3776, S3358, S1192 … refactor, don't suppress without a measured reason); `dotnet format --verify-no-changes` passes.
- The writer stays asynchronous towards the target: format writers write synchronously into the `SpillBuffer`; only `FlushAsync`/`CompleteAsync` touch the stream. The zip goes through `ZipWriter` (deflate streamed with data descriptors, zip64 past 4 GB) — never `System.IO.Compression.ZipArchive`, which writes synchronously.
- Entry name: the sheet name + `.csv`, encoded UTF-8; the zip "language encoding" flag (general purpose bit 11, `0x0800`) is set on every entry whose name is not pure ASCII, in the local header and the central directory. ASCII names stay byte-identical to today (xlsx and ods output must not change).
- Sheet names for zip: the workbook rules (`SheetNames.Problem`: 1–31 characters, none of `[ ] : * ? / \`, no control characters, no leading or trailing apostrophe, not "History", unique ignoring case) **plus** none of `< > " |` and no trailing `.` or space — characters a file system refuses in a file name once the zip is unpacked. Refused with `ArgumentException` from `BeginSheet`, faulting the writer like every other refusal.
- Each entry is a complete csv file under `CsvWriterOptions` (BOM per entry when set, delimiter, culture, `FormulaGuard`, CR LF). No row limit. Styles, merges' covered cells, freeze and filter behave exactly as for csv (styles and layout ignored; a merge writes its value and empty fields).
- `TabularWriterOptions.Zip` (`ZipWriterOptions` in namespace `TriasDev.Tabular.Archive`) with `CompressionLevel` (default `CompressionLevel.Fastest`, like xlsx and ods); null refused like the other options.
- Nothing allocated per cell; the adapter reuses one buffer.
- Re-import through `TabularFile.Open` gives the same sheets **by name**; the archive reader orders sheets by entry path, so the order is not promised (documented).
- The repository is public: no product or customer names anywhere.

Commands: build `dotnet build -c Release`; one class `dotnet test --project tests/TriasDev.Tabular.Tests -c Release -- --filter-class "*Name"`; full suite `TABULAR_REQUIRE_SOFFICE=1 dotnet test -c Release`; format `dotnet format --verify-no-changes`. Never run LibreOffice's bundled Python.

## Review Focus

1. A sheet named with non-ASCII letters ("Grüße", "Данные") reads back with that name through our reader and through `System.IO.Compression.ZipArchive`. (Task 1, Task 2)
2. Two sheets whose names differ only in case, or a name that becomes a reserved file name character problem, are refused before anything is written for that sheet. (Task 2)
3. A sheet with a header only (no data rows) is an entry with just the header line and reads back as a sheet with no data. (Task 2)
4. A failed write inside the second sheet leaves the writer faulted and the zip incomplete — no central directory, so no reader takes it for a whole file. (Task 2)
5. `TabularExport<T>.WriteSheetAsync` and `ColumnBatch` write into a zip like into any multi-sheet format. (Task 3)

---

### Task 1: The csv writer writes into any buffer writer; UTF-8 entry names in `ZipWriter`

**Files:**
- Create: `src/TriasDev.Tabular/Writing/StreamBufferWriter.cs`
- Modify: `src/TriasDev.Tabular/Csv/CsvSheetWriter.cs`, `src/TriasDev.Tabular/Writing/ZipWriter.cs`
- Test: `tests/TriasDev.Tabular.Tests/Writing/ZipWriterTests.cs`, `tests/TriasDev.Tabular.Tests/Writing/StreamBufferWriterTests.cs`

**Interfaces:**
- Produces:
  - `CsvSheetWriter(IBufferWriter<byte> output, CsvFormat format)` (was `SpillBuffer`; `SpillBuffer` implements `IBufferWriter<byte>`, so `TabularWriter.Create` keeps passing it).
  - `internal sealed class StreamBufferWriter : IBufferWriter<byte>` — `StreamBufferWriter(int initialSize = 16 * 1024)`, `Stream? Target { get; set; }` (where committed bytes go), `GetSpan`/`GetMemory` return the reused buffer (grown when a larger hint asks for it), `Advance(count)` writes `buffer[..count]` to `Target` at once.
  - `ZipWriter` entry names: UTF-8 bytes, flag `0x0800` added to the entry's flags when any byte ≥ 0x80.

- [ ] **Step 1: Failing tests**

`ZipWriterTests` (append):

```csharp
    [Theory]
    [InlineData("Grüße.csv")]
    [InlineData("Данные.csv")]
    [InlineData("数据.csv")]
    public async Task AnyZipReaderReadsANonAsciiEntryName(string name)
    {
        byte[] zip = await Zip(writer =>
        {
            using (Stream entry = writer.BeginDeflated(name))
            {
                entry.Write("a,b\r\n"u8);
            }

            writer.EndEntry();
            writer.AddStored("stored-" + name, "x"u8);
        });

        using ZipArchive archive = new(new MemoryStream(zip, writable: false), ZipArchiveMode.Read);
        Assert.Equal([name, "stored-" + name], archive.Entries.Select(e => e.FullName));
        Assert.Equal(0x0800, BitConverter.ToUInt16(zip, 6) & 0x0800);       // the first local header's flags
    }

    [Fact]
    public async Task AnAsciiNameLeavesTheFlagsAsTheyWere()
    {
        byte[] zip = await Zip(writer => writer.AddStored("mimetype", "x"u8));

        Assert.Equal(0, BitConverter.ToUInt16(zip, 6));
    }
```

Use the class's existing helper that builds a zip from an `Action<ZipWriter>` (read the top of `ZipWriterTests.cs`; if it is named differently or has a different shape, adapt the call and keep the assertions). The existing `IsReproducibleByteForByte` and every xlsx/ods test must stay green: ASCII names do not change a byte.

`StreamBufferWriterTests`:

```csharp
using System.Buffers;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>A buffer writer that pushes every committed span into a stream at once.</summary>
public sealed class StreamBufferWriterTests
{
    [Fact]
    public void WritesWhatIsCommittedAndReusesItsBuffer()
    {
        MemoryStream target = new();
        StreamBufferWriter writer = new(initialSize: 8) { Target = target };

        Span<byte> first = writer.GetSpan(3);
        "abc"u8.CopyTo(first);
        writer.Advance(3);
        Span<byte> second = writer.GetSpan(2);
        "de"u8.CopyTo(second);
        writer.Advance(2);

        Assert.Equal("abcde"u8.ToArray(), target.ToArray());
    }

    [Fact]
    public void GrowsForALargerHint()
    {
        MemoryStream target = new();
        StreamBufferWriter writer = new(initialSize: 4) { Target = target };

        Span<byte> span = writer.GetSpan(100);
        Assert.True(span.Length >= 100);
        span[..100].Fill((byte)'x');
        writer.Advance(100);

        Assert.Equal(100, target.Length);
    }

    [Fact]
    public void RefusesToCommitMoreThanItLent()
    {
        StreamBufferWriter writer = new(initialSize: 4) { Target = new MemoryStream() };
        writer.GetSpan(4);

        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Advance(5_000));
    }
}
```

- [ ] **Step 2: Run to verify they fail** → compile errors / the non-ASCII test fails (names come out as `?`).

- [ ] **Step 3: Implement**

`ZipWriter`: replace `Encoding.ASCII.GetBytes(name)` with UTF-8 in both `AddStored` and `BeginDeflated`, and add the flag:

```csharp
    /// <summary>The "language encoding" flag: the entry's name is UTF-8.</summary>
    private const ushort Utf8NameFlag = 0x0800;

    private static (byte[] Name, ushort Flag) EncodeName(string name)
    {
        byte[] encoded = Encoding.UTF8.GetBytes(name);
        return (encoded, encoded.AsSpan().IndexOfAnyInRange((byte)0x80, (byte)0xFF) >= 0 ? Utf8NameFlag : (ushort)0);
    }
```

`AddStored` writes `flags: nameFlag` and records it in the `Entry`; `BeginDeflated` writes `DescriptorFlag | nameFlag` and `EndEntry` records the same (keep the flags with the open `EntryStream`). The central header already writes `entry.Flags`.

`StreamBufferWriter`:

```csharp
using System.Buffers;

namespace TriasDev.Tabular;

/// <summary>
/// An <see cref="IBufferWriter{T}"/> over a stream: lends one reused buffer and writes every
/// committed span to <see cref="Target"/> at once — the way a formatter that writes into a buffer
/// writer can write into a zip entry's deflate stream.
/// </summary>
internal sealed class StreamBufferWriter(int initialSize = 16 * 1024) : IBufferWriter<byte>
{
    private byte[] _buffer = new byte[initialSize];

    /// <summary>Where committed bytes go.</summary>
    public Stream? Target { get; set; }

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _buffer.Length);
        Target!.Write(_buffer, 0, count);
    }

    public Memory<byte> GetMemory(int sizeHint = 0) => Lend(sizeHint);

    public Span<byte> GetSpan(int sizeHint = 0) => Lend(sizeHint);

    private byte[] Lend(int sizeHint)
    {
        if (sizeHint > _buffer.Length)
        {
            _buffer = new byte[Math.Max(sizeHint, _buffer.Length * 2)];
        }

        return _buffer;
    }
}
```

(A buffer that grew for one huge row stays large; `RowText` already caps what it keeps for the next row, so the encoded size it asks for stays bounded too. If the csv writer's huge-row test shows otherwise, shrink back the same way `XlsxSheetWriter.Emit` does.)

`CsvSheetWriter`: the field and constructor parameter become `IBufferWriter<byte> _out`; `_out.Write(Utf8Bom)` uses the `BuffersExtensions.Write` extension (`using System.Buffers;` is already there). Nothing else changes.

- [ ] **Step 4: Run** `--filter-class "*ZipWriterTests"`, `"*StreamBufferWriterTests"`, `"*Csv*"`, `"*Xlsx*"`, `"*Ods*"` and the full suite → PASS (xlsx/ods/csv output unchanged).

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat(write): UTF-8 zip entry names; the csv writer writes into any buffer writer"
```

---

### Task 2: `TabularFormat.Zip` — every sheet a csv entry

**Files:**
- Create: `src/TriasDev.Tabular/Archive/ZipCsvSheetWriter.cs`, `src/TriasDev.Tabular/Archive/ZipWriterOptions.cs`
- Modify: `src/TriasDev.Tabular/Writing/TabularWriter.cs` (`Create`), `src/TriasDev.Tabular/Writing/TabularWriterOptions.cs`, `src/TriasDev.Tabular/Writing/ISheetWriter.cs`, `src/TriasDev.Tabular/Writing/SheetNames.cs` (or a zip-specific rule beside it), `PublicAPI.Unshipped.txt`
- Test: `tests/TriasDev.Tabular.Tests/Writing/ZipCsvWriterTests.cs`

**Interfaces:**
- Consumes: Task 1's `CsvSheetWriter(IBufferWriter<byte>, CsvFormat)`, `StreamBufferWriter`, UTF-8 `ZipWriter`.
- Produces:
  - `public sealed record ZipWriterOptions` (namespace `TriasDev.Tabular.Archive`): `static ZipWriterOptions Default`, `CompressionLevel CompressionLevel { get; init; } = CompressionLevel.Fastest`, `internal ZipWriterOptions Checked()` (same shape as `XlsxWriterOptions.Checked`).
  - `TabularWriterOptions.Zip` (`ZipWriterOptions`, default `ZipWriterOptions.Default`), checked for null in `Create` like the others.
  - `ISheetWriter.NameProblem(string name) → string?`: a format's own refusal of a sheet name beyond `SheetNames.Problem`; csv, xlsx and ods return null; the zip writer refuses `< > " |` and a trailing `.` or space. `TabularWriter.BeginSheet` checks it after `SheetNames.Problem`, with the same `ArgumentException` + fault.

- [ ] **Step 1: Failing tests**

```csharp
using System.IO.Compression;
using System.Text;

using TriasDev.Tabular.Archive;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>A zip of csv sheets: one entry per sheet, read back sheet for sheet.</summary>
public sealed class ZipCsvWriterTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static void ThreeSheets(TabularWriter writer)
    {
        writer.BeginSheet("Data", [new("Id"), new("Name"), new("Score")]);

        for (int i = 1; i <= 3; i++)
        {
            writer.BeginRow();
            writer.Write((long)i);
            writer.Write($"Location {i}");
            writer.Write(i / 2.0);
            writer.EndRow();
        }

        writer.BeginSheet("Grüße", [new("Key"), new("Value")]);
        writer.BeginRow();
        writer.Write("a,b");
        writer.Write("line\nbreak");
        writer.EndRow();

        writer.BeginSheet("Empty", [new("Only header")]);
    }

    private static Dictionary<string, string> Entries(byte[] zip)
    {
        using ZipArchive archive = new(new MemoryStream(zip, writable: false), ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(e => e.FullName, e =>
        {
            using StreamReader reader = new(e.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            return reader.ReadToEnd();
        });
    }

    [Fact]
    public async Task WritesEverySheetAsACsvEntry()
    {
        byte[] zip = await SheetLayoutTests.Write(TabularFormat.Zip, ThreeSheets);
        Dictionary<string, string> entries = Entries(zip);

        Assert.Equal(["Data.csv", "Grüße.csv", "Empty.csv"], entries.Keys);
        Assert.Equal("﻿Id,Name,Score\r\n1,Location 1,0.5\r\n2,Location 2,1\r\n3,Location 3,1.5\r\n", entries["Data.csv"]);
        Assert.Equal("﻿Key,Value\r\n\"a,b\",\"line\nbreak\"\r\n", entries["Grüße.csv"]);
        Assert.Equal("﻿Only header\r\n", entries["Empty.csv"]);
    }

    [Fact]
    public async Task EachEntryIsTheCsvFileTheSameSheetWouldBe()
    {
        byte[] zip = await SheetLayoutTests.Write(TabularFormat.Zip, ThreeSheets);
        byte[] csv = await SheetLayoutTests.Write(TabularFormat.Csv, writer =>
        {
            writer.BeginSheet("Data", [new("Id"), new("Name"), new("Score")]);

            for (int i = 1; i <= 3; i++)
            {
                writer.BeginRow();
                writer.Write((long)i);
                writer.Write($"Location {i}");
                writer.Write(i / 2.0);
                writer.EndRow();
            }
        });

        using ZipArchive archive = new(new MemoryStream(zip, writable: false), ZipArchiveMode.Read);
        using MemoryStream entry = new();
        await archive.GetEntry("Data.csv")!.Open().CopyToAsync(entry, Token);
        Assert.Equal(csv, entry.ToArray());
    }

    [Fact]
    public async Task TheImportReadsTheSheetsBackByName()
    {
        byte[] zip = await SheetLayoutTests.Write(TabularFormat.Zip, ThreeSheets);

        using ITabularCursor cursor = TabularFile.Open(new MemoryStream(zip, writable: false), "export.zip", cancellationToken: Token);
        Dictionary<string, List<RawCell[]>> sheets = [];

        for (int i = 0; cursor.MoveToSheet(i, Token); i++)
        {
            List<RawCell[]> rows = [];

            while (cursor.ReadRow(Token))
            {
                rows.Add(cursor.CurrentRow.ToArray());
            }

            sheets[cursor.Sheets[i].Name] = rows;
        }

        Assert.Equal(["Data", "Empty", "Grüße"], sheets.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(RawCell.FromText("Location 2"), sheets["Data"][2][1]);
        Assert.Equal(RawCell.FromText("line\nbreak"), sheets["Grüße"][1][1]);
        Assert.Single(sheets["Empty"]);
    }

    [Theory]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a\"b")]
    [InlineData("a|b")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    public async Task ANameAFileSystemRefusesIsRefused(string name)
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Zip);

        Assert.Throws<ArgumentException>(() => writer.BeginSheet(name, [new("a")]));
    }

    [Fact]
    public async Task NamesDifferingOnlyInCaseAreRefused()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Zip);
        writer.BeginSheet("Data", [new("a")]);

        Assert.Throws<ArgumentException>(() => writer.BeginSheet("DATA", [new("a")]));
    }

    [Fact]
    public async Task OtherFormatsAcceptThoseNames()
    {
        byte[] xlsx = await SheetLayoutTests.Write(TabularFormat.Xlsx, writer => writer.BeginSheet("a|b.", [new("a")]));

        Assert.NotEmpty(xlsx);
    }

    [Fact]
    public async Task AFailureInTheSecondSheetLeavesNoCentralDirectory()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Zip))
        {
            writer.BeginSheet("First", [new("a")]);
            writer.BeginRow();
            writer.Write("ok");
            writer.EndRow();
            writer.BeginSheet("Second", [new("a")]);
            writer.BeginRow();
            Assert.Throws<TabularWriteException>(() => writer.Write(0.1 + 0.2));
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await writer.CompleteAsync(Token));
        }

        Assert.Throws<InvalidDataException>(() => new ZipArchive(new MemoryStream(target.ToArray()), ZipArchiveMode.Read));
    }

    [Fact]
    public async Task TheCsvOptionsApplyToEveryEntry()
    {
        TabularWriterOptions options = new() { Csv = new CsvWriterOptions { ByteOrderMark = false, Delimiter = ';' } };
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Zip, options))
        {
            writer.BeginSheet("One", [new("a"), new("b")]);
            writer.BeginSheet("Two", [new("c"), new("d")]);
            await writer.CompleteAsync(Token);
        }

        Dictionary<string, string> entries = Entries(target.ToArray());
        Assert.Equal("a;b\r\n", entries["One.csv"]);
        Assert.Equal("c;d\r\n", entries["Two.csv"]);
    }

    [Fact]
    public async Task ACompressionLevelOutsideTheEnumIsRefused()
    {
        TabularWriterOptions options = new() { Zip = new ZipWriterOptions { CompressionLevel = (CompressionLevel)42 } };

        Assert.Throws<ArgumentOutOfRangeException>(() => TabularWriter.Create(new WriteTarget(), TabularFormat.Zip, options));
    }

    [Fact]
    public async Task StylesAndLayoutAreIgnoredAsInCsv()
    {
        CellStyle red = new() { Fill = CellColor.FromRgb(0xFF0000) };

        byte[] plain = await SheetLayoutTests.Write(TabularFormat.Zip, writer =>
        {
            writer.BeginSheet("Data", [new("a"), new("b")]);
            writer.BeginRow();
            writer.Write("x");
            writer.Write("y");
            writer.EndRow();
        });

        byte[] laidOut = await SheetLayoutTests.Write(TabularFormat.Zip, writer =>
        {
            StyleId style = writer.Style(red);
            writer.BeginSheet("Data", [new("a"), new("b")], new SheetOptions { HeaderStyle = red, FreezeRows = 1, AutoFilter = true });
            writer.BeginRow();
            writer.Write("x", style);
            writer.Write("y", style);
            writer.EndRow();
        });

        Assert.Equal(plain, laidOut);
    }
}
```

Check against the code before running:
- `CsvWriterOptions` property names (`ByteOrderMark`, `Delimiter`): read `src/TriasDev.Tabular/Csv/CsvWriterOptions.cs` and use the real names; keep the meaning.
- `cursor.Sheets[i].Name` and `TabularFile.Open(stream, name, cancellationToken:)`: use the members `ITabularCursor` actually has (the archive cursor exposes its sheets' names; `SheetLayoutTests.Rows` shows how a sheet is read).
- `AFailureInTheSecondSheetLeavesNoCentralDirectory`: what `ZipArchive` throws for a zip without its end record is `InvalidDataException`; if the runtime throws another type, assert that type and say so.
- `OtherFormatsAcceptThoseNames`: `a|b.` is a valid workbook sheet name by `SheetNames.Problem` (only `[ ] : * ? / \` are refused there); confirm, and if not, pick a name with `|` and a trailing `.` that the workbook rules accept.

- [ ] **Step 2: Run to verify they fail** → compile errors (`TabularFormat.Zip` not writable, `ZipWriterOptions` missing).

- [ ] **Step 3: `ZipWriterOptions`** — in `src/TriasDev.Tabular/Archive/ZipWriterOptions.cs`, mirroring `XlsxWriterOptions` (summary: "How a zip of csv sheets is written."; `CompressionLevel` doc: "How hard each entry is compressed; `Fastest` by default — a zip is written for its size, but most of a csv's size goes at the fastest level already.").

- [ ] **Step 4: `ZipCsvSheetWriter`**

```csharp
using System.Buffers;
using System.IO.Compression;

using TriasDev.Tabular.Csv;

namespace TriasDev.Tabular.Archive;

/// <summary>
/// Writes a zip of csv files: each sheet one deflated entry, <c>&lt;sheet name&gt;.csv</c>, holding
/// exactly the csv file a single-sheet export of it would be.
/// </summary>
/// <remarks>
/// The csv writer formats into a buffer writer whose committed bytes go straight into the open
/// entry's deflate stream, so nothing is buffered per sheet. The archive reader reads such a zip back
/// as a workbook, a sheet per entry, named after the entry without <c>.csv</c>.
/// </remarks>
internal sealed class ZipCsvSheetWriter : ISheetWriter
{
    private static readonly SearchValues<char> FileNameForbidden = SearchValues.Create("<>\"|");

    private readonly ZipWriter _zip;
    private readonly StreamBufferWriter _entryBuffer = new();
    private readonly CsvSheetWriter _csv;
    private Stream? _entry;

    public ZipCsvSheetWriter(SpillBuffer output, ZipWriterOptions options, CsvFormat format)
    {
        _zip = new ZipWriter(output, options.CompressionLevel);
        _csv = new CsvSheetWriter(_entryBuffer, format);
    }

    public long MaxRows => long.MaxValue;

    public bool AllowsSeveralSheets => true;

    public bool NamesSheets => true;

    public int MaxMerges => int.MaxValue;

    public string? NameProblem(string name)
    {
        if (name.AsSpan().IndexOfAny(FileNameForbidden) >= 0)
        {
            return $"The sheet name \"{name}\" holds one of < > \" |, which a file system refuses in the entry's file name.";
        }

        return name[^1] is '.' or ' '
            ? $"The sheet name \"{name}\" ends with a dot or a space, which a file system drops from the entry's file name."
            : null;
    }

    public void BeginSheet(string name, ReadOnlySpan<WriteColumn> columns, SheetOptions options)
    {
        CloseEntry();
        _entry = _zip.BeginDeflated(name + ".csv");
        _entryBuffer.Target = _entry;
        _csv.BeginSheet(name, columns, options);
    }

    // Every cell, row and merge call delegates to _csv: BeginRow, WriteHeader, WriteText, WriteLong,
    // WriteDecimal, WriteDouble, WriteDate, WriteBoolean, WriteEmpty, WriteCovered, Merge, EndRow.

    public void Complete()
    {
        CloseEntry();
        _zip.Complete();
    }

    /// <summary>Releases the open entry's deflate state, writing nothing more: the file is abandoned.</summary>
    public void Dispose()
    {
        _entry?.Dispose();
        _entry = null;
        _entryBuffer.Target = null;
    }

    private void CloseEntry()
    {
        if (_entry is null)
        {
            return;
        }

        _zip.EndEntry();
        _entry = null;
        _entryBuffer.Target = null;
    }
}
```

Write the delegating members out in full (one line each). `CsvSheetWriter.AllowsSeveralSheets` is false, but it is never asked here: the zip writer answers for the format, and `CsvSheetWriter.BeginSheet` writes only the BOM — calling it once per entry gives each entry its BOM. If `CsvSheetWriter` keeps per-sheet state that a second `BeginSheet` must reset (column count, first-cell flag), check it resets correctly; add a reset if not.

- [ ] **Step 5: `NameProblem` on `ISheetWriter`** — add `string? NameProblem(string name);` with the doc "A refusal of a sheet name beyond the workbook rules, for a format whose names become something else (a file name); null when the name is fine." Csv, xlsx and ods return `null`. In `TabularWriter.BeginSheet`, right after the `SheetNames.Problem` check: `if (_sheet.NamesSheets && _sheet.NameProblem(name) is { } formatProblem) throw Faulting(new ArgumentException(formatProblem, nameof(name)));`.

- [ ] **Step 6: `Create`** — `TabularWriterOptions.Zip` (doc: "How a zip of csv sheets is written: its compression; each sheet's csv follows `Csv`."), null-checked like `Csv`/`Xlsx`/`Ods`; a `case TabularFormat.Zip:` building `new ZipCsvSheetWriter(buffer, effective.Zip.Checked(), effective.Csv.Resolve())`. Update `Create`'s docs ("csv, a zip of csv sheets, xlsx or ods") and `TabularFormat.Zip`'s doc remark (written as csv sheets).

- [ ] **Step 7: Run** `--filter-class "*ZipCsvWriterTests"` and the full suite → PASS. Public API, format.

- [ ] **Step 8: Commit**

```bash
git add src tests
git commit -m "feat(write): TabularFormat.Zip — every sheet a csv entry in one streamed zip"
```

---

### Task 3: Batches, exports and wide sheets into a zip; allocation; documentation

**Files:**
- Modify: `tests/TriasDev.Tabular.Tests/Writing/WideExportTests.cs`, `tests/TriasDev.Tabular.Tests/Writing/ZipCsvWriterTests.cs`, `docs/KNOWN-ISSUES.md`, `README.md` (only if it lists the writable formats)
- Test: as listed

- [ ] **Step 1: Tests**
- `WideExportTests`: add `[InlineData(TabularFormat.Zip)]` to `AThousandRowsOfFiveThousandColumnsReadBack` and `ABatchAllocatesNothingPerCell`. For zip, `SheetLayoutTests.Rows` reads sheet 0 of the archive, which is the only sheet; the expected cells are those of csv (text). If the read-back helper branches on `TabularFormat.Csv` for text cells, make it treat `Zip` the same way.
- `ZipCsvWriterTests` (append):

```csharp
    private sealed record Item(long Id, string Name);

    private static readonly TabularExport<Item> Export = TabularExport.For<Item>().Column("Id", i => i.Id).Column("Name", i => i.Name).Build();

    [Fact]
    public async Task AnExportWritesSeveralSheetsIntoOneZip()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Zip))
        {
            await Export.WriteSheetAsync(writer, "First", new[] { new Item(1, "a") }, Token);
            await Export.WriteSheetAsync(writer, "Second", new[] { new Item(2, "b"), new Item(3, "c") }, Token);
            await writer.CompleteAsync(Token);
        }

        Dictionary<string, string> entries = Entries(target.ToArray());
        Assert.Equal("﻿Id,Name\r\n1,a\r\n", entries["First.csv"]);
        Assert.Equal("﻿Id,Name\r\n2,b\r\n3,c\r\n", entries["Second.csv"]);
    }

    [Fact]
    public async Task AFiveHundredThousandRowSheetStreamsWithFlatMemory()
    {
        WriteTarget target = new();
        long[] ids = [.. Enumerable.Range(0, 10_000).Select(i => (long)i)];
        double[] scores = [.. Enumerable.Range(0, 10_000).Select(i => i / 4.0)];
        int largestPending = 0;

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Zip))
        {
            writer.BeginSheet("Big", [new("Id"), new("Score")]);
            ColumnBatch batch = new();

            for (int chunk = 0; chunk < 50; chunk++)
            {
                batch.Reset(ids.Length);
                batch.Add(ids);
                batch.Add(scores);
                await writer.WriteBatchAsync(batch, Token);
                largestPending = Math.Max(largestPending, writer.PendingBytes);
            }

            writer.BeginSheet("Small", [new("a")]);
            await writer.CompleteAsync(Token);
        }

        Assert.True(largestPending < 2 * 1024 * 1024, $"{largestPending:N0} bytes pending after a batch");

        using ZipArchive archive = new(new MemoryStream(target.ToArray(), writable: false), ZipArchiveMode.Read);
        using StreamReader reader = new(archive.GetEntry("Big.csv")!.Open());
        int lines = 0;

        while (await reader.ReadLineAsync(Token) is not null)
        {
            lines++;
        }

        Assert.Equal(500_001, lines);
    }
```

`writer.PendingBytes` does not exist: add `internal int PendingBytes => (int)Math.Min(int.MaxValue, _buffer.Pending);` to `TabularWriter` as a test hook (the buffer's pending count is what `FlushRecommended` compares against the 1 MB threshold), or assert the same thing another way the codebase already supports — and say which in the report.

- [ ] **Step 2: Run** `--filter-class "*WideExportTests"`, `"*ZipCsvWriterTests"`, the full suite → PASS.

- [ ] **Step 3: Documentation** — in `docs/KNOWN-ISSUES.md`, under the writing section, add:

```markdown
### A zip of csv sheets

- `TabularFormat.Zip` writes every sheet as `<sheet name>.csv` in one zip — csv sheets only, each exactly the csv file that sheet alone would be (`CsvWriterOptions` apply to every entry). Use it for several sheets past the xlsx row limit, or wherever csv is wanted but one file holds several tables.
- Sheet names follow the workbook rules and, since they become file names, may not hold `< > " |` or end with a dot or a space. Names are stored as UTF-8.
- Reading the zip back gives the same sheets by name. Their order follows the entries' paths, not the order they were written in.
- The zip is written by the library's own streaming zip writer — asynchronously, so it can go straight into an ASP.NET Core response — with zip64 past 4 GB.
```

If `README.md` lists the formats the library writes, add the zip of csv sheets there in one phrase; otherwise leave README to part 5.

- [ ] **Step 4: Format, commit**

```bash
git add tests docs README.md src
git commit -m "test(write): batches, exports and wide sheets into a zip of csv sheets; document it"
```
