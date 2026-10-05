using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Ods;

/// <summary>
/// The scanner's OpenDocument mode keeps the attributes it is given, by local name, whatever their
/// prefix — and nothing else.
/// </summary>
public sealed class SheetScannerLocalNameTests
{
    private static SheetScanner Scan(string xml, string[]? kept = null) => new(new StringReader(xml), keptLocalNames: kept);

    [Fact]
    public void KeepsPrefixedAttributesByTheirLocalName()
    {
        using SheetScanner scanner = Scan(
            """<table:table-cell table:number-columns-repeated="3" office:value-type="float" office:value="1.5" table:style-name="ce1"/>""",
            ["number-columns-repeated", "value-type", "value"]);

        Assert.True(scanner.Read());
        Assert.Equal("table-cell", scanner.Name.ToString());
        Assert.True(scanner.TryGetAttribute("value-type", out ReadOnlySpan<char> type));
        Assert.Equal("float", type.ToString());
        Assert.True(scanner.TryGetAttribute("value", out ReadOnlySpan<char> value));
        Assert.Equal("1.5", value.ToString());
        Assert.True(scanner.TryGetAttribute("number-columns-repeated", out ReadOnlySpan<char> repeat));
        Assert.Equal("3", repeat.ToString());
        Assert.False(scanner.TryGetAttribute("style-name", out _));
    }

    [Fact]
    public void KeepsOnlyTheWorksheetAttributesWithoutASet()
    {
        using SheetScanner scanner = Scan("""<c r="A1" t="s" s="2" x:value="9"/>""");

        Assert.True(scanner.Read());
        Assert.True(scanner.TryGetAttribute("r", out _));
        Assert.True(scanner.TryGetAttribute("t", out _));
        Assert.False(scanner.TryGetAttribute("value", out _));
    }

    [Fact]
    public void NamesTheOpenDocumentAttributesWhenOneIsRepeatedPastTheCeiling()
    {
        // The xlsx wording, "its r, t or s attribute", named attributes an OpenDocument part has none of.
        string repeated = string.Concat(Enumerable.Range(0, 17).Select(i => $" p{i}:value=\"1\""));
        using SheetScanner ods = Scan($"<table:table-cell{repeated}/>", ["value-type", "value"]);

        string message = Assert.Throws<TabularFormatException>(() => ods.Read()).Message;

        Assert.Contains("value-type", message, StringComparison.Ordinal);
        Assert.DoesNotContain("r, t or s", message, StringComparison.Ordinal);

        using SheetScanner xlsx = Scan($"<c{string.Concat(Enumerable.Range(0, 17).Select(i => $" r=\"A{i}\""))}/>");
        Assert.Contains("r, t or s", Assert.Throws<TabularFormatException>(() => xlsx.Read()).Message, StringComparison.Ordinal);
    }
}
