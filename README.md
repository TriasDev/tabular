# TriasDev.Tabular

[![NuGet](https://img.shields.io/nuget/v/TriasDev.Tabular.svg)](https://www.nuget.org/packages/TriasDev.Tabular/)
[![Build Status](https://img.shields.io/github/actions/workflow/status/TriasDev/tabular/ci.yml?branch=main)](https://github.com/TriasDev/tabular/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/TriasDev/tabular/blob/main/LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8%20%7C%2010-purple)](https://dotnet.microsoft.com/download)

**Fast, low-memory reading and writing of csv, Excel (.xlsx) and OpenDocument (.ods) files for .NET — with no dependencies.**

Two halves, one contract between them:

- **Import** — reads xlsx, ods and csv, alone or inside zip, tar and gzip archives; profiles every
  column over every row; imports typed rows through a column mapping a person confirmed, with errors
  that name their row, column and code.
- **Export** — writes csv, xlsx, ods and a zip of csv sheets, streaming and asynchronously into any
  stream (an ASP.NET Core response, a blob), with typed columns, cell styles, frozen headers and
  auto-filters. What it writes reads back through its own import; a value a format cannot hold
  exactly is refused, never rounded.

Multi-million-row files are read in seconds, and memory stays flat in both directions as files grow.
Built on the base class library alone: no third-party packages.

Use it when users upload spreadsheets into your application and when it hands them back as
downloads — or when you simply need to stream a multi-million-row file in or out of .NET quickly.

**Reading** — declare the fields once, profile the file, and build a mapping plan from its headers
(then `TabularImporter.Import` turns rows into your type, or into errors with a location and a code):

```csharp
// The fields, declared once: they build the schema and read the values.
TextImportField name = ImportField.Text("name").Require().MaxLength(100);
TextImportField country = ImportField.Text("country").ExactLength(2);
DateImportField signedOn = ImportField.Date("signed_on");
DecimalImportField amount = ImportField.Decimal("amount").Require();
ImportSchema schema = new() { Fields = [name, country, signedOn, amount] };
```

```csharp
// 1. Profile the file, and build the plan from its headers.
FileProfile profile;
using (FileStream file = File.OpenRead(path))
using (ITabularCursor cursor = TabularFile.Open(file, Path.GetFileName(path)))
{
    profile = TabularAnalyzer.Analyze(cursor);
}

MappingPlan plan = MappingPlan.ByHeader(profile.Sheets[0], schema, culture: "de-DE");
```

**Writing** — declare the columns once, with a style rule and a frozen, filtered header, and stream
chunks of objects into a workbook:

```csharp
private static readonly CellStyle Late = new() { Fill = CellColor.Parse("#FFC7CE") };

private static readonly CellStyle Header = new() { Fill = CellColor.Parse("#1F4E78"), Font = new CellFont { Bold = true, Color = CellColor.Parse("#FFFFFF") } };

// Declared once, kept in a static field, shared by any number of concurrent writes.
public static readonly TabularExport<Order> Export = TabularExport.For<Order>()
    .Column("Id", o => o.Id, width: 8)
    .Column("Customer", o => o.Customer, width: 28)
    .Column("Placed", o => o.Placed)
    .Column("Amount", o => o.Amount, width: 12)
    .Column("Status", o => o.Status, width: 10, style: status => status == "late" ? Late : null)
    .Column("Paid", o => o.Paid)
    .Sheet(new SheetOptions { HeaderStyle = Header, FreezeRows = 1, AutoFilter = true })
    .Build();
```

```csharp
public static async Task<long> WriteChunksAsync(Stream stream, IAsyncEnumerable<IReadOnlyList<Order>> chunks, CancellationToken cancellationToken) =>
    await Export.WriteAsync(stream, TabularFormat.Xlsx, "Orders", chunks, cancellationToken: cancellationToken);
```

Both are taken from samples the build compiles:
[`Samples.Import`](https://github.com/TriasDev/tabular/blob/main/samples/TriasDev.Tabular.Samples.Import/Program.cs)
and [`Samples.Export`](https://github.com/TriasDev/tabular/blob/main/samples/TriasDev.Tabular.Samples.Export/Declared.cs).
The [documentation](https://triasdev.github.io/tabular/) has the rest:
[importing](https://triasdev.github.io/tabular/importing/), [exporting](https://triasdev.github.io/tabular/exporting/),
formats, bounds and every error code.

## What a profile says

A csv file, as someone might export it — German number format, a date column with a stray value,
an empty amount:

```text
id;name;country;signed_on;amount;active
1001;Contoso Ltd;DE;2024-01-15;1.250,00;true
1002;Fabrikam GmbH;AT;2024-02-03;980,50;true
1003;Northwind;CH;2024-02-29;12.400,75;false
1004;Tailspin AG;DE;2024-03-11;;true
1005;Adventure Works;US;n/a;3.100,00;true
1006;Wide World Importers;US;2024-04-02;45.000,00;false
```

What analysis reports about it — the output of
[`samples/TriasDev.Tabular.Samples.Profile`](https://github.com/TriasDev/tabular/blob/main/samples/TriasDev.Tabular.Samples.Profile), abridged. The
profile itself is an object model, not this JSON; the sample prints what a mapping screen would show
first, and the full profile also carries counts under every culture, samples and distinct values:

```jsonc
{
  "format": "csv", "delimiter": ";", "encoding": "utf-8", "rows": 6,
  "columns": [
    {
      "name": "id",
      "type": "integer",
      "confidence": 1,
      "alsoFits": ["decimal", "text"],
      "empty": 0,
      "distinct": 6,
      "unique": true,
      "min": 1001,
      "max": 1006
    },
    {
      "name": "signed_on",
      "type": "date",
      "confidence": 0.83,
      "outliers": [{"row": 6, "value": "n/a"}],
      "alsoFits": ["text"],
      "empty": 0,
      "distinct": 6,
      "unique": true,
      "min": "2024-01-15",
      "max": "2024-04-02"
    },
    {
      "name": "amount",
      "type": "decimal",
      "culture": "de-DE",
      "confidence": 1,
      "alsoFits": ["text"],
      "empty": 1,
      "distinct": 5,
      "unique": false,
      "min": 980.50,
      "max": 45000.00
    },
    {
      "name": "active",
      "type": "boolean",
      "confidence": 1,
      "alsoFits": ["text"],
      "empty": 0,
      "distinct": 2,
      "unique": false
    }
    // name and country: text, 6 and 4 distinct values
  ]
}
```

`signed_on` is a date column in five rows of six, and the one that is not is named with its row.
`amount` reads only under German conventions, so the culture is part of the answer; `id` and the
ISO dates read the same under any culture, so none is claimed. Reproduce it with
`dotnet run --project samples/TriasDev.Tabular.Samples.Profile -- samples/customers.csv`.

## Why

**The fastest xlsx reader we measured**, and in the same memory band as the other streaming readers:

| | TriasDev.Tabular | Fastest alternative | Object-model libraries |
|---|--:|--:|--:|
| 1M-row workbook (101 MB) | **4.3 s, 125 MB peak** | Sylvan.Data.Excel: 5.3 s, 126 MB | ClosedXML 42 s / 2.3 GB · NPOI 47 s / 11.7 GB |
| 100k-row workbook (8.6 MB) | **0.8 s, 96 MB peak** | Sylvan.Data.Excel: 2.1 s, 95 MB | EPPlus 3.0 s / 502 MB · NPOI 5.6 s / 1.7 GB |

**Correct on dirty csv, where fast readers go quietly wrong — and the fastest there too.** On a 5M-row export with malformed
quoting — 5,127,969 lines, one record each:

| | Time | Peak | Rows read |
|---|--:|--:|---|
| **TriasDev.Tabular** | **2.7 s** | **51 MB** | **all 5,127,969** — 49 repairs reported |
| CsvHelper | 3.5 s | 71 MB | 39,231 records merged into others, no warning |
| Sep | 4.6 s | 208 MB | 2,282,484 records merged into others, no warning |
| Sylvan.Data.Csv | — | — | throws |

On clean csv the field is close: Sep is fastest (1.1 s for 3M rows), TriasDev.Tabular reads the same
file in 1.8 s with the same 50 MB peak, its own delimiter and encoding detection included — the
comparison hands every other reader the delimiter.

**And it tells you what is in the file**, which none of them does — a full profile of every column,
over every row, with memory that stays flat:

| | Rows | Full analysis | Rows per second | Peak |
|---|--:|--:|--:|--:|
| 100k-row workbook (8.6 MB) | 100,000 | 1.5 s | 66,000 | 106 MB |
| 1M-row workbook (101 MB) | 1,000,000 | 6.7 s | 149,000 | 188 MB |
| 3M-row csv (364 MB) | 3,000,000 | 9.6 s | 313,000 | 122 MB |
| 5M-row csv (572 MB) | 5,127,968 | 14.0 s | 366,000 | 122 MB |

**Writing, in flat memory too:** 5M rows × 30 columns of csv in 9.4 s at a 57 MB peak, and a
million-row xlsx in 4.8 s at 55 MB, styled or not — the least memory of the xlsx writers measured.
Sylvan.Data.Csv is faster on csv, and Sep on the wide file (level on the narrow one). On the narrow
xlsx files SpreadCheetah and LargeXlsx with cell references off are level with it or up to 6% faster,
and on the wide one up to 27% faster. CsvHelper, MiniExcel and LargeXlsx at its default are slower.
No other library measured writes ods or a zip of csv sheets.

Method, every library and every number: [docs/benchmarks.md](https://github.com/TriasDev/tabular/blob/main/docs/benchmarks.md).

## What it does

- **Profile a file** — per column: measured facts (empty and distinct counts, lengths, numeric and
  date ranges, how values parse under each culture, located outliers) and ranked type suggestions
  for a mapping screen to pre-select. Csv delimiter, quoting and encoding are detected.
- **Read it fast** — a forward-only cursor over rows of typed cells, for xlsx, OpenDocument (.ods) and csv alike. The
  format is detected from the file's bytes, not its name.
- **Read a zip or tar archive as one workbook** — plain or gzipped; every csv, xlsx and ods file inside becomes a sheet, named by its
  path; a zipped csv is read straight out of the archive, never unpacked, at the cost of decompression
  and nothing more.
- **Read a gzip file as the file inside it** — `.csv.gz` decompressed as it is read; a cut-off or
  damaged file is refused, never read as a shorter one.
- **Import it through a mapping** — declare fields once with their rules (required, length, range,
  pattern, allowed values, unique, or your own — check digits such as ISIN and LEI included), get
  typed rows or errors with stable codes, row by row or in batches. A precheck judges a mapping against the profile before anything is imported.
- **Survive hostile input** — malformed quoting is repaired and counted, every structure read from a
  file has a ceiling (zip expansion, shared strings, columns, field length), and every read honours
  cancellation, including inside a single long read.
- **Report progress** — a fraction of the file taken from the bytes read, about once per percent on
  large files, so a progress bar needs no second pass to count rows.
- **Write csv, xlsx, ods or a zip of csv sheets** — row by row (`TabularWriter`), from objects
  declared once (`TabularExport<T>`, from lists, async streams or chunks), or by column
  (`ColumnBatch`, thousands of columns). Asynchronous towards the target and flushed a megabyte at
  a time, so it can go straight into an ASP.NET Core response or a blob upload, which need not seek.
- **Style and lay out a workbook** — fill, font, number and date formats, alignment, wrap and border
  per cell or per column rule; a header style, frozen rows and columns, an auto-filter and merged
  cells per sheet. Csv ignores styles and layout, so one code path writes every format; a merged range
  is written there as its value once and empty fields around it.
- **Write only what reads back** — every value is checked against what the format holds: a double past
  15 significant digits, a date xlsx cannot store, text too long or holding a character XML forbids
  is refused with an error naming its sheet, row and column, never rounded or cut.

## Quick start

```bash
dotnet add package TriasDev.Tabular
```

```csharp
using TriasDev.Tabular;

using FileStream file = File.OpenRead("customers.xlsx");       // or .csv — detected from its bytes
using ITabularCursor cursor = TabularFile.Open(file, "customers.xlsx");

// 1. What is in it?
FileProfile profile = TabularAnalyzer.Analyze(cursor);

foreach (ColumnProfile column in profile.Sheets[0].Columns)
{
    TypeHypothesis? best = column.Hypotheses.FirstOrDefault();
    Console.WriteLine($"{column.Facts.Header}: {best?.Type} ({best?.Confidence:P0}), "
        + $"{column.Facts.EmptyCount} empty, unique: {column.Facts.IsUnique}");
}
```

```csharp
// 2. Read the data itself, fast.
using FileStream file = File.OpenRead("big.csv");
using ITabularCursor cursor = TabularFile.Open(file, "big.csv");

while (cursor.ReadRow())
{
    ReadOnlySpan<RawCell> row = cursor.CurrentRow;             // valid until the next ReadRow
    string? first = row.Length > 0 ? row[0].AsText() : null;
}
```

```csharp
// 3. Import it into your own type: fields declared once, a plan from the headers, a check, typed rows.
TextImportField name = ImportField.Text("name").Require().MaxLength(100);
DecimalImportField amount = ImportField.Decimal("amount").Require();
ImportSchema schema = new() { Fields = [name, amount] };

MappingPlan plan = MappingPlan.ByHeader(profile.Sheets[0], schema, culture: "de-DE");
PrecheckResult check = MappingPrecheck.Check(plan, schema, profile);   // before reading the file again

using FileStream again = File.OpenRead("customers.xlsx");
using ImportRun<Customer> run = TabularImporter.Import(again, "customers.xlsx", plan, schema,
    row => new Customer(row[name]!, row[amount]!.Value));

foreach (ImportOutcome<Customer> outcome in run.ReadRows())
{
    if (outcome.HasErrors)
        Console.WriteLine($"row {outcome.RowNumber}: {outcome.Errors[0].Code}");   // e.g. value.required
    else
        Save(outcome.Value);
}
```

The same flow, runnable, with its output: [`samples/TriasDev.Tabular.Samples.Import`](https://github.com/TriasDev/tabular/blob/main/samples/TriasDev.Tabular.Samples.Import).
Batches, the full rule set, translated fields and every error code are in the [documentation](https://triasdev.github.io/tabular/importing/).

## Writing

The other direction: csv, xlsx, ods and a zip of csv sheets, into any stream, written asynchronously
and flushed as it goes, so memory stays flat however many rows the file has. An xlsx with a styled,
frozen header, number and date formats, and a legend that marks late orders:

```csharp
// Styles are declared once. A style compares by value, so the file holds each one once.
private static readonly CellStyle HeaderStyle = new()
{
    Fill = CellColor.Parse("#1F4E78"),
    Font = new CellFont { Color = CellColor.Parse("#FFFFFF"), Bold = true },
};

private static readonly CellStyle MoneyStyle = new() { NumberFormat = NumberFormat.Parse("#,##0.00") };
private static readonly CellStyle DateStyle = new() { DateFormat = DateFormat.Parse("dd/mm/yyyy") };

// A legend: late orders are marked in red.
private static readonly CellStyle LateStyle = new() { Fill = CellColor.Parse("#FFC7CE") };

public static async Task WriteAsync(Stream stream, IEnumerable<Order> orders, CancellationToken cancellationToken)
{
    // Disposing the writer closes the stream. Without CompleteAsync the file stays incomplete.
    await using TabularWriter writer = TabularWriter.Create(stream, TabularFormat.Xlsx);

    writer.BeginSheet(
        "Orders",
        [new WriteColumn("Id", 8), new WriteColumn("Customer", 28), new WriteColumn("Placed", 12), new WriteColumn("Amount", 12), new WriteColumn("Status", 10)],
        new SheetOptions { HeaderStyle = HeaderStyle, FreezeRows = 1, AutoFilter = true });

    StyleId money = writer.RegisterStyle(MoneyStyle);
    StyleId date = writer.RegisterStyle(DateStyle);
    StyleId late = writer.RegisterStyle(LateStyle);

    foreach (Order order in orders)
    {
        writer.BeginRow();
        writer.Write(order.Id);
        writer.Write(order.Customer);
        writer.Write(order.Placed, date);
        writer.Write(order.Amount, money);
        writer.Write(order.Status, order.Status == "late" ? late : default);
        writer.EndRow();

        // Writes go into memory; this moves them to the stream once about a megabyte is pending.
        if (writer.FlushRecommended)
        {
            await writer.FlushAsync(cancellationToken);
        }
    }

    await writer.CompleteAsync(cancellationToken);
}
```

Objects are declared once with `TabularExport<T>` (a style rule per column, chunks or a server stream
as the source); data that arrives by column goes through `ColumnBatch`. The file is valid only after
`CompleteAsync`; one that is not completed is incomplete, and the caller discards it. The runnable
version, with an ASP.NET Core endpoint, is
[`samples/TriasDev.Tabular.Samples.Export`](https://github.com/TriasDev/tabular/blob/main/samples/TriasDev.Tabular.Samples.Export);
the guide is [Exporting](https://triasdev.github.io/tabular/exporting/).

## Limits

Stated here so they are found before they are hit:

- **Writing.** csv, xlsx, ods and a zip of csv sheets, asynchronous towards the target; styles and layout apply to xlsx and ods only. An xlsx or ods sheet holds 1,048,576 rows (the header included), a csv sheet 2,147,483,647 (the largest row number the reader gives), a sheet at most 16,384 columns, a file 4,096 distinct styles, an xlsx sheet 65,536 merged ranges; text is limited to 32,767 characters in xlsx, and a date before 1900-01-01 is refused in xlsx. A value a format cannot hold exactly is refused with a located `TabularWriteException`, not rounded. A writer that fails leaves an incomplete file, which the caller discards. [Known limitations](https://github.com/TriasDev/tabular/blob/main/docs/KNOWN-ISSUES.md#writing-what-does-not-come-back-exactly-as-written) lists what does not come back exactly as written.
- **xlsx, ods and csv only, alone, zipped, tarred or gzipped.** Legacy `.xls`, binary `.xlsb` and flat OpenDocument
  `.fods` are refused as `format.unsupported` rather than misread; inside an archive they are skipped
  and listed. Archives inside archives are not opened.
- **Reading is synchronous, over seekable streams.** Parsing is processor work over a buffered stream; a request
  body or blob stream is copied to a file or `MemoryStream` first.
- **Cultures.** Analysis tries `""` (invariant), `de-DE` and `en-US` by default — set
  `AnalysisOptions.Cultures` for files from elsewhere. Under invariant globalization (slim container
  images) only the invariant culture exists, and a plan naming another is refused.
- **Multi-line quoted csv fields** are bounded (a hundred lines by default). A quote that spans lines and
  closes within less than a whole record is taken at its word, as RFC 4180 says.

## Stability

Until 1.0, a minor version (0.x) may change the public API; every such change is listed in the
changelog. Error codes are the exception: once published, a code keeps its meaning.

## Documentation

| | |
|---|---|
| [Documentation](https://triasdev.github.io/tabular/) | Everything the library does and promises: getting started, profiling, import, export, formats, error codes, bounds, cancellation |
| [Exporting](https://triasdev.github.io/tabular/exporting/) | Writing csv, xlsx, ods and zip files: the writer, declared exports, data by column, styles, layout, ASP.NET Core responses, failures |
| [For AI agents](https://triasdev.github.io/tabular/llms.txt) | The same documentation for language models: [`llms.txt`](https://triasdev.github.io/tabular/llms.txt) and [`llms-full.txt`](https://triasdev.github.io/tabular/llms-full.txt) |
| [Benchmarks](https://triasdev.github.io/tabular/benchmarks/) | Reading: speed and memory against Sylvan, Sep, CsvHelper, ExcelDataReader, MiniExcel, Open XML SDK, ClosedXML, NPOI and EPPlus. Writing: against CsvHelper, Sep, Sylvan, LargeXlsx, SpreadCheetah and MiniExcel |
| [ADR-0001](https://github.com/TriasDev/tabular/blob/main/docs/adr/0001-tabular-parsing-is-our-own-cursor.md) | Why the parsing is our own |
| [ADR-0002](https://github.com/TriasDev/tabular/blob/main/docs/adr/0002-writing-is-our-own.md) | Why the writing is our own |
| [Known limitations](https://github.com/TriasDev/tabular/blob/main/docs/KNOWN-ISSUES.md) | Behaviour at the edges not changed yet, and what would make each one matter |
| [Contributing](https://github.com/TriasDev/tabular/blob/main/CONTRIBUTING.md) | Building, testing, the invariants a change must keep |
| [Security](https://github.com/TriasDev/tabular/blob/main/SECURITY.md) | What counts as a vulnerability in a file reader and writer, and how to report one privately |

## Requirements

.NET 8 or .NET 10 — the package targets both LTS releases. The numbers above are measured on .NET 10. No reflection and no dynamic code: it is trim-safe and works under Native AOT.

## License

[MIT](https://github.com/TriasDev/tabular/blob/main/LICENSE)
