using System.IO.Compression;
using System.Text;

namespace TriasDev.Tabular.Tests.Fixtures;

/// <summary>
/// Builds gzip files: as .NET writes them, or with the original file name the gzip tool stores.
/// </summary>
public static class GzipFile
{
    /// <summary>One member holding <paramref name="content"/>; with a file name, stored as FNAME.</summary>
    public static byte[] Of(byte[] content, string? fileName = null, CompressionLevel level = CompressionLevel.Optimal)
    {
        using MemoryStream buffer = new();

        using (GZipStream gzip = new(buffer, level, leaveOpen: true))
        {
            gzip.Write(content);
        }

        byte[] member = buffer.ToArray();

        // .NET 10 writes nothing at all when nothing was written; a gzip file of an empty file still
        // has its member: header, the empty deflate block 03 00, and a trailer of zeros.
        if (member.Length == 0)
        {
            member = [0x1F, 0x8B, 0x08, 0, 0, 0, 0, 0, 0, 0xFF, 0x03, 0x00, 0, 0, 0, 0, 0, 0, 0, 0];
        }

        return fileName is null ? member : WithName(member, fileName);
    }

    /// <summary>One member holding the text in UTF-8 without a byte order mark.</summary>
    public static byte[] Of(string text, string? fileName = null) =>
        Of(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text), fileName);

    /// <summary>
    /// The same member with FNAME set — the flag in byte 3, the Latin-1 name and its zero after the
    /// ten fixed bytes. The trailer covers the content only, so it stays as it was.
    /// </summary>
    public static byte[] WithName(byte[] member, string fileName)
    {
        byte[] named = [.. member[..10], .. Encoding.Latin1.GetBytes(fileName), 0, .. member[10..]];
        named[3] |= 0x08;
        return named;
    }
}
