# Writing csv, xlsx and ods

Status: agreed design, 2026-10-03. Issue #67.

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
| `TabularExport<T>`, `TabularExport.For<T>()` | `TriasDev.Tabular` | object layer: columns as lambdas |
| `TabularWriteException` | `TriasDev.Tabular` | a value the format cannot hold exactly |
| `CsvWriterOptions` | `TriasDev.Tabular.Csv` | culture, delimiter, BOM, formula guard |
| `XlsxWriterOptions` | `TriasDev.Tabular.Xlsx` | compression level |
| `OdsWriterOptions` | `TriasDev.Tabular.Ods` | compression level |
| `TabularWriterOptions` | `TriasDev.Tabular` | holds the three, as `TabularOpenOptions` does for reading |

Internal: `CsvSheetWriter`, `XlsxSheetWriter`, `OdsSheetWriter`, `SpillBuffer`.

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

- `BeginSheet(string name, ReadOnlySpan<WriteColumn> columns)`; `WriteColumn` is a header text and
  an optional width (characters). Width matters for xlsx only (`<cols>` precedes the sheet data);
  without it Excel shows a date-time as `#####`.
- `Write` overloads: `string?`, `long`, `decimal`, `double`, `DateTime`, `DateOnly`, `bool`; plus
  `WriteEmpty()`. Narrower integers reach the `long` overload by implicit conversion.
- The caller picks the format explicitly; there is no detection on the write side.
- Several sheets: call `BeginSheet` again. A csv file has one sheet; a second `BeginSheet` throws.
- The stream is closed on every path, failures included, unless `leaveOpen` is set.

### Sync writes, async flush

The format writers — the csv text, the sheet XML, `ZipArchive` in create mode — write synchronously
into `SpillBuffer`, which is memory. `FlushAsync` moves the buffer to the real stream asynchronously.
This is what makes the writer usable on an ASP.NET Core response body, which refuses synchronous
writes by default, on net8.0 as well as net10.0, and on a stream that cannot seek (`ZipArchive` then
writes data descriptors). `FlushRecommended` turns true once the buffer passes a threshold of about
1 MB. The writer never writes to the target stream synchronously.

### Risk to settle first

ODF requires the `mimetype` entry first and stored (zip method 0). Whether `ZipArchive` with
`CompressionLevel.NoCompression` writes method 0 on a non-seekable stream, and whether LibreOffice
accepts the result, is checked before the ods writer is built. If it does not, the library writes
its own minimal zip writer — with its own CRC-32, since `System.IO.Hashing` is a package.

## Values and the round trip

The rule: whatever is written is read back by the import as the same value of the same type. A
value a format cannot hold exactly is an error, never a silent change.

| Value | xlsx | ods | csv | Error when |
|---|---|---|---|---|
| `string` | `t="inlineStr"`, XML-escaped; control characters as `_xHHHH_`, a literal `_x` as `_x005F_` | `<text:p>` | RFC 4180 quoting | xlsx: longer than 32,767 chars; ods: a control character |
| `long` | `<v>` | `office:value-type="float"` | invariant or culture | magnitude above 2⁵³ |
| `decimal` | `<v>`, its own digits | `float` | its own digits | `(decimal)(double)v != v` |
| `double` | `<v>`, `"R"` | `float` | `"R"` | `NaN`, `±∞` |
| `DateTime`, `DateOnly` | serial, date or date-time style | `office:date-value`, ISO | ISO, or the culture's pattern | xlsx: before 1900-03-01 |
| `bool` | `t="b"` | `boolean` | `true` / `false` | — |
| `null`, `WriteEmpty` | no `<c>` | empty cell | empty field | — |

Strings are not written as a shared-string table: it would hold every distinct string until the end.

The decimal check matches how extraction reads a number cell (`ValueReading.TryToDecimal`, a plain
`(decimal)` cast of the parsed `double`), so a value that passes the check imports equal.

Three deliberate exceptions, documented:

1. **Leading and trailing whitespace.** The reader trims text (`RawCell.FromText`); the writer writes
   text as given, so `" DE "` comes back as `"DE"`.
2. **Time is rounded to milliseconds** in every format. An xlsx serial holds no more; an error on
   finer ticks would fire on every `DateTime.Now`. One rule for all formats keeps the round trip the
   same whichever format is chosen.
3. **`DateTime.Kind` is not kept.** The wall-clock value is written; the reader returns it as
   `Unspecified`.

xlsx styling is three fixed styles: General, date, date-time. The header row is unstyled.

### csv

`CsvWriterOptions`:

- Default: delimiter `,`, `.` as decimal separator, ISO dates, UTF-8 with BOM, quoting per RFC 4180,
  `\r\n` line ends.
- `Culture`: numbers and dates in that culture's format (e.g. `de-DE` → `1234,56`, `03.10.2026`);
  the delimiter defaults to `;` when the culture's decimal separator is `,`.
- `Delimiter`: explicit override.
- `FormulaGuard` (default off): a text cell starting with `=`, `+`, `-` or `@` is prefixed with `'`,
  the OWASP defence against csv injection. Text only, never a number. Off by default because it
  changes the value the import reads back; an application exporting other people's data to be
  opened in Excel turns it on.

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
  `DateTime`, `DateOnly`, `bool` and their nullable forms. `int` resolves to the `long` overload.
  No overloads for `enum`, `Guid` or others: the caller converts explicitly.
- `.Column(ImportField field, lambda)` takes the header from `field.Name`; `Build()` checks the
  lambda's type against `field.Type` (`Decimal` accepts `decimal` and `double`, `Integer` accepts
  `long`, `Date` accepts `DateTime` and `DateOnly`, `Text` accepts `string`, `Boolean` accepts
  `bool`) and throws `ArgumentException` on a mismatch. A translated field throws too.
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
  `IEnumerable<T>`. The chunked overload is the fast one for millions of rows; the per-item async
  overload awaits per row, which the docs say.
- It flushes after every chunk and within a chunk whenever `FlushRecommended`.
- Returns the number of data rows written.

## Errors

`TabularWriteException : TabularException` carries the code, the sheet name, the row number (as a
spreadsheet counts it, header = 1), and the column index and header.

| Code | When |
|---|---|
| `write.precision-loss` | a `decimal` or `long` that `double` cannot hold exactly |
| `write.not-finite` | `NaN`, `±∞` |
| `write.date-out-of-range` | xlsx: a date before 1900-03-01 |
| `write.text-too-long` | xlsx: text over 32,767 chars |
| `write.invalid-character` | ods: a control character |

More than 1,048,576 rows on an xlsx or ods sheet (header included) throws `TabularLimitException`
with `limit.exceeded`. No automatic continuation sheet: the importer reads one sheet per plan, so a
split file would not round-trip. An application that knows its row count up front picks csv for
large exports. The new codes go into `ErrorCodes` and `docs/error-codes.md`.

Programmer errors throw at once, `ArgumentException` or `InvalidOperationException`: `Write` outside
a row; more cells than columns (fewer are padded with empty cells); a second csv sheet; a duplicate
or invalid sheet name (xlsx: at most 31 chars, none of `[]:*?/\`); more than 16,384 columns; any
call after `CompleteAsync` or after a failure.

### An incomplete file

Streaming means the start of the file is already out when row 600,000 fails.

- After an exception the writer is faulted; every later call throws.
- **A file is valid only after `CompleteAsync`.** `DisposeAsync` without it does not write the
  trailer: the zip has no central directory and is plainly broken, rather than a valid-looking
  file of 600,000 rows.
- A truncated csv looks valid. The docs therefore say plainly: on failure, abort the response
  (`HttpContext.Abort()`) or delete the temporary file or blob — with an example for each.

Cancellation: a token is the last parameter of `FlushAsync`, `CompleteAsync`, `WriteAsync` and
`WriteSheetAsync`; the object layer checks it per chunk and on every flush. Cancelling leaves the
file incomplete, as above.

The writer is `IAsyncDisposable` only: a synchronous `Dispose` would have to write synchronously.

### Bounds

`SpillBuffer` is the only structure that grows, and only when a low-level caller never flushes; the
object layer always does, and the docs say so for the writer. Sheet names are few and small.

## Verification

- **Round trip**, per format and value kind: write through `TabularExport`, read through
  `TabularFile.Open` and `TabularImporter` with the same schema, compare values and types. Edge
  values: `0.1m`; decimals of 15 and 16 significant digits; `2⁵³`, `2⁵³ + 1`, `long.MinValue`;
  1900-03-01, 9999-12-31, midnight, `.999` ms; text with quotes, line breaks, `;`, leading zeros,
  `_x0001_`, emoji, umlauts; empty cells and empty rows.
- **Error codes:** each fires on its boundary value and only there.
- **Valid for other programs:** xlsx through Open XML SDK's `OpenXmlValidator` in the test project
  (the independence test covers the library's graph only); ods through LibreOffice headless in CI
  (`soffice --convert-to csv` on the written file), as in the differential tests; Excel by hand
  once before the release.
- **Streaming:** a target stream that throws on any synchronous `Write` or `Flush`; a non-seekable
  target; `DisposeAsync` without `CompleteAsync` gives a file our reader refuses (`format.corrupt`
  or `format.truncated`); cancellation midway.
- **Benchmarks** (BenchmarkDotNet): csv 5M rows, xlsx and ods 1M rows, in the shape of the existing
  fixtures, to `Stream.Null` and to a file; time, allocations, peak memory, which must not depend on
  the row count. Comparison against SpreadCheetah and MiniExcel (xlsx) and Sep, Sylvan and CsvHelper
  (csv); none exists for ods. `CompressionLevel.Fastest` against `Optimal` for time and size decides
  the default.

## Documentation

`docs/exporting.md` with the gRPC-chunks-to-HTTP-response and to-blob examples, failure handling
included; README drops "Read-only"; `formats.md`, `error-codes.md`, `bounds.md`;
`PublicAPI.Unshipped.txt`; ADR-0002.

## Delivery

Separate pull requests, in order:

1. `TabularWriter`, `SpillBuffer`, csv; the `ZipArchive` stored-entry check for ods.
2. xlsx.
3. ods.
4. `TabularExport<T>` and the `ImportField` bridge.
5. Benchmarks, comparison, documentation, ADR-0002.

Released as a feature: 0.6.0.
