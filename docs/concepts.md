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

## What belongs to the sheet

Each `SheetProfile` says what its own source was: `Format`, `Source` (a path inside an archive, else
null), the csv `Dialect` it was read with, and the `Diagnostics` of what was repaired in it.
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
