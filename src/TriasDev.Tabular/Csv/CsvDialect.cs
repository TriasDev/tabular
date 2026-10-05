using System.Text;

namespace TriasDev.Tabular.Csv;

/// <summary>How a csv file was encoded and punctuated, as the reader decided it.</summary>
/// <remarks>
/// Only a reader makes one, so its provenance can be trusted: to state a delimiter, an encoding or a
/// quote yourself, set <see cref="CsvCursorOptions.Delimiter"/>, <see cref="CsvCursorOptions.Encoding"/>
/// or <see cref="CsvCursorOptions.Quote"/>, and the reader reports them here as
/// <see cref="DialectSource.Specified"/>.
/// </remarks>
public sealed record CsvDialect
{
    internal CsvDialect(Encoding encoding, DialectSource encodingSource, char delimiter, DialectSource delimiterSource, char quote)
    {
        Encoding = encoding;
        EncodingSource = encodingSource;
        Delimiter = delimiter;
        DelimiterSource = delimiterSource;
        Quote = quote;
    }

    /// <summary>The encoding its bytes are read with.</summary>
    public Encoding Encoding { get; }

    /// <summary>Where <see cref="Encoding"/> came from.</summary>
    public DialectSource EncodingSource { get; }

    /// <summary>The character separating fields.</summary>
    public char Delimiter { get; }

    /// <summary>Where <see cref="Delimiter"/> came from.</summary>
    public DialectSource DelimiterSource { get; }

    /// <summary>
    /// The character that quotes a field: <see cref="CsvCursorOptions.Quote"/> when stated, else
    /// <c>"</c>.
    /// </summary>
    public char Quote { get; }
}
