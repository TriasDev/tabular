using TriasDev.Tabular.Import;

using Xunit;

namespace TriasDev.Tabular.Tests.Import;

public sealed class HeaderVariantTests
{
    private static readonly string[] Languages = ["en", "de", "fr"];

    [Theory]
    [InlineData("Title#en", "Title", "en")]           // what the TOM catalogue writes
    [InlineData("Title_de", "Title", "de")]
    [InlineData("Title-fr", "Title", "fr")]
    [InlineData("Title.en", "Title", "en")]
    [InlineData("Title DE", "Title", "de")]
    [InlineData("Title (de)", "Title", "de")]
    [InlineData("Title [FR]", "Title", "fr")]
    [InlineData("LinkTitle#en", "LinkTitle", "en")]
    public void ReadsTheMarksFilesActuallyCarry(string header, string name, string variant)
    {
        Assert.True(HeaderVariant.TryParse(header, Languages, out string parsedName, out string parsedVariant));
        Assert.Equal(name, parsedName);

        // Spelled as declared, not as the file spelled it, so a binding never carries "DE" where the
        // schema says "de".
        Assert.Equal(variant, parsedVariant);
    }

    [Theory]
    [InlineData("Order_id")]      // "id" is not a language and this is not an order in Indonesian
    [InlineData("Title")]
    [InlineData("Name#es")]       // a real mark, for a variant this group does not have
    [InlineData("de")]            // nothing but the mark names no field
    [InlineData("")]
    [InlineData(null)]
    public void InventsNoMarkThatIsNotThere(string? header)
    {
        Assert.False(HeaderVariant.TryParse(header, Languages, out _, out _));
    }

    [Fact]
    public void HandsBackTheHeaderItselfWhenThereIsNoMark()
    {
        HeaderVariant.TryParse("Rechtsträger", Languages, out string name, out string variant);

        Assert.Equal("Rechtsträger", name);
        Assert.Equal(string.Empty, variant);
    }
}
