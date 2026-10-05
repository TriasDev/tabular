# Contributing to TriasDev.Tabular

Thanks for helping. This page is what you need to build, test and change the library without
breaking the promises it makes. Questions and ideas are welcome as issues; security reports go
through [SECURITY.md](SECURITY.md), not public issues.

## Build and test

```bash
dotnet build TriasDev.Tabular.slnx
dotnet test --solution TriasDev.Tabular.slnx
dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~CsvCursorTests"   # one class
dotnet test --project tests/TriasDev.Tabular.Tests --filter-method "*CsvCursorTests.SomeTest"
dotnet test --project tests/TriasDev.Tabular.Tests --framework net8.0                            # one target
dotnet pack TriasDev.Tabular.slnx -c Release   # produces only TriasDev.Tabular.nupkg
```

The benchmark project (`benchmarks/TriasDev.Tabular.Benchmarks`) reads large fixtures that are not in
the repository; set `TABULAR_FIXTURES` to their folder and `TABULAR_FILES` to a comma-separated list
of file names in it — without both it measures nothing. It runs
every measurement in a child process on purpose, so peak working set is not shared between candidates.

`benchmarks/TriasDev.Tabular.WriteComparison` writes the same generated data with TriasDev.Tabular,
CsvHelper, Sep, Sylvan.Data.Csv, LargeXlsx, SpreadCheetah and MiniExcel (`TABULAR_RUNS`,
`TABULAR_WRITERS`, `TABULAR_SCENARIOS`, `TABULAR_COMPRESSION`, `TABULAR_ROWS_SCALE`); it needs no
fixtures — see "Reproducing the writing comparison" in `docs/benchmarks.md`.

No large files at hand? `benchmarks/TriasDev.Tabular.FixtureGenerator <folder> [scale]` writes
synthetic ones of the published shape — see "Reproducing" in `docs/benchmarks.md`.

`benchmarks/TriasDev.Tabular.Comparison` reads the same fixtures with Sylvan, Sep, CsvHelper,
ExcelDataReader, MiniExcel, the Open XML SDK, ClosedXML, NPOI and EPPlus (same variables, plus
`TABULAR_RUNS`, `TABULAR_READERS`). The two comparison projects are the only ones allowed third-party
csv and spreadsheet packages, and only versions free for commercial use are committed (EPPlus
4.5.3.3, NPOI 2.7.6). The test project's one exception is the Open XML SDK, used only to validate
the xlsx the writer produces against the OOXML schema.

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
  table in `docs/error-codes.md` is checked against the `ErrorCodes` constants by `ErrorCodeCatalogTests` in
  both directions — adding, renaming or removing a code means updating both.
- **Rows are views.** `CurrentRow`, `CurrentValues` and `ImportRow` (a `ref struct`) point at reused
  buffers; do not introduce per-row allocations on the read path.
- **The precheck must judge a value exactly as extraction would read it** — same reader, same cell
  kind, same culture, trimmed. Past bugs came from the two halves disagreeing.
- **Every collection that grows with the file has a ceiling** (see the table in `docs/bounds.md`).
  Hostile-input tests pin these; a new structure read from a file needs its own bound.
- **Cancellation is passed into `ReadRow`**, not only checked between rows, because single reads can
  be expensive on hostile files. Every reading operation takes a token as its last parameter; the
  token given to `Extract`/`Import` still applies to the whole run.
- **A stream handed over is closed on every path**, failures included, unless the caller asked for
  `leaveOpen`. New entry points follow the same rule.
- **Options are checked where they are handed over** (`OptionChecks`), never discovered mid-read.
- Every library exception derives from `TabularException` and carries a code: `TabularFormatException`
  (unreadable/unsupported), `TabularLimitException` (a bound), `TabularStructureException` (whole-run),
  `MappingPlanException`, and `TabularWriteException` (a value a format cannot hold, with its sheet, row
  and column). `ArgumentException` is for programmer errors only. Per-row problems are
  `RowError`s, and a row is either values or errors, never both.
- **The invariant culture is `""`** everywhere — profile, hypotheses and plan.
- **A writer touches its target only asynchronously.** The format writers write synchronously into the
  `SpillBuffer`; only `FlushAsync`, `CompleteAsync` and the async batch and export paths drain it into
  the stream. A synchronous `Write` on the target breaks an ASP.NET Core response body.
- **What is written reads back.** Every value a writer accepts must come back through the library's own
  import as the same value (within the documented exceptions in `docs/KNOWN-ISSUES.md`); anything else
  is refused with a `TabularWriteException` carrying a `write.*` code and its location, never rounded or
  cut. A new value type or format rule comes with its round-trip test.
- **A written file's limits are the reader's.** Text length, line breaks in a csv field, columns per
  sheet: what the writer allows is what the reader reads, so the limits move together.

## The public API is recorded

`src/TriasDev.Tabular/PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` list every public member.
Adding, changing or removing one without updating them fails the build (RS0016/RS0017); the analyzer's
code fix writes the line for you. New members go into `Unshipped`; a release moves them to `Shipped`.
A change to a line in `Shipped` is a breaking change — call it out in the pull request.

## Working on the read and write paths

Speed and flat memory are the point of this library, so a change to a cursor, the analyzer, the
import loop or a writer is measured, not assumed:

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

`Fixtures/Producers` is the exception: one small table as ClosedXML, the Open XML SDK, EPPlus, NPOI,
MiniExcel, Sylvan and LibreOffice write it, committed as they wrote it and embedded in the test
assembly, so the readers answer to real producers too. Its README says how to regenerate them.

`GoldenFixtureTests` holds both cursors to the golden fixtures. The parser candidates from the
ADR-0001 spike were removed once the decision was recorded; they remain in git history.

`Fuzz/` writes random tables and workbooks in each format — every quoting, spelling and repeat the
format allows — and reads them back exactly; it also breaks files at random and expects them read or
refused with a `TabularException`, never anything else and never a hang. The cases are seeded and
run a few hundred each by default. For a long local run set `TABULAR_FUZZ_CASES` (say `20000`); a
failure names its seed, which `TABULAR_FUZZ_SEED` replays alone, and `TABULAR_FUZZ_DUMP` names a
folder to keep that case's file in. A found failure becomes a named test beside the code it fixes.

Writing is tested by round trip: `tests/TriasDev.Tabular.Tests/Writing` writes each value type and
format and reads it back through the library's own import, fuzzes the csv writer, and checks that
LibreOffice opens what is written and shows the values (`Fixtures/LibreOffice.cs`, `soffice --headless`;
the tests skip where LibreOffice is not installed, unless `TABULAR_REQUIRE_SOFFICE` is set, as CI's
Linux job sets it).

Write the failing test first: a fix comes with the test that failed before it, and a new behaviour
with the test that pins it.

## Commits and pull requests

- **Every change reaches `main` through a pull request** — maintainers included. `main` is protected:
  no direct pushes, no force pushes, and a pull request merges only when the `CI Success` check is
  green (the build and tests on three systems and two runtimes, the culture jobs, the package and the
  documentation built strictly). No approval is required, so a maintainer can merge their own.
- Commit titles follow [Conventional Commits](https://www.conventionalcommits.org/) (`fix: …`,
  `feat: …`, `docs: …`); release notes are generated from them.
- One concern per pull request. Describe what changes and why, and call out anything that breaks the
  public API — until 1.0 that is allowed in a minor release, but it is listed in the changelog.
- Comments and XML docs in this repository explain *why*, often at length; match that when you change
  behaviour.
- No customer data in fixtures, issues or commits — build the smallest synthetic file that shows the
  case, from raw bytes if needed (`Fixtures/XlsxPackage.cs`).

## Releasing

Releases are cut by [release-please](https://github.com/googleapis/release-please) from Conventional
Commit titles:

1. Every push to `main` updates an open pull request, "chore(main): release x.y.z", with the next
   version (in `VersionPrefix`, `src/Directory.Build.props`) and the `CHANGELOG.md` entry.
2. Before merging it, move `PublicAPI.Unshipped.txt` into `PublicAPI.Shipped.txt` in that pull request
   — what ships is what the next version must stay compatible with. A `*REMOVED*X` line deletes `X`
   from Shipped rather than being moved; every other line is appended. Keep `main` quiet meanwhile:
   release-please rewrites its branch whenever the changelog changes. Bring `main` into that branch
   first (`gh pr update-branch`), so CI checks exactly what is released.
3. Merging it tags the release and creates the GitHub release. The `publish` job then waits for an
   approval on the `nuget` environment, and pushes the package and its symbols to nuget.org through
   Trusted Publishing — no API key is stored anywhere.
4. After every release, set `PackageValidationBaselineVersion` to the released version so package
   validation compares against it, and delete `src/TriasDev.Tabular/CompatibilitySuppressions.xml` if
   there is one: it records breaks against the previous baseline, which no longer applies. Until 1.0
   a release may break the API on purpose; such a release regenerates that file with
   `dotnet pack -p:ApiCompatGenerateSuppressionFile=true` and reviews every entry in it.

## Docs to consult

- `README.md` — the short front page: what the library does, reading and writing, a quick start, headline numbers.
  It is also the NuGet readme, so its links are absolute, and its snippets taken from `samples/` are copied
  verbatim — update them when a sample region changes
- `docs/*.md` — the documentation site (MkDocs, `mkdocs.yml`), published to https://triasdev.github.io/tabular/: the
  behavioural contract split by topic (`concepts`, `importing`, `exporting`, `formats`, `operations`), the error codes, the bounds
  and the library's own performance numbers. Build it with `pip install -r requirements.txt && mkdocs build --strict`;
  CI builds it the same way on every pull request. Code on the site is included from `samples/` with
  `--8<--` markers, so it is compiled by the build.
- `docs/benchmarks.md` — the comparison with other csv/xlsx libraries, reading (`benchmarks/TriasDev.Tabular.Comparison`)
  and writing (`benchmarks/TriasDev.Tabular.WriteComparison`)
- `docs/KNOWN-ISSUES.md` — known limitations, with when each would matter; add one when you choose not to fix something
- `docs/TEST-GAPS.md` — where the suite is thinner than the code deserves
- `docs/IDEAS.md` — extensions the design allows that nobody has asked for yet
- `docs/adr/0001-…` — why the parsing is our own; its measurements are frozen, the site's performance and benchmark pages are live
- `docs/adr/0002-…` — why the writing is our own: the zip writer, the spill buffer, the round-trip contract

Code comments and XML docs in this repository explain *why*, often at length; match that when
changing behaviour.
