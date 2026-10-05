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
| `write.too-many-rows`, `write.too-many-merges`, `write.too-many-styles` | `TabularWriteException`: the data reached a limit of the format while writing — more rows than the sheet holds (1,048,576 in xlsx and ods; 2,147,483,647, the largest row number the reader gives, in csv and a zip of csv; located at the last row the sheet holds, column 0), more merged ranges than an xlsx sheet holds (65,536; located where the range would begin), or a style rule's 4,097th distinct style in a file (located at its cell). Never `TabularLimitException`, which is a reader's bound. The file written so far is incomplete and must be discarded. |
| `write.precision-loss`, `write.not-finite`, `write.date-out-of-range`, `write.text-too-long`, `write.too-many-lines`, `write.ambiguous-line-breaks`, `write.invalid-character` | `TabularWriteException`: a value the chosen format cannot hold exactly, found while writing; `SheetName`, `RowNumber`, `ColumnIndex` and `Header` say where. The file written so far is incomplete and must be discarded. In xlsx: a long a double cannot hold exactly (beyond ±2^53 only some are), or a decimal with more than 15 significant digits (precision-loss); a date before 1900-01-01 (date-out-of-range); text over 32,767 characters (text-too-long). In ods: the same precision rule as xlsx (precision-loss); text over 16,777,216 characters, the reader's limit, or text whose escaped form would exceed the reader's per-value token limit (text-too-long); no date limit. |

This table is checked against the library's sources by `ErrorCodeCatalogTests`, in both directions.
It went out of step twice in the branch that added it — a code emitted, asserted, given a requirement
and described in this file's own prose, and left out of the table a frontend reads. Now it cannot.

Faults that invalidate a whole run are exceptions, not row errors — the two demand opposite
responses. All of them derive from `TabularException`, which carries a `Code` from the table, and
split by what a host does about them:

| Exception | Means | Typical HTTP answer |
|---|---|---|
| `TabularFormatException` | Not a file this library reads, or not a readable one | 400 / 415 |
| `TabularLimitException` | Readable, but beyond a configured bound — how most hostile files end. Thrown only while reading | 413 |
| `TabularStructureException` | Not the file the plan was built for (sheet, header row or header changed) | 409 / 422 |
| `MappingPlanException` | The plan does not fit its schema, before any file is read | 400 |
| `TabularWriteException` | A value the chosen format cannot hold exactly, or a limit of the format the data reached, found while writing | 500 for a server's own export; discard the partial file |

Mistakes in the calling code — a null argument, an option out of range, a field the schema does not
declare — are `ArgumentException` and `InvalidOperationException`, and a format code or colour that
`NumberFormat.Parse`, `DateFormat.Parse` or `CellColor.Parse` cannot read is a `FormatException`.
Nothing else escapes: malformed
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
