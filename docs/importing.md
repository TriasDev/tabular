# Importing

The read half: profile a file, map its columns to your fields, check the mapping, import typed rows.
The other direction — writing csv, xlsx, ods and zip files — is [Exporting](exporting.md).

```csharp
using FileStream file = File.OpenRead(path);
using ITabularCursor cursor = TabularFile.Open(file, Path.GetFileName(path));

FileProfile profile = TabularAnalyzer.Analyze(cursor);
// hand `profile` to a mapping screen, or build the plan from the headers (below)
```

`TabularFile.Open` is the one way to a cursor. It tells a csv, xlsx or ods file, a zip or tar archive
and a gzip file apart by their bytes, not their names, and reads each with its own reader; the options
for every one of them are in `TabularOpenOptions`.

```csharp
// Declared once. These same objects build the schema and read the values, so a field's name is
// written in exactly one place.
static class Fields
{
    public static readonly TextImportField Name    = ImportField.Text("name").Require().MaxLength(255);
    public static readonly TextImportField Country = ImportField.Text("countryCode").Require().ExactLength(2);
    public static readonly DateImportField Signed  = ImportField.Date("signedOn");
}

static readonly ImportSchema Schema = new() { Fields = [Fields.Name, Fields.Country, Fields.Signed] };

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

foreach (ImportOutcome<Customer> outcome in run.ReadRows(cancellationToken))
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
run.Summary     // a snapshot: rows read, produced, skipped, failed; whether it stopped early
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
foreach (ImportOutcome<Customer> outcome in run.ReadRows()) { }   // a row at a time

foreach (ImportChunk<Customer> chunk in run.ReadChunks(100_000))
{
    await repository.BulkInsertAsync(chunk.Items);          // a batch at a time
    Report(chunk.Errors);
}

ImportResult<Customer> result = run.ReadAll(limit: 50_000);   // all of it, with a ceiling
```

Five million rows are not five million inserts, which is what `ReadChunks` is for: each batch carries
what was built and the failures from the same window, so the report keeps pace with the writing.
`ReadAll` holds a ceiling because a convenience that quietly consumes a machine is not a convenience.

A run reads its file once, by one of the three: a second read of any kind throws, rather than
quietly returning nothing. `ReadRows` is lazy and counts as a read from the first row it hands out.
Each takes a token; the one the run was started with stops it too. `Summary` is a snapshot of the
counts as they stand when it is taken — take it again once the run is read out.

## If you need the values rather than an entity

`TabularExtractor.Extract` is the layer underneath, and hands back typed values without building
anything. `TabularImporter` is that plus your mapper, and is what a caller normally wants.

A workbook, an OpenDocument spreadsheet, a zip or tar archive or a gzip-compressed file is the same call. Which kind of file it is
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

`ImportSchema.Policy` decides what a partial success means. `BestEffort` imports what fits;
`AllOrNothing` refuses a file that would import partially. That is the programmer's call, not the
uploader's — whether half an import beats none depends on what is being imported, and only whoever
declared the target knows.

Under `AllOrNothing` the precheck blocks on any finding it is sure of, and the run itself stops at
the first row that fails (`Summary.StoppedEarly`). `ReadAll()` then returns no items, only the error, and
the batch from `ReadChunks` that holds the failure carries no items. What a streaming run cannot do is
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

## A column that mixes decimal separators

A hand-assembled file can hold `48,183604` in most rows and `34.020367` in one: a block pasted from
another tool. Under `de-DE` the second is no number, and since a German group has exactly three digits
it cannot be one either — its only reading is 34.020367. The profile counts such values per culture,
as `CultureParseCounts.OtherSeparatorDecimals`, so a screen can say "1 value uses a decimal point"
rather than show an unexplained outlier.

Importing them is the caller's decision, per binding:

```csharp
new ColumnBinding { ColumnIndex = 1, FieldName = "lat", Header = "lat", AcceptOtherDecimalSeparator = true }
```

A decimal field then reads a value written with the other separator, where it can be read no other
way: digits, that one separator, and not exactly three digits after it. `1.234` stays what the
culture makes of it. The precheck judges the plan with the setting, and the run counts the values it
read this way in `ExtractionSummary.OtherSeparatorDecimals`. It is off by default.

## A field the file says in several languages

A catalogue carries `Title#en` beside `Title#de` — two columns saying one thing. Declared once:

```csharp
public static readonly TranslatedImportField Title = ImportField.Translated("title", ["en", "de"]).Require();
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

## Fields that stand in for one another

Some fields are alternatives: a location is given by its coordinates, or — where a row has none — by
an address, and the address itself is a ladder: without a country it locates nothing, a postal code or
a city narrows it, a street and a house number narrow it further. Whether a *particular row* has what
it needs is a question about several columns at once, which no column profile answers: two
half-empty columns can cover each other's gaps. So the schema declares the rule and every row is
judged by it.

In short:

| What | API |
|---|---|
| Declare a set | `new FieldAlternatives(name, [groups…], unresolvedRowFails: false)` in `ImportSchema.Alternatives` |
| A group that needs every field | `AlternativeGroup.AllOf(name, fieldA, fieldB)` |
| A ladder usable from level *n* | `AlternativeGroup.Ladder(name, requiredLevels: n, new AlternativeLevel(name, anyOfFields…), …)` |
| Which group locates a row | `row.Resolution(set)` → `AlternativeResolution { Group, GroupIndex, Level, LevelName, IsResolved }` |
| How a run covered the rows | `ImportRun<T>.Alternatives`, `ExtractionRun.Alternatives` → `AlternativesReport` per set |
| Exact numbers before importing | `TabularExtractor.Review(cursor, plan, schema, ReviewOptions?, progress?, ct)` → `ImportReview` |
| What the mapping alone decides | `MappingPrecheck.Check` → `group.level-unmapped`, `group.unresolved` |
| Which column holds a code list | `ColumnFacts.RowsWithValueIn(new FieldConstraint.AllowedValues(codes, ignoreCase: true))` |

```csharp
--8<-- "samples/TriasDev.Tabular.Samples.Import/Locations.cs:declare"
```

- A **level** is satisfied when *any* of its fields carries a valid value — a postal code and a city
  locate equally well. One field may stand on two levels: a column holding "Main Street 5" satisfies
  the street and the house number.
- A row **reaches** level *k* when levels 1 to *k* are all satisfied; a gap ends the ladder, because a
  street without its city locates nothing finer than the country. A group is **usable** from its
  `RequiredLevels` on: `AllOf` needs every field, the address above needs the country.
- The first usable group **wins** the row. A later group is **needed** only where every earlier one
  fell short, and that is the only place it is judged.

That last point decides validation. The first group's fields are validated like any field: `abc` in
a latitude is a `RowError`, because it cannot be stored as one. A later group's fields are validated
strictly only in rows that need them — a row with good coordinates and the country `XX` imports
cleanly, its `XX` kept as the file holds it, because a caller may store the address even where it
does not locate by it. Such values are counted as `IgnoredInvalidValues` rather than silently
forgotten.

A row no group makes usable is imported and reported by default: the caller may hand every row to a
service that decides better than a file can. `new FieldAlternatives(…, unresolvedRowFails: true)`
makes it a row error, `group.unresolved`, instead.

**Per row**, a mapper can read which group won and how far it reached —
`row.Resolution(location)` gives `Group`, `Level` and `LevelName`. It is information only: every value
of the row is there whichever group won.

**Per run**, `ImportRun<T>.Alternatives` (and `ExtractionRun.Alternatives`) report each set over the
rows that produced values — rows that fail are in the errors, not here, so percentages describe the
rows that will arrive:

- `RowsJudged` — the denominator; `Unresolved` and `UnresolvedRows`;
- per group: `RowsNeeding` (every row for the first group, the rows without usable coordinates for
  the address), `Won`, `RowsAtLevel` (rows by how many levels they reach), `Empty` (needing the group
  and writing nothing into it), `Incomplete` and `IncompleteRows` — needing the group and stopping
  short of `MaxReachableLevel`, the deepest level *the mapping* can reach. An unmapped house number
  therefore does not turn every row into a warning; the precheck says it once.

Row-number lists keep the first `ExtractionOptions.MaxReportedRows` (50 by default, at most 10,000);
the counts are always complete.

### Reviewing a file before importing it

Those numbers are wanted on a screen between the mapping and the import, before anything is written.
`TabularExtractor.Review` is that pass: it reads the whole file through the mapping, keeps no value,
never stops at an error limit (`AllOrNothing` included), reports progress as analysis does, and
returns the summary, the first `ReviewOptions.MaxErrors` row errors and the alternatives report:

```csharp
--8<-- "samples/TriasDev.Tabular.Samples.Import/Locations.cs:review"
```

It costs one more read of the file. On the 5M-row csv, reading eight mapped columns, a review with
the location rule took 3.9 s against 3.7 s for extracting the same columns without it (best of three,
same allocations); a schema without alternatives pays nothing measurable.

The precheck, which needs no second read, says what the mapping alone decides:

- `group.level-unmapped` — a level with no mapped field, so no row reaches past it (`level`,
  `reachableLevel`);
- `group.unresolved` — no group of a set can become usable under this mapping; `Blocking` when the set
  fails such rows, `Warning` otherwise.

Findings on a field of a later group never block: whether a row needs its address is exactly what a
profile cannot tell. To suggest which column holds country codes before anything is mapped,
`ColumnFacts.RowsWithValueIn(allowed)` counts the rows whose value is in a set, from the counts the
analysis already kept.

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
- **In a precheck**, per *distinct value*: a `value.not-allowed` finding with `failingCount` 2 of
  `judgedCount` 5 and the `Examples` `QQ` and `ZZ`. That is the suggestion, and it is what lets a screen say *this is your country column* — or *this is not*,
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
public static readonly TextImportField Isin = ImportField.Text("isin")
    .Require()
    .ExactLength(12)
    .Must("isin.check-digit", CheckDigits.Luhn);

public static readonly TextImportField Lei = ImportField.Text("lei")
    .ExactLength(20)
    .Must("lei.check-digits", CheckDigits.Mod97);
```

It is applied wherever the built-in rules are: to every row at import, where a failure is a
`RowError` carrying that code and the cell's location, and in `MappingPrecheck` to the column's
distinct values — "2 of 1,200 distinct values fail this rule" — or `Undetermined` where the profile
did not keep them all. It is only asked about a value that is present, and typed by its field:
`Func<string, bool>` for text, `long` for integers, `decimal`, `DateTime`.

The code is yours to translate and may not start with `value.`, `mapping.`, `group.`, `structure.`,
`format.`, `limit.` or `write.` (`ErrorCodes.ReservedPrefixes`), so a caller's rule is never mistaken
for one of the library's [error codes](error-codes.md).
`CheckDigits` ships Luhn (card numbers; ISINs, with letters counted as A = 10 … Z = 35) and ISO 7064
MOD 97-10 (LEIs; IBANs with their first four characters moved to the end).

## Writing data back out

An export can be declared against the same fields: `TabularExport.For<T>().Column(field, x => …)` takes
the field's name as the column's header, so the file it writes maps back onto this schema by header.
See [Exporting](exporting.md).
