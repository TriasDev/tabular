# Benchmarks

TriasDev.Tabular against the libraries a .NET developer would otherwise reach for, reading the same
large files on the same machine under the same harness — and, [further down](#writing), writing the
same data.

**In short:** on workbooks it is the fastest reader measured, with a peak memory in the same band as
the other streaming readers and a fraction of what the object-model libraries need. On clean csv it
is third, just behind Sylvan, in a field that is close together. On a malformed csv it is the fastest
reader and the only one that reads all 5,127,969 records, where the others merge tens of thousands or
millions into their neighbours — and it says what it repaired.

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
2026-09-25; TriasDev.Tabular's csv rows re-measured 2026-09-26, after the reader got faster (#9) —
its workbook reader did not change.

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
| **TriasDev.Tabular** | 0.1.0 | 1.82 s | 52 MB | 1,567 MB |
| CsvHelper | 33.1.0 | 2.19 s | 50 MB | 1,566 MB |

All read 3,000,001 rows and the same 43,234,592 values. On clean input Sep is the fastest by a clear
margin — it creates no string until one is asked for, where every other reader here makes one per
cell; TriasDev.Tabular also spends part of its time on the dialect and encoding detection the others
are spared by being told the delimiter.

### 5M-row csv — 572 MB, 17 columns, with malformed quoting

A real-world export: quotes inside unquoted fields, text after a closing quote, and 302 quotes that
are never closed.

| Library | Version | Time | Peak memory | Rows read | Result |
|---|---|--:|--:|--:|---|
| **TriasDev.Tabular** | 0.1.0 | **2.69 s** | **50 MB** | **5,127,969** | every record; 49 stray quotes repaired and reported |
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
| 100k-row workbook, 8.6 MB | 100,000 | 0.87 s | 1.5 s | 66,000 | 106 MB |
| 1M-row workbook, 101 MB | 1,000,000 | 4.6 s | 6.7 s | 149,000 | 188 MB |
| 3M-row csv, 364 MB | 3,000,000 | 2.0 s | 9.6 s | 313,000 | 122 MB |
| 5M-row csv, 572 MB | 5,127,968 | 3.0 s | 14.0 s | 366,000 | 122 MB |

All files have 17 columns. Measured with `benchmarks/TriasDev.Tabular.Benchmarks` on the same machine
and day, best of two runs; the peak stays flat because analysis keeps counts and a bounded set of
values per column, never the rows. How far analysis could be made faster, and at what price to what
it reports, is in the [performance page](performance.md#if-analysis-ever-needs-to-be-faster).

## Writing

The same comparison for the other direction: writing the same data with TriasDev.Tabular and with
the libraries a .NET developer would otherwise reach for.

**In short:** on csv TriasDev.Tabular is faster than CsvHelper, which takes 51% longer on the narrow
file and 29% longer on the wide one, and allocates less than a tenth of what CsvHelper does. It is slower than
Sylvan.Data.Csv (Sylvan takes 24% less time on the narrow file, 40% less on the wide one; in the
ratios below, X takes N% longer than Y means X's time divided by Y's) and than Sep on the wide file
(TriasDev.Tabular takes 24% longer), while on the narrow one it is level with Sep (2% longer). On xlsx
it is faster than LargeXlsx at its default, which writes every cell reference: LargeXlsx takes 14%
longer on the unstyled narrow file, 21% longer on the styled one and 20% longer on the wide one. With
LargeXlsx's cell references off the two are level on the narrow files (TriasDev.Tabular 5% longer
unstyled, LargeXlsx 1% longer styled) and LargeXlsx is faster on the wide one (TriasDev.Tabular 27%
longer). SpreadCheetah is the fastest xlsx writer on the unstyled narrow file (TriasDev.Tabular 6%
longer) and level on the styled one (1%); on the wide one TriasDev.Tabular takes 15% longer. Styling
costs TriasDev.Tabular almost nothing over the unstyled file. MiniExcel is the slowest and allocates
the most. On the narrow xlsx files TriasDev.Tabular holds the least memory (55 MB against 60 to 79 MB);
on csv Sep and CsvHelper hold a little less (53 and 55 MB against 57 MB), and on the wide files
TriasDev.Tabular holds more than the others, because it batches 500 rows by column. It is the only
library here that writes ods and zip, so those have nothing to be compared with.

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
  others do not. In a profile of the wide xlsx write that check was 13.6% of the time, 0.3 to 0.5 s,
  a large part of the roughly 0.7 s gap to SpreadCheetah.

| Library | Version | Licence |
|---|---|---|
| TriasDev.Tabular | 0.5.0+ (main, unreleased) | MIT |
| CsvHelper | 33.1.0 | MS-PL or Apache-2.0 |
| Sep | 0.17.1 | MIT |
| Sylvan.Data.Csv | 1.4.4 | MIT |
| LargeXlsx | 2.0.2 | BSD-2-Clause |
| SpreadCheetah | 1.28.0 | MIT |
| MiniExcel | 1.46.0 | Apache-2.0 |

These are the latest stable versions, all free to use commercially.

Machine: Apple M1 Max, 10 cores, 64 GB, macOS 26.7.1, .NET 10.0.9, workstation GC. Measured
2026-10-05, with nothing else running.

### csv

#### 5M rows x 30 columns — 1.6 GB

| Library | Time | Peak memory | Allocated | Size |
|---|--:|--:|--:|--:|
| Sylvan.Data.Csv | 7.20 s | 59 MB | 959 MB | 1,604.5 MB |
| Sep | 9.28 s | 53 MB | 964 MB | 1,599.8 MB |
| **TriasDev.Tabular** | 9.44 s | 57 MB | 957 MB | 1,642.7 MB |
| CsvHelper | 14.25 s | 55 MB | 11,368 MB | 1,604.5 MB |

Generating the data alone, one cell struct per value, costs 0.2 s per million rows of this csv, so about 1 s of each figure here (measured separately, in a process of its own); on the wide file it costs 0.26 s by row and 0.11 s by typed column, which is how TriasDev.Tabular takes it. TriasDev.Tabular's
file is 2% larger because it writes a byte order mark and its date-times with milliseconds and a `T`
(`2026-01-01T00:00:00.000`), where the others write `yyyy-MM-dd HH:mm:ss`.

#### 10,000 rows x 5,003 columns — 208 MB

| Library | Time | Peak memory | Allocated | Size |
|---|--:|--:|--:|--:|
| Sylvan.Data.Csv | 2.35 s | 54 MB | 5 MB | 207.8 MB |
| Sep | 3.15 s | 51 MB | 5 MB | 207.8 MB |
| **TriasDev.Tabular** | 3.90 s | 92 MB | 42 MB | 207.8 MB |
| CsvHelper | 5.03 s | 55 MB | 3,498 MB | 207.8 MB |

### xlsx

#### 1,048,575 rows x 30 columns, unstyled

| Library | Time | Peak memory | Allocated | Size |
|---|--:|--:|--:|--:|
| SpreadCheetah | 4.51 s | 60 MB | 195 MB | 224.5 MB |
| LargeXlsx (no cell references) | 4.54 s | 66 MB | 195 MB | 227.2 MB |
| **TriasDev.Tabular** | 4.77 s | 55 MB | 197 MB | 236.7 MB |
| LargeXlsx | 5.42 s | 66 MB | 195 MB | 338.3 MB |
| MiniExcel | 8.81 s | 79 MB | 9,848 MB | 359.3 MB |

#### 1,048,575 rows x 30 columns, styled

| Library | Time | Peak memory | Allocated | Size |
|---|--:|--:|--:|--:|
| SpreadCheetah | 4.74 s | 60 MB | 195 MB | 237.8 MB |
| **TriasDev.Tabular** | 4.78 s | 55 MB | 197 MB | 238.7 MB |
| LargeXlsx (no cell references) | 4.83 s | 66 MB | 195 MB | 235.3 MB |
| LargeXlsx | 5.80 s | 66 MB | 195 MB | 348.6 MB |

MiniExcel cannot apply these styles and is not in the table. Styling costs TriasDev.Tabular 0.01 s
over the unstyled file, against 0.2 to 0.4 s for the others. The provisional pass on a loaded machine
had put it 14% behind LargeXlsx, at 2 s of styling; on the quiet machine that cost is gone, so it was
the load, not the style path.

#### 10,000 rows x 5,003 columns, styled

| Library | Time | Peak memory | Allocated | Size |
|---|--:|--:|--:|--:|
| LargeXlsx (no cell references) | 4.07 s | 62 MB | 3 MB | 102.2 MB |
| SpreadCheetah | 4.50 s | 55 MB | 2 MB | 101.3 MB |
| **TriasDev.Tabular** | 5.17 s | 94 MB | 44 MB | 102.2 MB |
| LargeXlsx | 6.21 s | 62 MB | 3 MB | 355.9 MB |

Cell references are most of the size: written for every cell (LargeXlsx's default), they make the
file three and a half times as large as without them. TriasDev.Tabular's
peak is higher because a `ColumnBatch` of 500 rows holds 2.5 million values.

### ods and zip

No other library in the comparison writes either.

| TriasDev.Tabular | Time | Peak memory | Allocated | Size |
|---|--:|--:|--:|--:|
| ods, 1,048,575 x 30 | 5.07 s | 57 MB | 197 MB | 234.7 MB |
| ods, 1,048,575 x 30, styled | 5.49 s | 57 MB | 197 MB | 266.8 MB |
| ods, 10,000 x 5,003 | 5.63 s | 94 MB | 44 MB | 176.1 MB |
| zip of one csv sheet, 5M x 30 | 16.77 s | 57 MB | 957 MB | 814.4 MB |

The zip holds the 1.6 GB csv of the first table.

### Compression

The xlsx, ods and zip writers compress with `CompressionLevel.Fastest` by default. The same runs with
`Optimal` (TriasDev.Tabular alone, `TABULAR_COMPRESSION=Optimal`):

| Scenario | Fastest | Optimal | Time | Size |
|---|--:|--:|--:|--:|
| xlsx, 1M x 30 | 4.77 s, 236.7 MB | 9.09 s, 144.5 MB | +91% | -39% |
| xlsx styled, 1M x 30 | 4.78 s, 238.7 MB | 9.83 s, 147.4 MB | +106% | -38% |
| xlsx styled, 10,000 x 5,003 | 5.17 s, 102.2 MB | 6.49 s, 42.6 MB | +26% | -58% |
| ods, 1M x 30 | 5.07 s, 234.7 MB | 9.59 s, 148.3 MB | +89% | -37% |
| ods styled, 1M x 30 | 5.49 s, 266.8 MB | 10.96 s, 164.0 MB | +100% | -39% |
| ods, 10,000 x 5,003 | 5.63 s, 176.1 MB | 9.29 s, 107.1 MB | +65% | -39% |
| zip of csv, 5M x 30 | 16.77 s, 814.4 MB | 27.70 s, 533.4 MB | +65% | -35% |

`Optimal` makes the files 35 to 58% smaller and the writing 26 to 106% slower (its time is that much more than `Fastest`'s). The default stays
`Fastest`: it would change only if `Optimal` cost under 15% in time for a file over 20% smaller, and
nowhere does. Peak memory and allocation are the same at both levels. Choose `Optimal` when the file
is stored or sent over a slow link and the writing is not the bottleneck.

### An entry over 4 GB

A zip with one csv sheet of 12,000,000 rows x 30 columns, whose single entry is 7.39 GB uncompressed
(1.59 GB compressed), written by TriasDev.Tabular in 35 s. `unzip -t` reports no errors (28 s),
`System.IO.Compression.ZipArchive` reads the entry to its last byte (10 s, 12,000,001 lines), and
`TabularFile.Open` reads it back as 12,000,001 rows (16 s).

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
them. The malformed file has 5,127,969 lines and reads as all 5,127,969 rows, with 320 stray quotes
repaired.

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
