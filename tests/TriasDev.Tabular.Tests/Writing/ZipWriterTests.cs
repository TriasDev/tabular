using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>
/// The zip the workbook writers stream: what any zip reader opens, laid out as LibreOffice and Excel
/// need it.
/// </summary>
public sealed class ZipWriterTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<byte[]> Build(Action<ZipWriter> write, long zip64Threshold = uint.MaxValue)
    {
        using SpillBuffer buffer = new();
        ZipWriter zip = new(buffer, CompressionLevel.Fastest, zip64Threshold);
        write(zip);

        using MemoryStream target = new();
        await buffer.DrainToAsync(target, Token);
        return target.ToArray();
    }

    private static Dictionary<string, byte[]> Read(byte[] file)
    {
        using ZipArchive archive = new(new MemoryStream(file, writable: false), ZipArchiveMode.Read);
        Dictionary<string, byte[]> entries = [];

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            using Stream stream = entry.Open();
            using MemoryStream content = new();
            stream.CopyTo(content);
            entries[entry.FullName] = content.ToArray();
        }

        return entries;
    }

    private static void Deflated(ZipWriter zip, string name, byte[] content)
    {
        Stream entry = zip.BeginDeflated(name);
        entry.Write(content);
        zip.EndEntry();
    }

    [Fact]
    public async Task WritesEntriesAnyZipReaderOpens()
    {
        byte[] large = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 50_000).Select(i => $"<row r=\"{i}\"/>")));

        byte[] file = await Build(zip =>
        {
            zip.AddStored("mimetype", "application/vnd.oasis.opendocument.spreadsheet"u8);
            Deflated(zip, "content.xml", large);
            zip.AddStored("META-INF/manifest.xml", "<manifest/>"u8);
            zip.Complete();
        });

        Dictionary<string, byte[]> entries = Read(file);

        Assert.Equal(["mimetype", "content.xml", "META-INF/manifest.xml"], entries.Keys);
        Assert.Equal("application/vnd.oasis.opendocument.spreadsheet"u8.ToArray(), entries["mimetype"]);
        Assert.Equal(large, entries["content.xml"]);
        Assert.Equal("<manifest/>"u8.ToArray(), entries["META-INF/manifest.xml"]);
        Assert.True(file.Length < large.Length / 2, $"{file.Length} bytes for {large.Length}");
    }

    [Fact]
    public async Task StoresASmallPartWithItsSizesUpFrontAndNoDescriptor()
    {
        byte[] file = await Build(zip =>
        {
            zip.AddStored("mimetype", "application/vnd.oasis.opendocument.spreadsheet"u8);
            zip.Complete();
        });

        // The ODF rule a stored mimetype must meet: name at 30, content at 38, no descriptor flag.
        Assert.Equal(0x04034b50u, BinaryPrimitives.ReadUInt32LittleEndian(file));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(6)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(8)));
        Assert.Equal(Crc32.Compute("application/vnd.oasis.opendocument.spreadsheet"u8), BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(14)));
        Assert.Equal(46u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(18)));
        Assert.Equal(46u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(22)));
        Assert.Equal("mimetype", Encoding.ASCII.GetString(file, 30, 8));
        Assert.Equal("application/vnd.oasis.opendocument.spreadsheet", Encoding.ASCII.GetString(file, 38, 46));
    }

    [Fact]
    public async Task StreamsALargePartWithADataDescriptor()
    {
        byte[] content = Encoding.ASCII.GetBytes(new string('x', 10_000));

        byte[] file = await Build(zip =>
        {
            Deflated(zip, "sheet.xml", content);
            zip.Complete();
        });

        Assert.Equal(0x0008, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(6)) & 0x0008);
        Assert.Equal(8, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(8)));
        Assert.Equal(content, Read(file)["sheet.xml"]);

        int descriptor = file.AsSpan().IndexOf((ReadOnlySpan<byte>)[0x50, 0x4B, 0x07, 0x08]);
        Assert.True(descriptor > 0);
        Assert.Equal(Crc32.Compute(content), BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(descriptor + 4)));
        Assert.Equal((uint)content.Length, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(descriptor + 12)));
    }

    [Fact]
    public async Task WritesZip64StructuresPastTheThreshold()
    {
        byte[] content = new byte[5_000];
        new Random(64).NextBytes(content);

        byte[] file = await Build(
            zip =>
            {
                zip.AddStored("small", "tiny"u8);
                Deflated(zip, "big", content);
                zip.Complete();
            },
            zip64Threshold: 100);

        Assert.True(file.AsSpan().IndexOf((ReadOnlySpan<byte>)[0x50, 0x4B, 0x06, 0x06]) > 0, "a zip64 end of central directory record");
        Assert.True(file.AsSpan().IndexOf((ReadOnlySpan<byte>)[0x50, 0x4B, 0x06, 0x07]) > 0, "a zip64 end of central directory locator");

        // The "big" entry's descriptor is 24 bytes: signature, CRC, then two 8-byte sizes. The central
        // directory follows it at once.
        ReadOnlySpan<byte> bytes = file;
        int descriptor = bytes.IndexOf((ReadOnlySpan<byte>)[0x50, 0x4B, 0x07, 0x08]);
        Assert.True(descriptor > 0, "a data descriptor");
        Assert.Equal(Crc32.Compute(content), BinaryPrimitives.ReadUInt32LittleEndian(bytes[(descriptor + 4)..]));
        long compressed = BinaryPrimitives.ReadInt64LittleEndian(bytes[(descriptor + 8)..]);
        Assert.True(compressed > 0);
        Assert.Equal(content.Length, BinaryPrimitives.ReadInt64LittleEndian(bytes[(descriptor + 16)..]));
        Assert.Equal(0x02014B50u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[(descriptor + 24)..]));

        Dictionary<string, byte[]> entries = Read(file);
        Assert.Equal("tiny"u8.ToArray(), entries["small"]);
        Assert.Equal(content, entries["big"]);
    }

    [Fact]
    public async Task IsReproducibleByteForByte()
    {
        static void Write(ZipWriter zip)
        {
            zip.AddStored("a", "1"u8);
            Deflated(zip, "b", Encoding.ASCII.GetBytes("<x/>"));
            zip.Complete();
        }

        Assert.Equal(await Build(Write), await Build(Write));
    }

    [Fact]
    public void RefusesAnotherEntryWhileOneIsOpen()
    {
        using SpillBuffer buffer = new();
        ZipWriter zip = new(buffer, CompressionLevel.Fastest);
        zip.BeginDeflated("open");

        Assert.Throws<InvalidOperationException>(() => zip.AddStored("other", "x"u8));
        Assert.Throws<InvalidOperationException>(() => zip.BeginDeflated("other"));
        Assert.Throws<InvalidOperationException>(() => zip.Complete());
    }
}
