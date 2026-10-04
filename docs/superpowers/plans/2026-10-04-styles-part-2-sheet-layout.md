# Styled export, part 2 — sheet layout Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A sheet can have a styled header row, frozen rows and columns, an auto-filter on its header, and merged cells; ods writes runs of empty cells as one repeated cell.

**Architecture:** `BeginSheet(name, columns, SheetOptions?)` carries the header style, freeze and filter; the format writers state them where their file format wants them (xlsx `sheetViews` before the data, `autoFilter` and a defined name after; ods `settings.xml` and `table:database-ranges` at the end). `writer.Merge(rows, columns)` declares that the next cell is the top-left of a merged range; `TabularWriter` keeps one covered interval per column, skips covered positions automatically and tells the format writer to write each one (`WriteCovered`), so merges cost nothing per cell when a sheet has none.

**Tech Stack:** C# / .NET (net8.0 + net10.0), BCL only; xunit v3 on Microsoft.Testing.Platform; OpenXml validator (tests); LibreOffice headless (interop tests).

**Spec:** `docs/superpowers/specs/2026-10-04-styled-columnar-export-design.md` §2 (part 1, §1, is merged: `CellStyle`, `StyleId`, `StyleTable`, `XlsxStyles`, `OdsStyles`). Issue #87.

## Global Constraints

- One package, no dependencies; public types in namespace `TriasDev.Tabular` under `src/TriasDev.Tabular/Writing/`; every public member in `src/TriasDev.Tabular/PublicAPI.Unshipped.txt` (copy the RS0016 lines).
- Analyzers are warnings-as-errors; `dotnet format --verify-no-changes` must pass.
- Nothing allocated per cell; a sheet without merges pays at most one null check per cell for them.
- `Merge(rows, columns)`: rows ≥ 1, columns ≥ 1, not 1 × 1 → otherwise `ArgumentOutOfRangeException`; a second `Merge` before a cell, a `Merge` no cell followed (at `EndRow`), a range overlapping another or reaching past the sheet's columns (at the top-left cell's write), a sheet ended while a range still has rows to cover (at `BeginSheet` / `CompleteAsync`) → `InvalidOperationException`. Every refusal faults the writer, as every writer refusal does today.
- Covered positions are skipped automatically: after the top-left cell the next write lands at column + `columns`; in later rows, covered positions are written by the writer when the row reaches them, and by `EndRow` for the rest of the row.
- xlsx: at most **65,536** merges per sheet → `TabularLimitException` (limit name `"MaxMerges"`); `<mergeCells>` after `<autoFilter>` after `</sheetData>`; freeze is `<sheetViews>` before `<cols>`; the filter also gets the hidden defined name `_xlnm._FilterDatabase` (`localSheetId` = zero-based sheet index) in `workbook.xml`.
- ods: spans are `table:number-columns-spanned` / `table:number-rows-spanned` on the top-left cell and `<table:covered-table-cell/>` for covered positions; freeze in `settings.xml` (listed in the manifest only when written); filters as `table:database-ranges` after the last table in `content.xml`; a run of unstyled empty cells is one `<table:table-cell table:number-columns-repeated="n"/>`.
- csv: header style, freeze, filter ignored; a merge writes its value in the top-left cell and empty fields for covered positions.
- `SheetOptions`: `FreezeRows` 0 … (format's max rows − 1), `FreezeColumns` 0 … the sheet's column count, otherwise `ArgumentOutOfRangeException` from `BeginSheet`. `HeaderStyle` is registered like any style (counts toward the 4096).
- The repository is public: no product or customer names anywhere.

Commands: build `dotnet build -c Release`; one class `dotnet test --project tests/TriasDev.Tabular.Tests -c Release -- --filter-class "*Name"`; full suite `TABULAR_REQUIRE_SOFFICE=1 dotnet test -c Release`; format `dotnet format --verify-no-changes`.

## Review Focus

1. A vertical merge in a column the caller never reaches in the rows below (the row ends early with `EndRow`): the covered cells are still written, the file valid, the import reads empties there. (Task 3, Task 4, Task 5)
2. A merge reaching past the last row written: `CompleteAsync` and the next `BeginSheet` refuse instead of writing a broken file. (Task 3)
3. A sheet name with an apostrophe and a space under an auto-filter: the xlsx defined name and the ods range address quote it correctly and the file opens in LibreOffice with the filter. (Task 2)
4. A merged range whose top-left cell is styled with a fill: the import reads the value once and empties elsewhere; LibreOffice keeps the merge. (Task 4, Task 5)
5. Two sheets with different freeze and filter settings: each sheet keeps its own. (Task 1, Task 2)

---

### Task 1: `SheetOptions` — header style and freeze panes

**Files:**
- Create: `src/TriasDev.Tabular/Writing/SheetOptions.cs`
- Modify: `src/TriasDev.Tabular/Writing/TabularWriter.cs`, `src/TriasDev.Tabular/Writing/ISheetWriter.cs`, `src/TriasDev.Tabular/Csv/CsvSheetWriter.cs`, `src/TriasDev.Tabular/Xlsx/XlsxSheetWriter.cs`, `src/TriasDev.Tabular/Ods/OdsSheetWriter.cs`, `src/TriasDev.Tabular/Ods/OdsParts.cs`, `PublicAPI.Unshipped.txt`
- Test: `tests/TriasDev.Tabular.Tests/Writing/SheetLayoutTests.cs`

**Interfaces:**
- Consumes: `StyleTable.Add(CellStyle)` (returns the index), `XlsxStyles.Xf(int, ValueKind)`, `OdsStyles.Cell(int, ValueKind)`, `ValueKind.Text`.
- Produces:
  - `public sealed record SheetOptions { CellStyle? HeaderStyle; int FreezeRows; int FreezeColumns; bool AutoFilter; }` (all `init`; `AutoFilter` is used by Task 2).
  - `TabularWriter.BeginSheet(string name, ReadOnlySpan<WriteColumn> columns, SheetOptions? options)`; the old two-argument overload calls it with `null`.
  - `ISheetWriter.BeginSheet(string name, ReadOnlySpan<WriteColumn> columns, SheetOptions options)` (never null: the writer passes `SheetOptions.Default`, an internal static instance) and `ISheetWriter.WriteHeader(string value, int style)`.
  - `OdsParts.Manifest(bool settings)` (method replacing the `Manifest` bytes) and `OdsParts.Settings(IReadOnlyList<(string Name, int Rows, int Columns)> frozen)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.IO.Compression;
using System.Text;

using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>How a sheet is laid out: header style, frozen panes, filter, merges.</summary>
public sealed class SheetLayoutTests
{
    private static readonly CellStyle Header = new() { Fill = CellColor.FromRgb(0x1F4E78), Font = new CellFont { Color = CellColor.FromRgb(0xFFFFFF), Bold = true } };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    internal static async Task<byte[]> Write(TabularFormat format, Action<TabularWriter> write)
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, format))
        {
            write(writer);
            await writer.CompleteAsync(Token);
        }

        return target.ToArray();
    }

    internal static string Entry(byte[] zip, string name)
    {
        using ZipArchive archive = new(new MemoryStream(zip, writable: false), ZipArchiveMode.Read);
        ZipArchiveEntry? entry = archive.GetEntry(name);
        Assert.NotNull(entry);
        using StreamReader reader = new(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    internal static bool HasEntry(byte[] zip, string name)
    {
        using ZipArchive archive = new(new MemoryStream(zip, writable: false), ZipArchiveMode.Read);
        return archive.GetEntry(name) is not null;
    }

    internal static List<RawCell[]> Rows(byte[] file, int sheet = 0)
    {
        using ITabularCursor cursor = TabularFile.Open(new MemoryStream(file, writable: false), "file", cancellationToken: Token);
        Assert.True(cursor.MoveToSheet(sheet, Token));
        List<RawCell[]> rows = [];

        while (cursor.ReadRow(Token))
        {
            rows.Add(cursor.CurrentRow.ToArray());
        }

        return rows;
    }

    private static void Frozen(TabularWriter writer)
    {
        writer.BeginSheet("data", [new("a"), new("b"), new("c")], new SheetOptions { HeaderStyle = Header, FreezeRows = 1, FreezeColumns = 1 });
        writer.BeginRow();
        writer.Write("x");
        writer.Write(1L);
        writer.Write(2L);
        writer.EndRow();
        writer.BeginSheet("rows only", [new("a")], new SheetOptions { FreezeRows = 2 });
        writer.BeginSheet("plain", [new("a")]);
    }

    [Fact]
    public async Task XlsxFreezesEachSheetAsAsked()
    {
        byte[] xlsx = await Write(TabularFormat.Xlsx, Frozen);

        Assert.Empty(OoxmlValidation.Errors(xlsx));
        Assert.Contains("<sheetViews><sheetView workbookViewId=\"0\"><pane xSplit=\"1\" ySplit=\"1\" topLeftCell=\"B2\" activePane=\"bottomRight\" state=\"frozen\"/><selection pane=\"bottomRight\"/></sheetView></sheetViews>", Entry(xlsx, "xl/worksheets/sheet1.xml"), StringComparison.Ordinal);
        Assert.Contains("<pane ySplit=\"2\" topLeftCell=\"A3\" activePane=\"bottomLeft\" state=\"frozen\"/><selection pane=\"bottomLeft\"/>", Entry(xlsx, "xl/worksheets/sheet2.xml"), StringComparison.Ordinal);
        Assert.DoesNotContain("<sheetViews>", Entry(xlsx, "xl/worksheets/sheet3.xml"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OdsFreezesEachSheetInItsSettings()
    {
        byte[] ods = await Write(TabularFormat.Ods, Frozen);
        string settings = Entry(ods, "settings.xml");

        Assert.Contains("<config:config-item-map-entry config:name=\"data\"><config:config-item config:name=\"HorizontalSplitMode\" config:type=\"short\">2</config:config-item><config:config-item config:name=\"VerticalSplitMode\" config:type=\"short\">2</config:config-item><config:config-item config:name=\"HorizontalSplitPosition\" config:type=\"int\">1</config:config-item><config:config-item config:name=\"VerticalSplitPosition\" config:type=\"int\">1</config:config-item>", settings, StringComparison.Ordinal);
        Assert.Contains("config:name=\"rows only\"><config:config-item config:name=\"HorizontalSplitMode\" config:type=\"short\">0</config:config-item><config:config-item config:name=\"VerticalSplitMode\" config:type=\"short\">2</config:config-item>", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("config:name=\"plain\"", settings, StringComparison.Ordinal);
        Assert.Contains("manifest:full-path=\"settings.xml\"", Entry(ods, "META-INF/manifest.xml"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OdsWithoutFrozenPanesHasNoSettings()
    {
        byte[] ods = await Write(TabularFormat.Ods, writer => writer.BeginSheet("plain", [new("a")]));

        Assert.False(HasEntry(ods, "settings.xml"));
        Assert.DoesNotContain("settings.xml", Entry(ods, "META-INF/manifest.xml"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TabularFormat.Xlsx)]
    [InlineData(TabularFormat.Ods)]
    [InlineData(TabularFormat.Csv)]
    public async Task AStyledHeaderReadsBackAsTheSameHeader(TabularFormat format)
    {
        byte[] styled = await Write(format, writer =>
        {
            writer.BeginSheet("data", [new("Name"), new("Count")], new SheetOptions { HeaderStyle = Header });
            writer.BeginRow();
            writer.Write("x");
            writer.Write(1L);
            writer.EndRow();
        });

        List<RawCell[]> rows = Rows(styled);
        Assert.Equal([RawCell.FromText("Name"), RawCell.FromText("Count")], rows[0]);
        Assert.Equal(RawCell.FromNumber(1), rows[1][1]);
    }

    [Fact]
    public async Task TheXlsxHeaderCarriesItsStyle()
    {
        byte[] xlsx = await Write(TabularFormat.Xlsx, writer => writer.BeginSheet("data", [new("Name")], new SheetOptions { HeaderStyle = Header }));

        Assert.Contains("<c r=\"A1\" s=\"4\" t=\"inlineStr\">", Entry(xlsx, "xl/worksheets/sheet1.xml"), StringComparison.Ordinal);
        Assert.Contains("<fgColor rgb=\"FF1F4E78\"/>", Entry(xlsx, "xl/styles.xml"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheOdsHeaderCarriesItsStyle()
    {
        byte[] ods = await Write(TabularFormat.Ods, writer => writer.BeginSheet("data", [new("Name")], new SheetOptions { HeaderStyle = Header }));

        Assert.Contains("<table:table-cell table:style-name=\"ts1\" office:value-type=\"string\">", Entry(ods, "content.xml"), StringComparison.Ordinal);
        Assert.Contains("fo:background-color=\"#1F4E78\"", Entry(ods, "styles.xml"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 4)]
    [InlineData(1_048_576, 0)]
    public async Task AFreezeOutsideTheSheetIsRefused(int rows, int columns)
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);

        Assert.Throws<ArgumentOutOfRangeException>(() => writer.BeginSheet("data", [new("a"), new("b"), new("c")], new SheetOptions { FreezeRows = rows, FreezeColumns = columns }));
    }

    [Fact]
    public async Task CsvIgnoresTheLayout()
    {
        byte[] plain = await Write(TabularFormat.Csv, writer => writer.BeginSheet("data", [new("a"), new("b")]));
        byte[] laidOut = await Write(TabularFormat.Csv, writer => writer.BeginSheet("data", [new("a"), new("b")], new SheetOptions { HeaderStyle = Header, FreezeRows = 1, FreezeColumns = 1, AutoFilter = true }));

        Assert.Equal(plain, laidOut);
    }
}
```

`[InlineData(0, 4)]`: four frozen columns on a three-column sheet is refused (`FreezeColumns` may equal the column count, not exceed it). `[InlineData(1_048_576, 0)]`: xlsx holds 1,048,576 rows, so at most 1,048,575 can be frozen.

The xlsx header assertion expects `s="4"`: the header style is the first registered, on a text cell, and the four fixed cell formats take 0–3.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet build -c Release` → compile errors (`SheetOptions` missing).

- [ ] **Step 3: Implement `SheetOptions`**

```csharp
namespace TriasDev.Tabular;

/// <summary>
/// How a sheet is laid out beyond its values: a style for its header row, rows and columns frozen
/// in view, an auto-filter on its header. Csv ignores all of it.
/// </summary>
public sealed record SheetOptions
{
    /// <summary>The options a sheet has when none are given.</summary>
    internal static readonly SheetOptions Default = new();

    /// <summary>The header row's style; null leaves it unstyled.</summary>
    public CellStyle? HeaderStyle { get; init; }

    /// <summary>How many rows from the top stay in view while scrolling — 1 keeps the header. 0 freezes none.</summary>
    public int FreezeRows { get; init; }

    /// <summary>How many columns from the left stay in view while scrolling. 0 freezes none.</summary>
    public int FreezeColumns { get; init; }

    /// <summary>An auto-filter on the header row, covering every row written.</summary>
    public bool AutoFilter { get; init; }
}
```

- [ ] **Step 4: Wire `TabularWriter.BeginSheet`**

Replace the two-argument `BeginSheet` with a forwarder and move the body into the new overload:

```csharp
    /// <summary>Begins a sheet and writes its header row.</summary>
    /// (keep the existing <param> docs for name and columns)
    public void BeginSheet(string name, ReadOnlySpan<WriteColumn> columns) => BeginSheet(name, columns, null);

    /// <summary>Begins a sheet laid out by <paramref name="options"/> and writes its header row.</summary>
    /// <param name="name">(as above)</param>
    /// <param name="columns">(as above)</param>
    /// <param name="options">The header style, frozen rows and columns, and filter; null for none.</param>
    /// <exception cref="ArgumentOutOfRangeException">A freeze outside the sheet: rows from 0 to the format's row limit less one, columns from 0 to the column count.</exception>
    public void BeginSheet(string name, ReadOnlySpan<WriteColumn> columns, SheetOptions? options)
```

In the body, after `CheckColumns(columns);`:

```csharp
        SheetOptions layout = options ?? SheetOptions.Default;

        if (layout.FreezeRows < 0 || layout.FreezeRows >= _sheet.MaxRows)
        {
            throw Faulting(new ArgumentOutOfRangeException(nameof(options), layout.FreezeRows, $"A sheet freezes 0 to {_sheet.MaxRows - 1} rows."));
        }

        if (layout.FreezeColumns < 0 || layout.FreezeColumns > columns.Length)
        {
            throw Faulting(new ArgumentOutOfRangeException(nameof(options), layout.FreezeColumns, $"A sheet of {columns.Length} columns freezes 0 to {columns.Length} of them."));
        }

        int headerStyle = layout.HeaderStyle is { } header ? RegisterStyle(header) : 0;
```

`RegisterStyle` is the body of `Style()` (the `_styles.Add` call with its fault-on-refusal `try/catch`), extracted so both use it; `Style()` becomes `ExpectWritable(); return new StyleId(_stamp, RegisterStyle(style));`.

Then `_sheet.BeginSheet(name, _columns, layout);` and the header loop calls `_sheet.WriteHeader(header, headerStyle)`.

- [ ] **Step 5: Format writers**

`ISheetWriter`: `void BeginSheet(string name, ReadOnlySpan<WriteColumn> columns, SheetOptions options);` and `string? WriteHeader(string value, int style);` (doc: "options is never null; csv ignores it"; "style: the header row's style index, 0 for none").

Csv: add the parameters, ignore them.

Xlsx (`XlsxSheetWriter`):
- `WriteHeader(string value, int style) => WriteInline(value, style == 0 ? 0 : _styles.Xf(style, ValueKind.Text));`
- In `BeginSheet`, between `_row.Append(XlsxParts.WorksheetStart);` and `AppendWidths(columns);`, call `AppendFreeze(options.FreezeRows, options.FreezeColumns);`:

```csharp
    /// <summary>Freezes the top rows and left columns: a split pane, in the state Excel writes for "Freeze Panes".</summary>
    private void AppendFreeze(int rows, int columns)
    {
        if (rows == 0 && columns == 0)
        {
            return;
        }

        string pane = (rows, columns) switch
        {
            (> 0, > 0) => "bottomRight",
            (> 0, _) => "bottomLeft",
            _ => "topRight",
        };

        _row.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane");

        if (columns > 0)
        {
            _row.Append(" xSplit=\"");
            _row.AppendFormatted(columns, default, CultureInfo.InvariantCulture);
            _row.Append('"');
        }

        if (rows > 0)
        {
            _row.Append(" ySplit=\"");
            _row.AppendFormatted(rows, default, CultureInfo.InvariantCulture);
            _row.Append('"');
        }

        _row.Append(" topLeftCell=\"");
        _row.Append(XlsxParts.ColumnName(columns));
        _row.AppendFormatted(rows + 1, default, CultureInfo.InvariantCulture);
        _row.Append("\" activePane=\"");
        _row.Append(pane);
        _row.Append("\" state=\"frozen\"/><selection pane=\"");
        _row.Append(pane);
        _row.Append("\"/></sheetView></sheetViews>");
    }
```

  `XlsxParts.ColumnName(columns)` when `columns` equals the sheet's column count and that is 16,384 would be out of range: `ColumnName` accepts 0–16,383. Clamp: `XlsxParts.ColumnName(Math.Min(columns, 16_383))` — a pane whose top-left cell is the last column is what Excel writes for that case.

Ods (`OdsSheetWriter`):
- `WriteHeader(string value, int style) => WriteString(value, style);`
- A field `private readonly List<(string Name, int Rows, int Columns)> _frozen = [];`; in `BeginSheet`, when `options.FreezeRows > 0 || options.FreezeColumns > 0`, add `(name, options.FreezeRows, options.FreezeColumns)`.
- `Complete()`: write `settings.xml` (stored) when `_frozen.Count > 0`: `_zip.AddStored("settings.xml", OdsParts.Settings(_frozen));`, and the manifest as `OdsParts.Manifest(settings: _frozen.Count > 0)`.

`OdsParts`: replace the `Manifest` bytes with

```csharp
    public static byte[] Manifest(bool settings) => Encoding.UTF8.GetBytes(
        XmlDeclaration
        + "<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"1.3\">"
        + "<manifest:file-entry manifest:full-path=\"/\" manifest:version=\"1.3\" manifest:media-type=\"" + Mimetype + "\"/>"
        + "<manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/>"
        + "<manifest:file-entry manifest:full-path=\"styles.xml\" manifest:media-type=\"text/xml\"/>"
        + (settings ? "<manifest:file-entry manifest:full-path=\"settings.xml\" manifest:media-type=\"text/xml\"/>" : string.Empty)
        + "</manifest:manifest>");
```

and add

```csharp
    /// <summary>
    /// The view settings that freeze panes, as LibreOffice stores them: per sheet, a split mode of 2
    /// (frozen) and the split position in rows or columns, the bottom-right part active.
    /// </summary>
    public static byte[] Settings(IReadOnlyList<(string Name, int Rows, int Columns)> frozen)
    {
        StringBuilder xml = new(
            XmlDeclaration
            + "<office:document-settings xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:config=\"urn:oasis:names:tc:opendocument:xmlns:config:1.0\" office:version=\"1.3\">"
            + "<office:settings><config:config-item-set config:name=\"ooo:view-settings\"><config:config-item-map-indexed config:name=\"Views\"><config:config-item-map-entry>"
            + "<config:config-item config:name=\"ViewId\" config:type=\"string\">view1</config:config-item><config:config-item-map-named config:name=\"Tables\">");

        foreach ((string name, int rows, int columns) in frozen)
        {
            xml.Append("<config:config-item-map-entry config:name=\"");
            AppendAttribute(xml, name);
            xml.Append("\">");
            Item(xml, "HorizontalSplitMode", "short", columns > 0 ? 2 : 0);
            Item(xml, "VerticalSplitMode", "short", rows > 0 ? 2 : 0);
            Item(xml, "HorizontalSplitPosition", "int", columns);
            Item(xml, "VerticalSplitPosition", "int", rows);
            Item(xml, "ActiveSplitRange", "short", 2);
            Item(xml, "PositionLeft", "int", 0);
            Item(xml, "PositionRight", "int", columns);
            Item(xml, "PositionTop", "int", 0);
            Item(xml, "PositionBottom", "int", rows);
            xml.Append("</config:config-item-map-entry>");
        }

        xml.Append("</config:config-item-map-named></config:config-item-map-entry></config:config-item-map-indexed></config:config-item-set></office:settings></office:document-settings>");
        return Encoding.UTF8.GetBytes(xml.ToString());
    }

    private static void Item(StringBuilder xml, string name, string type, int value) =>
        xml.Append(CultureInfo.InvariantCulture, $"<config:config-item config:name=\"{name}\" config:type=\"{type}\">{value}</config:config-item>");

    private static void AppendAttribute(StringBuilder xml, string value)
    {
        foreach (char c in value)
        {
            _ = c switch
            {
                '&' => xml.Append("&amp;"),
                '<' => xml.Append("&lt;"),
                '>' => xml.Append("&gt;"),
                '"' => xml.Append("&quot;"),
                _ => xml.Append(c),
            };
        }
    }
```

(Sheet names never hold control characters — `SheetNames.Problem` refuses them — so no character references are needed here.)

- [ ] **Step 6: Run, then add the LibreOffice check**

Run `--filter-class "*SheetLayoutTests"` → PASS; then add to `tests/TriasDev.Tabular.Tests/Writing/StyledInteropTests.cs`:

```csharp
    [Fact]
    public async Task LibreOfficeKeepsTheFrozenPanesOfAnOds()
    {
        byte[] ods = await SheetLayoutTests.Write(TabularFormat.Ods, writer => writer.BeginSheet("data", [new("a"), new("b")], new SheetOptions { FreezeRows = 1, FreezeColumns = 1 }));
        string sheet = Entry(LibreOffice.Convert(ods, "ods", "xlsx:Calc MS Excel 2007 XML", "xlsx"), "xl/worksheets/sheet1.xml");

        Assert.Contains("state=\"frozen\"", sheet, StringComparison.Ordinal);
        Assert.Matches("ySplit=\"1(\\.0)?\"", sheet);
    }

    [Fact]
    public async Task LibreOfficeKeepsTheFrozenPanesOfAnXlsx()
    {
        byte[] xlsx = await SheetLayoutTests.Write(TabularFormat.Xlsx, writer => writer.BeginSheet("data", [new("a"), new("b")], new SheetOptions { FreezeRows = 1 }));
        string settings = Entry(LibreOffice.Convert(xlsx, "xlsx", "ods", "ods"), "settings.xml");

        Assert.Contains("<config:config-item config:name=\"VerticalSplitPosition\" config:type=\"int\">1</config:config-item>", settings, StringComparison.Ordinal);
    }
```

Run `TABULAR_REQUIRE_SOFFICE=1 … --filter-class "*StyledInteropTests"` → PASS, then the full suite.

- [ ] **Step 7: Public API, format, commit**

```bash
git add src tests
git commit -m "feat(write): SheetOptions — a styled header row and frozen panes for xlsx and ods"
```

---

### Task 2: Auto-filter

**Files:**
- Modify: `src/TriasDev.Tabular/Xlsx/XlsxSheetWriter.cs`, `src/TriasDev.Tabular/Xlsx/XlsxParts.cs`, `src/TriasDev.Tabular/Ods/OdsSheetWriter.cs`, `src/TriasDev.Tabular/Ods/OdsParts.cs`
- Test: `tests/TriasDev.Tabular.Tests/Writing/SheetLayoutTests.cs`, `tests/TriasDev.Tabular.Tests/Writing/StyledInteropTests.cs`

**Interfaces:**
- Consumes: `SheetOptions.AutoFilter` (Task 1); `ISheetWriter.BeginSheet(…, SheetOptions)`.
- Produces: `XlsxParts.WorksheetEnd` split into `XlsxParts.SheetDataEnd = "</sheetData>"` and `XlsxParts.WorksheetClose = "</worksheet>"`; `XlsxParts.Workbook(IReadOnlyList<string> sheetNames, IReadOnlyList<(int Sheet, string Range)> filters)`; `OdsParts.ContentEnd` split so the database ranges go between the last `</table:table>` and `</office:spreadsheet>`. Task 4 inserts `<mergeCells>` after the `autoFilter` in the same `CloseSheet`.

- [ ] **Step 1: Write the failing tests** (append to `SheetLayoutTests`)

```csharp
    private static void Filtered(TabularWriter writer)
    {
        writer.BeginSheet("Bob's data", [new("a"), new("b"), new("c")], new SheetOptions { AutoFilter = true });

        for (int i = 0; i < 3; i++)
        {
            writer.BeginRow();
            writer.Write((long)i);
            writer.EndRow();
        }

        writer.BeginSheet("plain", [new("a")]);
        writer.BeginSheet("second", [new("a"), new("b")], new SheetOptions { AutoFilter = true });
    }

    [Fact]
    public async Task XlsxFiltersTheHeaderThroughTheLastRow()
    {
        byte[] xlsx = await Write(TabularFormat.Xlsx, Filtered);

        Assert.Empty(OoxmlValidation.Errors(xlsx));
        Assert.Contains("</sheetData><autoFilter ref=\"A1:C4\"/></worksheet>", Entry(xlsx, "xl/worksheets/sheet1.xml"), StringComparison.Ordinal);
        Assert.DoesNotContain("autoFilter", Entry(xlsx, "xl/worksheets/sheet2.xml"), StringComparison.Ordinal);
        Assert.Contains("<autoFilter ref=\"A1:B1\"/>", Entry(xlsx, "xl/worksheets/sheet3.xml"), StringComparison.Ordinal);

        string workbook = Entry(xlsx, "xl/workbook.xml");
        Assert.Contains("<definedNames><definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"0\" hidden=\"1\">'Bob''s data'!$A$1:$C$4</definedName><definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"2\" hidden=\"1\">'second'!$A$1:$B$1</definedName></definedNames>", workbook, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OdsFiltersTheHeaderThroughTheLastRow()
    {
        byte[] ods = await Write(TabularFormat.Ods, Filtered);
        string content = Entry(ods, "content.xml");

        Assert.Contains("</table:table><table:database-ranges><table:database-range table:name=\"__Anonymous_Sheet_DB__0\" table:target-range-address=\"'Bob''s data'.A1:'Bob''s data'.C4\" table:display-filter-buttons=\"true\"/><table:database-range table:name=\"__Anonymous_Sheet_DB__2\" table:target-range-address=\"'second'.A1:'second'.B1\" table:display-filter-buttons=\"true\"/></table:database-ranges></office:spreadsheet>", content, StringComparison.Ordinal);
        Assert.Equal(4, Rows(ods).Count);
    }
```

The xlsx workbook test pins the order inside `<workbook>`: `<sheets>…</sheets><definedNames>…</definedNames>` (CT_Workbook puts `definedNames` after `sheets`).

- [ ] **Step 2: Run to verify they fail** → FAIL.

- [ ] **Step 3: Xlsx**

- Per sheet keep `_filter` (bool) and the column count (`_columnNames.Length`). `CloseSheet()` writes `XlsxParts.SheetDataEnd`, then, when filtering, `<autoFilter ref="A1:{last column}{last row}"/>` where the last row is `_rowNumber` (the header alone gives row 1), then — Task 4 — merges, then `XlsxParts.WorksheetClose`. Record the filter for the workbook: `_filters.Add((_sheetNames.Count - 1, $"${XlsxParts.ColumnName(0)}$1:${lastColumn}${_rowNumber}"))` — written as `$A$1:$C$4`.
- `XlsxParts.Workbook(sheetNames, filters)`: after `</sheets>`, when `filters.Count > 0`, append `<definedNames>` and for each `<definedName name="_xlnm._FilterDatabase" localSheetId="{Sheet}" hidden="1">'{name with ' doubled, XML-escaped}'!{Range}</definedName>` then `</definedNames>`. Keep the existing escaping helper for the name.

- [ ] **Step 4: Ods**

- Count rows per sheet in `OdsSheetWriter` (`_rowNumber`, reset in `BeginSheet`, incremented in `BeginRow`), and the sheet index.
- On a sheet's end (the next `BeginSheet` before writing `</table:table>`, and `Complete`) record a filtered sheet: `_filters.Add((index, name, columns, _rowNumber))`.
- `OdsParts`: replace `ContentEnd` with `TableEnd = "</table:table>"` and `SpreadsheetEnd = "</office:spreadsheet></office:body></office:document-content>"`, and add

```csharp
    /// <summary>The auto-filters, as LibreOffice's sheet-local anonymous database ranges, header through last row.</summary>
    public static string DatabaseRanges(IReadOnlyList<(int Sheet, string Name, int Columns, long Rows)> filters)
    {
        if (filters.Count == 0)
        {
            return string.Empty;
        }

        StringBuilder xml = new("<table:database-ranges>");

        foreach ((int sheet, string name, int columns, long rows) in filters)
        {
            string quoted = "'" + name.Replace("'", "''", StringComparison.Ordinal) + "'";
            xml.Append(CultureInfo.InvariantCulture, $"<table:database-range table:name=\"__Anonymous_Sheet_DB__{sheet}\" table:target-range-address=\"");
            AppendAttribute(xml, $"{quoted}.A1:{quoted}.{Xlsx.XlsxParts.ColumnName(columns - 1)}{rows}");
            xml.Append("\" table:display-filter-buttons=\"true\"/>");
        }

        return xml.Append("</table:database-ranges>").ToString();
    }
```

  (`using TriasDev.Tabular.Xlsx;` is already in `OdsSheetWriter`; in `OdsParts` qualify or add the using.) `Complete()` writes `TableEnd + DatabaseRanges(_filters) + SpreadsheetEnd`.

- [ ] **Step 5: LibreOffice** (append to `StyledInteropTests`)

```csharp
    [Fact]
    public async Task LibreOfficeKeepsTheFilterOfAnOds()
    {
        byte[] ods = await SheetLayoutTests.Write(TabularFormat.Ods, writer =>
        {
            writer.BeginSheet("Bob's data", [new("a"), new("b")], new SheetOptions { AutoFilter = true });
            writer.BeginRow();
            writer.Write(1L);
            writer.EndRow();
        });

        Assert.Contains("<autoFilter ref=\"A1:B2\"", Entry(LibreOffice.Convert(ods, "ods", "xlsx:Calc MS Excel 2007 XML", "xlsx"), "xl/worksheets/sheet1.xml"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LibreOfficeKeepsTheFilterOfAnXlsx()
    {
        byte[] xlsx = await SheetLayoutTests.Write(TabularFormat.Xlsx, writer =>
        {
            writer.BeginSheet("Bob's data", [new("a"), new("b")], new SheetOptions { AutoFilter = true });
            writer.BeginRow();
            writer.Write(1L);
            writer.EndRow();
        });

        Assert.Contains("table:display-filter-buttons=\"true\"", Entry(LibreOffice.Convert(xlsx, "xlsx", "ods", "ods"), "content.xml"), StringComparison.Ordinal);
    }
```

- [ ] **Step 6: Run** the two classes (`TABULAR_REQUIRE_SOFFICE=1`) and the full suite → PASS.

- [ ] **Step 7: Commit**

```bash
git add src tests
git commit -m "feat(write): auto-filter on the header row for xlsx and ods"
```

---

### Task 3: Merges in the writer, and csv

**Files:**
- Modify: `src/TriasDev.Tabular/Writing/TabularWriter.cs`, `src/TriasDev.Tabular/Writing/ISheetWriter.cs`, `src/TriasDev.Tabular/Csv/CsvSheetWriter.cs`, `src/TriasDev.Tabular/Xlsx/XlsxSheetWriter.cs`, `src/TriasDev.Tabular/Ods/OdsSheetWriter.cs`, `PublicAPI.Unshipped.txt`
- Test: `tests/TriasDev.Tabular.Tests/Writing/MergeTests.cs`

**Interfaces:**
- Produces:
  - `public void TabularWriter.Merge(int rows, int columns)`.
  - `ISheetWriter`: `int MaxMerges { get; }` (csv and ods `int.MaxValue`, xlsx 65,536), `void Merge(int rows, int columns)` (the next cell written is the top-left of the range; called right before that cell), `void WriteCovered()` (a position inside a range, not its top-left).
  - In this task xlsx and ods implement `Merge` as a no-op and `WriteCovered` as `WriteEmpty(0)` — Tasks 4 and 5 replace both. Csv: `Merge` no-op, `WriteCovered() => Separate();`.

- [ ] **Step 1: Write the failing tests**

```csharp
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>Merged cells: declared before their top-left cell, covered positions skipped and written by the writer.</summary>
public sealed class MergeTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly WriteColumn[] Four = [new("a"), new("b"), new("c"), new("d")];

    /// <summary>
    /// Row 2: a 1 × 2 title in a–b, then c, d. Rows 3–4: a 2 × 2 block in b–c (b3 top-left), a and d around it.
    /// </summary>
    internal static void Legend(TabularWriter writer)
    {
        writer.BeginSheet("legend", Four);
        writer.BeginRow();
        writer.Merge(1, 2);
        writer.Write("title");
        writer.Write("c2");
        writer.Write("d2");
        writer.EndRow();
        writer.BeginRow();
        writer.Write("a3");
        writer.Merge(2, 2);
        writer.Write("block");
        writer.Write("d3");
        writer.EndRow();
        writer.BeginRow();
        writer.Write("a4");
        writer.Write("d4");                 // b4 and c4 are covered: this lands in d
        writer.EndRow();
    }

    [Fact]
    public async Task CsvWritesTheValueInTheTopLeftCellAndEmptiesElsewhere()
    {
        byte[] csv = await SheetLayoutTests.Write(TabularFormat.Csv, Legend);
        List<RawCell[]> rows = SheetLayoutTests.Rows(csv);

        Assert.Equal(["title", "", "c2", "d2"], rows[1].Select(c => c.Text ?? string.Empty));
        Assert.Equal(["a3", "block", "", "d3"], rows[2].Select(c => c.Text ?? string.Empty));
        Assert.Equal(["a4", "", "", "d4"], rows[3].Select(c => c.Text ?? string.Empty));
    }

    [Fact]
    public async Task ARowThatEndsEarlyStillCoversItsMergedColumns()
    {
        byte[] csv = await SheetLayoutTests.Write(TabularFormat.Csv, writer =>
        {
            writer.BeginSheet("data", Four);
            writer.BeginRow();
            writer.Write("a2");
            writer.Merge(2, 3);
            writer.Write("wide");
            writer.EndRow();
            writer.BeginRow();
            writer.Write("a3");
            writer.EndRow();                // b3–d3 covered, written by EndRow
        });

        Assert.Equal(["a3", "", "", ""], SheetLayoutTests.Rows(csv)[2].Select(c => c.Text ?? string.Empty));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(2, 0)]
    [InlineData(1, 1)]
    [InlineData(-1, 2)]
    public async Task AnEmptyOrSingleCellRangeIsRefused(int rows, int columns)
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Csv);
        writer.BeginSheet("data", Four);
        writer.BeginRow();

        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Merge(rows, columns));
    }

    [Fact]
    public async Task ASecondMergeBeforeACellIsRefused()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Csv);
        writer.BeginSheet("data", Four);
        writer.BeginRow();
        writer.Merge(1, 2);

        Assert.Throws<InvalidOperationException>(() => writer.Merge(1, 2));
    }

    [Fact]
    public async Task AMergeNoCellFollowedIsRefusedAtTheEndOfTheRow()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Csv);
        writer.BeginSheet("data", Four);
        writer.BeginRow();
        writer.Merge(1, 2);

        Assert.Throws<InvalidOperationException>(writer.EndRow);
    }

    [Fact]
    public async Task ARangePastTheLastColumnIsRefused()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Csv);
        writer.BeginSheet("data", Four);
        writer.BeginRow();
        writer.Write("a");
        writer.Write("b");
        writer.Merge(1, 3);

        Assert.Throws<InvalidOperationException>(() => writer.Write("c"));
    }

    [Fact]
    public async Task ARangeOverlappingAnotherIsRefused()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Csv);
        writer.BeginSheet("data", Four);
        writer.BeginRow();
        writer.Write("a2");
        writer.Write("b2");
        writer.Merge(3, 1);                 // c2–c4
        writer.Write("tall");
        writer.EndRow();
        writer.BeginRow();
        writer.Merge(1, 3);                 // a3–c3 would cover c3, which the tall range covers

        Assert.Throws<InvalidOperationException>(() => writer.Write("wide"));
    }

    [Fact]
    public async Task ASheetEndedWhileARangeStillCoversRowsIsRefused()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);
        writer.BeginSheet("data", Four);
        writer.BeginRow();
        writer.Merge(3, 1);
        writer.Write("tall");
        writer.EndRow();

        Assert.Throws<InvalidOperationException>(() => writer.BeginSheet("next", Four));
    }

    [Fact]
    public async Task CompletingWhileARangeStillCoversRowsIsRefused()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Csv);
        writer.BeginSheet("data", Four);
        writer.BeginRow();
        writer.Merge(2, 1);
        writer.Write("tall");
        writer.EndRow();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await writer.CompleteAsync(Token));
    }

    [Fact]
    public async Task AMergeOutsideARowIsRefused()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Csv);
        writer.BeginSheet("data", Four);

        Assert.Throws<InvalidOperationException>(() => writer.Merge(1, 2));
    }

    [Fact]
    public async Task AWriteIntoAFullyCoveredRowIsRefused()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Csv);
        writer.BeginSheet("data", [new("a"), new("b")]);
        writer.BeginRow();
        writer.Merge(2, 2);
        writer.Write("all");
        writer.EndRow();
        writer.BeginRow();

        Assert.Throws<InvalidOperationException>(() => writer.Write("x"));
    }
}
```

`RawCell.Text` — use the property `RawCell` exposes for text (read `src/TriasDev.Tabular/Abstractions/RawCell.cs`; if it is named differently, use that name and keep the assertions' meaning). The csv reader trims and reads empty fields as empty cells; if the trailing empty of a row is not returned as a cell at all, compare with the row padded to four, and say so in the report.

- [ ] **Step 2: Run to verify it fails** → compile errors (`Merge` missing).

- [ ] **Step 3: Implement in `TabularWriter`**

Fields (with the other per-sheet fields):

```csharp
    // Merges: per column, the rows a range covers there (first..last, 0 when none). Allocated on a
    // sheet's first merge, so a sheet without merges pays one null check per cell.
    private long[]? _coveredFrom;
    private long[]? _coveredThrough;
    private long _lastCoveredRow;
    private int _pendingRows;
    private int _pendingColumns;
    private int _merges;
```

`Merge`:

```csharp
    /// <summary>
    /// Merges the next cell written with the cells right of and below it: it becomes the top-left of
    /// a range <paramref name="rows"/> high and <paramref name="columns"/> wide, which shows its value.
    /// The writer skips the covered positions — the row's next write lands after the range, and later
    /// rows skip it too — and writes them itself. Csv writes the value in the top-left cell and empty
    /// fields elsewhere.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Rows or columns below 1, or a single cell.</exception>
    /// <exception cref="InvalidOperationException">Outside a row, or a merge already waits for its cell.</exception>
    /// <exception cref="TabularLimitException">The sheet already holds as many merges as its format allows.</exception>
    public void Merge(int rows, int columns)
    {
        ExpectWritable();

        if (_state != State.InRow)
        {
            throw Refuse("A merge is declared inside a row, right before its top-left cell.");
        }

        if (_pendingColumns != 0)
        {
            throw Refuse("A merge already waits for its top-left cell.");
        }

        if (rows < 1 || columns < 1 || (rows == 1 && columns == 1))
        {
            throw Faulting(new ArgumentOutOfRangeException(rows < 1 ? nameof(rows) : nameof(columns), $"A merged range is at least 1 × 1 and more than one cell; {rows} × {columns} is not."));
        }

        if (_merges == _sheet.MaxMerges)
        {
            throw Faulting(new TabularLimitException("MaxMerges", _sheet.MaxMerges, $"A {Format} sheet holds at most {_sheet.MaxMerges} merged ranges."));
        }

        _pendingRows = rows;
        _pendingColumns = columns;
    }
```

`NextCell` becomes:

```csharp
    private int NextCell()
    {
        ExpectWritable();

        if (_state != State.InRow)
        {
            throw Refuse("A value is written between BeginRow and EndRow.");
        }

        if (_coveredFrom is not null)
        {
            SkipCovered();
        }

        if (_column == _columns.Length)
        {
            throw Refuse($"The row already has a value for each of the sheet's {_columns.Length} columns.");
        }

        if (_pendingColumns != 0)
        {
            BeginMerge(_column);
        }

        return _column++;
    }

    /// <summary>Writes the covered positions the row has reached.</summary>
    private void SkipCovered()
    {
        while (_column < _columns.Length && IsCovered(_column))
        {
            _sheet.WriteCovered();
            _column++;
        }
    }

    private bool IsCovered(int column) =>
        _coveredFrom![column] <= _rowNumber && _rowNumber <= _coveredThrough![column];

    /// <summary>Checks the waiting merge against the sheet and earlier ranges, records it, and tells the format.</summary>
    private void BeginMerge(int column)
    {
        int rows = _pendingRows;
        int columns = _pendingColumns;
        _pendingRows = 0;
        _pendingColumns = 0;

        if (columns > _columns.Length - column)
        {
            throw Refuse($"A merge of {columns} columns from column {column + 1} reaches past the sheet's {_columns.Length} columns.");
        }

        _coveredFrom ??= new long[_columns.Length];
        _coveredThrough ??= new long[_columns.Length];
        long last = _rowNumber + rows - 1;

        for (int c = column; c < column + columns; c++)
        {
            if (_coveredThrough[c] >= _rowNumber && _coveredFrom[c] <= last)
            {
                throw Refuse($"The merge from row {_rowNumber}, column {column + 1} overlaps a range declared earlier.");
            }
        }

        for (int c = column; c < column + columns; c++)
        {
            // The top-left cell holds the value; every other position in the range is covered.
            _coveredFrom[c] = c == column ? _rowNumber + 1 : _rowNumber;
            _coveredThrough[c] = c == column && rows == 1 ? 0 : last;
        }

        _lastCoveredRow = Math.Max(_lastCoveredRow, last);
        _merges++;
        _sheet.Merge(rows, columns);
    }
```

Note on `_coveredThrough[c] = 0` for the top-left column of a one-row range: `IsCovered` then needs `from <= row <= 0`, never true. An earlier finished range in that column leaves stale values; they never match later rows because `through < _rowNumber`, and the overlap check uses the same comparison.

`FinishRow`:

```csharp
    private void FinishRow()
    {
        for (; _column < _columns.Length; _column++)
        {
            if (_coveredFrom is not null && IsCovered(_column))
            {
                _sheet.WriteCovered();
            }
            else
            {
                _sheet.WriteEmpty(0);
            }
        }

        _sheet.EndRow();
        _state = State.InSheet;
    }
```

`EndRow`, before `FinishRow()`: `if (_pendingColumns != 0) throw Refuse("A merge was declared but no cell followed it in the row.");`

Sheet end: a helper used at the top of `BeginSheet` (after the state checks, before anything is reset — only when a sheet was begun) and of `CompleteAsync` (after the existing checks):

```csharp
    private void ExpectRangesClosed()
    {
        if (_lastCoveredRow > _rowNumber)
        {
            throw Refuse($"A merged range reaches row {_lastCoveredRow}, but sheet \"{_sheetName}\" ends at row {_rowNumber}.");
        }
    }
```

`BeginSheet` resets per sheet: `_coveredFrom = null; _coveredThrough = null; _lastCoveredRow = 0; _merges = 0; _pendingRows = 0; _pendingColumns = 0;`.

- [ ] **Step 4: `ISheetWriter` and the three format writers**

Add to `ISheetWriter`:

```csharp
    /// <summary>The most merged ranges a sheet holds.</summary>
    int MaxMerges { get; }

    /// <summary>The next cell written is the top-left of a range this many rows high and columns wide; checked by the writer.</summary>
    void Merge(int rows, int columns);

    /// <summary>Writes a position a merged range covers, other than its top-left cell.</summary>
    void WriteCovered();
```

Csv: `MaxMerges => int.MaxValue`, `Merge` does nothing, `WriteCovered() => Separate();`. Xlsx (for now): `MaxMerges => 65_536`, `Merge` does nothing, `WriteCovered() => WriteEmpty(0);`. Ods (for now): `MaxMerges => int.MaxValue`, `Merge` does nothing, `WriteCovered() => WriteEmpty(0);`.

- [ ] **Step 5: Run** `--filter-class "*MergeTests"` and the full suite → PASS. Public API, format.

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "feat(write): merged cells — declared before the top-left cell, covered positions skipped and written by the writer; csv"
```

---

### Task 4: Xlsx merges

**Files:**
- Modify: `src/TriasDev.Tabular/Xlsx/XlsxSheetWriter.cs`
- Test: `tests/TriasDev.Tabular.Tests/Writing/MergeTests.cs`, `tests/TriasDev.Tabular.Tests/Writing/StyledInteropTests.cs`

**Interfaces:**
- Consumes: `ISheetWriter.Merge`/`WriteCovered`/`MaxMerges` (Task 3), `CloseSheet` with `SheetDataEnd` / `autoFilter` / `WorksheetClose` (Task 2).

- [ ] **Step 1: Write the failing tests** (append to `MergeTests`)

```csharp
    [Fact]
    public async Task XlsxListsTheRangesAfterTheData()
    {
        byte[] xlsx = await SheetLayoutTests.Write(TabularFormat.Xlsx, Legend);
        string sheet = SheetLayoutTests.Entry(xlsx, "xl/worksheets/sheet1.xml");

        Assert.Empty(OoxmlValidation.Errors(xlsx));
        Assert.Contains("</sheetData><mergeCells count=\"2\"><mergeCell ref=\"A2:B2\"/><mergeCell ref=\"B3:C4\"/></mergeCells></worksheet>", sheet, StringComparison.Ordinal);
        Assert.Contains("<c r=\"C2\" t=\"inlineStr\"><is><t>c2</t></is></c>", sheet, StringComparison.Ordinal);
        Assert.Contains("<c r=\"D4\" t=\"inlineStr\"><is><t>d4</t></is></c>", sheet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task XlsxPutsTheRangesAfterTheFilter()
    {
        byte[] xlsx = await SheetLayoutTests.Write(TabularFormat.Xlsx, writer =>
        {
            writer.BeginSheet("data", Four, new SheetOptions { AutoFilter = true });
            writer.BeginRow();
            writer.Merge(1, 2);
            writer.Write("x");
            writer.EndRow();
        });

        Assert.Empty(OoxmlValidation.Errors(xlsx));
        Assert.Contains("<autoFilter ref=\"A1:D2\"/><mergeCells count=\"1\"><mergeCell ref=\"A2:B2\"/></mergeCells>", SheetLayoutTests.Entry(xlsx, "xl/worksheets/sheet1.xml"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TabularFormat.Xlsx)]
    [InlineData(TabularFormat.Ods)]
    [InlineData(TabularFormat.Csv)]
    public async Task TheImportReadsAMergeAsItsValueAndEmpties(TabularFormat format)
    {
        byte[] file = await SheetLayoutTests.Write(format, Legend);
        List<RawCell[]> rows = SheetLayoutTests.Rows(file);

        Assert.Equal(RawCell.FromText("title"), rows[1][0]);
        Assert.True(rows[1][1].IsEmpty);
        Assert.Equal(RawCell.FromText("c2"), rows[1][2]);
        Assert.Equal(RawCell.FromText("block"), rows[2][1]);
        Assert.True(rows[3][1].IsEmpty);
        Assert.True(rows[3][2].IsEmpty);
        Assert.Equal(RawCell.FromText("d4"), rows[3][3]);
    }

    [Fact]
    public async Task TheMergeLimitIsTheFormats()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);
        writer.BeginSheet("data", [new("a"), new("b")]);

        for (int i = 0; i < 65_536; i++)
        {
            writer.BeginRow();
            writer.Merge(1, 2);
            writer.Write(i);
            writer.EndRow();
        }

        writer.BeginRow();
        Assert.Throws<TabularLimitException>(() => writer.Merge(1, 2));
    }
```

(`TheImportReadsAMergeAsItsValueAndEmpties` also covers ods; it passes for ods only after Task 5 if Task 3's ods stub writes plain empties — it does, so it passes now too, and Task 5 keeps it green with real covered cells.)

Interop (append to `StyledInteropTests`):

```csharp
    [Fact]
    public async Task LibreOfficeKeepsTheMergesOfAnXlsx()
    {
        byte[] xlsx = await SheetLayoutTests.Write(TabularFormat.Xlsx, MergeTests.Legend);
        string content = Entry(LibreOffice.Convert(xlsx, "xlsx", "ods", "ods"), "content.xml");

        Assert.Contains("table:number-columns-spanned=\"2\"", content, StringComparison.Ordinal);
        Assert.Contains("table:number-rows-spanned=\"2\"", content, StringComparison.Ordinal);
    }
```

- [ ] **Step 2: Run to verify they fail** (`XlsxListsTheRangesAfterTheData`, `XlsxPutsTheRangesAfterTheFilter`, the interop test) → FAIL.

- [ ] **Step 3: Implement**

- A per-sheet `private readonly List<string> _merges = [];` cleared in `BeginSheet`.
- `Merge(int rows, int columns)`: the top-left is the next cell, at `_column` in row `_rowNumber`: add `$"{_columnNames[_column]}{_rowNumber}:{_columnNames[_column + columns - 1]}{_rowNumber + rows - 1}"` (format the numbers with `CultureInfo.InvariantCulture`). One string per merge is allocated — per merge, not per cell.
- `WriteCovered() => _column++;` (a covered cell is simply absent, as an empty one).
- `CloseSheet`: after the `autoFilter`, when `_merges.Count > 0`: `<mergeCells count="{n}">` + `<mergeCell ref="…"/>` each + `</mergeCells>`, then `WorksheetClose`.

- [ ] **Step 4: Run** `--filter-class "*MergeTests"`, `"*StyledInteropTests"` (soffice required) and the full suite → PASS.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat(write): xlsx merged cells"
```

---

### Task 5: Ods merges and repeated empty cells

**Files:**
- Modify: `src/TriasDev.Tabular/Ods/OdsSheetWriter.cs`
- Test: `tests/TriasDev.Tabular.Tests/Writing/MergeTests.cs`, `tests/TriasDev.Tabular.Tests/Writing/OdsWriterTests.cs` (only where an existing assertion pinned one `<table:table-cell/>` per empty cell), `tests/TriasDev.Tabular.Tests/Writing/StyledInteropTests.cs`

**Interfaces:**
- Consumes: Task 3's `ISheetWriter` members.

- [ ] **Step 1: Write the failing tests** (append to `MergeTests`)

```csharp
    [Fact]
    public async Task OdsSpansTheTopLeftCellAndCoversTheRest()
    {
        byte[] ods = await SheetLayoutTests.Write(TabularFormat.Ods, Legend);
        string content = SheetLayoutTests.Entry(ods, "content.xml");

        Assert.Contains("<table:table-row><table:table-cell table:number-columns-spanned=\"2\" table:number-rows-spanned=\"1\" office:value-type=\"string\"><text:p>title</text:p></table:table-cell><table:covered-table-cell/>", content, StringComparison.Ordinal);
        Assert.Contains("<table:table-cell table:number-columns-spanned=\"2\" table:number-rows-spanned=\"2\" office:value-type=\"string\"><text:p>block</text:p></table:table-cell><table:covered-table-cell/>", content, StringComparison.Ordinal);
        Assert.Contains("<text:p>a4</text:p></table:table-cell><table:covered-table-cell/><table:covered-table-cell/><table:table-cell office:value-type=\"string\"><text:p>d4</text:p>", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OdsSpansAStyledAndAnEmptyTopLeftCell()
    {
        byte[] ods = await SheetLayoutTests.Write(TabularFormat.Ods, writer =>
        {
            StyleId fill = writer.Style(new CellStyle { Fill = CellColor.FromRgb(0xF8696B) });
            writer.BeginSheet("data", Four);
            writer.BeginRow();
            writer.Merge(1, 2);
            writer.Write(5.5, fill);
            writer.Merge(1, 2);
            writer.WriteEmpty(fill);
            writer.EndRow();
        });

        string content = SheetLayoutTests.Entry(ods, "content.xml");
        Assert.Contains("<table:table-cell table:number-columns-spanned=\"2\" table:number-rows-spanned=\"1\" table:style-name=\"ts1\" office:value-type=\"float\" office:value=\"5.5\"/><table:covered-table-cell/>", content, StringComparison.Ordinal);
        Assert.Contains("<table:table-cell table:number-columns-spanned=\"2\" table:number-rows-spanned=\"1\" table:style-name=\"ts1\"/><table:covered-table-cell/>", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OdsWritesARunOfEmptyCellsAsOne()
    {
        byte[] ods = await SheetLayoutTests.Write(TabularFormat.Ods, writer =>
        {
            writer.BeginSheet("data", [new("a"), new("b"), new("c"), new("d"), new("e")]);
            writer.BeginRow();
            writer.Write("a2");
            writer.WriteEmpty();
            writer.WriteEmpty();
            writer.WriteEmpty();
            writer.Write("e2");
            writer.EndRow();
            writer.BeginRow();
            writer.Write("a3");
            writer.EndRow();
        });

        string content = SheetLayoutTests.Entry(ods, "content.xml");
        Assert.Contains("<text:p>a2</text:p></table:table-cell><table:table-cell table:number-columns-repeated=\"3\"/><table:table-cell office:value-type=\"string\"><text:p>e2</text:p>", content, StringComparison.Ordinal);
        Assert.Contains("<text:p>a3</text:p></table:table-cell><table:table-cell table:number-columns-repeated=\"4\"/></table:table-row>", content, StringComparison.Ordinal);

        List<RawCell[]> rows = SheetLayoutTests.Rows(ods);
        Assert.Equal(RawCell.FromText("e2"), rows[1][4]);
        Assert.True(rows[1][2].IsEmpty);
    }
```

Interop (append to `StyledInteropTests`):

```csharp
    [Fact]
    public async Task LibreOfficeKeepsTheMergesOfAnOds()
    {
        byte[] ods = await SheetLayoutTests.Write(TabularFormat.Ods, MergeTests.Legend);
        string sheet = Entry(LibreOffice.Convert(ods, "ods", "xlsx:Calc MS Excel 2007 XML", "xlsx"), "xl/worksheets/sheet1.xml");

        Assert.Contains("<mergeCell ref=\"A2:B2\"/>", sheet, StringComparison.Ordinal);
        Assert.Contains("<mergeCell ref=\"B3:C4\"/>", sheet, StringComparison.Ordinal);
    }
```

- [ ] **Step 2: Run to verify they fail** → FAIL.

- [ ] **Step 3: One place opens every cell**

Every value cell's start tag is built in one helper, so a pending span is written once:

```csharp
    private string? _span;               // the pending span attributes for the next cell, or null
    private int _emptyRun;               // unstyled empty cells not yet written

    /// <summary>Opens a value cell: writes any pending empty run, then <c>&lt;table:table-cell</c> and a pending span.</summary>
    private void OpenCell()
    {
        FlushEmpties();
        _row.Append("<table:table-cell");

        if (_span is not null)
        {
            _row.Append(_span);
            _span = null;
        }
    }

    private void FlushEmpties()
    {
        if (_emptyRun == 0)
        {
            return;
        }

        if (_emptyRun == 1)
        {
            _row.Append("<table:table-cell/>");
        }
        else
        {
            _row.Append("<table:table-cell table:number-columns-repeated=\"");
            _row.AppendFormatted(_emptyRun, default, CultureInfo.InvariantCulture);
            _row.Append("\"/>");
        }

        _emptyRun = 0;
    }
```

Rewrite each cell start through `OpenCell()`: the `StyledCell` constant becomes `OpenCell(); _row.Append(" table:style-name=\"");`; `StartFloat()` becomes `OpenCell(); _row.Append(" office:value-type=\"float\" office:value=\"");`; the boolean, date, text and styled-empty starts likewise. Every existing expected string stays the same when no span is pending — run the existing `Ods*` tests after the refactor and before adding spans to prove it.

- `Merge(int rows, int columns)`: `_span = string.Create(CultureInfo.InvariantCulture, $" table:number-columns-spanned=\"{columns}\" table:number-rows-spanned=\"{rows}\"");` — one string per merge.
- `WriteCovered()`: `FlushEmpties(); _row.Append("<table:covered-table-cell/>");`.
- `WriteEmpty(int style)`: style 0 and no pending span → `_emptyRun++`; otherwise `OpenCell()` then ` table:style-name="NAME"/>` for a styled one, or `/>` for an unstyled empty top-left cell with a span.
- `EndRow()`: `FlushEmpties()` before `</table:table-row>`.

- [ ] **Step 4: Run** `--filter-class "*MergeTests"`, `"*Ods*"`, `"*StyledInteropTests"` (soffice required) and the full suite → PASS. If an existing ods test pinned `<table:table-cell/><table:table-cell/>`, update its expectation to the repeated form and name it in the report.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat(write): ods merged cells and runs of empty cells as one repeated cell"
```

---

### Task 6: A laid-out workbook end to end; documentation

**Files:**
- Modify: `tests/TriasDev.Tabular.Tests/Writing/StyledInteropTests.cs`, `docs/KNOWN-ISSUES.md`
- Test: `StyledInteropTests.ALaidOutWorkbookOpensAndReadsBack`

- [ ] **Step 1: The test**

```csharp
    [Theory]
    [InlineData(TabularFormat.Xlsx, "xlsx")]
    [InlineData(TabularFormat.Ods, "ods")]
    public async Task ALaidOutWorkbookOpensAndReadsBack(TabularFormat format, string extension)
    {
        CellStyle header = new() { Fill = CellColor.FromRgb(0x1F4E78), Font = new CellFont { Color = CellColor.FromRgb(0xFFFFFF), Bold = true } };
        CellStyle[] legend = [new() { Fill = CellColor.FromRgb(0x63BE7B) }, new() { Fill = CellColor.FromRgb(0xFFEB84) }, new() { Fill = CellColor.FromRgb(0xF8696B) }];

        byte[] file = await SheetLayoutTests.Write(format, writer =>
        {
            StyleId[] colours = [.. legend.Select(writer.Style)];
            StyleId title = writer.Style(new CellStyle { Font = new CellFont { Bold = true }, Horizontal = HorizontalAlignment.Center });

            writer.BeginSheet("Data", [new("Id"), new("Score"), new("Date", 12)], new SheetOptions { HeaderStyle = header, FreezeRows = 1, AutoFilter = true });

            for (int i = 1; i <= 100; i++)
            {
                writer.BeginRow();
                writer.Write((long)i);
                writer.Write(i / 10.0, colours[i % 3]);
                writer.Write(new DateOnly(2026, 1, 1).AddDays(i));
                writer.EndRow();
            }

            writer.BeginSheet("Legend", [new("Range"), new("Colour"), new("Meaning")], new SheetOptions { HeaderStyle = header });
            writer.BeginRow();
            writer.Merge(1, 3);
            writer.Write("Score legend", title);
            writer.EndRow();

            for (int i = 0; i < legend.Length; i++)
            {
                writer.BeginRow();
                writer.Write($"{i * 3}–{(i * 3) + 3}");
                writer.WriteEmpty(colours[i]);
                writer.Write(i switch { 0 => "low", 1 => "medium", _ => "high" });
                writer.EndRow();
            }
        });

        if (format == TabularFormat.Xlsx)
        {
            Assert.Empty(OoxmlValidation.Errors(file));
        }

        string[] lines = LibreOffice.ConvertToCsv(file, extension);
        Assert.Equal(101, lines.Length);

        List<RawCell[]> data = SheetLayoutTests.Rows(file, 0);
        Assert.Equal(101, data.Count);
        Assert.Equal(RawCell.FromNumber(5), data[50][1]);

        List<RawCell[]> legendRows = SheetLayoutTests.Rows(file, 1);
        Assert.Equal(RawCell.FromText("Score legend"), legendRows[1][0]);
        Assert.Equal(RawCell.FromText("high"), legendRows[4][2]);
    }
```

`lines.Length == 101`: `ConvertToCsv` converts the first sheet only (header + 100 rows).

- [ ] **Step 2: Run** with `TABULAR_REQUIRE_SOFFICE=1` → PASS (if it fails, the failure is a real bug in Tasks 1–5: find it and fix it in the library, with a focused test next to the code's own tests).

- [ ] **Step 3: Documentation** — under the `## Styles` heading in `docs/KNOWN-ISSUES.md`, add:

```markdown
### Sheet layout

- `SheetOptions` sets a header style, frozen rows and columns, and an auto-filter on the header row through the last row written; csv ignores it.
- `writer.Merge(rows, columns)` makes the next cell the top-left of a merged range. The writer skips the covered positions — the row's next write lands after the range, later rows skip it too — and writes them itself. The import reads a merged range as its value in the top-left cell and empty cells elsewhere; csv writes exactly that.
- A merge must end inside the sheet: ending a sheet (or the file) while a range still has rows to cover is refused.
- xlsx holds at most 65,536 merged ranges per sheet.
```

- [ ] **Step 4: Full suite, format, commit**

```bash
git add tests docs
git commit -m "test(write): a laid-out workbook opens in LibreOffice and reads back; document sheet layout"
```
