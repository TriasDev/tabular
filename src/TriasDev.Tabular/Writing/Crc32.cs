using System.Buffers.Binary;

namespace TriasDev.Tabular;

/// <summary>
/// The CRC-32 a zip entry carries (IEEE 802.3, reflected, polynomial 0xEDB88320).
/// </summary>
/// <remarks>
/// Our own, because <c>System.IO.Hashing</c> is a package and the library takes none. Slicing by
/// eight — eight bytes per step through eight tables — so that checksumming a sheet keeps up with
/// deflating it.
/// </remarks>
internal static class Crc32
{
    private static readonly uint[] Table = Build();

    /// <summary>The checksum of the data.</summary>
    public static uint Compute(ReadOnlySpan<byte> data) => Update(0, data);

    /// <summary>The checksum of what <paramref name="crc"/> covered followed by the data.</summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        uint[] t = Table;
        crc = ~crc;

        while (data.Length >= 8)
        {
            uint one = BinaryPrimitives.ReadUInt32LittleEndian(data) ^ crc;
            uint two = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);

            crc = t[(7 * 256) + (one & 0xFF)]
                ^ t[(6 * 256) + ((one >> 8) & 0xFF)]
                ^ t[(5 * 256) + ((one >> 16) & 0xFF)]
                ^ t[(4 * 256) + (one >> 24)]
                ^ t[(3 * 256) + (two & 0xFF)]
                ^ t[(2 * 256) + ((two >> 8) & 0xFF)]
                ^ t[256 + ((two >> 16) & 0xFF)]
                ^ t[two >> 24];

            data = data[8..];
        }

        foreach (byte b in data)
        {
            crc = t[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] Build()
    {
        uint[] table = new uint[8 * 256];

        for (uint i = 0; i < 256; i++)
        {
            uint c = i;

            for (int bit = 0; bit < 8; bit++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        for (int i = 0; i < 256; i++)
        {
            for (int slice = 1; slice < 8; slice++)
            {
                uint previous = table[((slice - 1) * 256) + i];
                table[(slice * 256) + i] = (previous >> 8) ^ table[previous & 0xFF];
            }
        }

        return table;
    }
}
