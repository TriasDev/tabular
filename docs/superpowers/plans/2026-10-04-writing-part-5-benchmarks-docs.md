# Writing, part 5 — benchmarks, comparison and documentation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Measure the write side against the libraries a .NET developer would otherwise use, settle the open performance items with numbers, and document writing — a guide page, README, formats and bounds, benchmarks, and ADR-0002.

**Architecture:** A write comparison harness beside the existing read comparison (`benchmarks/TriasDev.Tabular.Comparison`): the same rules — every measurement in a fresh process, median of three, Markdown out — with one `IWriter` per library writing the same deterministic dataset into a file in the library's own fastest idiom. Two library optimisations parked by earlier reviews go in first, each with before/after numbers. Documentation is written from the measured results and the shipped API.

**Tech Stack:** .NET 10 for benchmarks (net8.0 + net10.0 for the library), BenchmarkDotNet-free harness (as the read comparison), mkdocs-material for the docs site.

**Spec:** `docs/superpowers/specs/2026-10-04-styled-columnar-export-design.md` §5 and `docs/superpowers/specs/2026-10-03-writing-design.md` (its part 5). Issue #74 (scope extended in its comment).

## Global Constraints

- The library keeps zero dependencies; third-party packages go only into the benchmark project.
- Third-party versions: the latest that are free to use commercially (as the read comparison states for EPPlus/NPOI). Check each package's licence on nuget.org before adding it and state version + licence in the docs.
- Every library writes the same data and is checked to have written it: the harness re-reads each output with `TabularFile.Open` and compares the row count and a fixed sample of cells against the dataset. An output that does not read back is reported as failed, not timed.
- Each library writes in the idiom its documentation recommends for speed (streaming writers, no object model where the library has a streaming one). State the idiom in the docs.
- Measurements: wall clock, peak RSS (the existing `benchmarks/Shared/PeakMemory.cs`), allocated bytes, output size; fresh process per measurement; median of `TABULAR_RUNS` (default 3); Release, net10.
- Acceptance the user set: TriasDev.Tabular writes not slower than LargeXlsx (xlsx) and CsvHelper (csv) on the same data and styling. If a scenario misses it, report the gap and its cause with a profile; do not tune the benchmark to hide it.
- Docs: no product or customer names; English; the existing docs' tone (short, factual, numbers measured, limits stated).
- Analyzers warnings-as-errors and `dotnet format --verify-no-changes` apply to the benchmark projects too.

Commands: build `dotnet build -c Release`; tests `TABULAR_REQUIRE_SOFFICE=1 dotnet test -c Release`; format `dotnet format --verify-no-changes`; docs `mkdocs build --strict` (requirements.txt). Never run LibreOffice's bundled Python.

## Review Focus

1. A library whose output does not read back (wrong row count, a value changed) is reported as failed, never timed. (Task 3)
2. The datasets are identical across libraries — same values, same types, same styled cells. (Task 2, Task 3)
3. The documentation states only measured numbers, with the machine, runtime and versions. (Task 4)
4. The guide's code compiles: every snippet in `docs/exporting.md` is a compiled sample or copied from a test. (Task 5)
5. Parked optimisations change no output byte. (Task 1)

---

### Task 1: Two parked write-path optimisations, measured

**Files:** `src/TriasDev.Tabular/Writing/RowText.cs`, `src/TriasDev.Tabular/Xlsx/XlsxSheetWriter.cs`, `src/TriasDev.Tabular/Ods/OdsSheetWriter.cs`; tests as needed.

1. `RowText.WriteUtf8To`: ask `output.GetSpan(Math.Min(EncodePiece, (rest.Length + 1) * 3))` instead of a fixed 8 KB (verified by the part 4 review: a fixed hint makes `SpillBuffer` open a new 64 KB segment whenever less than 8 KB is free — ~14% more segments and drain writes on the csv path).
2. Buffered emit for xlsx and ods: `Emit` writes each row's bytes into the sheet's deflate stream at once (one zlib call per row). Collect rows in a reused 64 KB byte buffer and write it to the deflate stream when the next row would not fit and when the sheet/content entry closes (an earlier measurement estimated +7–9% for xlsx). Keep the retained-cap behaviour for a huge row. Output must stay byte-identical — the deflate stream sees the same bytes in larger writes; verify with the existing `IsReproducibleByteForByte`-style tests plus a new one comparing a 10,000-row xlsx and ods before/after the change (bytes of the decompressed parts, since deflate block boundaries may differ — state which you compared).
3. Measure each change before/after in a scratchpad program (not committed): 1M rows × 10 mixed columns into `Stream.Null` for csv (change 1) and xlsx/ods (change 2), plus the 10,000 × 5,003 styled batch for xlsx/ods; best of 5, Release, net10. Keep a change only if it is not slower; report the numbers.
4. Full suite green, format clean. Commit: `perf(write): …` with the numbers in the message body.

---

### Task 2: Write comparison harness — datasets and TriasDev.Tabular

**Files:** Create `benchmarks/TriasDev.Tabular.WriteComparison/` (csproj net10.0, `Program.cs`, `IWriter.cs`, `Datasets.cs`, `Writers/TabularWriters.cs`), add it to `TriasDev.Tabular.slnx`.

- Mirror `benchmarks/TriasDev.Tabular.Comparison/Program.cs`: a driver that, for each (scenario, writer), starts `dotnet <self> measure <scenario> <writer> <path>` `TABULAR_RUNS` times, reads one result line (`ms`, `peakRss`, `allocated`, `bytes`, `rows`), and prints Markdown tables per scenario sorted by time, with versions from each writer's `Anchor` type. Environment: `TABULAR_RUNS`, `TABULAR_WRITERS` (filter), `TABULAR_SCENARIOS` (filter), `TABULAR_OUT` (folder for the files, default a temp folder, deleted after each measurement), `TABULAR_TIMEOUT`.
- `IWriter`: `string Name`, `Type Anchor`, `FileKind Kind` (Csv, Xlsx, Ods, Zip), `bool Styled` (can it apply the styled scenario's styles), `void Write(Scenario scenario, Stream target)`.
- `Datasets.cs`: deterministic data, generated per row from the row index (no stored arrays beyond a chunk), identical for every writer:
  - **Narrow** (30 columns): `Id` long; `Name`, `Street`, `City`, `Country`, `Code` text (with commas and quotes in some rows, so quoting is exercised); `Value1`–`Value10` double (15 significant digits at most, ~10% empty); `Amount1`–`Amount5` decimal with 2 places; `Start`, `End` DateOnly, `Created`, `Updated` DateTime with time; `Active1`–`Active4` bool.
  - **Wide**: `Id`, `Name`, `Start` + 5,000 double columns (~10% empty), values as `WideExportTests` generates them.
  - Styles for the styled scenarios: header bold with a fill; on doubles a legend fill by value (three colours); dates formatted `dd/mm/yyyy`; decimals `#,##0.00`. Freeze the header row.
- Scenarios: `csv-5m` (Narrow, 5,000,000 rows), `zip-5m` (Narrow, 5,000,000, Tabular only), `xlsx-1m` (Narrow 1,048,575 rows, unstyled), `xlsx-1m-styled`, `ods-1m` and `ods-1m-styled` (Tabular only), `wide-xlsx` (Wide 10,000 rows, styled), `wide-csv` (Wide 10,000 rows), `wide-ods` (Tabular only).
- `TabularWriters.cs`: TriasDev.Tabular through `ColumnBatch` in 10,000-row chunks for the wide scenarios and through `TabularWriter.Write` rows (or `TabularExport<T>` — pick the faster, state which) for narrow ones; async writes completed with `GetAwaiter().GetResult()` at the harness boundary.
- Verification step in `measure`, after the timed part (not timed): re-read the file with `TabularFile.Open`, check row count and the cells of rows 1, 2, the middle and the last against the dataset (text compare for csv, typed for workbooks); a mismatch prints `FAILED …`.
- Run all Tabular scenarios once with `TABULAR_RUNS=1` and put the output in the report.

---

### Task 3: Third-party writers

**Files:** `benchmarks/TriasDev.Tabular.WriteComparison/Writers/CsvWriters.cs`, `Writers/XlsxWriters.cs`, csproj package references.

- csv: **CsvHelper** (`CsvWriter` writing fields, `InvariantCulture`, not record mapping), **Sep** (`Sep.Writer()` with its row/column API), **Sylvan.Data.Csv** (`CsvDataWriter` over a `DbDataReader` that the harness provides from the dataset — only if that adapter stays simple; otherwise leave Sylvan out and say why).
- xlsx: **LargeXlsx** (streaming `XlsxWriter`, styles via `XlsxStyle`), **SpreadCheetah** (`Spreadsheet.CreateNewAsync`, `AddRowAsync` with `Cell`/`DataCell` and `StyleId`), **MiniExcel** (unstyled scenarios only, `SaveAs` over an `IEnumerable` of rows — its recommended streaming form).
- Each writer applies the styled scenario's styles in its own API (header bold + fill, legend fill on doubles, date and number formats, freeze); a writer that cannot do one of them writes the rest and says what it skipped in its `Name` note.
- Verified by the harness's read-back (Review Focus 1). Run every scenario with `TABULAR_RUNS=1` once; fix writer idioms that are clearly not the fast path before Task 4's real runs.

---

### Task 4: Measure, decide, document the numbers

- Real runs: `TABULAR_RUNS=3` for every scenario on the development machine (state CPU, RAM, OS, .NET version).
- **Compression default**: run the xlsx, ods and zip scenarios with `CompressionLevel.Fastest` and `Optimal` (Tabular only); record time and size; keep `Fastest` unless `Optimal` costs < 15% time for a > 20% smaller file — then change the default in `XlsxWriterOptions`/`OdsWriterOptions`/`ZipWriterOptions` with a test and a CHANGELOG note.
- **> 4 GB zip entry**: write a zip of one csv sheet whose entry exceeds 4 GB uncompressed (Narrow with enough rows), then: `unzip -t` succeeds, `System.IO.Compression.ZipArchive` reads the entry to the end, and `TabularFile.Open` reads the row count back. Report sizes and times. (Not committed as a test.)
- `docs/benchmarks.md`: add a **Writing** section after the reading one: what was measured (same data, idioms, verification by re-reading), a table per scenario (time, peak memory, allocated, file size), the compression decision, the > 4 GB check, and a short "In short" paragraph at the top of the section that states plainly where TriasDev.Tabular is faster and where it is not.

---

### Task 5: The writing guide, README, formats, bounds

- `docs/exporting.md` (nav: under "Using it", after Importing): writing a file row by row (`TabularWriter`, `FlushRecommended`/`FlushAsync`, `CompleteAsync`, what an incomplete file looks like); an export declared once (`TabularExport<T>`, chunks, a server stream with `itemsOf`); data by column (`ColumnBatch`); styles (`CellStyle`, `StyleId`, format codes, rules); sheet layout (`SheetOptions`, `Merge`); a zip of csv sheets; into an ASP.NET Core response (async, abort on failure) and into a blob stream; failures (`TabularWriteException`, `TabularLimitException`, faulted writer); what does not come back exactly as written (link KNOWN-ISSUES). Every snippet compiles: put them in `samples/` (a new `samples/Exporting` project, net10, built by CI if the samples are) or copy them verbatim from tests — Review Focus 4.
- `README.md`: a short "Writing" section with one example (an export to xlsx with a styled header and a legend rule), and the Limits list consistent with the write side.
- `docs/formats.md` and `docs/bounds.md`: what each format holds when written (row limits, 16,384 columns, 4,096 styles, 65,536 merges for xlsx, text limits, dates) — consistent with the code's constants.
- `mkdocs build --strict` passes.

---

### Task 6: ADR-0002 — writing is our own too

- `docs/adr/0002-writing-is-our-own.md` in the format of ADR-0001: context (export symmetric with import, streaming into non-seekable async targets, flat memory, no dependencies), decision (own format writers over a spill buffer; own zip writer — `ZipArchive` writes synchronously, buffers whole entries when seekable, and LibreOffice refuses its streamed stored entries; inline strings; styles resolved lazily and written at the end), consequences (what we maintain; measured results from Task 4; limits), alternatives considered (LargeXlsx/SpreadCheetah as dependencies; Open XML SDK; ZipArchive).
- Add it to the mkdocs nav beside ADR-0001.
