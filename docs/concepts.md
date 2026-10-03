# How it works

Reads an Excel, OpenDocument or CSV file — or a zip of them — reports what is in it, and extracts it
through a mapping a person confirmed.

The library **declares no package reference of its own**: everything it does with a file, it does
with the base class library. The build adds analyzers, and nothing else; none of them is referenced
by any code here. `TabularIndependenceTests` checks the resolved package graph, not just the project
file, so a parsing library arriving through a transitive reference fails a test.

```
Analyze   file ─────────────────────────► FileProfile
                                          sheets, columns, measured facts,
                                          ranked readings, detected dialect

          a person maps columns to fields, in a UI this library knows nothing about

Extract   file + MappingPlan + schema ──► typed rows, or errors that locate themselves
```

The two are **independent reads of the file**. The library keeps no state between them and has no
persistence; a caller that wants to hold a profile while a user thinks stores it itself.

## Analysis reads every row

Not a sample. The questions a mapping screen has to answer are falsified by a single row anywhere:

> This column holds ISO-3166 country codes. It must be exactly three characters, never a number, and
> may be empty.

One four-character value in the forty-thousandth row *is* the answer, and no sample finds it.

## Facts and hypotheses are different things

- **`ColumnFacts`** is measured. Counts, lengths, ranges, how values fare under each culture,
  how many are distinct, where the ones that did not parse stand.
- **`TypeHypothesis`** is derived, ranked, and carries a confidence and the located outliers. It is a
  suggestion for a UI — it pre-selects a mapping and shows the risk. It never decides anything.

Why that matters, from a real file: a column of `1,00 2,00 3,00` yields a decimal hypothesis at full
confidence. That is arithmetically perfect and practically useless — the column is an identifier, and
only the facts beside it (every value distinct) say so.

## Which columns a sheet has

A sheet's columns reach as far as its last value, in the header row or below it. A row's empty cells
past its last value are padding — a workbook writes cells for their formatting alone, a csv line can
end in delimiters — and make no column. A value anywhere does: a note typed to the right of the
header row, or a value in a column the header does not name, makes a column with an empty `Header`,
and it keeps its index. A consumer that refuses empty headers refuses such a sheet, so it should
decide what an unnamed column that carries data means to it.

## When readings tie

Some values read completely under two cultures as different things. `11.01.2018` is 11 January in
German and 1 November in American; `48.137` is a decimal in English and the grouped integer 48137 in
German. The hypotheses then rank the reading the evidence favours first, and the extremes in the
facts follow it:

- **dates** go to the culture whose date separator the values are written with;
- **numbers** go to the decimal separator the sheet's other, unambiguous columns use, then the one a
  csv's delimiter implies (`;` a comma, `,` a point), and with nothing to go on to the decimal rather
  than the grouped integer.

That is a preference, not evidence, and the facts say so: `DateReadingsDisagree` and
`NumberReadingsDisagree` are true when the cultures that read the most values read some row
differently. A screen can then ask instead of guessing.

A group, by the grouping rule both halves share, is three digits after a group separator; the group
before the first separator may be any length. So under German `1234.567` reads as 1234567, and
`12.34` is no number at all.

## What belongs to the sheet

Each `SheetProfile` says what its own source was: `Format`, `Source` (a path inside an archive, or the name
a gzip header stores, else null), the csv `Dialect` it was read with, and the `Diagnostics` of what was repaired in it.
`FileProfile.Format` is the container's; `FileProfile.Diagnostics` is every sheet together. Both are
snapshots taken when the pass ended.

# What it deliberately does not do

- **Guess where the header is.** The first row is the header. A guess that is usually right produces
  a wrong answer nobody checks; `MappingPlan.HeaderRowIndex` is where a user says otherwise.
- **Decide a column's type.** It proposes. A person disposes.
- **Distinguish an empty field from a quoted empty one.** The syntax does; the data does not.
- **Scan for viruses.** That belongs before a file reaches here.
- **Offer a transformation language.** A list of empty-equivalents, and nothing more,
  because nothing yet asks for more.
- **Count distinct values beyond its budget.** Past it, a column reports a lower bound and an
  undetermined uniqueness — an explicit "not determined" rather than a confident wrong number.
