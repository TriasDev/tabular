using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

using TriasDev.Tabular.Archive;

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
        Assert.Equal(Crc32.Append(0, "application/vnd.oasis.opendocument.spreadsheet"u8), BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(14)));
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
        Assert.Equal(Crc32.Append(0, content), BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(descriptor + 4)));
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
        Assert.Equal(Crc32.Append(0, content), BinaryPrimitives.ReadUInt32LittleEndian(bytes[(descriptor + 4)..]));
        long compressed = BinaryPrimitives.ReadInt64LittleEndian(bytes[(descriptor + 8)..]);
        Assert.True(compressed > 0);
        Assert.Equal(content.Length, BinaryPrimitives.ReadInt64LittleEndian(bytes[(descriptor + 16)..]));
        Assert.Equal(0x02014B50u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[(descriptor + 24)..]));

        Dictionary<string, byte[]> entries = Read(file);
        Assert.Equal("tiny"u8.ToArray(), entries["small"]);
        Assert.Equal(content, entries["big"]);
    }

    /// <summary>The offsets of every record with the signature, in file order.</summary>
    private static List<int> Records(byte[] file, uint signature)
    {
        Span<byte> pattern = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(pattern, signature);
        List<int> found = [];

        for (int at = file.AsSpan().IndexOf(pattern); at >= 0; at = file.AsSpan(at + 1).IndexOf(pattern) is var next and >= 0 ? at + 1 + next : -1)
        {
            found.Add(at);
        }

        return found;
    }

    private const uint CentralHeader = 0x02014B50;
    private const uint Descriptor = 0x08074B50;
    private const uint EndOfDirectory = 0x06054B50;

    [Fact]
    public async Task WritesALargeEntryThatCompressesBelowTheThresholdAsZip64()
    {
        // Its size, not its compressed size, decides: 100,000 zeros deflate to a few hundred bytes.
        byte[] content = new byte[100_000];
        byte[] file = await Build(zip => { Deflated(zip, "big", content); zip.Complete(); }, zip64Threshold: 1_000);

        int descriptor = Assert.Single(Records(file, Descriptor));
        long compressed = BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(descriptor + 8));
        Assert.True(compressed is > 0 and < 1_000);
        Assert.Equal(content.Length, BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(descriptor + 16)));
        Assert.Equal(CentralHeader, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(descriptor + 24)));

        int central = Assert.Single(Records(file, CentralHeader));
        Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(central + 20)));
        Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(central + 24)));
        Assert.True(BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(central + 30)) > 0, "a zip64 extra field");
        Assert.Equal(content, Read(file)["big"]);
    }

    [Fact]
    public async Task StatesTheCompressedSizeInTheDescriptorAsTheDirectoryDoes()
    {
        byte[] content = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 5_000).Select(i => $"<row r=\"{i}\"/>")));
        byte[] file = await Build(zip => { Deflated(zip, "sheet.xml", content); zip.Complete(); });

        int descriptor = Assert.Single(Records(file, Descriptor));
        int central = Assert.Single(Records(file, CentralHeader));
        int data = 30 + BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(26)) + BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(28));
        uint compressed = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(descriptor + 8));

        Assert.Equal((uint)(descriptor - data), compressed);
        Assert.Equal(compressed, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(central + 20)));
        Assert.Equal((uint)content.Length, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(central + 24)));
    }

    [Fact]
    public async Task FlagsEveryEntryInTheDirectoryAsItsLocalHeaderDoes()
    {
        // The UTF-8 flag only on a name that needs it; the descriptor flag only on a deflated entry.
        byte[] file = await Build(zip =>
        {
            zip.AddStored("mimetype", "application/vnd.oasis.opendocument.spreadsheet"u8);
            zip.AddStored("größe.txt", "1"u8);
            Deflated(zip, "Übersicht.xml", Encoding.UTF8.GetBytes("<x/>"));
            Deflated(zip, "content.xml", Encoding.UTF8.GetBytes("<x/>"));
            zip.Complete();
        });

        List<int> local = Records(file, 0x04034B50);
        List<int> central = Records(file, CentralHeader);
        ushort[] expected = [0x0000, 0x0800, 0x0808, 0x0008];

        Assert.Equal(expected, local.Select(at => BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at + 6))));
        Assert.Equal(expected, central.Select(at => BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at + 8))));
        Assert.Equal(["mimetype", "größe.txt", "Übersicht.xml", "content.xml"], Read(file).Keys);
    }

    [Fact]
    public async Task KeepsASmallFilePlainZip()
    {
        byte[] file = await Build(zip =>
        {
            zip.AddStored("a", "1"u8);
            Deflated(zip, "b", Encoding.ASCII.GetBytes("<x/>"));
            zip.Complete();
        });

        Assert.Empty(Records(file, 0x06064B50));
        Assert.Empty(Records(file, 0x07064B50));
        Assert.All(Records(file, CentralHeader), at => Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at + 30))));

        int end = Assert.Single(Records(file, EndOfDirectory));
        int firstCentral = Records(file, CentralHeader)[0];
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(end + 8)));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(end + 10)));
        Assert.Equal((uint)(end - firstCentral), BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(end + 12)));
        Assert.Equal((uint)firstCentral, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(end + 16)));
    }

    [Fact]
    public async Task StatesAnEntryOffsetPastTheThresholdInZip64()
    {
        // b's local header starts at 30 + 1 + 100 = 131, past the threshold of 125.
        byte[] file = await Build(
            zip =>
            {
                zip.AddStored("a", new byte[100]);
                zip.AddStored("b", "2"u8);
                zip.Complete();
            },
            zip64Threshold: 125);

        List<int> central = Records(file, CentralHeader);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(central[0] + 42)));
        Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(central[1] + 42)));
        Assert.True(BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(central[1] + 30)) > 0, "a zip64 extra field with the offset");
        Assert.NotEmpty(Records(file, 0x06064B50));
        Assert.NotEmpty(Records(file, 0x07064B50));

        Dictionary<string, byte[]> entries = Read(file);
        Assert.Equal(new byte[100], entries["a"]);
        Assert.Equal("2"u8.ToArray(), entries["b"]);
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

    [Theory]
    [InlineData("Grüße.csv")]
    [InlineData("Данные.csv")]
    [InlineData("数据.csv")]
    public async Task AnyZipReaderReadsANonAsciiEntryName(string name)
    {
        byte[] zip = await Build(writer =>
        {
            using (Stream entry = writer.BeginDeflated(name))
            {
                entry.Write("a,b\r\n"u8);
            }

            writer.EndEntry();
            writer.AddStored("stored-" + name, "x"u8);
            writer.Complete();
        });

        using ZipArchive archive = new(new MemoryStream(zip, writable: false), ZipArchiveMode.Read);
        Assert.Equal([name, "stored-" + name], archive.Entries.Select(e => e.FullName));
        Assert.Equal(0x0800, BitConverter.ToUInt16(zip, 6) & 0x0800);       // the first local header's flags
    }

    [Fact]
    public async Task AnAsciiNameLeavesTheFlagsAsTheyWere()
    {
        byte[] zip = await Build(writer =>
        {
            writer.AddStored("mimetype", "x"u8);
            writer.Complete();
        });

        Assert.Equal(0, BitConverter.ToUInt16(zip, 6));
    }
}
