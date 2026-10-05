using System.Xml;

using TriasDev.Tabular.Ods;

using Xunit;

namespace TriasDev.Tabular.Tests.Ods;

/// <summary>
/// The time-cell parser reads an ISO 8601 duration exactly as <see cref="XmlConvert.ToTimeSpan"/>
/// does — the same value for what it accepts, and a refusal, not an exception, for what it throws on.
/// </summary>
/// <remarks>
/// The reader used <see cref="XmlConvert.ToTimeSpan"/> until a string per time cell, and an exception
/// per malformed one, were found to be most of what a time cell cost. The replacement is held to the
/// old answer on every input, the oddities of the XSD parser included (<c>PT.5S</c> is valid there).
/// </remarks>
public sealed class IsoDurationTests
{
    public static TheoryData<string> Inputs =>
    [
        // The forms LibreOffice writes.
        "PT12H30M00S", "PT10H59M59.999999999S", "PT01H02M03.4567S", "PT1234H05M06S", "PT10H59M59.99949999999S",
        "PT1080012H00M00S", "PT00H00M00S",

        // The rest of the grammar.
        "P1D", "P1Y2M3DT4H5M6.7S", "-PT1H", "P0D", "PT0S", " PT1H ", " PT1H\t", "P1M", "P13M", "P1Y", "PT1H1S", "PT1M",
        "P1DT1H", "PT1.0000000001S", "PT0.00000005S", "PT0.00000015S", "PT1.S", "PT.5S", "PT.S", "P1W",

        // Near and past the ends of a TimeSpan and of an int.
        "P10675199D", "P10675199DT2H48M5.4775807S", "P10675199DT2H48M5.4775808S", "-P10675199DT2H48M5.4775808S",
        "-P10675199DT2H48M5.4775809S", "P10675200D", "P2147483647D", "PT2147483647H", "PT2147483648H", "P2147483647Y",
        "P29247Y", "P29248Y", "PT1.99999999999999999999S",

        // Malformed.
        "", " ", "P", "PT", "-", "-P", "T1H", "1H", "P1H", "PT1D", "P1DT", "PT1H2", "P1Y2", "PT1HM", "P-1D", "+PT1H",
        "PT1,5S", "pt1h", "PT1H1H", "P1D1D", "P1T1H", "PT1.5", "PT1.5M", "PTS", "PT1S2", "PT1.5S2", "PT٣H",
        "PT１H", "P1D T1H", "P 1D", "--PT1H", "PT1H-", "P1YT", "PY", "PT.5", "P.5D", "PT1H.5S", "PT1M1H",
    ];

    [Theory]
    [MemberData(nameof(Inputs))]
    public void ReadsADurationAsXmlConvertDoes(string input) => AssertSame(input);

    [Fact]
    public void ReadsRandomMarkupAsXmlConvertDoes()
    {
        // Strings drawn from the grammar's own characters, so most are near misses of a duration.
        const string Alphabet = "PTYMDHS0123456789.- ";
        Random random = new(33);
        char[] buffer = new char[16];

        for (int i = 0; i < 20_000; i++)
        {
            int length = random.Next(buffer.Length + 1);

            for (int j = 0; j < length; j++)
            {
                buffer[j] = Alphabet[random.Next(Alphabet.Length)];
            }

            // Mostly well-started ones, or the sign and the P alone would decide almost every case.
            if (length > 1 && random.Next(4) > 0)
            {
                buffer[0] = 'P';
                buffer[1] = random.Next(2) == 0 ? 'T' : buffer[1];
            }

            AssertSame(new string(buffer, 0, length));
        }
    }

    private static void AssertSame(string input)
    {
        bool expectedRead;
        TimeSpan expected = default;

        try
        {
            expected = XmlConvert.ToTimeSpan(input);
            expectedRead = true;
        }
        catch (Exception refused) when (refused is FormatException or OverflowException)
        {
            expectedRead = false;
        }

        bool read = IsoDuration.TryParse(input, out TimeSpan actual);

        Assert.True(expectedRead == read, $"'{input}': XmlConvert {(expectedRead ? "reads" : "refuses")} it, the parser {(read ? "reads" : "refuses")} it.");

        if (read)
        {
            Assert.True(expected == actual, $"'{input}': XmlConvert reads {expected}, the parser {actual}.");
        }
    }
}
