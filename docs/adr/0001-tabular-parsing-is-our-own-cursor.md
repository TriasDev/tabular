# 0001 — Tabular files are read by our own cursor, not by a parsing library

Date: 2026-08-24
Status: Accepted

Recorded before the library moved to its own repository, and renumbered on the move.

## Context

This library reads an Excel or CSV file,
profiles its columns, and extracts it through a mapping a user builds. Analysis reads the file
completely — the questions it answers are falsified by a single row anywhere — so the reader
underneath is not an implementation detail. It was chosen by measurement, behind three gates taken in
order: licence, correctness, speed.

The measurements below come from `tests/TriasDev.Tabular.Tests/Spike` and
`benchmarks/TriasDev.Tabular.Benchmarks`, run on 2026-08-24 against sixteen golden fixtures and against
four large real-world files of 17 columns each, from one hundred thousand to five million rows. Every timing ran in its own child process, because
peak resident memory only ever rises within a process and would otherwise credit each candidate with
the greediest earlier one's peak.

### Gate one — licence

Verified from the published `.nuspec` of each package on the day of the spike rather than from
memory, because these terms change retroactively: EPPlus moved to Polyform Noncommercial.

**NPOI changed at 2.8.0.** Versions 2.6.2 through 2.7.5 declare `Apache-2.0` as a licence expression;
2.8.0 ships `OSMFEULA.txt`, an Open Source Maintenance Fee Agreement. The source remains Apache-2.0
and the fee is explicitly not a licence fee, but using the *NuGet binary* obliges revenue-generating
users with at least US$10,000 annual gross revenue to pay a monthly maintenance fee. NPOI was
therefore measured, at 2.7.5, as a baseline and was never a candidate: a dependency that can never be updated is not one to adopt.

`ClosedXML` (MIT) was excluded on technical grounds — it materialises the workbook — and
`SpreadCheetah` (MIT) is a writer, kept only to generate fixtures. `Sylvan.Data.Excel`,
`Sylvan.Data.Csv`, `ExcelDataReader`, `Sep` and `DocumentFormat.OpenXml` are MIT; `MiniExcel` is
Apache-2.0; `CsvHelper` is MS-PL or Apache-2.0.

### Gate two — correctness

Sixteen fixtures, each pinning one behaviour, assembled from raw OOXML and raw bytes rather than
through a writer — a well-behaved writer cannot emit the shapes worth pinning.

**Two candidates failed for a configuration reason rather than a defect, and were re-run.**
ExcelDataReader refused every workbook until a code-page provider was registered; Sep merged records
and returned quoted text verbatim until `Unescape` and `DisableColCountCheck` were set. Both then
passed the fixtures they had failed. Their requirements are recorded as findings — a reader that
mutates process-wide encoding state to become usable, and a reader whose correctness is off by
default, are both things a host has to know — but not as failures.

**MiniExcel failed on merit**: it ignores `workbookPr/@date1904` and reads the serial 43465 as
2018-12-31 where the workbook means 2023-01-01. The serial was chosen so the error is four years, not
one day, and so cannot be mistaken for rounding.

**The decisive failures came from real data, not from the fixtures.** On a 572 MB real-world export the
candidates disagreed about how many records the file even holds — 2,845,485, 5,088,738, or an
exception — and none of them raised a warning. The file mixes four shapes in one column:

```
"77 Main St, Suite 4"   a genuinely quoted field
"C" Road                       starts with a quote but is not a quoted field
";;12,34;...                     a lone quote, never closed
100 21"st AVE                  an inch mark inside an unquoted field
```

4,571 lines contain a quote and 302 of them carry an odd number. Two fixtures were added from this
data, and they split the field:

| | quote inside an unquoted field | text after a closing quote |
|---|:--:|:--:|
| Sylvan.Data.Csv | passes | **throws** |
| Sep | **merges records** | passes |
| CsvHelper | passes | passes |
| Our cursor, before the fix | **merges records** | passes |

Our prototype had the same defect as Sep: it treated a quote as syntax anywhere in a field rather
than only where a field begins, which is what RFC 4180 and Excel both mean. Fixed, it passes both.
Among third-party readers only CsvHelper survives real data — and the fastest, leanest candidate on
clean input, `Sylvan.Data.Csv`, cannot open the file at all.

### Gate three — speed

**These numbers are frozen at the decision, not maintained.** They compare the alternatives against
the prototype this decision was taken on, which is what an ADR is for. The reader has moved a long
way since — see [benchmarks](../benchmarks.md) and the [guide](../guide.md), which are kept current.

Only survivors were measured. `ms` is wall clock, `allocated` is total managed allocation, `peak` is
peak resident set.

| File | Reader | ms | allocated | peak | rows |
|---|---|--:|--:|--:|--:|
| 100k-row workbook (8.6 MB) | Sylvan.Data.Excel | 1,991 | 60 MB | 98 MB | 100,001 |
| | ExcelDataReader | 2,362 | 456 MB | 93 MB | 100,001 |
| | MiniExcel | 4,046 | 1,713 MB | 78 MB | 100,001 |
| | NPOI 2.7.5 (baseline) | 6,045 | 2,852 MB | 1,749 MB | 100,001 |
| | Our cursor | 2,383 | 1,321 MB | 90 MB | 100,001 |
| 1M-row workbook (101 MB) | Sylvan.Data.Excel | 5,672 | 324 MB | 129 MB | 1,000,001 |
| | ExcelDataReader | 9,582 | 3,381 MB | 120 MB | 1,000,001 |
| | MiniExcel | 19,788 | 15,308 MB | 76 MB | 1,000,001 |
| | NPOI 2.7.5 (baseline) | 54,367 | 20,117 MB | **11,298 MB** | 1,000,001 |
| | Our cursor | 11,074 | 10,837 MB | 123 MB | 1,000,001 |
| 3M-row csv (364 MB) | Sylvan.Data.Csv | 1,825 | 1,566 MB | 57 MB | 3,000,001 |
| | Sep | 1,276 | 1,429 MB | 55 MB | 3,000,001 |
| | CsvHelper | 2,404 | 1,566 MB | 55 MB | 3,000,001 |
| | Our cursor | 1,920 | 3,306 MB | 54 MB | 3,000,001 |
| 5M-row csv, malformed (572 MB) | Sylvan.Data.Csv | — | — | — | **failed** |
| | Sep | 4,837 | 1,962 MB | 201 MB | 2,845,485 (wrong) |
| | CsvHelper | 3,753 | 2,751 MB | 75 MB | 5,088,738 |
| | Our cursor | 3,151 | 5,685 MB | 58 MB | 5,088,738 |

NPOI's numbers are the quantitative form of what reading its prior use showed: 11.3 GB held for a
101 MB file, and the slowest run by a factor of five.

## Decision

**Both formats are read by a cursor we own, built on the base class library alone —
`ZipArchive` and `XmlReader` for xlsx, a state machine for csv. The library takes no third-party
parsing dependency.**

**An unterminated quoted field is bounded and reported.** A quoted field may span line endings, which
is legal and common, but not beyond a configured number of lines. Past that bound the opening quote
is treated as literal and the record is re-parsed, and the occurrence is counted in the run's summary
as a recovered anomaly.

## Consequences

The library depends on the base class library alone, which was one of its goals. It also becomes the only reader measured here that passes
all sixteen fixtures across both formats.

**The standard we held the candidates to applies to us.** Gate two recorded, against
ExcelDataReader, that a reader which mutates process-wide encoding state to become usable is
something a host has to know about. Our own dialect detector then did exactly that, from a static
constructor, to obtain Windows-1252 — invisibly, on the first csv anyone read. It no longer does: the
encoding is a table of twenty-seven characters and is decoded here. A criticism worth making of a
competitor is worth applying to oneself.

**We now own every edge case.** The date epoch, custom number formats, inline strings, rich-text runs,
sparse rows, encoding detection, delimiter detection and malformed quoting are ours to get right, and
the fixtures are what say whether we did. That is the real cost of this decision, and it is paid in
tests rather than in dependencies.

**On csv the choice costs nothing and gains resilience.** Our cursor is the fastest survivor on the
malformed 572 MB file and holds the smallest peak of any candidate on both csv fixtures. Encoding and
delimiter detection would have been ours in any case: none of the csv libraries offers either.

**On xlsx the choice costs speed, and the figure is honest.** Sylvan.Data.Excel is roughly twice as
fast and allocates a twentieth of what our prototype does. Two causes are known and already planned
against — the shared string table is loaded eagerly, and a string is allocated per cell — but the gap
after that work is not yet measured, and this decision does not assume it closes. Peak memory, which
is the number that governs whether a reader is usable at all, is already comparable: 123 MB against
129 MB on the million-row workbook.

### The condition was met — measured 2026-08-24

The bar below was cleared, and by more than it asked for. Replacing `XmlReader` with a scanner over
the character buffer for the worksheet part alone took the million-row workbook from 668 bytes of
allocation per cell to **29**, against a budget of 60, and from 11.2 seconds to 4.4 — which is
faster than the runner-up's 5.7. Consuming cells without turning any of them into text costs 13.

`XmlReader` was the whole of the gap: it has no span-returning attribute API, so every cell paid for
a string per attribute — `r`, `t`, `s` — whether or not anything read them. Nothing was copied from
the runner-up to achieve this; the technique follows from the measurement.

Two notes for whoever revisits these numbers. Bytes per cell rises on smaller files — 69 on a
100,000-row workbook, 66 on an 8,500-row one — because the shared string table is read once and
amortised over fewer cells; the budget is stated against the large fixture for that reason. And the
scanner is a hand-written reader of a narrow dialect: the golden fixtures could not have caught its
first real defect, because a small fixture never fills a buffer, so tests that force a compaction and
a growth were added alongside it.

**The decision carries a condition, and the condition has a number.** Expressed per cell, so it can
be compared across fixtures, the million-row workbook costs our prototype about 670 bytes of
allocation per cell against Sylvan's roughly 20. The optimisation work — a lazily indexed shared
string table, a reused row buffer, and no string materialised for a cell nobody asked for — must
bring that within **three times the runner-up, so at most 60 bytes per cell**, measured on the same
fixture and recorded here. If it does not, this decision is revisited rather than defended: the
cursor's shape exists precisely so that swapping in a Sylvan-backed implementation is one file, and
the sixteen fixtures already stand ready to prove the replacement identical.

**Sylvan.Data.Excel is the runner-up, and the cursor's shape is what keeps that cheap.** Everything
that depends on how xlsx is parsed lives behind `ITabularCursor`; replacing our implementation with a
Sylvan-backed one is one file, and the sixteen fixtures already exist to prove the replacement
behaves identically.

**The comparison is deliberately about parsing, not about typing.** Candidates were asked for cells
normalised to text, which makes readers that return typed values pay for formatting and readers that
return strings pay nothing. The bias favours the string-returning readers, our cursor included, and
it is stated here rather than hidden.
