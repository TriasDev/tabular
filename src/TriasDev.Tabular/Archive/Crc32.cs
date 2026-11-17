using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

using ArmAes = System.Runtime.Intrinsics.Arm.Aes;
using ArmCrc32 = System.Runtime.Intrinsics.Arm.Crc32;

namespace TriasDev.Tabular.Archive;

/// <summary>The CRC-32 gzip and zip record (IEEE 802.3, reflected, polynomial 0xEDB88320).</summary>
/// <remarks>
/// Ours because the base class library has no public one — <c>System.IO.Hashing</c> is a package,
/// and this library takes none. Reading a zip checks every entry against its CRC, so this runs over
/// every byte of a workbook and its speed is the reading's. Runs of 64 bytes or more are folded
/// sixty-four bytes a step with carry-less multiplication — PCLMULQDQ on x86, PMULL on ARM64, the
/// method of Intel's "Fast CRC Computation for Generic Polynomials Using PCLMULQDQ" as zlib and
/// System.IO.Hashing use it. Shorter runs take the ARM64 CRC32 instruction, which computes this
/// polynomial (x86's computes another), or the eight-table loop. Measured on 106 MB on an Apple M
/// CPU: folding 5 ms, the CRC32 instruction 13 ms, the tables 68 ms.
/// </remarks>
internal static class Crc32
{
    private const uint Polynomial = 0xEDB88320u;

    private static readonly uint[] Table = BuildTable();

    /// <summary>Extends a finished CRC — 0 for nothing yet — by more data.</summary>
    public static uint Append(uint crc, ReadOnlySpan<byte> data) =>
        ~(CanFold && data.Length >= FoldingMinimum ? UpdateFolding(~crc, data) : UpdateShort(~crc, data));

    /// <summary>A run too short to fold: the ARM64 instruction where there is one, else the tables.</summary>
    private static uint UpdateShort(uint state, ReadOnlySpan<byte> data) =>
        ArmCrc32.Arm64.IsSupported ? UpdateArm64(state, data) : UpdateManaged(state, data);

    /// <summary>The shortest run the folding takes: four 16-byte lanes.</summary>
    internal const int FoldingMinimum = 64;

    /// <summary>Whether this CPU can fold: PCLMULQDQ on x86, PMULL on ARM64.</summary>
    internal static bool CanFold => Pclmulqdq.IsSupported || ArmAes.Arm64.IsSupported;

    /// <summary>
    /// Advances the raw register by at least <see cref="FoldingMinimum"/> bytes, folding four lanes of
    /// sixteen bytes at a time, then one, then reducing to 32 bits; the last fewer than sixteen bytes
    /// go the short way. Only where <see cref="CanFold"/>.
    /// </summary>
    /// <remarks>
    /// The constants are x^(4*128+64) mod P, x^(4*128) mod P (k1, k2), x^(128+64) mod P, x^128 mod P
    /// (k3, k4), x^64 mod P (k5), and for the Barrett reduction P itself and floor(x^64 / P), all
    /// bit-reflected — the ones zlib's crc32_simd uses for this polynomial.
    /// </remarks>
    internal static uint UpdateFolding(uint state, ReadOnlySpan<byte> data)
    {
        Vector128<ulong> k1k2 = Vector128.Create(0x0154442bd4UL, 0x01c6e41596UL);
        Vector128<ulong> k3k4 = Vector128.Create(0x01751997d0UL, 0x00ccaa009eUL);
        Vector128<ulong> k5k0 = Vector128.Create(0x0163cd6124UL, 0UL);
        Vector128<ulong> poly = Vector128.Create(0x01db710641UL, 0x01f7011641UL);
        Vector128<ulong> low32 = Vector128.Create(uint.MaxValue, 0u, uint.MaxValue, 0u).AsUInt64();

        Vector128<ulong> x1 = Load(data, 0) ^ Vector128.Create((ulong)state, 0UL);
        Vector128<ulong> x2 = Load(data, 16);
        Vector128<ulong> x3 = Load(data, 32);
        Vector128<ulong> x4 = Load(data, 48);
        data = data[64..];

        while (data.Length >= 64)
        {
            x1 = Fold(x1, k1k2, Load(data, 0));
            x2 = Fold(x2, k1k2, Load(data, 16));
            x3 = Fold(x3, k1k2, Load(data, 32));
            x4 = Fold(x4, k1k2, Load(data, 48));
            data = data[64..];
        }

        x1 = Fold(x1, k3k4, x2);
        x1 = Fold(x1, k3k4, x3);
        x1 = Fold(x1, k3k4, x4);

        while (data.Length >= 16)
        {
            x1 = Fold(x1, k3k4, Load(data, 0));
            data = data[16..];
        }

        // 128 bits to 64, then Barrett's reduction to the 32-bit register.
        x1 = ShiftRightBytes(x1, 8) ^ CarrylessMultiply(x1, k3k4, 0x10);
        x1 = CarrylessMultiply(x1 & low32, k5k0, 0x00) ^ ShiftRightBytes(x1, 4);
        Vector128<ulong> t = CarrylessMultiply(x1 & low32, poly, 0x10) & low32;
        x1 ^= CarrylessMultiply(t, poly, 0x00);

        return UpdateShort(x1.AsUInt32().GetElement(1), data);
    }

    private static Vector128<ulong> Load(ReadOnlySpan<byte> data, int offset) =>
        Vector128.Create(data.Slice(offset, 16)).AsUInt64();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ulong> Fold(Vector128<ulong> x, Vector128<ulong> k, Vector128<ulong> next) =>
        CarrylessMultiply(x, k, 0x00) ^ CarrylessMultiply(x, k, 0x11) ^ next;

    /// <summary>The 128-bit lane shifted right by whole bytes, zeros shifted in.</summary>
    private static Vector128<ulong> ShiftRightBytes(Vector128<ulong> x, byte bytes) =>
        Vector128.Shuffle(x.AsByte(), Vector128.Create((byte)0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15) + Vector128.Create(bytes)).AsUInt64();

    /// <summary>
    /// PCLMULQDQ's product of one 64-bit half of each: bit 0 of <paramref name="halves"/> picks the
    /// left's, bit 4 the right's; on ARM64 the same product by PMULL.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ulong> CarrylessMultiply(Vector128<ulong> left, Vector128<ulong> right, [ConstantExpected] byte halves)
    {
        if (Pclmulqdq.IsSupported)
        {
            return Pclmulqdq.CarrylessMultiply(left, right, halves);
        }

        Vector64<ulong> a = (halves & 0x01) == 0 ? left.GetLower() : left.GetUpper();
        Vector64<ulong> b = (halves & 0x10) == 0 ? right.GetLower() : right.GetUpper();
        return ArmAes.PolynomialMultiplyWideningLower(a, b);
    }

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
