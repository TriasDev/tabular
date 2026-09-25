# TriasDev.Tabular — guide

The full behavioural description: what the library measures, how an import behaves, every error
code and bound. The [README](../README.md) is the short version; [benchmarks](benchmarks.md) compares
it with other libraries.

Reads an Excel or CSV file, reports what is in it, and extracts it through a mapping a person
confirmed.

The library **declares no package reference of its own**: everything it does with a file, it does
with the base class library. The build adds analyzers, and nothing else; none of them is referenced
by any code here. `TabularIndependenceTests` checks the resolved package graph, not just the project
file, so a parsing library arriving through a transitive reference fails a test.

## What it does

```
Analyze   file ─────────────────────────► FileProfile
                                          sheets, columns, measured facts,
                                          ranked readings, detected dialect

          a person maps columns to fields, in a UI this library knows nothing about

Extract   file + MappingPlan + schema ──► typed rows, or errors that locate themselves
```

The two are **independent reads of the file**. The library keeps no state between them and has no
persistence; a caller that wants to hold a profile while a user thinks stores it itself.

### Analysis reads every row

Not a sample. The questions a mapping screen has to answer are falsified by a single row anywhere:

> This column holds ISO-3166 country codes. It must be exactly three characters, never a number, and
> may be empty.

One four-character value in the forty-thousandth row *is* the answer, and no sample finds it.

### Facts and hypotheses are different things

- **`ColumnFacts`** is measured. Counts, lengths, ranges, how values fare under each culture,
  how many are distinct, where the ones that did not parse stand.
- **`TypeHypothesis`** is derived, ranked, and carries a confidence and the located outliers. It is a
  suggestion for a UI — it pre-selects a mapping and shows the risk. It never decides anything.

Why that matters, from a real file: a column of `1,00 2,00 3,00` yields a decimal hypothesis at full
confidence. That is arithmetically perfect and practically useless — the column is an identifier, and
only the facts beside it (every value distinct) say so.

### Malformed input is repaired, and the repair is counted

Files that people upload are not well-formed. A 572 MB real-world export carries quotes inside
unquoted fields (`100 21"st AVE`), text after a closing quote (`"C" Road`), and 302 quotes that
are never closed. Refusing such a file is defensible and useless; one bad address must not fail an
import of five million rows.

So the reader recovers — and counts what it recovered in `CursorDiagnostics`. A silent recovery is
indistinguishable from correct reading, and *that* is the defect: left unbounded, those 302 quotes
swallow about 39,000 records without a word.

## Using it

```csharp
using FileStream file = File.OpenRead(path);
using CsvCursor cursor = new(file, Path.GetFileName(path));

FileProfile profile = new TabularAnalyzer().Analyze(cursor);
// hand `profile` to a UI; get a MappingPlan back
```

```csharp
// Declared once. These same objects build the schema and read the values, so a field's name is
// written in exactly one place.
static class Fields
{
    public static readonly TextField Name    = ImportField.Text("name").Require().MaxLength(255);
    public static readonly TextField Country = ImportField.Text("countryCode").Require().ExactLength(2);
    public static readonly DateField Signed  = ImportField.Date("signedOn");
}

static readonly TargetSchema Schema = new() { Fields = [Fields.Name, Fields.Country, Fields.Signed] };

// One expression turns a row into your own type. It knows nothing about cursors, positions, or the
// order the schema declares.
static Customer Build(ImportRow row) => new()
{
    Name      = row[Fields.Name],
    Country   = row[Fields.Country],
    SignedOn  = row[Fields.Signed],
};
```

```csharp
using FileStream file = File.OpenRead(path);
using ImportRun<Customer> run = TabularImporter.Import(file, Path.GetFileName(path), plan, Schema, Build);

foreach (ImportOutcome<Customer> outcome in run)
{
    if (outcome.HasErrors)
    {
        Report(outcome.Errors);
        continue;
    }

    Persist(outcome.Value);
}

ExtractionSummary summary = run.Summary;
```

Importing a different kind of entity is a different `Fields`, a different `Schema` and a different
`Build`. Nothing else changes.

### What a run reports about itself

```csharp
run.Summary     // rows read, produced, skipped, failed; whether it stopped early
run.Coverage    // per field: how many rows carried a value, and the share
run.Preview     // the first N rows as mapped, when PreviewRows asked for any
```

`Coverage` is counted for every run, because it costs one check per field and answers the question an
error list cannot: whether the columns a person mapped actually carry anything. A field bound to the
wrong column is not *invalid* — it is empty, so it passes every rule and shows up nowhere else.

`Preview` is off by default. A screen showing a person what an import will produce asks for a
handful; a nightly load of five million rows has nobody to show them to.

### Three ways to read a run, over one core

```csharp
foreach (ImportOutcome<Customer> outcome in run) { }        // a row at a time

foreach (ImportChunk<Customer> chunk in run.InChunks(100_000))
{
    await repository.BulkInsertAsync(chunk.Items);          // a batch at a time
    Report(chunk.Errors);
}

ImportResult<Customer> result = run.All(limit: 50_000);     // all of it, with a ceiling
```

Five million rows are not five million inserts, which is what `InChunks` is for: each batch carries
what was built and the failures from the same window, so the report keeps pace with the writing.
`All` holds a ceiling because a convenience that quietly consumes a machine is not a convenience.

### If you need the values rather than an entity

`TabularExtractor.Start` is the layer underneath, and hands back typed values without building
anything. `TabularImporter` is that plus your mapper, and is what a caller normally wants.

A workbook is the same call. Which kind of file it is comes from its first bytes, not its name — a
csv saved as `.xlsx` is commoner than it ought to be, and a reader that trusts the extension fails on
it with a message about a corrupt archive.

### Checking a mapping before importing through it

The file was profiled once, over every row. Asking what that already measured is cheaper than
learning it again row by row, after some records have been written:

```csharp
PrecheckResult check = MappingPrecheck.Check(plan, Schema, profile);

if (!check.CanImport)
{
    return Refuse(check.Findings);
}
```

It answers what a row cannot. Whether a column's values are all different is a property of the
column, so no per-row rule can decide it — `ImportField.Text("cid").Require().Unique()` is settled
here, exactly, because the distinct values were already counted.

A finding says *these rows will fail*, or *no row can succeed*, or *this cannot be judged from
here*. It never says the rest is fine: an allowed-value set is measured against a bounded sample, and
a rule spanning two fields is invisible to facts about one.

`TargetSchema.Policy` decides what a partial success means. `BestEffort` imports what fits;
`AllOrNothing` refuses a file that would import partially. That is the programmer's call, not the
uploader's — whether half an import beats none depends on what is being imported, and only whoever
declared the target knows.

Under `AllOrNothing` the precheck blocks on any finding it is sure of, and the run itself stops at
the first row that fails (`Summary.StoppedEarly`). `All()` then returns no items, only the error, and
the batch from `InChunks` that holds the failure carries no items. What a streaming run cannot do is
take back batches it handed out before the failure: a caller writing batch by batch commits once, at
the end, or rolls back.

### Two rules a caller must know

**Rows are views, not copies.** `ImportRow`, `CurrentRow` and `CurrentValues` look at memory that is
overwritten on the next read. `ImportRow` is a `ref struct`, so the compiler will not let one escape
a mapper; the other two are yours to copy from if you keep anything. Handing out a fresh array per
row would cost an allocation per row of a file that may hold millions.

**A field belongs to a schema.** Asking a row for a field the schema does not declare throws, on the
first row. A field left over from another target, or renamed in one place and not the other, is a
defect in the caller and reads as one instead of arriving as an empty column.

**A row is either values or errors, never both.** Half a row invites half an entity, which is how
silent corruption starts.

### A field the file says in several languages

A catalogue carries `Title#en` beside `Title#de` — two columns saying one thing. Declared once:

```csharp
public static readonly TranslatedField Title = ImportField.Translated("title", ["en", "de"]).Require();
```

Underneath these are ordinary fields with ordinary names, `title.en` and `title.de`, each fed by one
column. That is the point of the shape: a binding, a plan and a row are exactly what they were, so
nothing downstream learns about languages. What the group adds is the three things the two columns
have in common — a screen can draw them together, `Required` means *at least one of them* rather than
each, and the mapper gets them back as one value:

```csharp
TitleTextValues = row.Translations(Fields.Title)   // { "en": "…", "de": "…" }
```

A language the row left empty is absent from that dictionary rather than present and empty: "not
translated" and "translated to nothing" are different things to whatever stores the result.

`Required` on a group is deliberately weak. A catalogue translated into German alone is a complete
catalogue, and a rule naming English would refuse it for saying nothing wrong. A row carrying none of
the languages fails once, as `group.required`, rather than once per declared language.

The precheck reaches one of the two conclusions here and says so. If every column mapped to the group
is empty from top to bottom, no row can carry any of them — that is certain, and it blocks. Whether
*some particular row* leaves all of them empty is not: two columns can be empty in complementary
halves of a file and cover every row between them. That is settled per row, while importing.

Where the file marks its languages in the headers, `HeaderVariant.TryParse` reads the mark — `#`,
`_`, `-`, `.`, `@`, a space, or brackets — but only for a variant the group declares, so `Order_id` stays
one field. It proposes; it never decides. A header is written by whoever exported the file, and
acting on it silently is the worst mistake available here: the text arrives, it is simply filed under
the wrong language, and nobody finds out until a reader of that language does.

### Rows the run drops

A row blank from end to end is padding — a spreadsheet accumulates it below the data as a matter of
course — and it is skipped without comment. A row that carries a value only in a column nobody mapped
is a different thing: a record the file contains and the run drops. `Summary.RowsWithNothingMapped`
counts those separately.

Counted rather than failed. Faulting them would bury a real import under errors about footnotes, and
skipping them silently is the worse mistake: data disappearing quietly is harder to notice than a
number that does not add up.

### What the precheck can and cannot settle

It answers from the profile, which was measured before anybody built a mapping. So it can only be
right about the import if it measured what the import will do, and three things used to make that
false — every one of them producing findings about values no row would ever hold.

Trimming is part of reading now, unconditionally, so `" DE "` is two characters to both halves of the
library rather than four to one of them. It was a per-binding option applied only while extracting,
which is a disagreement waiting to happen rather than a feature: leading whitespace in a cell is an
artefact of how it was typed, not a value.

A binding's empty-equivalents are subtracted before a column is judged, because `k.A.` is not a
country that failed to be allowed — it is the file saying it has nothing to say. Where they are
declared, the count of rows leaving a required field empty becomes *at least* N, since those rows
were measured as values and will be read as absent.

And a profile carries the header row it was measured against. When a mapping names a different one,
every fact describes a different file — the real header and everything above it were counted as data
— so the precheck reports `mapping.stale-profile` and judges nothing. Undetermined rather than
blocking: the file is very likely fine and it is the profile that is stale. Analysing it again under
the chosen row is what settles it.

Every rule is judged the same way: each of the column's distinct values is put back into the cell it
came out of and read exactly as the extractor would read it — same reader, same cell, same culture,
same type — and then the rule itself is asked. Four separate judges stood here before, three of which
compared the file's characters while the import compared what it renders from them. A pattern of three
digits passed `007` and then failed every row, because the import sees `7`.

*Same cell* is not decoration. A workbook types its own cells and the extractor takes such a cell at
its word; the profile keeps distinct values as text, so re-reading that text under the mapping's
culture answers a question the import never asks. A native `1234.5` read back under German — where the
point is a group separator — becomes 12345, and the precheck refused a file that imports perfectly.
That is the same defect as the one above, closed for csv and reopened for workbooks, and it is why
the value is rebuilt into its declared kind first.

That is affordable because it runs once per *distinct* value rather than once per row, and it is only
attempted where the profile kept every distinct value. Where it did not, nothing is claimed.

The header a binding recorded is compared with the one the column now carries, because extraction
refuses the whole run over that and the precheck used to be the only thing that could not see it.

One more rule governs when a finding may say *no row can succeed*: only when every row carries a
value. Facts about values are measured over the non-empty cells and a rule about values is never
applied to a cell without one, so a column of ten thousand blanks and one bad value fails one row and
imports the rest — unless the field is required, which makes an empty cell a failure on its own
account.

### Asking whether a column holds your reference data

Length and type say a column *could* be a country code. They cannot say it *is* one — `ZZ` is two
characters and parses as text exactly like `DE`. That question is about the values, and it is
answered against a set the library never sees:

```csharp
ImportField.Text("countryCode").ExactLength(2).AllowedValues(CountryCodes)
```

Declared once, it is enforced twice, and the two are not the same thing:

- **On import**, per row: a row carrying `ZZ` is refused with `value.not-allowed`. That is the
  guarantee.
- **In a precheck**, per *distinct value*: "2 of 5 distinct values are not allowed: QQ, ZZ". That is
  the suggestion, and it is what lets a screen say *this is your country column* — or *this is not*,
  when every value is a stranger.

The second is affordable because a column of codes is low-cardinality by nature — ISO 3166 has some
250 members, the ELF list of legal forms some 2,600 — so the work is bounded by the column's variety
rather than by the file's length. `ColumnFacts.DistinctValues` carries them, and
`DistinctValuesAreComplete` says whether it is the whole set. When it is not, the precheck answers
`Undetermined` rather than drawing a conclusion from a subset: a column with fifty thousand distinct
values is not a column of codes, and the per-row check is what stands behind it.

Measured cost of keeping them, against not keeping them — same binary, one option apart, best of
seven for the small files and of three for the large one:

| file | rows | time, off → on | allocated, off → on |
|---|--:|--:|--:|
| 10k-row workbook | 10,007 | 269 → 273 ms | 18.52 → 18.66 MB (+0.8%) |
| 10k-row csv | 10,000 | 168 → 170 ms | 12.10 → 12.25 MB (+1.2%) |
| 5M-row csv | 5,127,959 | 22.60 → 22.42 s | 8,372.50 → 8,372.70 MB (+0.002%) |

The time differences are inside run-to-run variance in both directions. The memory cost does not grow
with the file — it is a few hundred kilobytes for a thousand strings per column, spent once — which
is the property that makes the check affordable at all: it falls on how varied a column is, never on
how long the file is.

### Rules of your own

A pattern checks an identifier's shape; many identifiers also carry a check digit that a pattern
cannot see. `Must` declares a rule from a predicate, under a code of the caller's choosing:

```csharp
public static readonly TextField Isin = ImportField.Text("isin")
    .Require()
    .ExactLength(12)
    .Must("isin.check-digit", CheckDigits.Luhn);

public static readonly TextField Lei = ImportField.Text("lei")
    .ExactLength(20)
    .Must("lei.check-digits", CheckDigits.Mod97);
```

It is applied wherever the built-in rules are: to every row at import, where a failure is a
`RowError` carrying that code and the cell's location, and in `MappingPrecheck` to the column's
distinct values — "2 of 1,200 distinct values fail this rule" — or `Undetermined` where the profile
did not keep them all. It is only asked about a value that is present, and typed by its field:
`Func<string, bool>` for text, `long` for integers, `decimal`, `DateTime`.

The code is yours to translate and may not start with `value.`, `mapping.`, `group.` or
`structure.`, so a caller's rule is never mistaken for one of the library's codes below.
`CheckDigits` ships Luhn (card numbers; ISINs, with letters counted as A = 10 … Z = 35) and ISO 7064
MOD 97-10 (LEIs; IBANs with their first four characters moved to the end).

## Error codes

The library reports codes and never messages: it knows nothing about who reads them or in what
language. A calling domain maps them onto its own error envelope, and a frontend derives its wording
from them.

| Code | Meaning |
|---|---|
| `value.required` | A required field's cell was empty |
| `value.type-mismatch` | The value does not read as the field's type |
| `value.exact-length`, `value.min-length`, `value.max-length` | A length rule |
| `value.out-of-range` | A minimum or maximum |
| `value.not-allowed` | Outside the allowed set |
| `value.not-unique` | The column repeats a value, and the field identifies a record |
| `value.pattern` | Did not match the pattern |
| `group.required` | A row carries none of a group's variants |
| `mapping.unknown-field`, `mapping.duplicate-binding`, `mapping.required-field-unmapped`, `mapping.required-group-unmapped` | A plan that does not fit its schema |
| `mapping.invalid-column`, `mapping.invalid-header-row`, `mapping.invalid-sheet`, `mapping.unknown-culture` | A plan that is malformed |
| `mapping.stale-profile` | The profile was measured against a different header row |
| `mapping.header-changed` | The column's header is not the one the mapping recorded |
| `mapping.invalid-plan` | `MappingPlanException`: the plan does not fit its schema; its `Faults` carry the codes above |
| `structure.sheet-missing`, `structure.header-row-missing`, `structure.header-changed` | `TabularStructureException`: the file is not the one the plan was built for |
| `format.unsupported`, `format.corrupt`, `format.truncated` | `TabularFormatException`: not a format this library reads (.xls, .xlsb, .ods, binary), or damaged, or cut off |
| `limit.exceeded` | `TabularLimitException`: a bound was exceeded; `Limit` names the option, `Maximum` its value |

This table is checked against the library's sources by `ErrorCodeCatalogTests`, in both directions.
It went out of step twice in the branch that added it — a code emitted, asserted, given a requirement
and described in this file's own prose, and left out of the table a frontend reads. Now it cannot.

Faults that invalidate a whole run are exceptions, not row errors — the two demand opposite
responses. All of them derive from `TabularException`, which carries a `Code` from the table, and
split by what a host does about them:

| Exception | Means | Typical HTTP answer |
|---|---|---|
| `TabularFormatException` | Not a file this library reads, or not a readable one | 400 / 415 |
| `TabularLimitException` | Readable, but beyond a configured bound — how most hostile files end | 413 |
| `TabularStructureException` | Not the file the plan was built for (sheet, header row or header changed) | 409 / 422 |
| `MappingPlanException` | The plan does not fit its schema, before any file is read | 400 |

Mistakes in the calling code — a null argument, an option out of range, a field the schema does not
declare — are `ArgumentException` and `InvalidOperationException`. Nothing else escapes: malformed
XML and a damaged zip are reported as `TabularFormatException` with the parser's error as the inner
exception.

## What it deliberately does not do

- **Guess where the header is.** The first row is the header. A guess that is usually right produces
  a wrong answer nobody checks; `MappingPlan.HeaderRowIndex` is where a user says otherwise.
- **Decide a column's type.** It proposes. A person disposes.
- **Distinguish an empty field from a quoted empty one.** The syntax does; the data does not.
- **Scan for viruses.** That belongs before a file reaches here.
- **Offer a transformation language.** A list of empty-equivalents, and nothing more,
  because nothing yet asks for more.
- **Count distinct values beyond its budget.** Past it, a column reports a lower bound and an
  undetermined uniqueness — an explicit "not determined" rather than a confident wrong number.

## Cancellation

`ReadRow` takes a `CancellationToken`, and the analyzer and importer hand theirs down rather than
only checking between rows. That distinction is the whole of it: the expensive things happen *inside*
a single read — a shared string table of a million entries loads on the first cell that refers to
one — and a check between rows never runs while that is happening. A timeout at the request level
cannot reach a thread that is inside such a call.

The token is checked on a stride rather than per character, because the check is cheap but not free
and a row is normally over in a few hundred characters.

## Progress

Analysis of a multi-million-row file takes seconds to tens of seconds, and a screen waiting on it
wants to say how far it has got:

```csharp
IProgress<AnalysisProgress> progress = new Progress<AnalysisProgress>(p =>
    Console.WriteLine($"{p.SheetName}: {p.RowsRead:N0} rows, {p.Fraction:P0}"));

FileProfile profile = new TabularAnalyzer().Analyze(cursor, progress, cancellationToken);
```

The fraction is taken from how much of the file the reader has consumed — a csv's stream position
against its length, a workbook's worksheet bytes against their total, which the package directory
states up front — so nothing reads the file twice to have a denominator. It is exactly 1 in the final
report, which has `IsComplete` set; where the stream has no length it is null until then.

Reports go out when the fraction has moved by `AnalysisOptions.ProgressStep` (1% by default) **and**
at least `ProgressInterval` data rows (10,000) have passed since the last one. A five-million-row
file reports about a hundred times; a file of twenty thousand rows, read in milliseconds, once or
twice. With no length to measure, the row interval alone decides. `Progress<T>` posts each report to
the context it was created on; an `IProgress<T>` of your own is called on the analysing thread.

## Cultures

Every text value is tried under each culture in `AnalysisOptions.Cultures`, by default
`["", "de-DE", "en-US"]` — invariant, German and US conventions — and the ranked hypotheses name the
culture that read a column — the empty string for the invariant one, the same spelling
`MappingPlan.Culture` takes, so a hypothesis's culture goes into a plan as it is. The default leans towards the files this library was first written for;
a caller whose files come from elsewhere should list its own (`["", "fr-FR"]`). An unknown name is
refused when the analyzer is created.

Under invariant globalization (`InvariantGlobalization` / `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT`,
common in slim container images) only the invariant culture exists. Analysis then leaves the named
cultures out instead of failing — the profile's `ParseCounts` show which cultures were used — and a
mapping that names one is refused by the validator and the precheck as `mapping.unknown-culture`.
German amounts such as `1.234,50` cannot be read as numbers in that mode.

## Bounds

| | Default | Why |
|---|---|---|
| Package expansion | 2 GB | A zip's ratio is unbounded by design; a 50 MB upload could otherwise become fifty gigabytes |
| Package parts | 16,384 | Every entry's metadata is materialised to find parts by name, before any budget can be consulted |
| Worksheets | 4,096 | One descriptor per sheet, held for the cursor's life and walked by anything that analyses the file |
| Workbook relationships | 8,192 | A map built before anything reads from it; a workbook declares about one per sheet |
| Shared string entries | 1,048,576 | The sheet's own row count — more distinct strings than a column can hold cells |
| Shared string characters | 64 M | What a table costs is its characters, not its entries — a million entries of two thousand each is 3.9 GB |
| Cell formats | 100,000 | Read in the constructor, before a caller has anything to cancel with; the format itself stops at 65,490 |
| Cell value length | 16 M chars | A value is assembled from as many small runs as a file cares to write, none of them large |
| Package metadata string | 2,048 chars | A sheet name, a relationship target, a format code — each had a ceiling on how many, none on how long |
| Columns per row | 16,384 | The workbook format's own width. A csv has none, and a file of nothing but delimiters is the cheapest attack there is |
| Field length (csv) | 16 M chars | Held twice while a quoted field is open, once as the value and once as the text kept for a replay |
| Quoted field length | 4 lines | Beyond that an opening quote was never syntax |
| Distinct tracking | 2,000,000 values | Exact counting costs memory in proportion; the budget is per file, not per column |
| Retained distinct values | 1,000 per column | Enough to judge a column of codes against a reference set; a column with more is not one |
| Error rows | 1,000 | A wrong mapping fails every row, and the thousand-and-first error says nothing the first did not |

Getting these right took three attempts, and the pattern of the mistakes is worth more than the
numbers. The package budget counts bytes a part expands to, which is not what those bytes become in
the heap — so a shared string table needed a ceiling of its own, and the first such ceiling counted
entries, which a million two-thousand-character entries satisfy at a cost of 3.9 GB. Then a review
found three more collections growing from the file with no ceiling at all, one of them beside a list
that had just been given one. Every ceiling here now guards a quantity that was enumerated rather than
noticed — but the package budget above them still counts the wire, which is why each of the others
exists.

Distinct counting stores 64-bit hashes rather than strings. By the birthday bound the chance of an
accidental collision is about one in 37 million over a million values, and about one in 9 million
over the two million the default budget tracks — it rises with the count, not falls. Small enough to
call the count exact; not zero, and a collision undercounts.

## Performance

**This table is the living one.** Measured 2026-09-25 with the benchmark project: one process per
reading, best of two runs.

| Fixture | Rows | Cells | Time | Allocated | Per cell | Peak |
|---|--:|--:|--:|--:|--:|--:|
| 72 KB workbook | 10,759 | 17,340 | 0.07 s | 1 MB | 39 B | 57 MB |
| 8.6 MB workbook, dense | 100,001 | 1,700,017 | 0.87 s | 61 MB | 38 B | 96 MB |
| 101 MB workbook | 1,000,001 | 17,000,017 | 4.6 s | 326 MB | 20 B | 129 MB |
| 364 MB csv | 3,000,001 | 51,000,017 | 2.4 s | 1,567 MB | 32 B | 53 MB |
| 572 MB csv, malformed | 5,127,960 | 87,175,320 | 4.2 s | 2,742 MB | 33 B | 53 MB |

Peak memory stays flat as files grow: the 572 MB csv is read in 53 MB, and a workbook of a million
rows in 129 MB. Bytes per cell rises on smaller workbooks because the shared string table is read
once and amortised over fewer cells.

**That table is the reader, not the analysis.** Profiling costs more than reading, and how much more
depends on the format: a workbook's numbers and dates arrive already typed and are never parsed,
while every value in a csv is text and is tried under each culture in the options.

| Fixture | Read | Full analysis |
|---|--:|--:|
| 8.6 MB workbook, 1.7M cells | 0.87 s | 1.6 s |
| 101 MB workbook, 17M cells | 4.6 s | 7.8 s |
| 364 MB csv, 51M cells | 2.4 s | 14.9 s |
| 572 MB csv, 87M cells | 4.2 s | 24.6 s |

At ten thousand rows — the size of a typical upload through a mapping screen — analysis is around a
third of a second, so none of this is a constraint there. The larger figures are quoted for batch
loads, where files are three orders of magnitude larger.

### If analysis ever needs to be faster

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

## Documents

| | |
|---|---|
| [benchmarks.md](benchmarks.md) | Reading speed and memory against the common csv and xlsx libraries |
| [KNOWN-ISSUES.md](KNOWN-ISSUES.md) | What review found and we chose not to fix yet, with what would make each one matter |
| [IDEAS.md](IDEAS.md) | What the design would accommodate and nobody has asked for |
| [ADR-0001](adr/0001-tabular-parsing-is-our-own-cursor.md) | Why the parsing is ours, with the measurements the choice was made on |

## Known issues

What review found and we chose not to fix yet, with what would make each one matter:
[KNOWN-ISSUES.md](KNOWN-ISSUES.md).

## Ideas

What the design would accommodate and nobody has asked for, so that an idea worth half an hour is not
re-thought from scratch in a year: [IDEAS.md](IDEAS.md).

## Tests

The fixtures are built from raw bytes and raw OOXML rather than through a writer, because the shapes
worth pinning are the ones a well-behaved writer never emits: a shared string split into formatting
runs, an inline string, the 1904 date epoch, a row starting past column A, a quote where no
specification allows one.

The large fixtures behind the performance tables are not in the repository, and no test reads them.
The benchmark project does: set `TABULAR_FIXTURES` to their folder and `TABULAR_FILES` to the file
names to measure.
