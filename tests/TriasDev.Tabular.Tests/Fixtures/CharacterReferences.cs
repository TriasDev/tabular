using Xunit;

namespace TriasDev.Tabular.Tests.Fixtures;

/// <summary>
/// Character references and stray ampersands of any length, for every place the scanner reads text
/// in: an xlsx inline string and value, an ods paragraph and attribute (#184).
/// </summary>
internal static class CharacterReferences
{
    private static readonly string Zeros = new('0', 10_000);

    /// <summary>A reference as written, and the text it means.</summary>
    public static TheoryData<string, string> Resolved => new()
    {
        { "&#x000001F600;", "\U0001F600" },
        { "&#0000065;", "A" },
        { $"&#x{Zeros}1F600;", "\U0001F600" },
        { $"&#{Zeros}65;", "A" },
    };

    /// <summary>Text that is no reference to a character, which reads as written.</summary>
    public static TheoryData<string> KeptAsWritten => new()
    {
        "&#x0000110000;",                                                // past the last code point
        "&#x00000000FFFFFFFF;",                                          // what an int would wrap to -1
        $"&#x{Zeros}110000;",
        $"&#{Zeros};",                                                   // zero names no character
        $"&#{new string('9', 10_000)};",                                 // far past the last code point
        $"&{new string('D', 10_000)}",                                   // a stray ampersand before a long word
        $"&{new string('D', 10_000)} ;",                                 // a semicolon, but not the word's
        $"&{new string('D', 10_000)};",                                  // an unknown entity, however long
        $"&#x{Zeros}1F600 ;",
    };
}
