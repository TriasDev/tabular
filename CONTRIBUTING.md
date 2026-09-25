# Contributing to TriasDev.Tabular

Thanks for helping. This page is what you need to build, test and change the library without
breaking the promises it makes. Questions and ideas are welcome as issues; security reports go
through [SECURITY.md](SECURITY.md), not public issues.

## Build and test

```bash
dotnet build TriasDev.Tabular.slnx
dotnet test TriasDev.Tabular.slnx
dotnet test tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~CsvCursorTests"   # one class
dotnet test tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~CsvCursorTests.SomeTest"
dotnet pack TriasDev.Tabular.slnx -c Release   # produces only TriasDev.Tabular.nupkg
```

The benchmark project (`benchmarks/TriasDev.Tabular.Benchmarks`) reads large fixtures that are not in
the repository; set `TABULAR_FIXTURES` to their folder and `TABULAR_FILES` to a comma-separated list
of file names in it — without both it measures nothing. It runs
every measurement in a child process on purpose, so peak working set is not shared between candidates.

No large files at hand? `benchmarks/TriasDev.Tabular.FixtureGenerator <folder> [scale]` writes
synthetic ones of the published shape — see "Reproducing" in `docs/benchmarks.md`.

`benchmarks/TriasDev.Tabular.Comparison` reads the same fixtures with Sylvan, Sep, CsvHelper,
ExcelDataReader, MiniExcel, the Open XML SDK, ClosedXML, NPOI and EPPlus (same variables, plus
`TABULAR_RUNS`, `TABULAR_READERS`); it is the only project allowed third-party parsing packages, and
only versions free for commercial use are committed (EPPlus 4.5.3.3, NPOI 2.7.6).

There is no separate lint step: Roslynator, Sonar and the .NET analyzers run as part of the build.
`CS8509` (non-exhaustive switch) is an error everywhere, `CS8600` additionally in the library.

## Build layout

`Directory.Build.props` is layered, and nested files import the root one explicitly via
`GetPathOfFileAbove` (MSBuild otherwise stops at the nearest file):

- root — target framework, language settings, analyzers, `IsPackable=false` by default
- `src/` — `IsPackable=true`, XML docs, version, package metadata
- `tests/` — xunit.v3 and test SDK references

A project dropped into `src/` or `tests/` needs no settings of its own.

## Invariants that are easy to break

- **No package references in the library.** `TabularIndependenceTests` checks the *resolved* graph
  (`obj/project.assets.json`) against a list of parsing libraries (Sylvan, ExcelDataReader, CsvHelper,
  OpenXml, …). Analyzers are fine; anything else is a decision for an ADR, not a convenience.
- **Error codes, never messages.** Row errors carry codes like `value.required`. The "Error codes"
  table in `docs/guide.md` is checked against the `ErrorCodes` constants by `ErrorCodeCatalogTests` in
  both directions — adding, renaming or removing a code means updating both.
- **Rows are views.** `CurrentRow`, `CurrentValues` and `ImportRow` (a `ref struct`) point at reused
  buffers; do not introduce per-row allocations on the read path.
- **The precheck must judge a value exactly as extraction would read it** — same reader, same cell
  kind, same culture, trimmed. Past bugs came from the two halves disagreeing.
- **Every collection that grows with the file has a ceiling** (see the "Bounds" table in `docs/guide.md`).
  Hostile-input tests pin these; a new structure read from a file needs its own bound.
- **Cancellation is passed into `ReadRow`**, not only checked between rows, because single reads can
  be expensive on hostile files. Every reading operation takes a token as its last parameter; the
  token given to `Start`/`Import` still applies to the whole run.
- **A stream handed over is closed on every path**, failures included, unless the caller asked for
  `leaveOpen`. New entry points follow the same rule.
- **Options are checked where they are handed over** (`OptionChecks`), never discovered mid-read.
- Every library exception derives from `TabularException` and carries a code: `TabularFormatException`
  (unreadable/unsupported), `TabularLimitException` (a bound), `TabularStructureException` (whole-run),
  `MappingPlanException`. `ArgumentException` is for programmer errors only. Per-row problems are
  `RowError`s, and a row is either values or errors, never both.
- **The invariant culture is `""`** everywhere — profile, hypotheses and plan.

## Working on the read path

Speed and flat memory are the point of this library, so a change to a cursor, the analyzer or the
import loop is measured, not assumed:

- iterate on small files, then check the largest fixture you have before you call it done;
- compare against the commit before your change, alternating runs, and report time and allocations;
- a change that is slower for the sake of tidiness is not an improvement here.

## Tests

`tests/TriasDev.Tabular.InvariantGlobalizationTests` runs its whole host with `InvariantGlobalization`
(as a slim container would), so the core flow is pinned where no named culture exists; the main suite
depends on de-DE and en-US and cannot run that way.

Fixtures are built from raw bytes and raw OOXML (`Fixtures/XlsxPackage.cs`, `*GoldenFixtures.cs`)
rather than through a writer, because the cases worth pinning are ones well-behaved writers never
emit. Tests go through the public API; `InternalsVisibleTo` exists for the few units that are internal
by design (`ColumnProfiler`, `HypothesisBuilder`, `Windows1252Encoding`, a diagnostics counter) —
not a licence to test implementation details elsewhere.

`GoldenFixtureTests` holds both cursors to the golden fixtures. The parser candidates from the
ADR-0001 spike were removed once the decision was recorded; they remain in git history.

Write the failing test first: a fix comes with the test that failed before it, and a new behaviour
with the test that pins it.

## Commits and pull requests

- Commit titles follow [Conventional Commits](https://www.conventionalcommits.org/) (`fix: …`,
  `feat: …`, `docs: …`); release notes are generated from them.
- One concern per pull request. Describe what changes and why, and call out anything that breaks the
  public API — until 1.0 that is allowed in a minor release, but it is listed in the changelog.
- Comments and XML docs in this repository explain *why*, often at length; match that when you change
  behaviour.
- No customer data in fixtures, issues or commits — build the smallest synthetic file that shows the
  case, from raw bytes if needed (`Fixtures/XlsxPackage.cs`).

## Docs to consult

- `README.md` — the short front page: what the library does, a quick start, headline numbers
- `docs/guide.md` — the behavioural contract, error-code table, bounds and the library's own performance numbers
- `docs/benchmarks.md` — the comparison with other csv/xlsx libraries (`benchmarks/TriasDev.Tabular.Comparison`)
- `docs/KNOWN-ISSUES.md` — review findings deliberately left unfixed, with when each would matter
- `docs/IDEAS.md` — extensions the design allows that nobody has asked for yet
- `docs/adr/0001-…` — why the parsing is our own; its measurements are frozen, the guide's and benchmarks' are live

Code comments and XML docs in this repository explain *why*, often at length; match that when
changing behaviour.
