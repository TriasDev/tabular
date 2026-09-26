# Reading OpenDocument spreadsheets (.ods)

Status: agreed design, 2026-09-26. Issue #13. Comes before the archive reader (#19), so that an
archive can hand `.ods` entries to this cursor from its first version.

## Intent

A `.ods` file — LibreOffice's native format, increasingly required by German and EU public
administration — is read like a workbook: sheets, rows, typed cells. Analysis, mapping, precheck and
import do not change; this is one more `ITabularCursor`.

## Detection

By bytes, as the other formats. ODF requires the first zip entry to be `mimetype`, stored
uncompressed, so a spreadsheet is recognised from the file's first ~80 bytes without opening the
archive: the local header's file name `mimetype` at offset 30, followed directly by
`application/vnd.oasis.opendocument.spreadsheet`. `TabularFile.Detect` returns `TabularFormat.Ods`;
every other zip goes on to the xlsx path, whose existing refusal of other ODF documents (text,
presentation) stays. A spreadsheet written against the rule (mimetype not first) is refused by that
path too — rare enough to leave.

## Public API (additive)

- `TabularFormat.Ods`.
- `TriasDev.Tabular.Ods.OdsCursor : ITabularCursor` — `(Stream, OdsCursorOptions? = null, bool leaveOpen = false, CancellationToken = default)`, the same shape as `XlsxCursor`.
- `TriasDev.Tabular.Ods.OdsCursorOptions` — `MaxUncompressedBytes` (8 GB — raised from 2 after measuring: a million rows of seventeen columns is 1.9 GB of content), `MaxPackageEntries` (16,384), `MaxSheets` (4,096), `MaxColumns` (16,384), `MaxRows` (1,048,576), `MaxValueChars` (16 M); checked where handed over.
- `TabularOpenOptions.Ods`.

## Reading

`content.xml` is read forward with `SheetScanner`, which gains a second mode that keeps attributes
by local name (the part after the prefix) from a small set: `name`, `value-type`, `value`,
`date-value`, `time-value`, `boolean-value`, `string-value`, `number-columns-repeated`,
`number-rows-repeated`, `c`. The worksheet mode — `r`, `t`, `s` by a one-character check — is
unchanged, so xlsx reading does not slow down.

- **Sheets.** All tables live in one `content.xml`, and `Sheets` must be known before reading, so
  the cursor makes one pass over the part at construction for the `table:table` names. Moving to the
  next sheet continues from the current position; moving back reopens the part.
- **Rows.** `table:table-row` inside a table, through the transparent wrappers
  `table:table-header-rows`, `table:table-rows`, `table:table-row-group`. Row numbers count from 1.
- **Cells.** `office:value-type` decides:
  - `float`, `percentage`, `currency` → number from `office:value`;
  - `date` → date from `office:date-value` (ISO, date or date-time);
  - `time` → date-time from `office:time-value` (ISO duration), on 1899-12-31 as xlsx reads a
    time-only cell;
  - `boolean` → boolean from `office:boolean-value`;
  - `string` or no type → text: `office:string-value` when present, else the cell's paragraphs.
  Formulas are read through their cached value, as in xlsx.
- **Text.** `text:p` paragraphs joined by a line feed; `text:span` and `text:a` transparent;
  `text:s` as `c` spaces (default one), `text:tab` as a tab, `text:line-break` as a line feed;
  `office:annotation` left out. Bounded by `MaxValueChars`.
- **Repeats.** `table:number-columns-repeated` and `table:number-rows-repeated` are never expanded
  literally for empty cells or rows: an empty repeat only moves the column or row number. A repeated
  non-empty cell or row is expanded, bounded by `MaxColumns` and `MaxRows` — LibreOffice writes
  "the next million rows are empty" as one element, and a crafted file writes a million non-empty
  ones.
- **Merges.** `table:covered-table-cell` is an empty cell in its position.
- Hidden sheets, hidden rows and header rows read as ordinary ones, as in xlsx.

Out: flat ODS (`.fods`, plain XML) — its own issue.

## Verification

- Hand-built minimal packages (`Fixtures/OdsPackage.cs`, raw XML as `XlsxPackage`) for each rule
  above, and hostile ones for every bound.
- Differential: the xlsx golden fixtures and the #8 corpora converted with LibreOffice
  (`soffice --headless --convert-to ods`), each file read both ways, compared cell by cell — locally,
  not in the suite, as in #8.
- Performance: the large workbook converted to ods; peak memory flat, and the construction-time name
  pass measured.
