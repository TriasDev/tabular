using System.Text;

namespace TriasDev.Tabular.Csv;

/// <summary>How a csv file is encoded and punctuated.</summary>
public sealed record CsvDialect
{
    /// <summary>The encoding its bytes are read with.</summary>
    public required Encoding Encoding { get; init; }

    /// <summary>Where <see cref="Encoding"/> came from.</summary>
    public required DialectSource EncodingSource { get; init; }

    /// <summary>The character separating fields.</summary>
    public required char Delimiter { get; init; }

    /// <summary>Where <see cref="Delimiter"/> came from.</summary>
    public required DialectSource DelimiterSource { get; init; }

    /// <summary>The character that quotes a field.</summary>
    public required char Quote { get; init; }
}
