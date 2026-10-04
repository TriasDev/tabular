# Formats

Which kind of file it is comes from its bytes, never its name. What each kind reads as:

## An OpenDocument cell says its own type

A workbook guesses dates from number formats; a `.ods` cell states its type beside its value, so
there is nothing to guess. `float`, `percentage` and `currency` read as numbers, `date` as a date,
`time` as the workbook serial of as many days — a time of day on 31 December 1899, the day an xlsx
time-only cell reads on, and a longer duration on the day that serial names — and `boolean` as a
boolean. Anything else is text: the cell's `office:string-value` when it has one, else its paragraphs
joined by a line feed, comments left out. A formula reads as the value the writer cached. ODF has no
error type; LibreOffice marks a failed formula in an extension attribute, and it reads as an error
carrying the text the cell shows — `#N/A`, `#REF!`, or LibreOffice's own `Err:502`.

A row or cell repeated by attribute is expanded only when it holds a value; the million empty rows
LibreOffice declares after the last one cost nothing. Covered cells of a merge read as empty. Hidden
sheets and rows read like any other, as in xlsx. A sheet is a table of the spreadsheet itself: a
sub-table inside a cell, or the table a DDE link caches, is not one, and its text is not the cell's.

## A sheet says whether it is hidden

`SheetInfo.Visibility`, and `SheetProfile.Visibility` beside it, say whether the author left a sheet
showing: `Visible`, `Hidden`, or `VeryHidden` (an xlsx sheet only code can show again). An xlsx sheet's
comes from its `state`, an ods table's from its style's `table:display`; a csv's single sheet is
always visible. Hidden sheets are still listed and read — lookup lists, instructions and calculations
in a template are data too — so a screen offering sheets, or an upload picking one, decides what to
do with them. A chart sheet, macro sheet or dialog sheet holds no cells and is not listed at all.

## An archive reads as one workbook

A zip that is not itself a workbook, a tar, or a tar compressed with gzip is read as one workbook: its sheets are the sheets of every file in it
that can be read as a table — csv, xlsx and ods — in the order of their paths, each with its file's
path as `SheetInfo.Source` and its own `Format`. A csv file is named after its file; a workbook's
sheets keep their names, and `Source` tells two `Sheet1` apart. Files are judged by their bytes, as a
file on its own is.

- **Left out without a word:** directories, links and other entries that are not files, hidden files
  and folders (`.DS_Store`, anything whose name starts with a dot, macOS `._` files) and `__MACOSX/`.
- **Skipped with a reason** in `FileProfile.SkippedEntries`: an encrypted file, a gzip-compressed file, a nested zip or tar, another
  OpenDocument type, a legacy `.xls`, an XML document, a binary file, and a workbook that is damaged
  or of a kind not read (`.xlsb`). Nested archives are not opened.
- **Refused:** an archive with nothing readable in it, as `format.unsupported`.

A csv file is read as a stream straight out of the archive, never unpacked, with its dialect
decided from its own head; moving back to it reads it again from its first row. A workbook has to be
read with random access, so it is copied into memory while its sheets are read — one at a time, up to
`ArchiveCursorOptions.MaxEmbeddedWorkbookBytes` (256 MB by default, and any size a server can
afford: the copy is held in pieces, not one array). A mapping plan made from an archive records the
sheet's `Source`, and an import refuses the archive as `structure.sheet-changed` when another file now
stands at the plan's index.

A tar reads the same way, plain or compressed with gzip (`.tar.gz`, `.tgz`): `FileProfile.Format` is
`Tar` for both, `Source` is the entry's path as written, long paths (PAX and GNU) included. A plain
tar's files are read where they lie, workbooks included — nothing is copied. A compressed tar can only
be read from its start: opening it decompresses it once to list its sheets, and reading a sheet
decompresses it again up to that file, unless the cursor is moving forward through the archive
anyway — so reading the sheets in the order the archive stores them costs one pass. A tar cut off or
damaged anywhere, or missing the zero block that ends it, is refused as `format.truncated` or
`format.corrupt`; a tar holding a GNU sparse file, which cannot be read past, as `format.unsupported`.
Old v7 tars, which carry no magic, are not recognised.

## A gzip file reads as the file inside it

A file compressed with gzip — `data.csv.gz`, `report.xlsx.gz`, `table.ods.gz` — is read as the file
it holds: a csv file's one sheet, or a workbook's sheets. `FileProfile.Format` is `Gzip`; each sheet
keeps the inner file's `Format`. The sheet's `Source` is the file name the gzip header stores (the
`gzip` tool stores it; .NET's `GZipStream` does not), and `null` when it stores none — so a mapping
plan does not depend on the name a caller passes. A csv sheet is named after that stored name, or
after the file without its `.gz`.

A csv file is decompressed as it is read, never unpacked; moving back to its sheet decompresses it
again. A workbook is decompressed into memory once, up to `ArchiveCursorOptions.MaxEmbeddedWorkbookBytes`.
What a file expands to is bounded by `ArchiveCursorOptions.MaxUncompressedBytes`, counted while
decompressing.

Every gzip member's checksum and size are checked, so a file cut off or damaged anywhere is refused
as `format.truncated` or `format.corrupt` — never read as a shorter file, which is what .NET's own
`GZipStream` does with a cut-off one. A file of several members (`cat a.gz b.gz`, bgzip) reads as one
file; bytes after the last member that do not start another are ignored, as the `gzip` tool does.

Not read: a zip archive inside a gzip file, a file compressed twice, and a gzip file inside a zip
archive — skipped there as `Compressed`.

## Malformed input is repaired, and the repair is counted

Files that people upload are not well-formed. A 572 MB real-world export carries quotes inside
unquoted fields (`100 21"st AVE`), text after a closing quote (`"C" Road`), and 302 quotes that
are never closed. Refusing such a file is defensible and useless; one bad address must not fail an
import of five million rows.

So the reader recovers — and counts what it recovered in `CursorDiagnostics`. A silent recovery is
indistinguishable from correct reading, and *that* is the defect: left unbounded, those 302 quotes
swallow about 39,000 records without a word.

Two repairs, both counted. A quoted field that has crossed a line ending and holds a whole record's
worth of delimiters was never a quote — a lone `"` that opened a field and swallowed the records
after it — and those records are read as records the moment that is clear (`RecoveredStrayQuotes`).
Past a line ending a quote also closes a field only where a field can end, so an inch mark in the
swallowed text (`135"th`) does not close it. Behind that, a quoted field longer than 100 lines, or
one the file ends inside, is abandoned the same way (`RecoveredUnterminatedQuotes`); that bound is
what protects tables narrower than five columns, where the delimiter test is off. Genuine multi-line
values — an address, a long note — hold a delimiter or two at most and are left alone.

## What each format holds when written

A value a format cannot hold exactly is refused with a `TabularWriteException` that names the sheet,
row and column; nothing is rounded or cut silently. The rules are the same for every format wherever
they can be, so the same data fails the same way whichever format is chosen. See [Exporting](exporting.md).

| | csv | zip of csv | xlsx | ods |
|---|---|---|---|---|
| Sheets | one, its name not written | any number, each `<name>.csv` | any number | any number |
| Rows per sheet, header included | no limit | no limit | 1,048,576 | 1,048,576 |
| Columns per sheet | 1 to 16,384 | 1 to 16,384 | 1 to 16,384 | 1 to 16,384 |
| Styles, layout, widths | ignored | ignored | yes | yes |
| Distinct styles per file | | | 4,096 | 4,096 |
| Merged ranges per sheet | written as the value and empty fields | the same | 65,536 | no limit |
| Text per cell | 16 M chars (the reader's field limit) | 16 M chars | 32,767 chars | 16 M chars, and the escaped text within the reader's token limit |
| Line breaks in one text | 100 (the reader's quoted-field limit) | 100 | no limit | no limit |
| Dates | any `DateTime`, ISO or the culture's pattern | the same | from 1900-01-01 | any |
| Numbers | as written, in the culture | as written, in the culture | a double cell: a long or decimal only if a double holds it exactly (at most 15 significant digits for a decimal) | the same as xlsx |

What holds for all four:

- A `double` must be finite and within 15 significant digits (`write.not-finite`, `write.precision-loss`):
  that is what reads back as itself.
- Time is kept to whole milliseconds; a `DateTime`'s `Kind` is not kept; a `DateOnly` reads back as a
  `DateTime` at midnight.
- Text cannot hold the control characters XML 1.0 forbids (all but tab, line feed and carriage return)
  or an unpaired surrogate (`write.invalid-character`), csv included.
- A sheet's name is 1 to 31 characters, none of `[ ] : * ? / \`, no apostrophe at either end, not
  `History`, unique ignoring case; a zip sheet's name may not hold `< > " |` or end with a dot or a space.
  A header is not empty, neither starts nor ends with whitespace, and is unique in its sheet ignoring case.
- Csv: a quoted text whose line breaks the reader would take for a stray quote is refused
  (`write.ambiguous-line-breaks`) rather than written to be read back as several records.
- Csv's culture also picks the delimiter when none is set (`;` where the decimal separator is a comma),
  and a culture is accepted only if its numbers and dates read back through the import's own reader.
- Other programs may show a value differently from the one stored (an xlsx date before 1900-03-01,
  a date before 1582-10-15, a number of more than 15 digits after a re-save). The stored value is
  unchanged; see [Known issues](KNOWN-ISSUES.md#writing-what-does-not-come-back-exactly-as-written).
