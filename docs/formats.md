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

## A zip archive reads as one workbook

A zip that is not itself a workbook is read as one: its sheets are the sheets of every file in it
that can be read as a table — csv, xlsx and ods — in the order of their paths, each with its file's
path as `SheetInfo.Source` and its own `Format`. A csv file is named after its file; a workbook's
sheets keep their names, and `Source` tells two `Sheet1` apart. Files are judged by their bytes, as a
file on its own is.

- **Left out without a word:** directories, hidden files and folders (`.DS_Store`, anything whose
  name starts with a dot) and `__MACOSX/`.
- **Skipped with a reason** in `FileProfile.SkippedEntries`: an encrypted file, a nested zip, another
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
