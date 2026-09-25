# TriasDev.Tabular

**Fast, low-memory reading of large Excel and CSV files for .NET — with no dependencies.**

Hand it a file and it tells you what is in it: every column's type, emptiness, uniqueness, value
ranges and the rows that do not fit — measured over every row, not a sample. Then read the data
itself, as raw cells or as typed rows through a mapping, with errors that point at the row and
column they came from.

It reads multi-million-row files in seconds while its memory stays flat as the files grow, and it is
built on the base class library alone: no third-party packages.

## In one look

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

What analysis reports about it (abridged — the full profile also carries counts under every
culture, samples and distinct values):

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

**Correct on dirty csv, where fast readers go quietly wrong.** On a 5M-row export with malformed
quoting:

| | Time | Peak | Rows read |
|---|--:|--:|---|
| **TriasDev.Tabular** | 3.9 s | **51 MB** | **all 5,127,960** — 38 repairs reported |
| CsvHelper | 3.5 s | 71 MB | 39,222 records lost, no warning |
| Sep | 4.6 s | 208 MB | 2,282,475 records lost, no warning |
| Sylvan.Data.Csv | — | — | throws |

On clean csv the field is close: Sep is fastest (1.1 s for 3M rows), TriasDev.Tabular reads the same
file in 2.2 s with the same 50 MB peak — and detects the delimiter and encoding the others must be
told.

**And it tells you what is in the file**, which none of them does — a full profile of every column,
over every row, with memory that stays flat:

| | Rows | Full analysis | Rows per second | Peak |
|---|--:|--:|--:|--:|
| 100k-row workbook (8.6 MB) | 100,000 | 1.6 s | 62,000 | 107 MB |
| 1M-row workbook (101 MB) | 1,000,000 | 7.8 s | 128,000 | 180 MB |
| 3M-row csv (364 MB) | 3,000,000 | 14.9 s | 201,000 | 134 MB |
| 5M-row csv (572 MB) | 5,127,959 | 24.6 s | 209,000 | 131 MB |

Method, every library and every number: [docs/benchmarks.md](docs/benchmarks.md).

## What it does

- **Profile a file** — per column: measured facts (empty and distinct counts, lengths, numeric and
  date ranges, how values parse under each culture, located outliers) and ranked type suggestions
  for a mapping screen to pre-select. Csv delimiter, quoting and encoding are detected.
- **Read it fast** — a forward-only cursor over rows of typed cells, for xlsx and csv alike. The
  format is detected from the file's bytes, not its name.
- **Import it through a mapping** — declare fields once with their rules (required, length, range,
  pattern, allowed values, unique), get typed rows or errors with stable codes, row by row or in
  batches. A precheck judges a mapping against the profile before anything is imported.
- **Survive hostile input** — malformed quoting is repaired and counted, every structure read from a
  file has a ceiling (zip expansion, shared strings, columns, field length), and every read honours
  cancellation, including inside a single long read.
- **Report progress** — a fraction of the file taken from the bytes read, about once per percent on
  large files, so a progress bar needs no second pass to count rows.

## Quick start

```csharp
using TriasDev.Tabular.Abstractions;
using TriasDev.Tabular.Analysis;

using FileStream file = File.OpenRead("customers.xlsx");       // or .csv — detected from its bytes
using ITabularCursor cursor = TabularFile.Open(file, "customers.xlsx");

// 1. What is in it?
FileProfile profile = new TabularAnalyzer().Analyze(cursor);

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

Importing into your own types through a confirmed mapping — schemas, rules, the precheck, chunked
imports — is described in the [guide](docs/guide.md#using-it).

## Documentation

| | |
|---|---|
| [Guide](docs/guide.md) | Everything the library does and promises: profiling, import, error codes, bounds, cancellation |
| [Benchmarks](docs/benchmarks.md) | Speed and memory against Sylvan, Sep, CsvHelper, ExcelDataReader, MiniExcel, Open XML SDK, ClosedXML, NPOI and EPPlus |
| [ADR-0001](docs/adr/0001-tabular-parsing-is-our-own-cursor.md) | Why the parsing is our own |
| [Known issues](docs/KNOWN-ISSUES.md) | What review found and we chose not to fix yet |

## Requirements

.NET 10. Support for .NET 8 is planned.

## License

[MIT](LICENSE)
