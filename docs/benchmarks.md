# Benchmarks

TriasDev.Tabular against the libraries a .NET developer would otherwise reach for, reading the same
large files on the same machine under the same harness.

**In short:** on workbooks it is the fastest reader measured, with a peak memory in the same band as
the other streaming readers and a fraction of what the object-model libraries need. On clean csv it
is in the middle of a field that is close together. On a malformed csv it loses far fewer records
of all 5,127,969, where the others merge tens of thousands or millions into their neighbours — and
says what it repaired.

## What the numbers mean

- **Time** — wall clock for opening the file and reading every cell of its first sheet.
- **Peak memory** — the most memory the process held at any moment (peak resident set). This is the
  number that decides whether an import fits in the container or server it runs on, and it is the
  one to compare first.
- **Allocated** — everything the garbage collector handed out over the whole run. Memory is reused
  many times over, so this is far larger than the peak: reading the 5M-row csv allocates about
  2.7 GB while never holding more than about 50 MB. It measures pressure on the garbage collector,
  which shows up in the time, not how much memory a host needs.
- **Rows** — rows the library reported, the header included. Where a library reports a different
  number from TriasDev.Tabular on the same file, the note says by how much.

## How it was measured

- Every library does the same work: read the first sheet from start to end and take every cell's
  value as text, each in the idiom its documentation recommends for speed — Open XML SDK in its
  streaming (SAX) mode, CsvHelper through its parser rather than record mapping, Sep with unescaping
  on.
- Text, because that is what an import consumes and what every library can hand over. A library that
  returns typed values pays for formatting them; the bias favours the string-returning readers,
  TriasDev.Tabular included.
- The csv libraries other than TriasDev.Tabular do not detect a dialect, so they are given the
  delimiter. TriasDev.Tabular detects it.
- Each measurement runs in a fresh process — peak memory only ever rises within a process — and is
  repeated three times; the tables show the median.
- Versions are the latest that are free to use commercially: **EPPlus 4.5.3.3** is the last LGPL
  release (5.0 onwards is under the Polyform Noncommercial licence), and **NPOI 2.7.6** the last
  under Apache-2.0 (from 2.8.0 the NuGet package ships under a maintenance-fee agreement).

Machine: Apple M1 Max, 10 cores, 64 GB, macOS 26.7, .NET 10.0.9, workstation GC. Measured
2026-09-25.

## Workbooks

### 100k-row workbook — 8.6 MB, 17 columns

| Library | Version | Time | Peak memory | Allocated |
|---|---|--:|--:|--:|
| **TriasDev.Tabular** | 0.1.0 | **0.81 s** | 96 MB | 61 MB |
| Sylvan.Data.Excel | 0.5.8 | 2.11 s | 95 MB | 60 MB |
| ExcelDataReader | 3.9.0 | 2.30 s | 92 MB | 456 MB |
| EPPlus | 4.5.3.3 | 2.95 s | 502 MB | 1,834 MB |
| DocumentFormat.OpenXml (SAX) | 3.5.1 | 3.09 s | 103 MB | 1,359 MB |
| MiniExcel | 1.46.0 | 3.88 s | 76 MB | 1,698 MB |
| NPOI | 2.7.6 | 5.62 s | 1,747 MB | 2,607 MB |
| ClosedXML | 0.105.1 | 5.66 s | 456 MB | 2,501 MB |

All read 100,001 rows and the same 1,441,441 values.

### 1M-row workbook — 101 MB, 17 columns

| Library | Version | Time | Peak memory | Allocated |
|---|---|--:|--:|--:|
| **TriasDev.Tabular** | 0.1.0 | **4.30 s** | 125 MB | 326 MB |
| Sylvan.Data.Excel | 0.5.8 | 5.26 s | 126 MB | 324 MB |
| ExcelDataReader | 3.9.0 | 9.46 s | 118 MB | 3,381 MB |
| EPPlus | 4.5.3.3 | 13.86 s | 1,640 MB | 12,617 MB |
| DocumentFormat.OpenXml (SAX) | 3.5.1 | 17.47 s | 134 MB | 10,365 MB |
| MiniExcel | 1.46.0 | 19.58 s | 74 MB | 15,100 MB |
| ClosedXML | 0.105.1 | 41.71 s | 2,287 MB | 20,957 MB |
| NPOI | 2.7.6 | 47.19 s | 11,743 MB | 17,653 MB |

All read 1,000,001 rows and the same 14,413,653 values.

The streaming readers — TriasDev.Tabular, Sylvan, ExcelDataReader, the Open XML SDK in SAX mode and
MiniExcel — hold roughly the same memory whatever the file's size. ClosedXML, EPPlus and NPOI load
the workbook into an object model first, which is what makes them good at editing and is why their
peak grows with the file: NPOI holds 11.7 GB to read a 101 MB workbook.

## csv

### 3M-row csv — 364 MB, 17 columns, well-formed

| Library | Version | Time | Peak memory | Allocated |
|---|---|--:|--:|--:|
| Sep | 0.17.1 | 1.12 s | 50 MB | 1,428 MB |
| Sylvan.Data.Csv | 1.4.4 | 1.69 s | 52 MB | 1,566 MB |
| **TriasDev.Tabular** | 0.1.0 | 2.15 s | 50 MB | 1,567 MB |
| CsvHelper | 33.1.0 | 2.19 s | 50 MB | 1,566 MB |

All read 3,000,001 rows and the same 43,234,592 values. On clean input Sep is the fastest by a clear
margin; TriasDev.Tabular spends part of its time on the dialect and encoding detection the others
are spared by being told the delimiter.

### 5M-row csv — 572 MB, 17 columns, with malformed quoting

A real-world export: quotes inside unquoted fields, text after a closing quote, and 302 quotes that
are never closed.

| Library | Version | Time | Peak memory | Rows read | Result |
|---|---|--:|--:|--:|---|
| **TriasDev.Tabular** | 0.1.0 | 3.87 s | **51 MB** | **5,127,969** | every record; 40 unterminated and 9 stray quotes repaired and reported |
| CsvHelper | 33.1.0 | 3.51 s | 71 MB | 5,088,738 | 39,231 records merged into others, no warning — with its default settings too, its bad-data callback is never called |
| Sep | 0.17.1 | 4.56 s | 208 MB | 2,845,485 | 2,282,484 records merged into others, no warning |
| Sylvan.Data.Csv | 1.4.4 | — | — | — | throws: a delimiter, newline or EOF was expected after a closing quote |

**Ground truth.** The file has 5,127,969 lines, and every one of them has exactly 17 fields when split
on the delimiter without regard to quotes — so there is one record per line, 5,127,969 including the
header. The counts above are measured against that, and TriasDev.Tabular reads all of them.

It did not always. An earlier version of this page said "every record" and was checked against our
own count rather than against the file: nine lines were joined into six records, each by a field
that is a lone quote opening a quoted field and another lone quote in the same column of a later
line closing it — valid RFC 4180, so no repair fired. A quoted field that spans lines and holds a
whole record's worth of delimiters is now read as the records it is
([#20](https://github.com/TriasDev/tabular/issues/20)); read time is unchanged.

This is the file the library's design was decided on (see [ADR-0001](adr/0001-tabular-parsing-is-our-own-cursor.md)).
A reader that is fast on clean input and silently loses records on dirty input is fast at producing
a wrong import.

## Analysis

None of the libraries above profiles a file; TriasDev.Tabular does, and it is what the library is
for. Analysis reads every row and, for every column, counts empties and distinct values, measures
lengths and ranges, tries each value under every configured culture and records where the values
that do not fit stand — then ranks type suggestions from those facts.

That costs more than reading, and the cost depends on the format: a workbook's numbers and dates
arrive already typed, while every csv value is text and is tried under each culture.

The *Read* column comes from the library's own benchmark project (one process per reading, best of
two), not from the comparison above — which is why the million-row workbook reads in 4.6 s here and
4.30 s there. Different harness, different run; each table is consistent within itself.

| File | Rows | Read | Full analysis | Rows per second | Peak memory |
|---|--:|--:|--:|--:|--:|
| 100k-row workbook, 8.6 MB | 100,000 | 0.87 s | 1.6 s | 62,000 | 107 MB |
| 1M-row workbook, 101 MB | 1,000,000 | 4.6 s | 7.8 s | 128,000 | 180 MB |
| 3M-row csv, 364 MB | 3,000,000 | 2.4 s | 14.9 s | 201,000 | 134 MB |
| 5M-row csv, 572 MB | 5,127,959 | 4.2 s | 24.6 s | 209,000 | 131 MB |

All files have 17 columns. Measured with `benchmarks/TriasDev.Tabular.Benchmarks` on the same machine
and day, best of two runs; the peak stays flat because analysis keeps counts and a bounded set of
values per column, never the rows. How far analysis could be made faster, and at what price to what
it reports, is in the [guide](guide.md#if-analysis-ever-needs-to-be-faster).

## Reproducing

The real-world fixtures are not public. `benchmarks/TriasDev.Tabular.FixtureGenerator` writes
synthetic files of the same shape — 17 columns, the same row counts, and in the malformed csv the same
kinds of quoting defect in similar proportions — deterministically, so every run writes the same bytes:

```bash
dotnet run -c Release --project benchmarks/TriasDev.Tabular.FixtureGenerator -- /tmp/fixtures   # optional scale, e.g. 0.1
```

It writes `workbook-100k.xlsx` (9 MB), `workbook-1m.xlsx` (91 MB), `clean-3m.csv` (338 MB) and
`malformed-5m.csv` (578 MB). On them, TriasDev.Tabular alone (the benchmark project below, same
machine as above, 2026-09-25):

| File | Read | Peak | Full analysis | Peak |
|---|--:|--:|--:|--:|
| workbook-100k.xlsx | 0.80 s | 65 MB | 1.27 s | 81 MB |
| workbook-1m.xlsx | 3.89 s | 66 MB | 7.05 s | 151 MB |
| clean-3m.csv | 2.51 s | 52 MB | 11.8 s | 135 MB |
| malformed-5m.csv | 5.71 s | 53 MB | 22.2 s | 135 MB |

Same shape, not the same bytes, so expect figures close to the real-file tables rather than equal to
them. The malformed file has 5,127,969 lines and reads as 5,127,965 rows, with 310 unterminated and
6 stray quotes repaired: its generator also writes quotes that close within less than a record,
which the stray-quote repair leaves alone by design (see the known limitations).

Point the comparison project at those files, or at a folder of your own:

```bash
TABULAR_FIXTURES=/path/to/files \
TABULAR_FILES=big.xlsx,big.csv \
TABULAR_RUNS=3 \
dotnet run -c Release --project benchmarks/TriasDev.Tabular.Comparison
```

`TABULAR_READERS` restricts the run to named libraries, `TABULAR_DELIMITER` sets the delimiter the
csv libraries are given (default `;`), and `TABULAR_TIMEOUT` the seconds allowed per run (default
900). The output is Markdown, with the machine and every library's version in its header.

`benchmarks/TriasDev.Tabular.Benchmarks` is the other benchmark project: TriasDev.Tabular alone,
reader against full analysis, with no third-party packages — the quick check after a change.
