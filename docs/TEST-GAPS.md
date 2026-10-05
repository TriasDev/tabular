# Test gaps

Places the suite covers less well than the code deserves, for contributors picking up test work. Not
user-facing: nothing here is a behaviour, only a missing check on one.

### The suite assumes the German and English cultures exist
Under `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` the library degrades as the documentation describes — named
cultures are left out of analysis, a plan naming one is refused as `mapping.unknown-culture` — but
about seventy tests exercise de-DE and en-US readings directly and fail there by design. The core
flow under invariant mode is pinned instead by `tests/TriasDev.Tabular.InvariantGlobalizationTests`,
whose whole test host runs with `InvariantGlobalization` — so the main suite need not.

### Culture-cleanliness is pinned at build time and in CI, not in-process
The library never touches `CurrentCulture`. A call that would fall back to it — a parse, format,
comparison or case change without a culture or `StringComparison` — fails the build: CA1304, CA1305,
CA1310 and CA1311 are errors for `src/` (`.editorconfig`). The `culture-tests` CI jobs run the whole
suite under de-DE and tr-TR, which catches a dropped culture argument wherever those cultures
disagree with the one the code names. No test sets `CurrentCulture` in-process, so a run on one
machine in one locale proves nothing about the others; the analyzers are what holds there.

### Remaining weak spots
Found by reading the code against the suite; each is a one-test fix.

- `GzipBoundsTests.ReadsAFileThatExpandsToExactlyTheBound` reads without asserting what it read.
- `GzipCursor`: refusing a non-seekable stream, and reading after a move that failed part-way.
- `ArchiveCursor`: an entry that is unreadable while it is classified (a corrupt deflate stream)
  reaching `SkippedEntries` rather than aborting the archive.
- A self-closing `<table:table/>` between two ods sheets: the name pass and the reading must both
  skip it.
- `TabularWriter`: a header only the format writer refuses (a csv header with a lone CR) faulting the
  writer; `CsvWriterOptions` refusing a culture whose output the import would not read back;
  `SheetNames` refusing a lone surrogate or U+FFFF.
- `ZipEndRecord`: a 16-bit entry count that contradicts a larger zip64 count.
- `EquatableDictionary` equality and hash, as `FileProfile` equality uses them.
- The workbook write fuzz draws no merges.
- `TabularAnalyzer` checks its token between rows and hands it into `ReadRow`; for csv each covers
  for the other, so a test can only show that removing both is caught, not either alone.
