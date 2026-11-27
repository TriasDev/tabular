# Csv strict quotes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `CsvCursorOptions.QuoteMode = CsvQuoteMode.Strict` reads quotes as RFC 4180 does and refuses quoting it would otherwise repair (#198).

**Architecture:** One enum and one option; `CsvCursor.ReadRowCore` reads a `strict` local once per row and skips every replay path under it, refusing instead where the repair mode would repair. Default mode untouched and measured.

**Tech Stack:** C# / net10.0 (tests net10.0 + net11.0), xUnit v3 on MTP.

**Spec:** `docs/superpowers/specs/2026-11-27-csv-strict-quotes-design.md`

## Global Constraints

- `CsvQuoteMode { Repair = 0, Strict = 1 }`, public, namespace `TriasDev.Tabular`; default `Repair`.
- Refusals: `TabularFormatException(TabularFormatException.Corrupt, …)`, message names the row (`CurrentRowNumber + 1`).
- Strict whitespace = `' '`, and `'\t'` unless tab is the delimiter.
- Default (`Repair`) behaviour unchanged; read speed of the default path not worse than `main` (min-of-N).
- No product names in code, docs or commits.

## Review Focus

- The issue's own Excel export (five columns, comma-laden note across a line) → two records, clean. (Task 2)
- `""` escaped quote directly before the closing quote and before EOF: `"a"""` → `a"`. (Task 2)
- Closing quote, spaces, then `\r\n` / lone `\r` / EOF → closes. (Task 2)
- Tab delimiter: `a\t "b"` — the tab is the delimiter, the space is whitespace → `b`. (Task 2)
- A custom `Quote = '\''` honours strict rules the same way. (Task 2)

---

### Task 1: The option

**Files:**
- Create: `src/TriasDev.Tabular/Csv/CsvQuoteMode.cs`
- Modify: `src/TriasDev.Tabular/Csv/CsvCursorOptions.cs` (property + `Checked()`), `src/TriasDev.Tabular/PublicAPI.Unshipped.txt`
- Test: `tests/TriasDev.Tabular.Tests/OptionDefaultsTests.cs`, `tests/TriasDev.Tabular.Tests/OptionsValidationTests.cs`

**Interfaces:**
- Produces: `public enum CsvQuoteMode { Repair = 0, Strict = 1 }`; `public CsvQuoteMode CsvCursorOptions.QuoteMode { get; init; }` (default `Repair`).

- [ ] Step 1: tests — `Assert.Equal(CsvQuoteMode.Repair, csv.QuoteMode)` in the defaults test; `new CsvCursorOptions { QuoteMode = (CsvQuoteMode)7 }` refused with `ArgumentOutOfRangeException` when a cursor opens, in the validation test's style.
- [ ] Step 2: run, expect compile failure.
- [ ] Step 3: implement; `Checked()` uses `Enum.IsDefined` as `OdsWriterOptions` does; XML docs say what each mode repairs or refuses.
- [ ] Step 4: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~Option"` passes; build has no RS0016.
- [ ] Step 5: commit `feat: add CsvCursorOptions.QuoteMode`.

### Task 2: Strict reading in the cursor

**Files:**
- Modify: `src/TriasDev.Tabular/Csv/CsvCursor.cs` (`ReadRowCore`, the quoted-run fast path)
- Test: create `tests/TriasDev.Tabular.Tests/Csv/StrictQuoteTests.cs`

**Interfaces:**
- Consumes: `CsvCursorOptions.QuoteMode`, `CsvQuoteMode.Strict`.

- [ ] Step 1: tests (helper reads with `new CsvCursorOptions { QuoteMode = CsvQuoteMode.Strict }`, plus `Delimiter`/`Quote` where named):
  - `ReadsAnExcelCellWithALineBreakAndCommasAsOneValue` — the issue's sample: 3 rows incl. header; row 1 = `1 | Payroll | Monthly salary run.\nUses names, addresses, bank accounts, tax ids, and social security numbers. | HR | People`; `IsClean`.
  - `KeepsAQuotedFieldOfMoreThanTheLineBoundAsOneValue` — 150 line breaks in one field, `MaxQuotedFieldLines` 100 → one value with 150 `\n`; clean.
  - `RefusesAQuoteTheInputEndsInside` — `a,b\n1,"open\nmore` → `TabularFormatException`, `Code == Corrupt`, message contains `row 2`; a further `ReadRow` throws `InvalidOperationException`.
  - `RefusesTextAfterAClosingQuote` — `"C" Road,x` and multi-line `"a\n135"th",x` → `Corrupt`.
  - `IgnoresWhitespaceAroundAQuotedField` — `ID, Name, Note\r\n1, "Payroll", "monthly, HR"  \r\n` → `1 | Payroll | monthly, HR`.
  - `KeepsAQuoteInsideAnUnquotedField` — `21"st AVE,x` → `21"st AVE`.
  - Review Focus cases: `"a"""` at EOF → `a"`; `"a"  \r` lone CR and `"a"  ` at EOF; tab delimiter `a\t "b"` → `b`; `Quote = '\''`: `'x, y'  ,z` → `x, y | z` and `'C' Road` refused.
  - `RepairModeIsUnchanged` — the issue's sample without options still splits (`RecoveredStrayQuotes > 0`), pinning that the default did not move.
- [ ] Step 2: run, expect failures.
- [ ] Step 3: implement. `bool strict = _options.QuoteMode == CsvQuoteMode.Strict` at the top of `ReadRowCore`. Under `strict`: no `_quotedRaw` appends (fast path and per char), no line counting/bound, no delimiter-count replay, no `135"th` rule; EOF in quotes throws; a quote with `!atFieldStart` opens a quoted field when the field so far (pending run or `_field`) is only strict whitespace, discarding it; on a closing quote, consume strict whitespace via `PeekChar`/`ReadChar`, then anything but delimiter, `\r`, `\n`, EOF throws. Refusals through one `[NoInlining]` helper returning the exception.
- [ ] Step 4: whole test project passes on both targets.
- [ ] Step 5: fuzz round trip: in the csv writer fuzz (`Fuzz/`), also read every written file with `Strict` and assert the same rows — the writer's output is RFC 4180. Run passes.
- [ ] Step 6: measure the default path, `main` vs branch, alternating, min-of-N, on the 5M csv (memory: final perf check always includes it); report time and allocations.
- [ ] Step 7: commit `feat: read csv quotes strictly when asked (#198)`.

### Task 3: Docs

**Files:** `docs/formats.md` (repair section: a paragraph on strict mode), `docs/bounds.md` (quoted-field line: not applied in strict), `docs/error-codes.md` (`format.corrupt` covers strict csv quoting), `docs/KNOWN-ISSUES.md` (the stray-quote and `"C" Road` entries point at strict mode), remarks on `CsvCursorOptions.MaxQuotedFieldLines` and `CursorDiagnostics`.

- [ ] Step 1: edit; `dotnet build` (doc comments) clean.
- [ ] Step 2: commit `docs: describe strict csv quoting`.
