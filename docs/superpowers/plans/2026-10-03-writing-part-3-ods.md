# Writing — part 3: OpenDocument spreadsheets (ods) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `TabularWriter.Create(stream, TabularFormat.Ods)` streams an OpenDocument spreadsheet — several sheets, typed cells, flat memory — that LibreOffice opens and the library's own import reads back as the same values with the same types.

**Architecture:** `OdsSheetWriter` implements the existing internal `ISheetWriter` on the part-2 `ZipWriter`: the `mimetype` is the first entry, stored; every sheet goes into one deflated `content.xml` streamed row by row (ODF keeps all tables in one part), preceded by fixed automatic styles (date, date-time, boolean, and one column style per whole-character width); the manifest and a minimal `styles.xml` are stored at `Complete`. The precision checks xlsx and ods share move into `ValueChecks`.

**Tech Stack:** .NET 8 + 10, base class library only, xunit v4 on Microsoft.Testing.Platform; LibreOffice headless for an interoperability test (installed in CI on Linux; skipped locally where absent).

**Spec:** `docs/superpowers/specs/2026-10-03-writing-design.md` — delivery step 3 of 5 (issue #72). Parts 1 (#77) and 2 (#81) are merged.

## Global Constraints

- No package references in the library; only the base class library (`TabularIndependenceTests`).
- The writer never writes to, flushes or disposes the target stream synchronously; the format writers write only into `SpillBuffer`.
- No allocation per row or per cell on the write path.
- Error codes, never messages: value problems are `ErrorCodes.Write.*` codes returned by the sheet writer; programmer errors are `ArgumentException` / `InvalidOperationException`.
- Options are checked where they are handed over (`TabularWriter.Create`).
- Public API changes go in `src/TriasDev.Tabular/PublicAPI.Unshipped.txt` (RS0016 messages are authoritative).
- `dotnet format TriasDev.Tabular.slnx --verify-no-changes` must pass before every commit (CI runs it).
- Strings holding unpaired surrogates never go into attribute arguments (`[InlineData]`).
- Tests use the public API, except internal units (`ValueChecks`, `OdsParts`) — InternalsVisibleTo is set.
- The reader is the yardstick: never change `OdsCursor` to make a writer test pass; a disagreement is a finding to report.
- Nothing product-specific in code, docs or commits.

## Rulings made while planning (deviations from or additions to the spec)

- **A carriage return is kept exactly.** The spec said a CR in ods text is a line break like LF, which would read `\r\n` back as `\n` — a silent change the core rule forbids. Text containing `\r` additionally carries `office:string-value` with the exact value (`&#13;`, `&#10;`, `&#9;` as character references, which attribute normalization leaves alone); the reader takes `office:string-value` over the paragraphs (`OdsCursor.ReadCell`). The paragraphs still show the text in LibreOffice. Cost: such text is written twice.
- **Column widths are rounded to whole characters.** `content.xml` streams, so its automatic styles must precede the first table, before later sheets' widths are known; the writer declares one column style per width from 1 to 255 characters up front (≈ 30 KB before compression) and rounds each width to the nearest. Display only.
- **Number cells carry no paragraph.** LibreOffice formats the display from `office:value`; the reader reads the attribute. Saves a copy of every number.
- **Text over 16,777,216 characters is `write.text-too-long`** — the ods reader's `MaxValueChars`, as the spec's bounds section says.
- **LibreOffice is the format check, in CI.** There is no ODF schema validator in .NET; the CI's Linux job installs `libreoffice-calc` and sets `TABULAR_REQUIRE_SOFFICE=1`, so the interop test fails there rather than skipping. Elsewhere it skips when no `soffice` is found.
- **The shared checks** (`ValueChecks.LongInDouble`, `ValueChecks.DecimalInDouble`) replace the copies inside `XlsxSheetWriter`, so xlsx and ods apply one rule.

## Review Focus

1. **Text with runs of spaces, tabs and blank lines** (`"a  b"`, `"  lead"`, `"x\n\ny"`, `"\tz"`): our reader and LibreOffice both give the text back as written. → Task 2 `WritesSpacesTabsAndLineBreaksAsTheReaderReadsThem`; Task 4 interop.
2. **A sheet name with XML characters** (`R&D <2026> "q"`): escaped in `table:name`, read back by `TableNameScan`. → Task 2 `NamesSheetsAsWrittenEvenWithXmlCharacters`.
3. **A workbook abandoned after a flush**: what was sent does not open as a valid spreadsheet. → Task 2 `ASpreadsheetDisposedBeforeCompletingDoesNotOpen`.
4. **An empty row between data rows**: it stays a row (numbering intact) and the import skips it. → Task 5 `ReadsEmptyAndWhitespaceTextAsNoValueAndSkipsAnEmptyRow` (the empty row is placed between rows: the reader drops trailing empty rows).
5. **Dates before 1900 and in year 1** (ods has no serial): they round-trip, unlike xlsx. → Task 3 `WritesDatesXlsxCannotHold`.

---

### Task 1: One precision rule for every workbook format

**Files:**
- Modify: `src/TriasDev.Tabular/Writing/ValueChecks.cs` (add two methods)
- Modify: `src/TriasDev.Tabular/Xlsx/XlsxSheetWriter.cs` (`WriteLong`, `WriteDecimal` call them)
- Test: `tests/TriasDev.Tabular.Tests/Writing/ValueChecksTests.cs`

**Interfaces:**
- Produces (internal, `TriasDev.Tabular.ValueChecks`): `string? LongInDouble(long value)` and `string? DecimalInDouble(decimal value)` — null when a workbook's double-typed number cell gives the value back exactly, otherwise `ErrorCodes.Write.PrecisionLoss`. Task 3's ods writer calls both.

- [ ] **Step 1: Write the failing tests**

Create `tests/TriasDev.Tabular.Tests/Writing/ValueChecksTests.cs`:

```csharp
using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>What a workbook's number cell — a double — gives back exactly.</summary>
public sealed class ValueChecksTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(9_007_199_254_740_992L)]
    [InlineData(1L << 60)]
    [InlineData(long.MinValue)]
    public void TakesALongADoubleHolds(long value)
    {
        Assert.Null(ValueChecks.LongInDouble(value));
    }

    [Theory]
    [InlineData(9_007_199_254_740_993L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MaxValue - 100)]
    public void RefusesALongADoubleDoesNotHold(long value)
    {
        Assert.Equal(ErrorCodes.Write.PrecisionLoss, ValueChecks.LongInDouble(value));
    }

    [Fact]
    public void TakesADecimalOfFifteenDigitsAndRefusesMore()
    {
        Assert.Null(ValueChecks.DecimalInDouble(123_456_789_012.345m));
        Assert.Null(ValueChecks.DecimalInDouble(0.1m));
        Assert.Null(ValueChecks.DecimalInDouble(-0.000001m));
        Assert.Equal(ErrorCodes.Write.PrecisionLoss, ValueChecks.DecimalInDouble(1_234_567_890_123.456m));
        Assert.Equal(ErrorCodes.Write.PrecisionLoss, ValueChecks.DecimalInDouble(decimal.MaxValue));
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~ValueChecksTests"`
Expected: build FAIL — `LongInDouble` does not exist.

- [ ] **Step 3: Implement**

Add to `src/TriasDev.Tabular/Writing/ValueChecks.cs`, after `Double`:

```csharp
    /// <summary>
    /// Whether a long comes back as itself from a workbook number cell, which the reader parses as a
    /// double.
    /// </summary>
    /// <remarks>2^63 is out of long's range, hence the first test: a cast back from it would saturate.</remarks>
    public static string? LongInDouble(long value)
    {
        double asDouble = value;

        return asDouble >= 9.2233720368547758E18 || (long)asDouble != value
            ? ErrorCodes.Write.PrecisionLoss
            : null;
    }

    /// <summary>
    /// Whether a decimal comes back as itself from a workbook number cell: the import reads the cell's
    /// double back into a decimal by this same cast.
    /// </summary>
    public static string? DecimalInDouble(decimal value)
    {
        try
        {
            return (decimal)(double)value == value ? null : ErrorCodes.Write.PrecisionLoss;
        }
        catch (OverflowException)
        {
            // A decimal near its maximum is beyond what the cast back from a double can hold.
            return ErrorCodes.Write.PrecisionLoss;
        }
    }
```

In `src/TriasDev.Tabular/Xlsx/XlsxSheetWriter.cs`, replace the check at the top of `WriteLong` (the `double asDouble = value; if (...) { return ErrorCodes.Write.PrecisionLoss; }` block and its comment) with:

```csharp
        if (ValueChecks.LongInDouble(value) is { } code)
        {
            return code;
        }
```

and the `try { … } catch (OverflowException) { … }` block at the top of `WriteDecimal` with:

```csharp
        if (ValueChecks.DecimalInDouble(value) is { } code)
        {
            return code;
        }
```

If the analyzer reports S1244 (floating-point equality) on `ValueChecks`, use the same pragma-with-justification that `ValueChecks.Double` already uses.

- [ ] **Step 4: Run the tests**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~ValueChecksTests|FullyQualifiedName~Xlsx"`
Expected: PASS — the new tests and every xlsx test, none of them edited.

- [ ] **Step 5: Commit**

```bash
dotnet format TriasDev.Tabular.slnx --verify-no-changes
git add src/TriasDev.Tabular/Writing/ValueChecks.cs src/TriasDev.Tabular/Xlsx/XlsxSheetWriter.cs tests/TriasDev.Tabular.Tests/Writing/ValueChecksTests.cs
git commit -m "refactor(write): one precision rule for every workbook format"
```

---

### Task 2: The ods sheet writer — the package, text, booleans, empty cells, widths

**Files:**
- Create: `src/TriasDev.Tabular/Ods/OdsWriterOptions.cs`
- Create: `src/TriasDev.Tabular/Ods/OdsParts.cs`
- Create: `src/TriasDev.Tabular/Ods/OdsSheetWriter.cs`
- Modify: `src/TriasDev.Tabular/Writing/TabularWriterOptions.cs` (add `Ods`)
- Modify: `src/TriasDev.Tabular/Writing/TabularWriter.cs` (`Create` handles `TabularFormat.Ods`)
- Modify: `tests/TriasDev.Tabular.Tests/Writing/TabularWriterTests.cs` (`RefusesAFormatItCannotWriteYet`: drop the `Ods` row)
- Modify: `src/TriasDev.Tabular/PublicAPI.Unshipped.txt`
- Test: `tests/TriasDev.Tabular.Tests/Writing/OdsWriterTests.cs`

**Interfaces:**
- Consumes: `ZipWriter` (`AddStored`, `BeginDeflated → Stream`, `EndEntry`, `Complete`), `RowText`, `SpillBuffer`, `ISheetWriter` (incl. `NamesSheets`, `IDisposable`), `TextRules` (TabularWriter has run it before `WriteText`), `SheetNames` (TabularWriter checks names), `OdsCursor` (tests).
- Produces: `public sealed record OdsWriterOptions { static Default; CompressionLevel CompressionLevel = Fastest; internal OdsWriterOptions Checked() }` (namespace `TriasDev.Tabular.Ods`); `TabularWriterOptions.Ods`; internal `OdsSheetWriter : ISheetWriter` — this task implements everything except `WriteLong`, `WriteDecimal`, `WriteDouble`, `WriteDate`, which throw `NotSupportedException` until Task 3 (no test here reaches them); internal `static class OdsParts` (`Mimetype`, `ContentStart`, `ContentEnd`, `Manifest`, `Styles`, `ColumnStyleName(int chars)`, `ColumnStyleFor(double width)`).

- [ ] **Step 1: Write the failing tests**

Create `tests/TriasDev.Tabular.Tests/Writing/OdsWriterTests.cs`:

```csharp
using System.IO.Compression;
using System.Text;

using TriasDev.Tabular.Ods;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>The OpenDocument spreadsheet the ods writer streams, read by the library's own cursor as written.</summary>
public sealed class OdsWriterTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<byte[]> Spreadsheet(Action<TabularWriter> write)
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Ods))
        {
            write(writer);
            await writer.CompleteAsync(Token);
        }

        return target.ToArray();
    }

    private static List<RawCell[]> Rows(byte[] ods, int sheet = 0)
    {
        using OdsCursor cursor = new(new MemoryStream(ods, writable: false), cancellationToken: Token);
        Assert.True(cursor.MoveToSheet(sheet, Token));

        List<RawCell[]> rows = [];

        while (cursor.ReadRow(Token))
        {
            rows.Add(cursor.CurrentRow.ToArray());
        }

        return rows;
    }

    private static string Content(byte[] ods)
    {
        using ZipArchive archive = new(new MemoryStream(ods, writable: false), ZipArchiveMode.Read);
        using StreamReader reader = new(archive.GetEntry("content.xml")!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static async Task<RawCell[]> Column(params string[] values)
    {
        byte[] ods = await Spreadsheet(writer =>
        {
            writer.BeginSheet("data", [new("v")]);

            foreach (string value in values)
            {
                writer.BeginRow();
                writer.Write(value);
                writer.EndRow();
            }
        });

        return [.. Rows(ods).Skip(1).Select(r => r[0])];
    }

    [Fact]
    public async Task WritesASpreadsheetTheCursorReadsAndDetects()
    {
        byte[] ods = await Spreadsheet(writer =>
        {
            writer.BeginSheet("Portfolios", [new("Id"), new("Name"), new("Active")]);
            writer.BeginRow();
            writer.Write("P-1");
            writer.Write("Alpha");
            writer.Write(true);
            writer.EndRow();
            writer.BeginRow();
            writer.WriteEmpty();
            writer.Write("Beta");
            writer.Write(false);
            writer.EndRow();
        });

        Assert.Equal(TabularFormat.Ods, TabularFile.Detect(new MemoryStream(ods)));

        List<RawCell[]> rows = Rows(ods);
        Assert.Equal(3, rows.Count);
        Assert.Equal(["Id", "Name", "Active"], rows[0].Select(c => c.Text));
        Assert.Equal(RawCell.FromText("P-1"), rows[1][0]);
        Assert.Equal(RawCell.FromBoolean(true), rows[1][2]);
        Assert.True(rows[2][0].IsEmpty);
        Assert.Equal(RawCell.FromText("Beta"), rows[2][1]);
        Assert.Equal(RawCell.FromBoolean(false), rows[2][2]);
    }

    [Fact]
    public async Task StoresTheMimetypeFirstAsOpenDocumentRequires()
    {
        byte[] ods = await Spreadsheet(writer => writer.BeginSheet("data", [new("a")]));

        Assert.Equal("mimetype", Encoding.ASCII.GetString(ods, 30, 8));
        Assert.Equal("application/vnd.oasis.opendocument.spreadsheet", Encoding.ASCII.GetString(ods, 38, 46));
        Assert.Equal(0, ods[8] | ods[9]);                                   // stored
        Assert.Equal(0, ods[6] & 0x08);                                    // no data descriptor

        using ZipArchive archive = new(new MemoryStream(ods, writable: false), ZipArchiveMode.Read);
        Assert.Equal(["mimetype", "content.xml", "META-INF/manifest.xml", "styles.xml"], archive.Entries.Select(e => e.FullName));
    }

    [Fact]
    public async Task WritesSeveralSheetsInOneContentPart()
    {
        byte[] ods = await Spreadsheet(writer =>
        {
            writer.BeginSheet("First", [new("a")]);
            writer.BeginRow();
            writer.Write("1");
            writer.EndRow();
            writer.BeginSheet("Second", [new("b")]);
            writer.BeginRow();
            writer.Write("2");
            writer.EndRow();
        });

        using OdsCursor cursor = new(new MemoryStream(ods, writable: false), cancellationToken: Token);
        Assert.Equal(["First", "Second"], cursor.Sheets.Select(s => s.Name));
        Assert.Equal(RawCell.FromText("2"), Rows(ods, 1)[1][0]);
    }

    [Fact]
    public async Task NamesSheetsAsWrittenEvenWithXmlCharacters()
    {
        byte[] ods = await Spreadsheet(writer => writer.BeginSheet("R&D <2026> \"q\"", [new("a")]));

        using OdsCursor cursor = new(new MemoryStream(ods, writable: false), cancellationToken: Token);
        Assert.Equal("R&D <2026> \"q\"", Assert.Single(cursor.Sheets).Name);
    }

    [Fact]
    public async Task WritesTextXmlCannotCarryLiterally()
    {
        string[] values = ["a & b < c > d", "Grüße 👍", "_x0041_ stays", "quote \" and apostrophe '"];

        Assert.Equal(values.Select(v => RawCell.FromText(v)), await Column(values));
    }

    [Fact]
    public async Task WritesSpacesTabsAndLineBreaksAsTheReaderReadsThem()
    {
        string[] values = ["a  b", "x   y", "tab\there", "\tlead tab", "two\nlines", "x\n\ny", "a\n  indented", "a \t b"];

        Assert.Equal(values.Select(v => RawCell.FromText(v)), await Column(values));
    }

    [Fact]
    public async Task KeepsACarriageReturnExactly()
    {
        string[] values = ["windows\r\nline", "lone\rreturn", "mixed\r\n\nlines\r"];

        Assert.Equal(values.Select(v => RawCell.FromText(v)), await Column(values));
        Assert.Contains("office:string-value=\"windows&#13;&#10;line\"", Content(await Spreadsheet(writer =>
        {
            writer.BeginSheet("data", [new("v")]);
            writer.BeginRow();
            writer.Write("windows\r\nline");
            writer.EndRow();
        })), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WritesColumnWidthsRoundedToWholeCharacters()
    {
        byte[] ods = await Spreadsheet(writer => writer.BeginSheet("data", [new("a", 12.4), new("b"), new("c", 30)]));

        string content = Content(ods);

        Assert.Contains("<table:table-column table:style-name=\"co12\"/><table:table-column/><table:table-column table:style-name=\"co30\"/>", content, StringComparison.Ordinal);
        Assert.Contains("style:name=\"co255\"", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesTextLongerThanTheReaderReads()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Ods);
        writer.BeginSheet("data", [new("v")]);
        writer.BeginRow();

        Assert.Equal(ErrorCodes.Write.TextTooLong, Assert.Throws<TabularWriteException>(() => writer.Write(new string('x', OdsCursorOptions.Default.MaxValueChars + 1))).Code);
    }

    [Theory]
    [InlineData("History")]
    [InlineData("a/b")]
    [InlineData("a\tb")]
    public async Task RefusesASheetNameExcelRefuses(string name)
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Ods);

        Assert.Throws<ArgumentException>(() => writer.BeginSheet(name, [new("a")]));
    }

    [Fact]
    public async Task ASpreadsheetDisposedBeforeCompletingDoesNotOpen()
    {
        WriteTarget target = new();
        Random random = new(72);

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Ods))
        {
            writer.BeginSheet("data", [new("v")]);

            for (int i = 0; i < 50_000; i++)
            {
                writer.BeginRow();
                writer.Write($"{random.NextInt64():x16} a value long enough that a flush writes part of the sheet");
                writer.EndRow();
            }

            await writer.FlushAsync(Token);
        }

        byte[] partial = target.ToArray();
        Assert.True(partial.Length > 64 * 1024, $"only {partial.Length} bytes went out");

        TabularFormatException refused = Assert.Throws<TabularFormatException>(() => new OdsCursor(new MemoryStream(partial, writable: false), cancellationToken: Token));
        Assert.Contains(refused.Code, new[] { ErrorCodes.Format.Corrupt, ErrorCodes.Format.Truncated, ErrorCodes.Format.Unsupported });
    }

    [Fact]
    public async Task TouchesTheTargetOnlyAsynchronously()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Ods))
        {
            writer.BeginSheet("data", [new("v")]);

            for (int i = 0; i < 100_000; i++)
            {
                writer.BeginRow();
                writer.Write("row value");
                writer.EndRow();

                if (writer.FlushRecommended)
                {
                    await writer.FlushAsync(Token);
                }
            }

            await writer.CompleteAsync(Token);
        }

        Assert.False(target.DisposedSynchronously);
        Assert.Equal(100_001, Rows(target.ToArray()).Count);
    }

    [Fact]
    public void RefusesACompressionLevelItDoesNotKnow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TabularWriter.Create(
            new MemoryStream(),
            TabularFormat.Ods,
            new TabularWriterOptions { Ods = new OdsWriterOptions { CompressionLevel = (CompressionLevel)42 } }));
    }
}
```

In `tests/TriasDev.Tabular.Tests/Writing/TabularWriterTests.cs`, remove `[InlineData(TabularFormat.Ods)]` from `RefusesAFormatItCannotWriteYet`.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~OdsWriterTests"`
Expected: build FAIL — `OdsWriterOptions` does not exist.

- [ ] **Step 3: Implement the options**

Create `src/TriasDev.Tabular/Ods/OdsWriterOptions.cs`:

```csharp
using System.IO.Compression;

namespace TriasDev.Tabular.Ods;

/// <summary>Knobs for writing an OpenDocument spreadsheet.</summary>
public sealed record OdsWriterOptions
{
    /// <summary>The defaults: the fastest compression.</summary>
    public static OdsWriterOptions Default { get; } = new();

    /// <summary>
    /// How hard <c>content.xml</c> is compressed: <see cref="CompressionLevel.Fastest"/> by default,
    /// since writing speed is the point; <see cref="CompressionLevel.Optimal"/> writes a smaller file
    /// more slowly.
    /// </summary>
    public CompressionLevel CompressionLevel { get; init; } = CompressionLevel.Fastest;

    internal OdsWriterOptions Checked()
    {
        if (!Enum.IsDefined(CompressionLevel))
        {
            throw new ArgumentOutOfRangeException(
                nameof(CompressionLevel),
                CompressionLevel,
                $"{nameof(OdsWriterOptions)}.{nameof(CompressionLevel)} is not a compression level.");
        }

        return this;
    }
}
```

(Use the same S3928 pragma-with-justification as `XlsxWriterOptions` if the analyzer asks.)

In `src/TriasDev.Tabular/Writing/TabularWriterOptions.cs`, add `using TriasDev.Tabular.Ods;` and after `Xlsx`:

```csharp
    /// <summary>How an OpenDocument spreadsheet is written.</summary>
    public OdsWriterOptions Ods { get; init; } = OdsWriterOptions.Default;
```

- [ ] **Step 4: Implement the package parts**

Create `src/TriasDev.Tabular/Ods/OdsParts.cs`:

```csharp
using System.Globalization;
using System.Text;

namespace TriasDev.Tabular.Ods;

/// <summary>The fixed parts of an OpenDocument spreadsheet, and the start and end of its content.</summary>
internal static class OdsParts
{
    public const string Mimetype = "application/vnd.oasis.opendocument.spreadsheet";

    /// <summary>The widest column style declared, in characters — Excel's own limit, which the writer enforces.</summary>
    public const int WidestColumn = 255;

    /// <summary>Cell styles: a date, a date and time, a boolean.</summary>
    public const string DateStyle = "ce1";

    public const string DateTimeStyle = "ce2";

    public const string BooleanStyle = "ce3";

    public const string ContentEnd = "</table:table></office:spreadsheet></office:body></office:document-content>";

    private const string XmlDeclaration = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>";

    private const string Namespaces =
        " xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\""
        + " xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\""
        + " xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\""
        + " xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\""
        + " xmlns:number=\"urn:oasis:names:tc:opendocument:xmlns:datastyle:1.0\""
        + " xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\""
        + " office:version=\"1.3\"";

    private static readonly string[] ColumnStyleNames = [.. Enumerable.Range(0, WidestColumn + 1).Select(i => string.Create(CultureInfo.InvariantCulture, $"co{i}"))];

    /// <summary>
    /// Everything <c>content.xml</c> holds before its first table: the automatic styles every table
    /// may use, declared up front because the part streams and later sheets' widths are not yet known.
    /// </summary>
    public static readonly string ContentStart = BuildContentStart();

    public static readonly byte[] Manifest = Encoding.UTF8.GetBytes(
        XmlDeclaration
        + "<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"1.3\">"
        + "<manifest:file-entry manifest:full-path=\"/\" manifest:version=\"1.3\" manifest:media-type=\"" + Mimetype + "\"/>"
        + "<manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/>"
        + "<manifest:file-entry manifest:full-path=\"styles.xml\" manifest:media-type=\"text/xml\"/>"
        + "</manifest:manifest>");

    public static readonly byte[] Styles = Encoding.UTF8.GetBytes(
        XmlDeclaration + "<office:document-styles" + Namespaces + "><office:styles/></office:document-styles>");

    /// <summary>The name of the column style for a width of so many whole characters.</summary>
    public static string ColumnStyleName(int chars) => ColumnStyleNames[chars];

    /// <summary>The column style for a width in characters, rounded to the nearest whole one.</summary>
    public static string ColumnStyleFor(double width) =>
        ColumnStyleNames[Math.Clamp((int)Math.Round(width, MidpointRounding.AwayFromZero), 1, WidestColumn)];

    private static string BuildContentStart()
    {
        StringBuilder xml = new(XmlDeclaration + "<office:document-content" + Namespaces + "><office:automatic-styles>");

        for (int chars = 1; chars <= WidestColumn; chars++)
        {
            // Excel's measure, which WriteColumn's width is in: a character is 7 pixels, plus 5 of
            // padding, at 96 pixels to the inch.
            double inches = ((chars * 7) + 5) / 96d;
            xml.Append(CultureInfo.InvariantCulture, $"<style:style style:name=\"{ColumnStyleNames[chars]}\" style:family=\"table-column\"><style:table-column-properties style:column-width=\"{inches:0.####}in\"/></style:style>");
        }

        xml.Append(
            "<number:date-style style:name=\"N1\"><number:year number:style=\"long\"/><number:text>-</number:text>"
            + "<number:month number:style=\"long\"/><number:text>-</number:text><number:day number:style=\"long\"/></number:date-style>"
            + "<number:date-style style:name=\"N2\"><number:year number:style=\"long\"/><number:text>-</number:text>"
            + "<number:month number:style=\"long\"/><number:text>-</number:text><number:day number:style=\"long\"/>"
            + "<number:text> </number:text><number:hours number:style=\"long\"/><number:text>:</number:text>"
            + "<number:minutes number:style=\"long\"/><number:text>:</number:text><number:seconds number:style=\"long\"/></number:date-style>"
            + "<number:boolean-style style:name=\"N3\"><number:boolean/></number:boolean-style>"
            + "<style:style style:name=\"" + DateStyle + "\" style:family=\"table-cell\" style:data-style-name=\"N1\"/>"
            + "<style:style style:name=\"" + DateTimeStyle + "\" style:family=\"table-cell\" style:data-style-name=\"N2\"/>"
            + "<style:style style:name=\"" + BooleanStyle + "\" style:family=\"table-cell\" style:data-style-name=\"N3\"/>"
            + "</office:automatic-styles><office:body><office:spreadsheet>");

        return xml.ToString();
    }
}
```

`ContentEnd` closes the last table too: the writer opens `<table:table>` per sheet and closes the previous one when the next begins (Step 5).

- [ ] **Step 5: Implement the sheet writer**

Create `src/TriasDev.Tabular/Ods/OdsSheetWriter.cs`:

```csharp
using System.Buffers;
using System.Globalization;

namespace TriasDev.Tabular.Ods;

/// <summary>
/// Writes an OpenDocument spreadsheet: the <c>mimetype</c> first and stored, every sheet in one
/// deflated <c>content.xml</c> written row by row, the manifest and styles at the end.
/// </summary>
/// <remarks>
/// <para>
/// Text goes into paragraphs as LibreOffice shows it: a line break starts a paragraph, a tab is
/// <c>&lt;text:tab/&gt;</c>, and spaces that ODF would collapse — at the start of a paragraph, after
/// a tab, or after another space — are counted in <c>&lt;text:s/&gt;</c>.
/// </para>
/// <para>
/// A carriage return has no paragraph form that keeps it, so text holding one also states its exact
/// value in <c>office:string-value</c>, which the reader prefers over the paragraphs. Character
/// references keep the CR, LF and tab in that attribute from being normalized into spaces.
/// </para>
/// </remarks>
internal sealed class OdsSheetWriter : ISheetWriter
{
    private const int RetainedBytes = 3 * RowText.RetainedChars;

    private static readonly int MaxTextChars = OdsCursorOptions.Default.MaxValueChars;

    private static readonly SearchValues<char> NeedsMarkup = SearchValues.Create("&<> \t\n\r");

    private readonly ZipWriter _zip;
    private readonly RowText _row = new();
    private ArrayBufferWriter<byte> _bytes = new(16 * 1024);
    private Stream? _content;

    public OdsSheetWriter(SpillBuffer output, OdsWriterOptions options)
    {
        _zip = new ZipWriter(output, options.CompressionLevel);

        // OpenDocument's rule: the mimetype is the first entry, stored, so a reader knows the file
        // from its first bytes.
        _zip.AddStored("mimetype", "application/vnd.oasis.opendocument.spreadsheet"u8);
    }

    public long MaxRows => 1_048_576;

    public bool AllowsSeveralSheets => true;

    public bool NamesSheets => true;

    public void BeginSheet(string name, ReadOnlySpan<WriteColumn> columns)
    {
        _row.Clear();

        if (_content is null)
        {
            _content = _zip.BeginDeflated("content.xml");
            _row.Append(OdsParts.ContentStart);
        }
        else
        {
            _row.Append("</table:table>");
        }

        _row.Append("<table:table table:name=\"");
        AppendAttribute(name);
        _row.Append("\">");
        AppendColumns(columns);
        Emit();
    }

    public void BeginRow()
    {
        _row.Clear();
        _row.Append("<table:table-row>");
    }

    public string? WriteHeader(string value) => WriteString(value);

    public string? WriteText(string value, int column) => WriteString(value);

    public string? WriteLong(long value) => throw new NotSupportedException("Numbers arrive with the next change.");

    public string? WriteDecimal(decimal value) => throw new NotSupportedException("Numbers arrive with the next change.");

    public string? WriteDouble(double value) => throw new NotSupportedException("Numbers arrive with the next change.");

    public string? WriteDate(DateTime value, bool hasTime) => throw new NotSupportedException("Dates arrive with the next change.");

    public void WriteBoolean(bool value)
    {
        _row.Append("<table:table-cell table:style-name=\"");
        _row.Append(OdsParts.BooleanStyle);
        _row.Append("\" office:value-type=\"boolean\" office:boolean-value=\"");
        _row.Append(value ? "true" : "false");
        _row.Append("\"><text:p>");
        _row.Append(value ? "TRUE" : "FALSE");
        _row.Append("</text:p></table:table-cell>");
    }

    public void WriteEmpty() => _row.Append("<table:table-cell/>");

    public void EndRow()
    {
        _row.Append("</table:table-row>");
        Emit();
    }

    public void Complete()
    {
        if (_content is not null)
        {
            _row.Clear();
            _row.Append(OdsParts.ContentEnd);
            Emit();
            _zip.EndEntry();
            _content = null;
        }

        _zip.AddStored("META-INF/manifest.xml", OdsParts.Manifest);
        _zip.AddStored("styles.xml", OdsParts.Styles);
        _zip.Complete();
    }

    public void Dispose()
    {
        // An abandoned spreadsheet: release the open entry's deflate state, write nothing more.
        _content?.Dispose();
        _content = null;
    }

    private string? WriteString(string value)
    {
        if (value.Length > MaxTextChars)
        {
            return ErrorCodes.Write.TextTooLong;
        }

        _row.Append("<table:table-cell office:value-type=\"string\"");

        if (value.Contains('\r', StringComparison.Ordinal))
        {
            _row.Append(" office:string-value=\"");
            AppendAttribute(value);
            _row.Append('"');
        }

        _row.Append("><text:p>");
        AppendParagraphs(value);
        _row.Append("</text:p></table:table-cell>");
        return null;
    }

    /// <summary>Writes text as paragraph content: line breaks as paragraphs, tabs and collapsible spaces as elements.</summary>
    private void AppendParagraphs(ReadOnlySpan<char> text)
    {
        bool boundary = true;     // at the start of a paragraph or after a tab, where ODF drops a space

        while (!text.IsEmpty)
        {
            int at = text.IndexOfAny(NeedsMarkup);

            if (at < 0)
            {
                _row.Append(text);
                return;
            }

            if (at > 0)
            {
                _row.Append(text[..at]);
                boundary = false;
            }

            char c = text[at];
            text = text[(at + 1)..];

            switch (c)
            {
                case '&':
                    _row.Append("&amp;");
                    boundary = false;
                    break;
                case '<':
                    _row.Append("&lt;");
                    boundary = false;
                    break;
                case '>':
                    _row.Append("&gt;");
                    boundary = false;
                    break;
                case '\t':
                    _row.Append("<text:tab/>");
                    boundary = true;
                    break;
                case '\r' when !text.IsEmpty && text[0] == '\n':
                    break;        // CR LF is one line break; the LF that follows writes it
                case '\r' or '\n':
                    _row.Append("</text:p><text:p>");
                    boundary = true;
                    break;
                default:
                    int run = 1 + (text.Length - text.TrimStart(' ').Length);
                    text = text[(run - 1)..];

                    if (!boundary)
                    {
                        _row.Append(' ');
                        run--;
                    }

                    if (run > 0)
                    {
                        _row.Append("<text:s text:c=\"");
                        _row.AppendFormatted(run, default, CultureInfo.InvariantCulture);
                        _row.Append("\"/>");
                    }

                    boundary = false;
                    break;
            }
        }
    }

    /// <summary>Writes text into a double-quoted attribute, keeping CR, LF and tab as character references.</summary>
    private void AppendAttribute(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            switch (c)
            {
                case '&':
                    _row.Append("&amp;");
                    break;
                case '<':
                    _row.Append("&lt;");
                    break;
                case '>':
                    _row.Append("&gt;");
                    break;
                case '"':
                    _row.Append("&quot;");
                    break;
                case '\r':
                    _row.Append("&#13;");
                    break;
                case '\n':
                    _row.Append("&#10;");
                    break;
                case '\t':
                    _row.Append("&#9;");
                    break;
                default:
                    _row.Append(c);
                    break;
            }
        }
    }

    private void AppendColumns(ReadOnlySpan<WriteColumn> columns)
    {
        foreach (WriteColumn column in columns)
        {
            if (column.Width is { } width)
            {
                _row.Append("<table:table-column table:style-name=\"");
                _row.Append(OdsParts.ColumnStyleFor(width));
                _row.Append("\"/>");
            }
            else
            {
                _row.Append("<table:table-column/>");
            }
        }
    }

    /// <summary>Encodes what the row buffer holds into content.xml, and empties it.</summary>
    private void Emit()
    {
        _row.WriteUtf8To(_bytes);
        _content!.Write(_bytes.WrittenSpan);
        _bytes.ResetWrittenCount();
        _row.Clear();

        if (_bytes.Capacity > RetainedBytes)
        {
            _bytes = new ArrayBufferWriter<byte>(16 * 1024);
        }
    }
}
```

`RowText.RetainedChars` is internal since part 2's final fix; if it is not, make it `internal const` (do not duplicate the number). If `ISheetWriter.Dispose` is declared differently in the code (check `XlsxSheetWriter.Dispose` and `ISheetWriter`), follow that shape.

- [ ] **Step 6: Let `Create` build an ods writer**

In `src/TriasDev.Tabular/Writing/TabularWriter.cs`, add `using TriasDev.Tabular.Ods;`; after the `effective.Xlsx is null` check add the same for `effective.Ods` (`ArgumentNullException` naming `TabularWriterOptions.Ods`, same pragma); in the `switch` before `default:`:

```csharp
                case TabularFormat.Ods:
                    OdsWriterOptions ods = effective.Ods.Checked();
                    SpillBuffer spreadsheet = new();
                    return new TabularWriter(stream, format, spreadsheet, new OdsSheetWriter(spreadsheet, ods), effective.LeaveOpen);
```

Update `Create`'s `<param name="format">` doc to: `The format to write: csv, xlsx or ods.`

- [ ] **Step 7: Record the public API, run the tests**

Append the RS0016 lines for `OdsWriterOptions` and `TabularWriterOptions.Ods`.

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~Writing"`
Expected: PASS. A failure in `WritesSpacesTabsAndLineBreaksAsTheReaderReadsThem` or `KeepsACarriageReturnExactly` means the writer disagrees with `OdsCursor.ReadText`/`ReadCell` — fix the writer, never the reader.

- [ ] **Step 8: Commit**

```bash
dotnet format TriasDev.Tabular.slnx --verify-no-changes
git add src/TriasDev.Tabular tests/TriasDev.Tabular.Tests
git commit -m "feat(write): stream OpenDocument spreadsheets — text, booleans, several sheets, widths"
```

---

### Task 3: Numbers and dates in ods

**Files:**
- Modify: `src/TriasDev.Tabular/Ods/OdsSheetWriter.cs` (replace the four interim members)
- Test: `tests/TriasDev.Tabular.Tests/Writing/OdsValueTests.cs`

**Interfaces:**
- Consumes: `ValueChecks.LongInDouble`, `ValueChecks.DecimalInDouble` (Task 1); `OdsParts.DateStyle`, `DateTimeStyle` (Task 2). `TabularWriter` has already refused non-finite and >15-digit doubles and truncated dates to the millisecond.
- Produces: the spec's ods value rules — numbers as `office:value-type="float"` with `office:value`, dates as `office:date-value` ISO with the date or date-time style; precision checks as xlsx; no date range limit (ods has no serial).

- [ ] **Step 1: Write the failing tests**

Create `tests/TriasDev.Tabular.Tests/Writing/OdsValueTests.cs`:

```csharp
using TriasDev.Tabular.Ods;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>How an ods writer writes numbers and dates, and which it refuses.</summary>
public sealed class OdsValueTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<RawCell> One(Action<TabularWriter> write)
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Ods))
        {
            writer.BeginSheet("data", [new("v")]);
            writer.BeginRow();
            write(writer);
            writer.EndRow();
            await writer.CompleteAsync(Token);
        }

        using OdsCursor cursor = new(new MemoryStream(target.ToArray(), writable: false), cancellationToken: Token);
        Assert.True(cursor.ReadRow(Token));
        Assert.True(cursor.ReadRow(Token));
        return cursor.CurrentRow[0];
    }

    private static DateTime At(int year, int month, int day, int hour = 0, int minute = 0, int second = 0, int millisecond = 0) =>
        new(year, month, day, hour, minute, second, millisecond, DateTimeKind.Unspecified);

    public static TheoryData<string, Action<TabularWriter>, RawCell> Written => new()
    {
        { "long", w => w.Write(42L), RawCell.FromNumber(42) },
        { "2^60, exact in a double", w => w.Write(1L << 60), RawCell.FromNumber(1L << 60) },
        { "long.MinValue", w => w.Write(long.MinValue), RawCell.FromNumber(long.MinValue) },
        { "decimal", w => w.Write(1234.5m), RawCell.FromNumber(1234.5) },
        { "decimal of 15 digits", w => w.Write(123_456_789_012.345m), RawCell.FromNumber(123_456_789_012.345) },
        { "double", w => w.Write(-0.1), RawCell.FromNumber(-0.1) },
        { "large double", w => w.Write(1e20), RawCell.FromNumber(1e20) },
        { "date", w => w.Write(At(2026, 10, 3)), RawCell.FromDate(At(2026, 10, 3)) },
        { "date-time", w => w.Write(At(2026, 10, 3, 14, 5, 6, 789)), RawCell.FromDate(At(2026, 10, 3, 14, 5, 6, 789)) },
        { "max value", w => w.Write(DateTime.MaxValue), RawCell.FromDate(At(9999, 12, 31, 23, 59, 59, 999)) },
        { "date only", w => w.Write(new DateOnly(2026, 10, 3)), RawCell.FromDate(At(2026, 10, 3)) },
    };

    [Theory]
    [MemberData(nameof(Written))]
    public async Task WritesEachKindOfValueAsTheCursorReadsIt(string kind, Action<TabularWriter> write, RawCell expected)
    {
        Assert.NotEmpty(kind);
        Assert.Equal(expected, await One(write));
    }

    [Fact]
    public async Task WritesDatesXlsxCannotHold()
    {
        DateTime[] dates = [At(1899, 12, 31), At(1900, 2, 28, 23, 59, 59, 999), At(1066, 10, 14, 9, 0, 0), At(2, 1, 1), DateTime.MinValue];

        foreach (DateTime date in dates)
        {
            Assert.Equal(RawCell.FromDate(date), await One(w => w.Write(date)));
        }
    }

    public static TheoryData<string, Action<TabularWriter>> Refused => new()
    {
        { "2^53 + 1", w => w.Write(9_007_199_254_740_993L) },
        { "long.MaxValue", w => w.Write(long.MaxValue) },
        { "16 significant digits", w => w.Write(1_234_567_890_123.456m) },
        { "decimal.MaxValue", w => w.Write(decimal.MaxValue) },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task RefusesANumberADoubleWouldNotGiveBack(string kind, Action<TabularWriter> write)
    {
        Assert.NotEmpty(kind);
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Ods);
        writer.BeginSheet("data", [new("Id"), new("Amount")]);
        writer.BeginRow();
        writer.Write("x");

        TabularWriteException refused = Assert.Throws<TabularWriteException>(() => write(writer));

        Assert.Equal(ErrorCodes.Write.PrecisionLoss, refused.Code);
        Assert.Equal(1, refused.ColumnIndex);
    }

    [Fact]
    public async Task StylesDatesAndDateTimesForLibreOffice()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Ods))
        {
            writer.BeginSheet("data", [new("d"), new("t")]);
            writer.BeginRow();
            writer.Write(At(2026, 10, 3));
            writer.Write(At(2026, 10, 3, 14, 5, 6));
            writer.EndRow();
            await writer.CompleteAsync(Token);
        }

        using System.IO.Compression.ZipArchive archive = new(new MemoryStream(target.ToArray(), writable: false), System.IO.Compression.ZipArchiveMode.Read);
        using StreamReader reader = new(archive.GetEntry("content.xml")!.Open());
        string content = reader.ReadToEnd();

        Assert.Contains("table:style-name=\"ce1\" office:value-type=\"date\" office:date-value=\"2026-10-03\"", content, StringComparison.Ordinal);
        Assert.Contains("table:style-name=\"ce2\" office:value-type=\"date\" office:date-value=\"2026-10-03T14:05:06.000\"", content, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~OdsValueTests"`
Expected: FAIL with `NotSupportedException`.

- [ ] **Step 3: Implement**

In `src/TriasDev.Tabular/Ods/OdsSheetWriter.cs`, replace the four interim members with:

```csharp
    public string? WriteLong(long value)
    {
        if (ValueChecks.LongInDouble(value) is { } code)
        {
            return code;
        }

        StartFloat();
        _row.AppendFormatted(value, default, CultureInfo.InvariantCulture);
        _row.Append(NumberEnd);
        return null;
    }

    public string? WriteDecimal(decimal value)
    {
        if (ValueChecks.DecimalInDouble(value) is { } code)
        {
            return code;
        }

        StartFloat();
        _row.AppendFormatted(value, default, CultureInfo.InvariantCulture);
        _row.Append(NumberEnd);
        return null;
    }

    public string? WriteDouble(double value)
    {
        StartFloat();
        _row.AppendFormatted(value, "R", CultureInfo.InvariantCulture);
        _row.Append(NumberEnd);
        return null;
    }

    public string? WriteDate(DateTime value, bool hasTime)
    {
        // ISO in an attribute: no serial, so no 1900 floor and no leap-year bug — any year reads back.
        _row.Append("<table:table-cell table:style-name=\"");
        _row.Append(hasTime ? OdsParts.DateTimeStyle : OdsParts.DateStyle);
        _row.Append("\" office:value-type=\"date\" office:date-value=\"");
        _row.AppendFormatted(value, hasTime ? "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fff" : "yyyy'-'MM'-'dd", CultureInfo.InvariantCulture);
        _row.Append("\"/>");
        return null;
    }
```

and add, next to the other private members:

```csharp
    private const string NumberEnd = "\"/>";

    /// <summary>Opens a number cell up to its value; LibreOffice formats the display from the value.</summary>
    private void StartFloat() => _row.Append("<table:table-cell office:value-type=\"float\" office:value=\"");
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~OdsValueTests|FullyQualifiedName~OdsWriterTests"`
Expected: PASS. If `DateTime.MinValue` or year 2 does not come back, check `OdsCursor.TypedValue` (`date.IndexOf('-') >= 4`, `DateReading.TryParseAsWritten`) and report the disagreement — do not change the reader or drop the case.

- [ ] **Step 5: Commit**

```bash
dotnet format TriasDev.Tabular.slnx --verify-no-changes
git add src/TriasDev.Tabular/Ods/OdsSheetWriter.cs tests/TriasDev.Tabular.Tests/Writing/OdsValueTests.cs
git commit -m "feat(write): numbers and dates in ods — any year, the precision rule xlsx keeps"
```

---

### Task 4: LibreOffice reads what we write — locally where it is installed, always in CI

**Files:**
- Create: `tests/TriasDev.Tabular.Tests/Fixtures/LibreOffice.cs`
- Create: `tests/TriasDev.Tabular.Tests/Writing/LibreOfficeInteropTests.cs`
- Modify: `.github/workflows/ci.yml` (the Linux build job installs LibreOffice and requires the test)

**Interfaces:**
- Produces: test fixture `LibreOffice.ConvertToCsv(byte[] file, string extension) → string[] lines` (null-free; the converted first sheet as UTF-8 lines), skipping the test when no `soffice` is found unless `TABULAR_REQUIRE_SOFFICE=1`.

- [ ] **Step 1: Write the fixture**

Create `tests/TriasDev.Tabular.Tests/Fixtures/LibreOffice.cs`:

```csharp
using System.Diagnostics;
using System.Text;

using Xunit;

namespace TriasDev.Tabular.Tests.Fixtures;

/// <summary>
/// LibreOffice headless, as the judge of whether a file we write is one a spreadsheet program opens.
/// </summary>
/// <remarks>
/// Skips where LibreOffice is not installed — unless <c>TABULAR_REQUIRE_SOFFICE</c> is set, as CI's
/// Linux job sets it, where a missing LibreOffice is a failure rather than a pass by absence.
/// </remarks>
public static class LibreOffice
{
    private static readonly string? Soffice = Find();

    /// <summary>Converts the file's first sheet to csv with LibreOffice and returns its lines.</summary>
    public static string[] ConvertToCsv(byte[] file, string extension)
    {
        if (Soffice is null)
        {
            Assert.False(Environment.GetEnvironmentVariable("TABULAR_REQUIRE_SOFFICE") == "1", "LibreOffice is required here but soffice was not found.");
            Assert.Skip("LibreOffice is not installed.");
        }

        string folder = Path.Combine(Path.GetTempPath(), "tabular-soffice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            string input = Path.Combine(folder, "file." + extension);
            File.WriteAllBytes(input, file);

            ProcessStartInfo start = new(Soffice!)
            {
                ArgumentList =
                {
                    "--headless",
                    $"-env:UserInstallation=file://{folder.Replace('\\', '/')}/profile",
                    "--convert-to",
                    "csv:Text - txt - csv (StarCalc):44,34,76",
                    "--outdir",
                    folder,
                    input,
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using Process process = Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(120_000), "LibreOffice did not finish within two minutes.");

            string csv = Path.Combine(folder, "file.csv");
            Assert.True(File.Exists(csv), $"LibreOffice could not convert the file: {output}");

            return File.ReadAllLines(csv, Encoding.UTF8);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string? Find()
    {
        string[] candidates =
        [
            "/Applications/LibreOffice.app/Contents/MacOS/soffice",
            @"C:\Program Files\LibreOffice\program\soffice.exe",
            .. (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => Path.Combine(directory, "soffice")),
        ];

        return candidates.FirstOrDefault(File.Exists);
    }
}
```

(`csv:Text - txt - csv (StarCalc):44,34,76` is LibreOffice's csv filter with comma, double quote and UTF-8.)

- [ ] **Step 2: Write the tests**

Create `tests/TriasDev.Tabular.Tests/Writing/LibreOfficeInteropTests.cs`:

```csharp
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>LibreOffice opens the workbooks we write and shows what we wrote.</summary>
public sealed class LibreOfficeInteropTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<byte[]> Write(TabularFormat format)
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, format))
        {
            writer.BeginSheet("R&D <2026>", [new("Name", 20), new("Count"), new("Amount"), new("Start"), new("Active")]);
            writer.BeginRow();
            writer.Write("Grüße  two spaces");
            writer.Write(42L);
            writer.Write(1234m);
            writer.Write(new DateTime(2026, 10, 3, 9, 0, 0, DateTimeKind.Unspecified));
            writer.Write(true);
            writer.EndRow();
            writer.BeginRow();
            writer.Write("tab\there");
            writer.Write(-7L);
            writer.Write(-25m);
            writer.Write(new DateTime(2026, 10, 3, 14, 5, 6, DateTimeKind.Unspecified));
            writer.Write(false);
            writer.EndRow();
            writer.BeginSheet("Second", [new("x")]);
            await writer.CompleteAsync(Token);
        }

        return target.ToArray();
    }

    [Theory]
    [InlineData(TabularFormat.Ods, "ods")]
    [InlineData(TabularFormat.Xlsx, "xlsx")]
    public async Task OpensWhatWeWriteAndShowsTheValues(TabularFormat format, string extension)
    {
        string[] lines = LibreOffice.ConvertToCsv(await Write(format), extension);

        Assert.Equal("Name,Count,Amount,Start,Active", lines[0]);
        Assert.StartsWith("Grüße  two spaces,42,1234,2026-10-03 09:00:00,", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("\"tab\there\",-7,-25,2026-10-03 14:05:06,", lines[2], StringComparison.Ordinal);
    }
}
```

The values are chosen so the assertions do not depend on LibreOffice's locale: whole numbers (a fraction would show `1234,5` under a German locale), date-times (both formats write them through an explicit `yyyy-mm-dd hh:mm:ss` style; a date alone in xlsx uses the built-in format 14, which LibreOffice shows in the locale's short form). The boolean column's text depends on the locale (`TRUE`/`WAHR`), hence `StartsWith`. If LibreOffice shows the date-time differently from `2026-10-03 14:05:06` — it formats it with our `N2` style for ods and our `164` format for xlsx — report what it shows rather than loosening the assertion. If LibreOffice does not quote a field holding a tab, adjust only that literal to what it writes and note it in the report.

- [ ] **Step 3: Run locally**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~LibreOfficeInteropTests"`
Expected: PASS where LibreOffice is installed (macOS: `/Applications/LibreOffice.app`), SKIPPED otherwise. A conversion failure is a finding about the writer: report LibreOffice's output.

- [ ] **Step 4: Require it in CI**

In `.github/workflows/ci.yml`, in the `build-and-test` job, add before the `Test` step:

```yaml
      # LibreOffice is the judge of the ods (and xlsx) we write; the interop tests skip where it is
      # absent, so the Linux job installs it and insists.
      - name: Install LibreOffice (Linux)
        if: runner.os == 'Linux'
        run: |
          sudo apt-get update
          sudo apt-get install -y --no-install-recommends libreoffice-calc
```

and give the `Test` step an environment variable only on Linux:

```yaml
      - name: Test (net8.0 and net10.0)
        run: dotnet test --solution TriasDev.Tabular.slnx --no-build --configuration Release
        env:
          TABULAR_REQUIRE_SOFFICE: ${{ runner.os == 'Linux' && '1' || '' }}
```

- [ ] **Step 5: Commit**

```bash
dotnet format TriasDev.Tabular.slnx --verify-no-changes
git add tests/TriasDev.Tabular.Tests/Fixtures/LibreOffice.cs tests/TriasDev.Tabular.Tests/Writing/LibreOfficeInteropTests.cs .github/workflows/ci.yml
git commit -m "test(write): LibreOffice opens the ods and xlsx we write — required in CI on Linux"
```

---

### Task 5: The ods round trip, the row limit, and the docs

**Files:**
- Test: `tests/TriasDev.Tabular.Tests/Writing/OdsRoundTripTests.cs`
- Modify: `docs/error-codes.md` (the `write.*` row names ods's causes)
- Modify: `docs/KNOWN-ISSUES.md` (the writing section: ods's display notes)

**Interfaces:**
- Consumes: everything above; `TabularImporter.Import<T>(Stream, string, MappingPlan, ImportSchema, TabularRowMapper<T>, ImportOptions?, CancellationToken)`, `ImportRun<T>.ReadRows`, `ImportRun<T>.Summary.RowsSkipped`, the typed `ImportRow` indexers.
- Produces: the proof that ods meets the spec's round-trip rule.

- [ ] **Step 1: Write the tests**

Create `tests/TriasDev.Tabular.Tests/Writing/OdsRoundTripTests.cs`:

```csharp
using TriasDev.Tabular.Ods;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>What the ods writer writes, the import reads back as the same value of the same type.</summary>
public sealed class OdsRoundTripTests
{
    private static readonly TextImportField NameField = ImportField.Text("Name");
    private static readonly IntegerImportField CountField = ImportField.Integer("Count");
    private static readonly DecimalImportField AmountField = ImportField.Decimal("Amount");
    private static readonly DateImportField StartField = ImportField.Date("Start");
    private static readonly BooleanImportField ActiveField = ImportField.Boolean("Active");

    private static readonly ImportSchema Schema = new() { Fields = [NameField, CountField, AmountField, StartField, ActiveField] };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed record Row(string? Name, long? Count, decimal? Amount, DateTime? Start, bool? Active);

    private static DateTime At(int year, int month, int day, int hour = 0, int minute = 0, int second = 0, int millisecond = 0) =>
        new(year, month, day, hour, minute, second, millisecond, DateTimeKind.Unspecified);

    private static MappingPlan Plan(int sheetIndex = 0) => new()
    {
        SheetIndex = sheetIndex,
        Bindings = [.. Schema.Fields.Select((f, i) => new ColumnBinding { ColumnIndex = i, Header = f.Name, FieldName = f.Name })],
    };

    private static async Task<byte[]> WriteAsync(Action<TabularWriter> rows)
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Ods))
        {
            writer.BeginSheet("data", [.. Schema.Fields.Select(f => new WriteColumn(f.Name))]);
            rows(writer);
            await writer.CompleteAsync(Token);
        }

        return target.ToArray();
    }

    private static List<ImportOutcome<Row>> Import(byte[] file, int sheetIndex = 0)
    {
        using ImportRun<Row> run = TabularImporter.Import(
            new MemoryStream(file, writable: false),
            "t.ods",
            Plan(sheetIndex),
            Schema,
            row => new Row(row[NameField], row[CountField], row[AmountField], row[StartField], row[ActiveField]),
            cancellationToken: Token);

        return [.. run.ReadRows(Token)];
    }

    private static void WriteRow(TabularWriter writer, Row row)
    {
        writer.BeginRow();
        writer.Write(row.Name);
        writer.Write(row.Count!.Value);
        writer.Write(row.Amount!.Value);
        writer.Write(row.Start!.Value);
        writer.Write(row.Active!.Value);
        writer.EndRow();
    }

    [Fact]
    public async Task ReadsBackEveryKindOfValue()
    {
        Row[] written =
        [
            new("Alpha", 0, 0.1m, At(2026, 10, 3), true),
            new("Grüße, \"quoted\"; | & <tag>", -1, -1_234_567.891m, At(2026, 10, 3, 14, 5, 6, 789), false),
            new("007", long.MinValue, 123_456_789_012.345m, At(1066, 10, 14), true),
            new("two\nlines and\r\nwindows and a lone\rreturn", 9_007_199_254_740_992, 1.50m, At(1, 1, 1), false),
            new("emoji 👍 and _x0001_ and tab\there", 1L << 60, 0m, At(9999, 12, 31, 23, 59, 59, 999), true),
            new("a   run   of   spaces and\n\nan empty line", 42, -0.000001m, At(1899, 12, 31, 23, 59, 59, 999), false),
        ];

        byte[] file = await WriteAsync(writer =>
        {
            foreach (Row row in written)
            {
                WriteRow(writer, row);
            }
        });

        List<ImportOutcome<Row>> read = Import(file);

        Assert.All(read, outcome => Assert.False(outcome.HasErrors, string.Join(", ", outcome.Errors.Select(e => e.Code))));
        Assert.Equal(written, read.Select(outcome => outcome.Value));
    }

    [Fact]
    public async Task ReadsBackADoubleAsTheDecimalItsDigitsSay()
    {
        double[] values = [0.1, -2.5, 1e20, 1e-7, 123_456_789.012345];

        byte[] file = await WriteAsync(writer =>
        {
            foreach (double value in values)
            {
                writer.BeginRow();
                writer.Write("x");
                writer.WriteEmpty();
                writer.Write(value);
                writer.EndRow();
            }
        });

        Assert.Equal(values.Select(v => (decimal?)(decimal)v), Import(file).Select(outcome => outcome.Value!.Amount));
    }

    [Fact]
    public async Task ReadsEmptyAndWhitespaceTextAsNoValueAndSkipsAnEmptyRow()
    {
        byte[] file = await WriteAsync(writer =>
        {
            foreach (string? text in new[] { null, "", "   ", " padded " })
            {
                writer.BeginRow();
                writer.Write(text);
                writer.Write(1L);
                writer.EndRow();

                if (text is null)
                {
                    // Between data rows: the reader drops empty rows only at a sheet's end.
                    writer.BeginRow();
                    writer.EndRow();
                }
            }
        });

        using ImportRun<string?> run = TabularImporter.Import(new MemoryStream(file, writable: false), "t.ods", Plan(), Schema, row => row[NameField], cancellationToken: Token);

        Assert.Equal(new string?[] { null, null, null, "padded" }, run.ReadRows(Token).Select(outcome => outcome.Value));
        Assert.Equal(1, run.Summary.RowsSkipped);
    }

    [Fact]
    public async Task ImportsEachSheet()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Ods))
        {
            foreach (string sheet in new[] { "First", "Second" })
            {
                writer.BeginSheet(sheet, [.. Schema.Fields.Select(f => new WriteColumn(f.Name))]);
                WriteRow(writer, new Row(sheet, 1, 1m, At(2026, 1, 1), true));
            }

            await writer.CompleteAsync(Token);
        }

        Assert.Equal("First", Assert.Single(Import(target.ToArray(), 0)).Value!.Name);
        Assert.Equal("Second", Assert.Single(Import(target.ToArray(), 1)).Value!.Name);
    }

    [Fact]
    public async Task AMillionRowSheetReadsBackToItsLastRowAndTheNextIsRefused()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Ods))
        {
            writer.BeginSheet("data", [new("n")]);

            for (long n = 1; n < 1_048_576; n++)
            {
                writer.BeginRow();
                writer.Write(n);
                writer.EndRow();

                if (writer.FlushRecommended)
                {
                    await writer.FlushAsync(Token);
                }
            }

            await writer.CompleteAsync(Token);
        }

        using OdsCursor cursor = new(new MemoryStream(target.ToArray(), writable: false), cancellationToken: Token);
        RawCell last = default;
        int rows = 0;

        while (cursor.ReadRow(Token))
        {
            rows++;
            last = cursor.CurrentRow[0];
        }

        Assert.Equal(1_048_576, rows);
        Assert.Equal(RawCell.FromNumber(1_048_575), last);

        await using TabularWriter full = TabularWriter.Create(new WriteTarget(), TabularFormat.Ods);
        full.BeginSheet("data", [new("n")]);

        for (long n = 1; n < 1_048_576; n++)
        {
            full.BeginRow();
            full.EndRow();
        }

        Assert.Throws<TabularLimitException>(() => full.BeginRow());
    }

    [Theory]
    [InlineData(67)]
    [InlineData(72)]
    [InlineData(2026)]
    public async Task WhatTheWriterAcceptsTheImportReadsBack(int seed)
    {
        Random random = new(seed);
        string[] alphabet = ["a", "b", "c", "X", "Y", "Z", "0", "1", "9", " ", ",", ";", "\t", "|", "\"", "\n", "<", ">", "&", "_", "x", "ü", "ß", "👍", "\r"];
        List<Row> accepted = [];

        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Ods))
        {
            writer.BeginSheet("data", [.. Schema.Fields.Select(f => new WriteColumn(f.Name))]);

            for (int i = 0; i < 300; i++)
            {
                string text = string.Concat(Enumerable.Range(0, random.Next(1, 40)).Select(_ => alphabet[random.Next(alphabet.Length)]));
                decimal amount = random.Next(2) == 0
                    ? Math.Round((decimal)(random.NextDouble() * 2_000_000 - 1_000_000), random.Next(0, 6))
                    : decimal.Round(random.Next(-999_999_999, 999_999_999) + (decimal)random.NextDouble(), random.Next(4, 7));

                if (ValueChecks.DecimalInDouble(amount) is not null)
                {
                    amount = decimal.Round(amount, 2);
                }

                Row row = new(
                    text,
                    random.NextInt64(-(1L << 53), 1L << 53),
                    amount,
                    At(random.Next(1, 10_000), random.Next(1, 13), random.Next(1, 29), random.Next(0, 24), random.Next(0, 60), random.Next(0, 60), random.Next(0, 1000)),
                    random.Next(2) == 0);

                WriteRow(writer, row);
                accepted.Add(row with { Name = string.IsNullOrWhiteSpace(row.Name) ? null : row.Name.Trim() });
            }

            await writer.CompleteAsync(Token);
        }

        List<ImportOutcome<Row>> read = Import(target.ToArray());

        Assert.Equal(300, accepted.Count);
        Assert.Equal(accepted.Count, read.Count);

        for (int i = 0; i < accepted.Count; i++)
        {
            Assert.False(read[i].HasErrors, $"seed {seed}, row {i}: {string.Join(", ", read[i].Errors.Select(e => e.Code))}");
            Assert.True(accepted[i] == read[i].Value, $"seed {seed}, row {i}: wrote {accepted[i]}, read {read[i].Value}");
        }
    }
}
```

- [ ] **Step 2: Run them**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests --filter "FullyQualifiedName~OdsRoundTripTests"`
Expected: PASS. These tests prove earlier tasks, so a failure is a finding: **stop and report the value, what `content.xml` holds for that row, and what came back.** Do not loosen an expectation, and do not change the reader. If a fuzz row fails only because a text value is whitespace-plus-trim edge, report it with the seed. The million-row test takes several seconds.

- [ ] **Step 3: Update the docs**

In `docs/error-codes.md`, append to the `write.*` row's meaning: `In ods: the same precision rule as xlsx (precision-loss); text over 16,777,216 characters, the reader's limit (text-too-long); no date limit.`

In `docs/KNOWN-ISSUES.md`, in the writing section, append: `ods: column widths are rounded to whole characters; text holding a carriage return is written twice, as paragraphs for display and as office:string-value for the exact value.`

- [ ] **Step 4: Run the whole suite and the Release build**

Run: `dotnet test --solution TriasDev.Tabular.slnx`, `dotnet build TriasDev.Tabular.slnx -c Release`, `dotnet format TriasDev.Tabular.slnx --verify-no-changes`.
Expected: all green, 0 warnings, no format changes.

- [ ] **Step 5: Commit**

```bash
git add tests docs/error-codes.md docs/KNOWN-ISSUES.md
git commit -m "test(write): ods round-trips through the import, to the last of a million rows"
```

---

## After the last task

One pull request for part 3 (`feat(write): write OpenDocument spreadsheets through TabularWriter`), body referencing #72 and #67; merge when CI is green (the Linux job now runs LibreOffice). Then the part 4 plan (`TabularExport<T>`, #73).
