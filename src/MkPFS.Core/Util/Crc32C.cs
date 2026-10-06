using System.Buffers.Binary;
using System.Numerics;

namespace MkPFS.Core.Util;

/// <summary>
/// CRC-32C (Castagnoli, reflected polynomial 0x82F63B78). PS5 packages store one per 64 KiB block in
/// <c>playgo-chunk.crc</c>. Uses the SSE4.2 / ARMv8 CRC instructions when present.
/// </summary>
public static class Crc32C
{
    /// <summary>Continue a CRC over <paramref name="data"/>.</summary>
    /// <param name="crc">Running CRC (0 to start).</param>
    /// <param name="data">Next bytes.</param>
    /// <returns>Updated CRC.</returns>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        crc = ~crc;
        while (data.Length >= 8)
        {
            crc = BitOperations.Crc32C(crc, BinaryPrimitives.ReadUInt64LittleEndian(data));
            data = data[8..];
        }

        foreach (byte b in data)
        {
            crc = BitOperations.Crc32C(crc, b);
        }

        return ~crc;
    }
}
