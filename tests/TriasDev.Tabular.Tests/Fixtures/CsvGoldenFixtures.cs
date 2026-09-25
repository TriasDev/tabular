using System.Text;

namespace TriasDev.Tabular.Tests.Fixtures;

/// <summary>
/// The csv half of the correctness gate. A csv file carries no types, so every expected cell is the
/// field's text exactly as it stands in the file; deciding what those texts mean is the analyzer's
/// job, not the reader's.
/// </summary>
public static class CsvGoldenFixtures
{
    public static IReadOnlyList<GoldenFixture> All =>
    [
        Utf8WithoutByteOrderMark(),
        Utf8WithByteOrderMark(),
        Windows1252(),
        SemicolonWithGermanNumbers(),
        QuotedFieldWithLineBreak(),
        RaggedRows(),
        QuotesInsideUnquotedFields(),
        QuotedFieldWithTrailingText(),
    ];

    /// <summary>
    /// UTF-8 with no byte order mark. Assuming a single-byte encoding here is what turns
    /// <c>Müller</c> into mojibake, and it is the defect the prior art carries.
    /// </summary>
    private static GoldenFixture Utf8WithoutByteOrderMark()
    {
        byte[] content = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            .GetBytes("Name;Stadt\nMüller;Köln\nWeiß;Zürich\n");

        return new GoldenFixture
        {
            Name = "csv/utf8-without-bom",
            FileName = "utf8-no-bom.csv",
            Content = content,
            ExpectedCells = [["Name", "Stadt"], ["Müller", "Köln"], ["Weiß", "Zürich"]],
            Pins = "UTF-8 is recognised from the content when no byte order mark announces it.",
        };
    }

    /// <summary>UTF-8 announced by a byte order mark, which must not leak into the first field.</summary>
    private static GoldenFixture Utf8WithByteOrderMark()
    {
        byte[] content = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
            .GetPreamble()
            .Concat(new UTF8Encoding(false).GetBytes("Name;Stadt\nMüller;Köln\n"))
            .ToArray();

        return new GoldenFixture
        {
            Name = "csv/utf8-with-bom",
            FileName = "utf8-bom.csv",
            Content = content,
            ExpectedCells = [["Name", "Stadt"], ["Müller", "Köln"]],
            Pins = "A byte order mark selects the encoding and is not part of the first field.",
        };
    }

    /// <summary>
    /// The same text in Windows-1252, written as raw bytes rather than through an encoder so the test
    /// does not depend on a code-page provider being registered in the host.
    /// </summary>
    private static GoldenFixture Windows1252()
    {
        List<byte> content = [];
        content.AddRange("Name;Stadt\n"u8.ToArray());
        content.AddRange("M"u8.ToArray());
        content.Add(0xFC);                              // ü
        content.AddRange("ller;K"u8.ToArray());
        content.Add(0xF6);                              // ö
        content.AddRange("ln\n"u8.ToArray());

        return new GoldenFixture
        {
            Name = "csv/windows-1252",
            FileName = "win1252.csv",
            Content = content.ToArray(),
            ExpectedCells = [["Name", "Stadt"], ["Müller", "Köln"]],
            Pins = "Bytes that are not valid UTF-8 fall back to Windows-1252 rather than being mangled.",
        };
    }

    /// <summary>
    /// The shape of the large German fixture: semicolon delimiter, decimal comma, and an identifier
    /// column whose values look like decimals.
    /// </summary>
    private static GoldenFixture SemicolonWithGermanNumbers()
    {
        byte[] content = new UTF8Encoding(false)
            .GetBytes("ID;Betrag;Land\n1,00;1.234,56;DEU\n2,00;22693870,00;AUT\n");

        return new GoldenFixture
        {
            Name = "csv/semicolon-german-numbers",
            FileName = "german.csv",
            Content = content,
            ExpectedCells =
            [
                ["ID", "Betrag", "Land"],
                ["1,00", "1.234,56", "DEU"],
                ["2,00", "22693870,00", "AUT"],
            ],
            Pins = "The delimiter is the semicolon even though commas are far more numerous, and the "
                 + "reader hands values on untouched.",
        };
    }

    /// <summary>
    /// A quoted field holding a line break and doubled quotes. A reader that counts lines instead of
    /// records reports four rows here rather than three.
    /// </summary>
    private static GoldenFixture QuotedFieldWithLineBreak()
    {
        byte[] content = new UTF8Encoding(false)
            .GetBytes("Name;Note\r\n\"Acme Corporation\";\"first line\r\nsecond line\"\r\n\"He said \"\"hello\"\"\";plain\r\n");

        return new GoldenFixture
        {
            Name = "csv/quoted-field-with-line-break",
            FileName = "quoted.csv",
            Content = content,
            ExpectedCells =
            [
                ["Name", "Note"],
                ["Acme Corporation", "first line\r\nsecond line"],
                ["He said \"hello\"", "plain"],
            ],
            Pins = "A record is not a line: a quoted field may contain the line ending, and a doubled "
                 + "quote is one quote.",
        };
    }

    /// <summary>
    /// Quotes standing inside a field that was never quoted — inch marks and letter names, in the
    /// shapes a real address file carries.
    /// </summary>
    /// <remarks>
    /// A quote is special only where a field begins. Treating it as special anywhere makes a reader
    /// swallow every line up to the next quote, which on the five-million-row fixture turned
    /// 5,127,968 records into 2,845,485 without any error being raised.
    /// </remarks>
    private static GoldenFixture QuotesInsideUnquotedFields()
    {
        byte[] content = new UTF8Encoding(false)
            .GetBytes("ID;Street\n1;10 N. \"K\" St.\n2;100 21\"st AVE\n3;plain\n");

        return new GoldenFixture
        {
            Name = "csv/quotes-inside-unquoted-fields",
            FileName = "inch-marks.csv",
            Content = content,
            ExpectedCells =
            [
                ["ID", "Street"],
                ["1", "10 N. \"K\" St."],
                ["2", "100 21\"st AVE"],
                ["3", "plain"],
            ],
            Pins = "A quote inside an unquoted field is an ordinary character, and the record count "
                 + "does not change because of it.",
        };
    }

    /// <summary>
    /// A field that opens with a quote and carries text after the closing one, which no specification
    /// allows and which real files contain anyway.
    /// </summary>
    private static GoldenFixture QuotedFieldWithTrailingText()
    {
        byte[] content = new UTF8Encoding(false)
            .GetBytes("ID;Street\n1;\"C\" Road\n2;\"77 Main St, Suite 4\"\n");

        return new GoldenFixture
        {
            Name = "csv/quoted-field-with-trailing-text",
            FileName = "trailing.csv",
            Content = content,
            ExpectedCells =
            [
                ["ID", "Street"],
                ["1", "C Road"],
                ["2", "77 Main St, Suite 4"],
            ],
            Pins = "Text after a closing quote is appended rather than rejected, so one malformed "
                 + "address does not fail an import of five million rows.",
        };
    }

    /// <summary>Rows shorter and longer than the header.</summary>
    private static GoldenFixture RaggedRows()
    {
        byte[] content = new UTF8Encoding(false).GetBytes("a;b;c\n1;2\n1;2;3;4\n");

        return new GoldenFixture
        {
            Name = "csv/ragged-rows",
            FileName = "ragged.csv",
            Content = content,
            ExpectedCells = [["a", "b", "c"], ["1", "2"], ["1", "2", "3", "4"]],
            Pins = "A ragged row is reported at its own width; padding and truncation are decisions "
                 + "for the layer above, not losses inside the reader.",
        };
    }
}
