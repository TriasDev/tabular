# TriasDev.Tabular

**Fast, low-memory reading and writing of csv, Excel (.xlsx) and OpenDocument (.ods) files for
.NET — with no dependencies.**

The library has two halves:

- **Import** — reads xlsx, ods and csv, alone or inside zip, tar and gzip archives; profiles every
  column over every row (types, emptiness, uniqueness, ranges, the rows that do not fit); imports
  typed rows through a column mapping a person confirmed, with errors that name their row, column
  and code.
- **Export** — writes csv, xlsx, ods and a zip of csv sheets, streaming and asynchronously into any
  stream — a file, a blob, an ASP.NET Core response — with typed columns, cell styles, frozen
  headers and auto-filters. What it writes reads back through its own import; a value a format
  cannot hold exactly is refused, never rounded.

Multi-million-row files are read in seconds, memory stays flat in both directions as files grow,
and everything is built on the base class library alone: no third-party packages.

```bash
dotnet add package TriasDev.Tabular
```

**Reading** — declare the fields once, profile the file, build a mapping plan from its headers; then
[import](importing.md) typed rows or located errors through it:

```csharp
--8<-- "samples/TriasDev.Tabular.Samples.Import/Program.cs:schema"
```

```csharp
--8<-- "samples/TriasDev.Tabular.Samples.Import/Program.cs:profile"
```

**Writing** — declare the columns once, with a style rule and a frozen, filtered header, and stream
chunks of objects into a workbook ([exporting](exporting.md)):

```csharp
--8<-- "samples/TriasDev.Tabular.Samples.Export/Declared.cs:declare"
```

```csharp
--8<-- "samples/TriasDev.Tabular.Samples.Export/Declared.cs:chunks"
```

## Choose your path

### I am adding an upload to my application

A user uploads a spreadsheet; you profile it, let them map its columns to your fields, check the
mapping, and import typed rows or precise errors.

- [Getting started](getting-started.md) — install, profile, map, check, import; write a file
- [How it works](concepts.md) — two independent reads of the file, facts against suggestions
- [Importing](importing.md) — the run, batches, rules, translated fields, field alternatives, the precheck and the review
- [Error codes](error-codes.md) — every code a row error or an exception carries

### I just need to read big files fast

A forward-only cursor over rows of typed cells, for xlsx, ods, csv, zip and tar archives of them, and gzip-compressed files.

- [Formats](formats.md) — what each kind of file reads as, and how malformed csv is repaired
- [Performance](performance.md) — the library's own numbers
- [Benchmarks](benchmarks.md) — against Sylvan, Sep, CsvHelper, ExcelDataReader, MiniExcel, ClosedXML, NPOI and EPPlus

### I need to hand files back

Csv, xlsx, ods or a zip of csv sheets, written row by row, from objects declared once, or by column —
styled, streamed into a response or a blob, in flat memory.

- [Exporting](exporting.md) — the writer, declared exports, data by column, styles, sheet layout, where the file goes, failures
- [Formats](formats.md#what-each-format-holds-when-written) — what each format holds when written
- [Benchmarks](benchmarks.md#writing) — against CsvHelper, Sep, Sylvan, LargeXlsx, SpreadCheetah and MiniExcel

### I need to know what it will refuse

- [Bounds](bounds.md) — every ceiling that protects a server from a hostile file, and every limit a written file is held to
- [Streams, cancellation, progress and cultures](operations.md)
- [Error codes](error-codes.md) — including the `write.*` codes for a value a format cannot hold
- [Known issues](KNOWN-ISSUES.md) — behaviour at the edges, with what would make each one matter, and what does not come back exactly as written

## For AI agents

The documentation is also published for language models, following [llms.txt](https://llmstxt.org):
[`llms.txt`](https://triasdev.github.io/tabular/llms.txt) is a short map of the library, and [`llms-full.txt`](https://triasdev.github.io/tabular/llms-full.txt) is every
page in one Markdown file. Each page is also available as Markdown next to its HTML.

## Source

[github.com/TriasDev/tabular](https://github.com/TriasDev/tabular) · MIT licence ·
[NuGet](https://www.nuget.org/packages/TriasDev.Tabular/)
