# Styled export, part 3 — column batches Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Data that arrives by column — a row count plus one typed vector per column, thousands of columns — is written without boxing and without building rows, with a style per column given as a constant, a vector or a rule; `TabularExport<T>` columns get the same style rule.

**Architecture:** `ColumnBatch` holds one reusable typed slot per column (`ValuesColumn<T>` over `IReadOnlyList<T>`, `SelectedColumn<TItem, T>` over items plus a selector). `TabularWriter.WriteBatch` walks rows, then columns, with one virtual call per cell; the slot writes its value through `CellValue<T>.Write`, a `typeof(T)` switch the JIT folds for value types, into the existing styled `Write` overloads, so every check, error and format path is the one rows already use. A style rule's `CellStyle` becomes a `StyleId` through `TabularWriter.StyleFor`, a reference-keyed cache with a last-hit shortcut.

**Tech Stack:** C# / .NET (net8.0 + net10.0), BCL only; xunit v3 on Microsoft.Testing.Platform.

**Spec:** `docs/superpowers/specs/2026-10-04-styled-columnar-export-design.md` §3 (parts 1–2 are merged: styles, `StyleId`, sheet layout, merges). Issue #88.

## Global Constraints

- One package, no dependencies; public types in namespace `TriasDev.Tabular` under `src/TriasDev.Tabular/Writing/`; every public member in `PublicAPI.Unshipped.txt` (copy the RS0016 lines).
- Analyzers warnings-as-errors (Sonar S3776 complexity, S3358 nested ternaries: refactor into helpers, don't suppress); `dotnet format --verify-no-changes` passes.
- Supported value types: `string`, `long`, `int`, `short`, `double`, `decimal`, `bool`, `DateTime`, `DateOnly`, and `Nullable<>` of each value type. Anything else is refused by `Add` with `ArgumentException` naming the type. A null value (null string, empty nullable) is an empty cell — styled when the column has a style.
- A `DateTime` column writes each value as `Write(DateTime)` does (time of day decides date vs date-time); there is no `hasTime` parameter.
- Checks are per column, not per cell: `Add` refuses a vector (values, items, style vector) whose length differs from the batch's row count (`ArgumentException` naming the column, 1-based); `WriteBatch` refuses a batch whose column count differs from the sheet's (`ArgumentException`) and a call outside a sheet or inside a row (`InvalidOperationException`); both fault the writer, as every writer refusal does. Values are checked exactly as by `Write`.
- Nothing allocated per cell; nothing allocated per batch once the batch's slots exist (`Reset` reuses them by index when the column at that index keeps its kind).
- `WriteBatchAsync` flushes whenever `FlushRecommended` after a row, so memory stays flat inside a batch of 10,000 rows × 5,000 columns.
- A style rule returning a `CellStyle` costs a reference compare per cell when it returns the same instance as the previous cell, one dictionary lookup otherwise; `null` means unstyled.
- The repository is public: no product or customer names anywhere.

Commands: build `dotnet build -c Release`; one class `dotnet test --project tests/TriasDev.Tabular.Tests -c Release -- --filter-class "*Name"`; full suite `TABULAR_REQUIRE_SOFFICE=1 dotnet test -c Release`; format `dotnet format --verify-no-changes`.

## Review Focus

1. A batch whose source lists are reused and refilled between batches (the caller clears and refills one `List<double>` per column): each batch writes the values it held when written, and `Reset` drops the references it held. (Task 2)
2. A value the format refuses (a double with 17 significant digits, a forbidden character) inside a batch: the error names sheet, row, column and header like a row write, and the writer is faulted. (Task 2)
3. A style rule that allocates a new `CellStyle` per cell: the file stays correct and the writer's cache does not grow without bound. (Task 1)
4. The same `TabularExport<T>` with a style rule used by several writers at once: each file gets its own style ids, no cross-talk. (Task 3)
5. 1,000 rows × 5,000 columns in each format reads back value for value; a batch allocates nothing per cell. (Task 4)

---

### Task 1: Typed cell writes and the style cache

**Files:**
- Create: `src/TriasDev.Tabular/Writing/CellValue.cs`
- Modify: `src/TriasDev.Tabular/Writing/TabularWriter.cs`
- Test: `tests/TriasDev.Tabular.Tests/Writing/CellValueTests.cs`

**Interfaces:**
- Produces:
  - `internal static class CellValue<T>` with `public static readonly bool Supported` and `public static void Write(TabularWriter writer, T value, StyleId style)`; `internal static class CellValue` with `public const string SupportedTypes = "string, long, int, short, double, decimal, bool, DateTime, DateOnly and their nullable forms"`.
  - `TabularWriter.StyleFor(CellStyle? style) → StyleId` (internal).

- [ ] **Step 1: Write the failing tests**

```csharp
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>Writing a typed value without boxing, and turning a rule's style into an id.</summary>
public sealed class CellValueTests
{
    private static readonly CellStyle Red = new() { Fill = CellColor.FromRgb(0xFF0000) };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void KnowsTheSupportedTypes()
    {
        Assert.True(CellValue<string>.Supported);
        Assert.True(CellValue<int?>.Supported);
        Assert.True(CellValue<DateOnly>.Supported);
        Assert.True(CellValue<decimal?>.Supported);
        Assert.False(CellValue<float>.Supported);
        Assert.False(CellValue<Guid>.Supported);
        Assert.False(CellValue<object>.Supported);
    }

    [Fact]
    public async Task WritesEachTypeAsTheTypedWriteDoes()
    {
        static void Row(TabularWriter writer, bool typed)
        {
            writer.BeginRow();

            if (typed)
            {
                CellValue<string?>.Write(writer, "text", default);
                CellValue<int>.Write(writer, 7, default);
                CellValue<short?>.Write(writer, (short)3, default);
                CellValue<long?>.Write(writer, null, default);
                CellValue<double>.Write(writer, 2.5, default);
                CellValue<decimal>.Write(writer, 1.25m, default);
                CellValue<bool?>.Write(writer, true, default);
                CellValue<DateTime>.Write(writer, new DateTime(2026, 10, 4, 9, 5, 0, DateTimeKind.Unspecified), default);
                CellValue<DateOnly?>.Write(writer, new DateOnly(2026, 10, 4), default);
            }
            else
            {
                writer.Write("text");
                writer.Write(7L);
                writer.Write(3L);
                writer.WriteEmpty();
                writer.Write(2.5);
                writer.Write(1.25m);
                writer.Write(true);
                writer.Write(new DateTime(2026, 10, 4, 9, 5, 0, DateTimeKind.Unspecified));
                writer.Write(new DateOnly(2026, 10, 4));
            }

            writer.EndRow();
        }

        foreach (TabularFormat format in new[] { TabularFormat.Csv, TabularFormat.Xlsx, TabularFormat.Ods })
        {
            WriteColumn[] columns = [.. Enumerable.Range(1, 9).Select(i => new WriteColumn($"c{i}"))];
            byte[] typed = await SheetLayoutTests.Write(format, writer => { writer.BeginSheet("data", columns); Row(writer, typed: true); });
            byte[] plain = await SheetLayoutTests.Write(format, writer => { writer.BeginSheet("data", columns); Row(writer, typed: false); });

            Assert.Equal(plain, typed);
        }
    }

    [Fact]
    public async Task AStyledNullIsAStyledEmptyCell()
    {
        byte[] xlsx = await SheetLayoutTests.Write(TabularFormat.Xlsx, writer =>
        {
            StyleId red = writer.Style(Red);
            writer.BeginSheet("data", [new("a")]);
            writer.BeginRow();
            CellValue<double?>.Write(writer, null, red);
            writer.EndRow();
        });

        Assert.Contains("<c r=\"A2\" s=\"4\"/>", SheetLayoutTests.Entry(xlsx, "xl/worksheets/sheet1.xml"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StyleForGivesOneIdPerStyleAndNoneForNull()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);

        StyleId first = writer.StyleFor(Red);
        Assert.Equal(first, writer.StyleFor(Red));
        Assert.Equal(first, writer.StyleFor(Red with { }));           // another instance, same value
        Assert.Equal(first, writer.Style(Red));
        Assert.Equal(default, writer.StyleFor(null));
    }

    [Fact]
    public async Task StyleForKeepsItsCacheBoundedWhenARuleAllocatesPerCell()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);

        StyleId id = default;

        for (int i = 0; i < 100_000; i++)
        {
            id = writer.StyleFor(new CellStyle { Fill = CellColor.FromRgb(0xFF0000) });
        }

        Assert.Equal(writer.Style(Red), id);
        Assert.True(writer.StyleCacheCount <= TabularWriter.StyleCacheLimit, $"{writer.StyleCacheCount} cached styles");
    }

    [Fact]
    public async Task StyleForAllocatesNothingOnAHit()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);
        CellStyle blue = new() { Fill = CellColor.FromRgb(0x0000FF) };
        writer.StyleFor(Red);
        writer.StyleFor(blue);

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 100_000; i++)
        {
            writer.StyleFor(i % 2 == 0 ? Red : blue);
        }

        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 99_999);
    }
}
```

`StyleCacheCount` and `StyleCacheLimit` are internal test hooks (`internal int StyleCacheCount => _byReference.Count;`, `internal const int StyleCacheLimit = 16_384;`). Put `CellValueTests` in the `AllocationMeasurementCollection` (`[Collection(…)]` as the existing allocation tests do) because one test counts allocations.

- [ ] **Step 2: Run to verify it fails** → compile errors.

- [ ] **Step 3: Implement `CellValue<T>`**

```csharp
using System.Runtime.CompilerServices;

namespace TriasDev.Tabular;

/// <summary>The value types a column of a batch or an export can have.</summary>
internal static class CellValue
{
    public const string SupportedTypes = "string, long, int, short, double, decimal, bool, DateTime, DateOnly and their nullable forms";
}

/// <summary>
/// Writes a value of a known type into the writer's next cell through its typed <c>Write</c>: no
/// boxing, and for value types the type tests fold away when the JIT specialises the method.
/// </summary>
internal static class CellValue<T>
{
    /// <summary>Whether values of <typeparamref name="T"/> can be written.</summary>
    public static readonly bool Supported =
        typeof(T) == typeof(string)
        || typeof(T) == typeof(long) || typeof(T) == typeof(long?)
        || typeof(T) == typeof(int) || typeof(T) == typeof(int?)
        || typeof(T) == typeof(short) || typeof(T) == typeof(short?)
        || typeof(T) == typeof(double) || typeof(T) == typeof(double?)
        || typeof(T) == typeof(decimal) || typeof(T) == typeof(decimal?)
        || typeof(T) == typeof(bool) || typeof(T) == typeof(bool?)
        || typeof(T) == typeof(DateTime) || typeof(T) == typeof(DateTime?)
        || typeof(T) == typeof(DateOnly) || typeof(T) == typeof(DateOnly?);

    /// <summary>Writes the value in a style; null (a null string or an empty nullable) is an empty cell in that style.</summary>
    public static void Write(TabularWriter writer, T value, StyleId style)
    {
        if (typeof(T) == typeof(double))
        {
            writer.Write(Unsafe.As<T, double>(ref value), style);
        }
        else if (typeof(T) == typeof(long))
        {
            writer.Write(Unsafe.As<T, long>(ref value), style);
        }
        else if (typeof(T) == typeof(string))
        {
            writer.Write(Unsafe.As<T, string?>(ref value), style);
        }
        else
        {
            WriteOther(writer, value, style);
        }
    }
```

and `WriteOther` handles the rest the same way (`int` and `short` widen to `long`; each nullable writes its value or `writer.WriteEmpty(style)`), split into as many helpers as S3776 needs. `Write(string? null, style)` already writes an empty cell; for it to be a *styled* empty cell, check `TabularWriter.Write(string?, StyleId)`: if it calls `_sheet.WriteEmpty()` with style 0 for null, change it to pass the style index — and add that case to `AStyledNullIsAStyledEmptyCell` (a `CellValue<string?>.Write(writer, null, red)` cell). An unsupported `T` throws `InvalidOperationException` here (unreachable once `Add` checks `Supported`).

- [ ] **Step 4: Implement `StyleFor`** in `TabularWriter`:

```csharp
    /// <summary>The most rule-returned styles remembered by reference before the cache starts over.</summary>
    internal const int StyleCacheLimit = 16_384;

    private readonly Dictionary<CellStyle, StyleId> _byReference = new(ReferenceEqualityComparer.Instance);
    private CellStyle? _lastStyle;
    private StyleId _lastStyleId;

    internal int StyleCacheCount => _byReference.Count;

    /// <summary>
    /// The id of a style a rule returned: a reference compare when it is the previous one, a lookup by
    /// reference otherwise, registering it (by value) the first time. Null is the unstyled cell.
    /// </summary>
    /// <remarks>
    /// A rule that builds a new style per cell still writes correctly — registration dedupes by value —
    /// but the reference cache would grow with every cell, so it starts over at <see cref="StyleCacheLimit"/>.
    /// </remarks>
    internal StyleId StyleFor(CellStyle? style)
    {
        if (style is null)
        {
            return default;
        }

        if (ReferenceEquals(style, _lastStyle))
        {
            return _lastStyleId;
        }

        if (!_byReference.TryGetValue(style, out StyleId id))
        {
            id = Style(style);

            if (_byReference.Count == StyleCacheLimit)
            {
                _byReference.Clear();
            }

            _byReference.Add(style, id);
        }

        _lastStyle = style;
        _lastStyleId = id;
        return id;
    }
```

(`Dictionary<CellStyle, StyleId>` accepts `ReferenceEqualityComparer.Instance`, an `IEqualityComparer<object?>`, by contravariance.)

- [ ] **Step 5: Run** `--filter-class "*CellValueTests"` and the full suite → PASS. Format.

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "feat(write): typed cell writes without boxing and a reference cache for rule-returned styles"
```

---

### Task 2: `ColumnBatch` and `WriteBatch`

**Files:**
- Create: `src/TriasDev.Tabular/Writing/ColumnBatch.cs`, `src/TriasDev.Tabular/Writing/BatchColumn.cs`
- Modify: `src/TriasDev.Tabular/Writing/TabularWriter.cs`, `PublicAPI.Unshipped.txt`
- Test: `tests/TriasDev.Tabular.Tests/Writing/ColumnBatchTests.cs`

**Interfaces:**
- Consumes: `CellValue<T>.Supported` / `.Write`, `CellValue.SupportedTypes`, `TabularWriter.StyleFor` (Task 1).
- Produces:
  - `public sealed class ColumnBatch` — `int RowCount { get; }`, `int ColumnCount { get; }`, `void Reset(int rowCount)`, and eight `Add` methods:
    - `Add<T>(IReadOnlyList<T> values)`, `Add<T>(IReadOnlyList<T> values, StyleId style)`, `Add<T>(IReadOnlyList<T> values, IReadOnlyList<StyleId> styles)`, `Add<T>(IReadOnlyList<T> values, Func<T, CellStyle?> style)`;
    - `Add<TItem, T>(IReadOnlyList<TItem> items, Func<TItem, T> value)` and the same three style forms as a third parameter.
  - `TabularWriter.WriteBatch(ColumnBatch batch)`, `TabularWriter.WriteBatchAsync(ColumnBatch batch, CancellationToken cancellationToken = default) → ValueTask`.

A note on overloads: `Add(values, v => …)` binds to the style-rule overload when the lambda returns a `CellStyle` (its parameter type, `Func<T, CellStyle?>`, is more specific than `Func<TItem, T>`) and to the selector overload otherwise. Document it on both.

- [ ] **Step 1: Write the failing tests**

```csharp
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>Writing data given by column: typed vectors in, the same file as row by row out.</summary>
public sealed class ColumnBatchTests
{
    private static readonly CellStyle Low = new() { Fill = CellColor.FromRgb(0x63BE7B) };
    private static readonly CellStyle High = new() { Fill = CellColor.FromRgb(0xF8696B) };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly WriteColumn[] Columns = [new("Id"), new("Name"), new("Score"), new("Amount"), new("Start"), new("Active")];

    private sealed record Wrapper(double? Value);

    private static ColumnBatch Batch()
    {
        ColumnBatch batch = new();
        batch.Reset(3);
        batch.Add(new long[] { 1, 2, 3 });
        batch.Add(new List<string?> { "a", null, "c" });
        batch.Add(new Wrapper[] { new(1.5), new(null), new(9.5) }, w => w.Value);
        batch.Add((IReadOnlyList<decimal>)new[] { 1.25m, 2m, 3.5m }.AsReadOnly());
        batch.Add(new DateOnly?[] { new DateOnly(2026, 1, 1), null, new DateOnly(2026, 3, 1) });
        batch.Add(new[] { true, false, true });
        return batch;
    }

    private static void RowByRow(TabularWriter writer)
    {
        object?[][] rows =
        [
            [1L, "a", 1.5, 1.25m, new DateOnly(2026, 1, 1), true],
            [2L, null, null, 2m, null, false],
            [3L, "c", 9.5, 3.5m, new DateOnly(2026, 3, 1), true],
        ];

        foreach (object?[] row in rows)
        {
            writer.BeginRow();
            writer.Write((long)row[0]!);
            writer.Write((string?)row[1]);
            if (row[2] is double d) { writer.Write(d); } else { writer.WriteEmpty(); }
            writer.Write((decimal)row[3]!);
            if (row[4] is DateOnly day) { writer.Write(day); } else { writer.WriteEmpty(); }
            writer.Write((bool)row[5]!);
            writer.EndRow();
        }
    }

    [Theory]
    [InlineData(TabularFormat.Csv)]
    [InlineData(TabularFormat.Xlsx)]
    [InlineData(TabularFormat.Ods)]
    public async Task ABatchWritesTheSameFileAsRows(TabularFormat format)
    {
        byte[] batched = await SheetLayoutTests.Write(format, writer => { writer.BeginSheet("data", Columns); writer.WriteBatch(Batch()); });
        byte[] rows = await SheetLayoutTests.Write(format, writer => { writer.BeginSheet("data", Columns); RowByRow(writer); });

        Assert.Equal(rows, batched);
    }

    [Fact]
    public async Task StylesComeFromAConstantAVectorOrARule()
    {
        byte[] xlsx = await SheetLayoutTests.Write(TabularFormat.Xlsx, writer =>
        {
            StyleId low = writer.Style(Low);
            StyleId high = writer.Style(High);
            writer.BeginSheet("data", [new("constant"), new("vector"), new("rule")]);
            ColumnBatch batch = new();
            batch.Reset(2);
            batch.Add(new[] { 1.0, 2.0 }, low);
            batch.Add(new[] { 1.0, 2.0 }, new[] { low, high });
            batch.Add(new[] { 1.0, 9.0 }, v => v > 5 ? High : null);
            writer.WriteBatch(batch);
        });

        string sheet = SheetLayoutTests.Entry(xlsx, "xl/worksheets/sheet1.xml");
        Assert.Contains("<c r=\"A2\" s=\"4\"><v>1</v></c><c r=\"B2\" s=\"4\"><v>1</v></c><c r=\"C2\"><v>1</v></c>", sheet, StringComparison.Ordinal);
        Assert.Contains("<c r=\"A3\" s=\"4\"><v>2</v></c><c r=\"B3\" s=\"5\"><v>2</v></c><c r=\"C3\" s=\"5\"><v>9</v></c>", sheet, StringComparison.Ordinal);
    }

    [Fact]
    public void AVectorOfTheWrongLengthIsRefusedWhenAdded()
    {
        ColumnBatch batch = new();
        batch.Reset(3);
        batch.Add(new long[] { 1, 2, 3 });

        ArgumentException refused = Assert.Throws<ArgumentException>(() => batch.Add(new long[] { 1, 2 }));
        Assert.Contains("column 2", refused.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => batch.Add(new long[] { 1, 2, 3 }, new StyleId[2]));
    }

    [Fact]
    public void AnUnsupportedTypeIsRefusedWhenAdded()
    {
        ColumnBatch batch = new();
        batch.Reset(1);

        ArgumentException refused = Assert.Throws<ArgumentException>(() => batch.Add(new[] { Guid.Empty }));
        Assert.Contains("Guid", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABatchWithTheWrongNumberOfColumnsIsRefused()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Csv);
        writer.BeginSheet("data", [new("a"), new("b")]);
        ColumnBatch batch = new();
        batch.Reset(1);
        batch.Add(new[] { 1L });

        Assert.Throws<ArgumentException>(() => writer.WriteBatch(batch));
    }

    [Fact]
    public async Task ABatchOutsideASheetOrInsideARowIsRefused()
    {
        ColumnBatch batch = new();
        batch.Reset(0);

        await using (TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Csv))
        {
            Assert.Throws<InvalidOperationException>(() => writer.WriteBatch(batch));
        }

        await using (TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Csv))
        {
            writer.BeginSheet("data", [new("a")]);
            writer.BeginRow();
            Assert.Throws<InvalidOperationException>(() => writer.WriteBatch(batch));
        }
    }

    [Fact]
    public async Task AValueTheFormatRefusesIsReportedLikeARowWrite()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);
        writer.BeginSheet("data", [new("Id"), new("Score")]);
        ColumnBatch batch = new();
        batch.Reset(2);
        batch.Add(new[] { 1L, 2L });
        batch.Add(new[] { 1.5, 0.1 + 0.2 });                // 0.30000000000000004: 17 significant digits

        TabularWriteException refused = Assert.Throws<TabularWriteException>(() => writer.WriteBatch(batch));
        Assert.Equal(3, refused.RowNumber);
        Assert.Equal(1, refused.ColumnIndex);
        Assert.Equal("Score", refused.Header);
        Assert.Throws<InvalidOperationException>(() => writer.WriteBatch(batch));      // faulted
    }

    [Fact]
    public async Task ReusedSourceListsWriteWhatTheyHeldWhenWritten()
    {
        byte[] csv = await SheetLayoutTests.Write(TabularFormat.Csv, writer =>
        {
            writer.BeginSheet("data", [new("n")]);
            ColumnBatch batch = new();
            List<long> values = [];

            for (int chunk = 0; chunk < 3; chunk++)
            {
                values.Clear();
                values.AddRange([chunk * 10, (chunk * 10) + 1]);
                batch.Reset(values.Count);
                batch.Add(values);
                writer.WriteBatch(batch);
            }
        });

        Assert.Equal(["n", "0", "1", "10", "11", "20", "21"], SheetLayoutTests.Rows(csv).Select(r => r[0].Text));
    }

    [Fact]
    public void ResetDropsTheReferencesItHeld()
    {
        ColumnBatch batch = new();
        WeakReference held = Hold(batch);
        batch.Reset(0);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(held.IsAlive);

        static WeakReference Hold(ColumnBatch batch)
        {
            long[] values = new long[1_000];
            batch.Reset(values.Length);
            batch.Add(values);
            return new WeakReference(values);
        }
    }

    [Fact]
    public async Task WriteBatchAsyncFlushesInsideALargeBatch()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Csv))
        {
            writer.BeginSheet("data", [new("text")]);
            string[] values = [.. Enumerable.Range(0, 100_000).Select(i => $"value number {i}")];
            ColumnBatch batch = new();
            batch.Reset(values.Length);
            batch.Add(values);
            await writer.WriteBatchAsync(batch, Token);
            Assert.True(target.AsyncWrites > 1, $"{target.AsyncWrites} writes reached the target during the batch");
            await writer.CompleteAsync(Token);
        }
    }

    [Fact]
    public async Task WriteBatchAsyncHonoursCancellation()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Csv);
        writer.BeginSheet("data", [new("n")]);
        ColumnBatch batch = new();
        batch.Reset(10);
        batch.Add(new long[10]);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await writer.WriteBatchAsync(batch, cancelled.Token));
    }
}
```

`TabularWriteException` exposes `RowNumber`, `ColumnIndex` and `Header` — use the names the type actually has (read `src/TriasDev.Tabular/Abstractions/TabularException.cs`) and keep the assertions' meaning. The xlsx style test expects `s="4"`/`s="5"` for `Low`/`High` (the first two styles on numbers after the four fixed formats) and no `s` for a rule returning null; if `XlsxStyles` numbers them otherwise, trace and report rather than change the expectation silently.

- [ ] **Step 2: Run to verify it fails** → compile errors.

- [ ] **Step 3: Implement the slots** (`BatchColumn.cs`)

```csharp
namespace TriasDev.Tabular;

/// <summary>One column of a batch: writes its value for a row as the writer's next cell.</summary>
internal abstract class BatchColumn
{
    public abstract void Write(TabularWriter writer, int row);

    /// <summary>Drops the caller's lists, so a reset batch keeps nothing alive.</summary>
    public abstract void Clear();
}

/// <summary>Where a column's style comes from: a constant, a vector by row, or a rule on the value.</summary>
internal struct BatchStyle<T>
{
    public StyleId Constant;
    public IReadOnlyList<StyleId>? Vector;
    public Func<T, CellStyle?>? Rule;

    public readonly StyleId For(TabularWriter writer, T value, int row)
    {
        if (Rule is not null)
        {
            return writer.StyleFor(Rule(value));
        }

        return Vector is not null ? Vector[row] : Constant;
    }
}

/// <summary>A column over a list of values; arrays and lists are indexed directly, other lists through the interface.</summary>
internal sealed class ValuesColumn<T> : BatchColumn
{
    private T[]? _array;
    private List<T>? _list;
    private IReadOnlyList<T>? _values;
    private BatchStyle<T> _style;

    public void Bind(IReadOnlyList<T> values, BatchStyle<T> style)
    {
        _array = values as T[];
        _list = _array is null ? values as List<T> : null;
        _values = values;
        _style = style;
    }

    public override void Write(TabularWriter writer, int row)
    {
        T value = _array is not null ? _array[row] : Item(row);
        CellValue<T>.Write(writer, value, _style.For(writer, value, row));
    }

    public override void Clear()
    {
        _array = null;
        _list = null;
        _values = null;
        _style = default;
    }

    private T Item(int row) => _list is not null ? _list[row] : _values![row];
}

/// <summary>A column over items and a selector for the value: one delegate call per cell.</summary>
internal sealed class SelectedColumn<TItem, T> : BatchColumn
{
    private IReadOnlyList<TItem>? _items;
    private Func<TItem, T>? _value;
    private BatchStyle<T> _style;

    public void Bind(IReadOnlyList<TItem> items, Func<TItem, T> value, BatchStyle<T> style)
    {
        _items = items;
        _value = value;
        _style = style;
    }

    public override void Write(TabularWriter writer, int row)
    {
        T value = _value!(_items![row]);
        CellValue<T>.Write(writer, value, _style.For(writer, value, row));
    }

    public override void Clear()
    {
        _items = null;
        _value = null;
        _style = default;
    }
}
```

- [ ] **Step 4: Implement `ColumnBatch`**

```csharp
namespace TriasDev.Tabular;

/// <summary>
/// A batch of rows given by column — one list per column, all of the batch's row count — written by
/// <see cref="TabularWriter.WriteBatch"/> row by row, typed and without boxing. Reuse one batch for
/// every chunk: <see cref="Reset"/> keeps its column slots, so a batch allocates nothing once warm.
/// </summary>
/// <remarks>
/// <para>
/// Values: <c>string</c>, <c>long</c>, <c>int</c>, <c>short</c>, <c>double</c>, <c>decimal</c>,
/// <c>bool</c>, <c>DateTime</c>, <c>DateOnly</c>, and the nullable forms; a null is an empty cell.
/// Arrays, <see cref="List{T}"/> and any other <see cref="IReadOnlyList{T}"/> — a protobuf repeated
/// field among them — are read where they are, never copied; they must not change until the batch
/// is written.
/// </para>
/// <para>
/// A column's style is a constant id, a vector of ids by row, or a rule on the value. A rule should
/// return styles declared once (<c>static readonly</c>): the same instance costs a reference compare.
/// <c>Add(values, v =&gt; …)</c> takes the lambda as a style rule when it returns a <see cref="CellStyle"/>,
/// and as a value selector otherwise.
/// </para>
/// <para>Not thread-safe.</para>
/// </remarks>
public sealed class ColumnBatch
{
    private BatchColumn[] _columns = new BatchColumn[16];

    /// <summary>The rows each column holds.</summary>
    public int RowCount { get; private set; }

    /// <summary>The columns added since the last <see cref="Reset"/>.</summary>
    public int ColumnCount { get; private set; }

    /// <summary>Empties the batch for a chunk of <paramref name="rowCount"/> rows; its slots are kept for reuse.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rowCount"/> is negative.</exception>
    public void Reset(int rowCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);

        for (int i = 0; i < ColumnCount; i++)
        {
            _columns[i].Clear();
        }

        RowCount = rowCount;
        ColumnCount = 0;
    }

    /// <summary>Adds the next column: its values, one per row.</summary>
    /// <exception cref="ArgumentException">A type that cannot be written, or not one value per row.</exception>
    public void Add<T>(IReadOnlyList<T> values) => AddValues(values, default);

    /// <summary>Adds the next column, every cell in one style.</summary>
    public void Add<T>(IReadOnlyList<T> values, StyleId style) => AddValues(values, new BatchStyle<T> { Constant = style });

    /// <summary>Adds the next column, each cell in the style at its row.</summary>
    public void Add<T>(IReadOnlyList<T> values, IReadOnlyList<StyleId> styles) => AddValues(values, new BatchStyle<T> { Vector = Checked(styles, nameof(styles)) });

    /// <summary>Adds the next column, each cell in the style the rule returns for its value; null for none.</summary>
    public void Add<T>(IReadOnlyList<T> values, Func<T, CellStyle?> style) => AddValues(values, new BatchStyle<T> { Rule = style ?? throw new ArgumentNullException(nameof(style)) });

    /// <summary>Adds the next column: a value selected from each item, one item per row.</summary>
    public void Add<TItem, T>(IReadOnlyList<TItem> items, Func<TItem, T> value) => AddSelected(items, value, default);

    /// <summary>Adds the next column of selected values, every cell in one style.</summary>
    public void Add<TItem, T>(IReadOnlyList<TItem> items, Func<TItem, T> value, StyleId style) => AddSelected(items, value, new BatchStyle<T> { Constant = style });

    /// <summary>Adds the next column of selected values, each cell in the style at its row.</summary>
    public void Add<TItem, T>(IReadOnlyList<TItem> items, Func<TItem, T> value, IReadOnlyList<StyleId> styles) => AddSelected(items, value, new BatchStyle<T> { Vector = Checked(styles, nameof(styles)) });

    /// <summary>Adds the next column of selected values, each cell in the style the rule returns; null for none.</summary>
    public void Add<TItem, T>(IReadOnlyList<TItem> items, Func<TItem, T> value, Func<T, CellStyle?> style) => AddSelected(items, value, new BatchStyle<T> { Rule = style ?? throw new ArgumentNullException(nameof(style)) });

    internal BatchColumn Column(int index) => _columns[index];

    private void AddValues<T>(IReadOnlyList<T> values, BatchStyle<T> style)
    {
        ExpectWritable<T>(Checked(values, nameof(values)));
        Next<ValuesColumn<T>>().Bind(values, style);
    }

    private void AddSelected<TItem, T>(IReadOnlyList<TItem> items, Func<TItem, T> value, BatchStyle<T> style)
    {
        ArgumentNullException.ThrowIfNull(value);
        ExpectWritable<T>(Checked(items, nameof(items)));
        Next<SelectedColumn<TItem, T>>().Bind(items, value, style);
    }

    private void ExpectWritable<T>(object _)
    {
        if (!CellValue<T>.Supported)
        {
            throw new ArgumentException($"Column {ColumnCount + 1}: values of type {typeof(T).Name} cannot be written; a column holds {CellValue.SupportedTypes}.", "values");
        }
    }

    private IReadOnlyList<TList> Checked<TList>(IReadOnlyList<TList> list, string name)
    {
        ArgumentNullException.ThrowIfNull(list, name);

        if (list.Count != RowCount)
        {
            throw new ArgumentException($"Column {ColumnCount + 1} has {list.Count} entries in {name}; the batch has {RowCount} rows.", name);
        }

        return list;
    }

    private TColumn Next<TColumn>()
        where TColumn : BatchColumn, new()
    {
        if (ColumnCount == _columns.Length)
        {
            Array.Resize(ref _columns, _columns.Length * 2);
        }

        if (_columns[ColumnCount] is not TColumn slot)
        {
            slot = new TColumn();
            _columns[ColumnCount] = slot;
        }

        ColumnCount++;
        return slot;
    }
}
```

Tidy `ExpectWritable` (the `object _` parameter is only there to sequence the length check first; write it as two plain statements instead if it reads better — the order must stay: null and length checks, then the type check). The message for a wrong length must contain `column N` (1-based), which `AVectorOfTheWrongLengthIsRefusedWhenAdded` checks.

- [ ] **Step 5: `WriteBatch` / `WriteBatchAsync`** in `TabularWriter`:

```csharp
    /// <summary>
    /// Writes a batch given by column as its rows, at the sheet's next row: each column's value for the
    /// row in turn, typed, through the same checks as <c>Write</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The batch's columns are not the sheet's.</exception>
    /// <exception cref="InvalidOperationException">Outside a sheet, or inside a row.</exception>
    /// <exception cref="TabularWriteException">A value the format cannot hold, located by sheet, row, column and header.</exception>
    /// <exception cref="TabularLimitException">The sheet outgrew its format's row limit.</exception>
    public void WriteBatch(ColumnBatch batch)
    {
        ExpectBatch(batch);

        for (int row = 0; row < batch.RowCount; row++)
        {
            WriteBatchRow(batch, row);
        }
    }

    /// <summary>
    /// Writes a batch as <see cref="WriteBatch"/> does, flushing to the stream whenever
    /// <see cref="FlushRecommended"/> after a row — memory stays flat however large the batch.
    /// </summary>
    /// (same exceptions, plus OperationCanceledException)
    public async ValueTask WriteBatchAsync(ColumnBatch batch, CancellationToken cancellationToken = default)
    {
        ExpectBatch(batch);

        for (int row = 0; row < batch.RowCount; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteBatchRow(batch, row);

            if (FlushRecommended)
            {
                await FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void ExpectBatch(ColumnBatch batch)
    {
        ExpectWritable();

        if (batch is null)
        {
            throw Faulting(new ArgumentNullException(nameof(batch)));
        }

        if (_state != State.InSheet)
        {
            throw Refuse("A batch is written inside a sheet, between rows.");
        }

        if (batch.ColumnCount != _columns.Length)
        {
            throw Faulting(new ArgumentException($"The batch has {batch.ColumnCount} columns; sheet \"{_sheetName}\" has {_columns.Length}.", nameof(batch)));
        }
    }

    private void WriteBatchRow(ColumnBatch batch, int row)
    {
        BeginRow();

        for (int column = 0; column < _columns.Length; column++)
        {
            batch.Column(column).Write(this, row);
        }

        EndRow();
    }
```

A cancellation inside `WriteBatchAsync` leaves the writer mid-sheet; mark it faulted (`catch (OperationCanceledException) { MarkFaulted(); throw; }` around the loop), as a cancelled flush already does.

- [ ] **Step 6: Run** `--filter-class "*ColumnBatchTests"` and the full suite → PASS. Public API, format.

- [ ] **Step 7: Commit**

```bash
git add src tests
git commit -m "feat(write): ColumnBatch — rows given by column, typed and reused, with a style per column"
```

---

### Task 3: A style rule for `TabularExport<T>` columns

**Files:**
- Modify: `src/TriasDev.Tabular/Writing/ExportColumn.cs`, `src/TriasDev.Tabular/Writing/TabularExportBuilder.cs`, `PublicAPI.Unshipped.txt`
- Test: `tests/TriasDev.Tabular.Tests/Writing/TabularExportStyleTests.cs`

**Interfaces:**
- Consumes: `CellValue<T>.Write`, `TabularWriter.StyleFor` (Task 1).
- Produces: every `Column(…)` overload of `TabularExportBuilder<T>` gains a last optional parameter `Func<TValue, CellStyle?>? style = null` (TValue = the overload's value type), e.g. `Column(string header, Func<T, double?> value, double? width = null, Func<double?, CellStyle?>? style = null)`. The public API lines change accordingly (these overloads are still unshipped).

- [ ] **Step 1: Write the failing tests**

```csharp
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>An export column styled by a rule on its value.</summary>
public sealed class TabularExportStyleTests
{
    private static readonly CellStyle High = new() { Fill = CellColor.FromRgb(0xF8696B) };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed record Location(long Id, double? Score);

    private static readonly TabularExport<Location> Export = TabularExport.For<Location>()
        .Column("Id", l => l.Id)
        .Column("Score", l => l.Score, style: s => s > 5 ? High : null)
        .Build();

    [Fact]
    public async Task TheRuleStylesTheCellsItReturnsAStyleFor()
    {
        WriteTarget target = new();
        await Export.WriteAsync(target, TabularFormat.Xlsx, "data", new[] { new Location(1, 2), new Location(2, 9), new Location(3, null) }, cancellationToken: Token);
        string sheet = SheetLayoutTests.Entry(target.ToArray(), "xl/worksheets/sheet1.xml");

        Assert.Contains("<c r=\"B2\"><v>2</v></c>", sheet, StringComparison.Ordinal);
        Assert.Contains("<c r=\"B3\" s=\"4\"><v>9</v></c>", sheet, StringComparison.Ordinal);
        Assert.Empty(OoxmlValidation.Errors(target.ToArray()));
    }

    [Fact]
    public async Task OneExportServesConcurrentWritersWithTheirOwnStyles()
    {
        Location[] items = [.. Enumerable.Range(1, 2_000).Select(i => new Location(i, i % 10))];

        byte[][] files = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            WriteTarget target = new();
            await Export.WriteAsync(target, TabularFormat.Xlsx, "data", items, cancellationToken: Token);
            return target.ToArray();
        }));

        foreach (byte[] file in files)
        {
            Assert.Equal(files[0], file);
            Assert.Empty(OoxmlValidation.Errors(file));
        }
    }

    [Fact]
    public async Task AnExportWithoutRulesWritesWhatItWroteBefore()
    {
        TabularExport<Location> plain = TabularExport.For<Location>().Column("Id", l => l.Id).Column("Score", l => l.Score).Build();
        WriteTarget a = new();
        WriteTarget b = new();
        await plain.WriteAsync(a, TabularFormat.Csv, "data", new[] { new Location(1, 2.5) }, cancellationToken: Token);
        await Export.WriteAsync(b, TabularFormat.Csv, "data", new[] { new Location(1, 2.5) }, cancellationToken: Token);

        Assert.Equal(a.ToArray(), b.ToArray());
    }
}
```

- [ ] **Step 2: Run to verify it fails** → compile error (`style:` parameter missing).

- [ ] **Step 3: Implement**

- `ExportColumn<T, TValue>` drops the `Action<TabularWriter, TValue>` write delegate and gains an optional rule:

```csharp
internal sealed class ExportColumn<T, TValue>(WriteColumn column, Func<T, TValue> value, Func<TValue, CellStyle?>? style)
    : ExportColumn<T>(column)
{
    public override void Write(TabularWriter writer, T item)
    {
        TValue cell = value(item);
        CellValue<TValue>.Write(writer, cell, style is null ? default : writer.StyleFor(style(cell)));
    }
}
```

  Delete `CellWriters` (every use goes through `CellValue<TValue>` now). Per cell: one virtual call and one delegate call without a rule (one fewer than before); with a rule, one more delegate call and the style cache.
- `TabularExportBuilder<T>`: each `Column` overload gains `Func<TValue, CellStyle?>? style = null` as its last parameter and passes it on; the import-field overloads pass it through to the header overloads. `Add(header, width, value, style)` builds `new ExportColumn<T, TValue>(…, value, style)`.
- The style cache lives in the writer, not in the export, so one built export stays immutable and serves concurrent writers (Review Focus 4).

- [ ] **Step 4: Run** `--filter-class "*TabularExport*"` (the existing export tests must pass unchanged, including the allocation test) and the full suite → PASS. Public API (the changed overload lines), format.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat(write): a style rule per TabularExport column; columns write through CellValue"
```

---

### Task 4: Wide exports — 1,000 × 5,000 in the suite, allocation, measurements; docs

**Files:**
- Test: `tests/TriasDev.Tabular.Tests/Writing/WideExportTests.cs`
- Modify: `docs/KNOWN-ISSUES.md`

- [ ] **Step 1: The tests**

```csharp
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>A wide export — a few fixed columns and thousands of measured ones — written by batch and read back.</summary>
[Collection(AllocationMeasurementCollection.Name)]
public sealed class WideExportTests
{
    private const int Measured = 5_000;

    private static readonly CellStyle[] Legend = [new() { Fill = CellColor.FromRgb(0x63BE7B) }, new() { Fill = CellColor.FromRgb(0xFFEB84) }, new() { Fill = CellColor.FromRgb(0xF8696B) }];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly WriteColumn[] Columns =
        [new("Id"), new("Name"), new("Start"), .. Enumerable.Range(1, Measured).Select(i => new WriteColumn($"Service__Field{i}"))];

    /// <summary>The value of measured column <paramref name="column"/> in row <paramref name="row"/>: a tenth in 0–99.9, one in ten empty.</summary>
    private static double? Value(int row, int column) => (row + column) % 10 == 0 ? null : ((row * 7) + (column * 13)) % 1000 / 10.0;

    private static CellStyle? Colour(double? value) => value switch { null => null, < 33 => Legend[0], < 66 => Legend[1], _ => Legend[2] };

    /// <summary>The rule as one delegate: a method group converted at each Add would allocate a delegate per column.</summary>
    private static readonly Func<double?, CellStyle?> ColourRule = Colour;

    /// <summary>Fills a batch for rows <paramref name="first"/>…, reusing the column arrays.</summary>
    private static void Fill(ColumnBatch batch, double?[][] measured, long[] ids, string[] names, DateOnly[] starts, int first)
    {
        int rows = ids.Length;

        for (int r = 0; r < rows; r++)
        {
            ids[r] = first + r;
            names[r] = $"Location {first + r}";
            starts[r] = new DateOnly(2026, 1, 1).AddDays((first + r) % 365);
        }

        batch.Reset(rows);
        batch.Add(ids);
        batch.Add(names);
        batch.Add(starts);

        for (int c = 0; c < Measured; c++)
        {
            double?[] column = measured[c];

            for (int r = 0; r < rows; r++)
            {
                column[r] = Value(first + r, c);
            }

            batch.Add(column, ColourRule);
        }
    }

    private static async Task<byte[]> Write(TabularFormat format, int rows, int chunk)
    {
        WriteTarget target = new();
        double?[][] measured = [.. Enumerable.Range(0, Measured).Select(_ => new double?[chunk])];
        long[] ids = new long[chunk];
        string[] names = new string[chunk];
        DateOnly[] starts = new DateOnly[chunk];

        await using (TabularWriter writer = TabularWriter.Create(target, format))
        {
            writer.BeginSheet("Locations", Columns, new SheetOptions { FreezeRows = 1, FreezeColumns = 2 });
            ColumnBatch batch = new();

            for (int first = 0; first < rows; first += chunk)
            {
                Fill(batch, measured, ids, names, starts, first);
                await writer.WriteBatchAsync(batch, Token);
            }

            await writer.CompleteAsync(Token);
        }

        return target.ToArray();
    }

    [Theory]
    [InlineData(TabularFormat.Csv)]
    [InlineData(TabularFormat.Xlsx)]
    [InlineData(TabularFormat.Ods)]
    public async Task AThousandRowsOfFiveThousandColumnsReadBack(TabularFormat format)
    {
        byte[] file = await Write(format, rows: 1_000, chunk: 250);
        List<RawCell[]> rows = SheetLayoutTests.Rows(file);

        Assert.Equal(1_001, rows.Count);
        Assert.Equal(Measured + 3, rows[0].Length);

        foreach (int r in new[] { 0, 1, 499, 999 })
        {
            RawCell[] row = rows[r + 1];

            foreach (int c in new[] { 0, 1, 2_499, Measured - 1 })
            {
                RawCell cell = c + 3 < row.Length ? row[c + 3] : default;

                if (Value(r, c) is { } expected)
                {
                    Assert.Equal(format == TabularFormat.Csv ? RawCell.FromText(expected.ToString(System.Globalization.CultureInfo.InvariantCulture)) : RawCell.FromNumber(expected), cell);
                }
                else
                {
                    Assert.True(cell.IsEmpty, $"row {r}, column {c}");
                }
            }
        }
    }

    [Theory]
    [InlineData(TabularFormat.Csv)]
    [InlineData(TabularFormat.Xlsx)]
    [InlineData(TabularFormat.Ods)]
    public async Task ABatchAllocatesNothingPerCell(TabularFormat format)
    {
        const int Chunk = 100;
        double?[][] measured = [.. Enumerable.Range(0, Measured).Select(_ => new double?[Chunk])];
        long[] ids = new long[Chunk];
        string[] names = new string[Chunk];
        DateOnly[] starts = new DateOnly[Chunk];

        async ValueTask<long> Allocated(int batches)
        {
            await using TabularWriter writer = TabularWriter.Create(Stream.Null, format, new TabularWriterOptions { LeaveOpen = true });
            writer.BeginSheet("Locations", Columns);
            ColumnBatch batch = new();

            // The names are strings built per row by the caller; count only the writer.
            Fill(batch, measured, ids, names, starts, 0);
            long before = GC.GetAllocatedBytesForCurrentThread();

            for (int i = 0; i < batches; i++)
            {
                batch.Reset(Chunk);
                Rebind(batch, measured, ids, names, starts);
                await writer.WriteBatchAsync(batch, Token);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            await writer.CompleteAsync(Token);
            return allocated;
        }

        await Allocated(2);                       // warm-up: JIT, pools, the style cache
        long few = await Allocated(2);
        long many = await Allocated(12);

        // Ten more batches of 100 rows × 5,003 cells: what grows with them is under a byte per row.
        Assert.True(many - few < 1_000, $"{format}: {few:N0} bytes for 2 batches, {many:N0} for 12");
    }

    private static void Rebind(ColumnBatch batch, double?[][] measured, long[] ids, string[] names, DateOnly[] starts)
    {
        batch.Add(ids);
        batch.Add(names);
        batch.Add(starts);

        foreach (double?[] column in measured)
        {
            batch.Add(column, ColourRule);
        }
    }
}
```

Notes for the implementer:
- The ods reader drops an all-empty trailing run; a missing trailing cell reads as empty, which the `c + 3 < row.Length` guard handles.
- If a bound fails, measure first, then fix the allocation in the library — never loosen a bound.

- [ ] **Step 2: Run** `--filter-class "*WideExportTests"` → PASS. Record each test's duration from the test output in the report.

- [ ] **Step 3: Measure 10,000 × 5,000 once** (not committed as a test): a throwaway console program in the scratchpad (`$TMPDIR` or the session scratchpad given in the dispatch) referencing `src/TriasDev.Tabular`, writing 10,000 rows × (3 + 5,000) columns in chunks of 1,000 with the legend rule, for csv, xlsx and ods into `Stream.Null` and into a file; report per format: write time, file size, peak working set (`Process.GetCurrentProcess().PeakWorkingSet64`), and the re-import time of the file through `TabularFile.Open` + reading every row (raise the xlsx/ods `MaxUncompressedBytes` options if the default refuses, and report that it did). Release build, net10.

- [ ] **Step 4: Documentation** — add to `docs/KNOWN-ISSUES.md` under `## Styles`:

```markdown
### Data by column

- `ColumnBatch` takes one list per column (arrays, `List<T>`, any `IReadOnlyList<T>` such as a protobuf repeated field) and writes them as rows, typed, without copying; reuse one batch per sheet — `Reset` keeps its slots.
- The lists must not change until the batch is written; `Reset` drops them.
- A style rule should return styles declared once (`static readonly`): the same instance costs a reference compare per cell. A rule that builds a new style per cell still writes a correct file, more slowly.
- A very wide xlsx (10,000 rows × 5,000 columns is about 1.5 GB of sheet XML) is past the reader's default `MaxUncompressedBytes`; raise it to read such a file back.
```

(Adjust the 1.5 GB figure to the measured uncompressed size from Step 3.)

- [ ] **Step 5: Full suite, format, commit**

```bash
git add tests docs
git commit -m "test(write): 1,000 × 5,000 batches read back in every format and allocate nothing per cell; document data by column"
```
