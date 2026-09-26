# Importing

```csharp
using FileStream file = File.OpenRead(path);
using CsvCursor cursor = new(file, Path.GetFileName(path));

FileProfile profile = new TabularAnalyzer().Analyze(cursor);
// hand `profile` to a mapping screen, or build the plan from the headers (below)
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

The plan says which column feeds which field. A mapping screen builds it from the profile; where the
columns are named after the fields, `MappingPlan.ByHeader` builds it without one — headers compared
ignoring case, spaces and `_ - .`, or by a rule of your own (a synonym list, say). Unmatched fields stay
unbound, for `MappingPlanValidator` to report if they are required.

```csharp
MappingPlan plan = MappingPlan.ByHeader(profile.Sheets[0], Schema, culture: "de-DE");
```

A plan built by `ByHeader` also records the sheet's name and source. Extraction checks them against
the sheet at the plan's index and refuses another one (`structure.sheet-changed`), so a workbook whose
tabs were reordered is not imported from the wrong tab; the precheck blocks such a plan with the same
code. A plain csv's name is not recorded — it is whatever name the caller passed in, and the file
has one sheet anyway. A plan written by hand can leave both null.

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

## What a run reports about itself

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

## Three ways to read a run, over one core

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

## If you need the values rather than an entity

`TabularExtractor.Start` is the layer underneath, and hands back typed values without building
anything. `TabularImporter` is that plus your mapper, and is what a caller normally wants.

A workbook, an OpenDocument spreadsheet or a zip archive is the same call. Which kind of file it is
comes from its bytes, not its name — a csv saved as `.xlsx` is commoner than it ought to be, and a
reader that trusts the extension fails on it with a message about a corrupt archive. A zip's
directory says whether it is a workbook, a spreadsheet or an archive of files.

## Checking a mapping before importing through it

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

## Two rules a caller must know

**Rows are views, not copies.** `ImportRow`, `CurrentRow` and `CurrentValues` look at memory that is
overwritten on the next read. `ImportRow` is a `ref struct`, so the compiler will not let one escape
a mapper; the other two are yours to copy from if you keep anything. Handing out a fresh array per
row would cost an allocation per row of a file that may hold millions.

**A field belongs to a schema.** Asking a row for a field the schema does not declare throws, on the
first row. A field left over from another target, or renamed in one place and not the other, is a
defect in the caller and reads as one instead of arriving as an empty column.

**A row is either values or errors, never both.** Half a row invites half an entity, which is how
silent corruption starts.

## A field the file says in several languages

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

## Rows the run drops

A row blank from end to end is padding — a spreadsheet accumulates it below the data as a matter of
course — and it is skipped without comment. A row that carries a value only in a column nobody mapped
is a different thing: a record the file contains and the run drops. `Summary.RowsWithNothingMapped`
counts those separately.

Counted rather than failed. Faulting them would bury a real import under errors about footnotes, and
skipping them silently is the worse mistake: data disappearing quietly is harder to notice than a
number that does not add up.

## What the precheck can and cannot settle

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

## Asking whether a column holds your reference data

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

## Rules of your own

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
