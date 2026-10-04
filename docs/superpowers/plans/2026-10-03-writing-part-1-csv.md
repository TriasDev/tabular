# Writing — part 1: the writer and csv — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A caller writes a table to csv through `TabularWriter` — typed values, row by row, into any stream, flushing asynchronously — and importing the file through `TabularImporter` gives back the same values with the same types.

**Architecture:** `TabularWriter` (public, format-agnostic) owns the state machine, the row and column bookkeeping, the checks every format shares and the async flush. It writes through an internal `ISheetWriter`; this part adds the csv one, `CsvSheetWriter`, which formats a row into a reused `char[]` and encodes it as UTF-8 into `SpillBuffer`, a pooled in-memory stream that only `FlushAsync` drains into the target. `CsvWriterOptions` resolves to an internal `CsvFormat` where it is handed over, proving with a probe that the import reads its output back.

**Tech Stack:** .NET 8 + 10, base class library only, xunit v4 on Microsoft.Testing.Platform.

**Spec:** `docs/superpowers/specs/2026-10-03-writing-design.md` — this plan is delivery step 1 of 5. Steps 2–5 (zip writer and xlsx, ods, `TabularExport<T>`, benchmarks and docs) get their own plans, written against the interfaces this one produces.

## Global Constraints

- No package references in the library; only the base class library (`TabularIndependenceTests`).
- Error codes, never messages: every new code is a constant in `ErrorCodes`, a row in `docs/error-codes.md`, and matched by `ErrorCodeCatalogTests`.
- Every library exception derives from `TabularException`; `ArgumentException` / `InvalidOperationException` are for programmer errors only.
- The writer never writes to, flushes or disposes the target stream synchronously — except `Create`, which closes it with `Dispose()` when it fails before anything was written.
- A stream handed over is closed on every path, failures included, unless `LeaveOpen` is set.
- Options are checked where they are handed over (`TabularWriter.Create`), never discovered mid-write.
- No allocation per row on the write path: the row buffer and the UTF-8 output are reused; numbers and dates are formatted with `TryFormat` into the row buffer.
- Public API changes go in `src/TriasDev.Tabular/PublicAPI.Unshipped.txt`; the build fails (RS0016) until they do.
- Tests use only the public API, except `SpillBufferTests`, which tests an internal unit (`InternalsVisibleTo` is already set).
- The invariant culture is `""` (or null) everywhere.
- Nothing product-specific in code, docs or commits; generic names only (`Portfolios`, `data`, `Id`, `Name`, `Amount`).

## Rulings made while planning (deviations from or additions to the spec)

- **`LeaveOpen` lives on `TabularWriterOptions`**, as it does on `TabularOpenOptions`, not as a `Create` parameter: `TabularWriter.Create(Stream stream, TabularFormat format, TabularWriterOptions? options = null)`.
- **`CsvSheetWriter`, `CsvFormat` and `CsvWriterOptions` sit in `src/TriasDev.Tabular/Csv/`** (namespace `TriasDev.Tabular.Csv`), next to the cursor, as the spec does for options; the format-agnostic pieces are in `src/TriasDev.Tabular/Writing/` (namespace `TriasDev.Tabular`).
- **csv uses `write.text-too-long` too**, for text over the reader's `MaxFieldChars` (16,777,216): the spec's bounds section says the writer's limits match the reader's, and a longer field would come back as `limit.exceeded` on import.
- **Duplicate headers are refused** (`ArgumentException`), compared trimmed and ignoring case, as are empty or whitespace headers: the import binds by header and trims, so either would make the file ambiguous to read back.
- **A culture must use the Gregorian calendar** — the import reads dates through it, and a Hijri calendar cannot even format 9999-12-31.
- **The culture probe** writes −1,234,567.891 and −0.5 as decimals, −42 as an integer, 1801-02-13 14:05:06.789 as a date-time and 9999-12-31 as a date, and reads each back with `ValueReading.TryRead` — the import's own reader. 1801 and 9999 catch two-digit-year patterns; day 13 catches a day/month swap.
- **Xlsx and ods are refused by `Create` until parts 2 and 3** (`ArgumentOutOfRangeException` on `format`); `TabularFormat.Zip` is always refused. Nothing is released between parts.
- **`ColumnIndex` on `TabularWriteException` is zero-based**, as `ColumnBinding.ColumnIndex` is; `RowNumber` is a `long` and counts the header as row 1.

## Review Focus

1. **A target that fails mid-flush** (a client disconnects): `FlushAsync` throws the stream's exception, the writer is faulted, and `DisposeAsync` still closes the target without throwing again. → Task 4, `AFailingTargetFaultsTheWriterAndDisposalStillClosesIt`.
2. **Dispose twice, or dispose after complete with `LeaveOpen`**: idempotent, the target untouched the second time. → Task 4, `DisposingTwiceIsHarmless`.
3. **One enormous row** (a 10M-character note): the row buffer grows for it and must not stay that large for the rest of a 5M-row file. → Task 6, `AHugeRowDoesNotKeepItsBufferForTheRestOfTheFile` (internal check through `CsvSheetWriter.RowBufferLength`).
4. **A low-level caller who never flushes**: memory grows with the file; `FlushRecommended` is the signal, and it must turn true past 1 MB and false after a flush. → Task 4, `RecommendsAFlushPastAMegabyteAndNotAfterIt`.
5. **Cancellation during a flush**: `OperationCanceledException`, the writer faulted, the target holding only what earlier flushes wrote. → Task 4, `ACancelledFlushFaultsTheWriter`.

---

### Task 1: Error codes and `TabularWriteException`

**Files:**
- Modify: `src/TriasDev.Tabular/Abstractions/ErrorCodes.cs` (add class `Write` before `ReservedPrefixes`; add `"write."` to `ReservedPrefixes`)
- Modify: `src/TriasDev.Tabular/Abstractions/TabularException.cs` (add `TabularWriteException` after `TabularLimitException`; add a bullet to the base remarks)
- Modify: `docs/error-codes.md:25` (one row after `limit.exceeded`)
- Modify: `tests/TriasDev.Tabular.Tests/ErrorCodeCatalogTests.cs:16-22` (both regexes accept `write`)
- Modify: `src/TriasDev.Tabular/PublicAPI.Unshipped.txt`
- Test: `tests/TriasDev.Tabular.Tests/Writing/TabularWriteExceptionTests.cs`

**Interfaces:**
- Produces: `ErrorCodes.Write.PrecisionLoss` (`"write.precision-loss"`), `.NotFinite` (`"write.not-finite"`), `.DateOutOfRange` (`"write.date-out-of-range"`), `.TextTooLong` (`"write.text-too-long"`), `.TooManyLines` (`"write.too-many-lines"`), `.InvalidCharacter` (`"write.invalid-character"`); `TabularWriteException(string code, string sheetName, long rowNumber, int columnIndex, string header, string message)` with `SheetName`, `RowNumber`, `ColumnIndex`, `Header` and the inherited `Code`.

- [ ] **Step 1: Write the failing test**

Create `tests/TriasDev.Tabular.Tests/Writing/TabularWriteExceptionTests.cs`:

```csharp
using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>A value the format cannot hold is reported by code, with where it is.</summary>
public sealed class TabularWriteExceptionTests
{
    [Fact]
    public void CarriesTheCodeAndWhereTheValueIs()
    {
        TabularWriteException error = new(ErrorCodes.Write.PrecisionLoss, "data", 7, 2, "Amount", "message");

        Assert.IsAssignableFrom<TabularException>(error);
        Assert.Equal("write.precision-loss", error.Code);
        Assert.Equal("data", error.SheetName);
        Assert.Equal(7, error.RowNumber);
        Assert.Equal(2, error.ColumnIndex);
        Assert.Equal("Amount", error.Header);
    }

    [Fact]
    public void ReservesTheWritePrefix()
    {
        Assert.True(ErrorCodes.IsReserved("write.anything"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~TabularWriteExceptionTests"`
Expected: build FAIL — `TabularWriteException` and `ErrorCodes.Write` do not exist.

- [ ] **Step 3: Add the codes**

In `src/TriasDev.Tabular/Abstractions/ErrorCodes.cs`, before the `ReservedPrefixes` property:

```csharp
    /// <summary>A value the chosen format cannot hold exactly — TabularWriteException.</summary>
    public static class Write
    {
        /// <summary><c>write.date-out-of-range</c></summary>
        public const string DateOutOfRange = "write.date-out-of-range";

        /// <summary><c>write.invalid-character</c></summary>
        public const string InvalidCharacter = "write.invalid-character";

        /// <summary><c>write.not-finite</c></summary>
        public const string NotFinite = "write.not-finite";

        /// <summary><c>write.precision-loss</c></summary>
        public const string PrecisionLoss = "write.precision-loss";

        /// <summary><c>write.text-too-long</c></summary>
        public const string TextTooLong = "write.text-too-long";

        /// <summary><c>write.too-many-lines</c></summary>
        public const string TooManyLines = "write.too-many-lines";
    }
```

and change the prefixes line to:

```csharp
    public static IReadOnlyList<string> ReservedPrefixes { get; } = ["value.", "mapping.", "group.", "structure.", "format.", "limit.", "write."];
```

- [ ] **Step 4: Add the exception**

In `src/TriasDev.Tabular/Abstractions/TabularException.cs`, add to the base class's `<list type="bullet">` after the `TabularLimitException` item:

```csharp
/// <item><see cref="TabularWriteException"/> — a value the chosen format cannot hold exactly, found
/// while writing (500 for a server's own export: the data or the format choice must change).</item>
```

and after the `TabularLimitException` class:

```csharp
/// <summary>A value cannot be written in the chosen format without changing it.</summary>
/// <remarks>
/// Raised while writing, so part of the file is already out: the writer is faulted, and the caller
/// discards what it wrote — aborts the response, deletes the file. The code says what the format
/// could not hold; the sheet, row and column say where.
/// </remarks>
[SuppressMessage("Design", "RCS1194:Implement exception constructors", Justification = "Every instance carries a code a caller translates; a constructor without one would make an exception nobody can act on.")]
public sealed class TabularWriteException : TabularException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="code">One of the <see cref="ErrorCodes.Write"/> codes.</param>
    /// <param name="sheetName">The sheet being written.</param>
    /// <param name="rowNumber">The row as a spreadsheet counts it: the header is row 1.</param>
    /// <param name="columnIndex">The column, zero-based.</param>
    /// <param name="header">The column's header.</param>
    /// <param name="message">What was found, in English, for logs.</param>
    public TabularWriteException(string code, string sheetName, long rowNumber, int columnIndex, string header, string message)
        : base(code, message)
    {
        SheetName = sheetName;
        RowNumber = rowNumber;
        ColumnIndex = columnIndex;
        Header = header;
    }

    /// <summary>The sheet being written.</summary>
    public string SheetName { get; }

    /// <summary>The row as a spreadsheet counts it: the header is row 1.</summary>
    public long RowNumber { get; }

    /// <summary>The column, zero-based.</summary>
    public int ColumnIndex { get; }

    /// <summary>The column's header.</summary>
    public string Header { get; }
}
```

- [ ] **Step 5: Document the codes and teach the catalog test the prefix**

In `docs/error-codes.md`, after the `limit.exceeded` row (line 25):

```markdown
| `write.precision-loss`, `write.not-finite`, `write.date-out-of-range`, `write.text-too-long`, `write.too-many-lines`, `write.invalid-character` | `TabularWriteException`: a value the chosen format cannot hold exactly, found while writing; `SheetName`, `RowNumber`, `ColumnIndex` and `Header` say where. The file written so far is incomplete and must be discarded |
```

In `tests/TriasDev.Tabular.Tests/ErrorCodeCatalogTests.cs`, change both alternations `(?:value|mapping|group|structure|format|limit)` to `(?:value|mapping|group|structure|format|limit|write)`.

- [ ] **Step 6: Record the public API**

Append to `src/TriasDev.Tabular/PublicAPI.Unshipped.txt` (the build's RS0016 messages are authoritative; if a line differs, use the analyzer's):

```
const TriasDev.Tabular.ErrorCodes.Write.DateOutOfRange = "write.date-out-of-range" -> string!
const TriasDev.Tabular.ErrorCodes.Write.InvalidCharacter = "write.invalid-character" -> string!
const TriasDev.Tabular.ErrorCodes.Write.NotFinite = "write.not-finite" -> string!
const TriasDev.Tabular.ErrorCodes.Write.PrecisionLoss = "write.precision-loss" -> string!
const TriasDev.Tabular.ErrorCodes.Write.TextTooLong = "write.text-too-long" -> string!
const TriasDev.Tabular.ErrorCodes.Write.TooManyLines = "write.too-many-lines" -> string!
TriasDev.Tabular.ErrorCodes.Write
TriasDev.Tabular.TabularWriteException
TriasDev.Tabular.TabularWriteException.ColumnIndex.get -> int
TriasDev.Tabular.TabularWriteException.Header.get -> string!
TriasDev.Tabular.TabularWriteException.RowNumber.get -> long
TriasDev.Tabular.TabularWriteException.SheetName.get -> string!
TriasDev.Tabular.TabularWriteException.TabularWriteException(string! code, string! sheetName, long rowNumber, int columnIndex, string! header, string! message) -> void
```

- [ ] **Step 7: Run the tests**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~TabularWriteExceptionTests|FullyQualifiedName~ErrorCodeCatalogTests|FullyQualifiedName~ExceptionModelTests"`
Expected: PASS. If a test elsewhere pins the exact `ReservedPrefixes` list or the set of exception types, update it to include the new entry and say so in the commit body.

- [ ] **Step 8: Commit**

```bash
git add src/TriasDev.Tabular/Abstractions docs/error-codes.md tests/TriasDev.Tabular.Tests src/TriasDev.Tabular/PublicAPI.Unshipped.txt
git commit -m "feat(write): error codes and TabularWriteException for values a format cannot hold"
```

---

### Task 2: `SpillBuffer`

**Files:**
- Create: `src/TriasDev.Tabular/Writing/SpillBuffer.cs`
- Test: `tests/TriasDev.Tabular.Tests/Writing/SpillBufferTests.cs`

**Interfaces:**
- Produces (internal, namespace `TriasDev.Tabular`): `sealed class SpillBuffer : Stream, IBufferWriter<byte>` with `long Pending`, `long TotalWritten`, `Span<byte> GetSpan(int sizeHint = 0)`, `Memory<byte> GetMemory(int sizeHint = 0)`, `void Advance(int count)`, `void Write(ReadOnlySpan<byte>)`, `ValueTask DrainToAsync(Stream target, CancellationToken cancellationToken)`, `void Discard()`. Part 2's zip writer uses it as a `Stream` (for `DeflateStream`) and reads `TotalWritten` for offsets.

- [ ] **Step 1: Write the failing tests**

Create `tests/TriasDev.Tabular.Tests/Writing/SpillBufferTests.cs`:

```csharp
using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>The memory a writer writes into synchronously, drained into the target asynchronously.</summary>
public sealed class SpillBufferTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DrainsEverythingWrittenInOrder()
    {
        using SpillBuffer buffer = new();
        byte[] large = [.. Enumerable.Range(0, 200_000).Select(i => (byte)i)];

        buffer.Write([1, 2, 3]);
        buffer.Write(large);
        buffer.WriteByte(9);

        using MemoryStream target = new();
        await buffer.DrainToAsync(target, Token);

        Assert.Equal([1, 2, 3, .. large, 9], target.ToArray());
    }

    [Fact]
    public async Task CountsPendingUntilDrainedAndTotalForever()
    {
        using SpillBuffer buffer = new();
        buffer.Write(new byte[100]);

        Assert.Equal(100, buffer.Pending);

        await buffer.DrainToAsync(Stream.Null, Token);
        buffer.Write(new byte[5]);

        Assert.Equal(5, buffer.Pending);
        Assert.Equal(105, buffer.TotalWritten);
    }

    [Fact]
    public async Task HandsOutASpanAtLeastAsLargeAsAsked()
    {
        using SpillBuffer buffer = new();
        buffer.Write(new byte[65_000]);

        Span<byte> span = buffer.GetSpan(10_000);
        Assert.True(span.Length >= 10_000);
        span[..3].Fill(7);
        buffer.Advance(3);

        using MemoryStream target = new();
        await buffer.DrainToAsync(target, Token);

        Assert.Equal(65_003, target.Length);
        Assert.Equal([7, 7, 7], target.ToArray()[^3..]);
    }

    [Fact]
    public async Task DiscardDropsWhatIsPending()
    {
        using SpillBuffer buffer = new();
        buffer.Write(new byte[10]);

        buffer.Discard();

        using MemoryStream target = new();
        await buffer.DrainToAsync(target, Token);
        Assert.Equal(0, target.Length);
        Assert.Equal(0, buffer.Pending);
    }

    [Fact]
    public void RefusesToAdvancePastTheSpanItHandedOut()
    {
        using SpillBuffer buffer = new();
        int length = buffer.GetSpan(1).Length;

        Assert.Throws<InvalidOperationException>(() => buffer.Advance(length + 1));
    }

    [Fact]
    public void IsAWriteOnlyForwardOnlyStream()
    {
        using SpillBuffer buffer = new();

        Assert.True(buffer.CanWrite);
        Assert.False(buffer.CanRead);
        Assert.False(buffer.CanSeek);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~SpillBufferTests"`
Expected: build FAIL — `SpillBuffer` does not exist.

- [ ] **Step 3: Implement**

Create `src/TriasDev.Tabular/Writing/SpillBuffer.cs`:

```csharp
using System.Buffers;

namespace TriasDev.Tabular;

/// <summary>
/// Memory the format writers write into synchronously, emptied into the target asynchronously.
/// </summary>
/// <remarks>
/// <para>
/// The split is what lets a writer serve an ASP.NET Core response body, which refuses synchronous
/// writes: the csv text, the sheet XML and the zip writer are synchronous code, and only
/// <see cref="DrainToAsync"/> ever touches the target.
/// </para>
/// <para>
/// Pooled segments rather than one growing array, so a buffer that grows never copies what it holds
/// and never lands on the large object heap. Bounded by the caller: a writer drains it whenever it
/// passes the flush threshold.
/// </para>
/// </remarks>
internal sealed class SpillBuffer : Stream, IBufferWriter<byte>
{
    private const int SegmentSize = 64 * 1024;

    private readonly List<byte[]> _segments = [];
    private readonly List<int> _used = [];

    /// <summary>Bytes written and not yet drained.</summary>
    public long Pending { get; private set; }

    /// <summary>Bytes written over the buffer's life, drained or not — the offsets a zip writer records.</summary>
    public long TotalWritten { get; private set; }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        int needed = Math.Max(sizeHint, 1);
        int last = _segments.Count - 1;

        if (last < 0 || _segments[last].Length - _used[last] < needed)
        {
            _segments.Add(ArrayPool<byte>.Shared.Rent(Math.Max(needed, SegmentSize)));
            _used.Add(0);
            last++;
        }

        return _segments[last].AsMemory(_used[last]);
    }

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        int last = _segments.Count - 1;

        if (last < 0 || _used[last] + count > _segments[last].Length)
        {
            throw new InvalidOperationException("Advanced past the span the buffer handed out.");
        }

        _used[last] += count;
        Pending += count;
        TotalWritten += count;
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            Span<byte> target = GetSpan();
            int count = Math.Min(target.Length, buffer.Length);
            buffer[..count].CopyTo(target);
            Advance(count);
            buffer = buffer[count..];
        }
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void WriteByte(byte value)
    {
        GetSpan(1)[0] = value;
        Advance(1);
    }

    public override void Flush()
    {
        // Nothing to do: draining is DrainToAsync's job, and only the writer decides when.
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>Writes everything pending into the target, in order, and empties the buffer.</summary>
    public async ValueTask DrainToAsync(Stream target, CancellationToken cancellationToken)
    {
        for (int i = 0; i < _segments.Count; i++)
        {
            await target.WriteAsync(_segments[i].AsMemory(0, _used[i]), cancellationToken).ConfigureAwait(false);
        }

        Release();
    }

    /// <summary>Drops everything pending, unwritten.</summary>
    public void Discard() => Release();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Release();
        }

        base.Dispose(disposing);
    }

    private void Release()
    {
        foreach (byte[] segment in _segments)
        {
            ArrayPool<byte>.Shared.Return(segment);
        }

        _segments.Clear();
        _used.Clear();
        Pending = 0;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~SpillBufferTests"`
Expected: PASS on net8.0 and net10.0. Fix any analyzer error the build reports by following its message; do not suppress one without a `Justification` that says why.

- [ ] **Step 5: Commit**

```bash
git add src/TriasDev.Tabular/Writing/SpillBuffer.cs tests/TriasDev.Tabular.Tests/Writing/SpillBufferTests.cs
git commit -m "feat(write): a pooled buffer written synchronously and drained asynchronously"
```

---

### Task 3: Options — `WriteColumn`, `TabularWriterOptions`, `CsvWriterOptions`

**Files:**
- Create: `src/TriasDev.Tabular/Writing/WriteColumn.cs`
- Create: `src/TriasDev.Tabular/Writing/TabularWriterOptions.cs`
- Create: `src/TriasDev.Tabular/Csv/CsvWriterOptions.cs`
- Create: `src/TriasDev.Tabular/Csv/CsvFormat.cs`
- Modify: `src/TriasDev.Tabular/PublicAPI.Unshipped.txt`
- Test: `tests/TriasDev.Tabular.Tests/Writing/CsvWriterOptionsTests.cs`

**Interfaces:**
- Consumes: `CultureCatalog.TryGet(string?, out CultureInfo)`, `ValueReading.TryRead(in RawCell, string, ColumnType, CultureInfo, out MappedValue)`, `MappedValue.FromDecimal/FromInteger/FromDate`.
- Produces: `public readonly record struct WriteColumn(string Header, double? Width = null)`; `public sealed record TabularWriterOptions { static Default; CsvWriterOptions Csv; bool LeaveOpen }`; `public sealed record CsvWriterOptions { static Default; string? Culture; char? Delimiter; bool ByteOrderMark = true; bool FormulaGuard; internal CsvFormat Resolve() }`; internal `sealed class CsvFormat` with `CultureInfo Culture`, `char Delimiter`, `string DateFormat`, `string DateTimeFormat`, `bool ByteOrderMark`, `bool FormulaGuard`.

- [ ] **Step 1: Write the failing tests**

Create `tests/TriasDev.Tabular.Tests/Writing/CsvWriterOptionsTests.cs`:

```csharp
using System.Globalization;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>
/// The csv options resolve to a format the import reads back — or are refused where they are handed
/// over, naming the option.
/// </summary>
public sealed class CsvWriterOptionsTests
{
    [Fact]
    public void DefaultsToInvariantCommaIsoDatesAndAByteOrderMark()
    {
        CsvFormat format = CsvWriterOptions.Default.Resolve();

        Assert.Same(CultureInfo.InvariantCulture, format.Culture);
        Assert.Equal(',', format.Delimiter);
        Assert.Equal("yyyy'-'MM'-'dd", format.DateFormat);
        Assert.Equal("yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fff", format.DateTimeFormat);
        Assert.True(format.ByteOrderMark);
        Assert.False(format.FormulaGuard);
    }

    [Fact]
    public void ACultureWithADecimalCommaDelimitsWithASemicolon()
    {
        CsvFormat format = new CsvWriterOptions { Culture = "de-DE" }.Resolve();

        Assert.Equal(';', format.Delimiter);
        Assert.Equal("dd.MM.yyyy", format.DateFormat);
        Assert.Equal("dd.MM.yyyy HH:mm:ss.fff", format.DateTimeFormat);
    }

    [Fact]
    public void ACultureWithADecimalPointKeepsTheComma()
    {
        Assert.Equal(',', new CsvWriterOptions { Culture = "en-US" }.Resolve().Delimiter);
    }

    [Theory]
    [InlineData(',')]
    [InlineData(';')]
    [InlineData('\t')]
    [InlineData('|')]
    public void TakesADelimiterTheReaderDetects(char delimiter)
    {
        Assert.Equal(delimiter, new CsvWriterOptions { Delimiter = delimiter }.Resolve().Delimiter);
    }

    [Theory]
    [InlineData(':')]
    [InlineData('"')]
    [InlineData('\n')]
    [InlineData('\r')]
    [InlineData(' ')]
    public void RefusesADelimiterTheReaderDoesNotDetect(char delimiter)
    {
        ArgumentException refused = Assert.ThrowsAny<ArgumentException>(() => new CsvWriterOptions { Delimiter = delimiter }.Resolve());

        Assert.Contains(nameof(CsvWriterOptions.Delimiter), refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesADelimiterThatIsTheCulturesDecimalSeparator()
    {
        ArgumentException refused = Assert.ThrowsAny<ArgumentException>(() => new CsvWriterOptions { Culture = "de-DE", Delimiter = ',' }.Resolve());

        Assert.Contains(nameof(CsvWriterOptions.Delimiter), refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesACultureThatDoesNotExist()
    {
        ArgumentException refused = Assert.ThrowsAny<ArgumentException>(() => new CsvWriterOptions { Culture = "xx-NOWHERE" }.Resolve());

        Assert.Contains(nameof(CsvWriterOptions.Culture), refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesACultureWhoseCalendarIsNotGregorian()
    {
        // th-TH counts years in the Buddhist era by default; the import reads dates in the Gregorian calendar.
        ArgumentException refused = Assert.ThrowsAny<ArgumentException>(() => new CsvWriterOptions { Culture = "th-TH" }.Resolve());

        Assert.Contains(nameof(CsvWriterOptions.Culture), refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("de-DE")]
    [InlineData("en-US")]
    [InlineData("en-GB")]
    [InlineData("fr-FR")]
    public void ResolvesTheCulturesTheImportReadsBack(string culture)
    {
        Assert.NotNull(new CsvWriterOptions { Culture = culture }.Resolve());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~CsvWriterOptionsTests"`
Expected: build FAIL — `CsvWriterOptions` does not exist.

- [ ] **Step 3: Implement `WriteColumn` and `TabularWriterOptions`**

Create `src/TriasDev.Tabular/Writing/WriteColumn.cs`:

```csharp
namespace TriasDev.Tabular;

/// <summary>A column a writer declares when it begins a sheet: its header, and how wide to show it.</summary>
/// <param name="Header">The header text, written as the sheet's first row. Not empty, and unique in its sheet.</param>
/// <param name="Width">
/// The width in characters, for workbook formats; csv has none. Null leaves it to the program that
/// opens the file — which shows a date-time in a default-width column as <c>#####</c>.
/// </param>
public readonly record struct WriteColumn(string Header, double? Width = null);
```

Create `src/TriasDev.Tabular/Writing/TabularWriterOptions.cs`:

```csharp
using TriasDev.Tabular.Csv;

namespace TriasDev.Tabular;

/// <summary>Knobs for writing a file, one set per format, as <see cref="TabularOpenOptions"/> has for reading.</summary>
public sealed record TabularWriterOptions
{
    /// <summary>The defaults: invariant csv with a byte order mark, and the target closed when the writer is.</summary>
    public static TabularWriterOptions Default { get; } = new();

    /// <summary>How a csv file is written.</summary>
    public CsvWriterOptions Csv { get; init; } = CsvWriterOptions.Default;

    /// <summary>Leaves the target stream open when the writer is disposed, or fails to be created.</summary>
    public bool LeaveOpen { get; init; }
}
```

- [ ] **Step 4: Implement `CsvFormat` and `CsvWriterOptions`**

Create `src/TriasDev.Tabular/Csv/CsvFormat.cs`:

```csharp
using System.Globalization;

namespace TriasDev.Tabular.Csv;

/// <summary>What <see cref="CsvWriterOptions"/> resolve to: everything the csv writer needs, checked.</summary>
internal sealed class CsvFormat(CultureInfo culture, char delimiter, string dateFormat, string dateTimeFormat, bool byteOrderMark, bool formulaGuard)
{
    /// <summary>The culture numbers and dates are formatted in.</summary>
    public CultureInfo Culture { get; } = culture;

    /// <summary>The field delimiter.</summary>
    public char Delimiter { get; } = delimiter;

    /// <summary>The format of a date without a time.</summary>
    public string DateFormat { get; } = dateFormat;

    /// <summary>The format of a date with a time, to the millisecond.</summary>
    public string DateTimeFormat { get; } = dateTimeFormat;

    /// <summary>Whether the file starts with the UTF-8 byte order mark.</summary>
    public bool ByteOrderMark { get; } = byteOrderMark;

    /// <summary>Whether text that a spreadsheet would run as a formula is prefixed with an apostrophe.</summary>
    public bool FormulaGuard { get; } = formulaGuard;
}
```

Create `src/TriasDev.Tabular/Csv/CsvWriterOptions.cs`:

```csharp
using System.Globalization;

namespace TriasDev.Tabular.Csv;

/// <summary>Knobs for writing a csv file.</summary>
/// <remarks>
/// Whatever is chosen, the import reads the file back: a culture is accepted only if a probe of its
/// numbers and dates, written as the writer writes them, reads back through the import's own reader.
/// </remarks>
public sealed record CsvWriterOptions
{
    private const string IsoDate = "yyyy'-'MM'-'dd";

    private const string IsoDateTime = "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fff";

    /// <summary>The defaults: invariant culture, comma, ISO dates, a byte order mark, no formula guard.</summary>
    public static CsvWriterOptions Default { get; } = new();

    /// <summary>
    /// The culture numbers and dates are written in, by name, or null or empty for the invariant one —
    /// decimal point, ISO dates.
    /// </summary>
    /// <remarks>
    /// <c>de-DE</c> writes <c>1234,56</c> and <c>03.10.2026</c>, which a German Excel opens by double
    /// click. Import such a file with the culture named in the plan rather than taken from analysis:
    /// when every day in a column is 12 or less, day-first and month-first cannot be told apart.
    /// </remarks>
    public string? Culture { get; init; }

    /// <summary>
    /// The field delimiter: one of <c>,</c> <c>;</c> tab <c>|</c>, the ones the reader detects. Null
    /// chooses <c>;</c> when the culture's decimal separator is a comma, otherwise <c>,</c>.
    /// </summary>
    public char? Delimiter { get; init; }

    /// <summary>
    /// Starts the file with the UTF-8 byte order mark — without it, Excel reads the file in the
    /// system's code page and mangles every umlaut.
    /// </summary>
    public bool ByteOrderMark { get; init; } = true;

    /// <summary>
    /// Prefixes text starting with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, tab or carriage return with
    /// an apostrophe, so a spreadsheet does not run it as a formula — the OWASP defence against csv
    /// injection.
    /// </summary>
    /// <remarks>
    /// Off by default because it changes the value the import reads back: <c>=1+2</c> returns as
    /// <c>'=1+2</c>. Turn it on when exporting other people's data to be opened in a spreadsheet.
    /// Numbers are never prefixed.
    /// </remarks>
    public bool FormulaGuard { get; init; }

    internal CsvFormat Resolve()
    {
        if (!CultureCatalog.TryGet(Culture, out CultureInfo culture))
        {
            throw new ArgumentException(
                $"{nameof(CsvWriterOptions)}.{nameof(Culture)} '{Culture}' is not a culture this runtime has.",
                "options");
        }

        if (culture.DateTimeFormat.Calendar is not GregorianCalendar)
        {
            throw new ArgumentException(
                $"{nameof(CsvWriterOptions)}.{nameof(Culture)} '{Culture}' does not use the Gregorian calendar, which the import reads dates in.",
                "options");
        }

        string separator = culture.NumberFormat.NumberDecimalSeparator;
        char delimiter = Delimiter ?? (separator == "," ? ';' : ',');

        if (delimiter is not (',' or ';' or '\t' or '|'))
        {
            throw new ArgumentOutOfRangeException(
                "options",
                delimiter,
                $"{nameof(CsvWriterOptions)}.{nameof(Delimiter)} must be one of , ; tab | — the delimiters the reader detects.");
        }

        if (separator.Contains(delimiter, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{nameof(CsvWriterOptions)}.{nameof(Delimiter)} '{delimiter}' is the decimal separator of culture '{Culture}'.",
                "options");
        }

        bool invariant = culture.Name.Length == 0;
        CsvFormat format = new(
            culture,
            delimiter,
            invariant ? IsoDate : culture.DateTimeFormat.ShortDatePattern,
            invariant ? IsoDateTime : culture.DateTimeFormat.ShortDatePattern + " HH:mm:ss.fff",
            ByteOrderMark,
            FormulaGuard);

        Probe(format);
        return format;
    }

    /// <summary>
    /// Writes a value of each kind as the writer would and reads it back with the import's reader.
    /// </summary>
    /// <remarks>
    /// 1801 and 9999 catch a pattern with a two-digit year; the 13th catches day and month swapped;
    /// the negatives catch a minus sign the reader does not take.
    /// </remarks>
    private static void Probe(CsvFormat format)
    {
        CultureInfo culture = format.Culture;
        DateTime moment = new(1801, 2, 13, 14, 5, 6, 789);
        DateTime last = new(9999, 12, 31);

        Check(format, ColumnType.Decimal, (-1_234_567.891m).ToString(culture), MappedValue.FromDecimal(-1_234_567.891m));
        Check(format, ColumnType.Decimal, (-0.5).ToString("R", culture), MappedValue.FromDecimal(-0.5m));
        Check(format, ColumnType.Integer, (-42L).ToString(culture), MappedValue.FromInteger(-42));
        Check(format, ColumnType.Date, moment.ToString(format.DateTimeFormat, culture), MappedValue.FromDate(moment));
        Check(format, ColumnType.Date, last.ToString(format.DateFormat, culture), MappedValue.FromDate(last));
    }

    private static void Check(CsvFormat format, ColumnType type, string text, MappedValue expected)
    {
        RawCell cell = RawCell.FromText(text);

        if (!ValueReading.TryRead(cell, cell.Text ?? string.Empty, type, format.Culture, out MappedValue read) || read != expected)
        {
            throw new ArgumentException(
                $"{nameof(CsvWriterOptions)}.{nameof(Culture)} '{format.Culture.Name}' writes a {type} the import does not read back: \"{text}\".",
                "options");
        }
    }
}
```

- [ ] **Step 5: Record the public API**

Append to `src/TriasDev.Tabular/PublicAPI.Unshipped.txt` every line RS0016 reports for `WriteColumn`, `TabularWriterOptions` and `CsvWriterOptions` (records generate `<Clone>$`, `Equals`, `GetHashCode`, `ToString`, `PrintMembers`-free operators, the `init` accessors; the record struct also `Deconstruct`). The fastest way: `dotnet format analyzers src/TriasDev.Tabular --diagnostics RS0016 --severity info`, then check the diff contains only these three types.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~CsvWriterOptionsTests"`
Expected: PASS. If `ResolvesTheCulturesTheImportReadsBack` fails for a culture, the probe found a real disagreement between how .NET formats and how the import reads: **stop and report the culture and the probe text** — do not loosen the probe.

- [ ] **Step 7: Commit**

```bash
git add src/TriasDev.Tabular/Writing src/TriasDev.Tabular/Csv tests/TriasDev.Tabular.Tests/Writing src/TriasDev.Tabular/PublicAPI.Unshipped.txt
git commit -m "feat(write): writer options, and csv options that prove the import reads them back"
```

---

### Task 4: `TabularWriter` and the csv sheet writer — text, booleans, empty cells

**Files:**
- Create: `src/TriasDev.Tabular/Writing/ISheetWriter.cs`
- Create: `src/TriasDev.Tabular/Writing/TabularWriter.cs`
- Create: `src/TriasDev.Tabular/Csv/CsvSheetWriter.cs`
- Create: `tests/TriasDev.Tabular.Tests/Fixtures/WriteTarget.cs`
- Modify: `src/TriasDev.Tabular/PublicAPI.Unshipped.txt`
- Test: `tests/TriasDev.Tabular.Tests/Writing/TabularWriterTests.cs`

**Interfaces:**
- Consumes: `SpillBuffer` (Task 2), `TabularWriterOptions`, `CsvWriterOptions.Resolve()`, `CsvFormat`, `WriteColumn` (Task 3), `TabularLimitException`.
- Produces: `public sealed class TabularWriter : IAsyncDisposable` with `static TabularWriter Create(Stream stream, TabularFormat format, TabularWriterOptions? options = null)`, `TabularFormat Format`, `bool FlushRecommended`, `void BeginSheet(string name, ReadOnlySpan<WriteColumn> columns)`, `void BeginRow()`, `void Write(string? value)`, `void Write(bool value)`, `void WriteEmpty()`, `void EndRow()`, `ValueTask FlushAsync(CancellationToken cancellationToken = default)`, `ValueTask CompleteAsync(CancellationToken cancellationToken = default)`, `ValueTask DisposeAsync()`. Internal `interface ISheetWriter` (members below; Task 5 adds the number and date members). Test fixture `WriteTarget`.

- [ ] **Step 1: Write the test fixture**

Create `tests/TriasDev.Tabular.Tests/Fixtures/WriteTarget.cs`:

```csharp
namespace TriasDev.Tabular.Tests.Fixtures;

/// <summary>
/// A write target that behaves like an ASP.NET Core response body: it cannot seek, a synchronous
/// write or flush throws, and a synchronous dispose is recorded.
/// </summary>
public sealed class WriteTarget : MemoryStream
{
    private bool _disposingAsync;

    /// <summary>Whether the stream was closed.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>Whether it was closed by <c>Dispose()</c> rather than <c>DisposeAsync()</c>.</summary>
    public bool DisposedSynchronously { get; private set; }

    /// <summary>How many times <c>FlushAsync</c> was called.</summary>
    public int AsyncFlushes { get; private set; }

    /// <summary>When set, every asynchronous write throws it — a client that went away.</summary>
    public Exception? FailWith { get; set; }

    public override bool CanSeek => false;

    public override void Write(byte[] buffer, int offset, int count) => throw Synchronous();

    public override void Write(ReadOnlySpan<byte> buffer) => throw Synchronous();

    public override void WriteByte(byte value) => throw Synchronous();

    public override void Flush() => throw Synchronous();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (FailWith is { } failure)
        {
            throw failure;
        }

        base.Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        AsyncFlushes++;
        return Task.CompletedTask;
    }

    public override async ValueTask DisposeAsync()
    {
        _disposingAsync = true;
        await base.DisposeAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposingAsync)
        {
            DisposedSynchronously = true;
        }

        IsDisposed = true;
        base.Dispose(disposing);
    }

    private static InvalidOperationException Synchronous() => new("Synchronous operations are disallowed.");
}
```

- [ ] **Step 2: Write the failing tests**

Create `tests/TriasDev.Tabular.Tests/Writing/TabularWriterTests.cs`:

```csharp
using System.Text;

using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>
/// What the writer promises regardless of format: the order of calls, the stream it is given, and
/// that it touches that stream only asynchronously.
/// </summary>
public sealed class TabularWriterTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Csv(WriteTarget target) => Encoding.UTF8.GetString(target.ToArray());

    private static TabularWriter Writer(WriteTarget target, CsvWriterOptions? csv = null, bool leaveOpen = false) =>
        TabularWriter.Create(target, TabularFormat.Csv, new TabularWriterOptions { Csv = csv ?? CsvWriterOptions.Default, LeaveOpen = leaveOpen });

    [Fact]
    public async Task WritesAHeaderAndRowsAsUtf8WithAByteOrderMark()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = Writer(target))
        {
            writer.BeginSheet("data", [new("Id"), new("Name"), new("Active")]);
            writer.BeginRow();
            writer.Write("1");
            writer.Write("Grüße");
            writer.Write(true);
            writer.EndRow();
            await writer.CompleteAsync(Token);
        }

        Assert.Equal("﻿Id,Name,Active\r\n1,Grüße,true\r\n", Csv(target));
        Assert.True(target.IsDisposed);
        Assert.False(target.DisposedSynchronously);
    }

    [Fact]
    public async Task LeavesTheByteOrderMarkOutWhenAsked()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = Writer(target, new CsvWriterOptions { ByteOrderMark = false }))
        {
            writer.BeginSheet("data", [new("a")]);
            await writer.CompleteAsync(Token);
        }

        Assert.Equal("a\r\n", Csv(target));
    }

    [Fact]
    public async Task QuotesWhatWouldOtherwiseBreakTheRecord()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = Writer(target, new CsvWriterOptions { ByteOrderMark = false }))
        {
            writer.BeginSheet("data", [new("a"), new("b"), new("c"), new("d")]);
            writer.BeginRow();
            writer.Write("x,y");
            writer.Write("say \"hi\"");
            writer.Write("two\nlines");
            writer.Write("plain");
            writer.EndRow();
            await writer.CompleteAsync(Token);
        }

        Assert.Equal("a,b,c,d\r\n\"x,y\",\"say \"\"hi\"\"\",\"two\nlines\",plain\r\n", Csv(target));
    }

    [Fact]
    public async Task PadsAShortRowAndWritesNothingForEmptyCells()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = Writer(target, new CsvWriterOptions { ByteOrderMark = false }))
        {
            writer.BeginSheet("data", [new("a"), new("b"), new("c")]);
            writer.BeginRow();
            writer.Write((string?)null);
            writer.WriteEmpty();
            writer.EndRow();
            writer.BeginRow();
            writer.Write("x");
            writer.EndRow();
            await writer.CompleteAsync(Token);
        }

        Assert.Equal("a,b,c\r\n,,\r\nx,,\r\n", Csv(target));
    }

    [Fact]
    public async Task TouchesTheTargetOnlyAsynchronously()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = Writer(target))
        {
            writer.BeginSheet("data", [new("a")]);

            for (int i = 0; i < 50_000; i++)
            {
                writer.BeginRow();
                writer.Write("a value long enough to pass the flush threshold well before the end");
                writer.EndRow();

                if (writer.FlushRecommended)
                {
                    await writer.FlushAsync(Token);
                }
            }

            await writer.CompleteAsync(Token);
        }

        Assert.True(target.AsyncFlushes >= 1);
        Assert.False(target.DisposedSynchronously);
    }

    [Fact]
    public async Task RecommendsAFlushPastAMegabyteAndNotAfterIt()
    {
        WriteTarget target = new();
        await using TabularWriter writer = Writer(target);
        writer.BeginSheet("data", [new("a")]);
        string value = new('x', 1000);

        while (!writer.FlushRecommended)
        {
            writer.BeginRow();
            writer.Write(value);
            writer.EndRow();
        }

        Assert.Equal(0, target.Length);

        await writer.FlushAsync(Token);

        Assert.False(writer.FlushRecommended);
        Assert.True(target.Length >= 1024 * 1024);
    }

    [Fact]
    public async Task DisposingWithoutCompletingWritesNothingMore()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = Writer(target))
        {
            writer.BeginSheet("data", [new("a")]);
            writer.BeginRow();
            writer.Write("pending");
            writer.EndRow();
        }

        Assert.Equal(0, target.ToArray().Length);
        Assert.True(target.IsDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosesTheTargetUnlessToldToLeaveItOpen(bool leaveOpen)
    {
        WriteTarget target = new();

        await using (TabularWriter writer = Writer(target, leaveOpen: leaveOpen))
        {
            writer.BeginSheet("data", [new("a")]);
            await writer.CompleteAsync(Token);
        }

        Assert.Equal(!leaveOpen, target.IsDisposed);
    }

    [Fact]
    public async Task DisposingTwiceIsHarmless()
    {
        WriteTarget target = new();
        TabularWriter writer = Writer(target, leaveOpen: true);
        writer.BeginSheet("data", [new("a")]);
        await writer.CompleteAsync(Token);

        await writer.DisposeAsync();
        await writer.DisposeAsync();

        Assert.False(target.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => writer.BeginRow());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACreateThatFailsFollowsLeaveOpen(bool leaveOpen)
    {
        MemoryStream target = new();

        Assert.ThrowsAny<ArgumentException>(() => TabularWriter.Create(
            target,
            TabularFormat.Csv,
            new TabularWriterOptions { Csv = new CsvWriterOptions { Delimiter = ':' }, LeaveOpen = leaveOpen }));

        Assert.Equal(leaveOpen, target.CanWrite);
    }

    [Theory]
    [InlineData(TabularFormat.Xlsx)]
    [InlineData(TabularFormat.Ods)]
    [InlineData(TabularFormat.Zip)]
    public void RefusesAFormatItCannotWriteYet(TabularFormat format)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TabularWriter.Create(new MemoryStream(), format));
    }

    [Fact]
    public void RefusesAStreamThatCannotBeWritten()
    {
        Assert.Throws<ArgumentException>(() => TabularWriter.Create(new MemoryStream([], writable: false), TabularFormat.Csv));
    }

    [Fact]
    public async Task AFailingTargetFaultsTheWriterAndDisposalStillClosesIt()
    {
        WriteTarget target = new() { FailWith = new IOException("client went away") };
        TabularWriter writer = Writer(target);
        writer.BeginSheet("data", [new("a")]);

        await Assert.ThrowsAsync<IOException>(async () => await writer.FlushAsync(Token));
        Assert.Throws<InvalidOperationException>(() => writer.BeginRow());

        await writer.DisposeAsync();
        Assert.True(target.IsDisposed);
    }

    [Fact]
    public async Task ACancelledFlushFaultsTheWriter()
    {
        WriteTarget target = new();
        TabularWriter writer = Writer(target);
        writer.BeginSheet("data", [new("a")]);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await writer.FlushAsync(cancelled.Token));
        Assert.Throws<InvalidOperationException>(() => writer.BeginRow());
        Assert.Equal(0, target.ToArray().Length);

        await writer.DisposeAsync();
    }

    public static TheoryData<string, Action<TabularWriter>> Misuses => new()
    {
        { "a value outside a row", w => { w.BeginSheet("data", [new("a")]); w.Write("x"); } },
        { "a row before a sheet", w => w.BeginRow() },
        { "ending a row never begun", w => { w.BeginSheet("data", [new("a")]); w.EndRow(); } },
        { "more values than columns", w => { w.BeginSheet("data", [new("a")]); w.BeginRow(); w.Write("x"); w.Write("y"); } },
        { "a second csv sheet", w => { w.BeginSheet("data", [new("a")]); w.BeginSheet("more", [new("a")]); } },
        { "a sheet inside a row", w => { w.BeginSheet("data", [new("a")]); w.BeginRow(); w.BeginSheet("more", [new("a")]); } },
    };

    [Theory]
    [MemberData(nameof(Misuses))]
    public async Task RefusesCallsOutOfOrderAndStaysRefused(string misuse, Action<TabularWriter> calls)
    {
        Assert.NotEmpty(misuse);
        await using TabularWriter writer = Writer(new WriteTarget());

        Assert.Throws<InvalidOperationException>(() => calls(writer));
        Assert.Throws<InvalidOperationException>(() => writer.BeginSheet("again", [new("a")]));
    }

    public static TheoryData<string, WriteColumn[]> BadColumns => new()
    {
        { "no columns", [] },
        { "an empty header", [new("")] },
        { "a whitespace header", [new("  ")] },
        { "a missing header", [default] },
        { "a header repeated", [new("Name"), new(" name ")] },
        { "a zero width", [new("a", 0)] },
        { "a width past Excel's", [new("a", 256)] },
        { "too many columns", [.. Enumerable.Range(0, 16_385).Select(i => new WriteColumn($"c{i}"))] },
    };

    [Theory]
    [MemberData(nameof(BadColumns))]
    public async Task RefusesColumnsThatCouldNotBeReadBack(string problem, WriteColumn[] columns)
    {
        Assert.NotEmpty(problem);
        await using TabularWriter writer = Writer(new WriteTarget());

        Assert.ThrowsAny<ArgumentException>(() => writer.BeginSheet("data", columns));
    }

    [Fact]
    public async Task CompletingInsideARowOrWithoutASheetIsRefused()
    {
        await using TabularWriter empty = Writer(new WriteTarget());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await empty.CompleteAsync(Token));

        await using TabularWriter midRow = Writer(new WriteTarget());
        midRow.BeginSheet("data", [new("a")]);
        midRow.BeginRow();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await midRow.CompleteAsync(Token));
    }

    [Fact]
    public async Task NothingCanBeWrittenAfterCompleting()
    {
        await using TabularWriter writer = Writer(new WriteTarget());
        writer.BeginSheet("data", [new("a")]);
        await writer.CompleteAsync(Token);

        Assert.Throws<InvalidOperationException>(() => writer.BeginRow());
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~TabularWriterTests"`
Expected: build FAIL — `TabularWriter` does not exist.

- [ ] **Step 4: Implement `ISheetWriter`**

Create `src/TriasDev.Tabular/Writing/ISheetWriter.cs`:

```csharp
namespace TriasDev.Tabular;

/// <summary>
/// One format's encoding of a sheet: the only part of writing that knows a format, as
/// <see cref="ITabularCursor"/> is for reading.
/// </summary>
/// <remarks>
/// <see cref="TabularWriter"/> keeps the order of calls, the row and column count and the checks
/// every format shares; an implementation only encodes. A method that can meet a value its format
/// cannot hold returns the <see cref="ErrorCodes.Write"/> code instead of throwing, so that the
/// writer — which knows the sheet, row and column — raises the exception.
/// </remarks>
internal interface ISheetWriter
{
    /// <summary>The most rows a sheet holds, the header included.</summary>
    long MaxRows { get; }

    /// <summary>Whether a file holds more than one sheet.</summary>
    bool AllowsSeveralSheets { get; }

    void BeginSheet(string name, ReadOnlySpan<WriteColumn> columns);

    void BeginRow();

    /// <summary>Writes text already checked by <see cref="TextRules"/>; returns a code if the format cannot hold it.</summary>
    string? WriteText(string value);

    void WriteBoolean(bool value);

    void WriteEmpty();

    void EndRow();

    /// <summary>Writes whatever ends the file. Called once, after the last row.</summary>
    void Complete();
}
```

Note: `TextRules` arrives in Task 6. Until then, write the cref as plain text `TextRules` (`<c>TextRules</c>`) so the build's XML-doc check does not fail, and change it back to `<see cref="TextRules"/>` in Task 6.

- [ ] **Step 5: Implement `CsvSheetWriter` (text, booleans, empty)**

Create `src/TriasDev.Tabular/Csv/CsvSheetWriter.cs`:

```csharp
using System.Buffers;
using System.Text;

namespace TriasDev.Tabular.Csv;

/// <summary>
/// Writes csv: a row is formatted into a reused character buffer and encoded as UTF-8 into the
/// spill buffer when it ends.
/// </summary>
/// <remarks>
/// RFC 4180: a field holding the delimiter, a quote or a line break is quoted, its quotes doubled;
/// every record ends in CR LF. Nothing is allocated per row — the buffer is reused, and numbers and
/// dates are formatted straight into it.
/// </remarks>
internal sealed class CsvSheetWriter : ISheetWriter
{
    private const int InitialRowChars = 4 * 1024;

    /// <summary>A row buffer past this is let go after its row, rather than kept for the rest of the file.</summary>
    private const int RetainedRowChars = 1024 * 1024;

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private readonly SpillBuffer _out;
    private readonly CsvFormat _format;
    private readonly SearchValues<char> _needsQuotes;
    private char[] _row = new char[InitialRowChars];
    private int _length;
    private bool _firstCell = true;

    public CsvSheetWriter(SpillBuffer output, CsvFormat format)
    {
        _out = output;
        _format = format;
        _needsQuotes = SearchValues.Create([format.Delimiter, '"', '\r', '\n']);
    }

    public long MaxRows => long.MaxValue;

    public bool AllowsSeveralSheets => false;

    /// <summary>The row buffer's current size, for the test that a huge row does not keep it.</summary>
    internal int RowBufferLength => _row.Length;

    public void BeginSheet(string name, ReadOnlySpan<WriteColumn> columns)
    {
        if (_format.ByteOrderMark)
        {
            _out.Write(Utf8Bom);
        }
    }

    public void BeginRow()
    {
        _length = 0;
        _firstCell = true;
    }

    public string? WriteText(string value)
    {
        Separate();
        ReadOnlySpan<char> text = value;

        if (text.IndexOfAny(_needsQuotes) < 0)
        {
            Append(text);
            return null;
        }

        Append('"');

        while (true)
        {
            int quote = text.IndexOf('"');

            if (quote < 0)
            {
                Append(text);
                break;
            }

            Append(text[..(quote + 1)]);
            Append('"');
            text = text[(quote + 1)..];
        }

        Append('"');
        return null;
    }

    public void WriteBoolean(bool value)
    {
        Separate();
        Append(value ? "true" : "false");
    }

    public void WriteEmpty() => Separate();

    public void EndRow()
    {
        Append("\r\n");
        Span<byte> target = _out.GetSpan(Encoding.UTF8.GetMaxByteCount(_length));
        _out.Advance(Encoding.UTF8.GetBytes(_row.AsSpan(0, _length), target));

        if (_row.Length > RetainedRowChars)
        {
            _row = new char[InitialRowChars];
        }
    }

    public void Complete()
    {
        // A csv file ends with its last record.
    }

    private void Separate()
    {
        if (!_firstCell)
        {
            Append(_format.Delimiter);
        }

        _firstCell = false;
    }

    private void Append(char value)
    {
        Reserve(1);
        _row[_length++] = value;
    }

    private void Append(ReadOnlySpan<char> value)
    {
        Reserve(value.Length);
        value.CopyTo(_row.AsSpan(_length));
        _length += value.Length;
    }

    private void Reserve(int extra)
    {
        if (_length + extra > _row.Length)
        {
            Array.Resize(ref _row, Math.Max(_row.Length * 2, _length + extra));
        }
    }
}
```

- [ ] **Step 6: Implement `TabularWriter`**

Create `src/TriasDev.Tabular/Writing/TabularWriter.cs`:

```csharp
using TriasDev.Tabular.Csv;

namespace TriasDev.Tabular;

/// <summary>
/// Writes a table into a stream, row by row: typed values in, a csv, xlsx or ods file out.
/// </summary>
/// <remarks>
/// <para>
/// Writes go synchronously into memory; <see cref="FlushAsync"/> moves that memory into the stream.
/// The stream itself is only ever written, flushed and closed asynchronously, so the writer serves an
/// ASP.NET Core response body, which refuses synchronous writes. Flush whenever
/// <see cref="FlushRecommended"/> is true, and memory stays flat however many rows the file has.
/// </para>
/// <para>
/// The file is valid only after <see cref="CompleteAsync"/>. A writer disposed without it, or after
/// an exception, leaves an incomplete file behind — for a workbook, plainly broken; for csv, a file
/// that looks whole. The caller discards it: aborts the response, deletes the file.
/// </para>
/// <para>Not thread-safe: one writer, one sequence of calls.</para>
/// </remarks>
public sealed class TabularWriter : IAsyncDisposable
{
    /// <summary>Past this many pending bytes, <see cref="FlushRecommended"/> turns true.</summary>
    internal const int FlushThreshold = 1024 * 1024;

    /// <summary>The most columns a sheet has: the workbook formats' own limit, and the csv reader's.</summary>
    internal const int MaxColumns = 16_384;

    private const double MaxWidth = 255;

    private const string FaultedMessage = "The writer failed earlier; the file is incomplete and nothing more can be written.";

    private readonly Stream _target;
    private readonly SpillBuffer _buffer;
    private readonly ISheetWriter _sheet;
    private readonly bool _leaveOpen;
    private State _state = State.Open;
    private WriteColumn[] _columns = [];
    private string _sheetName = string.Empty;
    private int _sheets;
    private long _rowNumber;
    private int _column;

    private TabularWriter(Stream target, TabularFormat format, SpillBuffer buffer, ISheetWriter sheet, bool leaveOpen)
    {
        _target = target;
        Format = format;
        _buffer = buffer;
        _sheet = sheet;
        _leaveOpen = leaveOpen;
    }

    private enum State
    {
        Open,
        InSheet,
        InRow,
        Completed,
        Faulted,
        Disposed,
    }

    /// <summary>The format being written.</summary>
    public TabularFormat Format { get; }

    /// <summary>
    /// Whether enough is pending in memory that the caller should <see cref="FlushAsync"/> now.
    /// </summary>
    public bool FlushRecommended => _buffer.Pending >= FlushThreshold;

    /// <summary>Creates a writer for a format, into a stream.</summary>
    /// <param name="stream">Where the file goes: a file, a blob, a response body. It need not seek.</param>
    /// <param name="format">The format to write. Csv in this version.</param>
    /// <param name="options">The format's knobs; checked here, before anything is written.</param>
    /// <exception cref="ArgumentException">The stream cannot be written, or an option cannot work.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A format this version does not write.</exception>
    /// <remarks>
    /// On failure the stream is closed — with <c>Dispose()</c>, since nothing was written to it —
    /// unless <see cref="TabularWriterOptions.LeaveOpen"/> is set.
    /// </remarks>
    public static TabularWriter Create(Stream stream, TabularFormat format, TabularWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        TabularWriterOptions effective = options ?? TabularWriterOptions.Default;

        try
        {
            if (!stream.CanWrite)
            {
                throw new ArgumentException("The stream cannot be written to.", nameof(stream));
            }

            switch (format)
            {
                case TabularFormat.Csv:
                    CsvFormat csv = effective.Csv.Resolve();
                    SpillBuffer buffer = new();
                    return new TabularWriter(stream, format, buffer, new CsvSheetWriter(buffer, csv), effective.LeaveOpen);
                default:
                    throw new ArgumentOutOfRangeException(nameof(format), format, $"Writing {format} is not supported.");
            }
        }
        catch when (!effective.LeaveOpen)
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Begins a sheet and writes its header row.</summary>
    /// <param name="name">The sheet's name. A csv file has one sheet, whose name is not written.</param>
    /// <param name="columns">The columns: 1 to 16,384, each with a header that is not empty and is unique in the sheet, ignoring case and surrounding whitespace.</param>
    public void BeginSheet(string name, ReadOnlySpan<WriteColumn> columns)
    {
        ExpectWritable();

        if (_state != State.Open && _state != State.InSheet)
        {
            throw Refuse("A sheet begins outside a row.");
        }

        if (_sheets > 0 && !_sheet.AllowsSeveralSheets)
        {
            throw Refuse($"A {Format} file holds one sheet.");
        }

        if (name is null)
        {
            throw Faulting(new ArgumentNullException(nameof(name)));
        }

        CheckColumns(columns);

        _columns = columns.ToArray();
        _sheetName = name;
        _sheets++;
        _rowNumber = 0;
        _sheet.BeginSheet(name, _columns);

        StartRow();

        foreach (WriteColumn column in _columns)
        {
            if (_sheet.WriteText(column.Header) is { } code)
            {
                throw Faulting(new ArgumentException($"The header \"{column.Header}\" cannot be written: {code}.", nameof(columns)));
            }

            _column++;
        }

        FinishRow();
    }

    /// <summary>Begins a row; its values follow, one per column, then <see cref="EndRow"/>.</summary>
    /// <exception cref="TabularLimitException">The sheet already holds as many rows as its format allows.</exception>
    public void BeginRow()
    {
        ExpectWritable();

        if (_state != State.InSheet)
        {
            throw Refuse("A row begins inside a sheet, after the previous row ended.");
        }

        StartRow();
    }

    /// <summary>Writes the next cell as text; null writes an empty cell.</summary>
    /// <remarks>The import trims text and reads empty or whitespace-only text as no value.</remarks>
    public void Write(string? value)
    {
        int column = NextCell();

        if (value is null)
        {
            _sheet.WriteEmpty();
            return;
        }

        Check(_sheet.WriteText(value), column);
    }

    /// <summary>Writes the next cell as a boolean.</summary>
    public void Write(bool value)
    {
        NextCell();
        _sheet.WriteBoolean(value);
    }

    /// <summary>Writes the next cell empty.</summary>
    public void WriteEmpty()
    {
        NextCell();
        _sheet.WriteEmpty();
    }

    /// <summary>Ends the row; columns it did not reach are written empty.</summary>
    public void EndRow()
    {
        ExpectWritable();

        if (_state != State.InRow)
        {
            throw Refuse("A row ends after it began.");
        }

        FinishRow();
    }

    /// <summary>Moves everything pending in memory into the stream.</summary>
    /// <remarks>Cancelling, or a stream that fails, leaves the writer faulted and the file incomplete.</remarks>
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        ExpectWritable();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _buffer.DrainToAsync(_target, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            MarkFaulted();
            throw;
        }
    }

    /// <summary>Ends the file, writes what is pending and flushes the stream. Only now is the file valid.</summary>
    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        ExpectWritable();

        if (_state == State.InRow)
        {
            throw Refuse("The last row has not ended.");
        }

        if (_sheets == 0)
        {
            throw Refuse("A file has at least one sheet.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _sheet.Complete();
            await _buffer.DrainToAsync(_target, cancellationToken).ConfigureAwait(false);
            await _target.FlushAsync(cancellationToken).ConfigureAwait(false);
            _state = State.Completed;
        }
        catch
        {
            MarkFaulted();
            throw;
        }
    }

    /// <summary>
    /// Releases the writer and closes the stream, unless told to leave it open. Without
    /// <see cref="CompleteAsync"/> first, what is pending is dropped and the file stays incomplete.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_state == State.Disposed)
        {
            return;
        }

        _state = State.Disposed;
        _buffer.Dispose();

        if (!_leaveOpen)
        {
            await _target.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void CheckColumns(ReadOnlySpan<WriteColumn> columns)
    {
        if (columns.IsEmpty || columns.Length > MaxColumns)
        {
            throw Faulting(new ArgumentOutOfRangeException(nameof(columns), columns.Length, $"A sheet has 1 to {MaxColumns} columns."));
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (WriteColumn column in columns)
        {
            if (string.IsNullOrWhiteSpace(column.Header))
            {
                throw Faulting(new ArgumentException("Every column has a header that is not empty.", nameof(columns)));
            }

            if (!seen.Add(column.Header.Trim()))
            {
                throw Faulting(new ArgumentException($"The header \"{column.Header}\" appears twice; the import could not tell the columns apart.", nameof(columns)));
            }

            if (column.Width is { } width && (!double.IsFinite(width) || width <= 0 || width > MaxWidth))
            {
                throw Faulting(new ArgumentOutOfRangeException(nameof(columns), width, $"A column is more than 0 and at most {MaxWidth} characters wide."));
            }
        }
    }

    private void StartRow()
    {
        if (_rowNumber >= _sheet.MaxRows)
        {
            throw Faulting(new TabularLimitException("MaxRows", _sheet.MaxRows, $"A {Format} sheet holds at most {_sheet.MaxRows} rows, the header included."));
        }

        _rowNumber++;
        _column = 0;
        _sheet.BeginRow();
        _state = State.InRow;
    }

    private void FinishRow()
    {
        for (; _column < _columns.Length; _column++)
        {
            _sheet.WriteEmpty();
        }

        _sheet.EndRow();
        _state = State.InSheet;
    }

    private int NextCell()
    {
        ExpectWritable();

        if (_state != State.InRow)
        {
            throw Refuse("A value is written between BeginRow and EndRow.");
        }

        if (_column == _columns.Length)
        {
            throw Refuse($"The row already has a value for each of the sheet's {_columns.Length} columns.");
        }

        return _column++;
    }

    private void Check(string? code, int column)
    {
        if (code is null)
        {
            return;
        }

        string header = _columns[column].Header;

        throw Faulting(new TabularWriteException(
            code,
            _sheetName,
            _rowNumber,
            column,
            header,
            $"Row {_rowNumber}, column {column + 1} (\"{header}\") of sheet \"{_sheetName}\" cannot be written: {code}."));
    }

    private void ExpectWritable()
    {
        ObjectDisposedException.ThrowIf(_state == State.Disposed, this);

        if (_state == State.Faulted)
        {
            throw new InvalidOperationException(FaultedMessage);
        }

        if (_state == State.Completed)
        {
            throw Refuse("The file is complete; nothing more can be written.");
        }
    }

    private InvalidOperationException Refuse(string message) => Faulting(new InvalidOperationException(message));

    private T Faulting<T>(T exception)
        where T : Exception
    {
        MarkFaulted();
        return exception;
    }

    private void MarkFaulted()
    {
        if (_state != State.Disposed)
        {
            _state = State.Faulted;
        }
    }
}
```

- [ ] **Step 7: Record the public API**

Append the RS0016 lines for `TabularWriter` to `PublicAPI.Unshipped.txt` (same method as Task 3, Step 5). Expected members: the class, `Create`, `Format.get`, `FlushRecommended.get`, `BeginSheet`, `BeginRow`, `Write(string?)`, `Write(bool)`, `WriteEmpty`, `EndRow`, `FlushAsync`, `CompleteAsync`, `DisposeAsync`.

- [ ] **Step 8: Run tests to verify they pass**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~TabularWriterTests|FullyQualifiedName~SpillBufferTests|FullyQualifiedName~CsvWriterOptionsTests"`
Expected: PASS on both targets.

- [ ] **Step 9: Commit**

```bash
git add src/TriasDev.Tabular tests/TriasDev.Tabular.Tests
git commit -m "feat(write): TabularWriter, writing csv text, booleans and empty cells with an async-only target"
```

---

### Task 5: Numbers and dates

**Files:**
- Create: `src/TriasDev.Tabular/Writing/ValueChecks.cs`
- Modify: `src/TriasDev.Tabular/Writing/ISheetWriter.cs` (four members)
- Modify: `src/TriasDev.Tabular/Writing/TabularWriter.cs` (five `Write` overloads)
- Modify: `src/TriasDev.Tabular/Csv/CsvSheetWriter.cs` (four members, one helper)
- Modify: `src/TriasDev.Tabular/PublicAPI.Unshipped.txt`
- Test: `tests/TriasDev.Tabular.Tests/Writing/CsvValueTests.cs`

**Interfaces:**
- Consumes: `TabularWriter`, `ISheetWriter`, `CsvSheetWriter`, `CsvFormat` (Tasks 3–4).
- Produces: `TabularWriter.Write(long)`, `Write(decimal)`, `Write(double)`, `Write(DateTime)`, `Write(DateOnly)`; `ISheetWriter.WriteLong(long) → string?`, `WriteDecimal(decimal) → string?`, `WriteDouble(double) → string?`, `WriteDate(DateTime value, bool hasTime) → string?`; internal `static class ValueChecks` with `string? Double(double value)` and `DateTime Truncated(DateTime value)` — part 2's xlsx writer relies on both.

- [ ] **Step 1: Write the failing tests**

Create `tests/TriasDev.Tabular.Tests/Writing/CsvValueTests.cs`:

```csharp
using System.Text;

using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>How a csv writer writes each kind of value, and which values it refuses.</summary>
public sealed class CsvValueTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<string> One(Action<TabularWriter> write, string? culture = null)
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(
            target,
            TabularFormat.Csv,
            new TabularWriterOptions { Csv = new CsvWriterOptions { Culture = culture, ByteOrderMark = false } }))
        {
            writer.BeginSheet("data", [new("v")]);
            writer.BeginRow();
            write(writer);
            writer.EndRow();
            await writer.CompleteAsync(Token);
        }

        return Encoding.UTF8.GetString(target.ToArray())["v\r\n".Length..^2];
    }

    public static TheoryData<string, Action<TabularWriter>, string?, string> Written => new()
    {
        { "long", w => w.Write(-9_007_199_254_740_993L), null, "-9007199254740993" },
        { "long, de-DE", w => w.Write(-42L), "de-DE", "-42" },
        { "int as long", w => w.Write(7), null, "7" },
        { "decimal", w => w.Write(1234.50m), null, "1234.50" },
        { "decimal, de-DE", w => w.Write(-1234.5m), "de-DE", "-1234,5" },
        { "decimal past double", w => w.Write(12345678901234567.891m), null, "12345678901234567.891" },
        { "double", w => w.Write(0.1), null, "0.1" },
        { "double, de-DE", w => w.Write(-2.5), "de-DE", "-2,5" },
        { "date", w => w.Write(new DateTime(2026, 10, 3)), null, "2026-10-03" },
        { "date-time", w => w.Write(new DateTime(2026, 10, 3, 14, 5, 6, 789)), null, "2026-10-03T14:05:06.789" },
        { "date-time, de-DE", w => w.Write(new DateTime(2026, 10, 3, 14, 5, 6)), "de-DE", "03.10.2026 14:05:06.000" },
        { "date-time, en-US", w => w.Write(new DateTime(2026, 10, 3, 14, 5, 6)), "en-US", "10/3/2026 14:05:06.000" },
        { "sub-millisecond truncated", w => w.Write(new DateTime(2026, 10, 3, 23, 59, 59, 999).AddTicks(9_999)), null, "2026-10-03T23:59:59.999" },
        { "max value", w => w.Write(DateTime.MaxValue), null, "9999-12-31T23:59:59.999" },
        { "a time only in sub-milliseconds is a date", w => w.Write(new DateTime(2026, 10, 3).AddTicks(5)), null, "2026-10-03" },
        { "date only", w => w.Write(new DateOnly(2026, 10, 3)), null, "2026-10-03" },
        { "year 2", w => w.Write(new DateTime(2, 1, 1)), null, "0002-01-01" },
    };

    [Theory]
    [MemberData(nameof(Written))]
    public async Task WritesEachKindOfValue(string kind, Action<TabularWriter> write, string? culture, string expected)
    {
        Assert.NotEmpty(kind);
        Assert.Equal(expected, await One(write, culture));
    }

    public static TheoryData<string, Action<TabularWriter>, string> Refused => new()
    {
        { "NaN", w => w.Write(double.NaN), ErrorCodes.Write.NotFinite },
        { "infinity", w => w.Write(double.PositiveInfinity), ErrorCodes.Write.NotFinite },
        { "negative infinity", w => w.Write(double.NegativeInfinity), ErrorCodes.Write.NotFinite },
        { "more than 15 digits", w => w.Write(0.1 + 0.2), ErrorCodes.Write.PrecisionLoss },
        { "too small for decimal", w => w.Write(1e-30), ErrorCodes.Write.PrecisionLoss },
        { "too large for decimal", w => w.Write(1e30), ErrorCodes.Write.PrecisionLoss },
        { "year 1", w => w.Write(DateTime.MinValue), ErrorCodes.Write.DateOutOfRange },
        { "year 1, late", w => w.Write(new DateTime(1, 12, 31)), ErrorCodes.Write.DateOutOfRange },
        { "date only, year 1", w => w.Write(DateOnly.MinValue), ErrorCodes.Write.DateOutOfRange },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task RefusesAValueTheImportWouldNotReadBackAsWritten(string kind, Action<TabularWriter> write, string code)
    {
        Assert.NotEmpty(kind);
        WriteTarget target = new();
        await using TabularWriter writer = TabularWriter.Create(target, TabularFormat.Csv);
        writer.BeginSheet("data", [new("Id"), new("Amount")]);
        writer.BeginRow();
        writer.Write(1L);

        TabularWriteException refused = Assert.Throws<TabularWriteException>(() => write(writer));

        Assert.Equal(code, refused.Code);
        Assert.Equal("data", refused.SheetName);
        Assert.Equal(2, refused.RowNumber);
        Assert.Equal(1, refused.ColumnIndex);
        Assert.Equal("Amount", refused.Header);
        Assert.Throws<InvalidOperationException>(() => writer.WriteEmpty());
    }

    [Fact]
    public async Task HoldsLongsAndDecimalsExactlyWhereXlsxWouldNot()
    {
        // csv writes digits, so neither check that protects a double-based format applies.
        Assert.Equal("9007199254740993", await One(w => w.Write(9_007_199_254_740_993L)));
        Assert.Equal("0.1234567890123456789", await One(w => w.Write(0.1234567890123456789m)));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~CsvValueTests"`
Expected: build FAIL — no `Write(long)` and the other overloads.

- [ ] **Step 3: Implement `ValueChecks`**

Create `src/TriasDev.Tabular/Writing/ValueChecks.cs`:

```csharp
namespace TriasDev.Tabular;

/// <summary>The checks every format makes on a value before writing it.</summary>
internal static class ValueChecks
{
    /// <summary>
    /// Whether a double comes back as itself: finite, and within the 15 significant digits that a
    /// workbook's number cell is read back through.
    /// </summary>
    /// <remarks>
    /// The import reads a workbook number into a decimal by a plain cast, which keeps 15 significant
    /// digits. A double that survives that cast and the cast back reads back exactly; any other would
    /// come back changed — and differently by format, since csv reads its digits as written. One rule
    /// for every format keeps the round trip the same whichever is chosen.
    /// </remarks>
    public static string? Double(double value)
    {
        if (!double.IsFinite(value))
        {
            return ErrorCodes.Write.NotFinite;
        }

        decimal asDecimal;

        try
        {
            asDecimal = (decimal)value;
        }
        catch (OverflowException)
        {
            return ErrorCodes.Write.PrecisionLoss;
        }

        return (double)asDecimal == value ? null : ErrorCodes.Write.PrecisionLoss;
    }

    /// <summary>The date with anything finer than a millisecond dropped, and no kind.</summary>
    /// <remarks>
    /// Truncated, not rounded: rounding overflows on <see cref="DateTime.MaxValue"/> and moves
    /// 23:59:59.9996 into the next day. A workbook serial holds no more than milliseconds, and the
    /// rule is the same for every format so the round trip is too.
    /// </remarks>
    public static DateTime Truncated(DateTime value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Unspecified);
}
```

- [ ] **Step 4: Extend `ISheetWriter`**

Add to `src/TriasDev.Tabular/Writing/ISheetWriter.cs`, after `WriteText`:

```csharp
    /// <summary>Writes an integer; returns a code if the format cannot hold it exactly.</summary>
    string? WriteLong(long value);

    /// <summary>Writes a decimal; returns a code if the format cannot hold it exactly.</summary>
    string? WriteDecimal(decimal value);

    /// <summary>Writes a double already checked by <see cref="ValueChecks.Double"/>.</summary>
    string? WriteDouble(double value);

    /// <summary>Writes a date already truncated to the millisecond; <paramref name="hasTime"/> says whether it has a time of day.</summary>
    string? WriteDate(DateTime value, bool hasTime);
```

- [ ] **Step 5: Implement them for csv**

Add to `src/TriasDev.Tabular/Csv/CsvSheetWriter.cs`, after `WriteText`:

```csharp
    public string? WriteLong(long value)
    {
        Separate();
        AppendFormatted(value, default);
        return null;
    }

    public string? WriteDecimal(decimal value)
    {
        Separate();
        AppendFormatted(value, default);
        return null;
    }

    public string? WriteDouble(double value)
    {
        Separate();
        AppendFormatted(value, "R");
        return null;
    }

    public string? WriteDate(DateTime value, bool hasTime)
    {
        // The import's date reader refuses year 1 — it cannot tell it from a date with no year.
        if (value.Year == 1)
        {
            return ErrorCodes.Write.DateOutOfRange;
        }

        Separate();
        AppendFormatted(value, hasTime ? _format.DateTimeFormat : _format.DateFormat);
        return null;
    }
```

and after `Append(ReadOnlySpan<char>)`:

```csharp
    /// <summary>Formats a value straight into the row buffer, growing it until the value fits.</summary>
    private void AppendFormatted<T>(T value, ReadOnlySpan<char> format)
        where T : ISpanFormattable
    {
        Reserve(64);
        int written;

        while (!value.TryFormat(_row.AsSpan(_length), out written, format, _format.Culture))
        {
            Array.Resize(ref _row, _row.Length * 2);
        }

        _length += written;
    }
```

- [ ] **Step 6: Add the overloads to `TabularWriter`**

Add to `src/TriasDev.Tabular/Writing/TabularWriter.cs`, after `Write(string?)`:

```csharp
    /// <summary>Writes the next cell as an integer. Narrower integers arrive here by implicit conversion.</summary>
    public void Write(long value)
    {
        int column = NextCell();
        Check(_sheet.WriteLong(value), column);
    }

    /// <summary>Writes the next cell as a decimal number.</summary>
    public void Write(decimal value)
    {
        int column = NextCell();
        Check(_sheet.WriteDecimal(value), column);
    }

    /// <summary>
    /// Writes the next cell as a number. Refused when not finite, or when it has more than 15
    /// significant digits — which a workbook would not give back.
    /// </summary>
    public void Write(double value)
    {
        int column = NextCell();
        Check(ValueChecks.Double(value) ?? _sheet.WriteDouble(value), column);
    }

    /// <summary>
    /// Writes the next cell as a date, with its time of day when it has one. Anything finer than a
    /// millisecond is dropped, and the kind is not kept: the import returns the wall-clock value.
    /// </summary>
    public void Write(DateTime value)
    {
        int column = NextCell();
        DateTime truncated = ValueChecks.Truncated(value);
        Check(_sheet.WriteDate(truncated, truncated.TimeOfDay != TimeSpan.Zero), column);
    }

    /// <summary>Writes the next cell as a date. The import returns it as a <see cref="DateTime"/> at midnight.</summary>
    public void Write(DateOnly value)
    {
        int column = NextCell();
        Check(_sheet.WriteDate(value.ToDateTime(TimeOnly.MinValue), hasTime: false), column);
    }
```

- [ ] **Step 7: Record the public API, run the tests**

Append the five new `Write` lines RS0016 reports to `PublicAPI.Unshipped.txt`.

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~CsvValueTests|FullyQualifiedName~TabularWriterTests"`
Expected: PASS. The `en-US` row expects `10/3/2026 14:05:06.000` because the date part is the culture's `ShortDatePattern` (`M/d/yyyy`) and the time is always `HH:mm:ss.fff`; if the runtime's ICU gives another `ShortDatePattern`, correct the expected text to what `CultureInfo.GetCultureInfo("en-US").DateTimeFormat.ShortDatePattern` produces — never change the writer.

- [ ] **Step 8: Commit**

```bash
git add src/TriasDev.Tabular tests/TriasDev.Tabular.Tests
git commit -m "feat(write): numbers and dates, refusing a double or a date the import would not give back"
```

---

### Task 6: Text the import must read back — forbidden characters, line breaks, length, formula guard

**Files:**
- Create: `src/TriasDev.Tabular/Writing/TextRules.cs`
- Modify: `src/TriasDev.Tabular/Writing/TabularWriter.cs` (`Write(string?)` and the header check in `CheckColumns`)
- Modify: `src/TriasDev.Tabular/Writing/ISheetWriter.cs` (restore the `<see cref="TextRules"/>`)
- Modify: `src/TriasDev.Tabular/Csv/CsvSheetWriter.cs` (`WriteText`)
- Test: `tests/TriasDev.Tabular.Tests/Writing/CsvTextTests.cs`

**Interfaces:**
- Consumes: `CsvCursorOptions.Default.MaxQuotedFieldLines` (100) and `.MaxFieldChars` (16,777,216).
- Produces: internal `static class TextRules` with `string? Check(string text)` — `null` or `ErrorCodes.Write.InvalidCharacter`; part 2 and 3 writers rely on text reaching them already checked.

- [ ] **Step 1: Write the failing tests**

Create `tests/TriasDev.Tabular.Tests/Writing/CsvTextTests.cs`:

```csharp
using System.Text;

using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>Text the csv reader would not give back as written is refused; the formula guard is opt-in.</summary>
public sealed class CsvTextTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<string> One(string value, CsvWriterOptions? options = null)
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(
            target,
            TabularFormat.Csv,
            new TabularWriterOptions { Csv = (options ?? CsvWriterOptions.Default) with { ByteOrderMark = false } }))
        {
            writer.BeginSheet("data", [new("v")]);
            writer.BeginRow();
            writer.Write(value);
            writer.EndRow();
            await writer.CompleteAsync(Token);
        }

        return Encoding.UTF8.GetString(target.ToArray())["v\r\n".Length..^2];
    }

    private static async Task<TabularWriteException> Refused(string value)
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Csv);
        writer.BeginSheet("data", [new("v")]);
        writer.BeginRow();

        return Assert.Throws<TabularWriteException>(() => writer.Write(value));
    }

    [Theory]
    [InlineData("\u0000")]
    [InlineData("a\u0001b")]
    [InlineData("\u0008")]
    [InlineData("\u000B")]
    [InlineData("\u000C")]
    [InlineData("\u001F")]
    [InlineData("￾")]
    [InlineData("￿")]
    [InlineData("lone \uD800 high")]
    [InlineData("lone \uDC00 low")]
    [InlineData("swapped \uDC00\uD800")]
    [InlineData("ends high \uD83D")]
    public async Task RefusesACharacterXmlForbids(string value)
    {
        Assert.Equal(ErrorCodes.Write.InvalidCharacter, (await Refused(value)).Code);
    }

    [Theory]
    [InlineData("tab\there")]
    [InlineData("emoji 👍 pair")]
    [InlineData("_x0001_ literal")]
    public async Task WritesTheCharactersXmlAllows(string value)
    {
        Assert.Equal(value, await One(value));
    }

    [Fact]
    public async Task TakesAsManyLineBreaksAsTheReaderDoesAndNoMore()
    {
        string hundred = string.Join('\n', Enumerable.Repeat("line", 101));
        Assert.Equal('"' + hundred + '"', await One(hundred));

        Assert.Equal(ErrorCodes.Write.TooManyLines, (await Refused(hundred + "\nline")).Code);
    }

    [Theory]
    [InlineData("\r\n", 100, false)]
    [InlineData("\r\n", 101, true)]
    [InlineData("\r", 101, true)]
    [InlineData("\n", 101, true)]
    public async Task CountsLineBreaksAsTheReaderDoes(string lineBreak, int count, bool refused)
    {
        string value = "a" + string.Concat(Enumerable.Repeat(lineBreak + "a", count));

        if (refused)
        {
            Assert.Equal(ErrorCodes.Write.TooManyLines, (await Refused(value)).Code);
        }
        else
        {
            Assert.Equal('"' + value + '"', await One(value));
        }
    }

    [Fact]
    public async Task RefusesTextLongerThanTheReaderReads()
    {
        Assert.Equal(ErrorCodes.Write.TextTooLong, (await Refused(new string('x', CsvCursorOptions.Default.MaxFieldChars + 1))).Code);
    }

    [Theory]
    [InlineData("=1+2", "'=1+2")]
    [InlineData("+1", "'+1")]
    [InlineData("-1", "'-1")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tcmd", "'\tcmd")]
    [InlineData("\rcmd", "\"'\rcmd\"")]
    [InlineData("plain", "plain")]
    [InlineData("a=b", "a=b")]
    public async Task GuardsTextASpreadsheetWouldRunWhenAsked(string value, string written)
    {
        Assert.Equal(written, await One(value, new CsvWriterOptions { FormulaGuard = true }));
    }

    [Fact]
    public async Task GuardsNeitherByDefaultNorANumber()
    {
        Assert.Equal("=1+2", await One("=1+2"));

        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(
            target,
            TabularFormat.Csv,
            new TabularWriterOptions { Csv = new CsvWriterOptions { FormulaGuard = true, ByteOrderMark = false } }))
        {
            writer.BeginSheet("data", [new("v")]);
            writer.BeginRow();
            writer.Write(-5L);
            writer.EndRow();
            await writer.CompleteAsync(Token);
        }

        Assert.Equal("v\r\n-5\r\n", Encoding.UTF8.GetString(target.ToArray()));
    }

    [Fact]
    public async Task RefusesAHeaderWithAForbiddenCharacter()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Csv);

        Assert.ThrowsAny<ArgumentException>(() => writer.BeginSheet("data", [new("bad\u0001")]));
    }

    [Fact]
    public async Task AHugeRowDoesNotKeepItsBufferForTheRestOfTheFile()
    {
        using SpillBuffer buffer = new();
        CsvSheetWriter sheet = new(buffer, CsvWriterOptions.Default.Resolve());
        sheet.BeginSheet("data", [new("v")]);

        sheet.BeginRow();
        Assert.Null(sheet.WriteText(new string('x', 10_000_000)));
        sheet.EndRow();

        Assert.True(sheet.RowBufferLength <= 4 * 1024);
        await buffer.DrainToAsync(Stream.Null, Token);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~CsvTextTests"`
Expected: FAIL — forbidden characters, line breaks and length are written instead of refused; the guard does nothing. (`AHugeRowDoesNotKeepItsBufferForTheRestOfTheFile` and `WritesTheCharactersXmlAllows` already pass.)

- [ ] **Step 3: Implement `TextRules`**

Create `src/TriasDev.Tabular/Writing/TextRules.cs`:

```csharp
using System.Buffers;

namespace TriasDev.Tabular;

/// <summary>The characters no format may carry: the ones XML 1.0 forbids.</summary>
/// <remarks>
/// csv could carry them, but one rule for every format means an export that a workbook refuses is
/// not quietly accepted as csv: the same data fails the same way whichever format is chosen.
/// </remarks>
internal static class TextRules
{
    private static readonly SearchValues<char> Forbidden = SearchValues.Create(
        [.. Enumerable.Range(0, 0x20).Where(c => c is not (0x09 or 0x0A or 0x0D)).Select(c => (char)c), '￾', '￿']);

    /// <summary>Null when every character may be written, otherwise <see cref="ErrorCodes.Write.InvalidCharacter"/>.</summary>
    public static string? Check(string text)
    {
        ReadOnlySpan<char> span = text;

        if (span.IndexOfAny(Forbidden) >= 0)
        {
            return ErrorCodes.Write.InvalidCharacter;
        }

        int surrogate = span.IndexOfAnyInRange('\uD800', '\uDFFF');

        return surrogate < 0 || PairsAreWhole(span[surrogate..]) ? null : ErrorCodes.Write.InvalidCharacter;
    }

    /// <summary>Whether every surrogate is a high one directly followed by a low one.</summary>
    private static bool PairsAreWhole(ReadOnlySpan<char> span)
    {
        for (int i = 0; i < span.Length; i++)
        {
            if (char.IsHighSurrogate(span[i]))
            {
                if (i + 1 == span.Length || !char.IsLowSurrogate(span[i + 1]))
                {
                    return false;
                }

                i++;
            }
            else if (char.IsLowSurrogate(span[i]))
            {
                return false;
            }
        }

        return true;
    }
}
```

- [ ] **Step 4: Check text in `TabularWriter`**

In `Write(string?)`, replace `Check(_sheet.WriteText(value), column);` with:

```csharp
        Check(TextRules.Check(value) ?? _sheet.WriteText(value), column);
```

In `CheckColumns`, inside the `foreach`, after the width check:

```csharp
            if (TextRules.Check(column.Header) is { } code)
            {
                throw Faulting(new ArgumentException($"The header \"{column.Header}\" cannot be written: {code}.", nameof(columns)));
            }
```

In `ISheetWriter.WriteText`'s summary, restore `<see cref="TextRules"/>`.

- [ ] **Step 5: Teach `CsvSheetWriter.WriteText` the reader's limits and the guard**

Add the constants at the top of `CsvSheetWriter`:

```csharp
    /// <summary>The line breaks one quoted field may hold before the reader takes its quote for a stray one.</summary>
    private static readonly int MaxLineBreaks = CsvCursorOptions.Default.MaxQuotedFieldLines;

    /// <summary>The longest field the reader reads.</summary>
    private static readonly int MaxFieldChars = CsvCursorOptions.Default.MaxFieldChars;
```

Replace `WriteText` with:

```csharp
    public string? WriteText(string value)
    {
        ReadOnlySpan<char> text = value;
        bool guard = _format.FormulaGuard && !text.IsEmpty && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r';

        if (text.Length + (guard ? 1 : 0) > MaxFieldChars)
        {
            return ErrorCodes.Write.TextTooLong;
        }

        bool quoted = text.IndexOfAny(_needsQuotes) >= 0;

        if (quoted && CountLineBreaks(text) > MaxLineBreaks)
        {
            return ErrorCodes.Write.TooManyLines;
        }

        Separate();

        if (!quoted)
        {
            if (guard)
            {
                Append('\'');
            }

            Append(text);
            return null;
        }

        Append('"');

        if (guard)
        {
            Append('\'');
        }

        while (true)
        {
            int quote = text.IndexOf('"');

            if (quote < 0)
            {
                Append(text);
                break;
            }

            Append(text[..(quote + 1)]);
            Append('"');
            text = text[(quote + 1)..];
        }

        Append('"');
        return null;
    }

    /// <summary>Line breaks as the reader counts them: a line feed, or a carriage return not followed by one.</summary>
    private static int CountLineBreaks(ReadOnlySpan<char> text)
    {
        int count = 0;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n' || (text[i] == '\r' && (i + 1 == text.Length || text[i + 1] != '\n')))
            {
                count++;
            }
        }

        return count;
    }
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~CsvTextTests|FullyQualifiedName~TabularWriterTests|FullyQualifiedName~CsvValueTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/TriasDev.Tabular tests/TriasDev.Tabular.Tests
git commit -m "feat(write): refuse text the reader would not give back, and an opt-in formula guard for csv"
```

---

### Task 7: The round trip, and the library-wide contracts

**Files:**
- Test: `tests/TriasDev.Tabular.Tests/Writing/CsvRoundTripTests.cs`
- Modify: `tests/TriasDev.Tabular.Tests/NullArgumentTests.cs` (two entries in `Calls`)
- Modify: `tests/TriasDev.Tabular.Tests/OptionDefaultsTests.cs` (assert the writer defaults)
- Modify: `tests/TriasDev.Tabular.InvariantGlobalizationTests/InvariantGlobalizationTests.cs` (two tests)
- Modify: `docs/KNOWN-ISSUES.md` (the documented round-trip exceptions)

**Interfaces:**
- Consumes: everything above; `TabularImporter.Import<T>(Stream, string, MappingPlan, ImportSchema, TabularRowMapper<T>, ImportOptions?, CancellationToken)`, `ImportRun<T>.ReadRows`, `ImportRun<T>.Summary.RowsSkipped`, `ImportRow` indexers for `TextImportField`, `IntegerImportField`, `DecimalImportField`, `DateImportField`, `BooleanImportField`.
- Produces: nothing new — the proof that part 1 meets the spec's round-trip rule for csv.

- [ ] **Step 1: Write the round-trip tests**

Create `tests/TriasDev.Tabular.Tests/Writing/CsvRoundTripTests.cs`:

```csharp
using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>
/// The rule the writer exists for: what it writes, the import reads back as the same value of the
/// same type — under the invariant culture and the ones a person opens csv files in.
/// </summary>
public sealed class CsvRoundTripTests
{
    private static readonly TextImportField Name = ImportField.Text("Name");
    private static readonly IntegerImportField Count = ImportField.Integer("Count");
    private static readonly DecimalImportField Amount = ImportField.Decimal("Amount");
    private static readonly DateImportField Start = ImportField.Date("Start");
    private static readonly BooleanImportField Active = ImportField.Boolean("Active");

    private static readonly ImportSchema Schema = new() { Fields = [Name, Count, Amount, Start, Active] };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<string> Cultures => ["", "de-DE", "en-US"];

    private sealed record Row(string? Name, long? Count, decimal? Amount, DateTime? Start, bool? Active);

    private static async Task<byte[]> WriteAsync(string culture, Action<TabularWriter> rows, bool formulaGuard = false)
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(
            target,
            TabularFormat.Csv,
            new TabularWriterOptions { Csv = new CsvWriterOptions { Culture = culture, FormulaGuard = formulaGuard } }))
        {
            writer.BeginSheet("data", [.. Schema.Fields.Select(f => new WriteColumn(f.Name))]);
            rows(writer);
            await writer.CompleteAsync(Token);
        }

        return target.ToArray();
    }

    private static (List<ImportOutcome<Row>> Rows, int Skipped) Import(byte[] file, string culture)
    {
        MappingPlan plan = new()
        {
            Culture = culture,
            Bindings = [.. Schema.Fields.Select((f, i) => new ColumnBinding { ColumnIndex = i, Header = f.Name, FieldName = f.Name })],
        };

        using ImportRun<Row> run = TabularImporter.Import(
            new MemoryStream(file, writable: false),
            "t.csv",
            plan,
            Schema,
            row => new Row(row[Name], row[Count], row[Amount], row[Start], row[Active]),
            cancellationToken: Token);

        List<ImportOutcome<Row>> rows = [.. run.ReadRows(Token)];
        return (rows, run.Summary.RowsSkipped);
    }

    private static void WriteRow(TabularWriter writer, string? name, long count, decimal amount, DateTime start, bool active)
    {
        writer.BeginRow();
        writer.Write(name);
        writer.Write(count);
        writer.Write(amount);
        writer.Write(start);
        writer.Write(active);
        writer.EndRow();
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task ReadsBackEveryKindOfValue(string culture)
    {
        Row[] written =
        [
            new("Alpha", 0, 0.1m, new DateTime(2026, 10, 3), true),
            new("Grüße, \"quoted\"; | piped", -1, -1_234_567.891m, new DateTime(2026, 10, 3, 14, 5, 6, 789), false),
            new("007", long.MinValue, 123_456_789_012_345.6789m, new DateTime(1900, 1, 1), true),
            new("two\nlines and\r\nwindows", long.MaxValue, 1.50m, new DateTime(1900, 3, 1, 0, 0, 0, 1), false),
            new("emoji 👍 and _x0001_ and tab\there", 9_007_199_254_740_993, 0m, new DateTime(9999, 12, 31, 23, 59, 59, 999), true),
            new("a   run   of   spaces", 42, 79_228_162_514_264_337_593_543_950_335m, new DateTime(2, 1, 1), false),
        ];

        byte[] file = await WriteAsync(culture, writer =>
        {
            foreach (Row row in written)
            {
                WriteRow(writer, row.Name, row.Count!.Value, row.Amount!.Value, row.Start!.Value, row.Active!.Value);
            }
        });

        (List<ImportOutcome<Row>> read, _) = Import(file, culture);

        Assert.All(read, outcome => Assert.False(outcome.HasErrors, string.Join(", ", outcome.Errors.Select(e => e.Code))));
        Assert.Equal(written, read.Select(outcome => outcome.Value));
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task ReadsBackADoubleAsTheDecimalItsDigitsSay(string culture)
    {
        double[] values = [0.1, -2.5, 1e20, 1e-7, 123_456_789.012345];

        byte[] file = await WriteAsync(culture, writer =>
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

        (List<ImportOutcome<Row>> read, _) = Import(file, culture);

        Assert.Equal(values.Select(v => (decimal?)(decimal)v), read.Select(outcome => outcome.Value!.Amount));
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task ReadsBackDatesAsWrittenToTheMillisecond(string culture)
    {
        DateTime[] values =
        [
            new DateTime(2026, 10, 3, 23, 59, 59, 999).AddTicks(9_999),
            DateTime.MaxValue,
            new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Local),
        ];

        byte[] file = await WriteAsync(culture, writer =>
        {
            foreach (DateTime value in values)
            {
                writer.BeginRow();
                writer.Write("x");
                writer.WriteEmpty();
                writer.WriteEmpty();
                writer.Write(value);
                writer.EndRow();
            }

            writer.BeginRow();
            writer.Write("x");
            writer.WriteEmpty();
            writer.WriteEmpty();
            writer.Write(new DateOnly(2026, 10, 3));
            writer.EndRow();
        });

        (List<ImportOutcome<Row>> read, _) = Import(file, culture);

        DateTime[] expected =
        [
            new(2026, 10, 3, 23, 59, 59, 999),
            new(9999, 12, 31, 23, 59, 59, 999),
            new(2026, 10, 3, 12, 0, 0),
            new(2026, 10, 3, 12, 0, 0),
            new(2026, 10, 3),
        ];

        Assert.Equal(expected.Select(d => (DateTime?)d), read.Select(outcome => outcome.Value!.Start));
        Assert.All(read, outcome => Assert.Equal(DateTimeKind.Unspecified, outcome.Value!.Start!.Value.Kind));
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task ReadsEmptyAndWhitespaceTextAsNoValueAndSkipsAnEmptyRow(string culture)
    {
        byte[] file = await WriteAsync(culture, writer =>
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

        (List<ImportOutcome<Row>> read, int skipped) = Import(file, culture);

        Assert.Equal(new string?[] { null, null, null, "padded" }, read.Select(outcome => outcome.Value!.Name));
        Assert.Equal(1, skipped);
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task ReadsBackAFieldWithAsManyLineBreaksAsAllowed(string culture)
    {
        string note = string.Join('\n', Enumerable.Repeat("line", 101));

        byte[] file = await WriteAsync(culture, writer =>
        {
            writer.BeginRow();
            writer.Write(note);
            writer.EndRow();
            writer.BeginRow();
            writer.Write("after");
            writer.EndRow();
        });

        (List<ImportOutcome<Row>> read, _) = Import(file, culture);

        Assert.Equal(new[] { note, "after" }, read.Select(outcome => outcome.Value!.Name));
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task ReadsBackASingleColumnFile(string culture)
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(
            target,
            TabularFormat.Csv,
            new TabularWriterOptions { Csv = new CsvWriterOptions { Culture = culture } }))
        {
            writer.BeginSheet("data", [new("Amount")]);

            foreach (decimal amount in new[] { 1.5m, -2.25m, 1000m })
            {
                writer.BeginRow();
                writer.Write(amount);
                writer.EndRow();
            }

            await writer.CompleteAsync(Token);
        }

        ImportSchema schema = new() { Fields = [Amount] };
        MappingPlan plan = new() { Culture = culture, Bindings = [new ColumnBinding { ColumnIndex = 0, Header = "Amount", FieldName = "Amount" }] };

        using ImportRun<decimal?> run = TabularImporter.Import(new MemoryStream(target.ToArray(), writable: false), "t.csv", plan, schema, row => row[Amount], cancellationToken: Token);

        Assert.Equal(new decimal?[] { 1.5m, -2.25m, 1000m }, run.ReadRows(Token).Select(outcome => outcome.Value));
    }

    [Fact]
    public async Task TheFormulaGuardIsWhatTheImportReadsBack()
    {
        byte[] file = await WriteAsync("", writer =>
        {
            writer.BeginRow();
            writer.Write("=1+2");
            writer.Write(-5L);
            writer.EndRow();
        }, formulaGuard: true);

        (List<ImportOutcome<Row>> read, _) = Import(file, "");

        Row row = Assert.Single(read).Value!;
        Assert.Equal("'=1+2", row.Name);
        Assert.Equal(-5, row.Count);
    }
}
```

- [ ] **Step 2: Run them**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~CsvRoundTripTests"`
Expected: PASS. These tests prove what earlier tasks built rather than drive new code, so a failure is a finding: **stop and report which value, which culture, what was written (`Encoding.UTF8.GetString(file)`) and what came back.** Do not change a test's expectation to match. Known to watch: a lone `\r` inside a quoted field (in `ReadsBackEveryKindOfValue`, row 4 has `\r\n` only — if the reader turns it into `\n`, that is a spec question, not a test fix).

- [ ] **Step 3: Add the library-wide contract entries**

In `tests/TriasDev.Tabular.Tests/NullArgumentTests.cs`, add to `Calls` (and `using TriasDev.Tabular.Csv;` is already there):

```csharp
        { "TabularWriter.Create", "stream", () => TabularWriter.Create(null!, TabularFormat.Csv) },
        { "TabularWriter.BeginSheet", "name", () => TabularWriter.Create(new MemoryStream(), TabularFormat.Csv).BeginSheet(null!, [new("a")]) },
```

In `tests/TriasDev.Tabular.Tests/OptionDefaultsTests.cs`, at the end of `ShipsTheDefaultsTheDocumentsPromise`:

```csharp
        CsvWriterOptions csvWriter = TabularWriterOptions.Default.Csv;

        Assert.Null(csvWriter.Culture);
        Assert.Null(csvWriter.Delimiter);
        Assert.True(csvWriter.ByteOrderMark);
        Assert.False(csvWriter.FormulaGuard);
        Assert.False(TabularWriterOptions.Default.LeaveOpen);
```

In `tests/TriasDev.Tabular.InvariantGlobalizationTests/InvariantGlobalizationTests.cs`, add:

```csharp
    [Fact]
    public void RefusesToWriteInACultureTheRuntimeDoesNotHave()
    {
        ArgumentException refused = Assert.ThrowsAny<ArgumentException>(() => TabularWriter.Create(
            new MemoryStream(),
            TabularFormat.Csv,
            new TabularWriterOptions { Csv = new CsvWriterOptions { Culture = "de-DE" } }));

        Assert.Contains(nameof(CsvWriterOptions.Culture), refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WritesAnInvariantCsvTheImportReadsBack()
    {
        MemoryStream target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Csv, new TabularWriterOptions { LeaveOpen = true }))
        {
            writer.BeginSheet("data", [new("amount")]);
            writer.BeginRow();
            writer.Write(1.5m);
            writer.EndRow();
            await writer.CompleteAsync(TestContext.Current.CancellationToken);
        }

        target.Position = 0;
        using ImportRun<decimal?> run = TabularImporter.Import(target, "t.csv", Plan(null), Schema, row => row[Amount], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1.5m, Assert.Single(run.ReadRows(TestContext.Current.CancellationToken)).Value);
    }
```

- [ ] **Step 4: Record the round-trip exceptions**

Append to `docs/KNOWN-ISSUES.md` a section (match the file's existing heading level and style):

```markdown
## Writing: what does not come back exactly as written

By design, and the same in every format:

- Leading and trailing whitespace in text is trimmed on reading; empty and whitespace-only text reads as no value.
- Time is truncated to whole milliseconds.
- `DateTime.Kind` is not kept: the wall-clock value comes back as `Unspecified`.
- A `DateOnly` comes back as a `DateTime` at midnight.
- With `CsvWriterOptions.FormulaGuard`, text starting with `=`, `+`, `-`, `@`, tab or CR comes back with a leading `'`.
```

- [ ] **Step 5: Run the whole suite on both targets**

Run: `dotnet test --solution TriasDev.Tabular.slnx`
Expected: PASS, including `TabularIndependenceTests`, `ErrorCodeCatalogTests`, `NullArgumentTests` and the invariant-globalization project. Then `dotnet build TriasDev.Tabular.slnx -c Release` with zero warnings.

- [ ] **Step 6: Commit**

```bash
git add tests docs/KNOWN-ISSUES.md
git commit -m "test(write): csv round-trips through the import in the invariant, German and US cultures"
```

---

## After the last task

Open one pull request for part 1 (conventional title `feat(write): write csv through TabularWriter, round-tripping through the import`), body referencing #67 and the spec. Merge when CI is green. Then write the part 2 plan (`ZipWriter`, CRC-32, xlsx) against `ISheetWriter`, `SpillBuffer.TotalWritten`, `ValueChecks` and `TextRules` as built here.
