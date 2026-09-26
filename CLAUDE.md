# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

`TriasDev.Tabular` is a .NET library (net8.0 + net10.0; benchmarks and samples net10.0 only), published open source and destined for NuGet, that reads an
Excel (xlsx), OpenDocument (ods) or CSV file, profiles every row of it, and imports it through a column mapping a person
confirmed. It parses both formats itself using only the base class library — see
`docs/adr/0001-tabular-parsing-is-our-own-cursor.md` for why.

The repository is public. It was extracted from an internal product; nothing product-specific
(product names, internal paths, customer data) belongs in code, docs or commit messages.

## Read first

[CONTRIBUTING.md](CONTRIBUTING.md) holds the commands, the build layout, the benchmark variables, the
invariants that are easy to break and the test conventions. They apply here as written.

## Architecture

The pipeline has two independent reads of the same file with a human in between; the library holds
no state between them:

```
Analyze:  ITabularCursor ─► TabularAnalyzer ─► FileProfile (ColumnFacts + ranked TypeHypothesis)
          (a UI, outside this library, turns the profile into a MappingPlan)
Import:   ITabularCursor ─► TabularExtractor / TabularImporter ─► typed rows or located RowErrors
```

Everything a consumer touches is in the `TriasDev.Tabular` namespace; only the format-specific
cursors and their options live in `TriasDev.Tabular.Csv`, `TriasDev.Tabular.Xlsx`, `TriasDev.Tabular.Ods` and `TriasDev.Tabular.Archive`. The folders
under `src/TriasDev.Tabular` still group the code by layer:

- **Abstractions** — `ITabularCursor` is the only format-aware seam; everything above is written
  against it. `TabularFile.Open` picks the cursor from the file's bytes, never from its extension: not a
  zip → csv; a zip → xlsx when its directory holds `[Content_Types].xml` or `_rels/.rels`, ods when
  it holds the OpenDocument spreadsheet `mimetype`, otherwise an archive.
- **Csv / Xlsx / Ods** — the three cursors. The CSV cursor detects its dialect and *repairs* malformed input
  (stray quotes, unclosed quotes), counting each repair in `CursorDiagnostics`. The xlsx cursor reads
  the OOXML package directly (shared strings, styles, 1904 epoch, inline strings). The ods cursor reads
  `content.xml` with the same `SheetScanner` in its local-name mode; cells state their own type, and
  the sheet names come from a byte-level pass (`TableNameScan`) that must count the tables exactly as
  the reading does.
- **Archive** — `ArchiveCursor` reads a zip of files as one workbook: every csv, xlsx and ods entry's
  sheets in path order, `Source` = the entry's path, unreadable entries in `SkippedEntries`. A csv
  entry streams with its dialect detected from its head; a workbook entry is copied into a
  `ChunkedBuffer` (random access, past 2 GB) and read by its own cursor. `TabularFile.ClassifyZip`
  decides workbook / ods / other ODF / archive from the zip's directory, for `Detect` and for entries.
- **Analysis** — reads every row, not a sample. `ColumnFacts` are measured; `TypeHypothesis` is
  derived and only ever a suggestion. Keep that distinction. A sheet's source facts (`Format`,
  `Source`, `Dialect`, `Diagnostics`) live on `SheetProfile`, not `FileProfile` — an archive holds
  sources of different kinds.
- **Mapping** — `TargetSchema`, `MappingPlan`, `MappingPlanValidator`, field constraints, `ImportPolicy`.
- **Extraction** — `TabularExtractor.Start` → `ExtractionSession`: typed values per row, no entity.
- **Import** — `TabularImporter` = extraction + the caller's mapper, exposed as `ImportRun<T>`
  (row-at-a-time, `InChunks`, `All(limit)`). `ImportField` declares fields that are both the schema and
  the accessor. `MappingPrecheck` judges a plan against a `FileProfile` before importing.
