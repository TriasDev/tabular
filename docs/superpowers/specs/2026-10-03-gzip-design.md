# A gzip-compressed file read through its inner file

Status: agreed design, 2026-10-03. Issue #68, part of the archive-formats epic #76.

## Intent

A caller hands over `data.csv.gz` (or `report.xlsx.gz`, `table.ods.gz`) and gets what they would get
for the file inside it. Today such a file is not recognised: it is not a zip, so `TabularFile.Open`
gives it to the csv cursor, which refuses it as binary.

Why: `.csv.gz` is a common shape for exports and data deliveries, and the base class library decodes
its compression (`DeflateStream`; the gzip framing around it is ours, see Errors), so it costs no
dependency.

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
| `Sheets[i].Source` | the inner file's name, when the gzip header stores it |

The container's format and each sheet's format are kept apart exactly as for zip, so nothing above
the cursor learns what gzip is, and `SheetProfile.Format` stays truthful.

**The inner file's name** (`Source`) is the gzip header's `FNAME` field (the `gzip` tool writes it;
.NET's `GZipStream` does not), and null when the header stores none.

*Revised while planning:* the agreed design fell back to the `name` passed to `Open` without its
`.gz`. But a mapping plan records `Source` and an import refuses the file as
`structure.sheet-changed` when it differs — and `name` is whatever the caller passes on each read
(an upload's name at analysis, a storage key at import). A gzip file without `FNAME` would then be
refused by its own plan. `FNAME` is in the file, so it is the same on every read; without it, the
sheet carries no `Source`, exactly like a plain csv, and the plan does not depend on the name.

**The csv sheet's name** is `FNAME` when present, otherwise `name` without a trailing `.gz`
(ordinal, case-insensitive; `name` itself when that would leave nothing): `data.csv`, by the rule
that gives a plain csv file's sheet the `name` passed to `Open`. An inner workbook's sheets keep
their own names.

## Detection

The first three bytes `1F 8B 08` (gzip magic, deflate method) mean gzip. `Detect` checks them before
the zip signature. A csv that happens to start with `1F` but not the full signature stays csv.

## What the inner file may be

Judged by the first decompressed bytes (`CsvCursorOptions.DialectProbeBytes` of them):

| Inner file | Behaviour |
|---|---|
| csv | Streamed through the gzip reader (see Errors); the dialect is detected from the decompressed head, then the file is decompressed afresh from its start to be read (the dialect stated, so the csv cursor never seeks). Moving back to the sheet decompresses again from the start. |
| xlsx / ods | Copied into a `ChunkedBuffer` under `MaxEmbeddedWorkbookBytes`, judged with `TabularFile.ClassifyZip`, read by its own cursor. |
| any other zip (an archive, another ODF document) | `TabularFormatException` (`Unsupported`): a compressed zip is not read. |
| another gzip | `TabularFormatException` (`Unsupported`). |
| OLE2 (.xls), XML, binary | Refused exactly as on their own, by the same detector. |

## Bounds

No new options. `ArchiveCursorOptions` already holds the two that apply; its documentation changes
from "a zip archive" to "an archive or a compressed file".

- `MaxUncompressedBytes` — counted **while decompressing**, by the gzip reader (see Errors), across
  all members; exceeding it throws `TabularLimitException`. gzip's `ISIZE` trailer is the size
  modulo 2³² and sits at the end of the file, so it is neither trusted nor read.
- `MaxEmbeddedWorkbookBytes` — the inner workbook's buffer, as for a workbook inside a zip.

A bound exceeded fails the file; it is never downgraded to anything softer.

## Errors

**`GZipStream` cannot be used as is.** Measured on .NET 8.0.11 and 10.0.9: a gzip file cut off
anywhere in its compressed data decompresses *silently* to a prefix of the content (half the file
read 134 KB of 298 KB and reported success); a file with its trailer cut off, or only its 10-byte
header, reads without complaint too. Only a wrong CRC, a wrong `ISIZE` or broken deflate data throw
(an `InvalidDataException` with the misleading message "unsupported compression method"). A cut-off
upload would be profiled and imported as a smaller, valid file — with its last row half there. Raw
`DeflateStream` behaves the same: it returns 0 at a cut exactly as at the end of the final block.

So the gzip framing is ours, the deflate decoding stays the BCL's:

- **`GzipStreamReader`** (internal) reads member after member: the header (ours, `GzipHeader`), the
  deflate data through a raw `DeflateStream`, then the 8-byte trailer, checked against the CRC-32
  and the byte count of what was decompressed.
- A `DeflateStream` does not say where its data ended, and it reads past that end: measured, the
  `Read` call that finally returns 0 goes on reading past the end of the data — measured on a
  single member, to the end of the file. Every call that returns data stops reading as soon as it has some. So
  the end of the deflate data lies in the **last read made before the call that returned 0**, or in
  the **first read made during it** (when the end-of-block code stood alone at the start of a new
  read). The reader feeds the `DeflateStream` through a pass-through that hands out at most 64 KB a
  read and remembers both; the trailer is the first position in that window whose 8 bytes are the
  expected CRC-32 and `ISIZE`, and never earlier than 2 bytes into the member's deflate data (the
  shortest deflate stream; without this rule an empty member's trailer — eight zero bytes — is found
  inside its own `03 00`). The base stream is seekable, so the reader seeks to just past the trailer
  and continues.
- Prototyped before the plan, on .NET 8.0.11 and 10.0.9: 400 random files of one to three members,
  with and without trailing bytes, read in pieces of 1 byte to 1 MB — every one read exactly, and
  none of 2,400 random cuts read silently.
- **No matching trailer, and the data ran to the end of the file** → `TabularFormatException`
  (`Truncated`): the file was cut off. (A wrong CRC in a trailer that is the file's last 8 bytes
  reads the same way — the two cannot be told apart, and both are refused.)
  **No matching trailer, and the deflate data ended before the file did** → `Corrupt`.
  **Broken deflate data** (`InvalidDataException`) → `Corrupt`, with our own message.
- After a member: bytes starting `1F` are the next member (several members — `cat a.gz b.gz`,
  bgzip's BGZF blocks — are read as one file), and a member cut off inside its header is
  `Truncated`; judging by `1F 8B` instead would read a file cut one byte into its next member as a
  complete, shorter one. Anything else is trailing garbage and ignored, as the `gzip` tool and
  `GZipStream` both do. A file that is only a header is `Truncated`.
- **CRC-32** is ours too (the BCL has no public one; `System.IO.Hashing` is a package): slicing-by-8
  in managed code, with the ARM64 `Crc32` intrinsic where the CPU has it. Its cost is measured on
  the 5M-row csv; writing (#71) will reuse it.
- Every one of these is raised on every path — opening, moving to a sheet and `ReadRow` — and no BCL
  exception leaks.
- An empty file or one shorter than the signature is not gzip and goes the csv way as today.

## Progress

`ReadFraction` = position in the compressed stream, from where the file began, over the compressed
length. The stream is seekable (`Open` requires it), so this is observable — more exact than for a
zip entry.

## In ArchiveCursor

A `.gz` entry inside a zip is still skipped, but as `SkippedEntryReason.Compressed` instead of
`Binary`, so a caller can tell the user why. This is the only change to `ArchiveCursor`'s behaviour.

## Code

- `Archive/GzipCursor.cs` — public sealed `ITabularCursor`. Constructor: check options, read the
  header, judge the inner file, open the inner cursor. The rest delegates to the inner cursor. Stream
  ownership and `LeaveOpen` as for every cursor.
- `Archive/GzipHeader.cs` (internal) — parses a member header: the 10 fixed bytes, the flags,
  `FEXTRA`, `FNAME` (Latin-1, zero-terminated, bounded in length), `FCOMMENT`, `FHCRC`; returns the
  name and the header's length.
- `Archive/GzipStreamReader.cs` (internal) — the read-only stream over the decompressed content:
  members, trailer check, decompressed-byte bound; raises only `Tabular*` exceptions.
- `Archive/Crc32.cs` (internal) — CRC-32 (IEEE), incremental.
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
- Damage — cut at every position of a small file, trailer cut, header only, bad CRC, bad `ISIZE`,
  broken deflate data, garbage after the header: `Truncated` or `Corrupt` and never a BCL exception
  or a silently shorter read, on opening and on reading. Trailing garbage after a complete file is
  ignored.
- `Crc32` — known vectors, incremental equals one-shot, the intrinsic and managed paths agree.
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
