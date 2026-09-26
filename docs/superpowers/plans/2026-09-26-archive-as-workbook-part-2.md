# A zip archive read as one workbook — part 2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A zip archive handed to `TabularFile.Open` reads as one workbook whose sheets are every sheet of every readable entry — csv, xlsx and ods — each carrying the entry's path as `Source`.

**Architecture:** A new `ArchiveCursor : ITabularCursor` (namespace `TriasDev.Tabular.Archive`) lists the archive's entries at construction, sniffs each by its bytes, and flattens their sheets into one ordered list. Reading delegates to one inner cursor at a time: a `CsvCursor` over the entry stream with its dialect detected from a buffered head, or an `XlsxCursor` / `OdsCursor` over the entry copied into a chunked in-memory buffer. `TabularFile.Detect` tells a workbook zip from an archive by its central directory.

**Tech Stack:** .NET 8 + 10, `System.IO.Compression` only, xunit v4 on Microsoft.Testing.Platform.

**Spec:** `docs/superpowers/specs/2026-09-25-archive-as-workbook-design.md`, part 2 (revised 2026-09-26).

## Global Constraints

- No package references in the library; only the base class library.
- Error codes, never messages; every library exception derives from `TabularException`. `ArgumentException` for programmer errors only.
- Rows are views: no per-row allocations added on the read path; the archive's `ReadRow` only delegates.
- Every collection that grows with the file has a ceiling; each new bound goes in the guide's "Bounds" table and has a hostile-input test.
- Cancellation is passed into every reading operation as its last parameter.
- A stream handed over is closed on every path, failures included, unless the caller asked for `LeaveOpen`.
- Options are checked where they are handed over (`OptionChecks`), never discovered mid-read.
- Public API changes go in `PublicAPI.Unshipped.txt`; `TabularFormat` is documented as open to new members.
- Tests use only the public API; fixtures are built from raw bytes.
- Nothing from the local fixtures folder (names or content) appears in code, docs or commits.

## Rulings made while planning (deviations from the spec)

- **`ArchiveCursorOptions` holds the archive's own bounds only** (`MaxEntries`, `MaxUncompressedBytes`, `MaxEmbeddedWorkbookBytes`). The spec also put `Csv` / `Xlsx` / `Ods` in it; the entries are read with the ones `TabularOpenOptions` already carries, so a caller never has two csv settings of which one is silently ignored. Hence `ArchiveCursor(Stream, TabularOpenOptions?, CancellationToken)`, `LeaveOpen` taken from the options. Cost if wrong: a caller who wanted different csv options for entries than for plain files — none known.
- **Skip reasons are an enum, `SkippedEntryReason`,** not error-code strings: they are not errors, and an enum needs no catalog.
- **A workbook entry that is unreadable (corrupt, or an unsupported kind such as `.xlsb`) is skipped with a reason; a bound it exceeds still fails the archive.** Bounds are the library's defence and are never downgraded to a skip.
- **`ReadFraction` counts uncompressed bytes** (entry bytes read against the entries' declared sizes), not compressed ones: the compressed position of an entry stream is not observable.
- **`TabularFormat.Zip`** is the container's format; sheets keep their own (`Csv`, `Xlsx`, `Ods`).
- **An ODF document that is not a spreadsheet** is refused before any cursor is built, with its own message — the xlsx path's old "OpenDocument (.ods) … not supported yet" message is wrong since #13.

## Review Focus

1. An archive whose csv entry has a UTF-8 BOM, or UTF-16 — the dialect probe is taken from the entry's head, and the rest of the stream must follow the probe byte for byte (no lost or doubled bytes at the join).
2. Moving back and forth between sheets of different sources and of the same workbook source — the inner cursor is replaced or reused correctly, and diagnostics never count a repair twice.
3. Disposal on every path: a failure in the middle of construction (bad entry, bound exceeded) must close the archive stream unless `LeaveOpen`, and must release a buffered workbook.
4. An archive whose only readable content is skipped entries — `format.unsupported`, with the stream closed.
5. A workbook zip without `[Content_Types].xml` but with `_rels/.rels` (a writer that omits the first) is still a workbook, not an archive.

Each has a test in the task that owns the code.

---

### Task 1: Public surface — options, format, skipped entries

**Files:**
- Create: `src/TriasDev.Tabular/Archive/ArchiveCursorOptions.cs`
- Create: `src/TriasDev.Tabular/Abstractions/SkippedEntry.cs` (record `SkippedEntry` and enum `SkippedEntryReason`)
- Modify: `src/TriasDev.Tabular/Abstractions/TabularFormat.cs` — add `Zip`
- Modify: `src/TriasDev.Tabular/Abstractions/TabularOpenOptions.cs` — add `Archive`
- Modify: `src/TriasDev.Tabular/Abstractions/ITabularCursor.cs` — add `IReadOnlyList<SkippedEntry> SkippedEntries => [];`
- Modify: `src/TriasDev.Tabular/Analysis/FileProfile.cs` — add `IReadOnlyList<SkippedEntry> SkippedEntries { get; init; } = [];`
- Modify: `src/TriasDev.Tabular/Analysis/TabularAnalyzer.cs` — copy `cursor.SkippedEntries` into the profile
- Modify: `src/TriasDev.Tabular/PublicAPI.Unshipped.txt`
- Test: `tests/TriasDev.Tabular.Tests/Archive/ArchiveCursorOptionsTests.cs`

**Interfaces — Produces:**

```csharp
namespace TriasDev.Tabular.Archive;
public sealed record ArchiveCursorOptions
{
    public static ArchiveCursorOptions Default { get; } = new();
    public int MaxEntries { get; init; } = 16_384;
    public long MaxUncompressedBytes { get; init; } = 8L * 1024 * 1024 * 1024;
    public long MaxEmbeddedWorkbookBytes { get; init; } = 256L * 1024 * 1024;
    internal ArchiveCursorOptions Checked();   // OptionChecks.AtLeast(…, 1, …) on all three
}

namespace TriasDev.Tabular;
public enum SkippedEntryReason { NestedArchive, OtherDocument, LegacyWorkbook, XmlDocument, Binary, Encrypted, Unsupported, Unreadable }
public sealed record SkippedEntry { public required string Path { get; init; } public required SkippedEntryReason Reason { get; init; } }
```

- [ ] **Step 1: failing test** — `ArchiveCursorOptionsTests`: `Default` values as above; each of the three set to 0 is refused with `ArgumentOutOfRangeException` when handed to `TabularFile.Open` (through `TabularOpenOptions.Archive`) — even for a csv, because options are checked where they are handed over; `new FileProfile{…}.SkippedEntries` is empty by default; `TabularAnalyzer` on a plain csv yields an empty `SkippedEntries`.
- [ ] **Step 2:** run `dotnet test --project tests/TriasDev.Tabular.Tests --filter-class "*ArchiveCursorOptionsTests" --framework net10.0` — expected: build failure (types missing).
- [ ] **Step 3:** implement the types; `TabularFile.Open` calls `effective.Archive.Checked()` alongside the others; the analyzer sets `SkippedEntries = cursor.SkippedEntries`. `MaxUncompressedBytes` defaults to 8 GB to admit a zipped ods at the ods budget.
- [ ] **Step 4:** tests pass; RS0016 lines added to `PublicAPI.Unshipped.txt`.
- [ ] **Step 5:** commit `feat(archive): options, the zip format and skipped entries`.

### Task 2: `ArchiveCursor` over csv entries

**Files:**
- Create: `src/TriasDev.Tabular/Archive/ArchiveCursor.cs`
- Modify: `src/TriasDev.Tabular/Csv/CsvDialectDetector.cs` — extract `internal static CsvDialect Detect(ReadOnlySpan<byte> head, CsvCursorOptions options)`; the public `Detect(Stream, …)` reads the probe and calls it
- Test: `tests/TriasDev.Tabular.Tests/Fixtures/ZipArchiveBuilder.cs` (raw entries by path, bytes, compression; option to set the encryption flag)
- Test: `tests/TriasDev.Tabular.Tests/Archive/ArchiveCursorCsvTests.cs`

**Interfaces — Produces:**

```csharp
namespace TriasDev.Tabular.Archive;
public sealed class ArchiveCursor : ITabularCursor
{
    public ArchiveCursor(Stream stream, TabularOpenOptions? options = null, CancellationToken cancellationToken = default);
    public TabularFormat Format => TabularFormat.Zip;
    public IReadOnlyList<SkippedEntry> SkippedEntries { get; }
    // ITabularCursor members; Dialect is the current csv sheet's; Diagnostics is cumulative over all inner cursors
}
```

Construction:
1. `options.Csv/Xlsx/Ods/Archive.Checked()`; open `ZipArchive(stream, Read, leaveOpen: true)`; wrap the whole constructor so that any exception disposes the zip and (unless `LeaveOpen`) the stream.
2. Enumerate entries: count against `MaxEntries`, sum declared `Length` against `MaxUncompressedBytes` (both `TabularLimitException`, option names `MaxEntries` / `MaxUncompressedBytes`).
3. Silent skips: `FullName` ends with `/`; any path segment starting with `.`; paths under `__MACOSX/`.
4. Order the rest by `FullName`, `StringComparer.Ordinal`.
5. Sniff each (cancellation checked per entry): `IsEncrypted` → `Encrypted`; read the head (`Csv.DialectProbeBytes`) into a byte array; zip signature → Task 3 (until then `NestedArchive`); OLE2 signature → `LegacyWorkbook`; XML declaration (same rule as `CsvDialectDetector`) → `XmlDocument`; otherwise `CsvDialectDetector.Detect(head, Csv)`, a `TabularFormatException` from it → `Binary`. A readable csv becomes one `SheetInfo { Name = file name without extension, Format = Csv, Source = FullName }`, and its detected dialect is kept for reading.
6. `MaxSheets` (xlsx options) bounds the total.
7. No sheet at all → `TabularFormatException(Unsupported, "The archive holds no file that can be read as a table.")`.

Reading:
- `MoveToSheet(i)`: out of range → false. A csv source is always reopened: `entry.Open()` wrapped in the existing `CountingStream`, handed to `CsvCursor` with `Csv with { Dialect = detected }` — a stated dialect needs no seeking, so the head read at construction is only for detection. Moving back therefore re-reads from row 1. The previous inner cursor's diagnostics are folded into a running total before it is disposed.
- `Diagnostics` getter: total + current inner's counts, into one stable `CursorDiagnostics` instance.
- `ReadRow(ct)`: delegates to the inner cursor; the constructor moves to sheet 0, as the other cursors start on their first sheet.
- `ReadFraction`: (declared bytes of sources before the current + bytes read of the current) / declared total.
- `Dispose`: inner cursor, zip, stream unless `LeaveOpen`.

- [ ] **Step 1: failing tests** (`ArchiveCursorCsvTests`, cursor built directly with `new ArchiveCursor(stream, …)`):
  - one csv → one sheet, name `orders`, `Source == "export/orders.csv"`, `Format == Csv`, rows equal to the same csv read by `CsvCursor`;
  - three csv in different dialects (`;` UTF-8 with BOM, `,` Windows-1252 with umlauts, tab UTF-16 LE with BOM), written in reverse path order → sheets ordered by path, each sheet's `Dialect` matches its own file (Review Focus 1);
  - silent skips (`__MACOSX/._a.csv`, `.DS_Store`, `dir/`) absent from both `Sheets` and `SkippedEntries`;
  - skipped with reasons: a pdf-like binary → `Binary`, OLE2 head → `LegacyWorkbook`, `<?xml` → `XmlDocument`, encryption flag → `Encrypted`, a plain zip → `NestedArchive`;
  - moving from sheet 1 back to sheet 0 re-reads sheet 0 from its first row; diagnostics: an archive of two csv each with one unterminated quote → after reading both, `Diagnostics.RecoveredUnterminatedQuotes == 2`, and reading sheet 0 again makes it 3, never 4 (Review Focus 2);
  - bounds: `MaxEntries = 2` with three entries, `MaxUncompressedBytes` below the declared sum → `TabularLimitException` with the option's name; the stream is disposed afterwards (Review Focus 3);
  - an archive of only a pdf and `.DS_Store` → `format.unsupported`, stream disposed (Review Focus 4);
  - `ReadFraction` rises from 0 to 1 across the sheets.
- [ ] **Step 2:** run the class — expected: build failure.
- [ ] **Step 3:** implement.
- [ ] **Step 4:** tests pass on net10 and net8.
- [ ] **Step 5:** commit `feat(archive): read the csv files in a zip archive as sheets`.

### Task 3: Workbook entries through a chunked buffer

**Files:**
- Create: `src/TriasDev.Tabular/Archive/ChunkedBuffer.cs` (internal seekable read-only stream over 1 MB chunks, filled from a source up to a limit; throws `TabularLimitException(MaxEmbeddedWorkbookBytes)` past it)
- Modify: `src/TriasDev.Tabular/Archive/ArchiveCursor.cs`
- Test: `tests/TriasDev.Tabular.Tests/Archive/ArchiveCursorWorkbookTests.cs`

Sniffing a zip-signature entry: buffer it (`ChunkedBuffer`, bounded; the declared `Length` above the bound fails before copying), then classify with `TabularFile.Detect` on the buffer: `Xlsx` → open an `XlsxCursor` over the buffer to list its sheets; `Ods` → `OdsCursor`; a zip that is neither → `NestedArchive`; an ODF mimetype of another type → `OtherDocument`. A `TabularFormatException` while listing → `Unsupported` for code `format.unsupported`, else `Unreadable`. `TabularLimitException` propagates. The buffer is released after listing. Each workbook sheet: `SheetInfo { Name = its own name, Format = Xlsx/Ods, Source = FullName }`.

Reading a workbook source: buffer the entry again, open its cursor (kept while the next sheet is of the same source; `MoveToSheet` then delegates with the local index), release both when moving to another source.

- [ ] **Step 1: failing tests:**
  - a mixed archive — `a.csv`, `b.xlsx` with sheets `S1`,`S2`, `c.ods` with `T1` → sheets `a`, `S1`, `S2`, `T1` with sources and formats; each reads the same rows as the file on its own;
  - two workbooks each with `Sheet1` → both listed, told apart by `Source`;
  - an xlsx entry larger than one chunk (stored uncompressed, > 1 MB of shared strings) reads correctly — the buffer's chunk boundary (Review Focus: chunked seeks);
  - `MaxEmbeddedWorkbookBytes` below the workbook's size → `TabularLimitException("MaxEmbeddedWorkbookBytes")`, stream disposed;
  - an `.odt` inside → `OtherDocument`; a corrupt xlsx (content types, no workbook part) → `Unreadable`; an `.xlsb`-shaped zip → `Unsupported`;
  - moving S2 → a → S1 re-opens the workbook and reads S1 from row 1.
- [ ] **Step 2:** run — fails.
- [ ] **Step 3:** implement.
- [ ] **Step 4:** pass on both frameworks.
- [ ] **Step 5:** commit `feat(archive): read xlsx and ods workbooks inside an archive`.

### Task 4: Detection, `TabularFile.Open`, analysis and import end to end

**Files:**
- Modify: `src/TriasDev.Tabular/Abstractions/TabularFile.cs`
- Modify: `src/TriasDev.Tabular/Xlsx/XlsxCursor.cs` — `NotAWorkbook`: the ODF branch says the file is an OpenDocument document that is not a spreadsheet
- Test: `tests/TriasDev.Tabular.Tests/Archive/ArchiveDetectionTests.cs`; adjust existing detection tests that relied on "every zip is a workbook"

`Detect`: not a zip → `Csv` (unchanged); the local-header ods check → `Ods` (unchanged); otherwise open the central directory (`ZipArchive`, leave open, rewind): `[Content_Types].xml` or `_rels/.rels` → `Xlsx`; a `mimetype` entry whose content starts with `application/vnd.oasis.opendocument.spreadsheet` → `Ods` (mimetype not first); another `mimetype` naming OpenDocument → throw `TabularFormatException(Unsupported, "…an OpenDocument document that is not a spreadsheet…")` from `Open` (Detect returns `Xlsx` so the xlsx path refuses it, with the corrected message); anything else → `Zip`. An unreadable central directory → `Xlsx` so the workbook path reports it as corrupt, as today.

- [ ] **Step 1: failing tests:**
  - an archive of one csv opened with `TabularFile.Open` → `ArchiveCursor`, `Format == Zip`;
  - an xlsx renamed `.zip` → `Xlsx`; a workbook zip with `_rels/.rels` and no `[Content_Types].xml` → `Xlsx` (Review Focus 5);
  - an ods whose `mimetype` is not first → `Ods`; an `.odt` → `format.unsupported` whose message says "not a spreadsheet";
  - `TabularAnalyzer` over an archive: `FileProfile.Format == Zip`, per-sheet `Format`/`Source`/`Dialect`, `SkippedEntries` listing the pdf;
  - import end to end: `TabularImporter.Import(stream, …)` of a zipped csv with a plan from `MappingPlan.ByHeader` → the same rows as the plain csv; the plan's `SheetSource` equals the entry path;
  - a zipped csv of 20,000 rows imports with the stream closed afterwards.
- [ ] **Step 2:** run — fails.
- [ ] **Step 3:** implement.
- [ ] **Step 4:** full suite passes, both frameworks, 0 warnings.
- [ ] **Step 5:** commit `feat(archive): open a zip archive by its contents`.

### Task 5: Documentation and the performance check

**Files:** `README.md`, `docs/guide.md` (what an archive reads as; Bounds rows for the three archive bounds; `TabularOpenOptions.Archive`; performance paragraph), `docs/KNOWN-ISSUES.md` (the `MoveToSheet` rewind entry notes that archive csv sheets do rewind), `CLAUDE.md` (Archive layer), `benchmarks/TriasDev.Tabular.Benchmarks` (a zip candidate: `ArchiveCursor` read and full analysis), `samples` if they list formats.

- [ ] **Step 1:** benchmark the 5M-row csv zipped against unpacked, alternating, two runs each: read and full analysis time, peak memory. Expected: peak flat (≈ the csv's), time = csv + decompression.
- [ ] **Step 2:** write the docs with the measured numbers (generic fixture names only).
- [ ] **Step 3:** privacy scan; full suite; `dotnet format --verify-no-changes`; pack.
- [ ] **Step 4:** commit `docs(archive): …`; final whole-branch review; fix Critical/Important; push; close #19.
