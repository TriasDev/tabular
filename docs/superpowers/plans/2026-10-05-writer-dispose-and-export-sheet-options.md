# Writer dispose and export sheet options Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** (#85) When a write has failed, a stream that also fails to close no longer hides the original exception. (#95) A `TabularExport<T>` can carry `SheetOptions` — header style, frozen panes, auto-filter — applied to every sheet it writes.

**Architecture:** `TabularWriter.DisposeAsync` closes the target in a guarded block when the file is not complete (the writer faulted, or was abandoned): an exception from closing is dropped, so the exception already on its way to the caller stays the one it sees. A completed file's close still throws. `TabularExportBuilder<T>.Sheet(SheetOptions)` stores the options in the built (immutable) export; every `WriteAsync`/`WriteSheetAsync` passes them to `BeginSheet`.

**Tech Stack:** C# / .NET (net8.0 + net10.0), BCL only; xunit v3.

**Decisions (user, 2026-10-05):** #85 — surface the original exception, drop the close failure (not an AggregateException). #95 — options set once on the builder, for every sheet the export writes (no per-call parameter).

## Global Constraints

- Public members in `src/TriasDev.Tabular/PublicAPI.Unshipped.txt`; analyzers warnings-as-errors; `dotnet format --verify-no-changes`.
- Nothing allocated per cell; no change to what a successful write produces.
- The repository is public: no product or customer names.

Commands: build `dotnet build -c Release`; tests `TABULAR_REQUIRE_SOFFICE=1 dotnet test -c Release`; one class `dotnet test --project tests/TriasDev.Tabular.Tests -c Release -- --filter-class "*Name"`.

## Review Focus

1. A client that goes away mid-write: `await using` around the writer surfaces the write's exception (e.g. `IOException`), not the stream's close exception. (Task 1)
2. A completed file whose stream then fails to close: that exception still reaches the caller — success must not be silent about a failed close. (Task 1)
3. `TabularExport.WriteAsync` (which owns its writer) behaves the same. (Task 1)
4. Export sheet options are validated when the export is built (a freeze past the export's columns fails at `Build`, not at the first write). (Task 2)
5. One built export with options serves several writers at once. (Task 2)

---

### Task 1: A failed write keeps its exception when the stream fails to close (#85)

**Files:** `src/TriasDev.Tabular/Writing/TabularWriter.cs` (`DisposeAsync`), tests `tests/TriasDev.Tabular.Tests/Writing/TabularWriterTests.cs` (or a new `WriterDisposeTests.cs`), `tests/TriasDev.Tabular.Tests/Fixtures/WriteTarget.cs` if it needs a "fail on dispose" switch.

- [ ] **Step 1: Failing tests.** Give `WriteTarget` a `FailOnDispose` exception property (thrown from `DisposeAsync` and `Dispose`), next to its existing `FailWith`. Tests:

```csharp
    [Fact]
    public async Task AFailedWriteKeepsItsExceptionWhenTheStreamAlsoFailsToClose()
    {
        WriteTarget target = new() { FailWith = new IOException("client went away"), FailOnDispose = new IOException("close failed") };

        IOException surfaced = await Assert.ThrowsAsync<IOException>(async () =>
        {
            await using TabularWriter writer = TabularWriter.Create(target, TabularFormat.Csv);
            writer.BeginSheet("data", [new("a")]);
            writer.BeginRow();
            writer.Write("x");
            writer.EndRow();
            await writer.FlushAsync(Token);
        });

        Assert.Equal("client went away", surfaced.Message);
    }

    [Fact]
    public async Task AnAbandonedFileDropsTheCloseFailure()
    {
        WriteTarget target = new() { FailOnDispose = new IOException("close failed") };
        TabularWriter writer = TabularWriter.Create(target, TabularFormat.Xlsx);
        writer.BeginSheet("data", [new("a")]);

        await writer.DisposeAsync();         // not completed: the file is incomplete anyway; nothing to add

        Assert.True(target.IsDisposed);
    }

    [Fact]
    public async Task ACompletedFileStillReportsAFailedClose()
    {
        WriteTarget target = new() { FailOnDispose = new IOException("close failed") };
        TabularWriter writer = TabularWriter.Create(target, TabularFormat.Csv);
        writer.BeginSheet("data", [new("a")]);
        await writer.CompleteAsync(Token);

        IOException surfaced = await Assert.ThrowsAsync<IOException>(async () => await writer.DisposeAsync());
        Assert.Equal("close failed", surfaced.Message);
    }

    [Fact]
    public async Task AnExportThatFailsKeepsItsException()
    {
        WriteTarget target = new() { FailWith = new IOException("client went away"), FailOnDispose = new IOException("close failed") };
        TabularExport<long> export = TabularExport.For<long>().Column("n", n => n).Build();

        IOException surfaced = await Assert.ThrowsAsync<IOException>(async () =>
            await export.WriteAsync(target, TabularFormat.Csv, "data", Enumerable.Range(0, 100_000).Select(i => (long)i), cancellationToken: Token));

        Assert.Equal("client went away", surfaced.Message);
    }
```

  (Adjust to `WriteTarget`'s real API — `FailWith`, `IsDisposed` exist; check how `FailWith` triggers and that the export's flush reaches the target within 100,000 rows; raise the count or make the target fail on the first write if needed. If `WriteTarget` records `DisposedSynchronously`, keep that working.)

- [ ] **Step 2:** run → the first and fourth tests fail with "close failed"; the second throws.

- [ ] **Step 3: Implement.** In `DisposeAsync`, remember whether the file was complete before setting `Disposed` (`bool complete = _state == State.Completed;`), and close the target through a helper:

```csharp
    /// <summary>
    /// Closes the target. When the file is not complete — the writer failed, or was abandoned — a
    /// failure to close is dropped: the caller is already handling (or has already been told about)
    /// what went wrong, and an exception from closing would replace it. A complete file's close
    /// failure is reported: it may mean the bytes did not all arrive.
    /// </summary>
    private async ValueTask CloseTargetAsync(bool complete)
    {
        if (complete)
        {
            await _target.DisposeAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            await _target.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception closing) when (closing is not OutOfMemoryException)
        {
            // Dropped on purpose; see the summary.
        }
    }
```

  Update `DisposeAsync`'s XML doc (one sentence on the dropped close failure). `TabularExport.WriteAsync` owns its writer through `await using`, so it is covered by the same change — the fourth test proves it.

- [ ] **Step 4:** run the class and the full suite → PASS. Note the behaviour in `docs/exporting.md`'s failures section (one sentence) and in `docs/KNOWN-ISSUES.md` if it lists dispose behaviour.

- [ ] **Step 5: Commit** `fix(write): a failed write keeps its exception when the stream also fails to close`.

---

### Task 2: Sheet options on a declared export (#95)

**Files:** `src/TriasDev.Tabular/Writing/TabularExportBuilder.cs`, `src/TriasDev.Tabular/Writing/TabularExport.cs`, `PublicAPI.Unshipped.txt`, tests `tests/TriasDev.Tabular.Tests/Writing/TabularExportSheetOptionsTests.cs`, `docs/exporting.md` + the sample if it shows the workaround.

- [ ] **Step 1: Failing tests.**

```csharp
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>A declared export carries its sheets' layout: header style, frozen panes, filter.</summary>
public sealed class TabularExportSheetOptionsTests
{
    private static readonly CellStyle Header = new() { Fill = CellColor.FromRgb(0x1F4E78), Font = new CellFont { Bold = true } };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed record Item(long Id, string Name);

    private static readonly TabularExport<Item> Export = TabularExport.For<Item>()
        .Column("Id", i => i.Id)
        .Column("Name", i => i.Name)
        .Sheet(new SheetOptions { HeaderStyle = Header, FreezeRows = 1, AutoFilter = true })
        .Build();

    [Fact]
    public async Task EverySheetTheExportWritesHasTheLayout()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Xlsx))
        {
            await Export.WriteSheetAsync(writer, "First", new[] { new Item(1, "a") }, Token);
            await Export.WriteSheetAsync(writer, "Second", new[] { new Item(2, "b"), new Item(3, "c") }, Token);
            await writer.CompleteAsync(Token);
        }

        byte[] xlsx = target.ToArray();
        Assert.Empty(OoxmlValidation.Errors(xlsx));

        foreach ((string part, string range) in new[] { ("xl/worksheets/sheet1.xml", "A1:B2"), ("xl/worksheets/sheet2.xml", "A1:B3") })
        {
            string sheet = SheetLayoutTests.Entry(xlsx, part);
            Assert.Contains("state=\"frozen\"", sheet, StringComparison.Ordinal);
            Assert.Contains($"<autoFilter ref=\"{range}\"/>", sheet, StringComparison.Ordinal);
            Assert.Matches("<c r=\"A1\" s=\"[1-9][0-9]*\" t=\"inlineStr\">", sheet);
        }
    }

    [Fact]
    public async Task WriteAsyncAppliesTheLayoutToo()
    {
        WriteTarget target = new();
        await Export.WriteAsync(target, TabularFormat.Ods, "data", new[] { new Item(1, "a") }, cancellationToken: Token);

        Assert.Contains("table:database-range", SheetLayoutTests.Entry(target.ToArray(), "content.xml"), StringComparison.Ordinal);
    }

    [Fact]
    public void AFreezePastTheExportsColumnsFailsAtBuild() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TabularExport.For<Item>()
            .Column("Id", i => i.Id)
            .Sheet(new SheetOptions { FreezeColumns = 2 })
            .Build());

    [Fact]
    public void ANegativeFreezeFailsAtBuild() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TabularExport.For<Item>()
            .Column("Id", i => i.Id)
            .Sheet(new SheetOptions { FreezeRows = -1 })
            .Build());

    [Fact]
    public async Task OneExportWithALayoutServesConcurrentWriters()
    {
        Item[] items = [.. Enumerable.Range(1, 2_000).Select(i => new Item(i, $"n{i}"))];

        byte[][] files = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            WriteTarget target = new();
            await Export.WriteAsync(target, TabularFormat.Xlsx, "data", items, cancellationToken: Token);
            return target.ToArray();
        }));

        foreach (byte[] file in files)
        {
            Assert.Equal(files[0], file);
        }
    }

    [Fact]
    public async Task AnExportWithoutALayoutWritesWhatItWroteBefore()
    {
        TabularExport<Item> plain = TabularExport.For<Item>().Column("Id", i => i.Id).Column("Name", i => i.Name).Build();
        WriteTarget a = new();
        WriteTarget b = new();
        await plain.WriteAsync(a, TabularFormat.Xlsx, "data", new[] { new Item(1, "a") }, cancellationToken: Token);
        await TabularExport.For<Item>().Column("Id", i => i.Id).Column("Name", i => i.Name).Sheet(new SheetOptions()).Build()
            .WriteAsync(b, TabularFormat.Xlsx, "data", new[] { new Item(1, "a") }, cancellationToken: Token);

        Assert.Equal(a.ToArray(), b.ToArray());
    }
}
```

- [ ] **Step 2:** run → compile error (`Sheet` missing).

- [ ] **Step 3: Implement.**
  - Builder: `public TabularExportBuilder<T> Sheet(SheetOptions options)` — null → `ArgumentNullException`; calling it twice replaces (document). Store in a field.
  - `Build()`: validate the freeze against the declared columns: `FreezeRows` ≥ 0 and below 1,048,576 (the workbook formats' row limit; csv ignores layout), `FreezeColumns` 0 … column count; otherwise `ArgumentOutOfRangeException` naming the option — same messages as `TabularWriter.BeginSheet`'s checks (extract a shared internal `SheetOptions` check used by both so the rules cannot drift; keep `BeginSheet`'s behaviour, which also knows the format's actual row limit).
  - `TabularExport<T>`: an internal `SheetOptions? _sheet` passed from `Build`; every `writer.BeginSheet(sheetName, _declared)` becomes `writer.BeginSheet(sheetName, _declared, _sheet)`. Expose it read-only (`public SheetOptions? Sheet { get; }`) only if the export already exposes its `Columns` (it does) — keep the shape consistent.
  - XML docs on `Sheet(...)`: "Lays out every sheet the export writes: header style, frozen rows and columns, auto-filter. Csv ignores it."

- [ ] **Step 4:** run the new class, the export test classes and the full suite → PASS. Public API lines.

- [ ] **Step 5: Docs.** `docs/exporting.md`: where it says a declared export cannot style its header (and points to `TabularWriter`), show `.Sheet(...)` instead — through the sample (`samples/TriasDev.Tabular.Samples.Export`), keeping snippet includes; keep the README snippet identical to its sample region if it changes. `mkdocs build --strict` passes.

- [ ] **Step 6: Commit** `feat(write): a declared export carries its sheets' layout (SheetOptions)`.
