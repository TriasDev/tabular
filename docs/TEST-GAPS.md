# Test gaps

Places the suite covers less well than the code deserves, for contributors picking up test work. Not
user-facing: nothing here is a behaviour, only a missing check on one.

### The suite assumes the German and English cultures exist
Under `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` the library degrades as the documentation describes — named
cultures are left out of analysis, a plan naming one is refused as `mapping.unknown-culture` — but
about seventy tests exercise de-DE and en-US readings directly and fail there by design. The core
flow under invariant mode is pinned instead by `tests/TriasDev.Tabular.InvariantGlobalizationTests`,
whose whole test host runs with `InvariantGlobalization` — so the main suite need not.

### One error code is asserted nowhere
`value.min-length`. The codes are a frontend's translation contract; swapping two of them leaves the
suite green.

The count used to be wrong in both halves at once — it said four of nineteen when it was two of
twenty — so the catalog itself is now pinned by `ErrorCodeCatalogTests` rather than described here.

### Nothing pins the library's culture-cleanliness
The library never touches `CurrentCulture` and the suite passes under German and Turkish locales, but
nothing would *fail* if a `_culture` argument were dropped. On a US runner that regression is
invisible.

### Assorted weak tests
Several tests assert less than their names claim — the delimiter-consistency tests never reach the
consistency rule, `TabularAnalyzerTests`' cancellation test cancels before the pass begins rather than
during it, and the "no values for a failing row" invariant is never actually read.

Two that were on this list have been fixed rather than recorded: the quote-storm guard now compares
allocation across two input sizes instead of asserting a wall-clock ceiling no defect could exceed,
and `ExtractionSessionTests` cancels after a row rather than before the first.
