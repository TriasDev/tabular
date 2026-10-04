# Styled and columnar export — design

Status: approved in brainstorming, 2026-10-04. Extends
[2026-10-03-writing-design.md](2026-10-03-writing-design.md), whose rules still hold unless this
document says otherwise. Tracker: #67.

## Why

The write side (csv, xlsx, ods, `TabularExport<T>`) exports data. A real consumer that should move
its exports onto Tabular needs more, and its export libraries today are a streaming xlsx writer and a
csv writer:

- **Data arrives by column.** A server stream delivers batches; each batch is a row count plus one
  typed vector per column (doubles, longs, strings, dates, nullable wrappers). Thousands of columns
  are normal: typical exports are 1–10k rows × 4–5k columns, the worst case 5M rows.
- **Minimal formatting is used**: fill and font colour per cell (colour by value, from a legend),
  a styled header, a legend sheet with merged cells, date and number formats, column widths.
- **The csv form of a multi-sheet export is a zip of csv files**, one per sheet.

This document supersedes the earlier spec's "no styling" exclusion: **minimal formatting is in scope
for xlsx and ods**. Still excluded: formulas, hyperlinks, images, charts, comments, conditional
formatting rules stored in the file, rich text within a cell, row heights.

## Goals

1. Replace the consumer's export libraries without losing a feature it uses.
2. Speed not worse than those libraries on the same data and the same styling; memory flat in the
   number of rows; nothing allocated per cell.
3. The round trip holds: a styled export imports back through Tabular with the same values and
   types as an unstyled one.

## 1. Cell styles

```csharp
static readonly CellStyle Red = new()
{
    Fill = CellColor.FromRgb(0xF8696B),
    Font = new CellFont { Color = CellColor.FromRgb(0xFFFFFF), Bold = true, Italic = false },
    Number = NumberFormat.Parse("#,##0.00"),
    Date = DateFormat.Parse("dd/mm/yyyy"),
    Horizontal = HorizontalAlignment.Center,
    Wrap = true,
    Border = CellBorder.Thin(CellColor.FromRgb(0xBFBFBF)),
};

StyleId red = writer.Style(Red);
writer.Write(12.5, red);
```

- `CellStyle` is an immutable `sealed record` (value equality). Every property is optional; an
  unset property means the format's default. `CellColor` is our own RGB struct (`FromRgb(int)`,
  `Parse("#RRGGBB")`) — no `System.Drawing`.
- `CellFont`: `Color`, `Bold`, `Italic`. No font family or size.
- `HorizontalAlignment`: `General` (default), `Left`, `Center`, `Right`. `Wrap`: wrap text.
- `CellBorder`: none (default) or `Thin(color)` on all four sides.
- **Number and date formats are separate properties.** `Number` applies to numeric cells (long,
  decimal, double), `Date` to date and date-time cells; each is ignored for other kinds. A date cell
  without a `Date` format gets the writer's default date or date-time format, so a date always
  carries a date format and the xlsx reader still recognises it. A number can never get a date format.
- **Formats are Excel format codes, a supported subset** that translates to ODF number styles:
  - `NumberFormat`: digits `0` and `#`, one decimal point, a thousands separator `,` in the integer
    part, a trailing `%`, literal text in double quotes before or after. Examples: `0`, `0.00`,
    `#,##0`, `#,##0.00`, `0%`, `0.0%`, `"€ "#,##0.00`.
  - `DateFormat`: `yyyy`, `yy`, `mm`, `m`, `dd`, `d`, `hh`, `h`, `ss`, and separators
    `/ - . : space` or quoted literals. `mm`/`m` means minutes when it follows `h`/`hh` or precedes
    `ss`, months otherwise (Excel's rule).
  - Anything else — colours, conditions, sections (`;`), fractions, scientific, AM/PM, `@` — is
    refused by `Parse` with an `ArgumentException` naming the unsupported part. Nothing fails later,
    at write time.
- `writer.Style(CellStyle)` returns a `StyleId` (a struct holding an int). It may be called before or
  during any sheet; the same style (by value) returns the same id. One dictionary lookup per call, none
  per cell.
- Every `Write(value)` overload gains `Write(value, StyleId style)`; `WriteEmpty(StyleId)` writes a
  styled empty cell. `default(StyleId)` is the unstyled cell.
- The writer resolves a `(StyleId, value kind)` pair to the format's style index lazily and caches it
  in an array: one array read per styled cell.
- At most **4096** distinct styles per file; the next `Style` call throws `TabularLimitException`.
- **xlsx:** `styles.xml` is written at `CompleteAsync` (it is already a separate part). Fonts, fills,
  borders and number formats are deduplicated; custom number formats start at id 164 as now.
- **ods:** cell styles become common styles in `styles.xml` (`office:styles`), written at the end,
  each with its own data style. The automatic styles in `content.xml` (column widths, default date
  styles) stay as they are, so content still streams.
- **csv and zip:** styles are accepted and ignored, so one code path writes every format.

## 2. Sheet layout

```csharp
writer.BeginSheet("Legend", columns, new SheetOptions
{
    HeaderStyle = headerStyle,   // a CellStyle, applied to the header row
    FreezeRows = 1,
    FreezeColumns = 0,
    AutoFilter = true,
});

writer.BeginRow();
writer.Write("Hazard legend", title, span: new CellSpan(Rows: 1, Columns: 4));
writer.EndRow();
```

- `BeginSheet(name, columns, SheetOptions? options)` is a new overload; the existing one means
  default options.
- **Merged cells** are declared on the top-left cell through the optional `span` parameter of the
  styled `Write` overloads, because ods must know them when that
  cell is written. Covered positions are skipped automatically: the next write in the row lands at
  column + `Columns`; in the rows below, the writer emits the covered cells itself when the row
  reaches them. The caller never writes placeholders.
  - Errors, thrown at the write that causes them (`ArgumentException` for an invalid span,
    `InvalidOperationException` for the state): a span overlapping another, reaching past the sheet's
    columns, rows or columns < 1, a 1 × 1 span; a sheet ended (`BeginSheet`, `CompleteAsync`) while
    a span still has rows to cover.
  - xlsx: ranges are kept in memory and written as `<mergeCells>` after the sheet data; at most
    65,536 merges per sheet, the next throws `TabularLimitException`.
  - ods: `table:number-columns-spanned` / `table:number-rows-spanned` and `table:covered-table-cell`.
  - csv: the value in the top-left cell, the covered positions empty, so columns stay aligned.
  - Import reads a merged range as its value in the top-left cell and empty cells elsewhere.
- **Freeze panes** (`FreezeRows`, `FreezeColumns`): xlsx `<pane>` in `sheetViews` before the data;
  ods `settings.xml` config items, written at the end.
- **Auto-filter** on the header row through the last row written: xlsx `<autoFilter>` after the data
  and the hidden defined name `_xlnm._FilterDatabase` in `workbook.xml`, as Excel writes it; ods
  `table:database-ranges` at the end of `content.xml`. Requires a header (columns given).
- **Column limit:** 16,384 columns per sheet for xlsx and ods (Excel; LibreOffice since 7.4),
  checked by `BeginSheet` with `TabularLimitException`. csv and zip have none.
- **Empty runs in ods** are written as one cell with `table:number-columns-repeated`, so wide sheets
  with gaps stay small. xlsx already omits empty cells.
- Widths stay as today (`WriteColumn.Width`). No row heights: Excel and LibreOffice size rows,
  including wrapped ones, themselves. No per-column default style: a style travels with the cell.

## 3. `ColumnBatch` — writing a batch by column

```csharp
writer.BeginSheet("Locations", columns, options);

var batch = new ColumnBatch();                    // reused for every batch
await foreach (var chunk in source.WithCancellation(ct))
{
    batch.Reset(chunk.Count);
    foreach (var column in chunk.Columns)
        switch (column.Type)
        {
            case Kind.Double: batch.Add(column.Doubles, style: v => Legend(column, v)); break;
            case Kind.Long: batch.Add(column.Longs); break;
            case Kind.Text: batch.Add(column.Texts); break;
            case Kind.NullableDouble: batch.Add(column.NullableDoubles, w => w.HasValue ? w.Value : (double?)null); break;
            case Kind.Timestamp: batch.Add(column.Timestamps, t => t.ToDateTime()); break;
        }
    await writer.WriteBatchAsync(batch, ct);
}
await writer.CompleteAsync(ct);
```

- `Add(IReadOnlyList<T> values, …)` for `string`, `long`, `int`, `short`, `double`, `decimal`,
  `bool`, `DateTime`, `DateOnly` and the nullable value types. Arrays, `List<T>` and protobuf
  `RepeatedField<T>` are taken as they are, never copied; arrays and lists take a span fast path.
- `Add(IReadOnlyList<TItem> items, Func<TItem, T?> value, …)` for wrappers (nullable messages,
  timestamps): one delegate call per cell.
- A `DateTime` column takes `hasTime` (default: from the values, as `Write(DateTime)` decides today).
- Style per column, one of: a constant `StyleId`; a vector `IReadOnlyList<StyleId>` computed by the
  caller; a rule `Func<T, CellStyle?>`. A rule's result is turned into a `StyleId` through a
  reference-keyed cache with a last-hit shortcut per column, so a palette held in `static readonly`
  styles costs a reference compare per cell in the common case.
- Checks are per column, not per cell: the batch's column count equals the sheet's, every vector's
  length equals the batch's row count — otherwise `ArgumentException` naming the column. Values are
  checked exactly as by `Write` (precision, characters, limits) and a failure reports sheet, row,
  column and header.
- `WriteBatch(batch)` writes synchronously into the buffer; `WriteBatchAsync(batch, ct)` does the
  same and then flushes whenever `FlushRecommended`, also inside the batch, so memory stays flat
  with 5k columns.
- Inside: one typed slot per column, reused by index across batches (nothing allocated per batch
  once warm); rows traversed row by column with one virtual call per cell.
- Merges are not available through a batch; a batch writes data rows.
- `TabularExport<T>` gains the same rule per column:
  `.Column("Score", l => l.Score, style: v => v > 5 ? Red : null)`.

## 4. Zip of csv sheets

- `TabularWriter.Create(stream, TabularFormat.Zip)` writes every sheet as the entry
  `<sheet name>.csv` of one zip, through our `ZipWriter` (deflate, streamed, zip64 when large). Each
  entry follows the csv rules and `CsvWriterOptions` (BOM, quoting, `FormulaGuard`, culture,
  delimiter).
- Styles, merges, freeze and filter are ignored, as for csv. No row limit: this is the format for 5M
  rows and several sheets.
- Re-import through `TabularFile.Open` → `ArchiveCursor` gives the same sheets: the sheet name is the
  entry name without `.csv`. The reader orders sheets by path, so the round trip promises the same
  sheets by name, not their order; documented.

## 5. Performance and verification

- **Benchmarks** (net10, `benchmarks` project; third-party libraries only there, never in the
  package):

  | Scenario | Formats | Compared with |
  |---|---|---|
  | 10k × 5k: ~20 ordinary columns + 5k doubles, ~10 % empty, legend colouring by rule | csv, zip, xlsx, ods | LargeXlsx, SpreadCheetah (xlsx); CsvHelper, Sep (csv) |
  | 5M × 30 | csv, zip | CsvHelper, Sep |
  | 1M × 30, styled | xlsx, ods | LargeXlsx, SpreadCheetah |

- **Targets:** not slower than LargeXlsx (xlsx) and CsvHelper (csv) on the same data and styling;
  peak memory independent of the row count; zero bytes allocated per cell.
- **Size:** 10k × 5k xlsx is about 1.5 GB of XML uncompressed, near our own reader's default
  `MaxUncompressedBytes` (2 GB). The writer does not limit; the docs say such a file imports with a
  raised limit, and the 10k × 5k re-import check runs with one.
- **In the test suite:** 1k × 5k per format with re-import; round trip of styled and unstyled exports
  giving the same values; merge and span errors; style limit; format-code parser (accepted and
  refused codes, the `mm` rule); allocation tests per cell for styled writes and batches;
  OpenXmlValidator for every xlsx test; LibreOffice opens styled xlsx and ods, keeping merges, freeze
  and filter (checked by conversion), under the existing interop fixture.
- **Final manual check:** 10k × 5k per format with re-import, and the 5M-row csv and zip.

## Delivery

Each part is its own plan and pull request:

1. Styles: `CellStyle`, `CellColor`, `CellFont`, `CellBorder`, `NumberFormat`, `DateFormat`,
   `StyleId`, `Write(value, style)`, xlsx and ods styles.
2. Sheet layout: `SheetOptions` (header style, freeze, filter), merges, column limit, ods
   repeated empties.
3. `ColumnBatch`, `WriteBatch`/`WriteBatchAsync`, style rules (batch and `TabularExport<T>`).
4. Zip of csv sheets.
5. Benchmarks and comparison, docs (`docs/exporting.md`, README), ADR-0002 — absorbs #74.
