# Known issues

Things four review passes found in `TriasDev.Tabular` that were **not** fixed, with enough detail to
act on when one of them stops being theoretical.

Nothing here is a defect that loses or corrupts data — those were fixed when they were found, each
with a test that reproduced it first. That sentence was false for a while: a review round found that a
cancelled read left the cursor mid-record and the next read presented the remainder as a complete row.
It is fixed, and the sentence is worth re-earning rather than assuming. What remains is behaviour that is wrong at the edges, contracts
that are looser than they read, and gaps in coverage. Each entry says what would make it matter,
because the point of writing them down is to recognise the day one of them arrives rather than to
carry a backlog.

Reviewed 2026-08-26, before the library moved to its own repository.

---

## Correctness at the edges

### A `numFmt` inside `<dxfs>` can overwrite a cell format
Number formats are collected from anywhere in `styles.xml`. Differential formats — used by
conditional formatting — live in `<dxfs>` and carry their own ids. One colliding with a custom id
(164 and up) flips a column between numbers and dates.

**Matters when** a file uses conditional formatting *and* custom number formats, and the ids collide.
Fix by tracking whether the reader is inside `<numFmts>`, as it already does for `<cellXfs>`.

### Number formats the reader does not know are dates
`IsBuiltInDateFormat` accepts 14–22 and 45–47. The specification also reserves 27–36 and 50–58 for
dates in East Asian locales. A cell using one reads as a number.

### `applyNumberFormat="0"` is ignored
The attribute says the format is not applied. Honouring it would change whether a styled cell is read
as a date.

### `t="b"` accepts only `"1"`, and `date1904` only `"1"` and `"true"`
The schema type is `xsd:boolean`, which also permits `"true"`/`"false"` and `"0"`. A writer using the
long spelling produces a boolean read as false, or a 1904 workbook read as 1900 — the latter is a
four-year error.

### Relationship targets are not percent-decoded
Parts are found through the package relationships, and targets are resolved against their folder,
`../` included. A target that percent-encodes its name (`sheet%201.xml`) is not decoded, so it is
looked up literally and not found. No producer seen writes one.

### A whitespace-only string is dropped one way and kept the other
A shared string of spaces without `xml:space="preserve"` is dropped, because that path reads with
`XmlReader` and `IgnoreWhitespace`. The same content written inline is kept, because the scanner has
no such notion. Same value, two answers, depending only on how the writer chose to store it.


### Chartsheets and hidden sheets are indistinguishable from ordinary ones
`SheetInfo` carries a name and an index. A caller cannot tell a hidden sheet, or a chartsheet with no
cells, from a sheet the user meant.

---

## Contracts looser than they read

### A number written in exponential notation is read as text

Both the profiler and the extractor parse decimals with `NumberStyles.Number`, which does not include
`AllowExponent`. Measured against .NET rather than inferred:

| value | `NumberStyles.Number` | `NumberStyles.Float` |
|---|---|---|
| `5.4176e-03` | rejected | 0.0054176 |
| `1E+5` | rejected | 100000 |
| `-2.5e2` | rejected | -250 |

The consequence is worse than a refused row, because it happens at analysis too: a column of such
values is profiled as text, so a decimal is never proposed for it, and a mapping to a decimal field
then fails every row with `value.type-mismatch`.

Found on a real export — a column carried `5.4176e-03` — so this is not hypothetical.
`NumberStyles.Float` would fix both places, and the reason it has not been changed yet is not the one
this entry used to give. `Float` does **not** include `AllowThousands`, which `Number` does — measured,
`1,234.56` reads under `Number` and is rejected under `Float`. Losing grouped numbers in a library
whose stated subject is telling `1.234,56` from `1,234.56` would be a worse defect than the one being
fixed, so the change is `Number | AllowExponent` rather than `Float`, and it has to land in the
profiler and the extractor together or a column will be proposed as a decimal the extractor refuses.

**Matters when** any file carries scientific notation — scientific instruments, financial exports and
anything that has been through a naive `double.ToString()` all do.

### A date must carry two separators, so `15 Jan 2023` is text

`DateReading.LooksLikeOne` requires two of `-`, `/`, `.`, or a colon, before a parse is attempted.
That is what refuses a decimal (`1.5` reads as the fifth of January under en-US) and a day-and-month
(`3/15` takes the current year), and the price is a date written with spaces and a month name.

A genuine `0001-01-01` is refused for the neighbouring reason: year one is the marker that the text
named no year.

**Matters when** a customer exports dates in a long form. The fix is a parse against the culture's
year-bearing patterns rather than a shape test, which is more code than the case has so far earned.

### A range constraint on a non-numeric field is silently a no-op
`MinValue`/`MaxValue` compare `MappedValue.Number`, which is zero for text, dates and booleans. So a
range on a date field is unsatisfiable and a range on a text field compares against zero. Date ranges
— the obvious second use — cannot be expressed at all. The validator does not reject the combination
either.

**Matters as soon as** a caller puts a date range in a schema. Fix by comparing per type and by
refusing the combination in the validator.

### `ExtractionSession` implements `IDisposable` and owns nothing
`Dispose` sets a flag. It reads as ownership of the cursor, which it does not have.

### `TabularAnalyzer` casts to `CsvCursor` to read the dialect
The whole argument for `ITabularCursor` is that an implementation can be swapped. Swap it and
`FileProfile.Dialect` silently becomes null while the format still says csv. The dialect belongs on
the interface.

### `CsvCursor.MoveToSheet` does not rewind
The interface documents "positions before its first row"; the csv implementation returns `index == 0`
and stays where it is. Analysing and then extracting through one cursor instance reads a csv from
wherever it stopped.

### `"invariant"` and `""` are two spellings of one culture
`TypeHypothesis.Culture` reports `"invariant"`; `MappingPlan.Culture` expects the empty string. A UI
doing the obvious thing — take the top hypothesis's culture, put it in the plan — is told the culture
is unknown.

### `IsBlank` ignores the binding's empty-equivalents
A row whose every mapped cell holds `k.A.` is not skipped. It is counted as produced and handed over
as a valid row of entirely absent values.

### `RowError.RawValue` is the trimmed value
The spec says the value "as it appeared in the file". Trimming is on by default, so a value failing a
length rule *because of* its spaces is reported without them, pointing at something that looks right.

### `ValidateOnly` returns a full-length span of absent values
Indistinguishable from a row where every field was legitimately empty.

### `MappedValue` accessors throw, and say nothing about it
`Date` on a decimal value throws; `Integer` on a large one throws. `RawCell` documents exactly this
hazard for the identical design. `MappedValue.Absent` also reports `Type == Text`, so an absent value
is indistinguishable from an absent text value.

### `MappedValue.FromDate` loses the time of day in `Text`
Every length, pattern and allowed-value constraint sees `Text`, so a datetime is validated against a
date-only string while `Date` still carries the ticks.

### `CsvDialect` lets a caller claim a value was detected
`EncodingSource` and `DelimiterSource` are `required` on a record a caller constructs to *override*
detection. The provenance exists so a UI can tell a fact from a guess; a hand-built dialect can lie
about it.

### A byte order mark overrides a caller-specified encoding
`StreamReader` is constructed with `detectEncodingFromByteOrderMarks: true` alongside the chosen
encoding.

### The dialect override is all or nothing
A caller who knows only the delimiter must also supply the encoding, losing detection for it. There
is no per-property override, no line-ending member, and the quote character is hard-coded with no
provenance.

### Options records validate nothing
Zero and negative values are accepted for every bound. `MaxErrorRows = 0` stops after the first
failing row; a negative probe size throws from inside a constructor; an unknown culture name in
`AnalysisOptions.Cultures` throws from deep inside a pass.

### Implementation types are public
`ColumnProfiler`, `DistinctBudget` and `CsvDialectDetector.Detect` are on the public surface with no
caller. They become API a later refactor cannot move.

---

## Resource bounds that are narrower than they look

### The package ceiling counts bytes off the wire, not memory
`MaxUncompressedBytes` sums the entries' declared sizes. Text decoded to UTF-16 doubles, and a buffer
that grows to hold a token peaks at three times its content. The scanner buffer and the csv field
have their own ceilings, which carry most of this weight, but the package ceiling by itself is not
the bound it appears to be.

### A pattern constraint is bounded per value, not per run
The match timeout is 100 ms. A pattern using a lookaround falls back to the backtracking engine, and
a million rows at 100 ms each is a run measured in hours. Compilation is not bounded at all: a
pattern with large counted quantifiers costs time and memory before any value is seen.

**Matters when** patterns become admin-authored or config-driven rather than domain-authored.

### Per-column allowances multiply by the column count
Each column keeps ten first values, sixty-four frequency keys and up to a hundred and twenty located
outliers, each an unbounded string. The column count is now bounded by the format's last column, so
the product is bounded — but it is 16,384 times those allowances.

### A workbook's own strings have no length ceiling
Sheet names, relationship targets and number-format codes are taken straight from the package with a
ceiling on how many there may be and none on how long each may be. Measured: 4,096 sheets — exactly
the permitted number — with 400,000-character names is a 1.65 MB upload that retains 3,125 MB for the
cursor's whole life.

This is the third appearance of one mistake: **a ceiling on the number of things rather than on their
size.** It was fixed for the shared string table, which has both, and left standing on its three
siblings.

**Matters when** somebody sends a file built to do this. An ordinary export cannot reach it.

### The stream is not disposed if the archive fails to open
If `ZipArchive`'s constructor throws — the common case for a non-workbook upload — the caller's stream
is not disposed even when `leaveOpen` is false.

### A sheet whose part is missing aborts the analysis
`MoveToSheet` throws rather than returning false, so a workbook declaring a sheet whose part is absent
fails the whole pass instead of skipping it.

---

## Coverage

### The suite assumes the German and English cultures exist
Under `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` the library degrades as the guide describes — named
cultures are left out of analysis, a plan naming one is refused as `mapping.unknown-culture` — but
about seventy tests exercise de-DE and en-US readings directly and fail there by design. A CI leg in
invariant mode should run a subset that does not depend on them.

### One error code is asserted nowhere
`value.min-length`. The codes are a frontend's translation contract; swapping two of them leaves the
suite green.

The count used to be wrong in both halves at once — it said four of nineteen when it was two of
twenty — so the catalog itself is now pinned by `ErrorCodeCatalogTests` rather than described here.

### The typed fast paths in extraction are untested end to end
Every extraction test uses `CsvCursor`, which emits only text and empty cells. The branches that take
a workbook's own number, date and boolean are never exercised.

### Nothing pins the library's culture-cleanliness
The library never touches `CurrentCulture` and the suite passes under German and Turkish locales, but
nothing would *fail* if a `_culture` argument were dropped. On a US runner that regression is
invisible.

### The test project builds with warnings, and nobody counts them any more
Mostly `xUnit1051`, which arrived when `ReadRow` gained a cancellation-token overload and every
existing call in a test stopped passing one, plus `CS8631` from `Assert.Equal` binding to the span
overload. The library itself is at two, `S3236` and `S3267`.

No number here, deliberately. This entry carried one for four revisions and was wrong in all four —
twice from an incremental build, which reports only what it rebuilt, once because a commit added an
overload to a method every test calls, and once because the figure was already stale in the commit
whose subject was *re-measure what was claimed*. A count in prose is a claim that ages badly and
nothing checks. Run it instead:

```
dotnet build tests/TriasDev.Tabular.Tests/TriasDev.Tabular.Tests.csproj -c Debug --no-incremental \
  | grep -oE '[^/]+\.cs\([0-9]+,[0-9]+\): warning [A-Za-z0-9]+' | sort -u | wc -l
```

### Assorted weak tests
Several tests assert less than their names claim — the delimiter-consistency tests never reach the
consistency rule, `TabularAnalyzerTests`' cancellation test cancels before the pass begins rather than
during it, and the "no values for a failing row" invariant is never actually read.

Two that were on this list have been fixed rather than recorded: the quote-storm guard now compares
allocation across two input sizes instead of asserting a wall-clock ceiling no defect could exceed,
and `ExtractionSessionTests` cancels after a row rather than before the first.

---

## Deliberate, not deferred

Recorded so they are not re-raised as findings.

- **Synchronous throughout.** Parsing is processor work over a buffered stream, and a row cannot be a
  `ReadOnlySpan<T>` and be awaited at once. See the design document.
- **No comment syntax in csv.** The format does not define one. Add it if the files we receive use it.
- **The header is the first row.** No heuristic looks elsewhere; `MappingPlan.HeaderRowIndex` is where
  a user says otherwise.
- **`"C" Road` is repaired without a diagnostic.** The repair is lossy, and unlike an unterminated
  quote it is not counted. Whether it should be is a judgement, not an oversight.
