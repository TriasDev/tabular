# A gzip-compressed file read through its inner file

Status: agreed design, 2026-10-03. Issue #68, part of the archive-formats epic #76.

## Intent

A caller hands over `data.csv.gz` (or `report.xlsx.gz`, `table.ods.gz`) and gets what they would get
for the file inside it. Today such a file is not recognised: it is not a zip, so `TabularFile.Open`
gives it to the csv cursor, which refuses it as binary.

Why: `.csv.gz` is a common shape for exports and data deliveries, and the base class library decodes
it (`GZipStream`), so it costs no dependency.

gzip is a compression wrapper around **one** file, not an archive. It is read by decompressing and
judging the inner file by its bytes, with the rules `TabularFile.Open` already uses.

Not the goal: a `.gz` entry inside a zip archive (skipped, with a precise reason — no follow-up
planned); tar inside gzip (#69); a zip inside gzip; writing gzip.

## The model: a container, like zip

| | `data.csv.gz` |
|---|---|
| `TabularFile.Detect` | `TabularFormat.Gzip` |
| `cursor.Format` | `TabularFormat.Gzip` |
| `Sheets[i].Format` | the inner file's: `Csv`, `Xlsx` or `Ods` |
| `Sheets[i].Source` | the inner file's name |

The container's format and each sheet's format are kept apart exactly as for zip, so nothing above
the cursor learns what gzip is, and `SheetProfile.Format` stays truthful.

**The inner file's name** (`Source`) is the gzip header's `FNAME` field when present (the `gzip`
tool writes it; .NET's `GZipStream` does not), otherwise the `name` passed to `Open` without a
trailing `.gz` (ordinal, case-insensitive), otherwise `name` itself.

**The csv sheet's name** is that same name (`data.csv`), by the rule that gives a plain csv file's
sheet the `name` passed to `Open`. An inner workbook's sheets keep their own names.

## Detection

The first three bytes `1F 8B 08` (gzip magic, deflate method) mean gzip. `Detect` checks them before
the zip signature. A csv that happens to start with `1F` but not the full signature stays csv.

## What the inner file may be

Judged by the first decompressed bytes (`CsvCursorOptions.DialectProbeBytes` of them):

| Inner file | Behaviour |
|---|---|
| csv | Streamed through `GZipStream`; the dialect is detected from the decompressed head, then the file is decompressed afresh from its start to be read (the dialect stated, so the csv cursor never seeks). Moving back to the sheet decompresses again from the start. |
| xlsx / ods | Copied into a `ChunkedBuffer` under `MaxEmbeddedWorkbookBytes`, judged with `TabularFile.ClassifyZip`, read by its own cursor. |
| any other zip (an archive, another ODF document) | `TabularFormatException` (`Unsupported`): a compressed zip is not read. |
| another gzip | `TabularFormatException` (`Unsupported`). |
| OLE2 (.xls), XML, binary | Refused exactly as on their own, by the same detector. |

## Bounds

No new options. `ArchiveCursorOptions` already holds the two that apply; its documentation changes
from "a zip archive" to "an archive or a compressed file".

- `MaxUncompressedBytes` — counted **while decompressing**, by a bounding stream around
  `GZipStream`; exceeding it throws `TabularLimitException`. gzip's `ISIZE` trailer is the size
  modulo 2³² and sits at the end of the file, so it is neither trusted nor read.
- `MaxEmbeddedWorkbookBytes` — the inner workbook's buffer, as for a workbook inside a zip.

A bound exceeded fails the file; it is never downgraded to anything softer.

## Errors

- A truncated or damaged stream (bad header, bad deflate data, bad CRC) raises
  `InvalidDataException` inside the BCL; it is mapped to `TabularFormatException` (`Corrupt`) on
  every path — opening, moving to a sheet and `ReadRow` — and never leaks.
- An empty file or one shorter than the signature is not gzip and goes the csv way as today.
- A gzip of several members (concatenated, `cat a.gz b.gz`) is read whole; .NET's `GZipStream`
  does that, and a test pins it.

## Progress

`ReadFraction` = position in the compressed stream, from where the file began, over the compressed
length. The stream is seekable (`Open` requires it), so this is observable — more exact than for a
zip entry.

## In ArchiveCursor

A `.gz` entry inside a zip is still skipped, but as `SkippedEntryReason.Compressed` instead of
`Binary`, so a caller can tell the user why. This is the only change to `ArchiveCursor`'s behaviour.

## Code

- `Archive/GzipCursor.cs` — public sealed `ITabularCursor`. Constructor: check options, read the
  header, judge the inner file, open the inner cursor. The rest delegates to the inner cursor, with
  `InvalidDataException` mapped to `Corrupt`. Stream ownership and `LeaveOpen` as for every cursor.
- `Archive/GzipHeader.cs` (internal) — reads the 10-byte header, the flags, `FEXTRA` and `FNAME`
  (Latin-1, zero-terminated, bounded in length); nothing else is needed.
- `Archive/BoundedStream.cs` (internal) — read-only pass-through that throws when more than its
  limit has been read, after the pattern of `CountingStream`.
- **Shared with `ArchiveCursor`**, moved into an internal helper: judging a file by its head
  (zip / OLE2 / XML / csv dialect), copying into a bounded `ChunkedBuffer`, opening a workbook
  cursor on a buffer. tar (#69) and 7z (#75) will use it too. The move must be measured: reading the
  5M-row csv inside a zip may not get slower.
- `TabularFormat.Gzip`; the gzip arm in `TabularFile.Detect` and `Open`;
  `SkippedEntryReason.Compressed`; `PublicAPI.Unshipped.txt`; every `switch` over `TabularFormat`.

## Testing

Fixtures are built in code from raw bytes and `GZipStream`; no binary files in the repository.

- `Archive/GzipCursorTests` — csv, xlsx and ods inside; `Source` with and without `FNAME`; the sheet
  name; moving back to the sheet; `ReadFraction`; several members; the refused inner files (zip,
  gzip, OLE2, XML); an empty inner file.
- `Archive/GzipBoundsTests` — a gzip bomb stopped by `MaxUncompressedBytes`; an inner workbook over
  `MaxEmbeddedWorkbookBytes`.
- Damage — truncated stream, bad CRC, garbage after the header: `TabularFormatException` and never
  a BCL exception, on opening and on reading.
- `Detect` — gzip recognised; a csv starting with `1F` stays csv.
- The cross-cutting suites that enumerate cursors take gzip where they apply: stream ownership,
  cancellation of moves and reads, null arguments, hostile input, API contract, error-code catalog.
- Fuzz — random bytes after a valid gzip header raise only `Tabular*` exceptions.
- Differential — the same csv compressed by the `gzip` tool and by `GZipStream` gives the same
  profile as the uncompressed file.

## Measuring

The 5M-row csv as `.csv.gz` against the plain csv: analysis and import, time and peak memory, each in
its own process. Expected: the decompression time added, peak memory unchanged. And the same csv in a
zip before and after the shared helper moved out of `ArchiveCursor`: unchanged within noise.

## Documentation

`docs/formats.md` gains a gzip section; the format lists in `README.md` and `docs/index.md`; the
Archive paragraph in `CLAUDE.md`; `docs/error-codes.md` if a new case appears.
