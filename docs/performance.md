# Performance

**This table is the living one.** Measured with the benchmark project: one process per reading, best
of two runs — the workbooks on 2026-09-25, the csv files on 2026-09-26, after #9 made csv reading and
analysis faster, and the OpenDocument ones (the same two workbooks saved by LibreOffice 26.8) on
2026-09-26.

| Fixture | Rows | Cells | Time | Allocated | Per cell | Peak |
|---|--:|--:|--:|--:|--:|--:|
| 72 KB workbook | 10,759 | 17,340 | 0.07 s | 1 MB | 39 B | 57 MB |
| 8.6 MB workbook, dense | 100,001 | 1,700,017 | 0.87 s | 61 MB | 38 B | 96 MB |
| 101 MB workbook | 1,000,001 | 17,000,017 | 4.6 s | 326 MB | 20 B | 129 MB |
| the 8.6 MB workbook as ods (7 MB) | 100,001 | 1,700,017 | 1.7 s | 53 MB | 33 B | 65 MB |
| the 101 MB workbook as ods (89 MB) | 1,000,001 | 17,000,017 | 8.7 s | 526 MB | 32 B | 65 MB |
| 364 MB csv | 3,000,001 | 51,000,017 | 2.0 s | 1,567 MB | 32 B | 53 MB |
| 572 MB csv, malformed | 5,127,969 | 87,175,473 | 3.0 s | 2,742 MB | 33 B | 53 MB |

The comparison with other libraries (docs/benchmarks.md) measures the same reads through a different
harness and run — 4.30 s for the million-row workbook against 4.6 s here — so quote each table's
figures together rather than mixing them.

Peak memory stays flat as files grow: the 572 MB csv is read in 53 MB, and a workbook of a million
rows in 129 MB. Bytes per cell rises on smaller workbooks because the shared string table is read
once and amortised over fewer cells.

An OpenDocument spreadsheet reads in less memory than the same workbook — 65 MB for the million rows,
since it has no shared string table — and in about twice the time, because it is about four times the
XML: the million rows are 1.9 GB of content, against half a gigabyte of worksheet. The sheet names
are listed by a pass over that content's bytes before the first row, which is 0.2 s of the 100,000-row
file's time.

A zipped csv costs its decompression and nothing else. The 572 MB csv zipped to 179 MB reads in 4.2 s
against 3.0 s unpacked and analyses in 15.6 s against 14.2 s, the same 49 repairs and the same values
to the byte, with a peak of 56 MB against 53 MB (measured 2026-09-26, best of two alternating runs).

All of it is measured on .NET 10. On .NET 8 — measured before #9, so the absolute figures below are
the older ones; the ratio is what they show — the same code reads and imports the 572 MB csv about 15%
slower and analyses it 5–18% slower (import 6.05 s against 5.25–5.32 s; analysis 23.7–23.9 s against
20.1–22.9 s, the widest net10 run being noise), with the same allocations — the runtime's own gains,
not a different code path. Behaviour is identical on both; the test suite runs on each.

**That table is the reader, not the analysis.** Profiling costs more than reading, and how much more
depends on the format: a workbook's numbers and dates arrive already typed and are never parsed,
while every value in a csv is text and is tried under each culture in the options.

| Fixture | Read | Full analysis |
|---|--:|--:|
| 8.6 MB workbook, 1.7M cells | 0.87 s | 1.5 s |
| 101 MB workbook, 17M cells | 4.6 s | 6.7 s |
| the same workbook as ods | 8.7 s | 11.6 s |
| 364 MB csv, 51M cells | 2.0 s | 9.6 s |
| 572 MB csv, 87M cells | 3.0 s | 14.0 s |

At ten thousand rows — the size of a typical upload through a mapping screen — analysis is around a
third of a second, so none of this is a constraint there. The larger figures are quoted for batch
loads, where files are three orders of magnitude larger.

## If analysis ever needs to be faster

Two short-circuits are available and neither is free:

- **Stop parsing a column once one value fails.** Turns the confidence into a boolean. A column then
  reports "not all numbers" instead of "99.8%, and here are the 84 that are not", which is the thing
  the profile exists to say.
- **Stop counting distinct values once one repeats.** Turns `DistinctCount` into `IsUnique`. "195
  distinct" and "five million distinct" become the same answer.

Both are the right trade when a caller only needs the booleans, and both belong behind an option
rather than in the default. What is already done, because it costs nothing: a value is checked for
whether it could be a number at all before any culture is asked to parse it, which skips the attempt
on text columns entirely.

[ADR-0001](adr/0001-tabular-parsing-is-our-own-cursor.md) holds a **different and
deliberately frozen** set: the comparison against every alternative, measured during the spike
against the prototype the decision was made on. Its numbers are not these and are not meant to track
them. Reproduce this table with the benchmark project and `TABULAR_FIXTURES`.
