namespace MkPFS.Core.Util;

/// <summary>CRC-32 (IEEE 802.3, same as zlib <c>crc32</c> / Python <c>zlib.crc32</c>).</summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    /// <summary>Continue a CRC over <paramref name="data"/>.</summary>
    /// <param name="crc">Running CRC (0 to start).</param>
    /// <param name="data">Next bytes.</param>
    /// <returns>Updated CRC.</returns>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        crc = ~crc;
        foreach (byte b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] BuildTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
