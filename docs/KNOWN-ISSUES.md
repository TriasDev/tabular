# Known limitations

Behaviour of `TriasDev.Tabular` — reading and writing — that is wrong at the edges, or looser than it
reads, and has not been changed yet — each with what would make it matter, so you can tell whether it affects your files. None
of them loses or silently alters data in files an ordinary producer writes; where one could, it says
so and links the issue that tracks it.

Found a case that belongs here, or one of these that bites you? Open an issue; a real file that hits
an entry is the best reason to fix it.

---

## Correctness at the edges

### A stray quote closed at a field boundary within less than a record joins two lines
A quote that opens a field and is closed on a later line by a quote followed by a delimiter or a
line ending is read as one field spanning lines, as RFC 4180 says — unless the field has by then
swallowed a whole record's worth of delimiters, which marks it as a stray quote (#20, #10). A stray
quote that happens to be closed at a field boundary before that point keeps the two lines joined:
nothing in the text tells it from a genuine multi-line value. In tables narrower than five columns
the delimiter test is off, and only the 100-line bound catches a stray quote.

**Matters when** a producer writes lone quotes into fields often enough for two of them to meet at a
field boundary within one record. Neither the real-world nor the synthetic 5M-row fixture has such a
case.

### Number formats the reader does not know are dates
`IsBuiltInDateFormat` accepts 14–22 and 45–47. The specification also reserves 27–36 and 50–58 for
dates in East Asian locales. A cell using one reads as a number.

### `applyNumberFormat="0"` is ignored
The attribute says the format is not applied. Honouring it would change whether a styled cell is read
as a date.

### Relationship targets are not percent-decoded
Parts are found through the package relationships, and targets are resolved against their folder,
`../` included. A target that percent-encodes its name (`sheet%201.xml`) is not decoded, so it is
looked up literally and not found. No producer seen writes one.

### A whitespace-only string is dropped one way and kept the other
A shared string of spaces without `xml:space="preserve"` is dropped, because that path reads with
`XmlReader` and `IgnoreWhitespace`. The same content written inline is kept, because the scanner has
no such notion. Same value, two answers, depending only on how the writer chose to store it.


### Rows that repeat a row number are read as successive rows
A worksheet that writes every cell in a `<row>` of its own, all carrying `r="1"`, is read as that many
rows, each with one cell; LibreOffice joins them into one. Row numbers here never go backwards or
repeat, so a message never points at a row twice. Seen once, in one writer's output. Matters if a
producer that writes this way turns up among real files.

### An OpenDocument error cell carries the writer's text, not an Excel code
LibreOffice writes a formula that failed with its own wording — `Err:502` where Excel would say
`#VALUE!` — and recalculates on saving, so a workbook converted to `.ods` can hold different errors,
and different values in volatile or unsupported formulas, than the original. Read as written; a
caller comparing the two formats sees the writer's differences, not the reader's.

### Flat OpenDocument (`.fods`) and Excel 2003 XML are not read
Both are a spreadsheet as one plain XML document, without the zip. A file that opens with an XML
declaration — in UTF-8, or in UTF-16 after its byte order mark — is refused as unsupported rather
than read as csv. Matters if anyone sends one.

---

## Contracts looser than they read

### A date must carry two separators, so `15 Jan 2023` is text

`DateReading.LooksLikeOne` requires two of `-`, `/`, `.`, or a colon, before a parse is attempted.
That is what refuses a decimal (`1.5` reads as the fifth of January under en-US) and a day-and-month
(`3/15` takes the current year), and the price is a date written with spaces and a month name.

A genuine `0001-01-01` is refused for the neighbouring reason: year one is the marker that the text
named no year.

**Matters when** a customer exports dates in a long form. The fix is a parse against the culture's
year-bearing patterns rather than a shape test, which is more code than the case has so far earned.

### Dates cannot carry a range
`MinValue`/`MaxValue` compare numbers, so a range on a date field is refused by the validator
(`mapping.constraint-type-mismatch`) rather than silently judged against zero, as it once was. Date
ranges — the obvious second use — cannot be expressed yet.

**Matters as soon as** a caller wants one. Fix with a date-typed range constraint.

### `CsvCursor.MoveToSheet` does not rewind
The interface documents "positions before its first row"; the csv implementation returns `index == 0`
and stays where it is. Analysing and then extracting through one cursor instance reads a csv from
wherever it stopped. A csv inside an archive does rewind: the archive reopens its file.

### `IsBlank` ignores the binding's empty-equivalents
A row whose every mapped cell holds `k.A.` is not skipped. It is counted as produced and handed over
as a valid row of entirely absent values.

### `RowError.RawValue` is the trimmed value
The spec says the value "as it appeared in the file". Trimming is on by default, so a value failing a
length rule *because of* its spaces is reported without them, pointing at something that looks right.

### `ValidateOnly` returns a full-length span of absent values
Indistinguishable from a row where every field was legitimately empty.

### `MappedValue` accessors throw, and say nothing about it
`Date` on a decimal value throws; `Integer` on a large one throws. `RawCell` documents exactly this
hazard for the identical design. `MappedValue.Absent` also reports `Type == Text`, so an absent value
is indistinguishable from an absent text value.

### `MappedValue.FromDate` loses the time of day in `Text`
Every length, pattern and allowed-value constraint sees `Text`, so a datetime is validated against a
date-only string while `Date` still carries the ticks.

### `CsvDialect` lets a caller claim a value was detected
`EncodingSource` and `DelimiterSource` are `required` on a record a caller constructs to *override*
detection. The provenance exists so a UI can tell a fact from a guess; a hand-built dialect can lie
about it.

### A byte order mark overrides a caller-specified encoding
`StreamReader` is constructed with `detectEncodingFromByteOrderMarks: true` alongside the chosen
encoding.

### The dialect override is all or nothing
A caller who knows only the delimiter must also supply the encoding, losing detection for it. There
is no per-property override, no line-ending member, and the quote character is hard-coded with no
provenance.

### The package ceiling counts bytes off the wire, not memory
`MaxUncompressedBytes` sums the entries' declared sizes. Text decoded to UTF-16 doubles, and a buffer
that grows to hold a token peaks at three times its content. The scanner buffer and the csv field
have their own ceilings, which carry most of this weight, but the package ceiling by itself is not
the bound it appears to be.

### A zip entry that understates its size is read short, without an error
The runtime's zip reader stops an entry at the size its headers declare and does not check the
checksum, so a crafted archive — or a buggy writer — that declares 10 bytes for a csv of a hundred
thousand rows yields its first rows as a plausible, shorter table. Workbook parts are read the same
way. An upload cut off in transit is not this case: it loses the directory at the end and is refused
as damaged. Catching it needs a CRC-32 over every byte read, which the base class library offers on no
x86 processor, and a table-driven one costs about half a second on the 572 MB csv. Matters if files
come from a writer that gets sizes wrong.

### A pattern constraint is bounded per value, not per run
A pattern runs on .NET's non-backtracking engine, whose time is linear in the value and which has no
clock. One using a lookaround or a backreference falls back to the backtracking engine, bounded at
100 ms a value, and a million rows at 100 ms each is a run measured in hours. Compilation is not bounded at all: a
pattern with large counted quantifiers costs time and memory before any value is seen.

**Matters when** patterns become admin-authored or config-driven rather than domain-authored.

### Per-column allowances multiply by the column count
Each column keeps ten first values, sixty-four frequency keys and up to a hundred and twenty located
outliers, each an unbounded string. The column count is now bounded by the format's last column, so
the product is bounded — but it is 16,384 times those allowances.

### A workbook's own strings have no length ceiling
Sheet names, relationship targets and number-format codes are taken straight from the package with a
ceiling on how many there may be and none on how long each may be. Measured: 4,096 sheets — exactly
the permitted number — with 400,000-character names is a 1.65 MB upload that retains 3,125 MB for the
cursor's whole life.

This is the third appearance of one mistake: **a ceiling on the number of things rather than on their
size.** It was fixed for the shared string table, which has both, and left standing on its three
siblings.

**Matters when** somebody sends a file built to do this. An ordinary export cannot reach it.

### A sheet whose part is missing aborts the analysis
`MoveToSheet` throws rather than returning false, so a workbook declaring a sheet whose part is absent
fails the whole pass instead of skipping it.

---

## Deliberate choices

Decided, not deferred — listed so they are not mistaken for gaps.

- **Reading is synchronous.** Parsing is processor work over a buffered stream, and a row cannot be a
  `ReadOnlySpan<T>` and be awaited at once. See the remarks on `ITabularCursor`. Writing is the
  opposite: it writes into memory synchronously and touches the target stream only asynchronously (except closing it after a failed `Create`).
- **No comment syntax in csv.** The format does not define one. Add it if the files we receive use it.
- **The header is the first row.** No heuristic looks elsewhere; `MappingPlan.HeaderRowIndex` is where
  a user says otherwise.
- **`"C" Road` is repaired without a diagnostic.** The repair is lossy, and unlike an unterminated
  quote it is not counted. Whether it should be is a judgement, not an oversight.

## Writing: what does not come back exactly as written

By design, and the same in every format:

- Leading and trailing whitespace in text is trimmed on reading; empty and whitespace-only text reads as no value.
- Time is truncated to whole milliseconds.
- `DateTime.Kind` is not kept: the wall-clock value comes back as `Unspecified`.
- A `DateOnly` comes back as a `DateTime` at midnight.
- With `CsvWriterOptions.FormulaGuard`, text starting with `=`, `+`, `-`, `@`, tab or CR comes back with a leading `'`.
- A double comes back as a decimal — the import has no double type — and only with the at most 15 significant digits the writer accepts.

Read back through this library, xlsx and ods follow the same rules and add no exception of their own. Other programs differ, mostly in how they display a value — LibreOffice, in two cases, in the value itself:

- LibreOffice does not emulate Excel's 1900 leap-year bug: a date before 1900-03-01 written to xlsx shows one day early there (Excel and this library read it correctly).
- ods: column widths are rounded to whole characters; text holding a carriage return is written twice, as paragraphs for display and as office:string-value for the exact value.
- LibreOffice shows dates before 1582-10-15 in the Julian calendar: 0001-01-01 written appears as 0001-01-03, 1500-03-01 as 1500-02-20. The stored value is unchanged and reads back exactly through this library.
- LibreOffice (and Excel) keep 15 significant digits when they re-save a number: a 16-digit integer such as 9007199254740992 comes back as 9007199254740990 after opening and saving the file there. Write identifiers longer than 15 digits as text.

Reading ods back with this library, an all-empty row is passed over rather than handed out, so the import does not count it as skipped; the rows after it keep their numbers.

TabularExport picks a column type by overload resolution, and some lambdas do not resolve: a char property becomes an integer column (its code point); ulong, `p => default` and a lambda returning null are ambiguous; an int or long with a DecimalImportField is ambiguous between decimal and double; TimeSpan, DateTimeOffset, Guid and enums have no overload and fail with a misleading "cannot convert to string" (CS0029). Convert explicitly (`.ToString()`, `.UtcDateTime`, …); write enums and identifiers as text.

### A zip of csv sheets

- `TabularFormat.Zip` writes every sheet as `<sheet name>.csv` in one zip — csv sheets only, each exactly the csv file that sheet alone would be (`CsvWriterOptions` apply to every entry). Use it for several sheets past the xlsx row limit, or wherever csv is wanted but one file holds several tables.
- Sheet names follow the workbook rules and, since they become file names, may not hold `< > " |` or end with a dot or a space. Names are stored as UTF-8.
- Reading the zip back gives the same sheets by name. Their order follows the entries' paths, not the order they were written in.
- The zip is written by the library's own streaming zip writer — asynchronously, so it can go straight into an ASP.NET Core response — with zip64 past 4 GB.

## Styles

- Styles are for xlsx and ods; csv ignores them, so one code path writes every format.
- Format codes are a subset of Excel's (see `NumberFormat` and `DateFormat`); anything else is refused when parsed, never at write time.
- Separators in a date format show as written (`dd/mm/yyyy` shows slashes in every locale); the decimal point and thousands separator of a number format follow the reader's locale.
- A `StyleId` belongs to the writer that returned it; passing it to another writer is refused with an `ArgumentException` (the default `StyleId`, the unstyled cell, is accepted by every writer).
- At most 4096 distinct styles per file. Declare the styles once (`static readonly`) and register each once per writer. A style rule that returns a 4,097th is refused at its cell (`write.too-many-styles`); a 4,097th registered by hand is an `InvalidOperationException`.

### Sheet layout

- `SheetOptions` sets a header style, frozen rows and columns, and an auto-filter on the header row through the last row written; csv ignores it. A declared export takes its `SheetOptions` once on its builder (`.Sheet(...)`) and applies them to every sheet it writes.
- `writer.Merge(rows, columns)` makes the next cell the top-left of a merged range. The writer skips the covered positions — the row's next write lands after the range, later rows skip it too — and writes them itself. The import reads a merged range as its value in the top-left cell and empty cells elsewhere; csv writes exactly that.
- A merge must end inside the sheet: ending a sheet (or the file) while a range still has rows to cover is refused.
- xlsx holds at most 65,536 merged ranges per sheet; the next is refused with `write.too-many-merges`.

### Data by column

- `ColumnBatch` takes one list per column (arrays, `List<T>`, any `IReadOnlyList<T>` such as a protobuf repeated field) and writes them as rows, typed, without copying; reuse one batch per sheet — `Reset` keeps its slots.
- The lists must not change until the batch is written; `Reset` drops them.
- A style rule should return styles declared once (`static readonly`): the same instance costs a reference compare per cell. A rule that builds a new style per cell still writes a correct file, more slowly.
- A very wide xlsx is large when read back: 10,000 rows × 5,000 columns is about 1.6 GB of sheet XML, which still fits the reader's default `MaxUncompressedBytes` (2 GiB); past roughly 13,000 rows of that width, raise it to read the file back (ods defaults to 8 GiB and holds 4 GB for the same data).
