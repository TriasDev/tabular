# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

`TriasDev.Tabular` is a .NET library (net8.0 + net10.0; benchmarks and samples net10.0 only), published open source and destined for NuGet, that reads an
Excel (xlsx) or CSV file, profiles every row of it, and imports it through a column mapping a person
confirmed. It parses both formats itself using only the base class library — see
`docs/adr/0001-tabular-parsing-is-our-own-cursor.md` for why.

The repository is public. It was extracted from an internal product; nothing product-specific
(product names, internal paths, customer data) belongs in code, docs or commit messages.

## Commands

```bash
dotnet build TriasDev.Tabular.slnx
dotnet test TriasDev.Tabular.slnx
dotnet test tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~CsvCursorTests"   # one class
dotnet test tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~CsvCursorTests.SomeTest"
dotnet pack TriasDev.Tabular.slnx -c Release   # produces only TriasDev.Tabular.nupkg
```

The benchmark project (`benchmarks/TriasDev.Tabular.Benchmarks`) reads large fixtures that are not in
the repository; set `TABULAR_FIXTURES` to their folder and `TABULAR_FILES` to a comma-separated list
of file names in it — without both it measures nothing. It runs
every measurement in a child process on purpose, so peak working set is not shared between candidates.

`benchmarks/TriasDev.Tabular.Comparison` reads the same fixtures with Sylvan, Sep, CsvHelper,
ExcelDataReader, MiniExcel, the Open XML SDK, ClosedXML, NPOI and EPPlus (same variables, plus
`TABULAR_RUNS`, `TABULAR_READERS`); it is the only project allowed third-party parsing packages, and
only versions free for commercial use are committed (EPPlus 4.5.3.3, NPOI 2.7.6).

There is no separate lint step: Roslynator, Sonar and the .NET analyzers run as part of the build.
`CS8509` (non-exhaustive switch) is an error everywhere, `CS8600` additionally in the library.

## Build layout

`Directory.Build.props` is layered, and nested files import the root one explicitly via
`GetPathOfFileAbove` (MSBuild otherwise stops at the nearest file):

- root — target framework, language settings, analyzers, `IsPackable=false` by default
- `src/` — `IsPackable=true`, XML docs, version, package metadata
- `tests/` — xunit.v3 and test SDK references

A project dropped into `src/` or `tests/` needs no settings of its own.

## Architecture

The pipeline has two independent reads of the same file with a human in between; the library holds
no state between them:

```
Analyze:  ITabularCursor ─► TabularAnalyzer ─► FileProfile (ColumnFacts + ranked TypeHypothesis)
          (a UI, outside this library, turns the profile into a MappingPlan)
Import:   ITabularCursor ─► TabularExtractor / TabularImporter ─► typed rows or located RowErrors
```

Everything a consumer touches is in the `TriasDev.Tabular` namespace; only the format-specific
cursors and their options live in `TriasDev.Tabular.Csv` and `TriasDev.Tabular.Xlsx`. The folders
under `src/TriasDev.Tabular` still group the code by layer:

- **Abstractions** — `ITabularCursor` is the only format-aware seam; everything above is written
  against it. `TabularFile.Open` picks the cursor from the file's first bytes (zip signature → xlsx),
  never from its extension.
- **Csv / Xlsx** — the two cursors. The CSV cursor detects its dialect and *repairs* malformed input
  (stray quotes, unclosed quotes), counting each repair in `CursorDiagnostics`. The xlsx cursor reads
  the OOXML package directly (shared strings, styles, 1904 epoch, inline strings).
- **Analysis** — reads every row, not a sample. `ColumnFacts` are measured; `TypeHypothesis` is
  derived and only ever a suggestion. Keep that distinction. A sheet's source facts (`Format`,
  `Source`, `Dialect`, `Diagnostics`) live on `SheetProfile`, not `FileProfile` — an archive holds
  sources of different kinds.
- **Mapping** — `TargetSchema`, `MappingPlan`, `MappingPlanValidator`, field constraints, `ImportPolicy`.
- **Extraction** — `TabularExtractor.Start` → `ExtractionSession`: typed values per row, no entity.
- **Import** — `TabularImporter` = extraction + the caller's mapper, exposed as `ImportRun<T>`
  (row-at-a-time, `InChunks`, `All(limit)`). `ImportField` declares fields that are both the schema and
  the accessor. `MappingPrecheck` judges a plan against a `FileProfile` before importing.

## Invariants that are easy to break

- **No package references in the library.** `TabularIndependenceTests` checks the *resolved* graph
  (`obj/project.assets.json`) against a list of parsing libraries (Sylvan, ExcelDataReader, CsvHelper,
  OpenXml, …). Analyzers are fine; anything else is a decision for an ADR, not a convenience.
- **Error codes, never messages.** Row errors carry codes like `value.required`. The "Error codes"
  table in `docs/guide.md` is checked against the `ErrorCodes` constants by `ErrorCodeCatalogTests` in
  both directions — adding, renaming or removing a code means updating both.
- **Rows are views.** `CurrentRow`, `CurrentValues` and `ImportRow` (a `ref struct`) point at reused
  buffers; do not introduce per-row allocations on the read path.
- **The precheck must judge a value exactly as extraction would read it** — same reader, same cell
  kind, same culture, trimmed. Past bugs came from the two halves disagreeing.
- **Every collection that grows with the file has a ceiling** (see the "Bounds" table in `docs/guide.md`).
  Hostile-input tests pin these; a new structure read from a file needs its own bound.
- **Cancellation is passed into `ReadRow`**, not only checked between rows, because single reads can
  be expensive on hostile files. Every reading operation takes a token as its last parameter; the
  token given to `Start`/`Import` still applies to the whole run.
- **A stream handed over is closed on every path**, failures included, unless the caller asked for
  `leaveOpen`. New entry points follow the same rule.
- **Options are checked where they are handed over** (`OptionChecks`), never discovered mid-read.
- Every library exception derives from `TabularException` and carries a code: `TabularFormatException`
  (unreadable/unsupported), `TabularLimitException` (a bound), `TabularStructureException` (whole-run),
  `MappingPlanException`. `ArgumentException` is for programmer errors only. Per-row problems are
  `RowError`s, and a row is either values or errors, never both.
- **The invariant culture is `""`** everywhere — profile, hypotheses and plan.

## Tests

Fixtures are built from raw bytes and raw OOXML (`Fixtures/XlsxPackage.cs`, `*GoldenFixtures.cs`)
rather than through a writer, because the cases worth pinning are ones well-behaved writers never
emit. Tests use only the public API (there is no `InternalsVisibleTo`).

`tests/TriasDev.Tabular.Tests/Spike/` holds the parser candidates from the ADR spike. The benchmark
project compiles those files by link, so moving or deleting them breaks the benchmark build.

## Docs to consult

- `README.md` — the short front page: what the library does, a quick start, headline numbers
- `docs/guide.md` — the behavioural contract, error-code table, bounds and the library's own performance numbers
- `docs/benchmarks.md` — the comparison with other csv/xlsx libraries (`benchmarks/TriasDev.Tabular.Comparison`)
- `docs/KNOWN-ISSUES.md` — review findings deliberately left unfixed, with when each would matter
- `docs/IDEAS.md` — extensions the design allows that nobody has asked for yet
- `docs/adr/0001-…` — why the parsing is our own; its measurements are frozen, the guide's and benchmarks' are live

Code comments and XML docs in this repository explain *why*, often at length; match that when
changing behaviour.
