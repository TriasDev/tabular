using System.Text;

namespace TriasDev.Tabular.Archive;

/// <summary>The header of one gzip member (RFC 1952, section 2.3), and the words for its damage.</summary>
internal static class GzipHeader
{
    private const byte FlagHeaderCrc = 0x02;
    private const byte FlagExtra = 0x04;
    private const byte FlagName = 0x08;
    private const byte FlagComment = 0x10;
    private const byte ReservedFlags = 0xE0;

    /// <summary>How long a stored name or comment may run before the header is taken for damage.</summary>
    internal const int MaxTextBytes = 64 * 1024;

    /// <summary>The gzip magic and the deflate method — the only method the format defines.</summary>
    public static bool HasSignature(ReadOnlySpan<byte> head) =>
        head.Length >= 3 && head[0] == 0x1F && head[1] == 0x8B && head[2] == 0x08;

    /// <summary>Reads a member's header from the stream's position and leaves the stream right after it.</summary>
    /// <returns>The original file name the header stores, or null when it stores none.</returns>
    public static string? Read(Stream stream)
    {
        Span<byte> fixedPart = stackalloc byte[10];
        Fill(stream, fixedPart);

        if (!HasSignature(fixedPart))
        {
            throw Corrupt("a member does not start with the gzip signature");
        }

        byte flags = fixedPart[3];

        if ((flags & ReservedFlags) != 0)
        {
            throw Corrupt("its header sets flags the format reserves");
        }

        if ((flags & FlagExtra) != 0)
        {
            Span<byte> length = stackalloc byte[2];
            Fill(stream, length);
            Skip(stream, length[0] | (length[1] << 8));
        }

        string? name = (flags & FlagName) != 0 ? ReadText(stream) : null;

        if ((flags & FlagComment) != 0)
        {
            _ = ReadText(stream);
        }

        if ((flags & FlagHeaderCrc) != 0)
        {
            Skip(stream, 2);
        }

        return string.IsNullOrEmpty(name) ? null : name;
    }

    public static TabularFormatException Truncated() =>
        new(TabularFormatException.Truncated, "The gzip file is cut off: it ends before its content does.");

    public static TabularFormatException Corrupt(string what) =>
        new(TabularFormatException.Corrupt, $"The gzip file is damaged: {what}.");

    /// <summary>A zero-terminated field, which the format says is Latin-1.</summary>
    private static string ReadText(Stream stream)
    {
        byte[] text = new byte[256];
        int length = 0;

        while (true)
        {
            int b = stream.ReadByte();

            if (b < 0)
            {
                throw Truncated();
            }

            if (b == 0)
            {
                return Encoding.Latin1.GetString(text, 0, length);
            }

            if (length == MaxTextBytes)
            {
                throw Corrupt("a name or comment in its header has no end");
            }

            if (length == text.Length)
            {
                Array.Resize(ref text, text.Length * 2);
            }

            text[length++] = (byte)b;
        }
    }

    private static void Fill(Stream stream, Span<byte> buffer)
    {
        if (stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) < buffer.Length)
        {
            throw Truncated();
        }
    }

    /// <summary>Reads past a field rather than seeking, so a field longer than the file is a cut.</summary>
    private static void Skip(Stream stream, int count) => Fill(stream, new byte[count]);
}
