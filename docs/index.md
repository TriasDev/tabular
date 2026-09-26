# TriasDev.Tabular

**Fast, low-memory reading of large Excel, OpenDocument and CSV files for .NET — with no
dependencies.**

Hand it a file and it tells you what is in it: every column's type, emptiness, uniqueness, value
ranges and the rows that do not fit — measured over every row, not a sample. Then read the data
itself, as raw cells or as typed rows through a mapping, with errors that point at the row and
column they came from.

It reads multi-million-row files in seconds while its memory stays flat as the files grow, and it is
built on the base class library alone: no third-party packages.

```bash
dotnet add package TriasDev.Tabular
```

## Choose your path

### I am adding an upload to my application

A user uploads a spreadsheet; you profile it, let them map its columns to your fields, check the
mapping, and import typed rows or precise errors.

- [Getting started](getting-started.md) — install, profile, map, check, import
- [How it works](concepts.md) — two independent reads of the file, facts against suggestions
- [Importing](importing.md) — the run, batches, rules, translated fields, the precheck
- [Error codes](error-codes.md) — every code a row error or an exception carries

### I just need to read big files fast

A forward-only cursor over rows of typed cells, for xlsx, ods, csv and zip archives of them.

- [Formats](formats.md) — what each kind of file reads as, and how malformed csv is repaired
- [Performance](performance.md) — the library's own numbers
- [Benchmarks](benchmarks.md) — against Sylvan, Sep, CsvHelper, ExcelDataReader, MiniExcel, ClosedXML, NPOI and EPPlus

### I need to know what it will refuse

- [Bounds](bounds.md) — every ceiling that protects a server from a hostile file
- [Streams, cancellation, progress and cultures](operations.md)
- [Known issues](KNOWN-ISSUES.md) — behaviour at the edges, with what would make each one matter

## For AI agents

The documentation is also published for language models, following [llms.txt](https://llmstxt.org):
[`llms.txt`](https://triasdev.github.io/tabular/llms.txt) is a short map of the library, and [`llms-full.txt`](https://triasdev.github.io/tabular/llms-full.txt) is every
page in one Markdown file. Each page is also available as Markdown next to its HTML.

## Source

[github.com/TriasDev/tabular](https://github.com/TriasDev/tabular) · MIT licence ·
[NuGet](https://www.nuget.org/packages/TriasDev.Tabular/)
