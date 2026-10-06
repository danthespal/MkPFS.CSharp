using System.Buffers.Binary;
using System.Text;

namespace MkPFS.Core.PFS.PS5;

/// <summary>
/// The PS5 <c>\x7fFLT</c> flat-path-table hash: a three-lane reduced-Keccak round over 64-bit words with
/// two fixed seeds (LibProsperoPkg <c>ProsperoPs5FlatPathTable.HashBytes</c> and
/// <c>ProsperoOuterPfsBuilder.FltPathHash</c>, which compute the same function).
/// </summary>
public static class PS5PathHash
{
    /// <summary>First seed; also stored little-endian at FLT header +0x30.</summary>
    public const ulong Seed0 = 0x92CA8AAB26A24F51UL;

    /// <summary>Second seed; also stored little-endian at FLT header +0x38.</summary>
    public const ulong Seed1 = 0x09BBB761A41BC44DUL;

    private const ulong RoundConstant = 0x8000000080008081UL;

    /// <summary>Hash exact bytes.</summary>
    /// <param name="data">Name bytes (already normalized).</param>
    /// <returns>64-bit hash.</returns>
    public static ulong HashBytes(ReadOnlySpan<byte> data)
    {
        ulong a = Seed0;
        ulong b = ulong.RotateLeft(Seed0, 11);
        ulong c = ulong.RotateLeft(Seed0, 23);
        ulong tail = 0;
        if (data.Length != 0)
        {
            // All words but the last (1..8 bytes) go through the round; the last one is folded in below.
            int words = (data.Length - 1) >> 3;
            for (int i = 0; i < words; i++)
            {
                a ^= BinaryPrimitives.ReadUInt64LittleEndian(data[(i * 8)..]);
                ulong t18 = ulong.RotateRight(ulong.RotateLeft(c ^ b, 5) ^ a, 11);
                ulong t12 = ulong.RotateLeft(ulong.RotateLeft(c ^ a, 17) ^ b, 11);
                ulong c2 = ulong.RotateRight(ulong.RotateLeft(b ^ a, 1) ^ c, 5);
                a = (~t12 & c2) ^ t18 ^ RoundConstant;
                b = (~c2 & t18) ^ t12;
                c = (~t18 & t12) ^ c2;
            }

            Span<byte> last = stackalloc byte[8];
            last.Clear();
            data[(words * 8)..].CopyTo(last);
            tail = BinaryPrimitives.ReadUInt64LittleEndian(last);
        }

        ulong x6 = tail ^ a ^ Seed1;
        ulong u17 = ulong.RotateLeft(c ^ b, 5) ^ x6;
        ulong u18 = ulong.RotateLeft(c ^ x6, 17) ^ b;
        ulong x11 = ulong.RotateLeft(b ^ x6, 1) ^ c;
        return (~ulong.RotateLeft(u18, 11) & ulong.RotateRight(x11, 5)) ^ ulong.RotateRight(u17, 11) ^ RoundConstant;
    }

    /// <summary>
    /// Hash an outer-image file name: <c>a</c>-<c>z</c> are upper-cased and every UTF-16 unit is truncated
    /// to a byte (<c>FltPathHash</c>). Outer names are fixed ASCII (<c>pfs_image.dat</c>, ...).
    /// </summary>
    /// <param name="name">File name.</param>
    /// <returns>64-bit hash.</returns>
    public static ulong HashName(string name)
    {
        byte[] bytes = new byte[name.Length];
        for (int i = 0; i < name.Length; i++)
        {
            char ch = name[i];
            bytes[i] = (byte)(ch is >= 'a' and <= 'z' ? ch - 32 : ch);
        }

        return HashBytes(bytes);
    }

    /// <summary>
    /// Hash an inner-image path: one leading <c>/</c> is removed, the rest is upper-cased with
    /// <see cref="string.ToUpperInvariant"/> and encoded as ASCII, so every non-ASCII character becomes
    /// <c>?</c> (<c>ProsperoPs5FlatPathTable.HashPath</c>).
    /// </summary>
    /// <param name="path">Path from the user root, for example <c>/sce_sys/keystone</c>.</param>
    /// <returns>64-bit hash.</returns>
    public static ulong HashPath(string path)
    {
        if (path.StartsWith('/'))
        {
            path = path[1..];
        }

        return HashBytes(Encoding.ASCII.GetBytes(path.ToUpperInvariant()));
    }
}
