# Field alternatives Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a schema declare groups of fields in priority order (coordinates → address ladder), judge them per row during extraction, report the result, and offer a full-file dry run before import (issue #110).

**Architecture:** A declaration on `ImportSchema` (`FieldAlternatives` → `AlternativeGroup` → `AlternativeLevel`) is resolved once per run to schema positions (`SchemaAlternatives`). `ExtractionRun.Convert` defers the errors of later-group fields, resolves each set after the row is read, promotes the deferred errors only where the group is needed, and tallies the outcome into an `AlternativesTally`. `TabularExtractor.Review` runs extraction validate-only to the end with progress and returns an `ImportReview`. `MappingPrecheck` adds mapping-level findings from the plan alone.

**Tech Stack:** C# (LangVersion latest), net8.0 + net10.0, xunit.v3 on MTP, PublicAPI analyzers.

**Spec:** `docs/superpowers/specs/2026-10-05-field-alternatives-design.md` (and issue #110).

## Global Constraints

- No package references in the library; everything public lives in namespace `TriasDev.Tabular`.
- Every new public member goes into `src/TriasDev.Tabular/PublicAPI.Unshipped.txt` (the RS0016 code fix writes the line); the build fails otherwise.
- Every new error code is added to `ErrorCodes` **and** the table in `docs/error-codes.md` in the same commit (`ErrorCodeCatalogTests`); every new precheck argument to `PrecheckArguments` **and** the "Precheck arguments" table (`PrecheckArgumentsTests`).
- No per-row allocation on the read path; a schema without alternatives must cost nothing measurable (one `Length > 0` check per row).
- Every collection that grows with the file has a ceiling: row-number lists ≤ `MaxReportedRows` (default 50, max 10,000); review errors ≤ `MaxErrors` (default 50, max 10,000).
- Options are checked where they are handed over (`OptionChecks`).
- Programmer errors are `ArgumentException`; per-row problems are `RowError`s; a row is values or errors, never both.
- Comments and XML docs explain *why*, at the length of the surrounding code. Nothing product-specific in code, docs or commits.
- Commit titles: Conventional Commits (`feat:`, `docs:`, `test:`).
- Run the whole suite before each commit: `dotnet test --solution TriasDev.Tabular.slnx` — reflection-based suites (`NullArgumentTests`, `EqualityTests`, `ApiContractTests`, `ProblemShapeTests`) also judge new public types.

## Review Focus

1. A schema with **no** alternatives behaves and performs exactly as before — pinned by the existing suite plus the benchmark in Task 8.
2. A later-group field with an **invalid value in a row where an earlier group won** is imported as is, never an error — pinned in Task 2 (`KeepsAnInvalidAddressWhereCoordinatesWon`).
3. A field listed in **two levels** of one group (combined street + house) satisfies both — pinned in Task 2 (`OneFieldCanSatisfyTwoLevels`).
4. A **failing row** (ordinary value error) is not counted in the alternatives report — pinned in Task 3 (`CountsOnlyRowsThatProduceValues`).
5. **`ImportPolicy.AllOrNothing`** does not stop the dry run at its first error — pinned in Task 5 (`ReadsToTheEndUnderAllOrNothing`).

---

### Task 1: The declaration and its schema rules

**Files:**
- Create: `src/TriasDev.Tabular/Mapping/FieldAlternatives.cs`
- Create: `src/TriasDev.Tabular/Mapping/SchemaAlternatives.cs`
- Modify: `src/TriasDev.Tabular/Mapping/ImportSchema.cs`
- Modify: `src/TriasDev.Tabular/Mapping/MappingPlanValidator.cs` (call the resolver at the top of `Validate`)
- Modify: `src/TriasDev.Tabular/PublicAPI.Unshipped.txt`
- Test: `tests/TriasDev.Tabular.Tests/Mapping/FieldAlternativesTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class FieldAlternatives(string name, IEnumerable<AlternativeGroup> groups, bool unresolvedRowFails = false)` with `Name`, `Groups : IReadOnlyList<AlternativeGroup>`, `UnresolvedRowFails`.
  - `public sealed class AlternativeGroup` with `static AllOf(string name, params ImportField[] fields)`, `static Ladder(string name, int requiredLevels, params AlternativeLevel[] levels)`, `Name`, `Levels : IReadOnlyList<AlternativeLevel>`, `RequiredLevels`.
  - `public sealed class AlternativeLevel(string name, params ImportField[] anyOf)` with `Name`, `AnyOf : IReadOnlyList<ImportField>`.
  - `ImportSchema.Alternatives : IReadOnlyList<FieldAlternatives>` (default empty).
  - `internal sealed record ResolvedGroup(string Name, string[] LevelNames, int[][] Levels, int[] Members, int RequiredLevels)`.
  - `internal sealed record ResolvedAlternatives(FieldAlternatives Declared, ResolvedGroup[] Groups)`.
  - `internal static ResolvedAlternatives[] SchemaAlternatives.Resolve(ImportSchema schema)` — throws `ArgumentException` on a schema mistake.

- [ ] **Step 1: Write the failing tests**

```csharp
using Xunit;

namespace TriasDev.Tabular.Tests.Mapping;

/// <summary>
/// Groups of fields in priority order: a row needs one usable group, and a later group matters only
/// where the earlier ones fell short. The declaration's own rules are programmer errors.
/// </summary>
public sealed class FieldAlternativesTests
{
    private static readonly DecimalImportField Lat = ImportField.Decimal("lat");
    private static readonly DecimalImportField Lon = ImportField.Decimal("lon");
    private static readonly TextImportField Country = ImportField.Text("country");
    private static readonly TextImportField City = ImportField.Text("city");

    private static FieldAlternatives Location() => new(
        "location",
        [
            AlternativeGroup.AllOf("coordinates", Lat, Lon),
            AlternativeGroup.Ladder("address", 1, new AlternativeLevel("country", Country), new AlternativeLevel("locality", City)),
        ]);

    private static MappingPlan NoBindings => new() { Bindings = [] };

    [Fact]
    public void AllOfMakesOneLevelPerFieldAndNeedsThemAll()
    {
        AlternativeGroup group = AlternativeGroup.AllOf("coordinates", Lat, Lon);

        Assert.Equal(["lat", "lon"], group.Levels.Select(l => l.Name));
        Assert.Equal(2, group.RequiredLevels);
    }

    [Fact]
    public void AcceptsASchemaThatDeclaresEveryField()
    {
        ImportSchema schema = new() { Fields = [Lat, Lon, Country, City], Alternatives = [Location()] };

        Assert.Empty(MappingPlanValidator.Validate(NoBindings, schema));
    }

    [Fact]
    public void RefusesAFieldTheSchemaDoesNotDeclare()
    {
        ImportSchema schema = new() { Fields = [Lat, Lon, Country], Alternatives = [Location()] };

        ArgumentException error = Assert.Throws<ArgumentException>(() => MappingPlanValidator.Validate(NoBindings, schema));
        Assert.Contains("'city'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesARequiredField()
    {
        // Required says every row carries it; an alternative says a row may do without it. One field
        // cannot mean both.
        ImportSchema schema = new() { Fields = [Lat, Lon, Country.Require(), City], Alternatives = [Location()] };

        Assert.Throws<ArgumentException>(() => MappingPlanValidator.Validate(NoBindings, schema));
    }

    [Fact]
    public void RefusesAFieldInTwoGroups()
    {
        FieldAlternatives twice = new(
            "location",
            [AlternativeGroup.AllOf("a", Lat), AlternativeGroup.AllOf("b", Lat)]);

        ImportSchema schema = new() { Fields = [Lat], Alternatives = [twice] };

        Assert.Throws<ArgumentException>(() => MappingPlanValidator.Validate(NoBindings, schema));
    }

    [Fact]
    public void AllowsOneFieldInTwoLevelsOfOneGroup()
    {
        // A column holding "Main Street 5" satisfies the street and the house number at once.
        TextImportField streetAndHouse = ImportField.Text("streetAndHouse");
        FieldAlternatives address = new(
            "location",
            [AlternativeGroup.Ladder("address", 1, new AlternativeLevel("street", streetAndHouse), new AlternativeLevel("house", streetAndHouse))]);

        ImportSchema schema = new() { Fields = [streetAndHouse], Alternatives = [address] };

        Assert.Empty(MappingPlanValidator.Validate(NoBindings, schema));
    }

    [Fact]
    public void RefusesTwoSetsWithOneName()
    {
        ImportSchema schema = new()
        {
            Fields = [Lat, Lon],
            Alternatives = [new("x", [AlternativeGroup.AllOf("a", Lat)]), new("x", [AlternativeGroup.AllOf("b", Lon)])],
        };

        Assert.Throws<ArgumentException>(() => MappingPlanValidator.Validate(NoBindings, schema));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void RefusesRequiredLevelsOutsideTheLadder(int required) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AlternativeGroup.Ladder("address", required, new AlternativeLevel("country", Country), new AlternativeLevel("locality", City)));

    [Fact]
    public void RefusesEmptyDeclarations()
    {
        Assert.Throws<ArgumentException>(() => new FieldAlternatives("location", []));
        Assert.Throws<ArgumentException>(() => AlternativeGroup.AllOf("coordinates"));
        Assert.Throws<ArgumentException>(() => new AlternativeLevel("country"));
        Assert.Throws<ArgumentException>(() => new FieldAlternatives("location", [AlternativeGroup.AllOf("a", Lat), AlternativeGroup.AllOf("a", Lon)]));
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~FieldAlternativesTests"`
Expected: build FAIL — `FieldAlternatives`, `AlternativeGroup`, `AlternativeLevel`, `ImportSchema.Alternatives` do not exist.

- [ ] **Step 3: Write the declaration**

`src/TriasDev.Tabular/Mapping/FieldAlternatives.cs`:

```csharp
namespace TriasDev.Tabular;

/// <summary>
/// Groups of fields in priority order, of which a row needs one: coordinates, and where a row has
/// none, an address.
/// </summary>
/// <remarks>
/// <para>
/// The first group that is usable in a row wins it. A later group is judged only in rows where every
/// earlier one fell short — a row located by its coordinates does not care what its address says —
/// and its fields are validated strictly only there. Everywhere else their values are kept as the
/// file holds them, because a caller may still store them.
/// </para>
/// <para>
/// Rows are judged one by one: whether a particular row carries a usable group is something no
/// column profile can say, since two half-empty columns may cover each other's gaps.
/// </para>
/// </remarks>
public sealed class FieldAlternatives
{
    /// <summary>Declares a set of alternatives.</summary>
    /// <param name="name">The set's name, which reports and row errors carry.</param>
    /// <param name="groups">The groups, the most preferred first.</param>
    /// <param name="unresolvedRowFails">
    /// Whether a row with no usable group is a row error. False by default: the row is imported and
    /// reported, because the caller may hand it to something that decides better than a file can.
    /// </param>
    public FieldAlternatives(string name, IEnumerable<AlternativeGroup> groups, bool unresolvedRowFails = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(groups);

        AlternativeGroup[] list = [.. groups];

        if (list.Length == 0)
        {
            throw new ArgumentException("A set of alternatives needs at least one group.", nameof(groups));
        }

        HashSet<string> names = new(StringComparer.Ordinal);

        foreach (AlternativeGroup group in list)
        {
            ArgumentNullException.ThrowIfNull(group, nameof(groups));

            if (!names.Add(group.Name))
            {
                throw new ArgumentException($"The set '{name}' declares the group '{group.Name}' twice.", nameof(groups));
            }
        }

        Name = name;
        Groups = list;
        UnresolvedRowFails = unresolvedRowFails;
    }

    /// <summary>The set's name.</summary>
    public string Name { get; }

    /// <summary>The groups, the most preferred first.</summary>
    public IReadOnlyList<AlternativeGroup> Groups { get; }

    /// <summary>Whether a row with no usable group is a row error rather than a finding.</summary>
    public bool UnresolvedRowFails { get; }
}

/// <summary>
/// One way of satisfying a set of alternatives: an ordered list of levels, usable from
/// <see cref="RequiredLevels"/> on.
/// </summary>
/// <remarks>
/// A row reaches level <c>k</c> when levels 1 to <c>k</c> each carry a valid value — a gap ends the
/// ladder, because a street without its city locates nothing finer than the country. How far a row
/// reaches is its quality; whether it reaches <see cref="RequiredLevels"/> decides whether the group
/// is usable at all.
/// </remarks>
public sealed class AlternativeGroup
{
    private AlternativeGroup(string name, AlternativeLevel[] levels, int requiredLevels)
    {
        Name = name;
        Levels = levels;
        RequiredLevels = requiredLevels;
    }

    /// <summary>The group's name.</summary>
    public string Name { get; }

    /// <summary>The levels, the indispensable first.</summary>
    public IReadOnlyList<AlternativeLevel> Levels { get; }

    /// <summary>How many levels a row must reach for the group to be usable.</summary>
    public int RequiredLevels { get; }

    /// <summary>A group usable only when every one of its fields carries a value.</summary>
    /// <example><code>AlternativeGroup.AllOf("coordinates", Latitude, Longitude)</code></example>
    public static AlternativeGroup AllOf(string name, params ImportField[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        AlternativeLevel[] levels = [.. fields.Select(f => new AlternativeLevel(
            (f ?? throw new ArgumentException("A field cannot be null.", nameof(fields))).Name, f))];

        return Ladder(name, levels.Length, levels);
    }

    /// <summary>A group whose levels refine one another, usable from <paramref name="requiredLevels"/> on.</summary>
    /// <example>
    /// <code>
    /// AlternativeGroup.Ladder("address", 1,
    ///     new AlternativeLevel("country", Country),
    ///     new AlternativeLevel("locality", PostalCode, City),
    ///     new AlternativeLevel("street", Street, StreetAndHouse),
    ///     new AlternativeLevel("house", HouseNumber, StreetAndHouse))
    /// </code>
    /// </example>
    public static AlternativeGroup Ladder(string name, int requiredLevels, params AlternativeLevel[] levels)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(levels);

        if (levels.Length == 0)
        {
            throw new ArgumentException($"The group '{name}' needs at least one level.", nameof(levels));
        }

        if (levels.Any(l => l is null))
        {
            throw new ArgumentException("A level cannot be null.", nameof(levels));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(requiredLevels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(requiredLevels, levels.Length);

        return new AlternativeGroup(name, [.. levels], requiredLevels);
    }
}

/// <summary>One rung of a group: satisfied when any of its fields carries a valid value.</summary>
/// <remarks>
/// Any, not all: a postal code and a city locate a row equally well, and a file need carry only one.
/// </remarks>
public sealed class AlternativeLevel
{
    /// <summary>Declares a level.</summary>
    /// <param name="name">The level's name, which reports carry.</param>
    /// <param name="anyOf">The fields any one of which satisfies it.</param>
    public AlternativeLevel(string name, params ImportField[] anyOf)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(anyOf);

        if (anyOf.Length == 0 || anyOf.Any(f => f is null))
        {
            throw new ArgumentException($"The level '{name}' needs at least one field, and none of them null.", nameof(anyOf));
        }

        Name = name;
        AnyOf = [.. anyOf];
    }

    /// <summary>The level's name.</summary>
    public string Name { get; }

    /// <summary>The fields any one of which satisfies the level.</summary>
    public IReadOnlyList<ImportField> AnyOf { get; }
}
```

In `src/TriasDev.Tabular/Mapping/ImportSchema.cs`, add after `Fields`:

```csharp
    /// <summary>
    /// Sets of field groups in priority order, judged per row.
    /// </summary>
    /// <remarks>
    /// Empty by default, and then costs nothing: a run with no alternatives does not look for them.
    /// </remarks>
    public IReadOnlyList<FieldAlternatives> Alternatives { get; init; } = [];
```

`src/TriasDev.Tabular/Mapping/SchemaAlternatives.cs`:

```csharp
namespace TriasDev.Tabular;

/// <summary>One group resolved to schema positions.</summary>
/// <param name="Levels">Per level, the positions of the fields any of which satisfies it.</param>
/// <param name="Members">Every position the group names, once.</param>
internal sealed record ResolvedGroup(string Name, string[] LevelNames, int[][] Levels, int[] Members, int RequiredLevels);

/// <summary>One set of alternatives resolved to schema positions.</summary>
internal sealed record ResolvedAlternatives(FieldAlternatives Declared, ResolvedGroup[] Groups);

/// <summary>
/// A schema's alternatives resolved to positions, refused where they cannot mean anything.
/// </summary>
/// <remarks>
/// The mistakes here are in code the programmer wrote, long before any file is opened, so they throw
/// like a duplicate field name does — and from every entry point, so none of them meets them later.
/// </remarks>
internal static class SchemaAlternatives
{
    /// <exception cref="ArgumentException">The alternatives do not fit the schema.</exception>
    public static ResolvedAlternatives[] Resolve(ImportSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        if (schema.Alternatives.Count == 0)
        {
            return [];
        }

        Dictionary<string, int> positions = new(StringComparer.Ordinal);

        for (int i = 0; i < schema.Fields.Count; i++)
        {
            positions[schema.Fields[i].Name] = i;
        }

        HashSet<string> sets = new(StringComparer.Ordinal);
        Dictionary<string, string> owners = new(StringComparer.Ordinal);
        List<ResolvedAlternatives> resolved = [];

        foreach (FieldAlternatives set in schema.Alternatives)
        {
            ArgumentNullException.ThrowIfNull(set, nameof(schema));

            if (!sets.Add(set.Name))
            {
                throw new ArgumentException($"The schema declares more than one set of alternatives named '{set.Name}'.", nameof(schema));
            }

            ResolvedGroup[] groups = [.. set.Groups.Select(g => Group(schema, set, g, positions, owners))];
            resolved.Add(new ResolvedAlternatives(set, groups));
        }

        return [.. resolved];
    }

    private static ResolvedGroup Group(
        ImportSchema schema,
        FieldAlternatives set,
        AlternativeGroup group,
        Dictionary<string, int> positions,
        Dictionary<string, string> owners)
    {
        string owner = $"{set.Name}/{group.Name}";
        int[][] levels = new int[group.Levels.Count][];
        SortedSet<int> members = [];

        for (int l = 0; l < levels.Length; l++)
        {
            levels[l] = [.. group.Levels[l].AnyOf.Select(field => Position(schema, field, owner, positions, owners))];
            members.UnionWith(levels[l]);
        }

        return new ResolvedGroup(group.Name, [.. group.Levels.Select(l => l.Name)], levels, [.. members], group.RequiredLevels);
    }

    private static int Position(
        ImportSchema schema,
        ImportField field,
        string owner,
        Dictionary<string, int> positions,
        Dictionary<string, string> owners)
    {
        if (!positions.TryGetValue(field.Name, out int position))
        {
            throw new ArgumentException($"The alternatives '{owner}' name the field '{field.Name}', which the schema does not declare.", nameof(schema));
        }

        if (schema.Fields[position].Required)
        {
            // Required says every row carries it; an alternative says a row may do without it.
            throw new ArgumentException($"The field '{field.Name}' is required and part of the alternatives '{owner}'; it can be one or the other.", nameof(schema));
        }

        if (owners.TryGetValue(field.Name, out string? other) && !string.Equals(other, owner, StringComparison.Ordinal))
        {
            // Which group a value counts towards must be one answer, or the winner of a row depends
            // on the order the groups were checked in.
            throw new ArgumentException($"The field '{field.Name}' belongs to both '{other}' and '{owner}'.", nameof(schema));
        }

        owners[field.Name] = owner;
        return position;
    }
}
```

In `MappingPlanValidator.Validate`, after the schema's fields are indexed (`SchemaFields.ByName(schema)`), add:

```csharp
        // Refused here as at every other entry point: alternatives that name a field the schema does
        // not declare are the programmer's mistake, not the plan's.
        SchemaAlternatives.Resolve(schema);
```

- [ ] **Step 4: Record the public API**

Build, apply the RS0016 code fix (or add by hand) so `PublicAPI.Unshipped.txt` lists `FieldAlternatives`, its constructor and three getters; `AlternativeGroup`, `AllOf`, `Ladder` and three getters; `AlternativeLevel`, its constructor and two getters; `ImportSchema.Alternatives.get/init`.

Run: `dotnet build TriasDev.Tabular.slnx`
Expected: 0 warnings, 0 errors.

- [ ] **Step 5: Run the tests**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~FieldAlternativesTests"` then `dotnet test --solution TriasDev.Tabular.slnx`
Expected: PASS, whole suite green.

- [ ] **Step 6: Commit**

```bash
git add src/TriasDev.Tabular tests/TriasDev.Tabular.Tests/Mapping/FieldAlternativesTests.cs
git commit -m "feat: declare field groups as alternatives in priority order"
```

---

### Task 2: Judge alternatives per row during extraction

**Files:**
- Create: `src/TriasDev.Tabular/Extraction/AlternativeResolution.cs`
- Modify: `src/TriasDev.Tabular/Extraction/ExtractionRun.cs`
- Modify: `src/TriasDev.Tabular/Abstractions/ErrorCodes.cs` (`Group.Unresolved`)
- Modify: `docs/error-codes.md` (row for `group.unresolved`)
- Modify: `src/TriasDev.Tabular/PublicAPI.Unshipped.txt`
- Test: `tests/TriasDev.Tabular.Tests/Extraction/FieldAlternativesExtractionTests.cs`

**Interfaces:**
- Consumes: `SchemaAlternatives.Resolve`, `ResolvedAlternatives`, `ResolvedGroup` (Task 1).
- Produces:
  - `public readonly record struct AlternativeResolution` with `Group : string?`, `GroupIndex : int` (−1 unresolved), `Level : int`, `LevelName : string?`, `IsResolved : bool`; `internal AlternativeResolution(string? group, int groupIndex, int level, string? levelName)`; `internal static AlternativeResolution Unresolved`.
  - `ExtractionRun.CurrentResolutions : ReadOnlySpan<AlternativeResolution>` — one per set in `schema.Alternatives` order; empty for a failing row.
  - `ErrorCodes.Group.Unresolved = "group.unresolved"`.
  - Internal state Task 3 reads after each row: `_alternatives`, `_groupOffset[s]`, `_needed[id]`, `_levels[id]`, `_anyPresent[id]`, `_ignoredInRow[id]`, `_resolutions[s]` (flat group id = `_groupOffset[s] + g`).

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text;

using Xunit;

namespace TriasDev.Tabular.Tests.Extraction;

/// <summary>
/// Coordinates first, an address where a row has none: which group locates each row, and how a later
/// group's values are validated only where it is needed.
/// </summary>
public sealed class FieldAlternativesExtractionTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly TextImportField Id = ImportField.Text("id").Require();
    private static readonly DecimalImportField Lat = ImportField.Decimal("lat").AtLeast(-90).AtMost(90);
    private static readonly DecimalImportField Lon = ImportField.Decimal("lon").AtLeast(-180).AtMost(180);
    private static readonly TextImportField Country = ImportField.Text("country").AllowedValues(["DEU", "FRA"], ignoreCase: true);
    private static readonly TextImportField City = ImportField.Text("city");
    private static readonly TextImportField Street = ImportField.Text("street");
    private static readonly TextImportField StreetAndHouse = ImportField.Text("streetAndHouse");

    private const string Header = "id;lat;lon;country;city;street;streetAndHouse\n";

    private static ImportSchema Schema(bool unresolvedRowFails = false) => new()
    {
        Fields = [Id, Lat, Lon, Country, City, Street, StreetAndHouse],
        Alternatives =
        [
            new FieldAlternatives(
                "location",
                [
                    AlternativeGroup.AllOf("coordinates", Lat, Lon),
                    AlternativeGroup.Ladder(
                        "address",
                        1,
                        new AlternativeLevel("country", Country),
                        new AlternativeLevel("locality", City),
                        new AlternativeLevel("street", Street, StreetAndHouse),
                        new AlternativeLevel("house", StreetAndHouse)),
                ],
                unresolvedRowFails),
        ],
    };

    [Fact]
    public void CoordinatesWinWhereBothArePresent()
    {
        AlternativeResolution resolution = Single("1;48.1;11.5;;;;\n").Resolution;

        Assert.True(resolution.IsResolved);
        Assert.Equal("coordinates", resolution.Group);
        Assert.Equal(0, resolution.GroupIndex);
        Assert.Equal(2, resolution.Level);
        Assert.Equal("lon", resolution.LevelName);
    }

    [Fact]
    public void FallsBackToTheAddressWhereACoordinateIsMissing()
    {
        AlternativeResolution resolution = Single("1;48.1;;DEU;Munich;;\n").Resolution;

        Assert.Equal("address", resolution.Group);
        Assert.Equal(2, resolution.Level);
        Assert.Equal("locality", resolution.LevelName);
    }

    [Fact]
    public void AGapEndsTheLadder()
    {
        // A street without its city locates nothing finer than the country.
        Assert.Equal(1, Single("1;;;DEU;;Main Street;\n").Resolution.Level);
    }

    [Fact]
    public void OneFieldCanSatisfyTwoLevels()
    {
        Assert.Equal(4, Single("1;;;DEU;Munich;;Main Street 5\n").Resolution.Level);
    }

    [Fact]
    public void KeepsAnInvalidAddressWhereCoordinatesWon()
    {
        // The address is not needed here, so it is not judged — and it is kept, because a caller may
        // store it even when it locates nothing.
        Row row = Single("1;48.1;11.5;XX;;;\n");

        Assert.Empty(row.Errors);
        Assert.Equal("XX", row.Country);
    }

    [Fact]
    public void RefusesAnInvalidAddressWhereItIsNeeded()
    {
        RowError error = Assert.Single(Single("1;;;XX;Munich;;\n").Errors);

        Assert.Equal("value.not-allowed", error.Code);
        Assert.Equal("country", error.FieldName);
    }

    [Fact]
    public void RefusesAnInvalidCoordinateEvenWithAGoodAddress()
    {
        // The first group is always judged: "abc" cannot be stored as a latitude.
        RowError error = Assert.Single(Single("1;abc;11.5;DEU;Munich;;\n").Errors);

        Assert.Equal("value.type-mismatch", error.Code);
        Assert.Equal("lat", error.FieldName);
    }

    [Fact]
    public void ImportsARowNoGroupLocatesByDefault()
    {
        Row row = Single("1;;;;Munich;;\n");

        Assert.Empty(row.Errors);
        Assert.False(row.Resolution.IsResolved);
        Assert.Equal(-1, row.Resolution.GroupIndex);
    }

    [Fact]
    public void RefusesARowNoGroupLocatesWhenTheSetSaysSo()
    {
        RowError error = Assert.Single(Single("1;;;;Munich;;\n", unresolvedRowFails: true).Errors);

        Assert.Equal("group.unresolved", error.Code);
        Assert.Equal("location", error.FieldName);
    }

    private static Row Single(string data, bool unresolvedRowFails = false)
    {
        using ITabularCursor cursor = TabularFile.Open(new MemoryStream(Utf8NoBom.GetBytes(Header + data), writable: false), "test.csv");
        ImportSchema schema = Schema(unresolvedRowFails);
        ExtractionRun run = TabularExtractor.Extract(cursor, Plan(), schema);

        Assert.True(run.ReadRow(TestContext.Current.CancellationToken));

        Row row = new(
            [.. run.CurrentErrors],
            run.CurrentRowHasErrors ? default : run.CurrentResolutions[0],
            run.CurrentRowHasErrors ? null : run.CurrentValues[3].Text);

        Assert.False(run.ReadRow(TestContext.Current.CancellationToken));
        return row;
    }

    private static MappingPlan Plan() => new()
    {
        Bindings =
        [
            .. new[] { "id", "lat", "lon", "country", "city", "street", "streetAndHouse" }
                .Select((name, i) => new ColumnBinding { ColumnIndex = i, Header = name, FieldName = name }),
        ],
    };

    private sealed record Row(RowError[] Errors, AlternativeResolution Resolution, string? Country);
}
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~FieldAlternativesExtractionTests"`
Expected: build FAIL — `AlternativeResolution`, `CurrentResolutions` do not exist.

- [ ] **Step 3: Add the resolution type and the error code**

`src/TriasDev.Tabular/Extraction/AlternativeResolution.cs`:

```csharp
namespace TriasDev.Tabular;

/// <summary>Which group of a set of alternatives a row is located by, and how far it reaches.</summary>
/// <remarks>
/// Information, never a verdict: it changes no value. A caller that hands every row to another
/// service may ignore it; one that decides itself reads the winning group from here instead of
/// repeating the rule the schema already states.
/// </remarks>
public readonly record struct AlternativeResolution
{
    internal AlternativeResolution(string? group, int groupIndex, int level, string? levelName)
    {
        Group = group;
        GroupIndex = groupIndex;
        Level = level;
        LevelName = levelName;
    }

    /// <summary>A row no group of the set can locate.</summary>
    internal static AlternativeResolution Unresolved { get; } = new(null, -1, 0, null);

    /// <summary>The winning group's name, or null when no group is usable.</summary>
    public string? Group { get; }

    /// <summary>The winning group's position in the set, or −1.</summary>
    public int GroupIndex { get; }

    /// <summary>How many levels of the winning group the row reaches; 0 when unresolved.</summary>
    public int Level { get; }

    /// <summary>The deepest level reached, or null when unresolved.</summary>
    public string? LevelName { get; }

    /// <summary>Whether some group locates the row.</summary>
    public bool IsResolved => Group is not null;
}
```

In `ErrorCodes.Group` add:

```csharp
        /// <summary>A row no group of a set of alternatives can locate, where the set says that fails it.</summary>
        public const string Unresolved = "group.unresolved";
```

In `docs/error-codes.md`, below the `group.required` row:

```markdown
| `group.unresolved` | A row no group of a set of alternatives makes usable; a row error only where the set's `UnresolvedRowFails` says so, otherwise a precheck finding and a count in the run's report |
```

- [ ] **Step 4: Resolve the alternatives in `ExtractionRun`**

Add fields beside `_requiredGroups`:

```csharp
    // Alternatives, resolved once. Flat group ids (_groupOffset[set] + group) index the per-row arrays,
    // so judging a row allocates nothing.
    private readonly ResolvedAlternatives[] _alternatives;
    private readonly int[] _groupOffset;
    private readonly int[] _setColumn;
    private readonly int[] _deferredGroup;   // per schema position: the flat id of a later group, or -1
    private readonly bool[] _invalid;
    private readonly bool[] _needed;
    private readonly int[] _levels;
    private readonly bool[] _anyPresent;
    private readonly int[] _ignoredInRow;
    private readonly AlternativeResolution[] _resolutions;
    private readonly List<(RowError Error, int Group)> _deferred = [];
```

At the end of the constructor, before `Position();`:

```csharp
        _alternatives = SchemaAlternatives.Resolve(schema);
        _groupOffset = new int[_alternatives.Length];
        _setColumn = new int[_alternatives.Length];
        _resolutions = new AlternativeResolution[_alternatives.Length];
        _deferredGroup = new int[fields.Length];
        _invalid = new bool[fields.Length];
        Array.Fill(_deferredGroup, -1);

        int groups = 0;

        for (int s = 0; s < _alternatives.Length; s++)
        {
            _groupOffset[s] = groups;
            HashSet<int> members = [.. _alternatives[s].Groups.SelectMany(g => g.Members)];

            // The column a set's row error points at: its first mapped member, as for a required group.
            _setColumn[s] = mappings.FirstOrDefault(m => members.Contains(m.Position)).Binding?.ColumnIndex ?? -1;

            for (int g = 0; g < _alternatives[s].Groups.Length; g++, groups++)
            {
                // The first group is judged like any field; a later one only where it is needed, so
                // its errors wait until the row shows whether it is.
                if (g > 0)
                {
                    foreach (int position in _alternatives[s].Groups[g].Members)
                    {
                        _deferredGroup[position] = groups;
                    }
                }
            }
        }

        _needed = new bool[groups];
        _levels = new int[groups];
        _anyPresent = new bool[groups];
        _ignoredInRow = new int[groups];
```

Add the property after `CurrentErrors`:

```csharp
    /// <summary>
    /// The current row's resolution per set of alternatives, in schema order. Valid until the next
    /// <see cref="ReadRow"/>, and empty for a failing row.
    /// </summary>
    public ReadOnlySpan<AlternativeResolution> CurrentResolutions =>
        CurrentRowHasErrors ? [] : _resolutions.AsSpan();
```

In `Convert`: after `Array.Clear(_present);` add `Array.Clear(_invalid); _deferred.Clear();`. Pass the field's position into every `Fail`/`Check` call for a value (`Fail(binding, ErrorCodes.Value.TypeMismatch, text, position)`, `Check(binding, field.Constraints[c], value, text, position)`; the `Required` call stays without it — a field in a set cannot be required). After `CheckRequiredGroups();` add:

```csharp
        if (_alternatives.Length > 0)
        {
            ResolveAlternatives();
        }
```

Change `Check` to take `int position` and pass it on to `Fail`. Replace `Fail`:

```csharp
    private void Fail(ColumnBinding binding, string code, string? raw, int position = -1)
    {
        RowError error = new()
        {
            RowNumber = CurrentRowNumber,
            ColumnIndex = binding.ColumnIndex,
            FieldName = binding.FieldName,
            Code = code,
            RawValue = raw,
        };

        int group = position >= 0 ? _deferredGroup[position] : -1;

        if (group >= 0)
        {
            // A later group's value: wrong only if the row turns out to need the group.
            _invalid[position] = true;
            _deferred.Add((error, group));
            return;
        }

        _errors.Add(error);
    }
```

Add the resolution:

```csharp
    /// <summary>
    /// Finds each set's winning group and decides the fate of the errors that waited for it.
    /// </summary>
    /// <remarks>
    /// A group is needed while no earlier group is usable. Its waiting errors become the row's where
    /// it is needed; elsewhere they are dropped and counted, and the values stay as the file holds
    /// them — a caller may store an address it does not locate by.
    /// </remarks>
    private void ResolveAlternatives()
    {
        Array.Clear(_ignoredInRow);

        for (int s = 0; s < _alternatives.Length; s++)
        {
            ResolvedAlternatives set = _alternatives[s];
            int offset = _groupOffset[s];
            int winner = -1;

            for (int g = 0; g < set.Groups.Length; g++)
            {
                ResolvedGroup group = set.Groups[g];
                int id = offset + g;

                _needed[id] = winner < 0;
                _levels[id] = LevelsReached(group);
                _anyPresent[id] = AnyPresent(group.Members);

                if (winner < 0 && _levels[id] >= group.RequiredLevels)
                {
                    winner = g;
                }
            }

            if (winner >= 0)
            {
                ResolvedGroup won = set.Groups[winner];
                int level = _levels[offset + winner];
                _resolutions[s] = new AlternativeResolution(won.Name, winner, level, won.LevelNames[level - 1]);
            }
            else
            {
                _resolutions[s] = AlternativeResolution.Unresolved;

                if (set.Declared.UnresolvedRowFails)
                {
                    _errors.Add(new RowError
                    {
                        RowNumber = CurrentRowNumber,
                        ColumnIndex = _setColumn[s],
                        FieldName = set.Declared.Name,
                        Code = ErrorCodes.Group.Unresolved,
                        RawValue = null,
                    });
                }
            }
        }

        for (int d = 0; d < _deferred.Count; d++)
        {
            (RowError error, int group) = _deferred[d];

            if (_needed[group])
            {
                _errors.Add(error);
            }
            else
            {
                _ignoredInRow[group]++;
            }
        }
    }

    /// <summary>How many levels in a row carry a valid value, counting from the first.</summary>
    private int LevelsReached(ResolvedGroup group)
    {
        int reached = 0;

        while (reached < group.Levels.Length && AnyValid(group.Levels[reached]))
        {
            reached++;
        }

        return reached;
    }

    private bool AnyValid(int[] positions)
    {
        for (int i = 0; i < positions.Length; i++)
        {
            if (_present[positions[i]] && !_invalid[positions[i]])
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the row writes anything at all into the group, valid or not.</summary>
    private bool AnyPresent(int[] positions)
    {
        for (int i = 0; i < positions.Length; i++)
        {
            if (_present[positions[i]] || _invalid[positions[i]])
            {
                return true;
            }
        }

        return false;
    }
```

- [ ] **Step 5: Record the public API, build, run the tests**

Run: `dotnet build TriasDev.Tabular.slnx` (apply RS0016 fixes: `AlternativeResolution` and its members, `ExtractionRun.CurrentResolutions.get`, `ErrorCodes.Group.Unresolved`), then `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~FieldAlternativesExtractionTests"` and `dotnet test --solution TriasDev.Tabular.slnx`.
Expected: PASS; `ErrorCodeCatalogTests` green.

- [ ] **Step 6: Commit**

```bash
git add src/TriasDev.Tabular docs/error-codes.md tests/TriasDev.Tabular.Tests/Extraction/FieldAlternativesExtractionTests.cs
git commit -m "feat: judge field alternatives per row during extraction"
```

---

### Task 3: Count the outcome into a report

**Files:**
- Create: `src/TriasDev.Tabular/Extraction/AlternativesReport.cs` (public records)
- Create: `src/TriasDev.Tabular/Extraction/AlternativesTally.cs` (internal counters)
- Modify: `src/TriasDev.Tabular/Extraction/ExtractionRun.cs`
- Modify: `src/TriasDev.Tabular/Extraction/ExtractionOptions.cs`
- Modify: `src/TriasDev.Tabular/Abstractions/OptionChecks.cs` (`AtMost`)
- Modify: `src/TriasDev.Tabular/PublicAPI.Unshipped.txt`
- Test: `tests/TriasDev.Tabular.Tests/Extraction/AlternativesReportTests.cs`

**Interfaces:**
- Consumes: Task 2's per-row arrays.
- Produces:
  - `public sealed record AlternativesReport { Name, RowsJudged, Groups : IReadOnlyList<AlternativeGroupReport>, Unresolved, UnresolvedRows : IReadOnlyList<int>, UnresolvedRowsComplete }`.
  - `public sealed record AlternativeGroupReport { Name, RowsNeeding, Won, MaxReachableLevel, RowsAtLevel : IReadOnlyList<int>, Incomplete, Empty, IncompleteRows : IReadOnlyList<int>, IncompleteRowsComplete, IgnoredInvalidValues }`.
  - `ExtractionRun.Alternatives : IReadOnlyList<AlternativesReport>` (snapshot).
  - `ExtractionOptions.MaxReportedRows` (default 50, 1..10,000); `ExtractionOptions.ReportedRowsCeiling = 10_000` (public const).
  - `internal static void OptionChecks.AtMost(long value, long maximum, string owner, string option, string options = "options")`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text;

using Xunit;

namespace TriasDev.Tabular.Tests.Extraction;

/// <summary>What a run counts about its alternatives: the numbers a review screen shows.</summary>
public sealed class AlternativesReportTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly TextImportField Id = ImportField.Text("id").Require();
    private static readonly DecimalImportField Lat = ImportField.Decimal("lat");
    private static readonly DecimalImportField Lon = ImportField.Decimal("lon");
    private static readonly TextImportField Country = ImportField.Text("country").AllowedValues(["DEU"]);
    private static readonly TextImportField City = ImportField.Text("city");
    private static readonly TextImportField Street = ImportField.Text("street");

    private const string Header = "id;lat;lon;country;city;street\n";

    private static ImportSchema Schema => new()
    {
        Fields = [Id, Lat, Lon, Country, City, Street],
        Alternatives =
        [
            new FieldAlternatives(
                "location",
                [
                    AlternativeGroup.AllOf("coordinates", Lat, Lon),
                    AlternativeGroup.Ladder("address", 1, new AlternativeLevel("country", Country), new AlternativeLevel("locality", City), new AlternativeLevel("street", Street)),
                ]),
        ],
    };

    [Fact]
    public void CountsEachGroupWhereItIsNeeded()
    {
        // Spreadsheet rows 2 to 5: the header is row 1.
        AlternativesReport report = Report(
            "1;48.1;11.5;;;\n"              // row 2: coordinates
            + "2;48.1;;DEU;Munich;Main\n"   // row 3: partial coordinates, full address
            + "3;;;DEU;;\n"                 // row 4: country only
            + "4;;;;Munich;\n");            // row 5: nothing usable

        Assert.Equal(4, report.RowsJudged);

        AlternativeGroupReport coordinates = report.Groups[0];
        Assert.Equal(4, coordinates.RowsNeeding);
        Assert.Equal(1, coordinates.Won);
        Assert.Equal(3, coordinates.Incomplete);
        Assert.Equal(2, coordinates.Empty);
        Assert.Equal([3, 4, 5], coordinates.IncompleteRows);

        AlternativeGroupReport address = report.Groups[1];
        Assert.Equal(3, address.RowsNeeding);              // rows 3, 4, 5: no usable coordinates
        Assert.Equal(2, address.Won);
        Assert.Equal(3, address.MaxReachableLevel);
        Assert.Equal([1, 1, 0, 1], address.RowsAtLevel);   // row 5 at 0, row 4 at 1, row 3 at 3
        Assert.Equal(2, address.Incomplete);
        Assert.Equal([4, 5], address.IncompleteRows);

        Assert.Equal(1, report.Unresolved);
        Assert.Equal([5], report.UnresolvedRows);
        Assert.True(report.UnresolvedRowsComplete);
    }

    [Fact]
    public void CountsInvalidValuesItDidNotNeedToJudge()
    {
        AlternativesReport report = Report("1;48.1;11.5;XX;;\n");

        Assert.Equal(1, report.Groups[1].IgnoredInvalidValues);
        Assert.Equal(0, report.Groups[1].RowsNeeding);
    }

    [Fact]
    public void CountsOnlyRowsThatProduceValues()
    {
        // Row 2 fails on its latitude and is not imported, so it is in the errors, not in the report.
        AlternativesReport report = Report("1;48.1;11.5;;;\n2;abc;11.5;;;\n");

        Assert.Equal(1, report.RowsJudged);
        Assert.Equal(1, report.Groups[0].Won);
    }

    [Fact]
    public void CapsTheRowNumbersItKeeps()
    {
        // Five rows with a city and nothing else, at spreadsheet rows 2 to 6.
        string data = string.Concat(Enumerable.Range(1, 5).Select(i => $"{i};;;;x;\n"));
        AlternativesReport report = Report(data, new ExtractionOptions { MaxReportedRows = 2 });

        Assert.Equal(5, report.Unresolved);
        Assert.Equal([2, 3], report.UnresolvedRows);
        Assert.False(report.UnresolvedRowsComplete);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10_001)]
    public void RefusesARowCapOutsideItsRange(int cap)
    {
        using ITabularCursor cursor = Open(Header + "1;;;;;\n");

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TabularExtractor.Extract(cursor, Plan(), Schema, new ExtractionOptions { MaxReportedRows = cap }));
    }

    private static AlternativesReport Report(string data, ExtractionOptions? options = null)
    {
        using ITabularCursor cursor = Open(Header + data);
        ExtractionRun run = TabularExtractor.Extract(cursor, Plan(), Schema, options);

        while (run.ReadRow(TestContext.Current.CancellationToken))
        {
        }

        return Assert.Single(run.Alternatives);
    }

    private static MappingPlan Plan() => new()
    {
        Bindings =
        [
            .. new[] { "id", "lat", "lon", "country", "city", "street" }
                .Select((name, i) => new ColumnBinding { ColumnIndex = i, Header = name, FieldName = name }),
        ],
    };

    private static ITabularCursor Open(string csv) =>
        TabularFile.Open(new MemoryStream(Utf8NoBom.GetBytes(csv), writable: false), "test.csv");
}
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~AlternativesReportTests"`
Expected: build FAIL — `AlternativesReport`, `ExtractionRun.Alternatives`, `MaxReportedRows` do not exist.

- [ ] **Step 3: Write the report records**

`src/TriasDev.Tabular/Extraction/AlternativesReport.cs`:

```csharp
namespace TriasDev.Tabular;

/// <summary>What a run found about one set of alternatives, over the rows that produced values.</summary>
/// <remarks>
/// Rows that failed are left out: they are not imported, and the run's errors already say why. So
/// the percentages a screen derives from <see cref="RowsJudged"/> describe the rows that will arrive.
/// </remarks>
public sealed record AlternativesReport
{
    /// <summary>The set's name.</summary>
    public required string Name { get; init; }

    /// <summary>Rows that produced values, every one of them judged.</summary>
    public required int RowsJudged { get; init; }

    /// <summary>Per group, in the set's order.</summary>
    public required IReadOnlyList<AlternativeGroupReport> Groups { get; init => field = Equatable.List(value); }

    /// <summary>Rows no group made usable.</summary>
    public required int Unresolved { get; init; }

    /// <summary>The first of them, by spreadsheet row number, up to <see cref="ExtractionOptions.MaxReportedRows"/>.</summary>
    public required IReadOnlyList<int> UnresolvedRows { get; init => field = Equatable.List(value); }

    /// <summary>Whether <see cref="UnresolvedRows"/> holds every one.</summary>
    public required bool UnresolvedRowsComplete { get; init; }
}

/// <summary>What a run found about one group of a set of alternatives.</summary>
public sealed record AlternativeGroupReport
{
    /// <summary>The group's name.</summary>
    public required string Name { get; init; }

    /// <summary>Rows where every earlier group fell short, so this one was judged; every row for the first group.</summary>
    public required int RowsNeeding { get; init; }

    /// <summary>Rows this group located.</summary>
    public required int Won { get; init; }

    /// <summary>
    /// The deepest level the mapping can reach: levels with no mapped field end the ladder for every row.
    /// </summary>
    /// <remarks>
    /// What <see cref="Incomplete"/> is measured against, so an unmapped house number does not turn
    /// every row into a warning — the mapping says that once, in the precheck.
    /// </remarks>
    public required int MaxReachableLevel { get; init; }

    /// <summary>Rows needing the group, by how many levels they reach: index 0 to the level count.</summary>
    public required IReadOnlyList<int> RowsAtLevel { get; init => field = Equatable.List(value); }

    /// <summary>Rows needing the group that stop short of <see cref="MaxReachableLevel"/>.</summary>
    public required int Incomplete { get; init; }

    /// <summary>Rows needing the group that write nothing into any of its fields.</summary>
    public required int Empty { get; init; }

    /// <summary>The first incomplete rows, by spreadsheet row number, up to <see cref="ExtractionOptions.MaxReportedRows"/>.</summary>
    public required IReadOnlyList<int> IncompleteRows { get; init => field = Equatable.List(value); }

    /// <summary>Whether <see cref="IncompleteRows"/> holds every one.</summary>
    public required bool IncompleteRowsComplete { get; init; }

    /// <summary>
    /// Values that would have failed validation, in rows where an earlier group won and this one was
    /// not judged; kept as the file holds them.
    /// </summary>
    public required int IgnoredInvalidValues { get; init; }
}
```

- [ ] **Step 4: Write the tally and wire it in**

`src/TriasDev.Tabular/Extraction/AlternativesTally.cs`:

```csharp
namespace TriasDev.Tabular;

/// <summary>
/// The counters behind <see cref="AlternativesReport"/>, kept by a run as it reads.
/// </summary>
/// <remarks>
/// Fixed in size for the whole run except the row-number lists, which stop at their cap: a file of
/// five million unlocatable rows keeps fifty numbers and a count.
/// </remarks>
internal sealed class AlternativesTally
{
    private readonly ResolvedAlternatives[] _sets;
    private readonly int[] _offset;
    private readonly int[] _maxReachable;
    private readonly int _cap;

    private readonly int[] _judged;
    private readonly int[] _unresolved;
    private readonly List<int>[] _unresolvedRows;

    private readonly int[] _needing;
    private readonly int[] _won;
    private readonly int[][] _atLevel;
    private readonly int[] _incomplete;
    private readonly int[] _empty;
    private readonly int[] _ignored;
    private readonly List<int>[] _incompleteRows;

    public AlternativesTally(ResolvedAlternatives[] sets, int[] offset, int[] maxReachable, int cap)
    {
        _sets = sets;
        _offset = offset;
        _maxReachable = maxReachable;
        _cap = cap;

        _judged = new int[sets.Length];
        _unresolved = new int[sets.Length];
        _unresolvedRows = [.. sets.Select(_ => new List<int>())];

        ResolvedGroup[] groups = [.. sets.SelectMany(s => s.Groups)];
        _needing = new int[groups.Length];
        _won = new int[groups.Length];
        _atLevel = [.. groups.Select(g => new int[g.Levels.Length + 1])];
        _incomplete = new int[groups.Length];
        _empty = new int[groups.Length];
        _ignored = new int[groups.Length];
        _incompleteRows = [.. groups.Select(_ => new List<int>())];
    }

    /// <summary>Counts one row that produced values.</summary>
    public void Row(
        int rowNumber,
        ReadOnlySpan<AlternativeResolution> resolutions,
        bool[] needed,
        int[] levels,
        bool[] anyPresent,
        int[] ignored)
    {
        for (int s = 0; s < _sets.Length; s++)
        {
            _judged[s]++;

            if (!resolutions[s].IsResolved)
            {
                _unresolved[s]++;
                Keep(_unresolvedRows[s], rowNumber);
            }

            for (int g = 0; g < _sets[s].Groups.Length; g++)
            {
                int id = _offset[s] + g;
                _ignored[id] += ignored[id];

                if (!needed[id])
                {
                    continue;
                }

                _needing[id]++;
                _atLevel[id][levels[id]]++;

                if (resolutions[s].GroupIndex == g)
                {
                    _won[id]++;
                }

                if (!anyPresent[id])
                {
                    _empty[id]++;
                }

                if (levels[id] < _maxReachable[id])
                {
                    _incomplete[id]++;
                    Keep(_incompleteRows[id], rowNumber);
                }
            }
        }
    }

    public IReadOnlyList<AlternativesReport> Snapshot() =>
    [
        .. _sets.Select((set, s) => new AlternativesReport
        {
            Name = set.Declared.Name,
            RowsJudged = _judged[s],
            Unresolved = _unresolved[s],
            UnresolvedRows = [.. _unresolvedRows[s]],
            UnresolvedRowsComplete = _unresolvedRows[s].Count == _unresolved[s],
            Groups =
            [
                .. set.Groups.Select((group, g) =>
                {
                    int id = _offset[s] + g;

                    return new AlternativeGroupReport
                    {
                        Name = group.Name,
                        RowsNeeding = _needing[id],
                        Won = _won[id],
                        MaxReachableLevel = _maxReachable[id],
                        RowsAtLevel = [.. _atLevel[id]],
                        Incomplete = _incomplete[id],
                        Empty = _empty[id],
                        IncompleteRows = [.. _incompleteRows[id]],
                        IncompleteRowsComplete = _incompleteRows[id].Count == _incomplete[id],
                        IgnoredInvalidValues = _ignored[id],
                    };
                }),
            ],
        }),
    ];

    private void Keep(List<int> rows, int rowNumber)
    {
        if (rows.Count < _cap)
        {
            rows.Add(rowNumber);
        }
    }
}
```

`OptionChecks.AtMost` (beside `AtLeast`):

```csharp
    /// <param name="options">The parameter the options arrived through, as the exception names it.</param>
    public static void AtMost(long value, long maximum, string owner, string option, string options = "options")
    {
        if (value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                options,
                value,
                $"{owner}.{option} must be at most {maximum}.");
        }
    }
```

`ExtractionOptions` — add:

```csharp
    /// <summary>The most row numbers any list in a report may hold.</summary>
    public const int ReportedRowsCeiling = 10_000;

    /// <summary>
    /// How many row numbers each list of an <see cref="AlternativesReport"/> keeps; the counts are
    /// always complete.
    /// </summary>
    /// <remarks>
    /// Fifty by default: enough for a person to look at, and a list of every row of a large file
    /// would hold memory in proportion to it. At most <see cref="ReportedRowsCeiling"/>.
    /// </remarks>
    public int MaxReportedRows { get; init; } = 50;
```

and in `Checked()`:

```csharp
        OptionChecks.AtLeast(MaxReportedRows, 1, nameof(ExtractionOptions), nameof(MaxReportedRows));
        OptionChecks.AtMost(MaxReportedRows, ReportedRowsCeiling, nameof(ExtractionOptions), nameof(MaxReportedRows));
```

In `ExtractionRun`'s constructor, after the alternatives block of Task 2:

```csharp
        _tally = _alternatives.Length == 0
            ? null
            : new AlternativesTally(_alternatives, _groupOffset, MaxReachable(_alternatives, mappings), options.MaxReportedRows);
```

with the field `private readonly AlternativesTally? _tally;` and:

```csharp
    /// <summary>Per flat group id, how many levels the mapping can reach before a level with no mapped field.</summary>
    private static int[] MaxReachable(ResolvedAlternatives[] sets, List<Mapped> mappings)
    {
        HashSet<int> mapped = [.. mappings.Select(m => m.Position)];

        return
        [
            .. sets.SelectMany(s => s.Groups).Select(g =>
            {
                int reachable = 0;

                while (reachable < g.Levels.Length && g.Levels[reachable].Any(mapped.Contains))
                {
                    reachable++;
                }

                return reachable;
            }),
        ];
    }
```

Add the property:

```csharp
    /// <summary>What the run found about each set of alternatives, as it stands; complete once the rows have been read out.</summary>
    public IReadOnlyList<AlternativesReport> Alternatives => _tally?.Snapshot() ?? [];
```

In `ReadRow`, in the success branch just before `_counters.RowsProduced++;`:

```csharp
            _tally?.Row(CurrentRowNumber, _resolutions, _needed, _levels, _anyPresent, _ignoredInRow);
```

- [ ] **Step 5: Record the public API, build, run the tests**

Run: `dotnet build TriasDev.Tabular.slnx` (RS0016: both records and their members, `ExtractionRun.Alternatives.get`, `ExtractionOptions.MaxReportedRows.get/init`, `ExtractionOptions.ReportedRowsCeiling`), `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~AlternativesReportTests"`, then `dotnet test --solution TriasDev.Tabular.slnx`.
Expected: PASS. If `OptionDefaultsTests` lists every option default, add `MaxReportedRows = 50` there.

- [ ] **Step 6: Commit**

```bash
git add src/TriasDev.Tabular tests/TriasDev.Tabular.Tests/Extraction/AlternativesReportTests.cs tests/TriasDev.Tabular.Tests/OptionDefaultsTests.cs
git commit -m "feat: report how each set of alternatives covers the rows"
```

---

### Task 4: The resolution on `ImportRow`, the report on `ImportRun<T>`

**Files:**
- Modify: `src/TriasDev.Tabular/Import/ImportRow.cs`
- Modify: `src/TriasDev.Tabular/Import/FieldIndex.cs`
- Modify: `src/TriasDev.Tabular/Import/TabularImporter.cs` (`ImportRun<T>.Current()` builds the row; add `Alternatives`)
- Modify: `src/TriasDev.Tabular/PublicAPI.Unshipped.txt`
- Test: `tests/TriasDev.Tabular.Tests/Import/FieldAlternativesImportTests.cs`

**Interfaces:**
- Consumes: `ExtractionRun.CurrentResolutions`, `ExtractionRun.Alternatives` (Tasks 2–3).
- Produces: `ImportRow.Resolution(FieldAlternatives alternatives) : AlternativeResolution` (throws `ArgumentException` for a set the schema does not declare); `ImportRun<T>.Alternatives : IReadOnlyList<AlternativesReport>`; `FieldIndex.SetOf(FieldAlternatives) : int`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text;

using Xunit;

namespace TriasDev.Tabular.Tests.Import;

/// <summary>A mapper reads which group locates its row; the run carries the report.</summary>
public sealed class FieldAlternativesImportTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly DecimalImportField Lat = ImportField.Decimal("lat");
    private static readonly DecimalImportField Lon = ImportField.Decimal("lon");
    private static readonly TextImportField Country = ImportField.Text("country");

    private static readonly FieldAlternatives Location = new(
        "location",
        [AlternativeGroup.AllOf("coordinates", Lat, Lon), AlternativeGroup.Ladder("address", 1, new AlternativeLevel("country", Country))]);

    private static ImportSchema Schema => new() { Fields = [Lat, Lon, Country], Alternatives = [Location] };

    [Fact]
    public void HandsTheMapperTheWinningGroup()
    {
        using ImportRun<string?> run = Run("lat;lon;country\n48.1;11.5;\n;;DEU\n", row => row.Resolution(Location).Group);

        Assert.Equal(["coordinates", "address"], run.ReadAll(cancellationToken: TestContext.Current.CancellationToken).Items);
    }

    [Fact]
    public void CarriesTheReportOnTheRun()
    {
        using ImportRun<int> run = Run("lat;lon;country\n48.1;11.5;\n;;\n", row => row.RowNumber);

        run.ReadAll(cancellationToken: TestContext.Current.CancellationToken);

        AlternativesReport report = Assert.Single(run.Alternatives);
        Assert.Equal(1, report.Unresolved);
    }

    [Fact]
    public void RefusesASetTheSchemaDoesNotDeclare()
    {
        FieldAlternatives other = new("other", [AlternativeGroup.AllOf("x", Lat)]);
        using ImportRun<int> run = Run("lat;lon;country\n48.1;11.5;\n", row => row.Resolution(other).Level);

        Assert.Throws<ArgumentException>(() => run.ReadAll(cancellationToken: TestContext.Current.CancellationToken));
    }

    private static ImportRun<T> Run<T>(string csv, TabularRowMapper<T> mapper) =>
        TabularImporter.Import(
            new MemoryStream(Utf8NoBom.GetBytes(csv), writable: false),
            "test.csv",
            new MappingPlan
            {
                Bindings =
                [
                    new ColumnBinding { ColumnIndex = 0, Header = "lat", FieldName = "lat" },
                    new ColumnBinding { ColumnIndex = 1, Header = "lon", FieldName = "lon" },
                    new ColumnBinding { ColumnIndex = 2, Header = "country", FieldName = "country" },
                ],
            },
            Schema,
            mapper);
}
```

(Check the name of the items list on `ImportResult<T>` — `Items` above — and adjust if it differs.)

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~FieldAlternativesImportTests"`
Expected: build FAIL — `ImportRow.Resolution`, `ImportRun<T>.Alternatives` do not exist.

- [ ] **Step 3: Implement**

`FieldIndex` — store the sets by name in the constructor and add the lookup:

```csharp
    private readonly Dictionary<string, int> _bySet;

    // in the constructor:
        _bySet = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int i = 0; i < schema.Alternatives.Count; i++)
        {
            _bySet[schema.Alternatives[i].Name] = i;
        }

    public int SetOf(FieldAlternatives alternatives)
    {
        if (_bySet.TryGetValue(alternatives.Name, out int index))
        {
            return index;
        }

        throw new ArgumentException(
            $"The schema declares no set of alternatives named '{alternatives.Name}'. It declares: "
            + (_bySet.Count == 0 ? "none" : string.Join(", ", _bySet.Keys.Order(StringComparer.Ordinal))) + ".",
            nameof(alternatives));
    }
```

`ImportRow` — add a span of resolutions:

```csharp
    private readonly ReadOnlySpan<AlternativeResolution> _resolutions;

    internal ImportRow(ReadOnlySpan<MappedValue> values, ReadOnlySpan<AlternativeResolution> resolutions, FieldIndex index, int rowNumber)
    {
        _values = values;
        _resolutions = resolutions;
        _index = index;
        RowNumber = rowNumber;
    }

    /// <summary>Which group of a set of alternatives locates this row, and how far it reaches.</summary>
    /// <remarks>Information only: every value of the row is there whichever group won.</remarks>
    /// <exception cref="ArgumentException">The schema declares no such set.</exception>
    public AlternativeResolution Resolution(FieldAlternatives alternatives)
    {
        ArgumentNullException.ThrowIfNull(alternatives);
        return _resolutions[_index.SetOf(alternatives)];
    }
```

Update every `new ImportRow(...)` call (in `ImportRun<T>.Current()` and anywhere else `grep -rn "new ImportRow(" src` finds) to pass `_session.CurrentResolutions` as the second argument.

`ImportRun<T>` — add:

```csharp
    /// <summary>What the run found about each set of alternatives; complete once the rows have been read.</summary>
    public IReadOnlyList<AlternativesReport> Alternatives => _session.Alternatives;
```

- [ ] **Step 4: Record the public API, build, run the tests**

Run: `dotnet build TriasDev.Tabular.slnx` (RS0016: `ImportRow.Resolution`, `ImportRun<T>.Alternatives.get`), `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~FieldAlternativesImportTests"`, then the whole suite.
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/TriasDev.Tabular tests/TriasDev.Tabular.Tests/Import/FieldAlternativesImportTests.cs
git commit -m "feat: hand a mapper the group that locates its row"
```

---

### Task 5: The dry run — `TabularExtractor.Review`

**Files:**
- Create: `src/TriasDev.Tabular/Extraction/ImportReview.cs` (`ImportReview`, `ReviewOptions`)
- Create: `src/TriasDev.Tabular/Analysis/ProgressReporter.cs` (moved out of `AnalysisRun`, now `internal sealed class`)
- Modify: `src/TriasDev.Tabular/Analysis/TabularAnalyzer.cs` (remove the nested class; use the moved one)
- Modify: `src/TriasDev.Tabular/Extraction/TabularExtractor.cs`
- Modify: `src/TriasDev.Tabular/Extraction/ExtractionRun.cs` (internal `ignoreErrorLimit` constructor parameter)
- Modify: `src/TriasDev.Tabular/PublicAPI.Unshipped.txt`
- Test: `tests/TriasDev.Tabular.Tests/Extraction/ReviewTests.cs`

**Interfaces:**
- Consumes: `ExtractionRun` with `Alternatives` (Task 3); `AnalysisProgress`.
- Produces:
  - `public static ImportReview TabularExtractor.Review(ITabularCursor cursor, MappingPlan plan, ImportSchema schema, ReviewOptions? options = null, IProgress<AnalysisProgress>? progress = null, CancellationToken cancellationToken = default)`.
  - `public sealed record ReviewOptions { MaxErrors = 50, MaxReportedRows = 50, ProgressInterval = 10_000, ProgressStep = 0.01; static Default }`.
  - `public sealed record ImportReview { Summary : ExtractionSummary, Errors : IReadOnlyList<RowError>, ErrorsComplete : bool, Alternatives : IReadOnlyList<AlternativesReport> }`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text;

using Xunit;

namespace TriasDev.Tabular.Tests.Extraction;

/// <summary>
/// The whole file through the mapping, keeping nothing, before anything is written: what a review
/// screen shows between mapping and import.
/// </summary>
public sealed class ReviewTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly IntegerImportField Amount = ImportField.Integer("amount");

    private static readonly MappingPlan Plan = new()
    {
        Bindings = [new ColumnBinding { ColumnIndex = 0, Header = "amount", FieldName = "amount" }],
    };

    [Fact]
    public void ReadsEveryRowAndCountsEveryError()
    {
        ImportReview review = Review("amount\n1\nx\n2\ny\n", new ImportSchema { Fields = [Amount] });

        Assert.Equal(4, review.Summary.RowsRead);
        Assert.Equal(2, review.Summary.RowsFailed);
        Assert.Equal([3, 5], review.Errors.Select(e => e.RowNumber));
        Assert.True(review.ErrorsComplete);
        Assert.False(review.Summary.StoppedEarly);
    }

    [Fact]
    public void ReadsToTheEndUnderAllOrNothing()
    {
        // The policy decides an import; a review that stopped at the first error would show one
        // problem of many and call it the file's.
        ImportReview review = Review("amount\nx\ny\n", new ImportSchema { Fields = [Amount], Policy = ImportPolicy.AllOrNothing });

        Assert.Equal(2, review.Summary.RowsFailed);
        Assert.False(review.Summary.StoppedEarly);
    }

    [Fact]
    public void KeepsTheFirstErrorsAndCountsTheRest()
    {
        ImportReview review = Review("amount\nx\ny\nz\n", new ImportSchema { Fields = [Amount] }, new ReviewOptions { MaxErrors = 2 });

        Assert.Equal(2, review.Errors.Count);
        Assert.False(review.ErrorsComplete);
        Assert.Equal(3, review.Summary.ErrorCount);
    }

    [Fact]
    public void ReportsProgressAndCompletion()
    {
        List<AnalysisProgress> reports = [];
        SynchronousProgress progress = new(reports.Add);

        Review("amount\n1\n2\n3\n", new ImportSchema { Fields = [Amount] }, new ReviewOptions { ProgressInterval = 1, ProgressStep = 0 }, progress);

        Assert.True(reports[^1].IsComplete);
        Assert.Equal(3, reports[^1].RowsRead);
    }

    [Fact]
    public void CarriesTheAlternatives()
    {
        DecimalImportField lat = ImportField.Decimal("lat");
        ImportSchema schema = new()
        {
            Fields = [lat],
            Alternatives = [new FieldAlternatives("location", [AlternativeGroup.AllOf("coordinates", lat)])],
        };
        MappingPlan plan = new() { Bindings = [new ColumnBinding { ColumnIndex = 0, Header = "lat", FieldName = "lat" }] };

        using ITabularCursor cursor = Open("lat\n1\n\n2\n");
        ImportReview review = TabularExtractor.Review(cursor, plan, schema, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, Assert.Single(review.Alternatives).Groups[0].Won);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10_001)]
    public void RefusesAnErrorCapOutsideItsRange(int cap)
    {
        using ITabularCursor cursor = Open("amount\n1\n");

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TabularExtractor.Review(cursor, Plan, new ImportSchema { Fields = [Amount] }, new ReviewOptions { MaxErrors = cap }));
    }

    private static ImportReview Review(string csv, ImportSchema schema, ReviewOptions? options = null, IProgress<AnalysisProgress>? progress = null)
    {
        using ITabularCursor cursor = Open(csv);
        return TabularExtractor.Review(cursor, Plan, schema, options, progress, TestContext.Current.CancellationToken);
    }

    private static ITabularCursor Open(string csv) =>
        TabularFile.Open(new MemoryStream(Utf8NoBom.GetBytes(csv), writable: false), "test.csv");

    private sealed class SynchronousProgress(Action<AnalysisProgress> report) : IProgress<AnalysisProgress>
    {
        public void Report(AnalysisProgress value) => report(value);
    }
}
```

(The blank row in `CarriesTheAlternatives` is skipped by extraction, so two rows are judged. If `AnalysisProgressTests` already has a synchronous `IProgress` helper, reuse it.)

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~ReviewTests"`
Expected: build FAIL — `TabularExtractor.Review`, `ImportReview`, `ReviewOptions` do not exist.

- [ ] **Step 3: Move the progress reporter**

Cut the nested `private sealed class ProgressReporter(...)` out of `AnalysisRun` in `TabularAnalyzer.cs` into `src/TriasDev.Tabular/Analysis/ProgressReporter.cs` as `internal sealed class ProgressReporter(...)` in namespace `TriasDev.Tabular`, unchanged otherwise (keep its XML remarks). `AnalysisRun` keeps calling `new ProgressReporter(progress, cursor, _options.ProgressInterval, _options.ProgressStep)`.

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~AnalysisProgressTests"`
Expected: PASS (pure move).

- [ ] **Step 4: Let a run ignore its error limit**

In `ExtractionRun`, add a constructor parameter `bool ignoreErrorLimit = false` (last) and set:

```csharp
        // A review reads the whole file whatever the policy says: it reports what an import would
        // meet, and stopping at the first error would report one problem of many as the file's.
        _errorLimit = ignoreErrorLimit
            ? int.MaxValue
            : schema.Policy == ImportPolicy.AllOrNothing ? 1 : options.MaxErrorRows;
```

- [ ] **Step 5: Write the review**

`src/TriasDev.Tabular/Extraction/ImportReview.cs`:

```csharp
namespace TriasDev.Tabular;

/// <summary>Knobs for <see cref="TabularExtractor.Review"/>.</summary>
public sealed record ReviewOptions
{
    /// <summary>The defaults.</summary>
    public static ReviewOptions Default { get; } = new();

    /// <summary>
    /// How many row errors the review keeps; every one is counted in the summary.
    /// </summary>
    /// <remarks>At most <see cref="ExtractionOptions.ReportedRowsCeiling"/>, so a file that fails on every row holds no list in proportion to it.</remarks>
    public int MaxErrors { get; init; } = 50;

    /// <summary>How many row numbers each list of an <see cref="AlternativesReport"/> keeps.</summary>
    public int MaxReportedRows { get; init; } = 50;

    /// <summary>Data rows between progress reports, as for analysis.</summary>
    public int ProgressInterval { get; init; } = 10_000;

    /// <summary>The fraction of the file read between progress reports, as for analysis.</summary>
    public double ProgressStep { get; init; } = 0.01;

    internal ReviewOptions Checked()
    {
        OptionChecks.AtLeast(MaxErrors, 1, nameof(ReviewOptions), nameof(MaxErrors));
        OptionChecks.AtMost(MaxErrors, ExtractionOptions.ReportedRowsCeiling, nameof(ReviewOptions), nameof(MaxErrors));
        OptionChecks.AtLeast(ProgressInterval, 1, nameof(ReviewOptions), nameof(ProgressInterval));

        if (ProgressStep is < 0 or > 1 || double.IsNaN(ProgressStep))
        {
            throw new ArgumentOutOfRangeException("options", ProgressStep, "ReviewOptions.ProgressStep must be between 0 and 1.");
        }

        return this;
    }
}

/// <summary>What an import through a mapping would meet, found by reading the whole file once and keeping nothing.</summary>
public sealed record ImportReview
{
    /// <summary>The run's counts, over every row of the file.</summary>
    public required ExtractionSummary Summary { get; init; }

    /// <summary>The first row errors, up to <see cref="ReviewOptions.MaxErrors"/>.</summary>
    public required IReadOnlyList<RowError> Errors { get; init => field = Equatable.List(value); }

    /// <summary>Whether <see cref="Errors"/> holds every error the summary counts.</summary>
    public required bool ErrorsComplete { get; init; }

    /// <summary>What the run found about each set of alternatives.</summary>
    public required IReadOnlyList<AlternativesReport> Alternatives { get; init => field = Equatable.List(value); }
}
```

In `TabularExtractor` add:

```csharp
    /// <summary>
    /// Reads the whole file through a mapping without keeping a value, and reports what an import
    /// would meet: its errors, its counts and how each set of alternatives covers the rows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exact where a precheck can only estimate, because it reads every row as the import will — at
    /// the cost of one more pass over the file. Never stops at an error limit or for
    /// <see cref="ImportPolicy.AllOrNothing"/>: it is the whole file's account.
    /// </para>
    /// </remarks>
    /// <param name="cursor">An open cursor over the file.</param>
    /// <param name="plan">What a user decided.</param>
    /// <param name="schema">What the caller wants filled.</param>
    /// <param name="options">Review options, or null for the defaults; checked here.</param>
    /// <param name="progress">Told as analysis tells it; null to report nothing.</param>
    /// <param name="cancellationToken">Stops the review, including inside a single read.</param>
    /// <exception cref="MappingPlanException">The plan does not fit the schema.</exception>
    /// <exception cref="TabularStructureException">The file is not the one the plan was built against.</exception>
    public static ImportReview Review(
        ITabularCursor cursor,
        MappingPlan plan,
        ImportSchema schema,
        ReviewOptions? options = null,
        IProgress<AnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(schema);

        ReviewOptions effective = (options ?? ReviewOptions.Default).Checked();
        ExtractionOptions extraction = new ExtractionOptions { ValidateOnly = true, MaxReportedRows = effective.MaxReportedRows }.Checked();

        IReadOnlyList<MappingFault> faults = MappingPlanValidator.Validate(plan, schema);

        if (faults.Count > 0)
        {
            throw new MappingPlanException(faults);
        }

        ExtractionRun run = new(cursor, plan, schema, extraction, cancellationToken, ignoreErrorLimit: true);
        ProgressReporter reporter = new(progress, cursor, effective.ProgressInterval, effective.ProgressStep);
        List<RowError> errors = [];

        // Progress counts rows the run hands back; rows it skips as blank are few, and counting them
        // would need a hook into the run's inner loop for no visible difference.
        while (run.ReadRow(cancellationToken))
        {
            reporter.Row(cursor.Sheets[plan.SheetIndex]);

            for (int i = 0; i < run.CurrentErrors.Count && errors.Count < effective.MaxErrors; i++)
            {
                errors.Add(run.CurrentErrors[i]);
            }
        }

        reporter.Complete();
        ExtractionSummary summary = run.Summary;

        return new ImportReview
        {
            Summary = summary,
            Errors = errors,
            ErrorsComplete = errors.Count == summary.ErrorCount,
            Alternatives = run.Alternatives,
        };
    }
```

(If `ProgressReporter.Row` takes a `SheetInfo`, pass the plan's sheet as above; match its real signature.)

- [ ] **Step 6: Record the public API, build, run the tests**

Run: `dotnet build TriasDev.Tabular.slnx` (RS0016: `ImportReview`, `ReviewOptions` and members, `TabularExtractor.Review`), `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~ReviewTests"`, then the whole suite. `NullArgumentTests` may require the new public method to throw on each null argument — it does. If `OptionsValidationTests`/`OptionDefaultsTests` enumerate option records, add `ReviewOptions` there.
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/TriasDev.Tabular tests/TriasDev.Tabular.Tests
git commit -m "feat: review a whole file through a mapping before importing it"
```

---

### Task 6: Mapping-level findings in the precheck

**Files:**
- Modify: `src/TriasDev.Tabular/Import/MappingPrecheck.cs`
- Modify: `src/TriasDev.Tabular/Import/PrecheckArguments.cs`
- Modify: `src/TriasDev.Tabular/Abstractions/ErrorCodes.cs` (`Group.LevelUnmapped`)
- Modify: `docs/error-codes.md` (code row and argument rows)
- Modify: `src/TriasDev.Tabular/PublicAPI.Unshipped.txt`
- Test: `tests/TriasDev.Tabular.Tests/Import/AlternativesPrecheckTests.cs`

**Interfaces:**
- Consumes: `SchemaAlternatives.Resolve` (Task 1), `ErrorCodes.Group.Unresolved` (Task 2).
- Produces: `ErrorCodes.Group.LevelUnmapped = "group.level-unmapped"`; `PrecheckArguments.Alternatives = "alternatives"`, `Group = "group"`, `Level = "level"`, `ReachableLevel = "reachableLevel"`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text;

using Xunit;

namespace TriasDev.Tabular.Tests.Import;

/// <summary>What the mapping alone says about a set of alternatives, before any row is read again.</summary>
public sealed class AlternativesPrecheckTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly DecimalImportField Lat = ImportField.Decimal("lat");
    private static readonly DecimalImportField Lon = ImportField.Decimal("lon");
    private static readonly TextImportField Country = ImportField.Text("country").AllowedValues(["DEU"]);
    private static readonly TextImportField City = ImportField.Text("city");
    private static readonly TextImportField House = ImportField.Text("house");

    private static ImportSchema Schema(bool unresolvedRowFails = false) => new()
    {
        Fields = [Lat, Lon, Country, City, House],
        Alternatives =
        [
            new FieldAlternatives(
                "location",
                [
                    AlternativeGroup.AllOf("coordinates", Lat, Lon),
                    AlternativeGroup.Ladder("address", 1, new AlternativeLevel("country", Country), new AlternativeLevel("locality", City), new AlternativeLevel("house", House)),
                ],
                unresolvedRowFails),
        ],
    };

    private const string Csv = "lat;lon;country;city;house\n48.1;11.5;XX;Munich;5\n";

    [Fact]
    public void WarnsWhereTheMappingEndsALadderEarly()
    {
        PrecheckFinding finding = Assert.Single(Check(Schema(), "lat", "lon", "country", "house").Findings, f => f.Code == "group.level-unmapped");

        Assert.Equal(PrecheckSeverity.Warning, finding.Severity);
        Assert.Equal("address", finding.FieldName);
        Assert.Equal("locality", finding.Arguments["level"]);
        Assert.Equal("1", finding.Arguments["reachableLevel"]);
        Assert.Equal("location", finding.Arguments["alternatives"]);
    }

    [Fact]
    public void WarnsWhenNoGroupCanLocateARow()
    {
        PrecheckResult result = Check(Schema(), "lat", "city");
        PrecheckFinding finding = Assert.Single(result.Findings, f => f.Code == "group.unresolved");

        Assert.Equal(PrecheckSeverity.Warning, finding.Severity);
        Assert.Equal("location", finding.FieldName);
        Assert.True(result.CanImport);
    }

    [Fact]
    public void BlocksWhenNoGroupCanLocateARowAndThatFailsIt()
    {
        PrecheckResult result = Check(Schema(unresolvedRowFails: true), "lat", "city");

        Assert.Equal(PrecheckSeverity.Blocking, Assert.Single(result.Findings, f => f.Code == "group.unresolved").Severity);
        Assert.False(result.CanImport);
    }

    [Fact]
    public void NeverBlocksOnAFieldOfALaterGroup()
    {
        // Every country is wrong — which blocks a plain field — but only rows without coordinates need
        // one, and the profile cannot say which those are.
        PrecheckFinding finding = Assert.Single(Check(Schema(), "lat", "lon", "country", "city", "house").Findings, f => f.Code == "value.not-allowed");

        Assert.Equal(PrecheckSeverity.Warning, finding.Severity);
    }

    private static PrecheckResult Check(ImportSchema schema, params string[] mapped)
    {
        string[] headers = ["lat", "lon", "country", "city", "house"];
        MappingPlan plan = new()
        {
            Bindings =
            [
                .. headers.Select((h, i) => (h, i)).Where(c => mapped.Contains(c.h))
                    .Select(c => new ColumnBinding { ColumnIndex = c.i, Header = c.h, FieldName = c.h }),
            ],
        };

        using ITabularCursor cursor = TabularFile.Open(new MemoryStream(Utf8NoBom.GetBytes(Csv), writable: false), "test.csv");
        return MappingPrecheck.Check(plan, schema, TabularAnalyzer.Analyze(cursor));
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~AlternativesPrecheckTests"`
Expected: FAIL — no `group.level-unmapped`/`group.unresolved` findings; `value.not-allowed` is `Blocking`.

- [ ] **Step 3: Add the code and the arguments**

`ErrorCodes.Group`:

```csharp
        /// <summary>A mapping binds no field to a level of a group, so no row can reach past it.</summary>
        public const string LevelUnmapped = "group.level-unmapped";
```

`PrecheckArguments` (match the file's existing doc style):

```csharp
    /// <summary>The set of alternatives a finding is about.</summary>
    public const string Alternatives = "alternatives";

    /// <summary>The group of a set of alternatives a finding is about.</summary>
    public const string Group = "group";

    /// <summary>The first level of a group the mapping binds no field to.</summary>
    public const string Level = "level";

    /// <summary>How many levels of a group the mapping lets a row reach.</summary>
    public const string ReachableLevel = "reachableLevel";
```

`docs/error-codes.md` — the codes table:

```markdown
| `group.level-unmapped` | A precheck finding: the mapping binds no field to a level of a group, so no row reaches past it |
```

and the "Precheck arguments" table:

```markdown
| `group.level-unmapped` | `alternatives`, `group`, `level`, `reachableLevel` | |
| `group.unresolved` | `alternatives` | |
```

- [ ] **Step 4: Implement the findings**

In `MappingPrecheck.Check`, resolve once after `SchemaFields.ByName(schema)`:

```csharp
        ResolvedAlternatives[] alternatives = SchemaAlternatives.Resolve(schema);

        // Fields of a later group are needed only in rows an earlier group does not locate, which the
        // profile cannot tell apart; so nothing about them blocks here — the review settles it per row.
        HashSet<string> lenient = [.. alternatives.SelectMany(a => a.Groups.Skip(1)).SelectMany(g => g.Members).Select(p => schema.Fields[p].Name)];
```

Pass `lenient.Contains(field.Name)` into `Inspect` as a new last parameter `bool lenient`, and in its local `Add` use:

```csharp
                Severity = lenient && severity == PrecheckSeverity.Blocking ? PrecheckSeverity.Warning : severity,
```

After `CheckRequiredGroups(plan, schema, sheet, findings);` (inside `if (!stale)`), add `CheckAlternatives(plan, schema, alternatives, sheet, findings);`:

```csharp
    /// <summary>What the mapping alone decides about each set of alternatives.</summary>
    /// <remarks>
    /// Both findings are certain, unlike a count of empty cells: a level with no bound field is empty
    /// in every row, whatever the file holds.
    /// </remarks>
    private static void CheckAlternatives(
        MappingPlan plan,
        ImportSchema schema,
        ResolvedAlternatives[] alternatives,
        SheetProfile sheet,
        List<PrecheckFinding> findings)
    {
        HashSet<int> bound = [.. plan.Bindings.Select(b => IndexOf(schema, b.FieldName)).Where(i => i >= 0)];

        foreach (ResolvedAlternatives set in alternatives)
        {
            bool anyUsable = false;

            foreach (ResolvedGroup group in set.Groups)
            {
                int reachable = 0;

                while (reachable < group.Levels.Length && group.Levels[reachable].Any(bound.Contains))
                {
                    reachable++;
                }

                anyUsable |= reachable >= group.RequiredLevels;

                if (reachable < group.Levels.Length)
                {
                    findings.Add(new PrecheckFinding
                    {
                        Code = ErrorCodes.Group.LevelUnmapped,
                        Severity = PrecheckSeverity.Warning,
                        FieldName = group.Name,
                        Arguments = Args(
                            (PrecheckArguments.Alternatives, set.Declared.Name),
                            (PrecheckArguments.Group, group.Name),
                            (PrecheckArguments.Level, group.LevelNames[reachable]),
                            (PrecheckArguments.ReachableLevel, N(reachable))),
                    });
                }
            }

            if (!anyUsable)
            {
                findings.Add(new PrecheckFinding
                {
                    Code = ErrorCodes.Group.Unresolved,
                    Severity = set.Declared.UnresolvedRowFails ? PrecheckSeverity.Blocking : PrecheckSeverity.Warning,
                    FieldName = set.Declared.Name,
                    AffectedRows = sheet.RowCount,
                    Arguments = Args((PrecheckArguments.Alternatives, set.Declared.Name)),
                });
            }
        }
    }

    private static int IndexOf(ImportSchema schema, string name)
    {
        for (int i = 0; i < schema.Fields.Count; i++)
        {
            if (string.Equals(schema.Fields[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
```

(Use the file's existing `Args` and `N` helpers; check `N` takes an `int`.)

- [ ] **Step 5: Record the public API, build, run the tests**

Run: `dotnet build TriasDev.Tabular.slnx`, `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~AlternativesPrecheckTests"`, then the whole suite (`ErrorCodeCatalogTests`, `PrecheckArgumentsTests` must be green).
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/TriasDev.Tabular docs/error-codes.md tests/TriasDev.Tabular.Tests/Import/AlternativesPrecheckTests.cs
git commit -m "feat: precheck what a mapping decides about field alternatives"
```

---

### Task 7: How many rows of a column fall in a set of values

**Files:**
- Modify: `src/TriasDev.Tabular/Analysis/ColumnFacts.cs`
- Modify: `src/TriasDev.Tabular/PublicAPI.Unshipped.txt`
- Test: `tests/TriasDev.Tabular.Tests/Analysis/RowsWithValueInTests.cs`

**Interfaces:**
- Produces: `public int? ColumnFacts.RowsWithValueIn(FieldConstraint.AllowedValues allowed)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text;

using Xunit;

namespace TriasDev.Tabular.Tests.Analysis;

/// <summary>Which column holds country codes, asked before anything is mapped.</summary>
public sealed class RowsWithValueInTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly FieldConstraint.AllowedValues Countries = new(["DEU", "FRA"], ignoreCase: true);

    [Fact]
    public void CountsRowsNotValues()
    {
        ColumnFacts facts = Facts("code\nDEU\nDEU\nfra\nXX\n\n");

        Assert.Equal(3, facts.RowsWithValueIn(Countries));
    }

    [Fact]
    public void SaysNothingWhenTheDistinctValuesAreIncomplete()
    {
        string csv = "code\n" + string.Concat(Enumerable.Range(0, 30).Select(i => $"V{i}\n"));
        ColumnFacts facts = Facts(csv, new AnalysisOptions { RetainedDistinctValues = 10 });

        Assert.Null(facts.RowsWithValueIn(Countries));
    }

    private static ColumnFacts Facts(string csv, AnalysisOptions? options = null)
    {
        using ITabularCursor cursor = TabularFile.Open(new MemoryStream(Utf8NoBom.GetBytes(csv), writable: false), "test.csv");
        return TabularAnalyzer.Analyze(cursor, options).Sheets[0].Columns[0].Facts;
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~RowsWithValueInTests"`
Expected: build FAIL — `RowsWithValueIn` does not exist.

- [ ] **Step 3: Implement**

In `ColumnFacts`:

```csharp
    /// <summary>
    /// How many rows hold a value from the set, or null when the column's distinct values were too
    /// many to keep and the answer would be a guess.
    /// </summary>
    /// <remarks>
    /// Asked before anything is mapped — which column is the country code? — of every column in turn.
    /// Answered from <see cref="DistinctValueCounts"/>, so it costs nothing per row: the counting was
    /// done while analysing.
    /// </remarks>
    public int? RowsWithValueIn(FieldConstraint.AllowedValues allowed)
    {
        ArgumentNullException.ThrowIfNull(allowed);

        if (!DistinctValuesAreComplete)
        {
            return null;
        }

        int rows = 0;

        foreach (ValueFrequency value in DistinctValueCounts)
        {
            if (allowed.Contains(value.Value))
            {
                rows += value.Count;
            }
        }

        return rows;
    }
```

- [ ] **Step 4: Record the public API, build, run the tests**

Run: `dotnet build TriasDev.Tabular.slnx`, the filtered test, the whole suite.
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/TriasDev.Tabular tests/TriasDev.Tabular.Tests/Analysis/RowsWithValueInTests.cs
git commit -m "feat: count a column's rows that fall in a set of values"
```

---

### Task 8: Docs, sample and measurement

**Files:**
- Modify: `docs/importing.md` (new section "Fields that stand in for one another", before "Rules of your own")
- Modify: `samples/TriasDev.Tabular.Samples.Import/Program.cs` (a `// --8<-- [start:alternatives]` region) — or a new file in that project, matching how existing regions are included
- Modify: `docs/bounds.md` (the two new list ceilings)
- Modify: `CHANGELOG.md` only if the repo edits it by hand (release-please generates it — leave it otherwise)

- [ ] **Step 1: Write the sample region**

In the import sample, declare the location schema and run a review, inside include markers:

```csharp
// --8<-- [start:alternatives]
DecimalImportField latitude = ImportField.Decimal("latitude").AtLeast(-90).AtMost(90);
DecimalImportField longitude = ImportField.Decimal("longitude").AtLeast(-180).AtMost(180);
TextImportField country = ImportField.Text("country").AllowedValues(CountryCodes, ignoreCase: true);
TextImportField postalCode = ImportField.Text("postalCode");
TextImportField city = ImportField.Text("city");
TextImportField street = ImportField.Text("street");
TextImportField house = ImportField.Text("house");

ImportSchema schema = new()
{
    Fields = [latitude, longitude, country, postalCode, city, street, house],
    Alternatives =
    [
        new FieldAlternatives(
            "location",
            [
                AlternativeGroup.AllOf("coordinates", latitude, longitude),
                AlternativeGroup.Ladder(
                    "address",
                    requiredLevels: 1,
                    new AlternativeLevel("country", country),
                    new AlternativeLevel("locality", postalCode, city),
                    new AlternativeLevel("street", street),
                    new AlternativeLevel("house", house)),
            ]),
    ],
};

ImportReview review = TabularExtractor.Review(cursor, plan, schema);
AlternativesReport location = review.Alternatives[0];
// location.Groups[0].Incomplete of location.RowsJudged rows lack usable coordinates;
// location.Groups[1].Incomplete of them have an incomplete address; location.Unresolved have neither.
// --8<-- [end:alternatives]
```

Make it compile in the sample's context (a `CountryCodes` array, a `cursor` and `plan` from the sample's existing flow). Run: `dotnet build TriasDev.Tabular.slnx`.

- [ ] **Step 2: Write the docs section**

In `docs/importing.md`, before "## Rules of your own", add "## Fields that stand in for one another": the coordinates/address example; levels and `RequiredLevels`; validation only where needed; the per-row `ImportRow.Resolution`; the report fields and the denominator (`RowsJudged`); `TabularExtractor.Review` and its cost (one more full pass; quote the measured time from Step 4); the two precheck findings; `ColumnFacts.RowsWithValueIn` for suggesting a column. Include the sample with `--8<-- "samples/…/Program.cs:alternatives"` as the file's other snippets do. Add the two ceilings (`ExtractionOptions.MaxReportedRows`, `ReviewOptions.MaxErrors`, both ≤ 10,000) to `docs/bounds.md`'s table.

Run: `pip install -r requirements.txt && mkdocs build --strict`
Expected: builds without warnings.

- [ ] **Step 3: Run the whole suite on both targets**

Run: `dotnet test --solution TriasDev.Tabular.slnx`
Expected: PASS on net8.0 and net10.0.

- [ ] **Step 4: Measure**

On the 5M-row csv (`~/Documents/lri-files`, see `CONTRIBUTING.md` for `TABULAR_FIXTURES`/`TABULAR_FILES`), min of N runs, alternating with `main`:
- the existing import benchmark with a schema **without** alternatives — must be within run-to-run variance of `main` (time and allocations);
- `TabularExtractor.Review` with the location schema — record the time; target ≤ ~20 s.

Put the numbers in the docs section (Step 2) and in the PR description.

- [ ] **Step 5: Commit**

```bash
git add docs samples
git commit -m "docs: fields that stand in for one another, and the review before import"
```
