using System.Buffers.Binary;

using ArmCrc32 = System.Runtime.Intrinsics.Arm.Crc32;

namespace TriasDev.Tabular.Archive;

/// <summary>The CRC-32 gzip and zip record (IEEE 802.3, reflected, polynomial 0xEDB88320).</summary>
/// <remarks>
/// Ours because the base class library has no public one — <c>System.IO.Hashing</c> is a package,
/// and this library takes none. Table-driven, eight bytes a step, in managed code; the ARM64 CRC32
/// instruction computes the same polynomial and is used where the CPU has it. x86 has a CRC32
/// instruction too, but for another polynomial.
/// </remarks>
internal static class Crc32
{
    private const uint Polynomial = 0xEDB88320u;

    private static readonly uint[] Table = BuildTable();

    /// <summary>Extends a finished CRC — 0 for nothing yet — by more data.</summary>
    public static uint Append(uint crc, ReadOnlySpan<byte> data) =>
        ~(ArmCrc32.Arm64.IsSupported ? UpdateArm64(~crc, data) : UpdateManaged(~crc, data));

    /// <summary>Advances the raw register by the data, eight bytes a step through eight tables.</summary>
    internal static uint UpdateManaged(uint state, ReadOnlySpan<byte> data)
    {
        uint[] t = Table;

        while (data.Length >= 8)
        {
            uint one = BinaryPrimitives.ReadUInt32LittleEndian(data) ^ state;
            uint two = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
            state = t[(7 * 256) + (one & 0xFF)] ^ t[(6 * 256) + ((one >> 8) & 0xFF)]
                ^ t[(5 * 256) + ((one >> 16) & 0xFF)] ^ t[(4 * 256) + (one >> 24)]
                ^ t[(3 * 256) + (two & 0xFF)] ^ t[(2 * 256) + ((two >> 8) & 0xFF)]
                ^ t[256 + ((two >> 16) & 0xFF)] ^ t[two >> 24];
            data = data[8..];
        }

        foreach (byte b in data)
        {
            state = t[(state ^ b) & 0xFF] ^ (state >> 8);
        }

        return state;
    }

    /// <summary>Advances the raw register with the ARM64 instruction; only where it is supported.</summary>
    internal static uint UpdateArm64(uint state, ReadOnlySpan<byte> data)
    {
        while (data.Length >= 8)
        {
            state = ArmCrc32.Arm64.ComputeCrc32(state, BinaryPrimitives.ReadUInt64LittleEndian(data));
            data = data[8..];
        }

        foreach (byte b in data)
        {
            state = ArmCrc32.ComputeCrc32(state, b);
        }

        return state;
    }

    private static uint[] BuildTable()
    {
        uint[] table = new uint[8 * 256];

        for (uint n = 0; n < 256; n++)
        {
            uint c = n;

            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? Polynomial ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        // Table k holds the effect of a byte followed by k zero bytes, so eight bytes fold at once.
        for (int n = 0; n < 256; n++)
        {
            for (int k = 1; k < 8; k++)
            {
                uint previous = table[((k - 1) * 256) + n];
                table[(k * 256) + n] = (previous >> 8) ^ table[previous & 0xFF];
            }
        }

        return table;
    }
}
