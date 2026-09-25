namespace TriasDev.Tabular.Tests.Fixtures;

/// <summary>
/// The xlsx half of the correctness gate. Each fixture pins exactly one shape a real writer produces
/// and a naive reader gets wrong.
/// </summary>
public static class XlsxGoldenFixtures
{
    /// <summary>
    /// A stylesheet declaring three cell formats: general, the built-in date format 14, and a custom
    /// date format. A cell's <c>s</c> attribute indexes into <c>cellXfs</c>, which is the only way to
    /// tell a date from an ordinary number — the cell itself carries a bare serial either way.
    /// </summary>
    private const string StylesWithDateFormats =
        """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><numFmts count="1"><numFmt numFmtId="164" formatCode="dd\.mm\.yyyy"/></numFmts><fonts count="1"><font/></fonts><fills count="1"><fill/></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="3"><xf numFmtId="0" xfId="0"/><xf numFmtId="14" xfId="0" applyNumberFormat="1"/><xf numFmtId="164" xfId="0" applyNumberFormat="1"/></cellXfs></styleSheet>""";

    public static IReadOnlyList<GoldenFixture> All =>
    [
        RichText(),
        InlineString(),
        NoSharedStringTable(),
        SparseRow(),
        EmptyAndDuplicateHeaders(),
        Date1904(),
        DateFormats(),
        BooleanAndFormula(),
    ];

    /// <summary>
    /// A shared string assembled from several formatted runs. Reading only the item's first
    /// <c>&lt;t&gt;</c> — or its direct <c>Text</c> child, which does not exist here — yields an empty
    /// string, which is the defect observed in the prior art.
    /// </summary>
    private static GoldenFixture RichText()
    {
        byte[] content = new XlsxPackage()
            .WithSharedStrings("""<si><r><rPr><b/></rPr><t>Hello</t></r><r><t xml:space="preserve"> </t></r><r><rPr><i/></rPr><t>World</t></r></si><si><t>Plain</t></si>""")
            .WithSheet("Sheet1", """<row r="1"><c r="A1" t="s"><v>1</v></c></row><row r="2"><c r="A2" t="s"><v>0</v></c></row>""")
            .Build();

        return new GoldenFixture
        {
            Name = "xlsx/rich-text",
            FileName = "rich-text.xlsx",
            Content = content,
            ExpectedCells = [["Plain"], ["Hello World"]],
            Pins = "A shared string built from several runs is concatenated, not truncated to nothing.",
        };
    }

    /// <summary>
    /// A cell carrying its text inline instead of through the shared string table. Writers that never
    /// build a table produce whole files this way.
    /// </summary>
    private static GoldenFixture InlineString()
    {
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", """<row r="1"><c r="A1" t="inlineStr"><is><t>Inline</t></is></c><c r="B1" t="inlineStr"><is><r><t>Two</t></r><r><t xml:space="preserve"> runs</t></r></is></c></row>""")
            .Build();

        return new GoldenFixture
        {
            Name = "xlsx/inline-string",
            FileName = "inline-string.xlsx",
            Content = content,
            ExpectedCells = [["Inline", "Two runs"]],
            Pins = "Inline strings are read, including when they are themselves split into runs.",
        };
    }

    /// <summary>A workbook with no shared string part at all.</summary>
    private static GoldenFixture NoSharedStringTable()
    {
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", """<row r="1"><c r="A1"><v>1</v></c><c r="B1"><v>2.5</v></c></row>""")
            .Build();

        return new GoldenFixture
        {
            Name = "xlsx/no-shared-string-table",
            FileName = "no-sst.xlsx",
            Content = content,
            ExpectedCells = [["1", "2.5"]],
            Pins = "A missing shared string part is normal, not a failure.",
        };
    }

    /// <summary>
    /// Rows that skip columns and do not begin at A. Cell position comes from the <c>r</c> reference,
    /// never from the order cells appear in.
    /// </summary>
    private static GoldenFixture SparseRow()
    {
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", """<row r="1"><c r="A1" t="inlineStr"><is><t>a</t></is></c><c r="C1" t="inlineStr"><is><t>c</t></is></c></row><row r="2"><c r="B2" t="inlineStr"><is><t>b</t></is></c></row>""")
            .Build();

        return new GoldenFixture
        {
            Name = "xlsx/sparse-row",
            FileName = "sparse.xlsx",
            Content = content,
            ExpectedCells = [["a", null, "c"], [null, "b"]],
            Pins = "A gap is a gap: cells are placed by their reference, not by their order, and a "
                 + "row ends where its last cell does rather than being padded to the widest row.",
        };
    }

    /// <summary>A header row with an empty cell and a repeated name.</summary>
    private static GoldenFixture EmptyAndDuplicateHeaders()
    {
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", """<row r="1"><c r="A1" t="inlineStr"><is><t>Name</t></is></c><c r="C1" t="inlineStr"><is><t>Name</t></is></c></row><row r="2"><c r="A2" t="inlineStr"><is><t>x</t></is></c><c r="B2" t="inlineStr"><is><t>y</t></is></c><c r="C2" t="inlineStr"><is><t>z</t></is></c></row>""")
            .Build();

        return new GoldenFixture
        {
            Name = "xlsx/empty-and-duplicate-headers",
            FileName = "headers.xlsx",
            Content = content,
            ExpectedCells = [["Name", null, "Name"], ["x", "y", "z"]],
            Pins = "Header cells may be empty or repeated, which is why a column is addressed by index.",
        };
    }

    /// <summary>
    /// A date under the 1904 epoch. The serial is chosen so that a reader ignoring
    /// <c>workbookPr/@date1904</c> produces 2018-12-31 — wrong by more than four years, and therefore
    /// impossible to mistake for a rounding difference.
    /// </summary>
    private static GoldenFixture Date1904()
    {
        byte[] content = new XlsxPackage()
            .WithDate1904()
            .WithStyles(StylesWithDateFormats)
            .WithSheet("Sheet1", """<row r="1"><c r="A1" s="1"><v>43465</v></c></row>""")
            .Build();

        return new GoldenFixture
        {
            Name = "xlsx/date-1904",
            FileName = "date-1904.xlsx",
            Content = content,
            ExpectedCells = [["2023-01-01"]],
            Pins = "The workbook's date epoch is honoured; ignoring it yields 2018-12-31.",
        };
    }

    /// <summary>
    /// A date behind a built-in number format, a date behind a custom one, and a plain number. Only
    /// the style tells them apart.
    /// </summary>
    private static GoldenFixture DateFormats()
    {
        byte[] content = new XlsxPackage()
            .WithStyles(StylesWithDateFormats)
            .WithSheet("Sheet1", """<row r="1"><c r="A1" s="1"><v>44927</v></c><c r="B1" s="2"><v>45000</v></c><c r="C1" s="0"><v>44927</v></c></row>""")
            .Build();

        return new GoldenFixture
        {
            Name = "xlsx/date-formats",
            FileName = "dates.xlsx",
            Content = content,
            ExpectedCells = [["2023-01-01", "2023-03-15", "44927"]],
            Pins = "A date is recognised through both a built-in and a custom number format, and the "
                 + "same serial with no date format stays a number.",
        };
    }

    /// <summary>A boolean cell and a formula cell, the latter read from its cached value.</summary>
    private static GoldenFixture BooleanAndFormula()
    {
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", """<row r="1"><c r="A1" t="b"><v>1</v></c><c r="B1" t="b"><v>0</v></c><c r="C1"><f>1+1</f><v>2</v></c><c r="D1" t="str"><f>"a"&amp;"b"</f><v>ab</v></c></row>""")
            .Build();

        return new GoldenFixture
        {
            Name = "xlsx/boolean-and-formula",
            FileName = "bool-formula.xlsx",
            Content = content,
            ExpectedCells = [["true", "false", "2", "ab"]],
            Pins = "Booleans are not the numbers 1 and 0, and a formula is read from its cached value "
                 + "rather than evaluated.",
        };
    }
}
