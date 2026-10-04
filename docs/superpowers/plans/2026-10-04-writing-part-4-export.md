# Writing — part 4: `TabularExport<T>` — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A caller describes an export of objects once — columns as typed lambdas, optionally named by the import's own fields — and writes any source of those objects (chunks from gRPC, an async stream, a list) to csv, xlsx or ods in one call, with memory flat in the row count and the file importing back as the same values.

**Architecture:** `TabularExport.For<T>()` returns a mutable `TabularExportBuilder<T>`; each `Column(...)` overload stores an internal `ExportColumn<T, TValue>` — a typed value delegate plus a static typed write delegate, so a cell costs two delegate calls and no boxing. `Build()` checks the columns with the writer's own rules and returns an immutable, thread-safe `TabularExport<T>`, whose `WriteAsync` (a whole file) and `WriteSheetAsync` (one sheet into a caller's `TabularWriter`) drive the existing `TabularWriter`, flushing after every chunk and whenever the writer recommends it.

**Tech Stack:** .NET 8 + 10, base class library only, xunit v4 on Microsoft.Testing.Platform.

**Spec:** `docs/superpowers/specs/2026-10-03-writing-design.md`, section "Object layer" — delivery step 4 of 5 (issue #73). Parts 1–3 (#77, #81, #83) are merged.

## Global Constraints

- No package references in the library; only the base class library (`TabularIndependenceTests`).
- No allocation per row or per cell on the write path; per chunk, per sheet and per file are fine.
- The target stream is never written, flushed or disposed synchronously (the writer guarantees it; this layer only drives the writer).
- A stream handed over is closed on every path, failures included, unless `TabularWriterOptions.LeaveOpen` is set.
- Programmer errors are `ArgumentException` / `ArgumentNullException` / `InvalidOperationException`; value problems stay the writer's `TabularWriteException` codes.
- Cancellation: an async source is enumerated `WithCancellation(cancellationToken)`; the token is the last parameter of every async method.
- Public API changes go in `src/TriasDev.Tabular/PublicAPI.Unshipped.txt` (RS0016 messages are authoritative).
- `dotnet format TriasDev.Tabular.slnx --verify-no-changes` must pass before every commit.
- Nothing product-specific in code, docs or commits (generic names: `Portfolio`, `Item`, `Batch`).

## Rulings made while planning (deviations from or additions to the spec)

- **No untyped `ImportField` overload.** The spec allowed one, checked at `Build()`. It would also catch a typed field passed with a lambda of the wrong type — `Column(DecimalImportField, p => p.Name)` would compile and fail at run time — which defeats the spec's own rule that a mismatch is a compile error. A caller with an untyped field writes `Column(field.Name, …)`. Cost: one more word at the call site.
- **A translated field's variant is refused at `Column(...)`, not at `Build()`** (`ArgumentException`): the earliest point, with the field in hand. `TranslatedImportField` itself is not an `ImportField` and cannot be passed at all.
- **A chunk-selector overload** (`IAsyncEnumerable<TChunk>` plus `Func<TChunk, IReadOnlyList<T>>`) for both write methods. The driving case is a gRPC server stream of messages each carrying a repeated field (`RepeatedField<T>` is an `IReadOnlyList<T>`); without it, a net8 caller needs `System.Linq.Async` or a hand-written iterator just to call the library.
- **`Build()` validates the columns with the writer's own rules** — the check moves from `TabularWriter.CheckColumns` into an internal `TabularWriter.ColumnsProblem(ReadOnlySpan<WriteColumn>) → Exception?` both use — and throws its exception, so a duplicate header fails where the export is declared, not mid-request.
- **Default widths:** a `DateTime` column 19 characters (`yyyy-mm-dd hh:mm:ss`), a `DateOnly` column 10; other types none (the program's default). An explicit width wins.
- **Cancellation of a synchronous `IEnumerable<T>` source** is observed at each flush (about every megabyte written), not per item.

## Review Focus

1. **A gRPC-shaped source** — messages each holding a list — written without any adapter. → Task 3 `WritesChunksTakenFromMessages`.
2. **A million rows from reused chunks allocate (almost) nothing per row**, in every format. → Task 3 `AllocatesNothingPerRow`.
3. **A value the format refuses mid-stream** (e.g. a 16-digit decimal at row 40,000): `TabularWriteException` with its row, the stream closed, nothing that opens as valid. → Task 2 `AValueTheFormatRefusesClosesTheStreamAndLeavesNoValidFile`.
4. **Cancellation between chunks**: `OperationCanceledException`, stream closed. → Task 2 `CancellationStopsBetweenChunksAndClosesTheStream`.
5. **The same export used concurrently** by two requests: both files correct (the export is immutable). → Task 3 `OneExportServesConcurrentWrites`.

---

### Task 1: Columns — the builder and the export's declaration

**Files:**
- Create: `src/TriasDev.Tabular/Writing/ExportColumn.cs`
- Create: `src/TriasDev.Tabular/Writing/TabularExportBuilder.cs`
- Create: `src/TriasDev.Tabular/Writing/TabularExport.cs` (the static `TabularExport.For<T>()` and the class `TabularExport<T>` with `Columns` only; writing comes in Task 2)
- Modify: `src/TriasDev.Tabular/Writing/TabularWriter.cs` (`CheckColumns` → `ColumnsProblem`)
- Modify: `src/TriasDev.Tabular/PublicAPI.Unshipped.txt`
- Test: `tests/TriasDev.Tabular.Tests/Writing/TabularExportBuilderTests.cs`

**Interfaces:**
- Consumes: `TabularWriter` (`Write(string?/long/decimal/double/DateTime/DateOnly/bool)`, `WriteEmpty()`), `WriteColumn(string Header, double? Width = null)`, the typed import fields `TextImportField`, `IntegerImportField`, `DecimalImportField`, `DateImportField`, `BooleanImportField` (`Name`, `Variant`).
- Produces:
  - `public static class TabularExport { public static TabularExportBuilder<T> For<T>(); }`
  - `public sealed class TabularExportBuilder<T>` with `Column(string header, Func<T, X> value, double? width = null)` for X in `string?`, `long`, `long?`, `decimal`, `decimal?`, `double`, `double?`, `DateTime`, `DateTime?`, `DateOnly`, `DateOnly?`, `bool`, `bool?`; `Column(TextImportField, Func<T, string?>, double? width = null)`, `Column(IntegerImportField, Func<T, long?>, …)`, `Column(DecimalImportField, Func<T, decimal?>, …)`, `Column(DecimalImportField, Func<T, double?>, …)`, `Column(DateImportField, Func<T, DateTime?>, …)`, `Column(DateImportField, Func<T, DateOnly?>, …)`, `Column(BooleanImportField, Func<T, bool?>, …)` — each returns the builder; `TabularExport<T> Build()`.
  - `public sealed class TabularExport<T> { public IReadOnlyList<WriteColumn> Columns { get; } }` (Task 2 adds the write methods).
  - internal `abstract class ExportColumn<T>` (`WriteColumn Column`, `abstract void Write(TabularWriter writer, T item)`), `sealed class ExportColumn<T, TValue>`, `static class CellWriters`.
  - internal `static Exception? TabularWriter.ColumnsProblem(ReadOnlySpan<WriteColumn> columns)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/TriasDev.Tabular.Tests/Writing/TabularExportBuilderTests.cs`:

```csharp
using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>Declaring an export: a column's type from its lambda, its header from a field, the writer's rules at Build.</summary>
public sealed class TabularExportBuilderTests
{
    private sealed record Portfolio(long Id, int Count, string Name, decimal Amount, double Rate, DateTime Start, DateOnly Settled, bool Active, long? Parent);

    private static readonly IntegerImportField IdField = ImportField.Integer("Id");
    private static readonly TextImportField NameField = ImportField.Text("Name");
    private static readonly DecimalImportField AmountField = ImportField.Decimal("Amount");
    private static readonly DecimalImportField RateField = ImportField.Decimal("Rate");
    private static readonly DateImportField StartField = ImportField.Date("Start");
    private static readonly DateImportField SettledField = ImportField.Date("Settled");
    private static readonly BooleanImportField ActiveField = ImportField.Boolean("Active");

    [Fact]
    public void DeclaresColumnsInOrderWithTheirHeaders()
    {
        TabularExport<Portfolio> export = TabularExport.For<Portfolio>()
            .Column("Id", p => p.Id)
            .Column("Count", p => p.Count)
            .Column("Name", p => p.Name)
            .Column("Amount", p => p.Amount)
            .Column("Rate", p => p.Rate)
            .Column("Start", p => p.Start)
            .Column("Settled", p => p.Settled)
            .Column("Active", p => p.Active)
            .Column("Parent", p => p.Parent)
            .Build();

        Assert.Equal(["Id", "Count", "Name", "Amount", "Rate", "Start", "Settled", "Active", "Parent"], export.Columns.Select(c => c.Header));
    }

    [Fact]
    public void TakesItsHeadersFromTheImportsFields()
    {
        TabularExport<Portfolio> export = TabularExport.For<Portfolio>()
            .Column(IdField, p => p.Id)
            .Column(NameField, p => p.Name)
            .Column(AmountField, p => p.Amount)
            .Column(RateField, p => p.Rate)
            .Column(StartField, p => p.Start)
            .Column(SettledField, p => p.Settled)
            .Column(ActiveField, p => p.Active)
            .Build();

        Assert.Equal(["Id", "Name", "Amount", "Rate", "Start", "Settled", "Active"], export.Columns.Select(c => c.Header));
    }

    [Fact]
    public void WidensDateColumnsUnlessToldOtherwise()
    {
        TabularExport<Portfolio> export = TabularExport.For<Portfolio>()
            .Column("Start", p => p.Start)
            .Column("Settled", p => p.Settled)
            .Column("Name", p => p.Name)
            .Column("Narrow", p => p.Start, width: 12)
            .Build();

        Assert.Equal(new double?[] { 19, 10, null, 12 }, export.Columns.Select(c => c.Width));
    }

    [Fact]
    public void RefusesATranslatedFieldsVariant()
    {
        ImportField variant = Assert.Single(ImportField.Translated("Title", ["en"]));

        Assert.Throws<ArgumentException>(() => TabularExport.For<Portfolio>().Column((TextImportField)variant, p => p.Name));
    }

    [Fact]
    public void RefusesAnExportWithoutColumns()
    {
        Assert.Throws<InvalidOperationException>(() => TabularExport.For<Portfolio>().Build());
    }

    [Fact]
    public void RefusesColumnsTheWriterWouldRefuseWhereTheyAreDeclared()
    {
        Assert.ThrowsAny<ArgumentException>(() => TabularExport.For<Portfolio>().Column("Name", p => p.Name).Column(" name", p => p.Name).Build());
        Assert.ThrowsAny<ArgumentException>(() => TabularExport.For<Portfolio>().Column("Name", p => p.Name).Column("NAME", p => p.Name).Build());
        Assert.ThrowsAny<ArgumentException>(() => TabularExport.For<Portfolio>().Column("", p => p.Name).Build());
        Assert.ThrowsAny<ArgumentException>(() => TabularExport.For<Portfolio>().Column("Name", p => p.Name, width: 0).Build());
    }

    [Fact]
    public void RefusesANullHeaderOrLambda()
    {
        Assert.Throws<ArgumentNullException>(() => TabularExport.For<Portfolio>().Column((string)null!, p => p.Name));
        Assert.Throws<ArgumentNullException>(() => TabularExport.For<Portfolio>().Column("Name", (Func<Portfolio, string?>)null!));
        Assert.Throws<ArgumentNullException>(() => TabularExport.For<Portfolio>().Column((TextImportField)null!, p => p.Name));
    }

    [Fact]
    public void BuildsAnExportTheBuilderNoLongerChanges()
    {
        TabularExportBuilder<Portfolio> builder = TabularExport.For<Portfolio>().Column("Id", p => p.Id);
        TabularExport<Portfolio> export = builder.Build();

        builder.Column("Name", p => p.Name);

        Assert.Single(export.Columns);
    }
}
```

(`ImportField.Translated(...)` returns a `TranslatedImportField`, which enumerates its variant fields; check its element type in `src/TriasDev.Tabular/Import/TranslatedImportField.cs` and adapt the cast in `RefusesATranslatedFieldsVariant` if the variants are another typed field — the point is a field whose `Variant` is set.)

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~TabularExportBuilderTests"`
Expected: build FAIL — `TabularExport` does not exist.

- [ ] **Step 3: Move the column check into a reusable method**

In `src/TriasDev.Tabular/Writing/TabularWriter.cs`, replace `CheckColumns` with:

```csharp
    /// <summary>
    /// What is wrong with a sheet's columns, as the exception to throw, or null: 1 to 16,384 columns,
    /// headers not empty, not padded, unique ignoring case and writable, widths more than 0 and at
    /// most 255 characters.
    /// </summary>
    /// <remarks>Shared with <see cref="TabularExportBuilder{T}.Build"/>, so an export's columns fail where they are declared.</remarks>
    internal static Exception? ColumnsProblem(ReadOnlySpan<WriteColumn> columns)
    {
        if (columns.IsEmpty || columns.Length > MaxColumns)
        {
            return new ArgumentOutOfRangeException(nameof(columns), columns.Length, $"A sheet has 1 to {MaxColumns} columns.");
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (WriteColumn column in columns)
        {
            if (HeaderProblem(column.Header, seen) is { } problem)
            {
                return new ArgumentException(problem, nameof(columns));
            }

            if (column.Width is { } width && (!double.IsFinite(width) || width <= 0 || width > MaxWidth))
            {
                return new ArgumentOutOfRangeException(nameof(columns), width, $"A column is more than 0 and at most {MaxWidth} characters wide.");
            }
        }

        return null;
    }

    private void CheckColumns(ReadOnlySpan<WriteColumn> columns)
    {
        if (ColumnsProblem(columns) is { } problem)
        {
            throw Faulting(problem);
        }
    }
```

(`Faulting<T>` takes `T : Exception`; `throw Faulting(problem)` with `problem` typed `Exception` compiles.)

- [ ] **Step 4: Implement the columns**

Create `src/TriasDev.Tabular/Writing/ExportColumn.cs`:

```csharp
namespace TriasDev.Tabular;

/// <summary>One column of an export: its header and width, and how to write an item's value.</summary>
internal abstract class ExportColumn<T>(WriteColumn column)
{
    public WriteColumn Column { get; } = column;

    /// <summary>Writes the item's value for this column as the writer's next cell.</summary>
    public abstract void Write(TabularWriter writer, T item);
}

/// <summary>
/// A column of a known type: the caller's lambda for the value, and a static delegate that writes a
/// value of that type — two delegate calls per cell, no boxing.
/// </summary>
internal sealed class ExportColumn<T, TValue>(WriteColumn column, Func<T, TValue> value, Action<TabularWriter, TValue> write)
    : ExportColumn<T>(column)
{
    public override void Write(TabularWriter writer, T item) => write(writer, value(item));
}

/// <summary>The typed writes, one per value type a column can have; a missing value is an empty cell.</summary>
internal static class CellWriters
{
    public static readonly Action<TabularWriter, string?> Text = static (writer, value) => writer.Write(value);

    public static readonly Action<TabularWriter, long> Long = static (writer, value) => writer.Write(value);

    public static readonly Action<TabularWriter, long?> NullableLong = static (writer, value) =>
    {
        if (value is { } present)
        {
            writer.Write(present);
        }
        else
        {
            writer.WriteEmpty();
        }
    };

    public static readonly Action<TabularWriter, decimal> Decimal = static (writer, value) => writer.Write(value);

    public static readonly Action<TabularWriter, decimal?> NullableDecimal = static (writer, value) =>
    {
        if (value is { } present)
        {
            writer.Write(present);
        }
        else
        {
            writer.WriteEmpty();
        }
    };

    public static readonly Action<TabularWriter, double> Double = static (writer, value) => writer.Write(value);

    public static readonly Action<TabularWriter, double?> NullableDouble = static (writer, value) =>
    {
        if (value is { } present)
        {
            writer.Write(present);
        }
        else
        {
            writer.WriteEmpty();
        }
    };

    public static readonly Action<TabularWriter, DateTime> DateTime = static (writer, value) => writer.Write(value);

    public static readonly Action<TabularWriter, DateTime?> NullableDateTime = static (writer, value) =>
    {
        if (value is { } present)
        {
            writer.Write(present);
        }
        else
        {
            writer.WriteEmpty();
        }
    };

    public static readonly Action<TabularWriter, DateOnly> DateOnly = static (writer, value) => writer.Write(value);

    public static readonly Action<TabularWriter, DateOnly?> NullableDateOnly = static (writer, value) =>
    {
        if (value is { } present)
        {
            writer.Write(present);
        }
        else
        {
            writer.WriteEmpty();
        }
    };

    public static readonly Action<TabularWriter, bool> Boolean = static (writer, value) => writer.Write(value);

    public static readonly Action<TabularWriter, bool?> NullableBoolean = static (writer, value) =>
    {
        if (value is { } present)
        {
            writer.Write(present);
        }
        else
        {
            writer.WriteEmpty();
        }
    };
}
```

If the analyzer flags the six identical nullable bodies as duplication (S4144), replace them with one generic helper `private static void WriteOrEmpty<TValue>(TabularWriter writer, TValue? value, Action<TabularWriter, TValue> write) where TValue : struct` and define each nullable delegate as `static (writer, value) => WriteOrEmpty(writer, value, Long)` and so on — same behaviour, no per-cell allocation (the delegates are static fields).

Create `src/TriasDev.Tabular/Writing/TabularExportBuilder.cs`:

```csharp
namespace TriasDev.Tabular;

/// <summary>
/// Declares an export's columns, in order: a header and a lambda each, the column's type taken from
/// the lambda's.
/// </summary>
/// <remarks>
/// <para>
/// Overload resolution picks the column type: an <see cref="int"/> property resolves to
/// <see cref="long"/>, <c>int?</c> to <c>long?</c>, <see cref="float"/> to <see cref="double"/>.
/// Convert explicitly where it does not: a <see cref="char"/> would resolve to <see cref="long"/> and
/// write its code point; a <see cref="ulong"/> is ambiguous; a method group returning
/// <see cref="int"/> does not convert; <c>p =&gt; null</c> is ambiguous; an <see cref="int"/> passed
/// with a <see cref="DecimalImportField"/> is ambiguous between decimal and double. Write
/// enumerations, identifiers and other types as text: <c>p =&gt; p.Status.ToString()</c>.
/// </para>
/// <para>
/// A column named by an import field takes the field's name as its header, so a file written with
/// the export maps back onto the import's schema by header; the lambda's type must suit the field's,
/// or the call does not compile.
/// </para>
/// </remarks>
public sealed class TabularExportBuilder<T>
{
    /// <summary>The width a date-time column gets unless told otherwise: <c>yyyy-mm-dd hh:mm:ss</c>.</summary>
    private const double DateTimeWidth = 19;

    /// <summary>The width a date column gets unless told otherwise: <c>yyyy-mm-dd</c>.</summary>
    private const double DateWidth = 10;

    private readonly List<ExportColumn<T>> _columns = [];

    internal TabularExportBuilder()
    {
    }

    /// <summary>A text column.</summary>
    public TabularExportBuilder<T> Column(string header, Func<T, string?> value, double? width = null) => Add(header, width, value, CellWriters.Text);

    /// <summary>An integer column.</summary>
    public TabularExportBuilder<T> Column(string header, Func<T, long> value, double? width = null) => Add(header, width, value, CellWriters.Long);

    /// <summary>An integer column; null writes an empty cell.</summary>
    public TabularExportBuilder<T> Column(string header, Func<T, long?> value, double? width = null) => Add(header, width, value, CellWriters.NullableLong);

    /// <summary>A decimal column.</summary>
    public TabularExportBuilder<T> Column(string header, Func<T, decimal> value, double? width = null) => Add(header, width, value, CellWriters.Decimal);

    /// <summary>A decimal column; null writes an empty cell.</summary>
    public TabularExportBuilder<T> Column(string header, Func<T, decimal?> value, double? width = null) => Add(header, width, value, CellWriters.NullableDecimal);

    /// <summary>A number column.</summary>
    public TabularExportBuilder<T> Column(string header, Func<T, double> value, double? width = null) => Add(header, width, value, CellWriters.Double);

    /// <summary>A number column; null writes an empty cell.</summary>
    public TabularExportBuilder<T> Column(string header, Func<T, double?> value, double? width = null) => Add(header, width, value, CellWriters.NullableDouble);

    /// <summary>A date-time column, 19 characters wide unless told otherwise.</summary>
    public TabularExportBuilder<T> Column(string header, Func<T, DateTime> value, double? width = null) => Add(header, width ?? DateTimeWidth, value, CellWriters.DateTime);

    /// <summary>A date-time column, 19 characters wide unless told otherwise; null writes an empty cell.</summary>
    public TabularExportBuilder<T> Column(string header, Func<T, DateTime?> value, double? width = null) => Add(header, width ?? DateTimeWidth, value, CellWriters.NullableDateTime);

    /// <summary>A date column, 10 characters wide unless told otherwise.</summary>
    public TabularExportBuilder<T> Column(string header, Func<T, DateOnly> value, double? width = null) => Add(header, width ?? DateWidth, value, CellWriters.DateOnly);

    /// <summary>A date column, 10 characters wide unless told otherwise; null writes an empty cell.</summary>
    public TabularExportBuilder<T> Column(string header, Func<T, DateOnly?> value, double? width = null) => Add(header, width ?? DateWidth, value, CellWriters.NullableDateOnly);

    /// <summary>A boolean column.</summary>
    public TabularExportBuilder<T> Column(string header, Func<T, bool> value, double? width = null) => Add(header, width, value, CellWriters.Boolean);

    /// <summary>A boolean column; null writes an empty cell.</summary>
    public TabularExportBuilder<T> Column(string header, Func<T, bool?> value, double? width = null) => Add(header, width, value, CellWriters.NullableBoolean);

    /// <summary>A text column named by an import field.</summary>
    public TabularExportBuilder<T> Column(TextImportField field, Func<T, string?> value, double? width = null) => Column(HeaderOf(field), value, width);

    /// <summary>An integer column named by an import field; null writes an empty cell.</summary>
    public TabularExportBuilder<T> Column(IntegerImportField field, Func<T, long?> value, double? width = null) => Column(HeaderOf(field), value, width);

    /// <summary>A decimal column named by an import field; null writes an empty cell.</summary>
    public TabularExportBuilder<T> Column(DecimalImportField field, Func<T, decimal?> value, double? width = null) => Column(HeaderOf(field), value, width);

    /// <summary>A number column named by a decimal import field; null writes an empty cell.</summary>
    public TabularExportBuilder<T> Column(DecimalImportField field, Func<T, double?> value, double? width = null) => Column(HeaderOf(field), value, width);

    /// <summary>A date-time column named by an import field; null writes an empty cell.</summary>
    public TabularExportBuilder<T> Column(DateImportField field, Func<T, DateTime?> value, double? width = null) => Column(HeaderOf(field), value, width);

    /// <summary>A date column named by an import field; null writes an empty cell.</summary>
    public TabularExportBuilder<T> Column(DateImportField field, Func<T, DateOnly?> value, double? width = null) => Column(HeaderOf(field), value, width);

    /// <summary>A boolean column named by an import field; null writes an empty cell.</summary>
    public TabularExportBuilder<T> Column(BooleanImportField field, Func<T, bool?> value, double? width = null) => Column(HeaderOf(field), value, width);

    /// <summary>
    /// The export as declared so far, checked by the writer's rules for columns. The builder may go on
    /// being changed; the export it returned does not change with it.
    /// </summary>
    /// <exception cref="InvalidOperationException">No column was declared.</exception>
    /// <exception cref="ArgumentException">A header is empty, padded, repeated ignoring case or not writable, or a width is out of range.</exception>
    public TabularExport<T> Build()
    {
        if (_columns.Count == 0)
        {
            throw new InvalidOperationException("An export has at least one column.");
        }

        ExportColumn<T>[] columns = [.. _columns];
        WriteColumn[] declared = [.. columns.Select(column => column.Column)];

        if (TabularWriter.ColumnsProblem(declared) is { } problem)
        {
            throw problem;
        }

        return new TabularExport<T>(columns, declared);
    }

    private static string HeaderOf(ImportField field)
    {
        ArgumentNullException.ThrowIfNull(field);

        if (field.Variant is not null)
        {
            throw new ArgumentException(
                $"\"{field.Name}\" is one language of a translated field; name the column with a header of its own.",
                nameof(field));
        }

        return field.Name;
    }

    private TabularExportBuilder<T> Add<TValue>(string header, double? width, Func<T, TValue> value, Action<TabularWriter, TValue> write)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(value);
        _columns.Add(new ExportColumn<T, TValue>(new WriteColumn(header, width), value, write));
        return this;
    }
}
```

Create `src/TriasDev.Tabular/Writing/TabularExport.cs`:

```csharp
namespace TriasDev.Tabular;

/// <summary>Where an export of objects is declared: <c>TabularExport.For&lt;Portfolio&gt;().Column(…).Build()</c>.</summary>
public static class TabularExport
{
    /// <summary>Begins declaring an export of <typeparamref name="T"/>.</summary>
    public static TabularExportBuilder<T> For<T>() => new();
}

/// <summary>
/// An export of objects to a table: built once, kept in a static field, used by any number of
/// writes at once.
/// </summary>
/// <remarks>Immutable and thread-safe: it holds the declared columns and nothing a write changes.</remarks>
public sealed class TabularExport<T>
{
    private readonly ExportColumn<T>[] _columns;
    private readonly WriteColumn[] _declared;

    internal TabularExport(ExportColumn<T>[] columns, WriteColumn[] declared)
    {
        _columns = columns;
        _declared = declared;
    }

    /// <summary>The columns, in order, as a sheet's header row writes them.</summary>
    public IReadOnlyList<WriteColumn> Columns => _declared;
}
```

`_columns` is first read by the write methods of Task 2. So the field is not unused in this commit (an analyzer error with warnings as errors), add Task 2's private `WriteRow` here already:

```csharp
    private void WriteRow(TabularWriter writer, T item)
    {
        writer.BeginRow();

        foreach (ExportColumn<T> column in _columns)
        {
            column.Write(writer, item);
        }

        writer.EndRow();
    }
```

If the analyzer then flags `WriteRow` itself as an unused private method, keep it and add the justified suppression `[SuppressMessage("CodeQuality", "IDE0051", Justification = "Used by the write methods added next.")]`, which Task 2 removes.

- [ ] **Step 5: Record the public API, run the tests**

Append the RS0016 lines for `TabularExport`, `TabularExportBuilder<T>` and `TabularExport<T>` to `PublicAPI.Unshipped.txt`.

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~TabularExportBuilderTests|FullyQualifiedName~TabularWriterTests"`
Expected: PASS — the new tests and the writer's existing column tests (`RefusesColumnsThatCouldNotBeReadBack`) unchanged.

- [ ] **Step 6: Commit**

```bash
dotnet format TriasDev.Tabular.slnx --verify-no-changes
git add src/TriasDev.Tabular tests/TriasDev.Tabular.Tests
git commit -m "feat(write): declare an export of objects — typed columns, headers from the import's fields"
```

---

### Task 2: Writing an export — a sheet, a file, every kind of source

**Files:**
- Modify: `src/TriasDev.Tabular/Writing/TabularExport.cs` (the write methods; `WriteRow` already exists from Task 1 — do not add it twice, and drop any IDE0051 suppression Task 1 put on it)
- Modify: `tests/TriasDev.Tabular.Tests/Fixtures/WriteTarget.cs` (count asynchronous writes)
- Modify: `src/TriasDev.Tabular/PublicAPI.Unshipped.txt`
- Test: `tests/TriasDev.Tabular.Tests/Writing/TabularExportTests.cs`

**Interfaces:**
- Consumes: Task 1's `TabularExport<T>` (`_columns`, `_declared`), `TabularWriter` (`Create`, `BeginSheet`, `BeginRow`, `EndRow`, `FlushRecommended`, `FlushAsync`, `CompleteAsync`, `DisposeAsync`).
- Produces, on `TabularExport<T>`:
  - `ValueTask<long> WriteSheetAsync(TabularWriter writer, string sheetName, IAsyncEnumerable<IReadOnlyList<T>> chunks, CancellationToken cancellationToken = default)`
  - `ValueTask<long> WriteSheetAsync<TChunk>(TabularWriter writer, string sheetName, IAsyncEnumerable<TChunk> chunks, Func<TChunk, IReadOnlyList<T>> rows, CancellationToken cancellationToken = default)`
  - `ValueTask<long> WriteSheetAsync(TabularWriter writer, string sheetName, IAsyncEnumerable<T> items, CancellationToken cancellationToken = default)`
  - `ValueTask<long> WriteSheetAsync(TabularWriter writer, string sheetName, IEnumerable<T> items, CancellationToken cancellationToken = default)`
  - the same four as `WriteAsync(Stream stream, TabularFormat format, string sheetName, <source>, TabularWriterOptions? options = null, CancellationToken cancellationToken = default)` (the selector overload: `…, IAsyncEnumerable<TChunk> chunks, Func<TChunk, IReadOnlyList<T>> rows, TabularWriterOptions? options = null, …`) — each creates the writer, writes one sheet, completes the file, and returns the number of data rows.
  - `WriteTarget.AsyncWrites` (test fixture): how many times `WriteAsync` was called.

- [ ] **Step 1: Count the target's writes**

In `tests/TriasDev.Tabular.Tests/Fixtures/WriteTarget.cs`, add `public int AsyncWrites { get; private set; }` with a doc comment ("How many times an asynchronous write reached the target."), and increment it at the top of `WriteAsync(ReadOnlyMemory<byte>, CancellationToken)`, after the cancellation check.

- [ ] **Step 2: Write the failing tests**

Create `tests/TriasDev.Tabular.Tests/Writing/TabularExportTests.cs`:

```csharp
using System.Runtime.CompilerServices;
using System.Text;

using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>Writing an export: every kind of source, one sheet or a whole file, flushed as it goes.</summary>
public sealed class TabularExportTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed record Item(long Id, string Name, decimal Amount);

    private static readonly TabularExport<Item> Export = TabularExport.For<Item>()
        .Column("Id", i => i.Id)
        .Column("Name", i => i.Name)
        .Column("Amount", i => i.Amount)
        .Build();

    private static readonly TabularWriterOptions NoBom = new() { Csv = new CsvWriterOptions { ByteOrderMark = false } };

    private static Item[] Items(int count) => [.. Enumerable.Range(1, count).Select(i => new Item(i, $"item {i}", i / 4m))];

    private static async IAsyncEnumerable<IReadOnlyList<Item>> Chunks(Item[] items, int size, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (int at = 0; at < items.Length; at += size)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return items[at..Math.Min(items.Length, at + size)];
        }
    }

    private static async IAsyncEnumerable<Item> OneByOne(Item[] items)
    {
        foreach (Item item in items)
        {
            await Task.Yield();
            yield return item;
        }
    }

    private static string Csv(WriteTarget target) => Encoding.UTF8.GetString(target.ToArray());

    [Fact]
    public async Task WritesAHeaderAndARowPerItem()
    {
        WriteTarget target = new();

        long rows = await Export.WriteAsync(target, TabularFormat.Csv, "data", Items(2), NoBom, Token);

        Assert.Equal(2, rows);
        Assert.Equal("Id,Name,Amount\r\n1,item 1,0.25\r\n2,item 2,0.5\r\n", Csv(target));
        Assert.True(target.IsDisposed);
        Assert.False(target.DisposedSynchronously);
    }

    [Fact]
    public async Task EveryKindOfSourceWritesTheSameFile()
    {
        Item[] items = Items(5_000);

        WriteTarget chunked = new();
        WriteTarget streamed = new();
        WriteTarget listed = new();
        WriteTarget selected = new();

        Assert.Equal(5_000, await Export.WriteAsync(chunked, TabularFormat.Csv, "data", Chunks(items, 700), NoBom, Token));
        Assert.Equal(5_000, await Export.WriteAsync(streamed, TabularFormat.Csv, "data", OneByOne(items), NoBom, Token));
        Assert.Equal(5_000, await Export.WriteAsync(listed, TabularFormat.Csv, "data", items, NoBom, Token));
        Assert.Equal(5_000, await Export.WriteAsync(selected, TabularFormat.Csv, "data", Chunks(items, 700), chunk => chunk, NoBom, Token));

        string expected = Csv(listed);
        Assert.Equal(expected, Csv(chunked));
        Assert.Equal(expected, Csv(streamed));
        Assert.Equal(expected, Csv(selected));
    }

    [Fact]
    public async Task FlushesAfterEveryChunk()
    {
        WriteTarget target = new();

        await Export.WriteAsync(target, TabularFormat.Csv, "data", Chunks(Items(1_000), 100), NoBom, Token);

        // Ten chunks, each flushed as it is written, and the file's completion.
        Assert.True(target.AsyncWrites >= 10, $"{target.AsyncWrites} writes reached the target");
    }

    [Fact]
    public async Task FlushesInsideALargeChunkWhenTheWriterRecommendsIt()
    {
        WriteTarget target = new();
        Item[] items = [.. Enumerable.Range(1, 60_000).Select(i => new Item(i, new string('x', 40), i))];

        await Export.WriteAsync(target, TabularFormat.Csv, "data", Chunks(items, items.Length), NoBom, Token);

        Assert.True(target.AsyncWrites >= 3, $"{target.AsyncWrites} writes reached the target for one 3 MB chunk");
    }

    [Fact]
    public async Task WritesSeveralSheetsIntoOneWorkbook()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Xlsx))
        {
            Assert.Equal(3, await Export.WriteSheetAsync(writer, "First", Items(3), Token));
            Assert.Equal(2, await Export.WriteSheetAsync(writer, "Second", Chunks(Items(2), 1), Token));
            await writer.CompleteAsync(Token);
        }

        using ITabularCursor cursor = TabularFile.Open(new MemoryStream(target.ToArray(), writable: false), "t.xlsx", cancellationToken: Token);
        Assert.Equal(["First", "Second"], cursor.Sheets.Select(s => s.Name));
    }

    [Fact]
    public async Task AValueTheFormatRefusesClosesTheStreamAndLeavesNoValidFile()
    {
        TabularExport<Item> export = TabularExport.For<Item>().Column("Id", i => i.Id).Column("Amount", i => i.Amount).Build();
        Item[] items = [.. Items(50_000)];
        items[39_999] = items[39_999] with { Amount = 1_234_567_890_123.456m };
        WriteTarget target = new();

        TabularWriteException refused = await Assert.ThrowsAsync<TabularWriteException>(async () =>
            await export.WriteAsync(target, TabularFormat.Xlsx, "data", Chunks(items, 10_000), cancellationToken: Token));

        Assert.Equal(ErrorCodes.Write.PrecisionLoss, refused.Code);
        Assert.Equal(40_001, refused.RowNumber);
        Assert.Equal("Amount", refused.Header);
        Assert.True(target.IsDisposed);
        Assert.False(target.DisposedSynchronously);
        Assert.ThrowsAny<TabularException>(() => new TriasDev.Tabular.Xlsx.XlsxCursor(new MemoryStream(target.ToArray(), writable: false), cancellationToken: Token));
    }

    [Fact]
    public async Task CancellationStopsBetweenChunksAndClosesTheStream()
    {
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        WriteTarget target = new();
        int chunksSeen = 0;

        async IAsyncEnumerable<IReadOnlyList<Item>> Source([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (IReadOnlyList<Item> chunk in Chunks(Items(1_000), 100, cancellationToken))
            {
                if (++chunksSeen == 3)
                {
                    await cancel.CancelAsync();
                }

                yield return chunk;
            }
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Export.WriteAsync(target, TabularFormat.Csv, "data", Source(), NoBom, cancel.Token));

        Assert.True(chunksSeen < 10);
        Assert.True(target.IsDisposed);
    }

    [Fact]
    public async Task LeavesTheStreamOpenWhenAsked()
    {
        WriteTarget target = new();

        await Export.WriteAsync(target, TabularFormat.Csv, "data", Items(1), new TabularWriterOptions { LeaveOpen = true }, Token);

        Assert.False(target.IsDisposed);
    }

    [Fact]
    public async Task RefusesANullSourceAndStillClosesTheStream()
    {
        WriteTarget target = new();

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await Export.WriteAsync(target, TabularFormat.Csv, "data", (IEnumerable<Item>)null!, cancellationToken: Token));

        Assert.True(target.IsDisposed);
    }

    [Fact]
    public async Task RefusesANullChunk()
    {
        static async IAsyncEnumerable<IReadOnlyList<Item>> WithANullChunk()
        {
            await Task.Yield();
            yield return null!;
        }

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Export.WriteAsync(new WriteTarget(), TabularFormat.Csv, "data", WithANullChunk(), cancellationToken: Token));
    }
}
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~TabularExportTests"`
Expected: build FAIL — no `WriteAsync`.

- [ ] **Step 4: Implement**

In `src/TriasDev.Tabular/Writing/TabularExport.cs`, add `using System.Runtime.CompilerServices;` only if needed, and to `TabularExport<T>`:

```csharp
    /// <summary>Writes a file of one sheet from chunks as they arrive — the fast source for millions of rows.</summary>
    /// <returns>The number of data rows written.</returns>
    /// <remarks>
    /// The writer is flushed after every chunk and inside a chunk whenever it recommends it, so memory
    /// holds a chunk and about a megabyte, however many rows the file has. On any failure the stream is
    /// closed (unless <see cref="TabularWriterOptions.LeaveOpen"/>) and the file is incomplete.
    /// </remarks>
    public ValueTask<long> WriteAsync(Stream stream, TabularFormat format, string sheetName, IAsyncEnumerable<IReadOnlyList<T>> chunks, TabularWriterOptions? options = null, CancellationToken cancellationToken = default) =>
        WriteFileAsync(stream, format, options, writer => WriteSheetAsync(writer, sheetName, chunks, cancellationToken), cancellationToken);

    /// <summary>
    /// Writes a file of one sheet from a stream of messages that each carry a chunk — a gRPC server
    /// stream whose messages hold a repeated field, passed as it comes.
    /// </summary>
    /// <returns>The number of data rows written.</returns>
    public ValueTask<long> WriteAsync<TChunk>(Stream stream, TabularFormat format, string sheetName, IAsyncEnumerable<TChunk> chunks, Func<TChunk, IReadOnlyList<T>> rows, TabularWriterOptions? options = null, CancellationToken cancellationToken = default) =>
        WriteFileAsync(stream, format, options, writer => WriteSheetAsync(writer, sheetName, chunks, rows, cancellationToken), cancellationToken);

    /// <summary>Writes a file of one sheet from items arriving one at a time; awaits per item, so prefer chunks for millions.</summary>
    /// <returns>The number of data rows written.</returns>
    public ValueTask<long> WriteAsync(Stream stream, TabularFormat format, string sheetName, IAsyncEnumerable<T> items, TabularWriterOptions? options = null, CancellationToken cancellationToken = default) =>
        WriteFileAsync(stream, format, options, writer => WriteSheetAsync(writer, sheetName, items, cancellationToken), cancellationToken);

    /// <summary>Writes a file of one sheet from items in memory or produced synchronously.</summary>
    /// <returns>The number of data rows written.</returns>
    /// <remarks>Cancellation is observed at each flush, about every megabyte written.</remarks>
    public ValueTask<long> WriteAsync(Stream stream, TabularFormat format, string sheetName, IEnumerable<T> items, TabularWriterOptions? options = null, CancellationToken cancellationToken = default) =>
        WriteFileAsync(stream, format, options, writer => WriteSheetAsync(writer, sheetName, items, cancellationToken), cancellationToken);

    /// <summary>Writes one sheet into a writer the caller owns, from chunks as they arrive.</summary>
    /// <returns>The number of data rows written.</returns>
    /// <remarks>The caller completes the writer; several exports may write their sheets into one workbook.</remarks>
    public async ValueTask<long> WriteSheetAsync(TabularWriter writer, string sheetName, IAsyncEnumerable<IReadOnlyList<T>> chunks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(chunks);
        writer.BeginSheet(sheetName, _declared);
        long rows = 0;

        await foreach (IReadOnlyList<T> chunk in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            rows += await WriteChunkAsync(writer, chunk, cancellationToken).ConfigureAwait(false);
        }

        return rows;
    }

    /// <summary>Writes one sheet into a writer the caller owns, from messages that each carry a chunk.</summary>
    /// <returns>The number of data rows written.</returns>
    public async ValueTask<long> WriteSheetAsync<TChunk>(TabularWriter writer, string sheetName, IAsyncEnumerable<TChunk> chunks, Func<TChunk, IReadOnlyList<T>> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(rows);
        writer.BeginSheet(sheetName, _declared);
        long written = 0;

        await foreach (TChunk message in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            written += await WriteChunkAsync(writer, rows(message), cancellationToken).ConfigureAwait(false);
        }

        return written;
    }

    /// <summary>Writes one sheet into a writer the caller owns, from items arriving one at a time.</summary>
    /// <returns>The number of data rows written.</returns>
    public async ValueTask<long> WriteSheetAsync(TabularWriter writer, string sheetName, IAsyncEnumerable<T> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(items);
        writer.BeginSheet(sheetName, _declared);
        long rows = 0;

        await foreach (T item in items.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            WriteRow(writer, item);
            rows++;

            if (writer.FlushRecommended)
            {
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return rows;
    }

    /// <summary>Writes one sheet into a writer the caller owns, from items in memory or produced synchronously.</summary>
    /// <returns>The number of data rows written.</returns>
    public async ValueTask<long> WriteSheetAsync(TabularWriter writer, string sheetName, IEnumerable<T> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(items);
        writer.BeginSheet(sheetName, _declared);
        long rows = 0;

        foreach (T item in items)
        {
            WriteRow(writer, item);
            rows++;

            if (writer.FlushRecommended)
            {
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return rows;
    }

    /// <summary>
    /// Creates the writer, lets the sheet be written, completes the file. Arguments are checked inside
    /// the writer's lifetime, so a refused one still closes the stream.
    /// </summary>
    private static async ValueTask<long> WriteFileAsync(Stream stream, TabularFormat format, TabularWriterOptions? options, Func<TabularWriter, ValueTask<long>> write, CancellationToken cancellationToken)
    {
        TabularWriter writer = TabularWriter.Create(stream, format, options);

        await using (writer.ConfigureAwait(false))
        {
            long rows = await write(writer).ConfigureAwait(false);
            await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
            return rows;
        }
    }

    /// <summary>Writes a chunk's rows, flushing inside it when recommended and once after it.</summary>
    private async ValueTask<long> WriteChunkAsync(TabularWriter writer, IReadOnlyList<T> chunk, CancellationToken cancellationToken)
    {
        if (chunk is null)
        {
            throw new ArgumentException("A chunk is null.", nameof(chunk));
        }

        for (int i = 0; i < chunk.Count; i++)
        {
            WriteRow(writer, chunk[i]);

            if (writer.FlushRecommended)
            {
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        return chunk.Count;
    }

    private void WriteRow(TabularWriter writer, T item)
    {
        writer.BeginRow();

        foreach (ExportColumn<T> column in _columns)
        {
            column.Write(writer, item);
        }

        writer.EndRow();
    }
```

If the analyzer asks for the `ArgumentException` parameter name to match a parameter (S3928 / CA2208), use the same justified pragma the options classes use, or throw `InvalidOperationException("A chunk is null.")` — and update `RefusesANullChunk` to `ThrowsAnyAsync<Exception>`'s narrower match accordingly (state which in the report).

- [ ] **Step 5: Record the public API, run the tests**

Append the RS0016 lines for the eight write methods.

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~TabularExport"`
Expected: PASS on both targets.

- [ ] **Step 6: Commit**

```bash
dotnet format TriasDev.Tabular.slnx --verify-no-changes
git add src/TriasDev.Tabular tests/TriasDev.Tabular.Tests
git commit -m "feat(write): write an export from chunks, a message stream, an async or a plain sequence — flushed as it goes"
```

---

### Task 3: The export round-trips through the import — every format, flat memory, concurrent use

**Files:**
- Test: `tests/TriasDev.Tabular.Tests/Writing/TabularExportRoundTripTests.cs`
- Modify: `docs/KNOWN-ISSUES.md` (one line: the export's overload pitfalls)

**Interfaces:**
- Consumes: Tasks 1–2; `TabularImporter.Import<T>(Stream, string, MappingPlan, ImportSchema, TabularRowMapper<T>, ImportOptions?, CancellationToken)`, `ImportRun<T>.ReadRows`, typed `ImportRow` indexers.
- Produces: the proof that an export declared with the import's own fields writes files the import maps back by header, in csv, xlsx and ods; that writing allocates nothing per row; that one export serves concurrent writes.

- [ ] **Step 1: Write the tests**

Create `tests/TriasDev.Tabular.Tests/Writing/TabularExportRoundTripTests.cs`:

```csharp
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>An export declared with the import's fields writes files the import maps back by header.</summary>
public sealed class TabularExportRoundTripTests
{
    private static readonly IntegerImportField IdField = ImportField.Integer("Id");
    private static readonly TextImportField NameField = ImportField.Text("Name");
    private static readonly DecimalImportField AmountField = ImportField.Decimal("Amount");
    private static readonly DecimalImportField RateField = ImportField.Decimal("Rate");
    private static readonly DateImportField StartField = ImportField.Date("Start");
    private static readonly DateImportField SettledField = ImportField.Date("Settled");
    private static readonly BooleanImportField ActiveField = ImportField.Boolean("Active");

    private static readonly ImportSchema Schema = new() { Fields = [IdField, NameField, AmountField, RateField, StartField, SettledField, ActiveField] };

    private sealed record Portfolio(long Id, string? Name, decimal? Amount, double? Rate, DateTime Start, DateOnly? Settled, bool Active);

    private sealed record Batch(List<Portfolio> Items);

    private static readonly TabularExport<Portfolio> Export = TabularExport.For<Portfolio>()
        .Column(IdField, p => p.Id)
        .Column(NameField, p => p.Name)
        .Column(AmountField, p => p.Amount)
        .Column(RateField, p => p.Rate)
        .Column(StartField, p => p.Start)
        .Column(SettledField, p => p.Settled)
        .Column(ActiveField, p => p.Active)
        .Build();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<TabularFormat, string> Formats => new()
    {
        { TabularFormat.Csv, "t.csv" },
        { TabularFormat.Xlsx, "t.xlsx" },
        { TabularFormat.Ods, "t.ods" },
    };

    private static DateTime At(int year, int month, int day, int hour = 0, int minute = 0, int second = 0, int millisecond = 0) =>
        new(year, month, day, hour, minute, second, millisecond, DateTimeKind.Unspecified);

    private static Portfolio[] Portfolios(int count) =>
    [
        .. Enumerable.Range(1, count).Select(i => new Portfolio(
            i,
            i % 7 == 0 ? null : $"Portfolio {i}, \"quoted\"",
            i % 5 == 0 ? null : i * 1.25m,
            i % 3 == 0 ? null : i / 8d,
            At(2026, 1 + (i % 12), 1 + (i % 28), i % 24, i % 60, i % 60, i % 1000),
            i % 4 == 0 ? null : new DateOnly(2026, 1 + (i % 12), 1 + (i % 28)),
            i % 2 == 0)),
    ];

    private static async IAsyncEnumerable<Batch> Batches(Portfolio[] items, int size)
    {
        for (int at = 0; at < items.Length; at += size)
        {
            await Task.Yield();
            yield return new Batch([.. items[at..Math.Min(items.Length, at + size)]]);
        }
    }

    private static List<Portfolio> Import(byte[] file, string name)
    {
        using ITabularCursor cursor = TabularFile.Open(new MemoryStream(file, writable: false), name, cancellationToken: Token);
        FileProfile profile = TabularAnalyzer.Analyze(cursor, cancellationToken: Token);
        MappingPlan plan = MappingPlan.ByHeader(profile.Sheets[0], Schema);

        using ImportRun<Portfolio> run = TabularImporter.Import(
            new MemoryStream(file, writable: false),
            name,
            plan,
            Schema,
            row => new Portfolio(row[IdField]!.Value, row[NameField], row[AmountField], row[RateField] is { } rate ? (double)rate : null, row[StartField]!.Value, row[SettledField] is { } settled ? DateOnly.FromDateTime(settled) : null, row[ActiveField]!.Value),
            cancellationToken: Token);

        List<ImportOutcome<Portfolio>> outcomes = [.. run.ReadRows(Token)];
        Assert.All(outcomes, outcome => Assert.False(outcome.HasErrors, string.Join(", ", outcome.Errors.Select(e => e.Code))));
        return [.. outcomes.Select(outcome => outcome.Value!)];
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task MapsBackByHeaderAsWritten(TabularFormat format, string name)
    {
        Portfolio[] written = Portfolios(500);
        WriteTarget target = new();

        await Export.WriteAsync(target, format, "Portfolios", written, cancellationToken: Token);

        Assert.Equal(written, Import(target.ToArray(), name));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task WritesChunksTakenFromMessages(TabularFormat format, string name)
    {
        Portfolio[] written = Portfolios(2_345);
        WriteTarget target = new();

        long rows = await Export.WriteAsync(target, format, "Portfolios", Batches(written, 1_000), batch => batch.Items, cancellationToken: Token);

        Assert.Equal(written.Length, rows);
        Assert.Equal(written, Import(target.ToArray(), name));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task OneExportServesConcurrentWrites(TabularFormat format, string name)
    {
        Portfolio[] first = Portfolios(3_000);
        Portfolio[] second = [.. Portfolios(3_000).Select(p => p with { Name = "other " + p.Id })];
        WriteTarget one = new();
        WriteTarget two = new();

        await Task.WhenAll(
            Task.Run(async () => await Export.WriteAsync(one, format, "a", Batches(first, 100), batch => batch.Items, cancellationToken: Token), Token),
            Task.Run(async () => await Export.WriteAsync(two, format, "b", Batches(second, 100), batch => batch.Items, cancellationToken: Token), Token));

        Assert.Equal(first, Import(one.ToArray(), name));
        Assert.Equal(second, Import(two.ToArray(), name));
    }

    [Theory]
    [InlineData(TabularFormat.Csv)]
    [InlineData(TabularFormat.Xlsx)]
    [InlineData(TabularFormat.Ods)]
    public async Task AllocatesNothingPerRow(TabularFormat format)
    {
        // One reused chunk, a source that completes synchronously, and Stream.Null: every await stays
        // on this thread, so the thread's allocation counter sees the whole write and nothing else.
        Portfolio[] chunk = Portfolios(10_000);

        async ValueTask<long> Allocated(int chunks)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            await Export.WriteAsync(Stream.Null, format, "data", Repeat(chunk, chunks), new TabularWriterOptions { LeaveOpen = true }, Token);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        await Allocated(2);                       // warm-up: static state, pools, JIT
        long tenThousandRows = await Allocated(1);
        long millionRows = await Allocated(100);

        // A million rows against ten thousand: what grows with the row count is under a byte a row.
        Assert.True(millionRows - tenThousandRows < 1_000_000, $"{format}: {tenThousandRows:N0} bytes for 10k rows, {millionRows:N0} for 1M");
    }

    private static async IAsyncEnumerable<IReadOnlyList<Portfolio>> Repeat(Portfolio[] chunk, int times)
    {
        for (int i = 0; i < times; i++)
        {
            yield return chunk;
        }

        await Task.CompletedTask;
    }
}
```

The `Import` helper maps by header through analysis (`MappingPlan.ByHeader`), proving the headers the export took from the fields are the ones the schema expects. A `Rate` written as a double is read back by the decimal field and converted here, so `Rate` values must be exact in both (they are eighths). If `MappingPlan.ByHeader`'s signature differs (check `src/TriasDev.Tabular/Mapping/MappingPlan.cs`), adapt only the call.

If `AllocatesNothingPerRow` fails, it is a finding: report the measured bytes per format and do not loosen the bound. If the async source does not complete synchronously on some runtime (the counter then misses work on other threads, which would make the test pass vacuously — check that `tenThousandRows` is well above zero, e.g. at least the bytes of the writer's buffers, and report the numbers).

- [ ] **Step 2: Run them**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~TabularExportRoundTripTests"`
Expected: PASS. A failing round trip is a finding: report the format, the row, the value written and read; do not change the writer, the reader or an expectation.

- [ ] **Step 3: Note the overload pitfalls**

In `docs/KNOWN-ISSUES.md`, writing section, append: `TabularExport: a char property resolves to an integer column (its code point), ulong and p => null are ambiguous, an int with a DecimalImportField is ambiguous between decimal and double — convert explicitly; write enums and identifiers as text.`

- [ ] **Step 4: Run the whole suite and the Release build**

Run: `dotnet test --solution TriasDev.Tabular.slnx`, `dotnet build TriasDev.Tabular.slnx -c Release`, `dotnet format TriasDev.Tabular.slnx --verify-no-changes`.
Expected: all green, 0 warnings, no format changes.

- [ ] **Step 5: Commit**

```bash
git add tests docs/KNOWN-ISSUES.md
git commit -m "test(write): an export maps back by header in every format, allocates nothing per row, serves concurrent writes"
```

---

## After the last task

One pull request for part 4 (`feat(write): TabularExport<T> — export objects by declaring their columns once`), body referencing #73 and #67; merge when CI is green. Then part 5 (#74): benchmarks, comparison, `docs/exporting.md`, ADR-0002, and the wide-sheet decision noted in the project memory.
