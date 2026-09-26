using System.IO.Compression;
using System.Text;

namespace TriasDev.Tabular.Tests.Fixtures;

/// <summary>
/// Builds a zip archive of arbitrary files, in the order they are added — for archives a user
/// uploads, which hold whatever was in the folder they zipped.
/// </summary>
public sealed class ZipArchiveBuilder
{
    private readonly List<(string Path, byte[] Content, CompressionLevel Level)> _entries = [];
    private readonly HashSet<string> _encrypted = [];

    /// <summary>Adds a file with the given bytes.</summary>
    public ZipArchiveBuilder With(string path, byte[] content, CompressionLevel level = CompressionLevel.Optimal)
    {
        _entries.Add((path, content, level));
        return this;
    }

    /// <summary>Adds a text file in UTF-8 without a byte order mark.</summary>
    public ZipArchiveBuilder With(string path, string text, CompressionLevel level = CompressionLevel.Optimal) =>
        With(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text), level);

    /// <summary>Adds a directory entry.</summary>
    public ZipArchiveBuilder WithDirectory(string path) => With(path.EndsWith('/') ? path : path + "/", []);

    /// <summary>
    /// Adds a file whose headers say it is encrypted. The content stays readable: the flag is what a
    /// reader must honour, since opening it would hand over ciphertext as text.
    /// </summary>
    public ZipArchiveBuilder WithEncrypted(string path, string text)
    {
        _encrypted.Add(path);
        return With(path, text);
    }

    public byte[] Build()
    {
        using MemoryStream buffer = new();

        using (ZipArchive zip = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string path, byte[] content, CompressionLevel level) in _entries)
            {
                using Stream entry = zip.CreateEntry(path, level).Open();
                entry.Write(content);
            }
        }

        byte[] bytes = buffer.ToArray();

        foreach (string path in _encrypted)
        {
            SetEncryptedFlag(bytes, path);
        }

        return bytes;
    }

    /// <summary>Sets bit 0 of the general-purpose flags in the entry's local and central headers.</summary>
    private static void SetEncryptedFlag(byte[] bytes, string path)
    {
        byte[] name = Encoding.UTF8.GetBytes(path);

        for (int i = 0; i + 46 <= bytes.Length; i++)
        {
            if (bytes.AsSpan(i, 4).SequenceEqual("PK\u0003\u0004"u8))
            {
                FlagIfNamed(bytes, nameAt: i + 30, lengthAt: i + 26, flagsAt: i + 6, name);
            }
            else if (bytes.AsSpan(i, 4).SequenceEqual("PK\u0001\u0002"u8))
            {
                FlagIfNamed(bytes, nameAt: i + 46, lengthAt: i + 28, flagsAt: i + 8, name);
            }
        }
    }

    private static void FlagIfNamed(byte[] bytes, int nameAt, int lengthAt, int flagsAt, byte[] name)
    {
        int nameLength = bytes[lengthAt] | (bytes[lengthAt + 1] << 8);

        if (nameLength == name.Length && nameAt + nameLength <= bytes.Length
            && bytes.AsSpan(nameAt, nameLength).SequenceEqual(name))
        {
            bytes[flagsAt] |= 1;
        }
    }
}
