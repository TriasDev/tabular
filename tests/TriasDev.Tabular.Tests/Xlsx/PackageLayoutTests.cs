using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Xlsx;

/// <summary>
/// Parts found the way the package says, not the way Excel happens to name them; the 1904 epoch in
/// either spelling; empty shared and inline strings.
/// </summary>
/// <remarks>Every fixture elsewhere names its parts as Excel does, so none of these paths was taken.</remarks>
public sealed class PackageLayoutTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private const string DateStyles =
        """<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><cellXfs count="2"><xf numFmtId="0"/><xf numFmtId="14" applyNumberFormat="1"/></cellXfs></styleSheet>""";

    /// <summary>The package with its entries renamed and their text rewritten.</summary>
    private static byte[] Rewritten(byte[] package, Func<string, string> rename, Func<string, string, string> rewrite)
    {
        using MemoryStream result = new();

        using (ZipArchive source = new(new MemoryStream(package), ZipArchiveMode.Read))
        using (ZipArchive target = new(result, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (ZipArchiveEntry entry in source.Entries)
            {
                using StreamReader reader = new(entry.Open(), Encoding.UTF8);
                string text = rewrite(entry.FullName, reader.ReadToEnd());

                using StreamWriter writer = new(target.CreateEntry(rename(entry.FullName)).Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
        }

        return result.ToArray();
    }

    private static RawCell[] FirstRow(byte[] package)
    {
        using XlsxCursor cursor = new(new MemoryStream(package, writable: false), cancellationToken: Token);
        Assert.True(cursor.ReadRow(Token));
        return cursor.CurrentRow.ToArray();
    }

    private static byte[] TextAndDate() =>
        new XlsxPackage()
            .WithSheet("S", """<row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" s="1"><v>45000</v></c></row>""")
            .WithSharedStrings("<si><t>x</t></si>")
            .WithStyles(DateStyles)
            .Build();

    [Fact]
    public void FindsTheSharedStringsAndStylesByTheirRelationshipsWhateverTheirNames()
    {
        byte[] package = Rewritten(
            TextAndDate(),
            name => name switch
            {
                "xl/sharedStrings.xml" => "xl/strings/table.xml",
                "xl/styles.xml" => "xl/look.xml",
                _ => name,
            },
            (name, text) => name switch
            {
                "xl/_rels/workbook.xml.rels" => text.Replace("Target=\"sharedStrings.xml\"", "Target=\"strings/table.xml\"", StringComparison.Ordinal)
                    .Replace("Target=\"styles.xml\"", "Target=\"look.xml\"", StringComparison.Ordinal),
                "[Content_Types].xml" => text.Replace("/xl/sharedStrings.xml", "/xl/strings/table.xml", StringComparison.Ordinal)
                    .Replace("/xl/styles.xml", "/xl/look.xml", StringComparison.Ordinal),
                _ => text,
            });

        RawCell[] row = FirstRow(package);

        Assert.Equal("x", row[0].Text);
        Assert.Equal(new DateTime(2023, 3, 15, 0, 0, 0, DateTimeKind.Unspecified), row[1].Date);
    }

    [Fact]
    public void FindsTheSharedStringsAndStylesByTheirConventionalNamesWhenNoRelationshipStatesThem()
    {
        string relationships = string.Empty;
        byte[] package = Rewritten(
            TextAndDate(),
            name => name,
            (name, text) =>
            {
                if (name != "xl/_rels/workbook.xml.rels")
                {
                    return text;
                }

                relationships = Regex.Replace(text, "<Relationship [^>]*/(sharedStrings|styles)\"[^>]*/>", string.Empty);
                return relationships;
            });

        Assert.DoesNotContain("sharedStrings", relationships, StringComparison.Ordinal);
        Assert.DoesNotContain("styles", relationships, StringComparison.Ordinal);

        RawCell[] row = FirstRow(package);

        Assert.Equal("x", row[0].Text);
        Assert.Equal(new DateTime(2023, 3, 15, 0, 0, 0, DateTimeKind.Unspecified), row[1].Date);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    public void ReadsDatesFromThe1904EpochInEitherSpelling(string value)
    {
        byte[] package = Rewritten(
            new XlsxPackage().WithSheet("S", """<row r="1"><c r="A1" s="1"><v>43708</v></c></row>""").WithStyles(DateStyles).WithDate1904().Build(),
            name => name,
            (name, text) => name == "xl/workbook.xml" ? text.Replace("date1904=\"1\"", $"date1904=\"{value}\"", StringComparison.Ordinal) : text);

        Assert.Equal(new DateTime(2023, 9, 1, 0, 0, 0, DateTimeKind.Unspecified), FirstRow(package)[0].Date);
    }

    [Fact]
    public void ReadsAnEmptySharedOrInlineStringAsAnEmptyCellAndKeepsTheTableInStep()
    {
        byte[] package = new XlsxPackage()
            .WithSheet("S", """<row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c><c r="C1" t="inlineStr"><is/></c><c r="D1" t="s"><v>2</v></c></row>""")
            .WithSharedStrings("<si/><si><t>b</t></si><si><r><t/></r></si>")
            .Build();

        RawCell[] row = FirstRow(package);

        Assert.True(row[0].IsEmpty);
        Assert.Equal("b", row[1].Text);
        Assert.True(row[2].IsEmpty);
        Assert.True(row[3].IsEmpty);
    }

    [Fact]
    public void PassesOverRelationshipsThatPointOutsideThePackageOrAreIncomplete()
    {
        // Ahead of the real ones: an external target names nothing in this package, and a
        // relationship without an id or a target cannot be followed. Taken at their word, the styles
        // went missing — the date read as a number — or the open failed on a null.
        byte[] package = Rewritten(
            TextAndDate(),
            name => name,
            (name, text) => name == "xl/_rels/workbook.xml.rels"
                ? text.Replace(
                    "<Relationship ",
                    """<Relationship Id="rIdOut" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="https://example.invalid/styles.xml" TargetMode="External"/>"""
                    + """<Relationship Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="nowhere.xml"/>"""
                    + """<Relationship Id="rIdNoTarget" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"/>"""
                    + "<Relationship ",
                    StringComparison.Ordinal)
                : text);

        RawCell[] row = FirstRow(package);

        Assert.Equal("x", row[0].Text);
        Assert.Equal(new DateTime(2023, 3, 15, 0, 0, 0, DateTimeKind.Unspecified), row[1].Date);
    }
}
