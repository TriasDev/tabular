# Archive as workbook — part 1 (per-sheet facts) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move the facts that describe a sheet's source — format, source path, csv dialect, repairs — from the file onto the sheet, and let a plan check which sheet it was built for, so a later archive cursor needs no breaking change.

**Architecture:** `SheetInfo` (cursor side) and `SheetProfile` (analysis side) gain `Format` and `Source`; `SheetProfile` takes over `Dialect` and gets its own `Diagnostics`; `FileProfile` keeps the container's `Format` and a *snapshot* of the total diagnostics. `MappingPlan` gains optional `SheetName` / `SheetSource` checksums that extraction verifies (`structure.sheet-changed`) and the precheck treats as a stale profile.

**Tech Stack:** .NET 10, C#, xunit.v3. No package references in the library.

**Spec:** `docs/superpowers/specs/2026-09-25-archive-as-workbook-design.md` (part 1 only).

## Global Constraints

- No package references in `src/TriasDev.Tabular` (`TabularIndependenceTests`).
- src and tests build with `TreatWarningsAsErrors`; zero warnings.
- Error codes live in `ErrorCodes` and in the "Error codes" table of `docs/guide.md`; `ErrorCodeCatalogTests` checks both directions.
- Tests pass `TestContext.Current.CancellationToken` to every method that accepts a token (xUnit1051 is an error).
- The invariant culture is `""`; a stream handed over is closed on every path unless left open — untouched here, but do not regress.
- Comments and XML docs explain *why*, in the repository's voice.
- Nothing product- or customer-specific in code, docs or commit messages.
- Final check after the last task: the full suite, then analysis of the local 5M-row csv (`TABULAR_FIXTURES`) against `origin/main` — time and allocations within noise.

## Review Focus

1. **The profile's diagnostics alias the cursor's object.** Reading the cursor on after `Analyze` must not change a stored profile — covered in Task 2 by mutating the cursor's counter after analysis.
2. **A hand-written plan without `SheetName` / `SheetSource`.** Must keep working unchecked — covered in Task 3.
3. **`SheetSource` set but the file is a plain csv/xlsx (`Source == null`).** A mismatch, refused — covered in Task 3.
4. **Sheet names differing only in case.** Compared ordinally, like `SourceHeader`, so refused — covered in Task 3.
5. **A missing sheet and a changed sheet at once.** `structure.sheet-missing` wins, since there is nothing to compare — covered in Task 3.

---

### Task 1: `SheetInfo` carries the sheet's format and source

**Files:**
- Modify: `src/TriasDev.Tabular/Abstractions/SheetInfo.cs`
- Modify: `src/TriasDev.Tabular/Csv/CsvCursor.cs:99`
- Modify: `src/TriasDev.Tabular/Xlsx/XlsxCursor.cs:1188`
- Modify: `src/TriasDev.Tabular/Abstractions/TabularFormat.cs`
- Test: `tests/TriasDev.Tabular.Tests/SheetSourceTests.cs` (create)

**Interfaces:**
- Produces: `SheetInfo.Format` (`required TabularFormat`), `SheetInfo.Source` (`string?`, null outside an archive).

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text;

using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests;

/// <summary>
/// Every sheet says what kind of source it came from, and from where — the facts an archive holding
/// several files will need per sheet.
/// </summary>
public sealed class SheetSourceTests
{
    [Fact]
    public void ACsvSheetIsCsvAndHasNoSource()
    {
        using CsvCursor cursor = new(new MemoryStream(Encoding.UTF8.GetBytes("a;b\n1;2\n")), "t.csv");

        SheetInfo sheet = Assert.Single(cursor.Sheets);

        Assert.Equal((TabularFormat.Csv, (string?)null), (sheet.Format, sheet.Source));
    }

    [Fact]
    public void AWorkbookSheetIsXlsxAndHasNoSource()
    {
        byte[] workbook = new XlsxPackage()
            .WithSheet("S1", """<row r="1"><c r="A1" t="inlineStr"><is><t>a</t></is></c></row>""")
            .WithSheet("S2", """<row r="1"><c r="A1" t="inlineStr"><is><t>b</t></is></c></row>""")
            .Build();

        using XlsxCursor cursor = new(new MemoryStream(workbook), cancellationToken: TestContext.Current.CancellationToken);

        Assert.All(cursor.Sheets, s => Assert.Equal((TabularFormat.Xlsx, (string?)null), (s.Format, s.Source)));
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~SheetSourceTests"`
Expected: build error CS0117 — `SheetInfo` has no `Format` / `Source`.

- [ ] **Step 3: Implement**

`SheetInfo.cs` — add after `Name`:

```csharp
    /// <summary>The format of the file the sheet was read from.</summary>
    /// <remarks>
    /// The same as the cursor's for a plain file. Carried per sheet because an archive holds files of
    /// either kind, and a sheet read from a csv inside it has a dialect a workbook sheet beside it
    /// does not.
    /// </remarks>
    public required TabularFormat Format { get; init; }

    /// <summary>
    /// Where inside an archive the sheet's file lies, or null for a sheet of a plain file.
    /// </summary>
    /// <remarks>
    /// What a screen groups an archive's sheets by, and — with <see cref="Name"/> — what tells two
    /// workbooks' <c>Sheet1</c> apart.
    /// </remarks>
    public string? Source { get; init; }
```

`CsvCursor.cs:99`:

```csharp
        Sheets = [new SheetInfo { Index = 0, Name = sheetName, Format = TabularFormat.Csv }];
```

`XlsxCursor.cs:1188`:

```csharp
        sheets.Add(new SheetInfo { Index = sheets.Count, Name = Bounded(name, "sheet name"), Format = TabularFormat.Xlsx });
```

`TabularFormat.cs` — replace the summary with:

```csharp
/// <summary>The file shapes this library reads.</summary>
/// <remarks>
/// Open to new members — an archive of files, an OpenDocument spreadsheet — as the library learns to
/// read them. A switch over it needs a default arm, or the day a member is added breaks the build of
/// whoever wrote it.
/// </remarks>
```

Fix any other `new SheetInfo { … }` the compiler reports (tests included) by adding `Format = TabularFormat.Csv` or `Xlsx` as appropriate.

- [ ] **Step 4: Run the tests**

Run: `dotnet build TriasDev.Tabular.slnx && dotnet test TriasDev.Tabular.slnx`
Expected: 0 warnings, all tests pass.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Give every sheet its source format and, inside an archive, its source path"
```

---

### Task 2: The sheet's profile carries its format, source, dialect and repairs

**Files:**
- Modify: `src/TriasDev.Tabular/Abstractions/CursorDiagnostics.cs`
- Modify: `src/TriasDev.Tabular/Analysis/SheetProfile.cs`
- Modify: `src/TriasDev.Tabular/Analysis/FileProfile.cs`
- Modify: `src/TriasDev.Tabular/Analysis/TabularAnalyzer.cs:110-243`
- Modify: `src/TriasDev.Tabular/Abstractions/ITabularCursor.cs` (the `Dialect` doc)
- Modify: `samples/TriasDev.Tabular.Samples.Profile/Program.cs:25-26`
- Modify: `tests/TriasDev.Tabular.Tests/Analysis/TabularAnalyzerTests.cs:51-53,103`
- Modify: `tests/TriasDev.Tabular.Tests/Analysis/CursorDialectTests.cs:46`
- Test: `tests/TriasDev.Tabular.Tests/Analysis/SheetProfileSourceTests.cs` (create)

**Interfaces:**
- Consumes: `SheetInfo.Format`, `SheetInfo.Source` (Task 1).
- Produces: `SheetProfile.Format` (`required TabularFormat`), `SheetProfile.Source` (`string?`), `SheetProfile.Dialect` (`CsvDialect?`), `SheetProfile.Diagnostics` (`required CursorDiagnostics`); `FileProfile.Dialect` removed; `FileProfile.Diagnostics` is a snapshot. Internal: `CursorDiagnostics.Snapshot()`, `CursorDiagnostics.Since(CursorDiagnostics earlier)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text;

using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Analysis;

/// <summary>
/// What describes a sheet's source is reported on the sheet, and what the profile reports stays as
/// it was when the pass ended.
/// </summary>
public sealed class SheetProfileSourceTests
{
    private static CsvCursor Csv(string text) =>
        new(new MemoryStream(Encoding.UTF8.GetBytes(text), writable: false), "export.csv");

    [Fact]
    public void ACsvSheetCarriesItsFormatAndDialect()
    {
        using CsvCursor cursor = Csv("a;b\n1;2\n");

        SheetProfile sheet = Assert.Single(new TabularAnalyzer().Analyze(cursor, cancellationToken: TestContext.Current.CancellationToken).Sheets);

        Assert.Equal(TabularFormat.Csv, sheet.Format);
        Assert.Null(sheet.Source);
        Assert.Equal(';', sheet.Dialect?.Delimiter);
    }

    [Fact]
    public void AWorkbookSheetCarriesNoDialect()
    {
        byte[] workbook = new XlsxPackage()
            .WithSheet("S1", """<row r="1"><c r="A1" t="inlineStr"><is><t>a</t></is></c></row>""")
            .Build();
        using XlsxCursor cursor = new(new MemoryStream(workbook), cancellationToken: TestContext.Current.CancellationToken);

        SheetProfile sheet = Assert.Single(new TabularAnalyzer().Analyze(cursor, cancellationToken: TestContext.Current.CancellationToken).Sheets);

        Assert.Equal(TabularFormat.Xlsx, sheet.Format);
        Assert.Null(sheet.Dialect);
        Assert.True(sheet.Diagnostics.IsClean);
    }

    [Fact]
    public void ASheetReportsItsOwnRepairsAndTheFileTheirSum()
    {
        StringBuilder text = new("ID;Note\n1;\"unterminated\n");

        for (int i = 2; i <= 40; i++)
        {
            text.Append(i).Append(";row ").Append(i).Append('\n');
        }

        using CsvCursor cursor = Csv(text.ToString());
        FileProfile profile = new TabularAnalyzer().Analyze(cursor, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, Assert.Single(profile.Sheets).Diagnostics.RecoveredUnterminatedQuotes);
        Assert.Equal(1, profile.Diagnostics.RecoveredUnterminatedQuotes);
    }

    [Fact]
    public void AStoredProfileDoesNotChangeWhenTheCursorReadsOn()
    {
        // The profile used to hand out the cursor's own object, so anything the cursor counted after
        // the pass showed up in a profile a user was mapping against.
        using CsvCursor cursor = Csv("a;b\n1;2\n");
        FileProfile profile = new TabularAnalyzer().Analyze(cursor, cancellationToken: TestContext.Current.CancellationToken);

        cursor.Diagnostics.RecoveredUnterminatedQuotes++;

        Assert.NotSame(cursor.Diagnostics, profile.Diagnostics);
        Assert.True(profile.Diagnostics.IsClean);
        Assert.True(profile.Sheets[0].Diagnostics.IsClean);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~SheetProfileSourceTests"`
Expected: build error CS0117 — `SheetProfile` has no `Format` / `Source` / `Dialect` / `Diagnostics`.

- [ ] **Step 3: Implement**

`CursorDiagnostics.cs` — add inside the class:

```csharp
    /// <summary>A copy that stops counting, for a result that must stay as it was read.</summary>
    internal CursorDiagnostics Snapshot() => new() { RecoveredUnterminatedQuotes = RecoveredUnterminatedQuotes };

    /// <summary>What was repaired since <paramref name="earlier"/> was taken — one sheet's share.</summary>
    internal CursorDiagnostics Since(CursorDiagnostics earlier) => new()
    {
        RecoveredUnterminatedQuotes = RecoveredUnterminatedQuotes - earlier.RecoveredUnterminatedQuotes,
    };
```

`SheetProfile.cs` — add after `Name`:

```csharp
    /// <summary>The format of the file the sheet was read from.</summary>
    public required TabularFormat Format { get; init; }

    /// <summary>Where inside an archive the sheet's file lies, or null for a plain file.</summary>
    public string? Source { get; init; }

    /// <summary>
    /// How the sheet's file was punctuated and encoded, and how that was decided. Null for a
    /// workbook sheet.
    /// </summary>
    /// <remarks>
    /// On the sheet rather than the file because an archive holds several csv files, each detected
    /// on its own — one in UTF-8 with semicolons beside one in Windows-1252 with commas.
    /// </remarks>
    public CsvDialect? Dialect { get; init; }

    /// <summary>What the reader had to repair while reading this sheet.</summary>
    public required CursorDiagnostics Diagnostics { get; init; }
```

and add `using TriasDev.Tabular.Csv;` at the top of `SheetProfile.cs`.

`FileProfile.cs` — delete the `Dialect` property and its `using TriasDev.Tabular.Csv;`; change the two summaries:

```csharp
    /// <summary>What kind of file this was — the container, for an archive; each sheet says its own.</summary>
    public required TabularFormat Format { get; init; }
```

```csharp
    /// <summary>What the reader had to repair to get through the file, every sheet together.</summary>
    /// <remarks>Taken when the pass ended: reading the cursor on does not change it.</remarks>
    public required CursorDiagnostics Diagnostics { get; init; }
```

`TabularAnalyzer.cs` — in `Analyze`, replace the sheet loop and the returned profile:

```csharp
        foreach (SheetInfo sheet in cursor.Sheets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!cursor.MoveToSheet(sheet.Index))
            {
                continue;
            }

            // A sheet's repairs are what the counter gained while it was read; the cursor's counter
            // runs across the whole file.
            CursorDiagnostics before = cursor.Diagnostics.Snapshot();

            sheets.Add(AnalyzeSheet(cursor, sheet, budget, reporter, cancellationToken) with
            {
                Dialect = cursor.Dialect,
                Diagnostics = cursor.Diagnostics.Since(before),
            });
        }

        reporter.Complete();

        return new FileProfile
        {
            Format = cursor.Format,
            Sheets = sheets,
            Diagnostics = cursor.Diagnostics.Snapshot(),
        };
```

and in `AnalyzeSheet`'s `new SheetProfile { … }` add:

```csharp
            Format = sheet.Format,
            Source = sheet.Source,
            Diagnostics = new CursorDiagnostics(),
```

(`Analyze` replaces `Diagnostics` with the sheet's real share through `with`; `Dialect` is read after `MoveToSheet`, while the cursor is on that sheet.)

`ITabularCursor.cs` — change the `Dialect` summary to:

```csharp
    /// <summary>
    /// How the current sheet's file is encoded and punctuated, or null where it has no dialect — a
    /// workbook sheet.
    /// </summary>
```

`samples/…/Program.cs:25-26`:

```csharp
    delimiter = profile.Sheets[0].Dialect?.Delimiter.ToString(),
    encoding = profile.Sheets[0].Dialect?.Encoding.WebName,
```

`TabularAnalyzerTests.cs:51-53` — replace with:

```csharp
        CsvDialect? dialect = profile.Sheets[0].Dialect;
        Assert.NotNull(dialect);
        Assert.Equal(';', dialect.Delimiter);
        Assert.Equal(DialectSource.Detected, dialect.DelimiterSource);
```

`TabularAnalyzerTests.cs:103` — replace `Assert.Null(profile.Dialect);` with `Assert.All(profile.Sheets, s => Assert.Null(s.Dialect));`.

`CursorDialectTests.cs:46` — replace `profile.Dialect?.Delimiter` with `profile.Sheets[0].Dialect?.Delimiter`.

Fix any other `new SheetProfile { … }` (e.g. in precheck tests) by adding `Format = TabularFormat.Csv, Diagnostics = new CursorDiagnostics()`.

- [ ] **Step 4: Run the tests and the sample**

Run: `dotnet build TriasDev.Tabular.slnx && dotnet test TriasDev.Tabular.slnx`
Expected: 0 warnings, all tests pass.

Run: `dotnet run --project samples/TriasDev.Tabular.Samples.Profile -- samples/customers.csv`
Expected: output identical, character for character, to the JSON block in `README.md` (the delimiter/encoding line included).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Report each sheet's format, source, dialect and repairs; snapshot the profile's diagnostics"
```

---

### Task 3: A plan checks which sheet it was built for

**Files:**
- Modify: `src/TriasDev.Tabular/Mapping/MappingPlan.cs` (properties; `ByHeader` fills them)
- Modify: `src/TriasDev.Tabular/Abstractions/ErrorCodes.cs` (`Structure.SheetChanged`)
- Modify: `src/TriasDev.Tabular/Abstractions/TabularException.cs` (`TabularStructureException.SheetChanged`)
- Modify: `src/TriasDev.Tabular/Extraction/ExtractionSession.cs:209-219` (`Position`)
- Modify: `src/TriasDev.Tabular/Import/MappingPrecheck.cs:126` (`stale`)
- Modify: `docs/guide.md:394` (error-code table)
- Test: `tests/TriasDev.Tabular.Tests/Extraction/SheetIdentityTests.cs` (create)
- Test: `tests/TriasDev.Tabular.Tests/Mapping/MappingPlanByHeaderTests.cs` (extend)

**Interfaces:**
- Consumes: `SheetInfo.Name`, `SheetInfo.Source` (Task 1); `SheetProfile.Name`, `SheetProfile.Source` (Task 2).
- Produces: `MappingPlan.SheetName` (`string?`), `MappingPlan.SheetSource` (`string?`); `ErrorCodes.Structure.SheetChanged = "structure.sheet-changed"`; `TabularStructureException.SheetChanged`.

- [ ] **Step 1: Write the failing tests**

`SheetIdentityTests.cs`:

```csharp
using System.Text;

using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Extraction;

/// <summary>
/// A plan that recorded which sheet it was built for refuses a file where another sheet stands at
/// that index — the sheet-level twin of a changed header.
/// </summary>
public sealed class SheetIdentityTests
{
    private static readonly TargetSchema Schema = new() { Fields = [ImportField.Text("name")] };

    private static MappingPlan Plan(string? sheetName = null, string? sheetSource = null, int sheetIndex = 0) => new()
    {
        SheetIndex = sheetIndex,
        SheetName = sheetName,
        SheetSource = sheetSource,
        Bindings = [new ColumnBinding { SourceColumnIndex = 0, SourceHeader = "name", TargetFieldName = "name" }],
    };

    private static XlsxCursor Workbook() =>
        new(new MemoryStream(new XlsxPackage()
            .WithSheet("Orders", """<row r="1"><c r="A1" t="inlineStr"><is><t>name</t></is></c></row>""")
            .WithSheet("Returns", """<row r="1"><c r="A1" t="inlineStr"><is><t>name</t></is></c></row>""")
            .Build()), cancellationToken: TestContext.Current.CancellationToken);

    [Fact]
    public void AcceptsTheSheetThePlanRecorded()
    {
        using XlsxCursor cursor = Workbook();

        TabularExtractor.Start(cursor, Plan("Returns", sheetIndex: 1), Schema, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public void RefusesAnotherSheetStandingAtTheIndex()
    {
        using XlsxCursor cursor = Workbook();

        TabularStructureException error = Assert.Throws<TabularStructureException>(
            () => TabularExtractor.Start(cursor, Plan("Returns", sheetIndex: 0), Schema, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(TabularStructureException.SheetChanged, error.Code);
        Assert.Equal(0, error.SheetIndex);
    }

    [Fact]
    public void ComparesSheetNamesOrdinally()
    {
        using XlsxCursor cursor = Workbook();

        TabularStructureException error = Assert.Throws<TabularStructureException>(
            () => TabularExtractor.Start(cursor, Plan("orders"), Schema, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(TabularStructureException.SheetChanged, error.Code);
    }

    [Fact]
    public void RefusesASourceThePlainFileDoesNotHave()
    {
        using CsvCursor cursor = new(new MemoryStream(Encoding.UTF8.GetBytes("name\na\n")), "t.csv");

        TabularStructureException error = Assert.Throws<TabularStructureException>(
            () => TabularExtractor.Start(cursor, Plan(sheetSource: "export/t.csv"), Schema, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(TabularStructureException.SheetChanged, error.Code);
    }

    [Fact]
    public void DoesNotCheckAPlanThatRecordedNoSheet()
    {
        using CsvCursor cursor = new(new MemoryStream(Encoding.UTF8.GetBytes("name\na\n")), "renamed.csv");

        TabularExtractor.Start(cursor, Plan(), Schema, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public void ReportsAMissingSheetAsMissingNotChanged()
    {
        using XlsxCursor cursor = Workbook();

        TabularStructureException error = Assert.Throws<TabularStructureException>(
            () => TabularExtractor.Start(cursor, Plan("Orders", sheetIndex: 5), Schema, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(TabularStructureException.SheetMissing, error.Code);
    }

    [Fact]
    public void ThePrecheckCallsAPlanForAnotherSheetStale()
    {
        using XlsxCursor cursor = Workbook();
        FileProfile profile = new TabularAnalyzer().Analyze(cursor, cancellationToken: TestContext.Current.CancellationToken);

        PrecheckResult result = MappingPrecheck.Check(Plan("Returns", sheetIndex: 0), Schema, profile);

        PrecheckFinding finding = Assert.Single(result.Findings, f => f.Code == ErrorCodes.Mapping.StaleProfile);
        Assert.Equal(PrecheckSeverity.Undetermined, finding.Severity);
    }
}
```

`MappingPlanByHeaderTests.cs` — add to `CarriesTheSheetTheHeaderRowAndTheCulture`, after the `plan.Culture` assertion:

```csharp
        Assert.Equal((sheet.Name, sheet.Source), (plan.SheetName, plan.SheetSource));
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~SheetIdentityTests|FullyQualifiedName~MappingPlanByHeaderTests"`
Expected: build error CS0117 — `MappingPlan` has no `SheetName` / `SheetSource`.

- [ ] **Step 3: Implement**

`MappingPlan.cs` — add after `SheetIndex`:

```csharp
    /// <summary>
    /// The name of the sheet the plan was built for, checked against the sheet found at
    /// <see cref="SheetIndex"/>; null leaves it unchecked.
    /// </summary>
    /// <remarks>
    /// A checksum, as <see cref="ColumnBinding.SourceHeader"/> is for a column. An index says where,
    /// not what: a workbook whose tabs were reordered, or an archive whose entries were written in
    /// another order, puts a different sheet there, and importing it would load the wrong data under
    /// the right headers.
    /// </remarks>
    public string? SheetName { get; init; }

    /// <summary>
    /// The source of the sheet the plan was built for — its path inside an archive — checked like
    /// <see cref="SheetName"/>; null leaves it unchecked.
    /// </summary>
    /// <remarks>Two workbooks in one archive may both hold a <c>Sheet1</c>; this tells them apart.</remarks>
    public string? SheetSource { get; init; }
```

In `ByHeader`'s returned plan add:

```csharp
            SheetName = sheet.Name,
            SheetSource = sheet.Source,
```

`ErrorCodes.cs` — in `Structure`, alphabetically:

```csharp
        /// <summary><c>structure.sheet-changed</c></summary>
        public const string SheetChanged = "structure.sheet-changed";
```

`TabularException.cs` — in `TabularStructureException`, after `SheetMissing`:

```csharp
    /// <summary>The sheet at the plan's index is not the one the plan recorded.</summary>
    public const string SheetChanged = ErrorCodes.Structure.SheetChanged;
```

`ExtractionSession.cs` — in `Position`, right after the `MoveToSheet` check:

```csharp
        VerifySheet(_cursor.Sheets[_plan.SheetIndex]);
```

and add the method beside `VerifyHeaders`:

```csharp
    /// <summary>
    /// Checks the sheet at the plan's index against the one the plan recorded, where it recorded one.
    /// </summary>
    /// <remarks>
    /// Ordinal, as headers are compared: "Orders" and "orders" are different tabs in a workbook that
    /// has both, and guessing which was meant is what this check exists to stop.
    /// </remarks>
    private void VerifySheet(SheetInfo sheet)
    {
        bool nameChanged = _plan.SheetName is not null && !string.Equals(_plan.SheetName, sheet.Name, StringComparison.Ordinal);
        bool sourceChanged = _plan.SheetSource is not null && !string.Equals(_plan.SheetSource, sheet.Source, StringComparison.Ordinal);

        if (nameChanged || sourceChanged)
        {
            throw new TabularStructureException(
                TabularStructureException.SheetChanged,
                $"The sheet at index {_plan.SheetIndex} is not the one the plan was built for.")
            {
                SheetIndex = _plan.SheetIndex,
            };
        }
    }
```

(`_cursor.Sheets[_plan.SheetIndex]` is safe: `MoveToSheet` returned true, so the index exists.)

`MappingPrecheck.cs:126` — replace the `stale` line with:

```csharp
        bool stale = sheet.HeaderRowIndex != plan.HeaderRowIndex
            || (plan.SheetName is not null && !string.Equals(plan.SheetName, sheet.Name, StringComparison.Ordinal))
            || (plan.SheetSource is not null && !string.Equals(plan.SheetSource, sheet.Source, StringComparison.Ordinal));
```

and change the finding's `Detail` to name both causes:

```csharp
                Detail = sheet.HeaderRowIndex != plan.HeaderRowIndex
                    ? $"The file was analysed with row {sheet.HeaderRowIndex} as the header and the "
                        + $"mapping names row {plan.HeaderRowIndex}, so nothing measured about the values "
                        + "applies. Analyse it again to have those checked."
                    : $"The profile's sheet at index {plan.SheetIndex} is not the one the mapping was built "
                        + "for, so nothing measured about it applies.",
```

`docs/guide.md:394` — replace the row with:

```markdown
| `structure.sheet-missing`, `structure.sheet-changed`, `structure.header-row-missing`, `structure.header-changed` | `TabularStructureException`: the file is not the one the plan was built for |
```

- [ ] **Step 4: Run the tests**

Run: `dotnet build TriasDev.Tabular.slnx && dotnet test TriasDev.Tabular.slnx`
Expected: 0 warnings, all tests pass, `ErrorCodeCatalogTests` included.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Let a plan record its sheet, and refuse a file where another sheet stands there"
```

---

### Task 4: Documentation and the performance check

**Files:**
- Modify: `docs/guide.md` (the "Using it" plan paragraph; a sentence on per-sheet facts)
- Modify: `docs/IDEAS.md` (the zip entry points at the spec)
- Modify: `CLAUDE.md` (Architecture: per-sheet facts)

- [ ] **Step 1: Guide.** After the `MappingPlan.ByHeader` paragraph in "Using it", add:

```markdown
A plan built by `ByHeader` also records the sheet's name and source. Extraction checks them against
the sheet at the plan's index and refuses another one (`structure.sheet-changed`), so a workbook whose
tabs were reordered is not imported from the wrong tab. A plan written by hand can leave them null.
```

and after the "Facts and hypotheses are different things" section, add a short section:

```markdown
### What belongs to the sheet

Each `SheetProfile` says what its own source was: `Format`, `Source` (a path inside an archive, else
null), the csv `Dialect` it was read with, and the `Diagnostics` of what was repaired in it.
`FileProfile.Format` is the container's; `FileProfile.Diagnostics` is every sheet together. Both are
snapshots taken when the pass ended.
```

- [ ] **Step 2: IDEAS.md.** At the end of the "A zip of csv files, read as a workbook" entry, add:

```markdown
**Status.** Designed in `docs/superpowers/specs/2026-09-25-archive-as-workbook-design.md`. Its
breaking half — per-sheet format, source, dialect and diagnostics, and a plan that records its
sheet — shipped before v0.1; the archive cursor itself is additive and comes after it.
```

- [ ] **Step 3: CLAUDE.md.** In the Architecture list, under **Analysis**, append: "A sheet's source facts (`Format`, `Source`, `Dialect`, `Diagnostics`) live on `SheetProfile`, not `FileProfile` — an archive holds sources of different kinds."

- [ ] **Step 4: Full check**

Run: `dotnet build TriasDev.Tabular.slnx -c Release --no-incremental && dotnet test TriasDev.Tabular.slnx -c Release`
Expected: 0 warnings, all tests pass.

Performance: build a small harness against `origin/main` and HEAD (as in the stage-2 check), run analysis of the local 5M-row csv three times alternately. Expected: time within noise, allocations equal (the change adds two small objects per sheet).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Document per-sheet source facts and the plan's sheet check"
```
