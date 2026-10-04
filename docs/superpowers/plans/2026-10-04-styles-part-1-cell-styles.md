# Styled export, part 1 — cell styles Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cells written by `TabularWriter` can carry a style — fill, font colour, bold, italic, number format, date format, horizontal alignment, wrap, thin border — in xlsx and ods, with nothing allocated per cell and the round trip through our import unchanged.

**Architecture:** A public immutable `CellStyle` (value equality) is registered once per writer with `writer.Style(style)`, which returns a `StyleId` (an int). Every `Write` gains a `StyleId` overload; `ISheetWriter` methods take the style index (0 = unstyled, the existing fast path). Each workbook format resolves a `(style index, value kind)` pair lazily into its own style (xlsx `cellXfs` index, ods common cell style name), caches it in an array, and writes its whole style part at `Complete`. Csv ignores styles.

**Tech Stack:** C# / .NET (net8.0 + net10.0), BCL only; xunit v3 on Microsoft.Testing.Platform; DocumentFormat.OpenXml validator (tests only); LibreOffice headless (interop tests).

**Spec:** `docs/superpowers/specs/2026-10-04-styled-columnar-export-design.md` §1 (the earlier `docs/superpowers/specs/2026-10-03-writing-design.md` still holds where §1 is silent). Issue #86.

## Global Constraints

- One package, no dependencies: `src/TriasDev.Tabular` uses the base class library only.
- Public types live in namespace `TriasDev.Tabular` (files under `src/TriasDev.Tabular/Writing/`); format internals in `TriasDev.Tabular.Xlsx` / `TriasDev.Tabular.Ods`.
- Every public member is listed in `src/TriasDev.Tabular/PublicAPI.Unshipped.txt` (RS0016/RS0017 fail the build otherwise — copy the exact line the analyzer reports).
- Analyzers are warnings-as-errors; `dotnet format --verify-no-changes` must pass (CI runs it).
- Nothing allocated per cell on the styled path once a `(style, kind)` pair is resolved; the unstyled path (style index 0) stays as it is.
- At most **4096** distinct styles per file; the next `Style` call throws `TabularLimitException` (limit name `"MaxStyles"`).
- Number and date formats are separate properties: `Number` applies to integer, decimal and double cells, `Date` to date and date-time cells; each is ignored elsewhere. A date cell without `Date` keeps the writer's default date / date-time format.
- Supported format codes (anything else → `ArgumentException` from `Parse`, naming the unsupported part):
  - `NumberFormat`: optional quoted prefix, integer part of `#`, `0`, `,` (all `#` before all `0`; a comma only between placeholders = thousands grouping), optional `.` followed by `0`s then `#`s (1–30), optional `%`, optional quoted suffix.
  - `DateFormat`: `yyyy`, `yy`, `m`, `mm`, `d`, `dd`, `h`, `hh`, `s`, `ss` (case-insensitive), separators `/ - . : , space`, quoted literals, backslash-escaped characters. `m`/`mm` is minutes when the previous non-literal part is an hour or the next non-literal part is a second, months otherwise. At least one non-literal part.
- xlsx: custom number formats start at id **165** (164 is the default date-time format); the four fixed cell formats 0 General, 1 date (14), 2 date-time (164), 3 integer (1) keep their indices.
- ods: cell styles are common styles in `styles.xml` (`office:styles`), with their data styles there too; `content.xml`'s automatic styles stay as they are.
- The repository is public: no product or customer names in code, docs or commits.
- Tests: `TestContext.Current.CancellationToken` as token; LibreOffice tests go through `Fixtures/LibreOffice.cs` (skips without soffice unless `TABULAR_REQUIRE_SOFFICE=1`).

Commands (from the repo root):

- Build: `dotnet build -c Release`
- One test class: `dotnet test --project tests/TriasDev.Tabular.Tests -c Release -- --filter-class "*ClassName"`
- All tests: `dotnet test -c Release`
- Format: `dotnet format --verify-no-changes`

## Review Focus

1. A style registered after the sheet began, in the middle of the data (row 1000), is written correctly — styles are lazy in both workbook formats. (Task 4, Task 5)
2. One `CellStyle` used on text, a number and a date in the same row: the number format reaches only the number, the date keeps a date format and reads back as a date. (Task 4, Task 5)
3. Literal text in a format code holding `&`, `<`, `"` (escaped) is written as valid XML in both formats. (Task 4, Task 5)
4. A `StyleId` that this writer did not hand out (taken from another writer with more styles) is refused with `ArgumentException`, not written as a wrong or dangling style index. (Task 3)
5. Styled empty cells and styled booleans read back as empty and boolean — the style does not turn them into text or drop the cell's type. (Task 4, Task 5)

---

### Task 1: Public style model

**Files:**
- Create: `src/TriasDev.Tabular/Writing/CellColor.cs`, `CellFont.cs`, `CellBorder.cs`, `HorizontalAlignment.cs`, `CellStyle.cs`, `StyleId.cs`
- Modify: `src/TriasDev.Tabular/PublicAPI.Unshipped.txt`
- Test: `tests/TriasDev.Tabular.Tests/Writing/CellStyleTests.cs`

**Interfaces:**
- Produces: `CellColor` (`FromRgb(int)`, `Parse(string)`, `Rgb`, `ToString()` → `#RRGGBB`), `CellFont { Color, Bold, Italic }`, `CellBorder.Thin(CellColor)` with `Color`, `HorizontalAlignment { General, Left, Center, Right }`, `CellStyle { Fill, Font, Horizontal, Wrap, Border }`, `StyleId` (internal `Value`, internal ctor). `CellStyle.Number` and `CellStyle.Date` are added by Task 2, which creates their types.

- [ ] **Step 1: Write the failing tests**

```csharp
using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>The style model: colours, fonts, borders and styles compare by value.</summary>
public sealed class CellStyleTests
{
    [Theory]
    [InlineData(0x000000, "#000000")]
    [InlineData(0xF8696B, "#F8696B")]
    [InlineData(0xFFFFFF, "#FFFFFF")]
    public void AColourIsTwentyFourBitRgb(int rgb, string text)
    {
        CellColor color = CellColor.FromRgb(rgb);

        Assert.Equal(rgb, color.Rgb);
        Assert.Equal(text, color.ToString());
        Assert.Equal(color, CellColor.Parse(text));
        Assert.Equal(color, CellColor.Parse(text.ToLowerInvariant()));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0x1000000)]
    public void AColourOutsideRgbIsRefused(int rgb) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => CellColor.FromRgb(rgb));

    [Theory]
    [InlineData("F8696B")]
    [InlineData("#F8696")]
    [InlineData("#F8696B0")]
    [InlineData("#G8696B")]
    [InlineData("")]
    public void AColourTextOtherThanHashAndSixHexDigitsIsRefused(string text) =>
        Assert.Throws<FormatException>(() => CellColor.Parse(text));

    [Fact]
    public void StylesWithTheSameSettingsAreEqual()
    {
        CellStyle a = new() { Fill = CellColor.FromRgb(0xFF0000), Font = new CellFont { Bold = true, Color = CellColor.FromRgb(0xFFFFFF) }, Border = CellBorder.Thin(CellColor.FromRgb(0x808080)), Horizontal = HorizontalAlignment.Center, Wrap = true };
        CellStyle b = new() { Fill = CellColor.FromRgb(0xFF0000), Font = new CellFont { Bold = true, Color = CellColor.FromRgb(0xFFFFFF) }, Border = CellBorder.Thin(CellColor.FromRgb(0x808080)), Horizontal = HorizontalAlignment.Center, Wrap = true };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, b with { Wrap = false });
    }

    [Fact]
    public void AnEmptyStyleChangesNothing()
    {
        CellStyle style = new();

        Assert.Null(style.Fill);
        Assert.Null(style.Font);
        Assert.Null(style.Border);
        Assert.Equal(HorizontalAlignment.General, style.Horizontal);
        Assert.False(style.Wrap);
    }

    [Fact]
    public void TheDefaultStyleIdIsTheUnstyledCell() => Assert.Equal(default, new StyleId());
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet build -c Release`
Expected: compile errors — `CellColor`, `CellStyle`, … do not exist.

- [ ] **Step 3: Implement**

`CellColor.cs`:

```csharp
using System.Globalization;

namespace TriasDev.Tabular;

/// <summary>A colour for a cell's fill, font or border: 24-bit RGB.</summary>
public readonly record struct CellColor
{
    private CellColor(int rgb) => Rgb = rgb;

    /// <summary>The colour as 0xRRGGBB.</summary>
    public int Rgb { get; }

    /// <summary>A colour from 0xRRGGBB.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rgb"/> is not between 0 and 0xFFFFFF.</exception>
    public static CellColor FromRgb(int rgb)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rgb);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rgb, 0xFFFFFF);
        return new CellColor(rgb);
    }

    /// <summary>A colour from <c>#RRGGBB</c>, hex digits in either case.</summary>
    /// <exception cref="FormatException">The text is not <c>#</c> and six hex digits.</exception>
    public static CellColor Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length != 7 || text[0] != '#' || !int.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int rgb))
        {
            throw new FormatException($"\"{text}\" is not a colour; write it as #RRGGBB.");
        }

        return new CellColor(rgb);
    }

    /// <summary>The colour as <c>#RRGGBB</c>.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"#{Rgb:X6}");
}
```

(`int.TryParse` with `AllowHexSpecifier` rejects a sign and accepts at most what fits; six digits always fit, and the length check makes "six" exact.)

`CellFont.cs`:

```csharp
namespace TriasDev.Tabular;

/// <summary>How a cell's text looks: its colour, bold, italic. Family and size stay the format's default.</summary>
public sealed record CellFont
{
    /// <summary>The text colour; null keeps the default (black).</summary>
    public CellColor? Color { get; init; }

    /// <summary>Bold text.</summary>
    public bool Bold { get; init; }

    /// <summary>Italic text.</summary>
    public bool Italic { get; init; }
}
```

`CellBorder.cs`:

```csharp
namespace TriasDev.Tabular;

/// <summary>A border on all four sides of a cell.</summary>
public sealed record CellBorder
{
    private CellBorder(CellColor color) => Color = color;

    /// <summary>The border's colour.</summary>
    public CellColor Color { get; }

    /// <summary>A thin line in a colour on all four sides.</summary>
    public static CellBorder Thin(CellColor color) => new(color);
}
```

`HorizontalAlignment.cs`:

```csharp
namespace TriasDev.Tabular;

/// <summary>Where a cell's content sits across the cell.</summary>
public enum HorizontalAlignment
{
    /// <summary>The format's default: text left, numbers and dates right.</summary>
    General,

    /// <summary>Left.</summary>
    Left,

    /// <summary>Centred.</summary>
    Center,

    /// <summary>Right.</summary>
    Right,
}
```

`CellStyle.cs` (Task 2 adds `Number` and `Date`):

```csharp
namespace TriasDev.Tabular;

/// <summary>
/// How a cell looks in a workbook. Every setting is optional; one left unset keeps the format's
/// default. Csv ignores styles.
/// </summary>
/// <remarks>
/// Compares by value: two styles with the same settings are the same style, and a file holds it
/// once. Declare the styles an export uses once (<c>static readonly</c>) and register each with
/// <see cref="TabularWriter.Style"/>.
/// </remarks>
public sealed record CellStyle
{
    /// <summary>The cell's background colour; null leaves it unfilled.</summary>
    public CellColor? Fill { get; init; }

    /// <summary>The text's colour, bold and italic; null keeps the default font.</summary>
    public CellFont? Font { get; init; }

    /// <summary>Where the content sits across the cell.</summary>
    public HorizontalAlignment Horizontal { get; init; }

    /// <summary>Wraps text onto several lines within the column's width.</summary>
    public bool Wrap { get; init; }

    /// <summary>A border on all four sides; null draws none.</summary>
    public CellBorder? Border { get; init; }
}
```

`StyleId.cs`:

```csharp
using System.Globalization;

namespace TriasDev.Tabular;

/// <summary>
/// A style registered with one <see cref="TabularWriter"/> by <see cref="TabularWriter.Style"/>, to
/// pass with each cell written in it. Valid only with the writer that returned it. The default value
/// is the unstyled cell.
/// </summary>
public readonly record struct StyleId
{
    internal StyleId(int value) => Value = value;

    /// <summary>The style's index in its writer's table; 0 is no style.</summary>
    internal int Value { get; }

    /// <inheritdoc/>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"StyleId({Value})");
}
```

`TabularWriter.Style` does not exist yet: the `<see cref>` to it fails to compile until Task 3. Write the remark text without the cref in this task (`registered with a writer's Style method`) and turn it into the cref in Task 3.

- [ ] **Step 4: Add the public API lines and run**

Run `dotnet build -c Release`, copy every RS0016 member line into `src/TriasDev.Tabular/PublicAPI.Unshipped.txt` (keep the file sorted as it is), rebuild.
Run: `dotnet test --project tests/TriasDev.Tabular.Tests -c Release -- --filter-class "*CellStyleTests"`
Expected: PASS (both target frameworks).

- [ ] **Step 5: Commit**

```bash
git add src/TriasDev.Tabular/Writing src/TriasDev.Tabular/PublicAPI.Unshipped.txt tests/TriasDev.Tabular.Tests/Writing/CellStyleTests.cs
git commit -m "feat(write): the cell style model — colour, font, border, alignment, wrap; StyleId"
```

---

### Task 2: Number and date format codes

**Files:**
- Create: `src/TriasDev.Tabular/Writing/NumberFormat.cs`, `src/TriasDev.Tabular/Writing/DateFormat.cs`
- Modify: `src/TriasDev.Tabular/Writing/CellStyle.cs` (add `Number`, `Date`), `PublicAPI.Unshipped.txt`
- Test: `tests/TriasDev.Tabular.Tests/Writing/FormatCodeTests.cs`

**Interfaces:**
- Consumes: `TextRules.Check(string)` (internal, returns an error code or null) for literal text.
- Produces (used by Tasks 4 and 5):
  - `public sealed record NumberFormat` — `public static NumberFormat Parse(string code)`, `public string Code` (canonical Excel code), internal `bool Grouping`, `int MinIntegerDigits`, `int DecimalPlaces`, `int MinDecimalPlaces`, `bool Percent`, `string Prefix`, `string Suffix`.
  - `public sealed record DateFormat` — `public static DateFormat Parse(string code)`, `public string Code` (canonical Excel code: literals escaped with `\`, except `:`), internal `IReadOnlyList<DatePart> Parts`; equality and hash by `Code` only.
  - `internal enum DatePartKind { Literal, Year, Month, Day, Hour, Minute, Second }`, `internal readonly record struct DatePart(DatePartKind Kind, bool Long, string Text)` (in `DateFormat.cs`).
  - `CellStyle.Number` (`NumberFormat?`), `CellStyle.Date` (`DateFormat?`).

- [ ] **Step 1: Write the failing tests**

```csharp
using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>The supported subset of Excel format codes: parsed into what both workbook formats can state.</summary>
public sealed class FormatCodeTests
{
    [Theory]
    [InlineData("0", false, 1, 0, 0, false, "", "")]
    [InlineData("0.00", false, 1, 2, 2, false, "", "")]
    [InlineData("#,##0", true, 1, 0, 0, false, "", "")]
    [InlineData("#,##0.00", true, 1, 2, 2, false, "", "")]
    [InlineData("0.0#", false, 1, 2, 1, false, "", "")]
    [InlineData("#.##", false, 0, 2, 0, false, "", "")]
    [InlineData("0%", false, 1, 0, 0, true, "", "")]
    [InlineData("0.0%", false, 1, 1, 1, true, "", "")]
    [InlineData("\"€ \"#,##0.00", true, 1, 2, 2, false, "€ ", "")]
    [InlineData("0\" kg\"", false, 1, 0, 0, false, "", " kg")]
    public void ParsesANumberFormat(string code, bool grouping, int minInteger, int decimals, int minDecimals, bool percent, string prefix, string suffix)
    {
        NumberFormat format = NumberFormat.Parse(code);

        Assert.Equal(code, format.Code);
        Assert.Equal(grouping, format.Grouping);
        Assert.Equal(minInteger, format.MinIntegerDigits);
        Assert.Equal(decimals, format.DecimalPlaces);
        Assert.Equal(minDecimals, format.MinDecimalPlaces);
        Assert.Equal(percent, format.Percent);
        Assert.Equal(prefix, format.Prefix);
        Assert.Equal(suffix, format.Suffix);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("General", "'G'")]
    [InlineData("0;-0", "';'")]
    [InlineData("[Red]0", "'['")]
    [InlineData("0.00E+00", "'E'")]
    [InlineData("# ?/?", "' '")]
    [InlineData("@", "'@'")]
    [InlineData("0#", "a # after a 0")]
    [InlineData("0.#0", "a 0 after a #")]
    [InlineData("0.", "decimal point")]
    [InlineData("0,", "comma")]
    [InlineData(",0", "comma")]
    [InlineData("\"open", "quote")]
    [InlineData("0.0000000000000000000000000000000", "30")]
    public void RefusesANumberFormatOutsideTheSubset(string code, string named)
    {
        ArgumentException refused = Assert.Throws<ArgumentException>(() => NumberFormat.Parse(code));
        Assert.Contains(named, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NumberFormatsWithTheSameCodeAreEqual() =>
        Assert.Equal(NumberFormat.Parse("#,##0.00"), NumberFormat.Parse("#,##0.00"));

    [Theory]
    [InlineData("dd/mm/yyyy", "dd\\/mm\\/yyyy", "Day,Literal,Month,Literal,Year")]
    [InlineData("yyyy-mm-dd hh:mm:ss", "yyyy\\-mm\\-dd\\ hh:mm:ss", "Year,Literal,Month,Literal,Day,Literal,Hour,Literal,Minute,Literal,Second")]
    [InlineData("d.m.yy", "d\\.m\\.yy", "Day,Literal,Month,Literal,Year")]
    [InlineData("hh:mm", "hh:mm", "Hour,Literal,Minute")]
    [InlineData("mm:ss", "mm:ss", "Minute,Literal,Second")]
    [InlineData("h \"h\" m", "h\\ \\h\\ m", "Hour,Literal,Minute")]
    [InlineData("DD/MM/YYYY", "dd\\/mm\\/yyyy", "Day,Literal,Month,Literal,Year")]
    [InlineData("yyyy\\Wdd", "yyyy\\Wdd", "Year,Literal,Day")]
    public void ParsesADateFormat(string code, string canonical, string kinds)
    {
        DateFormat format = DateFormat.Parse(code);

        Assert.Equal(canonical, format.Code);
        Assert.Equal(kinds, string.Join(',', format.Parts.Select(p => p.Kind)));
    }

    [Fact]
    public void MonthAndMinuteFollowExcelsRule()
    {
        DateFormat format = DateFormat.Parse("mm/dd hh:mm");

        Assert.Equal(DatePartKind.Month, format.Parts[0].Kind);
        Assert.Equal(DatePartKind.Minute, format.Parts[^1].Kind);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("mmm yyyy", "mmm")]
    [InlineData("dddd", "dddd")]
    [InlineData("yyy", "yyy")]
    [InlineData("hh:mm AM/PM", "'A'")]
    [InlineData("[h]:mm", "'['")]
    [InlineData("hh:mm:ss.000", "'0'")]
    [InlineData("\"only text\"", "no date or time part")]
    [InlineData("dd\\", "backslash")]
    [InlineData("dd\"open", "quote")]
    public void RefusesADateFormatOutsideTheSubset(string code, string named)
    {
        ArgumentException refused = Assert.Throws<ArgumentException>(() => DateFormat.Parse(code));
        Assert.Contains(named, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DateFormatsCompareByTheirCode()
    {
        Assert.Equal(DateFormat.Parse("dd/mm/yyyy"), DateFormat.Parse("DD/MM/YYYY"));
        Assert.Equal(DateFormat.Parse("dd/mm/yyyy").GetHashCode(), DateFormat.Parse("DD/MM/YYYY").GetHashCode());
        Assert.NotEqual(DateFormat.Parse("dd/mm/yyyy"), DateFormat.Parse("dd.mm.yyyy"));
    }

    [Fact]
    public void ALiteralWithAForbiddenCharacterIsRefused() =>
        Assert.Throws<ArgumentException>(() => NumberFormat.Parse("0\"\u0007\""));

    [Fact]
    public void AStyleCarriesBothFormats()
    {
        CellStyle style = new() { Number = NumberFormat.Parse("0.00"), Date = DateFormat.Parse("dd/mm/yyyy") };

        Assert.Equal(style, new CellStyle { Number = NumberFormat.Parse("0.00"), Date = DateFormat.Parse("dd/mm/yyyy") });
    }
}
```

`DatePart` and `DatePartKind` are internal; the test project already sees internals (it uses `TextRules`, `XlsxCursor`).

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet build -c Release`
Expected: compile errors — `NumberFormat`, `DateFormat` do not exist.

- [ ] **Step 3: Implement `NumberFormat`**

```csharp
using System.Globalization;

namespace TriasDev.Tabular;

/// <summary>
/// How a numeric cell shows its value, as an Excel format code from a subset both workbook formats
/// can state: <c>0</c>, <c>0.00</c>, <c>#,##0</c>, <c>#,##0.00</c>, <c>0.0#</c>, <c>0%</c>, and
/// quoted text before or after, as in <c>"€ "#,##0.00</c>.
/// </summary>
/// <remarks>
/// The value stays the number written; only its display changes. The decimal point and the
/// thousands separator show as the reader's locale has them.
/// </remarks>
public sealed record NumberFormat
{
    private const int MaxDecimals = 30;

    private NumberFormat(string code, bool grouping, int minIntegerDigits, int decimalPlaces, int minDecimalPlaces, bool percent, string prefix, string suffix)
    {
        Code = code;
        Grouping = grouping;
        MinIntegerDigits = minIntegerDigits;
        DecimalPlaces = decimalPlaces;
        MinDecimalPlaces = minDecimalPlaces;
        Percent = percent;
        Prefix = prefix;
        Suffix = suffix;
    }

    /// <summary>The format code, as parsed.</summary>
    public string Code { get; }

    internal bool Grouping { get; }

    internal int MinIntegerDigits { get; }

    internal int DecimalPlaces { get; }

    internal int MinDecimalPlaces { get; }

    internal bool Percent { get; }

    internal string Prefix { get; }

    internal string Suffix { get; }

    /// <summary>Parses a format code.</summary>
    /// <exception cref="ArgumentException">The code is outside the supported subset; the message names the part.</exception>
    public static NumberFormat Parse(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        if (code.Length == 0)
        {
            throw Refuse(code, "the code is empty");
        }

        int i = 0;
        string prefix = FormatCodes.ReadQuoted(code, ref i);

        int integerStart = i;
        int digits = 0;
        int zeros = 0;
        bool grouping = false;

        for (; i < code.Length && code[i] is '#' or '0' or ','; i++)
        {
            switch (code[i])
            {
                case '#' when zeros > 0:
                    throw Refuse(code, "a # after a 0 in the integer part");
                case '#':
                    digits++;
                    break;
                case '0':
                    zeros++;
                    digits++;
                    break;
                default:
                    if (digits == 0 || i + 1 >= code.Length || code[i + 1] is not ('#' or '0'))
                    {
                        throw Refuse(code, "a comma that is not a thousands separator between digit placeholders");
                    }

                    grouping = true;
                    break;
            }
        }

        if (digits == 0)
        {
            throw Refuse(code, i < code.Length ? FormatCodes.At(code, i) : "no digit placeholder (0 or #) in the integer part");
        }

        string integer = code[integerStart..i];
        string decimalsText = string.Empty;
        int decimals = 0;
        int minDecimals = 0;

        if (i < code.Length && code[i] == '.')
        {
            i++;
            int decimalStart = i;
            bool optional = false;

            for (; i < code.Length && code[i] is '0' or '#'; i++)
            {
                if (code[i] == '0' && optional)
                {
                    throw Refuse(code, "a 0 after a # in the decimals");
                }

                optional |= code[i] == '#';
                minDecimals += code[i] == '0' ? 1 : 0;
                decimals++;
            }

            if (decimals == 0)
            {
                throw Refuse(code, "a decimal point without digit placeholders after it");
            }

            if (decimals > MaxDecimals)
            {
                throw Refuse(code, string.Create(CultureInfo.InvariantCulture, $"more than {MaxDecimals} decimal places"));
            }

            decimalsText = code[decimalStart..i];
        }

        bool percent = i < code.Length && code[i] == '%';
        i += percent ? 1 : 0;

        string suffix = FormatCodes.ReadQuoted(code, ref i);

        if (i < code.Length)
        {
            throw Refuse(code, FormatCodes.At(code, i));
        }

        string canonical = FormatCodes.Quote(prefix) + integer + (decimals > 0 ? "." + decimalsText : string.Empty) + (percent ? "%" : string.Empty) + FormatCodes.Quote(suffix);
        return new NumberFormat(canonical, grouping, zeros, decimals, minDecimals, percent, prefix, suffix);
    }

    /// <inheritdoc/>
    public override string ToString() => Code;

    private static ArgumentException Refuse(string code, string what) =>
        new($"The number format \"{code}\" is not supported: {what}.", nameof(code));
}
```

The shared helpers go into `DateFormat.cs` as an internal static class (both parsers use them):

```csharp
/// <summary>The pieces of format-code parsing both parsers share.</summary>
internal static class FormatCodes
{
    /// <summary>Reads a double-quoted literal at <paramref name="i"/>, if one starts there; "" otherwise.</summary>
    public static string ReadQuoted(string code, ref int i)
    {
        if (i >= code.Length || code[i] != '"')
        {
            return string.Empty;
        }

        int end = code.IndexOf('"', i + 1);

        if (end < 0)
        {
            throw new ArgumentException($"The format \"{code}\" is not supported: a quote at position {i + 1} that never closes.", nameof(code));
        }

        string text = code[(i + 1)..end];

        if (TextRules.Check(text) is { } problem)
        {
            throw new ArgumentException($"The format \"{code}\" is not supported: its quoted text cannot be written ({problem}).", nameof(code));
        }

        i = end + 1;
        return text;
    }

    /// <summary>The literal as a quoted run of a format code, or "" for none.</summary>
    public static string Quote(string text) => text.Length == 0 ? string.Empty : "\"" + text + "\"";

    /// <summary>Names the character at a position, for a refusal.</summary>
    public static string At(string code, int i) => string.Create(CultureInfo.InvariantCulture, $"'{code[i]}' at position {i + 1}");
}
```

- [ ] **Step 4: Implement `DateFormat`**

Add to `DateFormat.cs` (with `using System.Globalization; using System.Text;`):

```csharp
/// <summary>What a part of a date format shows.</summary>
internal enum DatePartKind
{
    Literal,
    Year,
    Month,
    Day,
    Hour,
    Minute,
    Second,
}

/// <summary>One part of a date format: a field (long = two digits, or four for the year) or literal text.</summary>
internal readonly record struct DatePart(DatePartKind Kind, bool Long, string Text);

/// <summary>
/// How a date or date-time cell shows its value, as an Excel format code from a subset both workbook
/// formats can state: <c>yyyy</c> <c>yy</c> <c>m</c> <c>mm</c> <c>d</c> <c>dd</c> <c>h</c> <c>hh</c>
/// <c>s</c> <c>ss</c>, separators <c>/ - . : , space</c>, quoted or backslash-escaped text — as in
/// <c>dd/mm/yyyy</c> or <c>yyyy-mm-dd hh:mm</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>m</c> and <c>mm</c> are minutes right after an hour or right before a second, months
/// otherwise — Excel's rule. The separators are written as literal text, so <c>/</c> shows as a
/// slash whatever the reader's locale.
/// </para>
/// <para>The value stays the date written; only its display changes. Compares by <see cref="Code"/>.</para>
/// </remarks>
public sealed record DateFormat
{
    private DateFormat(string code, DatePart[] parts)
    {
        Code = code;
        Parts = parts;
    }

    /// <summary>The format code as written into a workbook: lower-case fields, separators escaped.</summary>
    public string Code { get; }

    internal IReadOnlyList<DatePart> Parts { get; }

    /// <summary>Parses a format code.</summary>
    /// <exception cref="ArgumentException">The code is outside the supported subset; the message names the part.</exception>
    public static DateFormat Parse(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        if (code.Length == 0)
        {
            throw Refuse(code, "the code is empty");
        }

        List<DatePart> parts = [];
        StringBuilder literal = new();
        int i = 0;

        while (i < code.Length)
        {
            char c = code[i];

            if (c == '"')
            {
                literal.Append(FormatCodes.ReadQuoted(code, ref i));
                continue;
            }

            if (c == '\\')
            {
                if (i + 1 >= code.Length)
                {
                    throw Refuse(code, "a backslash at the end, escaping nothing");
                }

                literal.Append(code[i + 1]);
                i += 2;
                continue;
            }

            if (c is '/' or '-' or '.' or ':' or ',' or ' ')
            {
                literal.Append(c);
                i++;
                continue;
            }

            char letter = char.ToLowerInvariant(c);

            if (letter is not ('y' or 'm' or 'd' or 'h' or 's'))
            {
                throw Refuse(code, FormatCodes.At(code, i));
            }

            int run = 1;

            while (i + run < code.Length && char.ToLowerInvariant(code[i + run]) == letter)
            {
                run++;
            }

            DatePart field = Field(code, letter, run);
            Flush(parts, literal);
            parts.Add(field);
            i += run;
        }

        Flush(parts, literal);

        if (parts.TrueForAll(p => p.Kind == DatePartKind.Literal))
        {
            throw Refuse(code, "no date or time part");
        }

        ResolveMinutes(parts);
        return new DateFormat(Canonical(parts), [.. parts]);
    }

    /// <inheritdoc/>
    public bool Equals(DateFormat? other) => other is not null && string.Equals(Code, other.Code, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Code);

    /// <inheritdoc/>
    public override string ToString() => Code;

    private static DatePart Field(string code, char letter, int run)
    {
        string token = new(letter, run);

        return (letter, run) switch
        {
            ('y', 2) => new DatePart(DatePartKind.Year, false, token),
            ('y', 4) => new DatePart(DatePartKind.Year, true, token),
            ('m', <= 2) => new DatePart(DatePartKind.Month, run == 2, token),
            ('d', <= 2) => new DatePart(DatePartKind.Day, run == 2, token),
            ('h', <= 2) => new DatePart(DatePartKind.Hour, run == 2, token),
            ('s', <= 2) => new DatePart(DatePartKind.Second, run == 2, token),
            _ => throw Refuse(code, $"\"{token}\" (month and weekday names, and years other than yy or yyyy, are not supported)"),
        };
    }

    private static void Flush(List<DatePart> parts, StringBuilder literal)
    {
        if (literal.Length > 0)
        {
            parts.Add(new DatePart(DatePartKind.Literal, false, literal.ToString()));
            literal.Clear();
        }
    }

    /// <summary>Excel's rule: m right after an hour, or right before a second, is minutes.</summary>
    private static void ResolveMinutes(List<DatePart> parts)
    {
        for (int i = 0; i < parts.Count; i++)
        {
            if (parts[i].Kind != DatePartKind.Month)
            {
                continue;
            }

            DatePartKind before = Neighbour(parts, i, -1);
            DatePartKind after = Neighbour(parts, i, +1);

            if (before == DatePartKind.Hour || after == DatePartKind.Second)
            {
                parts[i] = parts[i] with { Kind = DatePartKind.Minute };
            }
        }
    }

    private static DatePartKind Neighbour(List<DatePart> parts, int i, int step)
    {
        for (int j = i + step; j >= 0 && j < parts.Count; j += step)
        {
            if (parts[j].Kind != DatePartKind.Literal)
            {
                return parts[j].Kind;
            }
        }

        return DatePartKind.Literal;
    }

    /// <summary>Fields in lower case; literal characters escaped with a backslash, except ':', which Excel's own date-time formats leave bare.</summary>
    private static string Canonical(List<DatePart> parts)
    {
        StringBuilder code = new();

        foreach (DatePart part in parts)
        {
            if (part.Kind != DatePartKind.Literal)
            {
                code.Append(part.Text);
                continue;
            }

            foreach (char c in part.Text)
            {
                if (c != ':')
                {
                    code.Append('\\');
                }

                code.Append(c);
            }
        }

        return code.ToString();
    }

    private static ArgumentException Refuse(string code, string what) =>
        new($"The date format \"{code}\" is not supported: {what}.", nameof(code));
}
```

The refusal tests match these messages: `Field` names the token (`mmm`, `dddd`, `yyy`), `FormatCodes.At` the character (`'['`, `'A'`, `'0'`), and the backslash and unclosed-quote messages contain `backslash` and `quote`.

- [ ] **Step 5: Add `Number` and `Date` to `CellStyle`**

Insert after `Font` in `CellStyle.cs`:

```csharp
    /// <summary>How an integer, decimal or double cell shows its value; ignored for other cells. Null keeps the default.</summary>
    public NumberFormat? Number { get; init; }

    /// <summary>How a date or date-time cell shows its value; ignored for other cells. Null keeps the writer's default date format.</summary>
    public DateFormat? Date { get; init; }
```

- [ ] **Step 6: Public API and run**

Add the RS0016 lines to `PublicAPI.Unshipped.txt` (record-generated members included: `Equals`, `GetHashCode`, `<Clone>$`, `EqualityContract`, operators, `PrintMembers`, `ToString` as the analyzer lists them).
Run: `dotnet test --project tests/TriasDev.Tabular.Tests -c Release -- --filter-class "*FormatCodeTests"` and `--filter-class "*CellStyleTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/TriasDev.Tabular/Writing src/TriasDev.Tabular/PublicAPI.Unshipped.txt tests/TriasDev.Tabular.Tests/Writing/FormatCodeTests.cs
git commit -m "feat(write): number and date format codes — a supported subset of Excel's, refused when outside it"
```

---

### Task 3: Writer plumbing — `Style`, styled `Write` overloads, style index through every format

**Files:**
- Create: `src/TriasDev.Tabular/Writing/StyleTable.cs`
- Modify: `src/TriasDev.Tabular/Writing/TabularWriter.cs`, `src/TriasDev.Tabular/Writing/ISheetWriter.cs`, `src/TriasDev.Tabular/Csv/CsvSheetWriter.cs`, `src/TriasDev.Tabular/Xlsx/XlsxSheetWriter.cs`, `src/TriasDev.Tabular/Ods/OdsSheetWriter.cs`, `src/TriasDev.Tabular/Writing/StyleId.cs` (remark cref), `PublicAPI.Unshipped.txt`
- Test: `tests/TriasDev.Tabular.Tests/Writing/StyledWriterTests.cs`

**Interfaces:**
- Consumes: `CellStyle`, `StyleId` (Task 1), `NumberFormat`, `DateFormat` (Task 2).
- Produces (Tasks 4–5 rely on these exact names):
  - `internal enum ValueKind { Text, Integer, Number, Date, DateTime, Boolean, Empty }` and `internal const int ValueKinds = 7` on `StyleTable`.
  - `internal sealed class StyleTable` — `public const int MaxStyles = 4096;` `public int Count` (registered styles + 1 for index 0), `public CellStyle this[int index]` (index ≥ 1), `public int Add(CellStyle style)` (returns the index; same value → same index; throws `TabularLimitException("MaxStyles", 4096, …)` on the 4097th distinct style; `ArgumentOutOfRangeException` for an undefined `Horizontal`).
  - `TabularWriter`: `public StyleId Style(CellStyle style)`; `Write(string? value, StyleId style)`, `Write(long value, StyleId style)`, `Write(decimal value, StyleId style)`, `Write(double value, StyleId style)`, `Write(DateTime value, StyleId style)`, `Write(DateOnly value, StyleId style)`, `Write(bool value, StyleId style)`, `WriteEmpty(StyleId style)`.
  - `ISheetWriter` (every implementation): `string? WriteText(string value, int column, int style)`, `string? WriteLong(long value, int style)`, `string? WriteDecimal(decimal value, int style)`, `string? WriteDouble(double value, int style)`, `string? WriteDate(DateTime value, bool hasTime, int style)`, `void WriteBoolean(bool value, int style)`, `void WriteEmpty(int style)`; style 0 = unstyled.
  - Format writers receive the table in their constructor: `new CsvSheetWriter(buffer, csv)` unchanged (csv ignores styles); `new XlsxSheetWriter(workbook, xlsx, styles)`, `new OdsSheetWriter(spreadsheet, ods, styles)`. In this task xlsx and ods accept the parameter and **ignore it** (they write every cell as unstyled); Tasks 4 and 5 use it.

- [ ] **Step 1: Write the failing tests**

```csharp
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>Registering styles and writing styled cells, independent of the format.</summary>
public sealed class StyledWriterTests
{
    private static readonly CellStyle Red = new() { Fill = CellColor.FromRgb(0xFF0000) };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheSameStyleGetsTheSameId()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);

        StyleId first = writer.Style(Red);
        StyleId again = writer.Style(new CellStyle { Fill = CellColor.FromRgb(0xFF0000) });
        StyleId other = writer.Style(Red with { Wrap = true });

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        Assert.NotEqual(default, first);
    }

    [Fact]
    public async Task TheStyleAfterTheLimitIsRefused()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);

        for (int i = 0; i < 4096; i++)
        {
            writer.Style(new CellStyle { Fill = CellColor.FromRgb(i) });
        }

        writer.Style(new CellStyle { Fill = CellColor.FromRgb(0) });       // already registered: no new style
        TabularLimitException refused = Assert.Throws<TabularLimitException>(() => writer.Style(new CellStyle { Fill = CellColor.FromRgb(4096) }));
        Assert.Contains("4096", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUndefinedAlignmentIsRefused()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);

        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Style(new CellStyle { Horizontal = (HorizontalAlignment)9 }));
    }

    [Fact]
    public async Task AStyleIdFromAnotherWriterIsRefused()
    {
        await using TabularWriter other = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);
        other.Style(Red);
        StyleId foreign = other.Style(Red with { Wrap = true });           // index 2 there

        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);
        writer.Style(Red);                                                  // only index 1 here
        writer.BeginSheet("data", [new("x")]);
        writer.BeginRow();

        Assert.Throws<ArgumentException>(() => writer.Write(1.5, foreign));
    }

    [Fact]
    public async Task CsvIgnoresStyles()
    {
        async Task<byte[]> Write(bool styled)
        {
            WriteTarget target = new();

            await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Csv))
            {
                StyleId style = styled ? writer.Style(Red with { Number = NumberFormat.Parse("0.00"), Date = DateFormat.Parse("dd/mm/yyyy") }) : default;
                writer.BeginSheet("data", [new("text"), new("long"), new("decimal"), new("double"), new("date"), new("day"), new("flag"), new("none")]);
                writer.BeginRow();
                writer.Write("a", style);
                writer.Write(12L, style);
                writer.Write(1.25m, style);
                writer.Write(2.5, style);
                writer.Write(new DateTime(2026, 10, 4, 9, 5, 0, DateTimeKind.Unspecified), style);
                writer.Write(new DateOnly(2026, 10, 4), style);
                writer.Write(true, style);
                writer.WriteEmpty(style);
                writer.EndRow();
                await writer.CompleteAsync(Token);
            }

            return target.ToArray();
        }

        Assert.Equal(await Write(styled: false), await Write(styled: true));
    }

    [Fact]
    public async Task ANullStyleIsRefused()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);

        Assert.Throws<ArgumentNullException>(() => writer.Style(null!));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet build -c Release`
Expected: compile errors — `TabularWriter.Style`, `Write(…, StyleId)` do not exist.

- [ ] **Step 3: Implement `StyleTable`**

```csharp
namespace TriasDev.Tabular;

/// <summary>What a cell holds, as far as its style is concerned: each kind may need its own format style.</summary>
internal enum ValueKind
{
    Text,
    Integer,
    Number,
    Date,
    DateTime,
    Boolean,
    Empty,
}

/// <summary>
/// The styles one writer has handed out, by index: 0 is the unstyled cell, 1 onwards the registered
/// styles. Each format resolves an index, per <see cref="ValueKind"/>, into its own style lazily.
/// </summary>
internal sealed class StyleTable
{
    /// <summary>The most distinct styles a file holds.</summary>
    public const int MaxStyles = 4096;

    /// <summary>The number of <see cref="ValueKind"/> values, for per-kind caches.</summary>
    public const int ValueKinds = 7;

    private readonly Dictionary<CellStyle, int> _indices = [];
    private readonly List<CellStyle> _styles = [new CellStyle()];

    /// <summary>The indices in use: the registered styles plus index 0.</summary>
    public int Count => _styles.Count;

    /// <summary>The style at an index (1 onwards; 0 is the empty style).</summary>
    public CellStyle this[int index] => _styles[index];

    /// <summary>Registers a style, or finds it registered already; returns its index.</summary>
    public int Add(CellStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);

        if (_indices.TryGetValue(style, out int index))
        {
            return index;
        }

        if (!Enum.IsDefined(style.Horizontal))
        {
            throw new ArgumentOutOfRangeException(nameof(style), style.Horizontal, "The horizontal alignment is not one HorizontalAlignment defines.");
        }

        if (_indices.Count == MaxStyles)
        {
            throw new TabularLimitException("MaxStyles", MaxStyles, $"A file holds at most {MaxStyles} distinct styles.");
        }

        index = _styles.Count;
        _styles.Add(style);
        _indices.Add(style, index);
        return index;
    }
}
```

- [ ] **Step 4: Wire `TabularWriter`**

1. Field: `private readonly StyleTable _styles = new();`. Pass it to the format writers in `Create` (constructors change: `XlsxSheetWriter(SpillBuffer output, XlsxWriterOptions options, StyleTable styles)`, `OdsSheetWriter(SpillBuffer output, OdsWriterOptions options, StyleTable styles)`). Because `Create` builds the sheet writer before the `TabularWriter` constructor runs, create the table in `Create` and pass it to the constructor too: add a `StyleTable styles` constructor parameter and assign `_styles = styles`.
2. Add:

```csharp
    /// <summary>
    /// Registers a style for this writer's cells and returns its id, to pass to the <c>Write</c>
    /// overloads. The same style, by value, returns the same id. Call it before or during any sheet;
    /// csv files ignore styles.
    /// </summary>
    /// <exception cref="TabularLimitException">The file already holds 4096 distinct styles.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The style's alignment is not a defined value.</exception>
    public StyleId Style(CellStyle style)
    {
        ExpectWritable();

        try
        {
            return new StyleId(_styles.Add(style));
        }
        catch (Exception refused) when (refused is ArgumentException or TabularLimitException)
        {
            MarkFaulted();
            throw;
        }
    }
```

3. Turn each existing `Write(x)` into a call of the new overload with `default`, and move the body into the new overload. Example for double (do the same for every overload; text checks `TextRules` exactly as now; `Write(bool, StyleId)` passes the index; `WriteEmpty(StyleId)`):

```csharp
    public void Write(double value) => Write(value, default);

    /// <summary>Writes the next cell as a number, in a style this writer handed out.</summary>
    /// <exception cref="ArgumentException"><paramref name="style"/> was not handed out by this writer.</exception>
    public void Write(double value, StyleId style)
    {
        int column = NextCell();
        Check(ValueChecks.Double(value) ?? _sheet.WriteDouble(value, Index(style)), column);
    }
```

Keep each existing overload's XML doc on the unstyled overload and give the styled one `<inheritdoc cref="Write(double)"/>` plus the `<param name="style">` and the `ArgumentException` line.

4. The index check — one compare per cell:

```csharp
    /// <summary>The style's index, refused unless this writer handed it out.</summary>
    private int Index(StyleId style)
    {
        int index = style.Value;

        if ((uint)index >= (uint)_styles.Count)
        {
            throw Faulting(new ArgumentException($"{style} was not handed out by this writer's Style method.", nameof(style)));
        }

        return index;
    }
```

5. `ISheetWriter`: add `int style` as the last parameter of `WriteText`, `WriteLong`, `WriteDecimal`, `WriteDouble`, `WriteDate`, `WriteBoolean`, `WriteEmpty`, with the doc line `<param name="style">The style index in the writer's <see cref="StyleTable"/>; 0 is unstyled.</param>`. `FinishRow` calls `_sheet.WriteEmpty(0)`; `BeginSheet`'s header loop is unchanged (`WriteHeader` takes no style in this part).
6. `CsvSheetWriter`: add the parameter to the seven methods and ignore it (name it `style` and add `_ = style;` only if an analyzer requires it — IDE0060 is suppressed for interface implementations).
7. `XlsxSheetWriter` and `OdsSheetWriter`: add the constructor parameter and the method parameters; ignore them for now (store the table in a field `_styles` — Task 4/5 use it).
8. `StyleId.cs`: restore the `<see cref="TabularWriter.Style"/>` in the summary.

- [ ] **Step 5: Public API, run**

Add the RS0016 lines. Run: `dotnet test --project tests/TriasDev.Tabular.Tests -c Release -- --filter-class "*StyledWriterTests"` → PASS; then `dotnet test -c Release` → all PASS (every existing writer test runs through the changed signatures).

- [ ] **Step 6: Commit**

```bash
git add src tests/TriasDev.Tabular.Tests/Writing/StyledWriterTests.cs
git commit -m "feat(write): register styles and write styled cells; the style index reaches every format, csv ignores it"
```

---

### Task 4: xlsx styles

**Files:**
- Create: `src/TriasDev.Tabular/Xlsx/XlsxStyles.cs`
- Modify: `src/TriasDev.Tabular/Xlsx/XlsxSheetWriter.cs`, `src/TriasDev.Tabular/Xlsx/XlsxParts.cs` (remove the fixed `Styles` constant; keep its doc comment's content on `XlsxStyles`), `tests/TriasDev.Tabular.Tests/Fixtures/LibreOffice.cs`
- Test: `tests/TriasDev.Tabular.Tests/Writing/XlsxStyleTests.cs`, `tests/TriasDev.Tabular.Tests/Writing/StyledInteropTests.cs`

**Interfaces:**
- Consumes: `StyleTable`, `ValueKind`, `StyleTable.ValueKinds` (Task 3); `NumberFormat.Code`, `DateFormat.Code` (Task 2); `CellStyle`, `CellColor.Rgb`, `CellFont`, `CellBorder`, `HorizontalAlignment` (Task 1).
- Produces: `internal sealed class XlsxStyles(StyleTable table)` with `public int Xf(int style, ValueKind kind)` (style ≥ 1; returns the `cellXfs` index) and `public byte[] Build()` (the whole `xl/styles.xml`). `LibreOffice.Convert(byte[] file, string extension, string filter, string outputExtension) → byte[]` (Task 5 uses it too).

- [ ] **Step 1: Generalise the LibreOffice fixture**

In `Fixtures/LibreOffice.cs` split `ConvertToCsv` into a general `Convert` and a csv wrapper:

```csharp
    /// <summary>Converts the file's first sheet to csv with LibreOffice and returns its lines, values as shown.</summary>
    public static string[] ConvertToCsv(byte[] file, string extension) =>
        Encoding.UTF8.GetString(Convert(file, extension, "csv:Text - txt - csv (StarCalc):44,34,76,1,,0,true", "csv"))
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where((line, i) => line.Length > 0 || i == 0)
            .ToArray();

    /// <summary>Converts a file with LibreOffice, by an export filter, and returns the converted file.</summary>
    public static byte[] Convert(byte[] file, string extension, string filter, string outputExtension)
```

`Convert` holds today's body with `"--convert-to", filter`, the expected output `Path.Combine(folder, "file." + outputExtension)`, and returns `File.ReadAllBytes(output)`. Keep `ConvertToCsv`'s old return exactly: it used `File.ReadAllLines`, which drops the trailing empty line — the `Where` above does the same for a trailing newline; check with the existing `LibreOfficeInteropTests` (Step 6) that they pass unchanged.

- [ ] **Step 2: Write the failing structural tests**

`XlsxStyleTests.cs`:

```csharp
using System.IO.Compression;
using System.Text;

using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>Styles in the workbook: valid to the schema, deduplicated, and invisible to the import.</summary>
public sealed class XlsxStyleTests
{
    private static readonly CellStyle Legend = new()
    {
        Fill = CellColor.FromRgb(0xF8696B),
        Font = new CellFont { Color = CellColor.FromRgb(0xFFFFFF), Bold = true, Italic = true },
        Number = NumberFormat.Parse("#,##0.00"),
        Date = DateFormat.Parse("dd/mm/yyyy"),
        Horizontal = HorizontalAlignment.Center,
        Wrap = true,
        Border = CellBorder.Thin(CellColor.FromRgb(0xBFBFBF)),
    };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<byte[]> Workbook(Action<TabularWriter> write)
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Xlsx))
        {
            write(writer);
            await writer.CompleteAsync(Token);
        }

        return target.ToArray();
    }

    private static string Part(byte[] xlsx, string name)
    {
        using ZipArchive archive = new(new MemoryStream(xlsx, writable: false), ZipArchiveMode.Read);
        using StreamReader reader = new(archive.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static List<RawCell[]> Rows(byte[] xlsx)
    {
        using XlsxCursor cursor = new(new MemoryStream(xlsx, writable: false), cancellationToken: Token);
        Assert.True(cursor.MoveToSheet(0, Token));
        List<RawCell[]> rows = [];

        while (cursor.ReadRow(Token))
        {
            rows.Add(cursor.CurrentRow.ToArray());
        }

        return rows;
    }

    /// <summary>One row of every kind, all in one style (or unstyled).</summary>
    private static void EveryKind(TabularWriter writer, StyleId style)
    {
        writer.BeginSheet("data", [new("text"), new("long"), new("decimal"), new("double"), new("date"), new("stamp"), new("flag"), new("none")]);
        writer.BeginRow();
        writer.Write("R&D", style);
        writer.Write(1234567L, style);
        writer.Write(1234.5m, style);
        writer.Write(0.125, style);
        writer.Write(new DateOnly(2026, 10, 4), style);
        writer.Write(new DateTime(2026, 10, 4, 9, 5, 0, DateTimeKind.Unspecified), style);
        writer.Write(true, style);
        writer.WriteEmpty(style);
        writer.EndRow();
    }

    [Fact]
    public async Task AStyledWorkbookIsValidAndStatesTheStyle()
    {
        byte[] xlsx = await Workbook(writer => EveryKind(writer, writer.Style(Legend)));

        Assert.Empty(OoxmlValidation.Errors(xlsx));

        string styles = Part(xlsx, "xl/styles.xml");
        Assert.Contains("<numFmt numFmtId=\"165\" formatCode=\"#,##0.00\"/>", styles, StringComparison.Ordinal);
        Assert.Contains("<numFmt numFmtId=\"166\" formatCode=\"dd\\/mm\\/yyyy\"/>", styles, StringComparison.Ordinal);
        Assert.Contains("<fgColor rgb=\"FFF8696B\"/>", styles, StringComparison.Ordinal);
        Assert.Contains("<b/><i/>", styles, StringComparison.Ordinal);
        Assert.Contains("<color rgb=\"FFFFFFFF\"/>", styles, StringComparison.Ordinal);
        Assert.Contains("<left style=\"thin\"><color rgb=\"FFBFBFBF\"/></left>", styles, StringComparison.Ordinal);
        Assert.Contains("<alignment horizontal=\"center\" wrapText=\"1\"/>", styles, StringComparison.Ordinal);

        string sheet = Part(xlsx, "xl/worksheets/sheet1.xml");
        Assert.Contains("<c r=\"H2\" s=\"", sheet, StringComparison.Ordinal);      // the styled empty cell is written
    }

    [Fact]
    public async Task TheImportReadsAStyledRowAsItReadsAnUnstyledOne()
    {
        byte[] plain = await Workbook(writer => EveryKind(writer, default));
        byte[] styled = await Workbook(writer => EveryKind(writer, writer.Style(Legend)));

        Assert.Equal(Rows(plain).Select(r => r.ToArray()), Rows(styled).Select(r => r.ToArray()));
        Assert.Equal(new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Unspecified), Rows(styled)[1][4].DateTime);
        Assert.Equal(RawCell.FromBoolean(true), Rows(styled)[1][6]);
        Assert.True(Rows(styled)[1][7].IsEmpty);
    }

    [Fact]
    public async Task OneStyleOnSeveralKindsTakesAFormatPerKind()
    {
        byte[] xlsx = await Workbook(writer => EveryKind(writer, writer.Style(Legend)));
        string styles = Part(xlsx, "xl/styles.xml");

        // text/flag/none share one xf (General), long/decimal/double share the number format, date and stamp the date format.
        Assert.Contains("<cellXfs count=\"7\">", styles, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EqualStylesAreWrittenOnce()
    {
        byte[] xlsx = await Workbook(writer =>
        {
            StyleId a = writer.Style(new CellStyle { Fill = CellColor.FromRgb(0x00FF00) });
            StyleId b = writer.Style(new CellStyle { Fill = CellColor.FromRgb(0x00FF00) });
            StyleId c = writer.Style(new CellStyle { Fill = CellColor.FromRgb(0x00FF00), Wrap = true });
            writer.BeginSheet("data", [new("a"), new("b"), new("c")]);
            writer.BeginRow();
            writer.Write("x", a);
            writer.Write("y", b);
            writer.Write("z", c);
            writer.EndRow();
        });

        string styles = Part(xlsx, "xl/styles.xml");
        Assert.Contains("<fills count=\"3\">", styles, StringComparison.Ordinal);          // none, gray125, one green
        Assert.Contains("<cellXfs count=\"6\">", styles, StringComparison.Ordinal);        // four fixed + two
    }

    [Fact]
    public async Task AStyleRegisteredInTheMiddleOfTheDataIsWritten()
    {
        byte[] xlsx = await Workbook(writer =>
        {
            writer.BeginSheet("data", [new("n")]);

            for (int i = 1; i <= 1000; i++)
            {
                writer.BeginRow();
                writer.Write(i, i == 1000 ? writer.Style(new CellStyle { Fill = CellColor.FromRgb(0x0000FF) }) : default);
                writer.EndRow();
            }
        });

        Assert.Empty(OoxmlValidation.Errors(xlsx));
        Assert.Contains("<fgColor rgb=\"FF0000FF\"/>", Part(xlsx, "xl/styles.xml"), StringComparison.Ordinal);
        Assert.Contains("<c r=\"A1001\" s=\"4\"><v>1000</v></c>", Part(xlsx, "xl/worksheets/sheet1.xml"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiteralTextInAFormatIsEscaped()
    {
        byte[] xlsx = await Workbook(writer =>
        {
            StyleId style = writer.Style(new CellStyle { Number = NumberFormat.Parse("\"R&D <\"0") });
            writer.BeginSheet("data", [new("n")]);
            writer.BeginRow();
            writer.Write(5L, style);
            writer.EndRow();
        });

        Assert.Empty(OoxmlValidation.Errors(xlsx));
        Assert.Contains("formatCode=\"&quot;R&amp;D &lt;&quot;0\"", Part(xlsx, "xl/styles.xml"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnstyledWorkbookKeepsItsFourFormats()
    {
        byte[] xlsx = await Workbook(writer => EveryKind(writer, default));

        Assert.Contains("<cellXfs count=\"4\">", Part(xlsx, "xl/styles.xml"), StringComparison.Ordinal);
        Assert.Empty(OoxmlValidation.Errors(xlsx));
    }
}
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests -c Release -- --filter-class "*XlsxStyleTests"`
Expected: FAIL — styles.xml still the fixed four formats, no `s` on styled cells.

- [ ] **Step 4: Implement `XlsxStyles`**

```csharp
using System.Globalization;
using System.Text;

namespace TriasDev.Tabular.Xlsx;

/// <summary>
/// The workbook's styles: four fixed cell formats every unstyled cell uses, then one per registered
/// style and value kind actually written, resolved on first use and cached.
/// </summary>
/// <remarks>
/// The fixed formats, by index: 0 General, 1 a date (built-in 14), 2 a date and time (custom 164),
/// 3 an integer (built-in 1, so an id of twelve digits does not show as <c>1.23457E+11</c>). The
/// reader takes formats 14 and 164 — and any custom code with a date token — for dates. Custom
/// number formats start at 165.
/// </remarks>
internal sealed class XlsxStyles
{
    public const int DateFormat = 14;
    public const int DateTimeFormat = 164;
    public const int IntegerFormat = 1;
    private const int FirstCustomFormat = 165;
    private const string XmlDeclaration = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";

    private readonly StyleTable _table;
    private readonly List<Xf> _xfs = [new(0, 0, 0, 0, HorizontalAlignment.General, false), new(DateFormat, 0, 0, 0, HorizontalAlignment.General, false), new(DateTimeFormat, 0, 0, 0, HorizontalAlignment.General, false), new(IntegerFormat, 0, 0, 0, HorizontalAlignment.General, false)];
    private readonly Dictionary<Xf, int> _xfIndices = [];
    private readonly List<CellFont> _fonts = [];          // index + 1 in <fonts>; 0 is the default font
    private readonly Dictionary<CellFont, int> _fontIndices = [];
    private readonly List<CellColor> _fills = [];         // index + 2 in <fills>; 0 none, 1 gray125
    private readonly Dictionary<CellColor, int> _fillIndices = [];
    private readonly List<CellColor> _borders = [];       // index + 1 in <borders>; 0 none
    private readonly Dictionary<CellColor, int> _borderIndices = [];
    private readonly List<string> _formats = [];          // id FirstCustomFormat + index
    private readonly Dictionary<string, int> _formatIds = new(StringComparer.Ordinal);
    private int[] _resolved = [];                         // xf index + 1 per (style, kind); 0 = not yet

    public XlsxStyles(StyleTable table)
    {
        _table = table;

        for (int i = 0; i < _xfs.Count; i++)
        {
            _xfIndices.Add(_xfs[i], i);
        }
    }

    /// <summary>The cell format for a registered style (index 1 onwards) on a kind of value.</summary>
    public int Xf(int style, ValueKind kind)
    {
        int slot = (style * StyleTable.ValueKinds) + (int)kind;
        int[] resolved = _resolved;
        return slot < resolved.Length && resolved[slot] != 0 ? resolved[slot] - 1 : Resolve(slot, style, kind);
    }

    /// <summary>The whole <c>xl/styles.xml</c>.</summary>
    public byte[] Build()
    {
        StringBuilder xml = new(XmlDeclaration + "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");

        xml.Append(CultureInfo.InvariantCulture, $"<numFmts count=\"{1 + _formats.Count}\"><numFmt numFmtId=\"{DateTimeFormat}\" formatCode=\"yyyy\\-mm\\-dd\\ hh:mm:ss\"/>");

        for (int i = 0; i < _formats.Count; i++)
        {
            xml.Append(CultureInfo.InvariantCulture, $"<numFmt numFmtId=\"{FirstCustomFormat + i}\" formatCode=\"");
            AppendEscaped(xml, _formats[i]);
            xml.Append("\"/>");
        }

        xml.Append(CultureInfo.InvariantCulture, $"</numFmts><fonts count=\"{1 + _fonts.Count}\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font>");

        foreach (CellFont font in _fonts)
        {
            xml.Append("<font>");
            xml.Append(font.Bold ? "<b/>" : string.Empty);
            xml.Append(font.Italic ? "<i/>" : string.Empty);
            xml.Append("<sz val=\"11\"/>");

            if (font.Color is { } color)
            {
                xml.Append("<color rgb=\"").Append(Argb(color)).Append("\"/>");
            }

            xml.Append("<name val=\"Calibri\"/></font>");
        }

        xml.Append(CultureInfo.InvariantCulture, $"</fonts><fills count=\"{2 + _fills.Count}\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>");

        foreach (CellColor fill in _fills)
        {
            xml.Append("<fill><patternFill patternType=\"solid\"><fgColor rgb=\"").Append(Argb(fill)).Append("\"/><bgColor indexed=\"64\"/></patternFill></fill>");
        }

        xml.Append(CultureInfo.InvariantCulture, $"</fills><borders count=\"{1 + _borders.Count}\"><border><left/><right/><top/><bottom/><diagonal/></border>");

        foreach (CellColor border in _borders)
        {
            string side = "style=\"thin\"><color rgb=\"" + Argb(border) + "\"/>";
            xml.Append("<border><left ").Append(side).Append("</left><right ").Append(side).Append("</right><top ").Append(side).Append("</top><bottom ").Append(side).Append("</bottom><diagonal/></border>");
        }

        xml.Append("</borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>");
        xml.Append(CultureInfo.InvariantCulture, $"<cellXfs count=\"{_xfs.Count}\">");

        foreach (Xf xf in _xfs)
        {
            AppendXf(xml, xf);
        }

        xml.Append("</cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>");
        return Encoding.UTF8.GetBytes(xml.ToString());
    }

    private int Resolve(int slot, int style, ValueKind kind)
    {
        CellStyle cell = _table[style];

        int format = kind switch
        {
            ValueKind.Integer => cell.Number is { } number ? FormatId(number.Code) : IntegerFormat,
            ValueKind.Number => cell.Number is { } number ? FormatId(number.Code) : 0,
            ValueKind.Date => cell.Date is { } date ? FormatId(date.Code) : DateFormat,
            ValueKind.DateTime => cell.Date is { } date ? FormatId(date.Code) : DateTimeFormat,
            _ => 0,
        };

        int font = cell.Font is { } f ? Index(_fonts, _fontIndices, f) + 1 : 0;
        int fill = cell.Fill is { } c ? Index(_fills, _fillIndices, c) + 2 : 0;
        int border = cell.Border is { } b ? Index(_borders, _borderIndices, b.Color) + 1 : 0;
        int xf = Index(_xfs, _xfIndices, new Xf(format, font, fill, border, cell.Horizontal, cell.Wrap));

        if (slot >= _resolved.Length)
        {
            Array.Resize(ref _resolved, Math.Max(slot + 1, _table.Count * StyleTable.ValueKinds));
        }

        _resolved[slot] = xf + 1;
        return xf;
    }

    private int FormatId(string code)
    {
        if (!_formatIds.TryGetValue(code, out int id))
        {
            id = FirstCustomFormat + _formats.Count;
            _formats.Add(code);
            _formatIds.Add(code, id);
        }

        return id;
    }

    private static int Index<T>(List<T> list, Dictionary<T, int> indices, T value)
        where T : notnull
    {
        if (!indices.TryGetValue(value, out int index))
        {
            index = list.Count;
            list.Add(value);
            indices.Add(value, index);
        }

        return index;
    }

    private static void AppendXf(StringBuilder xml, Xf xf)
    {
        xml.Append(CultureInfo.InvariantCulture, $"<xf numFmtId=\"{xf.Format}\" fontId=\"{xf.Font}\" fillId=\"{xf.Fill}\" borderId=\"{xf.Border}\" xfId=\"0\"");
        xml.Append(xf.Format != 0 ? " applyNumberFormat=\"1\"" : string.Empty);
        xml.Append(xf.Font != 0 ? " applyFont=\"1\"" : string.Empty);
        xml.Append(xf.Fill != 0 ? " applyFill=\"1\"" : string.Empty);
        xml.Append(xf.Border != 0 ? " applyBorder=\"1\"" : string.Empty);

        if (xf.Horizontal == HorizontalAlignment.General && !xf.Wrap)
        {
            xml.Append("/>");
            return;
        }

        xml.Append(" applyAlignment=\"1\"><alignment");

        if (xf.Horizontal != HorizontalAlignment.General)
        {
            xml.Append(" horizontal=\"").Append(xf.Horizontal switch { HorizontalAlignment.Left => "left", HorizontalAlignment.Center => "center", _ => "right" }).Append('"');
        }

        xml.Append(xf.Wrap ? " wrapText=\"1\"" : string.Empty).Append("/></xf>");
    }

    private static string Argb(CellColor color) => string.Create(CultureInfo.InvariantCulture, $"FF{color.Rgb:X6}");

    private static void AppendEscaped(StringBuilder xml, string value)
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

    private readonly record struct Xf(int Format, int Font, int Fill, int Border, HorizontalAlignment Horizontal, bool Wrap);
}
```

Check the counts the tests expect against this code before running: `EveryKind` in one style yields three new xfs (General-with-style for text/flag/none, number format 165 for long/decimal/double, date format 166 for date and stamp) → `cellXfs count="7"`. `EqualStylesAreWrittenOnce`: fills `none, gray125, green` → 3; xfs 4 + (green) + (green, wrap) → 6. `AStyleRegisteredInTheMiddle…`: the first styled xf is index 4 → `s="4"`. The `AStyledWorkbook…` test's font assertion `<b/><i/>` matches the order above.

- [ ] **Step 5: Use it in `XlsxSheetWriter`**

1. Field `private readonly XlsxStyles _styles;` set from the constructor's `StyleTable` (`_styles = new XlsxStyles(styles)`).
2. `StartCell()` becomes `StartCell(int xf)`: after the reference attribute, when `xf != 0`, append ` s="` + `AppendFormatted(xf, default, CultureInfo.InvariantCulture)` + `"`. `WriteNumber(int style)` becomes `WriteNumber(int xf)` → `StartCell(xf); _row.Append("><v>");` (the single-digit `(char)('0' + style)` goes away).
3. Pick the xf per method, unstyled first so index 0 costs one compare:

```csharp
    public string? WriteLong(long value, int style)
    {
        if (ValueChecks.LongInDouble(value) is { } code)
        {
            return code;
        }

        WriteNumber(style == 0 ? IntegerStyle : _styles.Xf(style, ValueKind.Integer));
        _row.AppendFormatted(value, default, CultureInfo.InvariantCulture);
        _row.Append(ValueEnd);
        return null;
    }
```

   Decimal and double: `style == 0 ? 0 : _styles.Xf(style, ValueKind.Number)`. Date: `style == 0 ? (hasTime ? DateTimeStyle : DateStyle) : _styles.Xf(style, hasTime ? ValueKind.DateTime : ValueKind.Date)`. Text (`WriteText`) and header: `WriteInline(value, xf)` with `xf = style == 0 ? 0 : _styles.Xf(style, ValueKind.Text)` (header passes 0). Boolean: `StartCell(style == 0 ? 0 : _styles.Xf(style, ValueKind.Boolean))`.
4. `WriteEmpty(int style)`: `if (style == 0) { _column++; return; } StartCell(_styles.Xf(style, ValueKind.Empty)); _row.Append("/>");`
5. `Complete()`: `_zip.AddStored("xl/styles.xml", _styles.Build());`. Delete `XlsxParts.Styles` (its content now comes from `Build`).

- [ ] **Step 6: Run the structural tests and the existing suite**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests -c Release -- --filter-class "*XlsxStyleTests"` → PASS. Then `--filter-class "*Xlsx*"` and `--filter-class "*LibreOfficeInteropTests"` → PASS unchanged.

- [ ] **Step 7: Write the LibreOffice interop tests (xlsx half)**

`StyledInteropTests.cs`:

```csharp
using System.IO.Compression;
using System.Text;

using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>LibreOffice shows our formats and keeps our fills and fonts.</summary>
public sealed class StyledInteropTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly CellStyle Red = new()
    {
        Fill = CellColor.FromRgb(0xF8696B),
        Font = new CellFont { Bold = true },
    };

    private static async Task<byte[]> Write(TabularFormat format)
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, format))
        {
            StyleId twoPlaces = writer.Style(new CellStyle { Number = NumberFormat.Parse("0.00") });
            StyleId percent = writer.Style(new CellStyle { Number = NumberFormat.Parse("0.0%") });
            StyleId prefixed = writer.Style(new CellStyle { Number = NumberFormat.Parse("\"EUR \"0") });
            StyleId day = writer.Style(new CellStyle { Date = DateFormat.Parse("dd/mm/yyyy") });
            StyleId stamp = writer.Style(new CellStyle { Date = DateFormat.Parse("dd.mm.yyyy hh:mm") });
            StyleId red = writer.Style(Red);

            writer.BeginSheet("data", [new("a"), new("b"), new("c"), new("d"), new("e"), new("f")]);
            writer.BeginRow();
            writer.Write(12.5, twoPlaces);
            writer.Write(0.125, percent);
            writer.Write(42L, prefixed);
            writer.Write(new DateOnly(2026, 10, 4), day);
            writer.Write(new DateTime(2026, 10, 4, 9, 5, 0, DateTimeKind.Unspecified), stamp);
            writer.Write("hot", red);
            writer.EndRow();
            await writer.CompleteAsync(Token);
        }

        return target.ToArray();
    }

    private static string Entry(byte[] zip, string name)
    {
        using ZipArchive archive = new(new MemoryStream(zip, writable: false), ZipArchiveMode.Read);
        using StreamReader reader = new(archive.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Theory]
    [InlineData(TabularFormat.Xlsx, "xlsx")]
    public async Task ShowsTheFormats(TabularFormat format, string extension)
    {
        string[] lines = LibreOffice.ConvertToCsv(await Write(format), extension);

        // The decimal separator is the LibreOffice profile's locale; the rest is what the codes state.
        Assert.Matches("^12[.,]50,12[.,]5%,EUR 42,04/10/2026,04\\.10\\.2026 09:05,\"hot\"$", lines[1]);
    }

    [Fact]
    public async Task KeepsTheFillAndFontOfAnXlsx()
    {
        byte[] ods = LibreOffice.Convert(await Write(TabularFormat.Xlsx), "xlsx", "ods", "ods");
        string styles = Entry(ods, "content.xml") + Entry(ods, "styles.xml");

        Assert.Contains("fo:background-color=\"#f8696b\"", styles, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fo:font-weight=\"bold\"", styles, StringComparison.Ordinal);
    }
}
```

If `ShowsTheFormats` fails only because LibreOffice quotes a field (for example the percent with a locale comma), change the regex to accept an optional quote around that field — the assertion is about the shown text, not csv quoting.

- [ ] **Step 8: Run interop**

Run: `TABULAR_REQUIRE_SOFFICE=1 dotnet test --project tests/TriasDev.Tabular.Tests -c Release -- --filter-class "*StyledInteropTests"` (LibreOffice is installed on the development machine and in CI's Linux job).
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add src tests
git commit -m "feat(write): xlsx styles — fills, fonts, borders, alignment, number and date formats, resolved per kind on first use"
```

---

### Task 5: ods styles

**Files:**
- Create: `src/TriasDev.Tabular/Ods/OdsStyles.cs`
- Modify: `src/TriasDev.Tabular/Ods/OdsSheetWriter.cs`, `src/TriasDev.Tabular/Ods/OdsParts.cs` (make `Namespaces` and the `XmlDeclaration` internal; expose the two default date data-style bodies as constants; remove the fixed `Styles` bytes)
- Test: `tests/TriasDev.Tabular.Tests/Writing/OdsStyleTests.cs`, `tests/TriasDev.Tabular.Tests/Writing/StyledInteropTests.cs`

**Interfaces:**
- Consumes: as Task 4, plus `NumberFormat` internals (`Grouping`, `MinIntegerDigits`, `DecimalPlaces`, `MinDecimalPlaces`, `Percent`, `Prefix`, `Suffix`) and `DateFormat.Parts` / `DatePart` / `DatePartKind` (Task 2); `LibreOffice.Convert` (Task 4).
- Produces: `internal sealed class OdsStyles(StyleTable table)` with `public OdsCellStyle Cell(int style, ValueKind kind)` and `public byte[] Build()`; `internal readonly record struct OdsCellStyle(string Name, bool Percent)`.

- [ ] **Step 1: Probe that LibreOffice applies a common cell style referenced from content**

This is the design's one assumption about ods (spec §1). Write the probe as the first test in `OdsStyleTests.cs`, by hand-building nothing: write it against the finished writer, so it fails now and passes after Step 4:

```csharp
    [Fact]
    public async Task LibreOfficeAppliesTheCommonStyle()
    {
        byte[] ods = await Spreadsheet(writer =>
        {
            StyleId red = writer.Style(new CellStyle { Fill = CellColor.FromRgb(0xF8696B), Font = new CellFont { Bold = true } });
            writer.BeginSheet("data", [new("a")]);
            writer.BeginRow();
            writer.Write("hot", red);
            writer.EndRow();
        });

        byte[] xlsx = LibreOffice.Convert(ods, "ods", "xlsx:Calc MS Excel 2007 XML", "xlsx");
        string styles = Entry(xlsx, "xl/styles.xml");

        Assert.Contains("rgb=\"FFF8696B\"", styles, StringComparison.OrdinalIgnoreCase);
        Assert.Matches("<b( val=\"(true|1)\")?/>", styles);
    }
```

If, after Step 4, this test fails because LibreOffice ignores a common style referenced directly by `table:style-name` (and only then), stop and report BLOCKED with the converted file's `content.xml` — the controller rules on the fallback (declaring styles before the body); do not invent one.

- [ ] **Step 2: Write the failing structural tests**

Rest of `OdsStyleTests.cs` — the helpers mirror `OdsWriterTests` (`Spreadsheet(Action<TabularWriter>)`, `Rows(byte[] ods)` with `OdsCursor`, `Entry(byte[] zip, string name)`); copy them from `tests/TriasDev.Tabular.Tests/Writing/OdsWriterTests.cs` lines 16–50 and add `Entry` as in `StyledInteropTests`.

```csharp
    private static readonly CellStyle Legend = new()
    {
        Fill = CellColor.FromRgb(0xF8696B),
        Font = new CellFont { Color = CellColor.FromRgb(0xFFFFFF), Bold = true, Italic = true },
        Number = NumberFormat.Parse("#,##0.00"),
        Date = DateFormat.Parse("dd/mm/yyyy"),
        Horizontal = HorizontalAlignment.Center,
        Wrap = true,
        Border = CellBorder.Thin(CellColor.FromRgb(0xBFBFBF)),
    };

    private static void EveryKind(TabularWriter writer, StyleId style)
    {
        writer.BeginSheet("data", [new("text"), new("long"), new("decimal"), new("double"), new("date"), new("stamp"), new("flag"), new("none")]);
        writer.BeginRow();
        writer.Write("R&D", style);
        writer.Write(1234567L, style);
        writer.Write(1234.5m, style);
        writer.Write(0.125, style);
        writer.Write(new DateOnly(2026, 10, 4), style);
        writer.Write(new DateTime(2026, 10, 4, 9, 5, 0, DateTimeKind.Unspecified), style);
        writer.Write(true, style);
        writer.WriteEmpty(style);
        writer.EndRow();
    }

    [Fact]
    public async Task StatesTheStyleAsACommonStyle()
    {
        byte[] ods = await Spreadsheet(writer => EveryKind(writer, writer.Style(Legend)));
        string styles = Entry(ods, "styles.xml");
        string content = Entry(ods, "content.xml");

        Assert.Contains("<number:number-style style:name=\"tn1\"><number:number number:decimal-places=\"2\" number:min-decimal-places=\"2\" number:min-integer-digits=\"1\" number:grouping=\"true\"/></number:number-style>", styles, StringComparison.Ordinal);
        Assert.Contains("<number:date-style style:name=\"tn2\"><number:day number:style=\"long\"/><number:text>/</number:text><number:month number:style=\"long\"/><number:text>/</number:text><number:year number:style=\"long\"/></number:date-style>", styles, StringComparison.Ordinal);
        Assert.Contains("fo:background-color=\"#F8696B\"", styles, StringComparison.Ordinal);
        Assert.Contains("fo:border=\"0.06pt solid #BFBFBF\"", styles, StringComparison.Ordinal);
        Assert.Contains("fo:wrap-option=\"wrap\"", styles, StringComparison.Ordinal);
        Assert.Contains("<style:paragraph-properties fo:text-align=\"center\"/>", styles, StringComparison.Ordinal);
        Assert.Contains("fo:color=\"#FFFFFF\" fo:font-weight=\"bold\"", styles, StringComparison.Ordinal);
        Assert.Contains("fo:font-style=\"italic\"", styles, StringComparison.Ordinal);
        Assert.Contains("table:style-name=\"ts", content, StringComparison.Ordinal);
        Assert.Contains("<table:table-cell table:style-name=\"ts1\"/>", content, StringComparison.Ordinal);   // the styled empty cell
    }

    [Fact]
    public async Task TheImportReadsAStyledRowAsItReadsAnUnstyledOne()
    {
        byte[] plain = await Spreadsheet(writer => EveryKind(writer, default));
        byte[] styled = await Spreadsheet(writer => EveryKind(writer, writer.Style(Legend)));

        Assert.Equal(Rows(plain).Select(r => r.ToArray()), Rows(styled).Select(r => r.ToArray()));
        Assert.Equal(RawCell.FromBoolean(true), Rows(styled)[1][6]);
    }

    [Fact]
    public async Task APercentFormatMakesAPercentageCell()
    {
        byte[] ods = await Spreadsheet(writer =>
        {
            StyleId percent = writer.Style(new CellStyle { Number = NumberFormat.Parse("0.0%") });
            writer.BeginSheet("data", [new("p")]);
            writer.BeginRow();
            writer.Write(0.125, percent);
            writer.EndRow();
        });

        Assert.Contains("office:value-type=\"percentage\" office:value=\"0.125\"", Entry(ods, "content.xml"), StringComparison.Ordinal);
        Assert.Equal(0.125, Rows(ods)[1][0].Number);
    }

    [Fact]
    public async Task AStyleRegisteredInTheMiddleOfTheDataIsWritten()
    {
        byte[] ods = await Spreadsheet(writer =>
        {
            writer.BeginSheet("data", [new("n")]);

            for (int i = 1; i <= 1000; i++)
            {
                writer.BeginRow();
                writer.Write(i, i == 1000 ? writer.Style(new CellStyle { Fill = CellColor.FromRgb(0x0000FF) }) : default);
                writer.EndRow();
            }
        });

        Assert.Contains("fo:background-color=\"#0000FF\"", Entry(ods, "styles.xml"), StringComparison.Ordinal);
        Assert.Equal(1000, Rows(ods)[1000][0].Number);
    }

    [Fact]
    public async Task LiteralTextInAFormatIsEscaped()
    {
        byte[] ods = await Spreadsheet(writer =>
        {
            StyleId style = writer.Style(new CellStyle { Number = NumberFormat.Parse("\"R&D <\"0") });
            writer.BeginSheet("data", [new("n")]);
            writer.BeginRow();
            writer.Write(5L, style);
            writer.EndRow();
        });

        Assert.Contains("<number:text>R&amp;D &lt;</number:text>", Entry(ods, "styles.xml"), StringComparison.Ordinal);
        Assert.Equal(5, Rows(ods)[1][0].Number);
    }
```

`RawCell.Number` — use whatever accessor `OdsWriterTests` uses for numeric cells (open the file and match it; if the property is named differently, use that name).

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests -c Release -- --filter-class "*OdsStyleTests"`
Expected: FAIL.

- [ ] **Step 4: Implement `OdsStyles` and use it**

```csharp
using System.Globalization;
using System.Text;

namespace TriasDev.Tabular.Ods;

/// <summary>A resolved cell style: its name in <c>styles.xml</c>, and whether its numbers are percentages.</summary>
internal readonly record struct OdsCellStyle(string Name, bool Percent);

/// <summary>
/// The spreadsheet's registered styles as common cell styles in <c>styles.xml</c>, with their data
/// styles, written at the end — so they may appear while <c>content.xml</c> streams. One per style
/// and data style actually used, resolved on first use and cached.
/// </summary>
internal sealed class OdsStyles
{
    private const string DateData = "tnd";
    private const string DateTimeData = "tndt";
    private const string BooleanData = "tnb";

    private readonly StyleTable _table;
    private readonly List<string> _cellStyles = [];                     // markup of each ts{n}, n = index + 1
    private readonly Dictionary<(int Style, string? Data), string> _cellNames = [];
    private readonly List<string> _dataStyles = [];                     // markup of each tn{n}
    private readonly Dictionary<string, string> _dataNames = new(StringComparer.Ordinal); // code + kind → name
    private OdsCellStyle[] _resolved = [];                              // per (style, kind); Name null = not yet

    public OdsStyles(StyleTable table) => _table = table;

    /// <summary>The cell style for a registered style (index 1 onwards) on a kind of value.</summary>
    public OdsCellStyle Cell(int style, ValueKind kind)
    {
        int slot = (style * StyleTable.ValueKinds) + (int)kind;
        OdsCellStyle[] resolved = _resolved;
        return slot < resolved.Length && resolved[slot].Name is not null ? resolved[slot] : Resolve(slot, style, kind);
    }

    /// <summary>The whole <c>styles.xml</c>.</summary>
    public byte[] Build()
    {
        StringBuilder xml = new(OdsParts.XmlDeclaration + "<office:document-styles" + OdsParts.Namespaces + "><office:styles>");

        foreach (string data in _dataStyles)
        {
            xml.Append(data);
        }

        foreach (string cell in _cellStyles)
        {
            xml.Append(cell);
        }

        return Encoding.UTF8.GetBytes(xml.Append("</office:styles></office:document-styles>").ToString());
    }

    private OdsCellStyle Resolve(int slot, int style, ValueKind kind)
    {
        CellStyle cell = _table[style];
        bool percent = kind is ValueKind.Integer or ValueKind.Number && cell.Number is { Percent: true };

        string? data = kind switch
        {
            ValueKind.Integer or ValueKind.Number when cell.Number is { } number => DataStyle("n:" + number.Code, name => NumberStyle(name, number)),
            ValueKind.Date or ValueKind.DateTime when cell.Date is { } date => DataStyle("d:" + date.Code, name => DateStyle(name, date)),
            ValueKind.Date => FixedDataStyle(DateData, OdsParts.DateDataStyle),
            ValueKind.DateTime => FixedDataStyle(DateTimeData, OdsParts.DateTimeDataStyle),
            ValueKind.Boolean => FixedDataStyle(BooleanData, name => $"<number:boolean-style style:name=\"{name}\"><number:boolean/></number:boolean-style>"),
            _ => null,
        };

        if (!_cellNames.TryGetValue((style, data), out string? cellName))
        {
            cellName = string.Create(CultureInfo.InvariantCulture, $"ts{_cellStyles.Count + 1}");
            _cellStyles.Add(CellStyleMarkup(cellName, data, cell));
            _cellNames.Add((style, data), cellName);
        }

        if (slot >= _resolved.Length)
        {
            Array.Resize(ref _resolved, Math.Max(slot + 1, _table.Count * StyleTable.ValueKinds));
        }

        return _resolved[slot] = new OdsCellStyle(cellName, percent);
    }

    /// <summary>The data style for a format code — keys "n:…" and "d:…" keep numbers and dates apart — numbered tn1, tn2, … on first use.</summary>
    private string DataStyle(string key, Func<string, string> markup)
    {
        if (!_dataNames.TryGetValue(key, out string? name))
        {
            name = string.Create(CultureInfo.InvariantCulture, $"tn{++_numbered}");
            _dataStyles.Add(markup(name));
            _dataNames.Add(key, name);
        }

        return name;
    }

    /// <summary>A data style with a fixed name (the default date, date-time and boolean ones), declared on first use.</summary>
    private string FixedDataStyle(string name, Func<string, string> markup)
    {
        if (_dataNames.TryAdd(name, name))
        {
            _dataStyles.Add(markup(name));
        }

        return name;
    }
}
```

Add the field `private int _numbered;` next to `_dataNames`. The lambdas in `Resolve` allocate, but `Resolve` runs once per `(style, kind)` pair, never per cell. With `EveryKind` in one style the number style is `tn1` (the long cell comes before the date) and the date style `tn2`; text and empty share one cell style by `(style, data)`, so `ts1` serves both.

The markup helpers (private static in `OdsStyles`):

```csharp
    private static string NumberStyle(string name, NumberFormat format)
    {
        StringBuilder xml = new();
        xml.Append(format.Percent ? "<number:percentage-style" : "<number:number-style").Append(" style:name=\"").Append(name).Append("\">");
        AppendText(xml, format.Prefix);
        xml.Append(CultureInfo.InvariantCulture, $"<number:number number:decimal-places=\"{format.DecimalPlaces}\" number:min-decimal-places=\"{format.MinDecimalPlaces}\" number:min-integer-digits=\"{format.MinIntegerDigits}\"");
        xml.Append(format.Grouping ? " number:grouping=\"true\"/>" : "/>");
        AppendText(xml, format.Percent ? "%" : string.Empty);
        AppendText(xml, format.Suffix);
        return xml.Append(format.Percent ? "</number:percentage-style>" : "</number:number-style>").ToString();
    }

    private static string DateStyle(string name, DateFormat format)
    {
        StringBuilder xml = new($"<number:date-style style:name=\"{name}\">");

        foreach (DatePart part in format.Parts)
        {
            string? element = part.Kind switch
            {
                DatePartKind.Year => "year",
                DatePartKind.Month => "month",
                DatePartKind.Day => "day",
                DatePartKind.Hour => "hours",
                DatePartKind.Minute => "minutes",
                DatePartKind.Second => "seconds",
                _ => null,
            };

            if (element is null)
            {
                AppendText(xml, part.Text);
                continue;
            }

            xml.Append("<number:").Append(element).Append(part.Long ? " number:style=\"long\"/>" : "/>");
        }

        return xml.Append("</number:date-style>").ToString();
    }

    private static string CellStyleMarkup(string name, string? data, CellStyle cell)
    {
        StringBuilder xml = new($"<style:style style:name=\"{name}\" style:family=\"table-cell\"");
        xml.Append(data is null ? ">" : $" style:data-style-name=\"{data}\">");

        xml.Append("<style:table-cell-properties");
        xml.Append(cell.Fill is { } fill ? $" fo:background-color=\"{fill}\"" : string.Empty);
        xml.Append(cell.Border is { } border ? $" fo:border=\"0.06pt solid {border.Color}\"" : string.Empty);
        xml.Append(cell.Wrap ? " fo:wrap-option=\"wrap\"" : string.Empty);
        xml.Append(cell.Horizontal != HorizontalAlignment.General ? " style:text-align-source=\"fix\"" : string.Empty);
        xml.Append("/>");

        if (cell.Horizontal != HorizontalAlignment.General)
        {
            xml.Append("<style:paragraph-properties fo:text-align=\"").Append(cell.Horizontal switch { HorizontalAlignment.Left => "start", HorizontalAlignment.Center => "center", _ => "end" }).Append("\"/>");
        }

        if (cell.Font is { } font)
        {
            xml.Append("<style:text-properties");
            xml.Append(font.Color is { } color ? $" fo:color=\"{color}\"" : string.Empty);
            xml.Append(font.Bold ? " fo:font-weight=\"bold\" style:font-weight-asian=\"bold\" style:font-weight-complex=\"bold\"" : string.Empty);
            xml.Append(font.Italic ? " fo:font-style=\"italic\" style:font-style-asian=\"italic\" style:font-style-complex=\"italic\"" : string.Empty);
            xml.Append("/>");
        }

        return xml.Append("</style:style>").ToString();
    }

    private static void AppendText(StringBuilder xml, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        xml.Append("<number:text>");

        foreach (char c in text)
        {
            _ = c switch
            {
                '&' => xml.Append("&amp;"),
                '<' => xml.Append("&lt;"),
                '>' => xml.Append("&gt;"),
                _ => xml.Append(c),
            };
        }

        xml.Append("</number:text>");
    }
```

`CellColor.ToString()` gives `#RRGGBB` (upper case), which the structural test expects. The test's font assertion `fo:color="#FFFFFF" fo:font-weight="bold"` matches the order above.

`OdsParts` changes: make `XmlDeclaration` and `Namespaces` `internal const`; move the two default date data-style bodies into `public static string DateDataStyle(string name)` and `public static string DateTimeDataStyle(string name)` returning the `<number:date-style style:name="{name}">…</number:date-style>` markup now inlined in `BuildContentStart` (which calls them with `"N1"` and `"N2"`, so `content.xml` is unchanged); delete `Styles` (now `OdsStyles.Build()`).

`OdsSheetWriter`:

1. Field `private readonly OdsStyles _styles;` from the constructor's table.
2. Numbers: `StartFloat()` stays for style 0. Styled: 

```csharp
    private void StartNumber(int style, ValueKind kind)
    {
        if (style == 0)
        {
            StartFloat();
            return;
        }

        OdsCellStyle cell = _styles.Cell(style, kind);
        _row.Append("<table:table-cell table:style-name=\"");
        _row.Append(cell.Name);
        _row.Append(cell.Percent ? "\" office:value-type=\"percentage\" office:value=\"" : "\" office:value-type=\"float\" office:value=\"");
    }
```

   `WriteLong` calls `StartNumber(style, ValueKind.Integer)`, decimal and double `StartNumber(style, ValueKind.Number)`.
3. Dates: the style name is `style == 0 ? (hasTime ? OdsParts.DateTimeStyle : OdsParts.DateStyle) : _styles.Cell(style, hasTime ? ValueKind.DateTime : ValueKind.Date).Name`.
4. Booleans: `style == 0 ? OdsParts.BooleanStyle : _styles.Cell(style, ValueKind.Boolean).Name`.
5. Text: `WriteString(value, style)` — after `<table:table-cell`, when `style != 0` append ` table:style-name="` + `_styles.Cell(style, ValueKind.Text).Name` + `"`, then ` office:value-type="string"` as now. The header passes 0.
6. `WriteEmpty(int style)`: `style == 0` → `<table:table-cell/>` as now; otherwise `<table:table-cell table:style-name="NAME"/>` with `ValueKind.Empty`.
7. `Complete()`: `_zip.AddStored("styles.xml", _styles.Build());`.

- [ ] **Step 5: Run**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests -c Release -- --filter-class "*OdsStyleTests"` (with `TABULAR_REQUIRE_SOFFICE=1` so the probe cannot skip) → PASS; then `--filter-class "*Ods*"` and `--filter-class "*LibreOfficeInteropTests"` → PASS unchanged.

- [ ] **Step 6: Add the ods half of the interop tests**

In `StyledInteropTests`: add `[InlineData(TabularFormat.Ods, "ods")]` to `ShowsTheFormats`, and:

```csharp
    [Fact]
    public async Task KeepsTheFillAndFontOfAnOds()
    {
        byte[] xlsx = LibreOffice.Convert(await Write(TabularFormat.Ods), "ods", "xlsx:Calc MS Excel 2007 XML", "xlsx");
        string styles = Entry(xlsx, "xl/styles.xml");

        Assert.Contains("rgb=\"FFF8696B\"", styles, StringComparison.OrdinalIgnoreCase);
        Assert.Matches("<b( val=\"(true|1)\")?/>", styles);
    }
```

Run: `TABULAR_REQUIRE_SOFFICE=1 dotnet test --project tests/TriasDev.Tabular.Tests -c Release -- --filter-class "*StyledInteropTests"` → PASS.

- [ ] **Step 7: Commit**

```bash
git add src tests
git commit -m "feat(write): ods styles — common cell styles and data styles in styles.xml, written at the end"
```

---

### Task 6: No allocation per styled cell; documentation

**Files:**
- Modify: `tests/TriasDev.Tabular.Tests/Writing/StyledWriterTests.cs`, `docs/KNOWN-ISSUES.md`, `README.md` (the writing section, if one exists — otherwise only KNOWN-ISSUES)
- Test: `StyledWriterTests.AllocatesNothingPerStyledCell`

**Interfaces:**
- Consumes: everything above.

- [ ] **Step 1: Write the test**

```csharp
    [Theory]
    [InlineData(TabularFormat.Csv)]
    [InlineData(TabularFormat.Xlsx)]
    [InlineData(TabularFormat.Ods)]
    public async Task AllocatesNothingPerStyledCell(TabularFormat format)
    {
        CellStyle[] palette = [.. Enumerable.Range(0, 8).Select(i => new CellStyle { Fill = CellColor.FromRgb(i * 0x101010), Number = NumberFormat.Parse("0.00"), Date = DateFormat.Parse("dd/mm/yyyy") })];

        async ValueTask<long> Allocated(int rows)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();

            await using (TabularWriter writer = TabularWriter.Create(Stream.Null, format, new TabularWriterOptions { LeaveOpen = true }))
            {
                StyleId[] styles = [.. palette.Select(writer.Style)];
                writer.BeginSheet("data", [new("text"), new("number"), new("integer"), new("date"), new("flag")]);

                for (int i = 0; i < rows; i++)
                {
                    StyleId style = styles[i % styles.Length];
                    writer.BeginRow();
                    writer.Write("text", style);
                    writer.Write(i * 0.5, style);
                    writer.Write((long)i, style);
                    writer.Write(new DateOnly(2026, 10, 4), style);
                    writer.Write(i % 2 == 0, style);
                    writer.EndRow();

                    if (writer.FlushRecommended)
                    {
                        await writer.FlushAsync(Token);
                    }
                }

                await writer.CompleteAsync(Token);
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        await Allocated(10_000);                       // warm-up: static state, pools, JIT
        long tenThousand = await Allocated(10_000);
        long hundredThousand = await Allocated(100_000);

        // 90,000 more rows of five styled cells: what grows with them is under a byte a row.
        Assert.True(hundredThousand - tenThousand < 90_000, $"{format}: {tenThousand:N0} bytes for 10k rows, {hundredThousand:N0} for 100k");
    }
```

- [ ] **Step 2: Run**

Run: `dotnet test --project tests/TriasDev.Tabular.Tests -c Release -- --filter-class "*StyledWriterTests"`
Expected: PASS. If a format fails, measure (dotnet-counters or a loop with `GC.GetAllocatedBytesForCurrentThread` around single calls) and fix the allocation in the library — never loosen the bound. Typical culprits: a closure or `Func` allocated in `Resolve` on the hot path (must only run on first use), string formatting of the style index (use `AppendFormatted`), boxing of `ValueKind`.

- [ ] **Step 3: Documentation**

`docs/KNOWN-ISSUES.md` — add under the export section:

```markdown
### Styles

- Styles are for xlsx and ods; csv (and a zip of csv sheets) ignores them, so one code path writes every format.
- Format codes are a subset of Excel's (see `NumberFormat` and `DateFormat`); anything else is refused when parsed, never at write time.
- Separators in a date format show as written (`dd/mm/yyyy` shows slashes in every locale); the decimal point and thousands separator of a number format follow the reader's locale.
- A `StyleId` belongs to the writer that returned it; passing it to another writer is refused.
- At most 4096 distinct styles per file. Declare the styles once (`static readonly`) and register each once per writer.
```

Read `README.md`; if it has a section on writing/exporting, add a three-line styled example there (`writer.Style(...)`, `writer.Write(value, style)`); if not, leave README to part 5.

- [ ] **Step 4: Full suite, format, commit**

Run: `dotnet test -c Release` → all PASS. Run `dotnet format --verify-no-changes` → no output.

```bash
git add tests docs README.md
git commit -m "test(write): nothing allocated per styled cell in any format; document styles"
```
