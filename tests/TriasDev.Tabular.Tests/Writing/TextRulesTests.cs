using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>The characters no format may carry, checked on every text cell.</summary>
/// <remarks>Surrogates are built in code: a lone one in attribute data does not survive the test framework.</remarks>
[Collection(AllocationMeasurementCollection.Name)]
public sealed class TextRulesTests
{
    [Theory]
    [InlineData("plain")]
    [InlineData("tab\tand\r\nbreaks")]
    public void AcceptsWritableText(string text) => Assert.Null(TextRules.Check(text));

    [Fact]
    public void AcceptsAWholePair() => Assert.Null(TextRules.Check(string.Concat("pair ", "\uD83D", "\uDE00", " whole")));

    [Theory]
    [InlineData(0x0007)]
    [InlineData(0xFFFE)]
    [InlineData(0xD83D)]
    [InlineData(0xDE00)]
    public void RefusesAForbiddenCharacter(int forbidden) =>
        Assert.Equal(ErrorCodes.Write.InvalidCharacter, TextRules.Check(string.Concat("text ", ((char)forbidden).ToString(), ".")));

    [Fact]
    public void RefusesAPairInTheWrongOrder() =>
        Assert.Equal(ErrorCodes.Write.InvalidCharacter, TextRules.Check(string.Concat("reversed ", "\uDE00", "\uD83D")));

    [Fact]
    public void AllocatesNothingPerCall()
    {
        string text = string.Concat("Portfolio 12, \"quoted\" ", "\uD83D", "\uDE00");
        TextRules.Check(text);

        const int Calls = 100_000;
        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < Calls; i++)
        {
            TextRules.Check(text);
        }

        // Less than a byte per call: a one-off from the runtime tiering the loop up is not a per-call
        // allocation; the boxing this guards against cost 96 bytes on every call.
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, Calls - 1);
    }
}
