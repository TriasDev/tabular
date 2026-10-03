# tar and tar.gz archives read as one workbook

Status: agreed design, 2026-10-03. Issue #69, part 2 of the archive-formats epic #76, after gzip (#68,
merged in #80).

## Intent

A caller hands over `export.tar` or `export.tar.gz` and gets what a zip gives today: one list of
sheets — every csv, xlsx and ods file in the archive — each profiled, mapped and imported on its own.
Today a tar is not recognised (it is not a zip, so it goes the csv way and is refused as binary), and
a tar.gz is refused by `GzipCursor` as a binary inner file.

Why: tar is the usual archive on Linux and in data pipelines, and Windows 11 creates and extracts tar
natively in File Explorer, so tar arrives from Windows users too. Both layers are in the base class
library (`System.Formats.Tar`, our own gzip framing from #68): no dependency.

This part also settles how the library reads an archive **without random access to its entries**
(tar.gz now, 7z's solid blocks in #75): the container abstraction below is built for both.

Not the goal: `.tar.bz2`, `.tar.xz`, `.tar.zst` (no BCL decoders on net8/net10), old v7 tars without
the `ustar` magic, multi-volume archives, a tar inside a zip (stays a `NestedArchive` skip), writing tar.

## The model: the zip model, unchanged

| | `export.tar` | `export.tar.gz` |
|---|---|---|
| `TabularFile.Detect` | `TabularFormat.Tar` | `TabularFormat.Tar` |
| `cursor.Format`, `FileProfile.Format` | `Tar` | `Tar` |
| cursor | `ArchiveCursor` | `ArchiveCursor` |
| `Sheets[i].Format` | the entry's (`Csv`, `Xlsx`, `Ods`) | same |
| `Sheets[i].Source` | the entry's path in the tar | same |

tar and tar.gz are the same archive with and without compression, so both report `Tar`; `Gzip` keeps
meaning "one compressed file". Everything a zip does applies: sheets of every csv, xlsx and ods entry
in the **order of their paths**; a csv named after its file, a workbook's sheets keeping their names;
entries judged by their bytes; `SkippedEntries` for what is not read.

**Public API:** only `TabularFormat.Tar` is new. `ArchiveCursor(stream, options, ct)` judges by bytes
whether it holds a zip, a tar or a tar.gz; `TabularFile.Open` sends tar there.

## Detection

`Detect` reads 512 bytes instead of 128:

1. `PK\3\4` → as today (xlsx / ods / zip archive).
2. gzip signature (`1F 8B 08`) → decompress the first 512 bytes; a tar header there → `Tar`, else
   `Gzip`. A gzip file shorter than 512 decompressed bytes, or damaged in its head, is `Gzip` (the
   gzip path reports the damage as it does today).
3. A tar header in the raw first 512 bytes → `Tar`.
4. Else csv.

**A tar header** is a 512-byte block whose magic at offset 257 is `ustar\0` (POSIX, version `00`) or
`ustar  \0` (GNU) **and** whose checksum (offset 148, octal; the sum of all bytes with the checksum
field read as spaces) matches. The checksum keeps a text file that happens to hold "ustar" at offset
257 a csv file. v7 tars, which have no magic, are not recognised (they go the csv way and are refused
as binary, as today).

## Entries

Read through `System.Formats.Tar.TarReader`, which handles PAX and GNU long paths and validates
header checksums itself.

| Entry | Treatment |
|---|---|
| `RegularFile`, `V7RegularFile`, `ContiguousFile` | a file: judged by its bytes like a zip entry |
| `Directory`, `SymbolicLink`, `HardLink`, `CharacterDevice`, `BlockDevice`, `Fifo`, PAX global attributes, and any other non-file type | left out without a word, like a zip directory |
| `SparseFile` (GNU sparse) | *revised in implementation:* `TarReader` refuses it at the header itself (`NotSupportedException`, .NET 8 and 10), so nothing after it can be reached — the whole tar is refused as `format.unsupported`, with a message naming sparse files |
| hidden paths (a segment starting with `.`, including macOS `._*` AppleDouble files), `__MACOSX/`, and an entry whose name is empty (only a damaged header gives one) | left out, by the rule zip uses |

`Source` is `TarEntry.Name` as written (a leading `./` stays, as a zip's does).

## Reading

### `.tar` — indexed, random access

Opening makes one pass over the headers with `TarReader` on the seekable stream: it skips each
entry's data by seeking (measured: listing a 3 MB tar read 6 KB). The data offset of each entry is
recorded — `TarEntry.DataOffset` on .NET 9+. net8 has no `DataOffset`, and on a seekable stream its
`TarReader` has already sought *past* the entry's data when `GetNextEntry` returns, so there the
offset is `stream.Position` minus the data length rounded up to 512 (measured: equal to
`DataOffset` on .NET 10 for ustar, PAX and GNU entries). One helper under `#if`; it goes when net8 is
dropped at the .NET 11 release.

An entry is opened as a **`StreamWindow`**: a read-only, seekable view `(offset, length)` over the
file, with its own position (it seeks the file before every read, so windows never disturb each
other). A csv entry streams through it; **a workbook entry is read straight through it, without
being copied** — a tar is not compressed, so it has the random access a workbook needs. (A zip's
workbook entry still goes into a `ChunkedBuffer`.)

### `.tar.gz` — sequential

There is no random access: reaching an entry means decompressing everything before it.

- **Opening** decompresses the archive once, entry by entry in file order, judging each file entry
  by its head as it passes — a workbook entry is copied into a `ChunkedBuffer` long enough to list its
  sheets, then released, as a zip's is today. The sheets are then ordered by path.
- **Reading a sheet** decompresses again from the start up to its entry. When the sheet asked for
  lies **further on** in the file than where the current pass stands, the pass simply continues
  forward instead of starting over. Reading the sheets in file order therefore costs one pass;
  the worst case — many files written in an order unlike their paths' — costs one pass per sheet.
- Memory stays flat: nothing but the current workbook entry is held, one at a time.

Measured in the benchmark (see Measuring), including the worst case.

## Integrity

- `TarReader` raises `EndOfStreamException` on a cut and `InvalidDataException` / `FormatException`
  on a damaged header. These map to `TabularFormatException` — `Truncated` and `Corrupt` — on every
  path (opening, moving to a sheet, reading a row); no BCL exception leaks. Any other exception type
  the fuzzer finds `TarReader` throwing on bad input is mapped too, to `Corrupt`.
- **The end of the archive must be marked.** A tar ends with zero blocks; a `.tar` cut exactly at an
  entry boundary would otherwise read as a complete, smaller archive. `TarReader` enforces this
  itself — measured on .NET 8 and 10: a tar whose end blocks are missing raises
  `EndOfStreamException` — but only on a stream it cannot seek. On a seekable `.tar` it takes the end
  of the file for the end of the archive, so `TarContainer` checks for the zero block itself (found in
  implementation). A single zero block is accepted as the end. And a tar.gz pass that reaches the end
  of the tar reads the gzip stream to its end too, or its trailer — the checksum of the whole file —
  would never be checked: `TarReader` stops at the first zero block. (A tar.gz cut anywhere is also caught by the gzip layer from #68.)
- A gzip layer that is damaged raises what it raises today (`Truncated` / `Corrupt` from
  `GzipStreamReader`).

## Bounds

No new options; `ArchiveCursorOptions` applies as for zip:

- `MaxEntries` — every entry the tar holds, of any type.
- `MaxUncompressedBytes` — the sum of the file entries' declared sizes; on a `.tar` checked after the
  header pass, before any entry is read; on a `.tar.gz` checked as the pass goes, failing as soon as
  it is exceeded. The gzip layer additionally counts every decompressed byte of each pass against the
  same bound.
- `MaxEmbeddedWorkbookBytes` — a workbook entry copied into memory (tar.gz, zip); a `.tar` workbook
  read through its window is not copied, but its declared size is still held to the bound, so the
  rule reads the same for every container.

A bound exceeded fails the archive, as for zip; never a skip.

## `GzipCursor`

Constructed directly on a tar.gz, it refuses the file as `Unsupported` with a message that names it
— "a tar archive compressed with gzip; open it with `TabularFile.Open` or `ArchiveCursor`" — instead
of today's binary-file refusal. `TabularFile.Open` never sends a tar.gz there.

## Code

- `Archive/ArchiveContainer.cs` (internal, abstract) — `Format`; `Entries(ct)`: entries in file order
  (`ArchiveEntry`: path, length, kind — file / encrypted / unsupported / left out — ordinal, data
  offset); `Open(entry, ct)`: a forward stream of the entry's data; `OpenSeekable(entry)`: a random
  access stream when it is cheap, else null. An entry yielded by `Entries` can be opened while it is
  current — cheaply, for every container — and opened again later through `Open`.
- `Archive/ZipContainer.cs` — the zip code that lives in `ArchiveCursor` today, moved, unchanged in
  behaviour.
- `Archive/TarContainer.cs` — the `.tar` index and windows.
- `Archive/TarGzContainer.cs` — the sequential pass, forward continuation, restart.
- `Archive/StreamWindow.cs` — the read-only seekable view.
- `Archive/TarHeader.cs` — the header test (magic and checksum) for detection, raw and inside gzip.
- `Archive/TarEntries.cs` — entry kinds, the data offset (with the net8 fallback), and the mapping of `TarReader`'s exceptions.
- `ArchiveCursor` — lists entries in file order, judges each current entry, orders the sources by
  path; opens a workbook through `OpenSeekable` when available, else copies it; chooses its container
  by bytes. Its listing, sniffing, skipping, bounds and sheet logic is shared by all containers.
- `TabularFormat.Tar`; `TabularFile.Detect` (512 bytes, tar inside gzip, raw tar) and `Open`;
  `GzipCursor`'s tar refusal; `PublicAPI.Unshipped.txt`; every `switch` over `TabularFormat`.

The refactor of `ArchiveCursor` must be measured: reading the 5M-row csv in a zip may not get slower.

## Testing

Fixtures are built in code with `System.Formats.Tar.TarWriter` (ustar, PAX and GNU formats) and from
raw bytes where `TarWriter` cannot produce the shape; no binary files in the repository.

- Every entry type of the table above; long paths (PAX and GNU); AppleDouble files; a sparse entry;
  nested archives and `.gz` entries.
- Every cut of a small `.tar` and `.tar.gz`; a tar without its end marker; damaged headers.
- Differential: the same files as `.zip`, `.tar` and `.tar.gz` give identical profiles.
- A tar.gz whose entries are stored in an order unlike their paths: every sheet reads correctly, in
  path order, and moving back and forth between sheets works.
- Bounds: entry count, declared size, workbook size, the gzip layer's count.
- Detection: tar, tar.gz, a csv holding "ustar" at offset 257 with a wrong checksum stays csv, a
  short gzip stays gzip.
- Regression: every existing `ArchiveCursor` test passes unchanged.
- The cross-cutting suites (stream ownership, cancellation of moves and reads, null arguments, API
  contract) and the fuzzer (mangled `.tar` and `.tar.gz`).
- Locally, not in CI: real archives made by macOS `tar` (bsdtar — libarchive, what Windows uses) and,
  where installed, GNU tar.

## Measuring

The 5M-row csv as `.tar` and `.tar.gz` against `.zip`: reading and full analysis, time and peak
memory, each in its own process. The `.zip` before and after the refactor: unchanged within noise.
The worst case: a tar.gz of 20 csv files written in reverse path order, analysed — the cost of
decompressing again per sheet, stated in the PR.

## Documentation

`docs/formats.md` (a tar section, and the zip section generalised where it speaks of "a zip"),
`docs/bounds.md`, `docs/concepts.md` (`Source`), `README.md`, `docs/index.md`, the Archive paragraph
in `CLAUDE.md`.
