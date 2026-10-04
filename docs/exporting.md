# Exporting

The library writes csv, xlsx, ods and a zip of csv sheets, into any stream that can be written —
a file, a blob upload, an ASP.NET Core response body. The stream need not seek, and the library only
ever writes, flushes and closes it asynchronously (except that a failed `Create` disposes the stream, unless it was left open). Memory stays flat however many rows the file has:
a write goes into memory, and a flush moves about a megabyte at a time into the stream.

Every snippet on this page is taken from
[`samples/TriasDev.Tabular.Samples.Export`](https://github.com/TriasDev/tabular/blob/main/samples/TriasDev.Tabular.Samples.Export),
which CI builds with the library; `dotnet run --project samples/TriasDev.Tabular.Samples.Export` writes
each file and reads it back.

## A file is valid only after `CompleteAsync`

`TabularWriter` writes a table row by row: `Create`, `BeginSheet` (which writes the header row),
then `BeginRow`, one `Write` per column, `EndRow`, and `CompleteAsync` at the end.

```csharp
--8<-- "samples/TriasDev.Tabular.Samples.Export/RowByRow.cs:rows"
```

- `Write` has an overload per type: text, `long` (narrower integers convert), `decimal`, `double`,
  `bool`, `DateTime`, `DateOnly`, each with a `StyleId` overload, and `WriteEmpty`. `null` text is an
  empty cell.
- `FlushRecommended` turns true when about a megabyte is pending; `FlushAsync` moves it into the stream.
  Flushing and completing are the only points where the stream is written, so it is also where a slow or gone consumer
  shows.
- `CompleteAsync` ends the file. **Only then is the file valid.** A writer disposed without it, or
  after an exception, leaves an incomplete file behind: a workbook that does not open, but a csv that
  looks whole and is short. Discard it — abort the response, delete the blob or file. The library
  cannot tell a short csv from a finished one afterwards; you can, because you know whether
  `CompleteAsync` returned.
- `DisposeAsync` closes the stream unless `TabularWriterOptions.LeaveOpen` is set. It drops what is
  pending and never completes the file.
- A writer that failed (a value it refused, a cancelled flush, a stream that threw) is faulted: the
  file is incomplete and every further call throws. Start over with a new writer.
- Not thread-safe: one writer, one sequence of calls.

## An export declared once

When the rows are objects, declare the columns once. A `TabularExport<T>` is immutable and
thread-safe: keep it in a static field and use it for any number of writes at once.

```csharp
--8<-- "samples/TriasDev.Tabular.Samples.Export/Declared.cs:declare"
```

A column is a header and a lambda; the lambda's type picks the column's type (text, integer, decimal,
number, date, date-time, boolean — nullable forms write an empty cell for `null`). `width` is in
characters. `style` is a rule: it receives the cell's value and returns the style to use, or `null`
for none. Return styles declared once, as above: the same instance costs a reference compare per cell,
and a rule that builds a style per cell still writes a correct file, more slowly. A column named by
an import field (`Column(field, o => …)`) takes the field's name as its header, so the file maps back
onto the import's schema by header.

The type is picked by overload resolution, and some lambdas do not resolve; see
[Known issues](KNOWN-ISSUES.md#writing-what-does-not-come-back-exactly-as-written) for the list
(`char`, `ulong`, `Guid`, enums, `TimeSpan`, `DateTimeOffset`) and what to write instead.

`WriteAsync` writes a whole file of one sheet and returns the number of data rows written. Its
sources: `IEnumerable<T>`, `IAsyncEnumerable<T>` (awaits per item; prefer chunks for millions), and
chunks, which is the fast one:

```csharp
--8<-- "samples/TriasDev.Tabular.Samples.Export/Declared.cs:chunks"
```

The writer is flushed after every chunk, so memory holds a chunk and about a megabyte. Very small
chunks mean one write to the target each.

### A server stream

A gRPC server stream, or any source whose messages carry a repeated field, is passed as it comes;
`itemsOf` says where a message keeps its items. A protobuf `RepeatedField<T>` is an
`IReadOnlyList<T>`, so nothing is copied.

```csharp
--8<-- "samples/TriasDev.Tabular.Samples.Export/Declared.cs:itemsof"
```

Several sheets in one workbook: one writer, one `WriteSheetAsync` per sheet, then `CompleteAsync`.

```csharp
--8<-- "samples/TriasDev.Tabular.Samples.Export/Declared.cs:sheet"
```

A source that is both `IEnumerable<T>` and `IAsyncEnumerable<T>` (an EF Core `DbSet`) is ambiguous:
pass `.AsAsyncEnumerable()`.

## Data by column

A source that delivers columns — arrays per field, a columnar result — is written without turning it
into objects. `ColumnBatch` takes one list per column and writes them as rows, typed, without boxing
or copying; arrays, `List<T>` and any `IReadOnlyList<T>` work.

```csharp
--8<-- "samples/TriasDev.Tabular.Samples.Export/RowByRow.cs:batch"
```

- Reuse one batch for the whole sheet: `Reset(rowCount)` keeps its slots, so a warm batch allocates
  nothing.
- The lists must not change until the batch is written. `WriteBatchAsync` flushes inside the batch,
  so memory stays flat however large it is; `WriteBatch` does not flush.
- A column's style is a constant `StyleId`, a list of ids by row, or a rule on the value.
- A batch must have as many columns as the sheet has.

## Styles

Styles apply to xlsx and ods; csv ignores them, so one code path writes every format.

```csharp
--8<-- "samples/TriasDev.Tabular.Samples.Export/RowByRow.cs:styles"
```

A `CellStyle` sets any of: `Fill` and `Font` (colour, bold, italic) with `CellColor` (`Parse("#RRGGBB")`
or `FromRgb`), `Number`, `Date`, `Horizontal` (`General`, `Left`, `Center`, `Right`), `Wrap`, and
`Border` (`CellBorder.Thin(color)`). Unset means the format's default. Styles compare by value, and a
file holds each distinct style once, at most 4,096.

`writer.Style(style)` registers a style with that writer and returns a `StyleId` for the `Write`
overloads. A `StyleId` belongs to the writer that returned it; another writer refuses it. Register
each style once per writer.

Format codes are a subset of Excel's, parsed by `NumberFormat.Parse` and `DateFormat.Parse`, and
refused there — when you build the style, not when the file is written — if they are outside it:

| | Supported |
|---|---|
| `NumberFormat` | `0`, `0.00`, `#,##0`, `#,##0.00`, `0.0#`, `0%`, and quoted text before or after, as in `"€ "#,##0.00` |
| `DateFormat` | `yyyy` `yy` `m` `mm` `d` `dd` `h` `hh` `s` `ss`, separators `/ - . : , space`, quoted or backslash-escaped text, as in `dd/mm/yyyy` or `yyyy-mm-dd hh:mm` |

Month and weekday names are not supported. A date format shows its separators as written; a number
format's decimal point and thousands separator follow the reader's locale.

## Sheet layout

`SheetOptions` goes with `BeginSheet`: a `HeaderStyle`, `FreezeRows` and `FreezeColumns` (how many
stay in view), and `AutoFilter` on the header row through the last row written. Csv ignores it. A
declared export's methods take no `SheetOptions`; its header row is unstyled. For a styled, frozen
or filtered header, write with `TabularWriter` as in the first example.

`writer.Merge(rows, columns)` makes the next cell the top-left of a merged range. The writer skips
the covered positions — the next write in the row lands after the range, and later rows skip it
too — and writes them itself.

```csharp
--8<-- "samples/TriasDev.Tabular.Samples.Export/RowByRow.cs:merge"
```

A merge must end inside the sheet: ending the sheet or the file while a range still has rows to cover
is refused. The import reads a merged range as its value in the top-left cell and empty cells
elsewhere, and csv writes exactly that. A batch writes plain rows, so end a range before it.

## A zip of csv sheets

`TabularFormat.Zip` writes each sheet as `<sheet name>.csv` in one zip, each exactly the csv file
that sheet alone would be. Use it for several sheets past the xlsx row limit, or wherever csv is wanted
but one file holds several tables.

```csharp
--8<-- "samples/TriasDev.Tabular.Samples.Export/RowByRow.cs:zip"
```

Sheet names follow the workbook rules and, since they become file names, may not hold `< > " |` or
end with a dot or a space. The zip is written by the library's own streaming writer, with zip64 past
4 GB. Read back, the sheets come in the order of the entries' paths, not of writing.

## Csv

`CsvWriterOptions` (in `TabularWriterOptions.Csv`) sets:

- `Culture` — the culture numbers and dates are written in; none writes the invariant culture,
  ISO dates. A culture is accepted only if a probe of its numbers and dates reads back through the
  import's own reader.
- `Delimiter` — `,` `;` tab or `|`. Left unset it is `;` when the culture's decimal separator is a
  comma, otherwise `,`.
- `ByteOrderMark` — on by default; without it Excel reads the file in the system's code page and
  mangles umlauts.
- `FormulaGuard` — prefixes text starting with `=`, `+`, `-`, `@`, tab or CR with an apostrophe, so
  a spreadsheet does not run it as a formula. Off by default, because it changes the value read back.
  Turn it on when exporting other people's data to be opened in a spreadsheet.

`XlsxWriterOptions`, `OdsWriterOptions` and `ZipWriterOptions` each set a `CompressionLevel`
(`Fastest` by default; `Optimal` writes a smaller file more slowly).

## Where the file goes

### An ASP.NET Core response

The response body refuses synchronous writes; the library writes to it asynchronously only, so it can
be passed straight in. Set the headers first: the first flush starts the response, and headers cannot
change after it. Set `LeaveOpen`, because the server owns the body.

```csharp
--8<-- "samples/TriasDev.Tabular.Samples.Export/Endpoint.cs:endpoint"
```

When the client goes away, the request token is cancelled, the write stops at the next flush and the
response is incomplete. Once the response has started, a status code cannot be changed; cut the
connection (`HttpContext.Abort()`), so the client sees a failed download and not a short file. A
failure before the first flush leaves the response unstarted, and an error status can still be sent.
The write is async only: do not call the synchronous `Write` family on the body yourself.

### A blob or a file

```csharp
--8<-- "samples/TriasDev.Tabular.Samples.Export/Program.cs:blob"
```

Any writable stream works. If the write fails, the blob holds an incomplete file: delete it, or upload
it to a temporary name and rename on success.

## Failures

```csharp
--8<-- "samples/TriasDev.Tabular.Samples.Export/Program.cs:failure"
```

- `TabularWriteException` — a value the chosen format cannot hold exactly. `Code` is one of
  `write.precision-loss`, `write.not-finite`, `write.date-out-of-range`, `write.text-too-long`,
  `write.too-many-lines`, `write.ambiguous-line-breaks`, `write.invalid-character`
  ([error codes](error-codes.md)); `SheetName`, `RowNumber`, `ColumnIndex` (counted from zero) and `Header` say where. The
  file is incomplete.
- `TabularLimitException` — a bound of the format: more rows than an xlsx or ods sheet holds
  (1,048,576, the header included), more than 4,096 styles, more than 65,536 merges in an xlsx sheet.
  The rows before it were written; choose csv or a zip when the count may exceed the row limit. See
  [bounds](bounds.md).
- A bad argument — a header that is empty, padded or repeated ignoring case, a sheet name the format
  refuses, a column count outside 1 to 16,384 — throws `ArgumentException`, and a call out of order
  (a row inside a row, `CompleteAsync` with a row open) throws `InvalidOperationException`. The same
  data fails the same way whichever format is chosen: the characters XML forbids are refused in csv
  too.
- Cancelling a flush, or a stream that throws, faults the writer.

An exception from the writer, or from your lambdas and rules inside it, leaves the writer faulted. Drop
the file; do not try to complete it.

## What does not come back exactly as written

Text is trimmed on reading, time is cut to milliseconds, a `DateTime` loses its `Kind`, a `double`
comes back as a decimal, and other programs show some dates and long numbers differently. Each case,
and the other writing caveats, is in
[Known issues](KNOWN-ISSUES.md#writing-what-does-not-come-back-exactly-as-written); what each format
holds is in [Formats](formats.md#what-each-format-holds-when-written).
