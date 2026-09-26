# A zip archive read as one workbook

Status: agreed design, 2026-09-25. Part 1 lands before v0.1; part 2 after it. Part 2 revised on
2026-09-26 after OpenDocument reading (#13) landed: `.ods` entries, the XML refusal, and an embedded
workbook budget without the 2 GB ceiling of one array.

## Intent

A caller hands over a zip archive and gets back what they would get for a workbook: one list of
sheets, each profiled and mappable on its own. The UI never learns what a "document" is — it sees
sheets, and may group them by where they came from.

Why:

1. **One large csv, zipped** to shrink the upload. A 572 MB export is 60–80 MB zipped, and it can be
   read straight from the archive without unpacking it to disk.
2. **Several csv files of one export** in a single archive.
3. **A mixed archive** (workbooks and csv side by side) — rare, but it falls out of 1 and 2 at almost
   no extra cost.

Not the goal: nested archives, extraction to disk, entry selection by pattern, password-protected
archives.

## The model: a flat virtual workbook

An archive is a workbook whose sheets are every sheet of every readable entry. Each sheet carries its
**source** — the entry's path — so a UI can group sheets by it. A csv entry is one sheet; a workbook
entry contributes all of its sheets.

Rejected: a two-level `Documents[] → Sheets[]` model. More faithful to the archive, but every
consumer would carry the nesting, and a plan would need a document-plus-sheet address — a cost paid
by callers who never see an archive.

Nothing above the cursor changes: profiling, mapping, precheck and import still work on one sheet.

## Part 1 — before v0.1 (breaking, so now)

Facts that today describe the whole file become facts about a sheet.

### `SheetInfo`

```csharp
public sealed record SheetInfo
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public required TabularFormat Format { get; init; }   // the format of the sheet's source
    public string? Source { get; init; }                  // path inside an archive; null otherwise
}
```

### `SheetProfile` and `FileProfile`

```csharp
public sealed record SheetProfile
{
    // Index, Name, RowCount, Columns, HeaderRowIndex — unchanged
    public required TabularFormat Format { get; init; }
    public string? Source { get; init; }
    public CsvDialect? Dialect { get; init; }                     // moved from FileProfile
    public required CursorDiagnostics Diagnostics { get; init; }  // repairs made in this sheet
}

public sealed record FileProfile
{
    public required TabularFormat Format { get; init; }           // the container's format
    public required IReadOnlyList<SheetProfile> Sheets { get; init; }
    public required CursorDiagnostics Diagnostics { get; init; }  // the file's total
    // Dialect removed
}
```

Both `Diagnostics` are **snapshots** taken when the pass ends, not the cursor's live object. That also
settles the #15 item on live mutable results. A sheet's diagnostics are the repairs counted while that
sheet was read.

### `ITabularCursor`

- `Dialect` is documented as the dialect of the **current** sheet, or null.
- `Sheets` carries the new `SheetInfo` members; `CsvCursor` and `XlsxCursor` set `Format`, and leave
  `Source` null.

### `TabularFormat`

Documented as open to new members (`Zip`, later `Ods`): consumers must not switch over it exhaustively
without a default arm. No member is added in part 1.

### `MappingPlan`: which sheet, checked

Entry order in an archive is arbitrary, and two workbooks may both hold a `Sheet1`. The plan keeps
addressing a sheet by index, and gains two optional checksums, in the spirit of
`ColumnBinding.SourceHeader`:

```csharp
public string? SheetName { get; init; }
public string? SheetSource { get; init; }
```

When set, extraction compares them with the sheet found at `SheetIndex` and refuses a mismatch as
`structure.sheet-changed` (new code, a `TabularStructureException`); the precheck blocks such a plan
with the same code and judges nothing else, because analysing again would find the same other sheet
there. `MappingPlan.ByHeader` fills both from the `SheetProfile` — except a plain csv's name, which is
whatever name the caller passed in, not a fact of the file. Null means "not checked", so a
hand-written plan keeps working.

Addressing a sheet by name instead of index stays an idea (docs/IDEAS.md).

### Tests for part 1

- A csv and an xlsx profile carry `Format` per sheet and `Source == null`; a csv sheet carries its
  dialect, an xlsx sheet none.
- A sheet's diagnostics count only that sheet's repairs; the file's are the sum, and neither changes
  when the cursor is read again after the pass.
- A plan whose `SheetName` or `SheetSource` does not match the sheet at its index is refused with
  `structure.sheet-changed`; one without them is not checked; `ByHeader` fills both.
- `ErrorCodeCatalogTests` picks up `structure.sheet-changed` from the guide's table.

## Part 2 — after v0.1 (additive)

### Detection

By bytes, as today. A zip signature opens the central directory (at the end of the file, cheap):

| Found | Read as |
|---|---|
| a first entry `mimetype` naming an OpenDocument spreadsheet (read from the local header, as today) | `TabularFormat.Ods` — `OdsCursor` |
| `[Content_Types].xml` at the root | a workbook — `XlsxCursor`, as today |
| a `mimetype` entry naming any other OpenDocument type (text, presentation) | `format.unsupported`, as today — not an archive of XML parts |
| anything else | an archive — `TabularFormat.Zip`, `ArchiveCursor` |

This also reads a workbook renamed to `.zip` correctly.

### Which entries become sheets

By their bytes, not their extension:

- Skipped silently: directories, `__MACOSX/`, hidden files (`.DS_Store` and anything starting with `.`).
- Every other entry is sniffed with the rules `TabularFile.Open` uses: a zip holding an xlsx or ods
  workbook contributes its sheets; text becomes one csv sheet.
- Skipped with a reason: a nested archive, another OpenDocument type, a legacy `.xls` or other OLE2
  file, a binary file, an XML document (`.fods`, Excel 2003 XML), an encrypted entry.
- An archive with no readable entry is `format.unsupported`.

A `readme.txt` becomes a sheet. That is deliberate: the UI shows it, and nobody maps it.

Skipped entries are reported as `FileProfile.SkippedEntries` — path and reason — so a UI can say
"`scan.pdf` skipped: not a table".

### Order and names

- Sheets are ordered by entry path, ordinally — deterministic, whatever order the archiver wrote.
- `Name`: a csv entry's file name without extension; a workbook sheet's own name.
- `Source`: the entry's full path (`export/2026/orders.csv`, `q3.xlsx`).
- Names may repeat across sources; `Source` + `Name` identify a sheet.

### Reading

- **Csv entries** are read as streams straight out of the archive. An entry stream cannot seek, so the
  dialect probe is buffered and re-joined to the front of the stream (an internal change to
  `CsvCursor` / `CsvDialectDetector`). Moving back to a csv sheet reopens its entry — which also
  closes the known issue "`CsvCursor.MoveToSheet` does not rewind" for archives.
- **Workbook entries** (xlsx and ods) are buffered in memory, bounded by `MaxEmbeddedWorkbookBytes`,
  and opened with the existing `XlsxCursor` or `OdsCursor`. At most one workbook is held at a time. To
  list sheets, each workbook is opened once at construction for its metadata and released.
- The buffer is a seekable stream over chunks, not one array: an array stops short of 2 GB, and a
  server with memory to spare may set the budget to many gigabytes. The workbook's own options still
  bound what it expands to.
- `ReadFraction` is compressed bytes consumed against the archive's compressed size.

### Options and bounds

```csharp
public sealed record ArchiveCursorOptions
{
    public CsvCursorOptions Csv { get; init; }
    public XlsxCursorOptions Xlsx { get; init; }
    public OdsCursorOptions Ods { get; init; }
    public int MaxEntries { get; init; }                 // entries in the central directory
    public long MaxUncompressedBytes { get; init; }      // the whole archive, against zip bombs
    public long MaxEmbeddedWorkbookBytes { get; init; }  // default 256 MB; any positive value, beyond 2 GB too
}
```

`MaxSheets` from the xlsx options bounds the total across all sources, ods sheets included. `TabularOpenOptions` gains
`Archive`. Every bound fails as `TabularLimitException`, and every option is checked where it is
handed over, as the others are.

### Tests for part 2

Archives built from raw bytes, like `Fixtures/XlsxPackage.cs`:

- one csv; several csv in different encodings and delimiters; a mixed archive;
- a workbook renamed to `.zip`; an ods workbook inside an archive, and an OpenDocument text file
  inside (skipped) and outside (refused);
- an embedded workbook read through the chunked buffer across a chunk boundary;
- junk entries (`__MACOSX/`, `.DS_Store`, a pdf, a nested zip, an encrypted entry);
- a zip bomb against `MaxUncompressedBytes`, an oversized workbook against `MaxEmbeddedWorkbookBytes`;
- sheet order independent of entry order; repeated sheet names across sources;
- moving back to an earlier csv sheet re-reads it from its first row.

Performance: the 5M-row csv zipped against the same file unpacked — analysis and import time, peak
memory. The zipped read should cost decompression and nothing else.
