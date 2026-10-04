# Writing csv, xlsx and ods

Status: agreed design, 2026-10-03, revised after an independent review the same day. Issue #67.

## Intent

An application that imports through TriasDev.Tabular exports through it too: it writes a table to
csv, xlsx or ods, and importing that file again gives back the same values with the same types.

Speed and memory are the point, as on the read side. The driving case is a streaming export: a data
owner sends a 5M-row table to the C# side over gRPC server streaming, in chunks of about 10,000
items, and the C# side writes the file either straight into an HTTP response or into a temporary
file or blob for later download. Memory must not grow with the row count: one chunk plus a bounded
buffer.

Out of scope: formulas, free styling, merged cells, charts, reports, editing existing files,
`DataTable`, `IDataReader`, translated (`ImportField.Translated`) fields, `DateTimeOffset`.

The write side lives in the same package and takes no dependency, like the read side (ADR-0001;
ADR-0002 records the same decision for writing, with the reason that .NET has no usable
open-source ods writer).

## Layout

New folder `src/TriasDev.Tabular/Writing`. Public types are in the `TriasDev.Tabular` namespace;
format options sit next to their cursors.

| Public type | Namespace | Purpose |
|---|---|---|
| `TabularWriter` | `TriasDev.Tabular` | low-level writer, one per file |
| `WriteColumn` | `TriasDev.Tabular` | a header text and an optional width |
| `TabularExport<T>`, `TabularExport.For<T>()` | `TriasDev.Tabular` | object layer: columns as lambdas |
| `TabularWriteException` | `TriasDev.Tabular` | a value the format cannot hold exactly |
| `CsvWriterOptions` | `TriasDev.Tabular.Csv` | culture, delimiter, BOM, formula guard |
| `XlsxWriterOptions` | `TriasDev.Tabular.Xlsx` | compression level |
| `OdsWriterOptions` | `TriasDev.Tabular.Ods` | compression level |
| `TabularWriterOptions` | `TriasDev.Tabular` | holds the three, as `TabularOpenOptions` does for reading |

Internal: `CsvSheetWriter`, `XlsxSheetWriter`, `OdsSheetWriter`, `SpillBuffer`, `ZipWriter`, `Crc32`.

## Low-level writer

Shaped like `Utf8JsonWriter`: typed calls, no boxing, no allocation per row.

```csharp
await using TabularWriter w = TabularWriter.Create(stream, TabularFormat.Xlsx, options, leaveOpen: false);
w.BeginSheet("Portfolios", columns);   // header row is written here
w.BeginRow();
w.Write(42L); w.Write("Alpha"); w.Write(1234.56m); w.Write(new DateOnly(2026, 10, 3));
w.EndRow();
if (w.FlushRecommended) await w.FlushAsync(ct);
await w.CompleteAsync(ct);
```

- `BeginSheet(string name, ReadOnlySpan<WriteColumn> columns)`. A width is written for xlsx
  (`<cols>`, which precedes the sheet data) and ods (`table:table-column` with a column style);
  csv ignores it. Without it Excel shows a date-time as `#####`.
- `Write` overloads: `string?`, `long`, `decimal`, `double`, `DateTime`, `DateOnly`, `bool`; plus
  `WriteEmpty()`. Narrower integers reach the `long` overload by implicit conversion.
- The caller picks the format explicitly; there is no detection on the write side.
- Several sheets: call `BeginSheet` again. A csv file has one sheet; a second `BeginSheet` throws.
- Every xlsx row element carries its `r` attribute, so an empty row never shifts the row numbers
  after it.

### Sync writes, async flush

The format writers — the csv text, the sheet XML, the zip writer below — write synchronously
into `SpillBuffer`, which is memory. `FlushAsync` moves the buffer to the real stream asynchronously.
This is what makes the writer usable on an ASP.NET Core response body, which refuses synchronous
writes by default, on net8.0 as well as net10.0. `FlushRecommended` turns true once the buffer
passes a threshold of about 1 MB. The writer never writes to the target stream synchronously.

### The library's own zip writer

`ZipArchive` does not fit, measured during planning (LibreOffice 25.x, .NET 8):

| Written | LibreOffice |
|---|---|
| `ZipArchive` to a non-seekable stream: every entry, the stored `mimetype` too, with a data descriptor | refuses: "source file could not be loaded" |
| `ZipArchive` to a seekable stream: no data descriptors | opens |
| `mimetype` stored without a descriptor, the deflated entries with one | opens |

On a seekable stream `ZipArchive` seeks back to patch each local header, so the whole entry — a
sheet of a million rows — would stay in the buffer until it closes. So the library writes zip
itself, internal `ZipWriter`, used by both workbook formats:

- **Small parts** (`mimetype`, manifest, content types, relationships, workbook, styles) are
  compressed in memory — or stored, for `mimetype` — and written with CRC and sizes in the local
  header, no data descriptor.
- **The large part** (an xlsx sheet, the ods `content.xml`) streams through `DeflateStream` into
  `SpillBuffer`, with flag 0x0008 and a data descriptor after the data.
- Its own CRC-32 (slicing-by-8, so it keeps up with deflate), since `System.IO.Hashing` is a
  package; checked against known vectors and against `ZipArchive` reading our output.
- Zip64 is written when an entry or an offset passes 4 GB: a zip64 data descriptor, zip64 extra
  fields in the central directory, the zip64 end records. A wide 1M-row sheet can pass 4 GB
  uncompressed; Excel and LibreOffice are tested on such a file in the benchmark phase, and if
  either refuses it, the writer caps an entry's uncompressed size with `limit.exceeded`.

## Values and the round trip

The rule: whatever is written is read back by the import as the same value of the same type. A
value a format cannot hold exactly is an error, never a silent change.

| Value | xlsx | ods | csv | Error when |
|---|---|---|---|---|
| `string` | `t="inlineStr"`, XML-escaped; `\r` and other control characters as `_xHHHH_`, a literal `_x` as `_x005F_` | `<text:p>` per line, `text:tab`, `text:s c=` for runs of spaces | RFC 4180 quoting | all: a character XML 1.0 forbids (in csv too, for one rule everywhere); xlsx: longer than 32,767 chars; csv: more line breaks than the reader accepts in one field, or multi-line text the reader would split into records |
| `long` | `<v>` | `office:value-type="float"` | invariant or culture | xlsx, ods: a value `double` cannot hold exactly |
| `decimal` | `<v>`, its own digits | `float` | its own digits | xlsx, ods: `(decimal)(double)v != v`, in effect more than 15 significant digits |
| `double` | `<v>`, `"R"` | `float` | `"R"` | all: `NaN`, `±∞`; all: `(double)(decimal)d != d`, i.e. more than 15 significant digits or outside `decimal`'s range |
| `DateTime`, `DateOnly` | serial, date or date-time style | `office:date-value`, ISO, with an automatic date style | ISO, or the culture pattern below | xlsx: before 1900-01-01; csv: year 1 (`DateReading` refuses it) |
| `bool` | `t="b"` | `boolean`, with an automatic boolean style | `true` / `false` | — |
| `null`, `WriteEmpty` | no `<c>` | empty cell | empty field | — |

"A character XML 1.0 forbids" is exact: U+0000–U+0008, U+000B, U+000C, U+000E–U+001F, U+FFFE,
U+FFFF and unpaired surrogates. Tab, LF and CR are allowed and written as above. `\r` needs the
escape in xlsx because the scanner turns a literal CR into LF (`SheetScanner`). In ods a paragraph
cannot keep a CR, so text holding one also states its exact value in `office:string-value` (CR, LF and
tab as character references), which the reader prefers; the paragraphs show it, a CR as a line
break. Ods column widths are rounded to whole characters: `content.xml` streams, so its column
styles are declared before the first sheet.

Strings are not written as a shared-string table: it would hold every distinct string until the end.

The decimal check matches how extraction reads a number cell (`ValueReading.TryToDecimal`, a plain
`(decimal)` cast of the parsed `double`): two million random decimals showed no disagreement. csv
holds a `long` or a `decimal` exactly, so it raises neither check. A `double` is checked in every
format, because xlsx and ods read it back through that 15-digit cast and csv would not: without the
check, `0.1 + 0.2` would come back as `0.30000000000000004` from csv and `0.3` from xlsx.

The csv line-break limit is the reader's default `CsvCursorOptions.MaxQuotedFieldLines` (100). A
quoted field past it is read as an unterminated quote and replayed as rows, so a field with more
line breaks would come back as many rows — the writer refuses it instead.

Two more csv rules come from the reader's heuristics, found by the final review of part 1:

- **Every delimiter the reader could detect is quoted.** The dialect detector picks any of `,` `;`
  tab `|` that appears outside quotes on most lines, so a value carrying one — text, or a number or
  date formatted with a decimal comma — is quoted even when it is not the file's delimiter.
  Otherwise a single de-DE column of `1,5`, `2,5` is read with `,` as its delimiter and imports as
  1, 2.
- **Text the reader would take for a stray quote is refused** (`write.ambiguous-line-breaks`). In a
  sheet of five columns or more, the reader replays a quoted field that crosses a line break and
  holds a record's worth of delimiters as the records it swallowed; the writer applies the same rule
  and refuses such text rather than write a file that imports with extra rows.

Documented exceptions:

1. **Leading and trailing whitespace.** The reader trims text (`RawCell.FromText`); the writer writes
   text as given, so `" DE "` comes back as `"DE"`.
2. **Empty and whitespace-only text** come back as an absent value, like `null`.
3. **Time is truncated to whole milliseconds** in every format. An xlsx serial holds no more, and
   the reader rounds a serial to the millisecond, so a truncated value reads back exactly.
   Truncation, not rounding: rounding overflows on `DateTime.MaxValue` and moves 23:59:59.9996 to
   the next day.
4. **`DateTime.Kind` is not kept.** The wall-clock value is written; the reader returns it as
   `Unspecified`.
5. **`DateOnly` comes back as a `DateTime` at midnight.** The read side has no `DateOnly`.

xlsx styling is three fixed styles: General, date, date-time. The header row is unstyled.

### csv

`CsvWriterOptions`:

- Default: delimiter `,`, `.` as decimal separator, ISO dates (`yyyy-MM-dd`, and
  `yyyy-MM-ddTHH:mm:ss.fff` when there is a time), UTF-8 with BOM, quoting per RFC 4180, `\r\n`
  line ends.
- `Culture`: numbers in that culture's format, no group separators; dates in its
  `ShortDatePattern`, plus ` HH:mm:ss.fff` when there is a time (the culture's "G" pattern would
  drop the milliseconds, and appending `.fff` to it breaks AM/PM patterns). The delimiter defaults
  to `;` when the culture's decimal separator is `,`.
- `Delimiter`: one of `,` `;` tab `|` — the ones the dialect detector knows — and never the
  culture's decimal separator.
- Checked where handed over (`OptionChecks`): the delimiter as above, and the culture by writing a
  probe date-time and number and reading them back with the import's own readers (`DateReading`,
  `NumberReading`). A culture whose output does not read back, or that is missing under invariant
  globalization, is refused.
- The import of a culture-formatted file should name the culture in its plan rather than take it
  from analysis: when every day is 12 or less, `d/M` and `M/d` cannot be told apart. The docs say so.
- `FormulaGuard` (default off): a text cell starting with `=`, `+`, `-`, `@`, tab or CR is prefixed
  with `'`, the OWASP defence against csv injection. Text only, never a number. Off by default
  because it changes the value the import reads back; an application exporting other people's data
  to be opened in Excel turns it on.

## Object layer

```csharp
static readonly TabularExport<Portfolio> Export = TabularExport.For<Portfolio>()
    .Column("Id",     p => p.Id)
    .Column("Name",   p => p.Name)
    .Column(PortfolioFields.Amount, p => p.Amount)
    .Column("Start",  p => p.Start.ToDateTime())
    .Column("Status", p => p.Status.ToString())
    .Build();
```

- A column's type comes from the lambda: overloads for `string`, `long`, `decimal`, `double`,
  `DateTime`, `DateOnly`, `bool` and their nullable forms. No overloads for `enum`, `Guid` or
  others: the caller converts explicitly.
- Overload resolution, checked by compiling: `int` resolves to `long`, `int?` to `long?`, `float`
  to `double`. Documented pitfalls: `char` silently resolves to `long` and writes the code point;
  `ulong` is ambiguous (CS0121); a method group returning `int` does not convert (CS0407); `p =>
  null` is ambiguous. Each needs an explicit conversion or cast.
- The bridge to import uses the typed fields that already exist (`TypedImportFields.cs`):
  `.Column(TextImportField, Func<T, string?>)`, `.Column(IntegerImportField, Func<T, long?>)`,
  `.Column(DecimalImportField, Func<T, decimal?>)` and `Func<T, double?>`,
  `.Column(DateImportField, Func<T, DateTime?>)` and `Func<T, DateOnly?>`,
  `.Column(BooleanImportField, Func<T, bool?>)`. A mismatch is a compile error. The header is
  `field.Name`. An untyped `ImportField` is accepted too and checked against its `Type` at
  `Build()`, with `ArgumentException`; a translated field throws there.
- Each column is an `ExportColumn<T, TValue>` holding a typed delegate: one delegate call and one
  typed `Write` per cell.
- Width: set per column, or defaulted by type (wider for date-time).
- The built export is immutable and thread-safe.

Writing:

```csharp
long rows = await Export.WriteAsync(stream, TabularFormat.Xlsx, "Portfolios", chunks, options, ct);

await using TabularWriter w = TabularWriter.Create(stream, TabularFormat.Xlsx);
await PortfolioExport.WriteSheetAsync(w, "Portfolios", portfolios, ct);
await PositionExport.WriteSheetAsync(w, "Positions", positions, ct);
await w.CompleteAsync(ct);
```

- Sources: `IAsyncEnumerable<IReadOnlyList<T>>` (chunks as they arrive), `IAsyncEnumerable<T>`,
  `IEnumerable<T>`. An async source is enumerated `WithCancellation(ct)`. The chunked overload is
  the fast one for millions of rows; the per-item async overload awaits per row, which the docs say.
- It flushes after every chunk and within a chunk whenever `FlushRecommended`.
- Returns the number of data rows written.

## Errors

`TabularWriteException : TabularException` carries the code, the sheet name, the row number (as a
spreadsheet counts it, header = 1), and the column index and header.

| Code | When |
|---|---|
| `write.precision-loss` | a `long`, `decimal` or `double` the format would not give back exactly (table above) |
| `write.not-finite` | `NaN`, `±∞` |
| `write.date-out-of-range` | xlsx: before 1900-01-01; csv: year 1 |
| `write.text-too-long` | xlsx: text over 32,767 chars |
| `write.too-many-lines` | csv: a text value with more line breaks than the reader's default accepts |
| `write.ambiguous-line-breaks` | csv: multi-line text the reader would take for a stray quote and split into records |
| `write.invalid-character` | a character XML 1.0 forbids |

More than 1,048,576 rows on an xlsx or ods sheet (header included) throws `TabularLimitException`
with `limit.exceeded`. No automatic continuation sheet: the importer reads one sheet per plan, so a
split file would not round-trip. An application that knows its row count up front picks csv for
large exports. The new codes go into `ErrorCodes` and `docs/error-codes.md`, and `write.` into
`ErrorCodes.ReservedPrefixes`.

Programmer errors throw at once, `ArgumentException` or `InvalidOperationException`: `Write` outside
a row; more cells than columns (fewer are padded with empty cells); a second csv sheet; more than
16,384 columns; any call after `CompleteAsync` or after a failure; an invalid sheet name. Sheet
names follow Excel's rules for both workbook formats: 1 to 31 characters, none of `[]:*?/\`, no
leading or trailing `'`, not `History`, and unique ignoring case.

### An incomplete file

Streaming means the start of the file is already out when row 600,000 fails.

- After an exception the writer is faulted; every later call throws.
- **A file is valid only after `CompleteAsync`.** `DisposeAsync` without it discards what is still
  buffered and does not write the trailer: the zip has no central directory and is plainly broken,
  rather than a valid-looking file of 600,000 rows.
- A truncated csv looks valid, and closing a blob write stream may commit what was written so far.
  The docs therefore say plainly: on failure, abort the response (`HttpContext.Abort()`) or delete
  the temporary file or blob — with an example for each.

The target stream is closed with `DisposeAsync`, on every path, failures included, unless
`leaveOpen` is set: a synchronous `Dispose` of a blob or network stream does synchronous I/O.

Cancellation: a token is the last parameter of `FlushAsync`, `CompleteAsync`, `WriteAsync` and
`WriteSheetAsync`; the object layer checks it per chunk and on every flush. Cancelling leaves the
file incomplete, as above.

The writer is `IAsyncDisposable` only: a synchronous `Dispose` would have to write synchronously.

### Bounds

`SpillBuffer` is the only structure that grows, and only when a low-level caller never flushes; the
object layer always does, and the docs say so for the writer. Sheet names are few and small. The
writer's limits match the reader's: 16,384 columns, 1,048,576 rows, 16M characters per value.

## Verification

- **Round trip**, per format and value kind: write through `TabularExport`, read through
  `TabularFile.Open` and `TabularImporter` with the same schema, compare values and types. Edge
  values: `0.1m`; decimals of 15 and 16 significant digits; the double `0.1 + 0.2`; `2⁵³`,
  `2⁵³ + 1`, `long.MinValue`; 1900-01-01, 1900-03-01, 9999-12-31, `DateTime.MaxValue`, midnight,
  `.999` ms, 23:59:59.9996; text with quotes, `\n`, `\r\n`, a lone `\r`, tabs, runs of spaces, `;`,
  leading zeros, `_x0001_`, emoji, umlauts; a csv field with 100 and with 101 line breaks; empty
  cells and empty rows; csv under the invariant culture, `de-DE` and `en-US`.
- **Error codes:** each fires on its boundary value and only there.
- **Valid for other programs:** xlsx through Open XML SDK's `OpenXmlValidator` in the test project
  (the independence test covers the library's graph only); ods through LibreOffice headless in CI
  (`soffice --convert-to csv` on the written file), as in the differential tests, and that dates and
  booleans display as such; Excel by hand once before the release.
- **Streaming:** a target stream that throws on any synchronous `Write`, `Flush` or `Dispose`; a
  non-seekable target; `DisposeAsync` without `CompleteAsync` gives a file our reader refuses
  (`format.corrupt` or `format.truncated`); cancellation midway.
- **Benchmarks** (BenchmarkDotNet): csv 5M rows, xlsx and ods 1M rows, in the shape of the existing
  fixtures, to `Stream.Null` and to a file; time, allocations, peak memory, which must not depend on
  the row count. Comparison against SpreadCheetah and MiniExcel (xlsx) and Sep, Sylvan and CsvHelper
  (csv); none exists for ods. `CompressionLevel.Fastest` against `Optimal` for time and size decides
  the default. One wide sheet past 4 GB uncompressed, opened in Excel and LibreOffice.

## Documentation

`docs/exporting.md` with the gRPC-chunks-to-HTTP-response and to-blob examples, failure handling
included, the overload pitfalls and the csv culture advice; README drops "Read-only"; `formats.md`,
`error-codes.md`, `bounds.md`; `PublicAPI.Unshipped.txt`; ADR-0002.

## Delivery

Separate pull requests, in order:

1. `TabularWriter`, `SpillBuffer`, csv.
2. `ZipWriter` and CRC-32, xlsx.
3. ods.
4. `TabularExport<T>` and the import-field bridge.
5. Benchmarks, comparison, documentation, ADR-0002.

Released as a feature: 0.6.0.
