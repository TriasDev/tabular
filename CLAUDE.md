# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

`TriasDev.Tabular` is a .NET library (net8.0 + net10.0; benchmarks and samples net10.0 only), published open source and destined for NuGet, with two halves. It
**reads** an Excel (xlsx), OpenDocument (ods) or CSV file — alone or in a zip, tar or gzip archive — profiles every
row of it, and imports it through a column mapping a person confirmed. It **writes** csv, xlsx, ods and a zip of csv
sheets, streaming and asynchronously towards the target, with cell styles and sheet layout, in a form its own import
reads back. It parses and writes every format itself using only the base class library — see
`docs/adr/0001-tabular-parsing-is-our-own-cursor.md` and `docs/adr/0002-writing-is-our-own.md` for why.

The repository is public. It was extracted from an internal product; nothing product-specific
(product names, internal paths, customer data) belongs in code, docs or commit messages.

## Read first

[CONTRIBUTING.md](CONTRIBUTING.md) holds the commands, the build layout, the benchmark variables, the
invariants that are easy to break and the test conventions. They apply here as written.

Above all: every change reaches `main` through a pull request that is **squash-merged** (the only
merge method the repository allows). The squash commit is the pull request's title and description,
so write both by hand for the changelog: a Conventional Commit title, and a description of what
changes and why — the `PR description` check refuses anything less. A breaking change puts `!` in the
title and a `BREAKING CHANGE:` paragraph in the description (see "Commits and pull requests" in
CONTRIBUTING.md).

## Architecture

The read pipeline has two independent reads of the same file with a human in between; the library
holds no state between them. The write side is separate and forward-only:

```
Analyze:  ITabularCursor ─► TabularAnalyzer ─► FileProfile (ColumnFacts + ranked TypeHypothesis)
          (a UI, outside this library, turns the profile into a MappingPlan)
Import:   ITabularCursor ─► TabularExtractor / TabularImporter ─► typed rows or located RowErrors
Write:    TabularWriter / TabularExport<T> / ColumnBatch ─► ISheetWriter ─► SpillBuffer ─► target Stream
          (the round trip through Import is the contract; a value a format cannot hold → TabularWriteException)
```

Every public type is in the `TriasDev.Tabular` namespace, the format options included (`CsvCursorOptions`, `XlsxWriterOptions`, `ZipWriterOptions` and the rest); only internal format code (the cursors, sheet writers, containers) keeps the sub-namespaces `TriasDev.Tabular.Csv`, `.Xlsx`, `.Ods` and `.Archive`. The folders
under `src/TriasDev.Tabular` still group the code by layer:

- **Abstractions** — `ITabularCursor` is the only format-aware seam of reading; everything above is
  written against it. `TabularFile.Open` picks the cursor from the file's bytes, never from its extension: not a
  zip → csv; a zip → xlsx when its directory holds `[Content_Types].xml` or `_rels/.rels`, ods when
  it holds the OpenDocument spreadsheet `mimetype`, otherwise an archive; gzip (`1F 8B 08`) → `GzipCursor`, unless its first decompressed block is a tar
  header; a tar header (magic + checksum), raw or inside gzip → `ArchiveCursor`.
- **Csv / Xlsx / Ods** — the three cursors. The CSV cursor detects its dialect and *repairs* malformed input
  (stray quotes, unclosed quotes), counting each repair in `CursorDiagnostics`. The xlsx cursor reads
  the OOXML package directly (shared strings, styles, 1904 epoch, inline strings). The ods cursor reads
  `content.xml` with the same `SheetScanner` in its local-name mode; cells state their own type, and
  the sheet names come from a byte-level pass (`TableNameScan`) that must count the tables exactly as
  the reading does.
- **Archive** — `ArchiveCursor` reads a zip, a tar or a tar.gz as one workbook, its entries coming from an
  `ArchiveContainer` (`ZipContainer`; `TarContainer`, indexed and read in place; `TarGzContainer`,
  sequential, decompressed again only to move back): every csv, xlsx and ods entry's
  sheets in path order, `Source` = the entry's path, unreadable entries in `SkippedEntries`. A csv
  entry streams with its dialect detected from its head; a workbook entry is copied into a
  `ChunkedBuffer` (random access, past 2 GB) and read by its own cursor. `TabularFile.ClassifyZip`
  decides workbook / ods / other ODF / archive from the zip's directory, for `Detect` and for entries.
  `GzipCursor` reads a gzip file as the file inside it; the gzip framing (header, CRC-32 and size per
  member) is ours over the BCL's `DeflateStream`, because `GZipStream` reads a cut-off file silently.
- **Analysis** — reads every row, not a sample. `ColumnFacts` are measured; `TypeHypothesis` is
  derived and only ever a suggestion. Keep that distinction. A sheet's source facts (`Format`,
  `Source`, `Dialect`, `Diagnostics`) live on `SheetProfile`, not `FileProfile` — an archive holds
  sources of different kinds.
- **Mapping** — `ImportSchema`, `MappingPlan`, `MappingPlanValidator`, field constraints, `ImportPolicy`;
  `FieldAlternatives` (groups in priority order, `AlternativeGroup.AllOf`/`Ladder`, `AlternativeLevel`), resolved
  to positions by `SchemaAlternatives` at every entry point.
- **Extraction** — `TabularExtractor.Extract` → `ExtractionRun`: typed values per row, no entity. A later
  alternative group's errors are deferred and kept only where the row needs that group; the outcome per row is
  `AlternativeResolution`, counted into `AlternativesReport` (`AlternativesTally`). `TabularExtractor.Review` →
  `ImportReview`: the whole file validate-only, past every error limit, with progress.
- **Import** — `TabularImporter` = extraction + the caller's mapper, exposed as `ImportRun<T>`
  (read once, by `ReadRows`, `ReadChunks` or `ReadAll(limit)`). `ImportField` declares fields that are both the schema and
  the accessor. `MappingPrecheck` judges a plan against a `FileProfile` before importing.
- **Writing** — `TabularWriter` (`Create(stream, TabularFormat, options)`, `BeginSheet`, `BeginRow`/`Write`/`EndRow`,
  `FlushAsync` when `FlushRecommended`, `CompleteAsync`) keeps the order of calls, the row and column counts and merges;
  the checks every format shares live in `ValueChecks`, `TextRules`, `SheetNames` and `SheetLimits`. A file is valid only after
  `CompleteAsync`, and a writer that failed is faulted. `ISheetWriter` is the only format-aware seam, as
  `ITabularCursor` is for reading. Format writers write synchronously into a `SpillBuffer` (pooled 64 KB segments);
  only `FlushAsync`/`CompleteAsync` drain it into the target, asynchronously, so an ASP.NET Core response body works
  and memory stays flat. `TabularExport<T>` (built by `TabularExportBuilder<T>`, immutable, thread-safe) declares typed
  columns with style rules and `SheetOptions` and writes items, async items or chunks; `ColumnBatch` takes data by
  column, typed, without boxing. Styles: `CellStyle` (fill, `CellFont`, `NumberFormat`/`DateFormat` — a parsed subset
  of Excel's codes —, alignment, wrap, `CellBorder`) registered per writer as a `StyleId` in a `StyleTable` (at most
  4,096); `SheetOptions` gives the header style, frozen panes and auto-filter; `Merge` merged ranges.
- **Format writers** — `CsvSheetWriter` (culture, delimiter, BOM, formula guard; refuses text the reader would split),
  `XlsxSheetWriter` + `XlsxStyles` (inline strings, no shared string table; styles part written at the end),
  `OdsSheetWriter` + `OdsStyles` (common styles in `styles.xml`), `ZipCsvSheetWriter` (`TabularFormat.Zip`: one
  `<sheet>.csv` entry per sheet). xlsx, ods and the zip go through `ZipWriter`, our own forward-only zip writer
  (stored small parts with sizes up front — the ODF `mimetype` needs that —, deflated large parts with data
  descriptors, zip64 past 4 GB), because `ZipArchive` does not fit: on a non-seekable stream it puts a data
  descriptor on every entry, the stored ODF `mimetype` included, which LibreOffice refuses; on a seekable one it
  seeks back to patch each local header, so a sheet entry cannot be flushed onward until it closes; and it writes
  synchronously.
