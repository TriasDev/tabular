using System.Text;

namespace TriasDev.Tabular.Csv;

/// <summary>
/// Windows-1252, decoded without registering anything.
/// </summary>
/// <remarks>
/// <para>
/// The obvious way to get this encoding is
/// <c>Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)</c>, and this library used it.
/// But registering a provider mutates process-wide state, and it did so from a static constructor —
/// invisibly, on the first csv anyone read. ADR-0001 records exactly that behaviour as a mark
/// against one of the libraries this cursor replaced. Doing it ourselves while holding a competitor
/// to account for it is not a position worth defending.
/// </para>
/// <para>
/// The encoding is small enough to own: bytes 0x00-0x7F and 0xA0-0xFF map to the code points of the
/// same value, and the twenty-seven printable characters in 0x80-0x9F are a table. Five positions in
/// that range are unassigned and decode to the replacement character, as the specification says.
/// </para>
/// <para>
/// Decoding only. This is the fallback for a file that is not valid UTF-8; nothing here writes.
/// </para>
/// </remarks>
internal sealed class Windows1252Encoding : Encoding
{
    /// <summary>The characters for bytes 0x80 through 0x9F.</summary>
    private static readonly char[] HighRange =
    [
        '€', '�', '‚', 'ƒ', '„', '…', '†', '‡',
        'ˆ', '‰', 'Š', '‹', 'Œ', '�', 'Ž', '�',
        '�', '‘', '’', '“', '”', '•', '–', '—',
        '˜', '™', 'š', '›', 'œ', '�', 'ž', 'Ÿ',
    ];

    /// <summary>The single shared instance.</summary>
    public static Windows1252Encoding Instance { get; } = new();

    private Windows1252Encoding()
    {
    }

    /// <inheritdoc />
    public override string WebName => "windows-1252";

    /// <inheritdoc />
    public override int CodePage => 1252;

    /// <inheritdoc />
    public override int GetCharCount(byte[] bytes, int index, int count) => count;

    /// <inheritdoc />
    public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(chars);

        for (int i = 0; i < byteCount; i++)
        {
            chars[charIndex + i] = Decode(bytes[byteIndex + i]);
        }

        return byteCount;
    }

    /// <inheritdoc />
    public override int GetMaxCharCount(int byteCount) => byteCount;

    private static char Decode(byte value) =>
        value is >= 0x80 and <= 0x9F ? HighRange[value - 0x80] : (char)value;

    /// <inheritdoc />
    /// <remarks>Encoding is not supported; this exists to read files, not to write them.</remarks>
    public override int GetByteCount(char[] chars, int index, int count) =>
        throw new NotSupportedException("This encoding decodes only.");

    /// <inheritdoc />
    /// <remarks>Encoding is not supported; this exists to read files, not to write them.</remarks>
    public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex) =>
        throw new NotSupportedException("This encoding decodes only.");

    /// <inheritdoc />
    /// <remarks>Encoding is not supported; this exists to read files, not to write them.</remarks>
    public override int GetMaxByteCount(int charCount) =>
        throw new NotSupportedException("This encoding decodes only.");
}
