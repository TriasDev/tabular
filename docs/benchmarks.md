# Benchmarks

TriasDev.Tabular against the libraries a .NET developer would otherwise reach for, reading the same
large files on the same machine under the same harness — and, [further down](#writing), writing the
same data.

**In short:** on workbooks it is the fastest reader measured, with the lowest peak memory and a
fraction of what the object-model libraries need. On clean csv it is the slowest of the four readers:
CsvHelper 34 and Sep parse UTF-8 bytes with SIMD instructions and read the clean file in less than
half its time. On a malformed csv it is the only one that reads all 5,127,969 records, where the
others merge tens of thousands or millions into their neighbours or throw — and it says what it
repaired.

## What the numbers mean

- **Time** — wall clock for opening the file and reading every cell of its first sheet.
- **Peak memory** — the most memory the process held at any moment (peak resident set). This is the
  number that decides whether an import fits in the container or server it runs on, and it is the
  one to compare first.
- **Allocated** — everything the garbage collector handed out over the whole run. Memory is reused
  many times over, so this is far larger than the peak: reading the 5M-row csv allocates about
  2.9 GB while never holding more than about 55 MB. It measures pressure on the garbage collector,
  which shows up in the time, not how much memory a host needs.
- **Rows** — rows the library reported, the header included. Where a library reports a different
  number from TriasDev.Tabular on the same file, the note says by how much.

## How it was measured

- Every library does the same work: read the first sheet from start to end and take every cell's
  value as text, each in the idiom its documentation recommends for speed — Open XML SDK in its
  streaming (SAX) mode, CsvHelper through its `CsvReader` field by field rather than record mapping,
  Sep with unescaping on.
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
- The files are synthetic, of the shape of the real-world exports the library was designed on — see
  [Reproducing](#reproducing). Anyone can generate the same bytes.

Machine: a Hetzner Cloud CCX33 — 8 dedicated vCPU of an AMD EPYC-Milan, 32 GB, Ubuntu 24.04 — with
.NET 10.0.15 and workstation GC. Measured 2026-11-12, reading and writing in one run on one server
(`benchmarks/cloud/remote/comparison.sh`).

## Workbooks

### 100k-row workbook — 9.0 MB, 17 columns

| Library | Version | Time | Peak memory | Allocated |
|---|---|--:|--:|--:|
| **TriasDev.Tabular** | 0.7.2 | **0.78 s** | **61 MB** | 43 MB |
| Sylvan.Data.Excel | 0.5.8 | 1.48 s | 69 MB | 43 MB |
| ExcelDataReader | 3.9.0 | 2.26 s | 67 MB | 392 MB |
| EPPlus | 4.5.3.3 | 2.80 s | 264 MB | 1,271 MB |
| MiniExcel | 1.47.0 | 2.87 s | 68 MB | 2,960 MB |
| DocumentFormat.OpenXml (SAX) | 3.5.1 | 2.91 s | 76 MB | 1,188 MB |
| ClosedXML | 0.105.1 | 5.34 s | 227 MB | 2,129 MB |
| NPOI | 2.7.6 | 6.61 s | 1,258 MB | 1,923 MB |

All read 100,001 rows and the same 1,600,017 values.

### 1M-row workbook — 91 MB, 17 columns

| Library | Version | Time | Peak memory | Allocated |
|---|---|--:|--:|--:|
| **TriasDev.Tabular** | 0.7.2 | **3.86 s** | **63 MB** | 427 MB |
| Sylvan.Data.Excel | 0.5.8 | 5.85 s | 69 MB | 429 MB |
| ExcelDataReader | 3.9.0 | 11.64 s | 67 MB | 3,958 MB |
| EPPlus | 4.5.3.3 | 17.52 s | 1,705 MB | 12,291 MB |
| MiniExcel | 1.47.0 | 19.44 s | 69 MB | 29,723 MB |
| DocumentFormat.OpenXml (SAX) | 3.5.1 | 20.49 s | 75 MB | 11,888 MB |
| ClosedXML | 0.105.1 | 44.99 s | 1,323 MB | 21,189 MB |
| NPOI | 2.7.6 | 51.39 s | 11,821 MB | 19,238 MB |

All read 1,000,001 rows and the same 16,000,017 values.

The streaming readers — TriasDev.Tabular, Sylvan, ExcelDataReader, the Open XML SDK in SAX mode and
MiniExcel — hold roughly the same memory whatever the file's size. ClosedXML, EPPlus and NPOI load
the workbook into an object model first, which is what makes them good at editing and is why their
peak grows with the file: NPOI holds 11.8 GB to read a 91 MB workbook.

## csv

### 3M-row csv — 338 MB, 17 columns, well-formed

| Library | Version | Time | Peak memory | Allocated |
|---|---|--:|--:|--:|
| CsvHelper | 34.0.0 | 0.73 s | 54 MB | 1,557 MB |
| Sep | 0.18.0 | 0.96 s | 53 MB | 1,557 MB |
| Sylvan.Data.Csv | 1.4.4 | 1.20 s | 53 MB | 1,694 MB |
| **TriasDev.Tabular** | 0.7.2 | 1.81 s | 54 MB | 1,695 MB |

All read 3,000,001 rows and the same 48,000,017 values. CsvHelper — rewritten in 34.0 — and Sep parse
the file as UTF-8 bytes with SIMD instructions and decode a field only when it is asked for, so even
taking every value as a string they are far ahead of the char-based readers. CsvHelper 33, before the
rewrite, took 20% longer than TriasDev.Tabular (on the real-world file, 2026-09-25, another machine).
TriasDev.Tabular also spends part of its time on the dialect and encoding detection the others are spared by being told the delimiter.

### 5M-row csv — 578 MB, 17 columns, with malformed quoting

The defects of a real-world export: quotes inside unquoted fields, text after a closing quote, quotes
that are never closed, and lone quotes standing as a whole field.

| Library | Version | Time | Peak memory | Rows read | Result |
|---|---|--:|--:|--:|---|
| CsvHelper | 34.0.0 | 1.18 s | 61 MB | 5,092,629 | 35,340 records merged into others, no warning at its default settings |
| Sep | 0.18.0 | 1.22 s | 61 MB | 2,555,505 | 2,572,464 records merged into others, no warning |
| **TriasDev.Tabular** | 0.7.2 | 2.99 s | **54 MB** | **5,127,969** | every record; 320 stray quotes repaired and reported |
| Sylvan.Data.Csv | 1.4.4 | — | — | — | throws: a delimiter, newline or EOF was expected after a closing quote |

**Ground truth.** The file has 5,127,969 lines, and every one of them has exactly 17 fields when split
on the delimiter without regard to quotes — so there is one record per line, 5,127,969 including the
header. The counts above are measured against that, and TriasDev.Tabular reads all of them.

It did not always. An earlier version of this page, measured on the real export, said "every record"
and was checked against our own count rather than against the file: nine lines were joined into six records, each by a field
that is a lone quote opening a quoted field and another lone quote in the same column of a later
line closing it — valid RFC 4180, so no repair fired. A quoted field that spans lines and holds a
whole record's worth of delimiters is now read as the records it is
([#20](https://github.com/TriasDev/tabular/issues/20)); read time is unchanged.

The file reproduces the defects of the export the library's design was decided on (see
[ADR-0001](adr/0001-tabular-parsing-is-our-own-cursor.md)), which has the same number of lines. A reader that
is fast on clean input and silently loses records on dirty input is fast at producing a wrong import.

## Analysis

None of the libraries above profiles a file; TriasDev.Tabular does, and it is what the library is
for. Analysis reads every row and, for every column, counts empties and distinct values, measures
lengths and ranges, tries each value under every configured culture and records where the values
that do not fit stand — then ranks type suggestions from those facts.

That costs more than reading, and the cost depends on the format: a workbook's numbers and dates
arrive already typed, while every csv value is text and is tried under each culture.

The *Read* column comes from the library's own benchmark project (one process per reading, best of
two), not from the comparison above — which is why the million-row workbook reads in 3.93 s here and
3.86 s there. Different harness, same run; each table is consistent within itself.

| File | Rows | Read | Full analysis | Rows per second | Peak memory |
|---|--:|--:|--:|--:|--:|
| 100k-row workbook, 9.0 MB | 100,000 | 0.82 s | 1.22 s | 82,000 | 80 MB |
| 1M-row workbook, 91 MB | 1,000,000 | 3.93 s | 6.26 s | 160,000 | 139 MB |
| 3M-row csv, 338 MB | 3,000,000 | 2.05 s | 9.98 s | 301,000 | 162 MB |
| 5M-row csv, 578 MB | 5,127,968 | 3.48 s | 16.05 s | 320,000 | 162 MB |

All files have 17 columns. Measured with `benchmarks/TriasDev.Tabular.Benchmarks` on the same server
in the same run, best of two; the peak stays flat because analysis keeps counts and a bounded set of
values per column, never the rows. How far analysis could be made faster, and at what price to what
it reports, is in the [performance page](performance.md#if-analysis-ever-needs-to-be-faster).

## Writing

The same comparison for the other direction: writing the same data with TriasDev.Tabular and with
the libraries a .NET developer would otherwise reach for.

**In short:** on csv CsvHelper 34 is the fastest writer measured: TriasDev.Tabular takes 90% longer on
the narrow file and three times as long on the wide one (in the ratios here, X takes N% longer than Y
means X's time divided by Y's). Sylvan.Data.Csv is faster too (TriasDev.Tabular takes 33% longer on
the narrow file, 67% on the wide one); Sep is level on the narrow file (Sep 5% longer) and faster on
the wide one (TriasDev.Tabular 21% longer). On xlsx SpreadCheetah and LargeXlsx with its cell
references off are the fastest: TriasDev.Tabular takes 16% longer on the unstyled narrow file, 11 to
14% longer on the styled one and 30 to 48% longer on the wide one. It is level with LargeXlsx at its
default, which writes every cell reference (LargeXlsx 1% longer unstyled and 7% styled,
TriasDev.Tabular 5% longer on the wide file). Styling costs TriasDev.Tabular less than it costs the
others. MiniExcel is the slowest and allocates the most. On the narrow xlsx files TriasDev.Tabular
holds the least memory (60 to 61 MB against 62 to 73 MB); on csv the others hold a little less (56 to
59 MB against 62 MB), and on the wide files TriasDev.Tabular holds about twice as much, because it
batches 500 rows by column. It is the only library here that writes ods and zip, so those have nothing
to be compared with.

### What was measured

- **The data.** Every library writes the same generated rows — text with commas and quotes in it,
  integers, doubles, decimals, dates, date-times, booleans and empty cells — under the same header.
  *Narrow* is 30 columns; *wide* is 10,000 rows of 5,003 columns, almost all doubles. The *styled*
  scenarios add a bold, filled, frozen header row, date and number formats, and a fill per double
  chosen by its value; each library's output is read back to check the styles.
- **The idiom.** Each library is used the way its documentation recommends for speed, with no object
  model where it has a streaming writer: TriasDev.Tabular through `TabularWriter` (cell by cell, flushed
  when it recommends it; wide data by column through `ColumnBatch`), CsvHelper through `WriteField` per
  cell with no class mapping, Sep with indexed columns, Sylvan.Data.Csv from a `DbDataReader`, LargeXlsx
  and SpreadCheetah row by row with styles registered once, MiniExcel from an `IDataReader` in its
  streaming mode (`FastMode` off, which streams and needs no readable target).
- **Time** is the fastest of three runs, each in a fresh process: other work on the machine only ever
  makes a run slower. Peak memory, allocated and size are the medians. The data is generated inside the
  timed part for every library, from the same generators.
- **Into a counting null stream.** The timed write goes into a stream that counts the bytes and throws
  them away, so no figure includes the file system's. It also means the libraries that build their zip
  with `ZipArchive` (LargeXlsx, SpreadCheetah, MiniExcel) write it in streaming mode, with the sizes after
  the data (data descriptors), as they would into an HTTP response.
- **Verified.** After the timed run the process writes the same file again to disk, re-reads it with
  TriasDev.Tabular and checks the row count, the header and a fixed sample of cells against the data.
  A file that does not read back is reported as failed, not timed.
- **What differs between the files.** Sep writes LF as its row terminator and cannot be told to write
  CR LF; the others write CR LF. SpreadCheetah leaves out each cell's reference (`r="B7"`) by default,
  which makes its files smaller and the writing faster; TriasDev.Tabular leaves it out where a cell
  follows the one before it, and LargeXlsx writes it by default (`requireCellReferences: true`) and can leave it out; the tables show
  it both ways, the second as "LargeXlsx (no cell references)". TriasDev.Tabular also checks that every
  double survives the round trip (15 significant digits) and fails the write if one would not; the
  others do not. In a profile of the wide xlsx write (2026-10-05) that check was 13.6% of the time,
  a part of the 1.5 s gap to SpreadCheetah.

| Library | Version | Licence |
|---|---|---|
| TriasDev.Tabular | 0.7.2 | MIT |
| CsvHelper | 34.0.0 | MS-PL or Apache-2.0 |
| Sep | 0.18.0 | MIT |
| Sylvan.Data.Csv | 1.4.4 | MIT |
| LargeXlsx | 2.0.2 | BSD-2-Clause |
| SpreadCheetah | 1.29.0 | MIT |
| MiniExcel | 1.47.0 | Apache-2.0 |

These are the latest stable versions, all free to use commercially. Same server and run as the reading
comparison above.

### csv

#### 5M rows x 30 columns — 1.6 GB

| Library | Time | Peak memory | Allocated | Size |
|---|--:|--:|--:|--:|
| CsvHelper | 6.91 s | 59 MB | 959 MB | 1,604.5 MB |
| Sylvan.Data.Csv | 9.84 s | 59 MB | 959 MB | 1,604.5 MB |
| **TriasDev.Tabular** | 13.10 s | 62 MB | 959 MB | 1,642.7 MB |
| Sep | 13.71 s | 56 MB | 963 MB | 1,599.8 MB |

Generating the data alone, one cell struct per value, costs 0.38 s per million rows of this csv, so about 1.9 s of each figure here (measured separately, in a process of its own); on the wide file it costs 0.30 s by row and 0.16 s by typed column, which is how TriasDev.Tabular takes it. TriasDev.Tabular's
file is 2% larger because it writes a byte order mark and its date-times with milliseconds and a `T`
(`2026-01-01T00:00:00.000`), where the others write `yyyy-MM-dd HH:mm:ss`.

#### 10,000 rows x 5,003 columns — 208 MB

| Library | Time | Peak memory | Allocated | Size |
|---|--:|--:|--:|--:|
| CsvHelper | 1.59 s | 42 MB | 2 MB | 207.8 MB |
| Sylvan.Data.Csv | 2.92 s | 45 MB | 5 MB | 207.8 MB |
| Sep | 4.02 s | 44 MB | 5 MB | 207.8 MB |
| **TriasDev.Tabular** | 4.88 s | 86 MB | 42 MB | 207.8 MB |

### xlsx

#### 1,048,575 rows x 30 columns, unstyled

| Library | Time | Peak memory | Allocated | Size |
|---|--:|--:|--:|--:|
| LargeXlsx (no cell references) | 5.14 s | 62 MB | 195 MB | 227.2 MB |
| SpreadCheetah | 5.14 s | 62 MB | 195 MB | 224.5 MB |
| **TriasDev.Tabular** | 5.96 s | 60 MB | 197 MB | 236.7 MB |
| LargeXlsx | 5.99 s | 62 MB | 195 MB | 338.3 MB |
| MiniExcel | 10.86 s | 73 MB | 9,832 MB | 359.3 MB |

#### 1,048,575 rows x 30 columns, styled

| Library | Time | Peak memory | Allocated | Size |
|---|--:|--:|--:|--:|
| SpreadCheetah | 5.29 s | 62 MB | 195 MB | 237.8 MB |
| LargeXlsx (no cell references) | 5.43 s | 62 MB | 195 MB | 235.3 MB |
| **TriasDev.Tabular** | 6.02 s | 61 MB | 196 MB | 238.7 MB |
| LargeXlsx | 6.44 s | 62 MB | 195 MB | 348.6 MB |

MiniExcel cannot apply these styles and is not in the table. Styling costs TriasDev.Tabular 0.06 s
over the unstyled file, against 0.15 to 0.45 s for the others.

#### 10,000 rows x 5,003 columns, styled

| Library | Time | Peak memory | Allocated | Size |
|---|--:|--:|--:|--:|
| LargeXlsx (no cell references) | 4.58 s | 47 MB | 3 MB | 102.2 MB |
| SpreadCheetah | 5.25 s | 47 MB | 2 MB | 101.3 MB |
| LargeXlsx | 6.45 s | 47 MB | 3 MB | 355.9 MB |
| **TriasDev.Tabular** | 6.80 s | 87 MB | 44 MB | 102.2 MB |

Cell references are most of the size: written for every cell (LargeXlsx's default), they make the
file three and a half times as large as without them. TriasDev.Tabular's
peak is higher because a `ColumnBatch` of 500 rows holds 2.5 million values.

### ods and zip

No other library in the comparison writes either.

| TriasDev.Tabular | Time | Peak memory | Allocated | Size |
|---|--:|--:|--:|--:|
| ods, 1,048,575 x 30 | 7.11 s | 62 MB | 196 MB | 234.7 MB |
| ods, 1,048,575 x 30, styled | 7.63 s | 62 MB | 196 MB | 266.8 MB |
| ods, 10,000 x 5,003 | 7.89 s | 87 MB | 44 MB | 176.1 MB |
| zip of one csv sheet, 5M x 30 | 20.68 s | 63 MB | 958 MB | 814.4 MB |

The zip holds the 1.6 GB csv of the first table.

### Compression

The xlsx, ods and zip writers compress with `CompressionLevel.Fastest` by default. The same runs with
`Optimal` (TriasDev.Tabular alone, `TABULAR_COMPRESSION=Optimal`):

| Scenario | Fastest | Optimal | Time | Size |
|---|--:|--:|--:|--:|
| xlsx, 1M x 30 | 5.96 s, 236.7 MB | 12.75 s, 144.5 MB | +114% | -39% |
| xlsx styled, 1M x 30 | 6.02 s, 238.7 MB | 14.16 s, 147.4 MB | +135% | -38% |
| xlsx styled, 10,000 x 5,003 | 6.80 s, 102.2 MB | 8.94 s, 42.6 MB | +31% | -58% |
| ods, 1M x 30 | 7.11 s, 234.7 MB | 13.38 s, 148.3 MB | +88% | -37% |
| ods styled, 1M x 30 | 7.63 s, 266.8 MB | 15.08 s, 164.0 MB | +98% | -39% |
| ods, 10,000 x 5,003 | 7.89 s, 176.1 MB | 13.22 s, 107.1 MB | +68% | -39% |
| zip of csv, 5M x 30 | 20.68 s, 814.4 MB | 38.18 s, 533.4 MB | +85% | -35% |

`Optimal` makes the files 35 to 58% smaller and the writing 31 to 135% slower (its time is that much more than `Fastest`'s). The default stays
`Fastest`: it would change only if `Optimal` cost under 15% in time for a file over 20% smaller, and
nowhere does. Peak memory and allocation are the same at both levels. Choose `Optimal` when the file
is stored or sent over a slow link and the writing is not the bottleneck.

### An entry over 4 GB

A zip with one csv sheet of 12,000,000 rows x 30 columns, whose single entry is 7.39 GB uncompressed
(1.59 GB compressed), written by TriasDev.Tabular in 35 s. `unzip -t` reports no errors (28 s),
`System.IO.Compression.ZipArchive` reads the entry to its last byte (10 s, 12,000,001 lines), and
`TabularFile.Open` reads it back as 12,000,001 rows (16 s). Measured on an Apple M1 Max, 2026-10-05.

### Reproducing the writing comparison

```bash
TABULAR_RUNS=3 dotnet run -c Release --project benchmarks/TriasDev.Tabular.WriteComparison
```

`TABULAR_WRITERS` and `TABULAR_SCENARIOS` restrict the run to named writers and scenarios,
`TABULAR_COMPRESSION` sets the compression level of TriasDev.Tabular's xlsx, ods and zip writers,
`TABULAR_ROWS_SCALE` (below 1) shrinks every scenario to try the harness, and `TABULAR_OUT` and
`TABULAR_TIMEOUT` are as for the reading comparison. The output is Markdown, with the machine and every
library's version in its header.

## Reproducing

The real-world exports the library was designed on are not public. `benchmarks/TriasDev.Tabular.FixtureGenerator`
writes synthetic files of their shape — 17 columns, the same row counts, and in the malformed csv the
same kinds of quoting defect in similar proportions — deterministically, so every run writes the same
bytes. Every reading table on this page was measured on them:

```bash
dotnet run -c Release --project benchmarks/TriasDev.Tabular.FixtureGenerator -- /tmp/fixtures   # optional scale, e.g. 0.1
```

It writes `workbook-100k.xlsx` (9 MB), `workbook-1m.xlsx` (91 MB), `clean-3m.csv` (338 MB) and
`malformed-5m.csv` (578 MB).

The whole page — both comparisons, `Optimal` compression and the analysis table — comes from one run
on a rented server with dedicated cores, which generates the files there (about an hour; see
[`benchmarks/cloud`](https://github.com/TriasDev/tabular/tree/main/benchmarks/cloud)):

```bash
TABULAR_RUNS=3 REF=main benchmarks/cloud/hetzner.sh benchmarks/cloud/remote/comparison.sh
```

**When this page is re-measured.** Not for every change: for a release whose reading or writing path
changed in a way that may move these figures, or when a compared library ships a new version that
changes its API or its results, as CsvHelper 34 did. Patch releases and minor dependency updates keep
the figures they have, and updates of the compared libraries wait for the next such run, so the
versions in the tables are always the ones measured. A change that may touch speed is checked against
`main` instead (`benchmarks/cloud/remote/refs.sh`, about ten minutes).

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
