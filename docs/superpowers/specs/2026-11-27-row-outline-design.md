# Row grouping (outline levels) in the xlsx and ods writers — design

Status: approved in brainstorming, 2026-11-27. Issue: #197. Extends
[2026-10-03-writing-design.md](2026-10-03-writing-design.md) and
[2026-10-04-styled-columnar-export-design.md](2026-10-04-styled-columnar-export-design.md), whose rules
still hold unless this document says otherwise.

## Why

A report whose rows form a hierarchy — a contract framework with its documents beneath it, an IT asset
with the processes that use it — is read by collapsing it: first the top level, then one branch. Excel
and LibreOffice do that with row grouping (outline levels). The writer has styles, frozen panes,
auto-filters and merged ranges, and no way to say that a row belongs under the row above it.

A consumer that needs a nested sheet today flattens the hierarchy (the parent's values repeated on every
child row) or writes that one report with another spreadsheet library. The first consumer writes its
report by hand through `TabularWriter` (`BeginSheet` with `SheetOptions … with { AutoFilter = true }`,
then `BeginRow` / `Write` / `EndRow` per row) and draws a parent row followed by its children. The
outline it replaces — written today with another library — puts each row's level on the row, places the
summary row **above** its group, and opens fully expanded.

## Goals

1. A row can be written at an outline level; Excel and LibreOffice show the outline controls and
   collapse and expand the groups.
2. The summary row can sit above the rows it summarises (a parent followed by its children).
3. A sheet can open collapsed to a chosen level, with every collapsed group showing "+" in both
   Excel and LibreOffice.
4. The round trip holds: what the writer writes reads back through `TabularFile.Open` with the same
   rows and values, hidden rows included. The grouping is layout.
5. A sheet without an outline is written byte for byte as today, at today's speed.

## Non-goals

- Column grouping.
- Reading outline levels, hidden rows or collapsed state on import.
- A level in `TabularExport<T>` (a selector per item) or in `ColumnBatch` (a level column). The first
  consumer writes by hand; each waits for a consumer that needs it. The API below leaves room for both.
- Collapsing one group and not another. A sheet opens collapsed to one level or fully expanded.
- Choosing where LibreOffice draws its outline button (see "What the formats do").

## Anti-goals

- Buffering a sheet, or more than one row of it, to learn its depth or its groups: memory stays flat in
  the number of rows.
- Any cost on the row path of a sheet without an outline beyond a single branch.
- A file that opens with a group shown as expanded ("−") whose rows are hidden — the first click then
  does nothing, which reads as broken.

## What the formats do (verified)

Verified on 2026-11-27 with Excel for Mac and LibreOffice: a hand-written xlsx with
`<outlinePr summaryBelow="0"/>`, levels 0/1/2 and one collapsed group, converted by LibreOffice to ods
and back.

- **xlsx.** A row carries `outlineLevel`, `hidden` and `collapsed`. The sheet carries
  `<sheetPr><outlinePr summaryBelow="0"/></sheetPr>` for summary rows above and
  `<sheetFormatPr … outlineLevelRow="n"/>`, the deepest level, which sizes the outline gutter and its
  level buttons. `collapsed="1"` belongs on the summary row of a collapsed group: with summary rows
  above, the row before the group; with summary rows below, the row after it. Excel showed the button on
  the parent row, "+" on the collapsed group, "−" on the others.
- **xlsx in LibreOffice.** Verified by the owner on 2026-11-27 with Excel for Mac and LibreOffice:
  LibreOffice ignores `<outlinePr summaryBelow="0"/>` and takes a group's collapsed state from
  `collapsed="1"` on the row **after** the group; Excel, with summary rows above, takes it from the row
  before it. With the flag on the parent only, LibreOffice opened the group's rows hidden with "−" over
  them. With the flag on both rows (`Name`, `F1` collapsed, `D1a`/`D1b` level 1 hidden, `F2` collapsed)
  both opened it right: Excel "+" on `F1` and nothing extra at `F2`; LibreOffice "+", opening on the
  first click.
- **ods.** Groups are nested `<table:table-row-group>` elements around the rows; a hidden row carries
  `table:visibility="collapse"`; a collapsed group carries `table:display="false"`. ODF has **no**
  attribute for the summary position: LibreOffice dropped `summaryBelow="0"` on the way back to xlsx and
  draws its button on the **first row of the group**, the bracket running down — readable for a parent
  followed by its children. LibreOffice's own conversion did not write `table:display="false"`, and the
  collapsed group then opened showing "−" over hidden rows: the writer must write it.
- **The ods collapse markup, confirmed.** On 2026-11-27 hand-made ods files in exactly this document's
  shape — `table:display="false"` on each group opened at a level ≥ `ShownLevels`,
  `table:visibility="collapse"` on each row at such a level, a group nested in a collapsed one, a jump
  0 → 2 under a shown level-0 row — opened in LibreOffice as intended: with `ShownLevels = 1` only the
  level-0 rows, each group showing "+"; with `ShownLevels = 2` the level-1 rows shown ("−") and the
  level-2 rows behind "+", the jump 0 → 2 drawn as a shown group holding a collapsed one; fully expanded
  without the attributes; every group expanded to the rows written.
- **Reading.** The xlsx cursor does not skip hidden rows; the ods cursor reads rows inside
  `table-row-group` (`OdsCursorTests.ReadsRowsInsideHeaderRowsAndRowGroups`) and does not skip collapsed
  rows. The round trip needs no change on the read side.

## Approaches

1. **A level on each row, the outline declared up front in `SheetOptions` — chosen.** Each row is
   independent and checked on its own; it is how the replaced library and the issue's sketch model it;
   a later `TabularExport<T>` selector (`item => item.Depth`) and a `ColumnBatch` level column fit it
   without a new model.
2. **Explicit groups, `BeginGroup()` / `EndGroup()` between rows.** Mirrors ods and makes per-group
   collapse natural, but puts open/close bookkeeping and its errors ("group not closed") on the caller,
   still needs the depth declared for xlsx, and fits a typed export poorly — an item has a depth, not a
   start and an end. Rejected.
3. **A level on each row, nothing declared.** The xlsx writer streams: `BeginSheet` emits the sheet's
   head and `<sheetData>` at once, so `outlineLevelRow` cannot wait for the deepest row. Writing it as 7
   always shows eight level buttons for a two-level sheet; leaving it out risks Excel not showing the
   outline symbols. Rejected.

## API

```csharp
public sealed record SheetOptions
{
    // HeaderStyle, FreezeRows, FreezeColumns, AutoFilter as today.

    /// <summary>Groups the sheet's rows by the level each is begun at; null writes no outline.</summary>
    public RowOutline? Outline { get; init; }
}

/// <summary>How a sheet's rows are grouped. Csv ignores it.</summary>
public sealed record RowOutline
{
    /// <summary>The deepest level a row of the sheet is written at: 1 to 7.</summary>
    public required int Levels { get; init; }

    /// <summary>Whether a group's summary row sits above or below it. Ods has no such setting; LibreOffice draws its button on the group's first row.</summary>
    public SummaryRows SummaryRows { get; init; }   // default Above

    /// <summary>How many levels show when the file opens, as Excel's level buttons count them: 1 shows the level-0 rows only. Null shows every row.</summary>
    public int? ShownLevels { get; init; }
}

public enum SummaryRows { Above, Below }

public sealed class TabularWriter
{
    public void BeginRow();                  // unchanged: level 0
    public void BeginRow(int outlineLevel);  // new
}
```

The consumer's change:

```csharp
writer.BeginSheet(name, columns, new SheetOptions
{
    FreezeRows = 1,
    AutoFilter = true,
    Outline = new RowOutline { Levels = 1, ShownLevels = 1 },
});

writer.BeginRow();                 // the framework, level 0
…
writer.EndRow();

writer.BeginRow(outlineLevel: 1);  // one of its documents
…
writer.EndRow();
```

Decisions inside the API, each a call made here:

- **`RowOutline` is a record of its own**, not three more flat properties on `SheetOptions`: the three
  belong together, `Outline = null` is the plain "no outline" that keeps files unchanged, and a check of
  `ShownLevels` against `Levels` stays inside one value.
- **`SummaryRows` defaults to `Above`**, unlike Excel's default. A writer streams a tree parent first;
  both known consumers put the parent above. `Below` is there for a sheet of subtotals.
- **`ShownLevels` counts as Excel's buttons do** ("1" = level 0 only), so that what a caller sets is
  what they would click. It ranges from 1 to `Levels`; `Levels + 1` would show everything, which is
  `null`.
- **`Levels` is the declared deepest level, not the deepest used.** A sheet declared deeper than it
  writes shows extra level buttons; the documentation says to declare the deepest level the sheet
  uses.
- **The header row is level 0 and never in a group.** Rows written by `WriteBatch` /
  `WriteBatchAsync` and by `TabularExport<T>` are level 0.
- **A level may rise by more than one** from one row to the next (0 → 2), as Excel allows: ods opens
  two groups at once. A level may fall by any amount.

## Checks and refusals

Checked by `TabularWriter`, the same for every format — a csv writer refuses what an xlsx writer
refuses, so code that runs against one runs against all:

- **The options** — `Levels` outside 1..7, `ShownLevels` outside 1..`Levels`, `SummaryRows` not a
  defined value — are refused by `BeginSheet` with `ArgumentOutOfRangeException`, as a freeze outside
  the sheet is. The rule lives on `SheetOptions` (an `OutlineProblem` beside `FreezeProblem`) so that the
  writer and `TabularExportBuilder<T>.Build` cannot drift.
- **`TabularExportBuilder<T>.Build` refuses options with an `Outline`** (`ArgumentException`): an export
  writes every row at level 0, so the outline would be empty. The refusal is lifted when the export
  gains a level selector.
- **A level outside 0..`Levels`** — negative, deeper than declared, or above 0 on a sheet without an
  `Outline` — is refused by `BeginRow(int)` with `TabularWriteException`, new code
  **`write.outline-level`** (`ErrorCodes.Write.OutlineLevel`), located at the sheet, **the row that would
  have been written** (`_rowNumber + 1`), column 0 and its header. This differs from
  `write.too-many-rows`, which is located at the last row held because the refused row's number may not
  fit an int for csv: `BeginRow(int)` checks the row limit **first**, so a row past the limit is refused
  as `write.too-many-rows` whatever its level, and `_rowNumber + 1` cannot overflow when the level is
  judged. A level comes from the data (a tree's depth), so it is a write refusal, not an argument
  exception. The writer is faulted, as for every write refusal.

## xlsx

- **Sheet head**, written by `BeginSheet` only when `Outline` is set, in the schema's order:
  `<sheetPr><outlinePr summaryBelow="0"/></sheetPr>` first in `<worksheet>` when `SummaryRows.Above`
  (nothing for `Below`, Excel's default), and `<sheetFormatPr defaultRowHeight="15"
  outlineLevelRow="{Levels}"/>` after `<sheetViews>` and before `<cols>`. `defaultRowHeight` is
  required by the schema; 15 points is the height of the workbook's default font, Calibri 11, as
  `XlsxStyles` writes it.
- **Rows**: `outlineLevel="L"` when L > 0; `hidden="1"` when `ShownLevels` is set and
  L ≥ `ShownLevels`; `collapsed="1"` on a row whose group is hidden —
  - *above*: on row r when **either**
    1. (Excel) the next row's level is greater than r's and the next row is hidden; **or**
    2. (LibreOffice, which reads the mark from the row after a group) the previous row's level is
       greater than r's and the previous row is hidden — **unless** the next row's level is greater than
       r's and the next row is shown: r heads a shown group of its own, and Excel, with summary rows
       above, reads `collapsed` on r as the state of that group and would open it collapsed. Excel wins
       and rule 2 is skipped for r. The cost is LibreOffice's: it does not take the mark on r as r's own
       group (checked by converting hand-made files), so without it LibreOffice opens the hidden group
       before r with "−" over its hidden rows. This happens wherever a parent holding a hidden subgroup is
       followed by a parent with shown children (levels 0,1,2,0,1 with `ShownLevels = 2`), and is in
       `docs/KNOWN-ISSUES.md`.

    The header row has no previous row. A collapsed group that ends the sheet has no row after it:
    nothing is written there (a cell-less row reads back as one more row, as below), and LibreOffice
    shows that group with "−" (`docs/KNOWN-ISSUES.md`).

    A jump of more than one level: LibreOffice builds a group per level, the two spanning the same rows,
    and attaches the mark on the row after them to the **outer** one (with levels one step apart, 0,1,2,0,
    it collapses the inner level-2 group, as intended). With levels 0,2,0 and `ShownLevels = 2`,
    rule 2 marks the last row and LibreOffice collapses the level-1 group, which Excel shows expanded
    ("−", holding only the hidden level-2 rows); the level-2 group itself carries no collapsed state in
    LibreOffice. The same rows are hidden in both programs; only the buttons differ
    (`docs/KNOWN-ISSUES.md`);
  - *below*: on row r when the previous row's level is greater than r's and the previous row is hidden.

  Nested summary rows inside a hidden group are marked the same way, so that a group expanded by hand
  shows its own children's state correctly.
- **One row held back, only where needed.** With `Above` and `ShownLevels` set, whether a row is
  `collapsed` depends on the next row, so its bytes wait in the writer until the next `BeginRow` or the
  end of the sheet, then go out with the right start tag. Without `ShownLevels`, or with `Below`, no row
  waits. A held row is emitted before anything that follows `</sheetData>` (merged ranges, the
  auto-filter) and before the sheet closes.
- **The row after the last group, with `Below`.** A collapsed group that ends the sheet has no summary
  row below it. Left to the builder: write a cell-less `<row r="n+1" collapsed="1"/>` (what Excel
  writes) only if the xlsx cursor and the import then read the sheet back unchanged — no extra row — and
  n + 1 is within the row limit; otherwise leave it out and record the "−" on that last group in
  `docs/KNOWN-ISSUES.md`.

## ods

- **Groups**: before a row at level L with D groups open, open L − D `<table:table-row-group>` elements
  when L > D, close D − L when L < D. Every group still open is closed before `</table:table>` — when the
  next sheet begins and when the file completes.
- **Collapse**: a group opened for level L ≥ `ShownLevels` carries `table:display="false"`; a row at
  level L ≥ `ShownLevels` carries `table:visibility="collapse"`.
- **`SummaryRows` writes nothing**; the documentation says LibreOffice decides where its button is.
- Nothing is held back: each decision is known when the row begins.

## csv and a zip of csv sheets

Ignore the outline, as they ignore the other layout options. The checks above still apply.

## Unchanged files and speed

- With `Outline` null, `BeginRow()` and `BeginRow(0)` write exactly today's bytes in every format. A
  test pins it: a sheet with styles, a freeze, a filter and merges, written before this change, compared
  by bytes after it.
- The row path of a sheet without an outline adds at most one branch. The write benchmarks are run
  before and after (min of N, see CONTRIBUTING.md) and show no regression; a sheet with an outline is
  measured too, with and without `ShownLevels`.

## Tests

- Options: each refusal of `Levels`, `ShownLevels`, `SummaryRows`, from `BeginSheet` and from
  `TabularExportBuilder<T>.Build`; the export refusal of an `Outline`.
- `BeginRow(int)`: −1, `Levels` + 1, 1 without an `Outline`; code, location, faulted writer; csv refuses
  the same.
- xlsx: the sheet head's elements and their order, with and without a freeze and widths; row attributes
  for levels 0/1/2, a jump 0 → 2, a fall 2 → 0; `collapsed` for above and below, nested; the held row
  across `FlushAsync`, at the end of a sheet, before merges and a filter; the last row of the sheet.
- ods: nesting with jumps and falls; groups closed at the next sheet and at completion; `display` and
  `visibility`; several sheets.
- Round trip for xlsx and ods: the same rows and values as the same data written without an outline,
  hidden rows included.
- Bytes unchanged without an outline (above).
- **By hand, first, before any writer code.** The ods collapse markup above is inferred, not yet seen
  working: LibreOffice's own conversion wrote no `table:display="false"`. The plan's first step is a
  hand-made `content.xml` in exactly the shape this document prescribes — groups with
  `table:display="false"`, rows with `table:visibility="collapse"`, a nested group inside a collapsed
  one, a jump 0 → 2 under a shown level-0 row — opened in LibreOffice by the owner. If a collapsed group
  does not show "+" and open on the first click, the ods section is revised before it is built.
- **By hand, before release:** a sheet with levels 0/1/2, summary above, `ShownLevels = 1`, written by
  the writer: the outline controls show; only the level-0 rows are visible; every collapsed group shows
  "+" and opens on the first click; level button 3 shows every row. The same file with `ShownLevels`
  null opens fully expanded.
  - **Required, blocking the release:** xlsx in Excel, ods in LibreOffice, xlsx in LibreOffice.
  - **Checked, not blocking:** ods in Excel. Excel's ods import is outside the writer's reach; a
    difference there goes into `docs/KNOWN-ISSUES.md`.

## Documentation

- `docs/exporting.md`: the outline beside the other `SheetOptions`, with the example above, the
  meaning of `ShownLevels`, and that `SummaryRows` is xlsx-only.
- `docs/error-codes.md`: `write.outline-level`.
- `docs/formats.md`, where the formats' layout differences are listed: ods and the summary position.
- `PublicAPI.Unshipped.txt`. The change is additive: a `feat:` pull request, no breaking change.

## Left to the builder

- Internal names, and how `ISheetWriter.BeginRow` learns the level (a parameter or a separate call).
- How the xlsx writer holds a row back, within the constraint above (one row, only where needed).
- The decision on the cell-less row after a trailing group with `Below`, within the constraint above.
- The attribute order on `<row>` and the arrangement of tests.
