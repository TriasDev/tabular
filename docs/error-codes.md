# Error codes

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
| `value.pattern` | Did not match the pattern (as a whole: patterns are anchored at both ends) |
| `group.required` | A row carries none of a group's variants |
| `mapping.unknown-field`, `mapping.duplicate-binding`, `mapping.required-field-unmapped`, `mapping.required-group-unmapped` | A plan that does not fit its schema |
| `mapping.invalid-column`, `mapping.invalid-header-row`, `mapping.invalid-sheet`, `mapping.unknown-culture` | A plan that is malformed |
| `mapping.constraint-type-mismatch` | The schema puts a range (`MinValue`/`MaxValue`) on a field that is not a number |
| `mapping.stale-profile` | The profile was measured against a different header row |
| `mapping.header-changed` | The column's header is not the one the mapping recorded |
| `mapping.invalid-plan` | `MappingPlanException`: the plan does not fit its schema; its `Faults` carry the codes above |
| `structure.sheet-missing`, `structure.sheet-changed`, `structure.header-row-missing`, `structure.header-changed` | `TabularStructureException`: the file is not the one the plan was built for |
| `format.unsupported`, `format.corrupt`, `format.truncated` | `TabularFormatException`: not a format this library reads (.xls, .xlsb, .fods, another OpenDocument type, binary, an archive with nothing readable), or damaged, or cut off |
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

## Precheck arguments

A `PrecheckFinding` states data, not a sentence, so a UI fills its own template for the code in its
own language. Besides its `Code`, `Severity`, `FieldName` and `ColumnIndex` it carries:

- `AffectedRows` with `AffectedRowsBound` — `Exact`, `AtLeast`, `AtMost` or `Unknown`, because a count
  of empty cells can be off in a known direction (spellings of nothing counted as values; rows the
  import will skip);
- `Examples` — up to five of the values that fail, in ordinal order;
- `Arguments` — named values, numbers written invariantly. The names are constants in
  `PrecheckArguments`, and where one code has several causes, `reason` names which one, from
  `PrecheckReasons`.

| Code | Arguments | Reasons |
|---|---|---|
| `mapping.invalid-sheet`, `structure.sheet-changed` | `sheetIndex` | |
| `mapping.unknown-culture` | `culture` | |
| `mapping.stale-profile` | `profileHeaderRowIndex`, `planHeaderRowIndex` | |
| `mapping.invalid-column` | none: the column is `ColumnIndex` | |
| `mapping.header-changed` | `expectedHeader`, `actualHeader` | |
| `group.required` | `boundColumnCount` | |
| `value.required` | `reason` | `every-value-is-nothing`, `empty-cells` |
| `value.not-unique` | `reason`; `distinctCount` for `too-many-distinct` | `spelled-as-nothing`, `repeats`, `empty-cells`, `no-values`, `too-many-distinct` |
| a rule's code (`value.max-length`, `value.pattern`, …) | `reason`; `failingCount`, `judgedCount` (distinct values) and `Examples` when judged; `distinctCount` when not | `values-fail`, `no-row-can-satisfy`, `too-many-distinct` |
| `value.type-mismatch` | `reason`, `culture`, `type`; `failingCount`, `judgedCount` (values) when judged | `values-fail`, `no-row-can-satisfy`, `not-profiled` |

`PrecheckArgumentsTests` keeps this table and the constants in step, in both directions.
