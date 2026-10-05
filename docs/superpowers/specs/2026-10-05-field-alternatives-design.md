# Field alternatives: row-level completeness of field groups — design

Issue: #110. The decisions were reached in a design interview and are recorded in the issue; this
file fixes the names and shapes the implementation uses.

## The problem

A consumer locates a row either by coordinates or, where it has none, by an address. It needs to
show, after mapping and before import, how complete the data is *per row across several columns* —
which a `FileProfile` cannot answer, because `ColumnFacts` are per column. The library stays
domain-free: coordinates and addresses are the consumer's words.

## Model

- **`FieldAlternatives`** — a named set of groups in priority order, declared on
  `ImportSchema.Alternatives`. A row needs one usable group; the first usable group *wins*; a later
  group is *needed* only where every earlier one fell short.
- **`AlternativeGroup`** — an ordered list of **levels** plus `RequiredLevels`. A row *reaches*
  level *k* when levels 1..*k* are each satisfied; a level is satisfied when *any* of its fields
  carries a valid value. One field may appear in several levels of one group (a combined
  "street and house no." column). The group is *usable* in a row when it reaches at least
  `RequiredLevels`.
  - `AlternativeGroup.AllOf("coordinates", lat, lon)` — one level per field, `RequiredLevels` =
    field count: usable only with every member.
  - `AlternativeGroup.Ladder("address", requiredLevels: 1, country, locality, street, house)` —
    usable from the first rung; deeper rungs raise the quality.
- **`AlternativeLevel`** — a name and the fields any of which satisfies it.
- **`FieldAlternatives.UnresolvedRowFails`** — false by default: a row with no usable group is
  imported and reported. True: it is a `RowError` with `group.unresolved`.

Schema rules (programmer errors, `ArgumentException`): every field named is declared by the
schema; a field belongs to at most one group of at most one set; a field in a set is not
`Required`; set names unique; group names unique within a set.

## Validation

- Fields of the **first** group are validated as every field is: a value that does not read or
  breaks a constraint is a `RowError`.
- Fields of a **later** group are validated strictly only in rows where that group is needed. In a
  row where an earlier group won, a value that would fail is kept as is (a text value is still
  handed out; a value that does not read as its type is absent), and counted as
  `IgnoredInvalidValues`.
- An invalid value never counts towards a level.

## Per row: `AlternativeResolution`

`ExtractionRun.CurrentResolutions` (one per set, schema order) and
`ImportRow.Resolution(FieldAlternatives)`: `Group` (name, null when unresolved), `GroupIndex`
(−1), `Level` (levels reached by the winner), `LevelName`. Informational: it never changes a value.

## Report: `AlternativesReport`

Kept by every `ExtractionRun` (`ExtractionRun.Alternatives`, `ImportRun<T>.Alternatives`), counted
over rows that produce values only:

- set: `RowsJudged`, `Unresolved`, `UnresolvedRows` (+ `UnresolvedRowsComplete`);
- per group (`AlternativeGroupReport`): `RowsNeeding`, `Won`, `MaxReachableLevel` (deepest level
  the mapping can reach), `RowsAtLevel` (index = levels reached, 0..level count), `Incomplete`
  (needed and below `MaxReachableLevel`), `Empty` (needed, no member present), `IncompleteRows`
  (+ `IncompleteRowsComplete`), `IgnoredInvalidValues`.

Row lists hold at most `ExtractionOptions.MaxReportedRows` (default 50, at most 10,000).

## Dry run: `TabularExtractor.Review`

`Review(cursor, plan, schema, ReviewOptions?, IProgress<AnalysisProgress>?, CancellationToken)` →
`ImportReview { Summary, Errors, ErrorsComplete, Alternatives }`. Reads the whole file with
`ValidateOnly`, never stops at an error limit (`ImportPolicy` included), keeps the first
`ReviewOptions.MaxErrors` errors (default 50, at most 10,000), reports progress as analysis does.

## Precheck

- `group.level-unmapped` (Warning) per group whose mapping stops short of its last level;
  arguments `alternatives`, `group`, `level` (the first unmapped level), `reachableLevel`.
- `group.unresolved` when no group of a set can become usable under the mapping: `Blocking` if
  `UnresolvedRowFails`, else `Warning`; `AffectedRows` = the sheet's row count.
- Findings on a field of a later group are never `Blocking` (downgraded to `Warning`): only the
  dry run knows the rows that need it.

## Helper

`ColumnFacts.RowsWithValueIn(FieldConstraint.AllowedValues)` → `int?`: rows whose value is in the
set, null when the distinct values are not complete.

## Out of scope

`0,0` coordinates; required members inside a rung beyond `RequiredLevels`.
