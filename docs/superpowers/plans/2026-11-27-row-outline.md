# Row grouping (outline levels) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rows written at an outline level (0–7) in the xlsx and ods writers, summary rows above or below, a sheet that can open collapsed to a level — csv ignores it, files without an outline unchanged byte for byte.

**Architecture:** `SheetOptions.Outline` (a new `RowOutline` record) declares the outline when the sheet begins; `TabularWriter.BeginRow(int)` checks each row's level and passes it through `ISheetWriter.BeginRow(int level)`. The xlsx writer writes the outline into the sheet head and row attributes, holding one row back only for summary-above + collapsed; the ods writer opens and closes nested `table:table-row-group` elements as the level changes.

**Tech Stack:** C# / .NET 10 (tests also net11.0), xUnit v3 on Microsoft.Testing.Platform, no third-party runtime dependencies.

**Spec:** `docs/superpowers/specs/2026-11-27-row-outline-design.md` — read it first; this plan does not repeat its reasoning.

## Global Constraints

- Public types in namespace `TriasDev.Tabular`; every public member in `src/TriasDev.Tabular/PublicAPI.Unshipped.txt`.
- `Levels` 1 to 7; `ShownLevels` 1 to `Levels` or null; `SummaryRows` default `Above`.
- New error code exactly `write.outline-level`, constant `ErrorCodes.Write.OutlineLevel`.
- `Outline = null` and `BeginRow()` / `BeginRow(0)` produce today's bytes in csv, xlsx, ods and zip.
- No buffering beyond one row; on a sheet without an outline, at most one branch added to the row path.
- xlsx head order: `sheetPr`, `sheetViews`, `sheetFormatPr`, `cols`, `sheetData`; `<sheetFormatPr defaultRowHeight="15" outlineLevelRow="{Levels}"/>`.
- Nothing product-specific (consumer names, internal paths) in code, docs or commit messages; the repo is public.
- Pull request title: `feat: group rows into outline levels in the xlsx and ods writers`, squash-merged, description written by hand (CONTRIBUTING.md, "Commits and pull requests").

## Review Focus

- An outline together with `FreezeRows`, column widths, `AutoFilter` and merges on one sheet: the head keeps the schema order, and a held row is written before `</sheetData>`, `<autoFilter>` and `<mergeCells>`. → Task 5, `HeldRowPrecedesFilterAndMerges`; Task 4, `HeadKeepsSchemaOrderWithFreezeAndWidths`.
- Several sheets: an outlined sheet followed by one without an outline (and the reverse) — groups closed, held row emitted, state reset; the plain sheet's part is byte-equal to the same sheet written alone. → Task 4 `NextSheetStartsWithoutOutlineState`, Task 6 `GroupsCloseAtNextSheetAndAtCompletion`.
- An outlined sheet with only the header, or only level-0 rows: a valid file, no empty `table-row-group`, no `outlineLevel` attribute. → Task 4 and Task 6, `OutlineWithoutDeeperRowsWritesNoGroups`.
- `WriteBatch` rows (level 0) after rows at level 2: ods closes both groups; xlsx gives the held row its `collapsed` from the batch's first row. → Task 5 `BatchRowEndsGroups`, Task 6 `BatchRowEndsGroups`.
- A group that runs to the last row of the sheet, for above and below, and a jump 0 → 2 followed by a fall 2 → 0. → Task 4 `TrailingGroupBelow`, Task 5 `LastHeldRowIsWritten`, Task 6 `JumpAndFallNestCorrectly`.

---

### Task 1: LibreOffice confirms the ods collapse markup (gate, by hand)

**Files:** none in the repo. Hand-made files, written by a throwaway script outside the repo:
`shown-1.ods`, `shown-2.ods`, `expanded.ods` — rows `Name`(0), `F1`(0), `D1a`(1), `P1`(2), `D1b`(1), `F2`(0), `X (0->2)`(2), `D2b`(1), `F3`(0); a group opened for level L ≥ `ShownLevels` carries `table:display="false"`, a row at level ≥ `ShownLevels` carries `table:visibility="collapse"`.

- [ ] **Step 1: The owner opens the three files in LibreOffice.**
  Expected: `shown-1.ods` shows only `Name`, `F1`, `F2`, `F3`, each group with "+", opening on the first click; `shown-2.ods` shows level 0 and 1 rows, `P1` and `X` hidden behind "+"; `expanded.ods` shows every row with "−".
- [ ] **Step 2: Record the result** in the spec's "What the formats do (verified)" section (one sentence, dated). If a collapsed group shows "−" or needs two clicks, stop: the ods section of the spec is revised before Task 6.

### Task 2: Pin today's bytes

**Files:**
- Create: `tests/TriasDev.Tabular.Tests/Writing/OutlineUnchangedBytesTests.cs`

**Interfaces:**
- Produces: `OutlineUnchangedBytesTests.Write(TabularFormat, Action<TabularWriter>?)` — the reference sheet, reused by Task 3.

- [ ] **Step 1: Write the test on `main`'s code, before any change.** One sheet `"data"` with five columns (one with a width), a `HeaderStyle`, `FreezeRows = 1`, `FreezeColumns = 1`, `AutoFilter = true`, a style on one cell, a 2×2 merge, 20 rows of text, long, decimal, date, bool; a second sheet `"second"` with one row. `[Theory]` over `Csv`, `Xlsx`, `Ods`, `Zip`: `Assert.Equal(Expected[format], Convert.ToHexString(SHA256.HashData(bytes)))`.
- [ ] **Step 2: Run it, copy the four hashes it reports into `Expected`, run again.**
  Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~OutlineUnchangedBytesTests"`
  Expected: PASS for all four formats, on net10.0 and net11.0.
- [ ] **Step 3: Commit** — `test: pin the bytes the writers write today`.

### Task 3: Outline options, the level on a row, and its refusals

**Files:**
- Create: `src/TriasDev.Tabular/Writing/RowOutline.cs` (`RowOutline`, `SummaryRows`)
- Modify: `src/TriasDev.Tabular/Writing/SheetOptions.cs` (property `Outline`, method `OutlineProblem`)
- Modify: `src/TriasDev.Tabular/Writing/TabularWriter.cs` (`BeginSheet` check at `:246`, `BeginRow(int)`, `StartRow` at `:948`)
- Modify: `src/TriasDev.Tabular/Writing/ISheetWriter.cs`, `Xlsx/XlsxSheetWriter.cs:119`, `Ods/OdsSheetWriter.cs:105`, `Csv/CsvSheetWriter.cs:77`, `Archive/ZipCsvSheetWriter.cs:88` (`BeginRow(int level)`; all four ignore the level in this task)
- Modify: `src/TriasDev.Tabular/Writing/TabularExportBuilder.cs:135` (refuse `Outline`)
- Modify: `src/TriasDev.Tabular/Abstractions/ErrorCodes.cs` (`Write.OutlineLevel`), `docs/error-codes.md:28` row, `src/TriasDev.Tabular/PublicAPI.Unshipped.txt`
- Test: `tests/TriasDev.Tabular.Tests/Writing/RowOutlineTests.cs`, `tests/TriasDev.Tabular.Tests/Writing/TabularExportSheetOptionsTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record RowOutline { public required int Levels { get; init; } public SummaryRows SummaryRows { get; init; } public int? ShownLevels { get; init; } }`
  - `public enum SummaryRows { Above = 0, Below = 1 }`
  - `public RowOutline? SheetOptions.Outline { get; init; }`
  - `internal ArgumentOutOfRangeException? SheetOptions.OutlineProblem(string paramName)`
  - `public void TabularWriter.BeginRow(int outlineLevel)`; `BeginRow()` calls the same path with 0
  - `void ISheetWriter.BeginRow(int level)` — replaces `BeginRow()`; `options` passed to `ISheetWriter.BeginSheet` carries `Outline`
  - `ErrorCodes.Write.OutlineLevel = "write.outline-level"`

- [ ] **Step 1: Write the failing tests** in `RowOutlineTests`:
  - `RefusesLevelsOutsideOneToSeven` — `[Theory]` 0, 8, −1: `BeginSheet(…, new SheetOptions { Outline = new RowOutline { Levels = n } })` throws `ArgumentOutOfRangeException` with `ParamName == "options"`.
  - `RefusesShownLevelsOutsideOneToLevels` — `Levels = 2`, `ShownLevels` 0 and 3 → `ArgumentOutOfRangeException`; 1 and 2 accepted.
  - `RefusesAnUndefinedSummaryRows` — `(SummaryRows)7` → `ArgumentOutOfRangeException`.
  - `RefusesALevelOutsideTheOutline` — `[Theory]` over all four formats × (level −1, `Levels` + 1 with `Levels = 2`, level 1 with no `Outline`): `BeginRow(level)` after one written row throws `TabularWriteException` with `Code == "write.outline-level"`, `SheetName == "data"`, `RowNumber == 3` (header 1, one row 2, refused 3), `ColumnIndex == 0`, `Header` = first header; the next call throws `InvalidOperationException` (faulted).
  - `RowLimitIsJudgedBeforeTheLevel` — xlsx, `writer.MaxRows = 3` (as `RowNumberLimitTests.TheRowPastTheLimitIsAWriteErrorNotAReaderBound` does), `Levels = 1`: after two rows, `BeginRow(5)` throws `write.too-many-rows`, not `write.outline-level`.
  - `AcceptsEveryLevelUpToLevels` — `Levels = 7`, rows at 0..7 and back to 0 in every format; `CompleteAsync` succeeds.
  - `CsvIgnoresTheOutline` — `[Theory]` `Csv`, `Zip`: rows at levels 0,1,2,1,0 with an `Outline` give the same bytes as the same rows with `BeginRow()` and no `Outline`.
  - In `TabularExportSheetOptionsTests`: `BuildRefusesAnOutline` — `.Sheet(new SheetOptions { Outline = new RowOutline { Levels = 1 } }).Build()` throws `ArgumentException`.
- [ ] **Step 2: Run, verify they fail** (compile errors: `RowOutline` not defined).
  Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~RowOutlineTests|FullyQualifiedName~TabularExportSheetOptionsTests"`
- [ ] **Step 3: Implement.** `OutlineProblem` returns null for `Outline == null`. `BeginSheet` calls it right after `FreezeProblem` and keeps the declared `Levels` (0 when no outline) in a field. `StartRow(int level)`: the row-limit check first (unchanged), then `level < 0 || level > _outlineLevels` → `throw Faulting(OutlineLevel(level))` located at `_rowNumber + 1`, then `_rowNumber++` and `_sheet.BeginRow(level)`. The header row passes 0. Messages: with an outline — `Row {row} of sheet "{name}" is at outline level {level}; the sheet's outline holds levels 0 to {Levels}.`; without — `…; the sheet has no outline (SheetOptions.Outline), so its rows are at level 0.` Add the code to the `write.*` row of `docs/error-codes.md` with its location ("located at the row that would have been written, column 0") so `ErrorCodeCatalogTests` passes. Add every new public member to `PublicAPI.Unshipped.txt`. Doc comments as in the spec's API block.
- [ ] **Step 4: Run the new tests, `OutlineUnchangedBytesTests` and `ErrorCodeCatalogTests`.** Expected: PASS.
- [ ] **Step 5: Commit** — `feat: declare a sheet's row outline and begin a row at a level`.

### Task 4: xlsx — outline head, row levels, hidden rows, collapsed with summary below

**Files:**
- Modify: `src/TriasDev.Tabular/Xlsx/XlsxSheetWriter.cs` (`BeginSheet` `:93`, `BeginRow` `:119`, `EndRow` `:227`, `CloseSheet` `:496`)
- Test: `tests/TriasDev.Tabular.Tests/Writing/XlsxOutlineTests.cs`

**Interfaces:**
- Consumes: `ISheetWriter.BeginRow(int level)`, `SheetOptions.Outline` (Task 3).

- [ ] **Step 1: Write the failing tests.** Read `xl/worksheets/sheet1.xml` with `SheetLayoutTests.Write` / `SheetLayoutTests.Entry`; rows of levels 0,1,2,1,0 unless stated.
  - `HeadKeepsSchemaOrderWithFreezeAndWidths` — `Levels = 2`, `FreezeRows = 1`, a width: the indexes of `<sheetPr><outlinePr summaryBelow="0"/></sheetPr>`, `<sheetViews>`, `<sheetFormatPr defaultRowHeight="15" outlineLevelRow="2"/>`, `<cols>`, `<sheetData>` are increasing.
  - `SummaryBelowWritesNoSheetPr` — `SummaryRows.Below`: no `<sheetPr`, `<sheetFormatPr … outlineLevelRow="2"/>` present.
  - `RowsCarryTheirLevel` — row 3 has `outlineLevel="1"`, row 4 `outlineLevel="2"`, rows 1, 2, 6 have no `outlineLevel`; no `hidden` or `collapsed` anywhere without `ShownLevels`.
  - `ShownLevelsHidesDeeperRows` — `ShownLevels = 1`, `Below`: rows 3–5 carry `hidden="1"`, rows 1, 2, 6 do not.
  - `CollapsedBelowMarksTheRowAfterTheGroup` — `Below`, `ShownLevels = 1`, levels 0,1,2,1,0: row 6 has `collapsed="1"`; with `ShownLevels = 2`: row 5 (level 1 after a hidden level-2 row) has `collapsed="1"` and row 6 has none.
  - `TrailingGroupBelow` — `Below`, `ShownLevels = 1`, the last row at level 1: implement the spec's builder decision (a cell-less `<row r="n+1" collapsed="1"/>` only if `SheetLayoutTests.Rows` and `TabularImporter` read the same rows back as without it; otherwise no such row and a `docs/KNOWN-ISSUES.md` entry). The test asserts whichever was chosen, and that the read-back rows equal the written rows.
  - `OutlineWithoutDeeperRowsWritesNoGroups` — `Levels = 1`, header only; and header + level-0 rows: valid file, no `outlineLevel` attribute.
  - `NextSheetStartsWithoutOutlineState` — sheet 1 outlined (`Below`, `ShownLevels = 1`), sheet 2 plain: `sheet2.xml` equals the `sheet1.xml` of the same plain sheet written alone.
  - `ReadsBackTheSameRows` — the round trip: `SheetLayoutTests.Rows` of an outlined file equals that of the same data written without an outline, hidden rows included.
- [ ] **Step 2: Run, verify they fail.**
  Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~XlsxOutlineTests"`
- [ ] **Step 3: Implement.** In `BeginSheet`, with `Outline` set: `<sheetPr><outlinePr summaryBelow="0"/></sheetPr>` right after `XlsxParts.WorksheetStart` for `Above`, and the `sheetFormatPr` between `AppendFreeze` and `AppendWidths`. Row start tag: `outlineLevel`, `hidden`, `collapsed` per the spec's xlsx rules; for `Below`, remember the previous row's level and whether it was hidden. `SummaryRows.Above` with `ShownLevels` is Task 5: in this task it writes `hidden` but no `collapsed`. Reset all outline state in `BeginSheet`.
- [ ] **Step 4: Run `XlsxOutlineTests`, `OutlineUnchangedBytesTests`, `XlsxRoundTripTests`, `SheetLayoutTests`.** Expected: PASS.
- [ ] **Step 5: Commit** — `feat: write row outline levels in xlsx`.

### Task 5: xlsx — collapsed with summary above (one row held back)

**Files:**
- Modify: `src/TriasDev.Tabular/Xlsx/XlsxSheetWriter.cs`
- Test: `tests/TriasDev.Tabular.Tests/Writing/XlsxOutlineTests.cs`

**Interfaces:**
- Consumes: Task 4's row start tag and outline state.

- [ ] **Step 1: Write the failing tests** (`Above`, `ShownLevels = 1` unless stated).
  - `CollapsedAboveMarksTheParentRow` — levels 0,1,2,1,0: row 2 has `collapsed="1"`, row 3 (level 1, hidden, followed by a hidden level 2) has `collapsed="1"`, rows 4–6 none. With `ShownLevels = 2`: row 2 none, row 3 `collapsed="1"`.
  - `LastHeldRowIsWritten` — the sheet's last row at level 1, then `CompleteAsync`: it is present, before `</sheetData>`, without `collapsed`; and a last row at level 0 after a parent with no children: no `collapsed`.
  - `HeldRowPrecedesFilterAndMerges` — `AutoFilter = true`, a merge on row 2: the last row precedes `</sheetData>`, which precedes `<autoFilter` and `<mergeCells`.
  - `HeldRowSurvivesFlush` — write 3 rows, `await writer.FlushAsync()`, write 3 more, complete: rows 1–7 present once each, in order, attributes as for the unflushed sheet (compare the sheet XML of both).
  - `BatchRowEndsGroups` — rows at 0, 1, then `WriteBatch` of two rows: row 2 has `collapsed="1"`, the batch rows have no `outlineLevel` and no `hidden`.
  - `AboveWithoutShownLevelsHoldsNothing` — `Above`, `ShownLevels = null`, levels 0,1,2,1,0: no `collapsed`, no `hidden`; the sheet XML equals the one Task 4 wrote for the same rows (an expected string kept in the test).
- [ ] **Step 2: Run, verify they fail.**
- [ ] **Step 3: Implement the hold**, only for `Above` + `ShownLevels`: the ended row's cell text waits in a second `RowText`, its start tag not yet written; the next `BeginRow(level)` (or `CloseSheet`, before `SheetDataEnd`) writes the waiting row's start tag — `collapsed="1"` when `level` is greater than the waiting row's and `level ≥ ShownLevels` — then its cells, then begins the new row. Swap the two `RowText` buffers rather than copying.
- [ ] **Step 4: Run `XlsxOutlineTests`, `OutlineUnchangedBytesTests`, `BufferedEmitTests`, `MergeTests`.** Expected: PASS.
- [ ] **Step 5: Commit** — `feat: open an xlsx outline collapsed with summary rows above`.

### Task 6: ods — nested row groups and collapse

**Files:**
- Modify: `src/TriasDev.Tabular/Ods/OdsSheetWriter.cs` (`BeginSheet` `:72` and the `TableEnd` at `:95`, `BeginRow` `:105`, the `TableEnd` in `Complete` at `:218`)
- Test: `tests/TriasDev.Tabular.Tests/Writing/OdsOutlineTests.cs`

**Interfaces:**
- Consumes: `ISheetWriter.BeginRow(int level)`, `SheetOptions.Outline` (Task 3).

- [ ] **Step 1: Write the failing tests** on `content.xml`, rows reduced to their markers.
  - `JumpAndFallNestCorrectly` — levels 0,2,1,0: `<table:table-row-group><table:table-row-group>` before the level-2 row, one `</table:table-row-group>` before the level-1 row, one before the last row; groups balanced.
  - `ShownLevelsCollapses` — `ShownLevels = 1`, levels 0,1,2,1,0: both groups carry `table:display="false"`, rows at level ≥ 1 carry `table:visibility="collapse"`; with `ShownLevels = 2`: only the inner group and the level-2 row.
  - `SummaryRowsWritesNothing` — `Above` and `Below` give identical `content.xml`.
  - `GroupsCloseAtNextSheetAndAtCompletion` — the last row of sheet 1 at level 2, sheet 2 begins: two closings before the first `</table:table>`; the same at completion for the last sheet.
  - `OutlineWithoutDeeperRowsWritesNoGroups` — header only, and level-0 rows only: no `table-row-group`.
  - `BatchRowEndsGroups` — rows at 0, 2, then a `WriteBatch` row: two closings before the batch row.
  - `ReadsBackTheSameRows` — as in Task 4, for ods.
- [ ] **Step 2: Run, verify they fail.**
  Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~OdsOutlineTests"`
- [ ] **Step 3: Implement** per the spec's ods section: a depth counter; open/close in `BeginRow(level)` before `<table:table-row`; close what is open before each `OdsParts.TableEnd`; `table:visibility="collapse"` inside the row's start tag.
- [ ] **Step 4: Run `OdsOutlineTests`, `OutlineUnchangedBytesTests`, `OdsRoundTripTests`, `OdsWriterTests`.** Expected: PASS.
- [ ] **Step 5: Commit** — `feat: write row outline groups in ods`.

### Task 7: LibreOffice reads the outline; documentation

**Files:**
- Test: `tests/TriasDev.Tabular.Tests/Writing/OutlineInteropTests.cs`
- Modify: `docs/exporting.md` (after the `SheetOptions` paragraph at `:148`), `docs/formats.md` (the layout differences), `docs/KNOWN-ISSUES.md` only if Task 4 chose to omit the trailing row

**Interfaces:**
- Consumes: `LibreOffice.Resave(byte[], string)` and `LibreOffice.ConvertToCsv` (`tests/TriasDev.Tabular.Tests/Fixtures/LibreOffice.cs`), which skip when LibreOffice is absent.

- [ ] **Step 1: Write the tests** (`[Theory]` over xlsx and ods; levels 0,1,2,1,0, `Above`, `ShownLevels = 1`).
  - `LibreOfficeUnderstandsTheOutline` — `content.xml` of `LibreOffice.Resave(file, ext)` has two nested `table:table-row-group` elements around rows 3–5, and rows 3–5 carry `table:visibility="collapse"`.
  - `LibreOfficeShowsEveryValue` — `ConvertToCsv` lists all six rows, hidden ones included, values as written.
- [ ] **Step 2: Run them.**
  Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~OutlineInteropTests"`
  Expected: PASS (locally LibreOffice is installed; CI sets `TABULAR_REQUIRE_SOFFICE=1`).
- [ ] **Step 3: Document.** `docs/exporting.md`: the spec's consumer example, `Levels` as the deepest level used, `ShownLevels` counted as Excel's level buttons, `SummaryRows` xlsx-only (LibreOffice draws its button on a group's first row), `write.outline-level`, `TabularExport<T>` refusing an `Outline`. `docs/formats.md`: one line on the outline in each format.
- [ ] **Step 4: Run the whole suite.**
  Run: `dotnet test --solution TriasDev.Tabular.slnx`
  Expected: PASS, no warnings (warnings are errors).
- [ ] **Step 5: Commit** — `test: LibreOffice reads the row outline; docs: the row outline`.

### Task 8: Acceptance by hand, speed, pull request

- [ ] **Step 1: Write the acceptance files** with a throwaway program against the branch's build (levels 0/1/2, `Above`, `ShownLevels = 1`; and the same with `ShownLevels = null`), as xlsx and ods. The owner opens: xlsx in Excel, ods in LibreOffice, xlsx in LibreOffice (blocking), ods in Excel (not blocking). Expected per the spec's hand test; a non-blocking difference goes into `docs/KNOWN-ISSUES.md`.
- [ ] **Step 2: Speed.** With the owner's go-ahead (it rents a server): push the branch, then
  `SUITE=write HEAD=feat/197-row-outline benchmarks/cloud/hetzner.sh benchmarks/cloud/remote/refs.sh`.
  Expected: no writer slower than `main` beyond run-to-run noise. Add an outlined sheet to the write scenarios only if the owner asks.
- [ ] **Step 3: Open the pull request** with the title from Global Constraints and a hand-written description (what changes, why, the new code, csv ignoring it, `TabularExport<T>` refusing it, unchanged bytes), `Closes #197`.
