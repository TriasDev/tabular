using System.Formats.Tar;
using System.Text;

namespace TriasDev.Tabular.Tests.Fixtures;

/// <summary>Builds tar files in code, in any of the formats System.Formats.Tar writes.</summary>
public static class TarArchive
{
    /// <summary>A tar of text files in UTF-8, in the order given.</summary>
    public static byte[] Of(TarEntryFormat format, params (string Path, string Content)[] files) =>
        Of(format, [.. files.Select(f => File(format, f.Path, f.Content))]);

    /// <summary>A tar of the given entries, in the order given, with its end blocks.</summary>
    public static byte[] Of(TarEntryFormat format, params TarEntry[] entries)
    {
        using MemoryStream buffer = new();

        using (TarWriter writer = new(buffer, format, leaveOpen: true))
        {
            foreach (TarEntry entry in entries)
            {
                writer.WriteEntry(entry);
            }
        }

        return buffer.ToArray();
    }

    public static TarEntry File(TarEntryFormat format, string path, string content) =>
        File(format, path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content));

    public static TarEntry File(TarEntryFormat format, string path, byte[] content)
    {
        TarEntry entry = Entry(format, TarEntryType.RegularFile, path);
        entry.DataStream = new MemoryStream(content);
        return entry;
    }

    /// <summary>An entry of any type without data — a directory, a link (give it a LinkName), a device.</summary>
    public static TarEntry Entry(TarEntryFormat format, TarEntryType type, string path) => format switch
    {
        TarEntryFormat.V7 => new V7TarEntry(type == TarEntryType.RegularFile ? TarEntryType.V7RegularFile : type, path),
        TarEntryFormat.Ustar => new UstarTarEntry(type, path),
        TarEntryFormat.Gnu => new GnuTarEntry(type, path),
        _ => new PaxTarEntry(type, path),
    };

    /// <summary>
    /// The same tar with the header at <paramref name="headerOffset"/> given another type flag and a
    /// checksum to match — for types TarWriter will not write, such as a GNU sparse file.
    /// </summary>
    public static byte[] Retyped(byte[] tar, int headerOffset, char typeFlag) =>
        Patched(tar, headerOffset, header => header[156] = (byte)typeFlag);

    /// <summary>The same tar with the header at <paramref name="headerOffset"/> changed and its checksum made to match again.</summary>
    public static byte[] Patched(byte[] tar, int headerOffset, Action<byte[]> change)
    {
        byte[] copy = (byte[])tar.Clone();
        byte[] block = copy[headerOffset..(headerOffset + 512)];
        change(block);
        block.CopyTo(copy, headerOffset);
        Span<byte> header = copy.AsSpan(headerOffset, 512);
        header.Slice(148, 8).Fill((byte)' ');
        int sum = 0;

        foreach (byte b in header)
        {
            sum += b;
        }

        Encoding.ASCII.GetBytes(Convert.ToString(sum, 8).PadLeft(6, '0')).CopyTo(header[148..]);
        header[154] = 0;
        header[155] = (byte)' ';
        return copy;
    }
}
