namespace MkPFS.Core.AMPR;

/// <summary>
/// Constants of the AMPR seekable asset-pack format (ampr_emu <c>tools/ampr_pack_format.py</c>):
/// the <c>AMPRPAK4</c> manifest, <c>AMPRDAT3</c> data volumes, <c>AMPRCRC1</c> chunk CRC sidecar and
/// <c>AMPRCFG1</c> runtime settings. All integers are little endian.
/// </summary>
public static class AMPRPackFormat
{
    /// <summary>Manifest magic.</summary>
    public static ReadOnlySpan<byte> IndexMagic => "AMPRPAK4"u8;

    /// <summary>Data volume magic.</summary>
    public static ReadOnlySpan<byte> DataMagic => "AMPRDAT3"u8;

    /// <summary>Chunk CRC sidecar magic.</summary>
    public static ReadOnlySpan<byte> CrcMagic => "AMPRCRC1"u8;

    /// <summary>Runtime settings magic.</summary>
    public static ReadOnlySpan<byte> RuntimeMagic => "AMPRCFG1"u8;

    /// <summary>Manifest version.</summary>
    public const uint IndexVersion = 4;

    /// <summary>Data volume version.</summary>
    public const uint DataVersion = 3;

    /// <summary>Chunk CRC sidecar version.</summary>
    public const uint CrcVersion = 1;

    /// <summary>Runtime settings version.</summary>
    public const uint RuntimeVersion = 1;

    /// <summary>Manifest byte-order marker.</summary>
    public const uint EndianMarker = 0x01020304;

    /// <summary>Manifest header size (<c>&lt;8sIIII16sQQIIIIQQQQQIIQ</c>).</summary>
    public const int IndexHeaderSize = 128;

    /// <summary>Data volume header size (<c>&lt;8sIIII16sQQII</c>).</summary>
    public const int DataHeaderSize = 64;

    /// <summary>Chunk CRC sidecar header size (<c>&lt;8sII16sQII</c>).</summary>
    public const int CrcHeaderSize = 48;

    /// <summary>Runtime settings size (<c>&lt;8sII16sQQIIII</c>).</summary>
    public const int RuntimeSize = 64;

    /// <summary>File record size (<c>&lt;QQqIIIIIBBH</c>).</summary>
    public const int FileRecordSize = 48;

    /// <summary>Chunk record size (<c>&lt;QI</c>).</summary>
    public const int ChunkRecordSize = 12;

    /// <summary>Pack record size (<c>&lt;QQIIII</c>).</summary>
    public const int PackRecordSize = 32;

    /// <summary>Build id length.</summary>
    public const int BuildIdSize = 16;

    /// <summary>File is stored in pack volumes.</summary>
    public const uint FileFlagPacked = 1 << 0;

    /// <summary>File chunks are never compressed.</summary>
    public const uint FileFlagStoreOnly = 1 << 1;

    /// <summary>File uses the dense streaming layout.</summary>
    public const uint FileFlagStreaming = 1 << 2;

    /// <summary>File is admitted to the decoded cache immediately.</summary>
    public const uint FileFlagHot = 1 << 3;

    /// <summary>File uses the random-access layout.</summary>
    public const uint FileFlagRandomAccess = 1 << 4;

    /// <summary>Every defined file flag.</summary>
    public const uint FileKnownFlags =
        FileFlagPacked | FileFlagStoreOnly | FileFlagStreaming | FileFlagHot | FileFlagRandomAccess;

    /// <summary>Chunk stored uncompressed.</summary>
    public const int ChunkCodecRaw = 0;

    /// <summary>Chunk stored as a raw LZ4 block.</summary>
    public const int ChunkCodecLZ4 = 1;

    /// <summary>Chunk bytes are shared with an earlier chunk (deduplication).</summary>
    public const int ChunkFlagShared = 1 << 0;

    /// <summary>Chunk belongs to a streaming extent.</summary>
    public const int ChunkFlagStreaming = 1 << 1;

    /// <summary>Chunk lies inside one I/O page.</summary>
    public const int ChunkFlagPageContained = 1 << 2;

    /// <summary>Chunk starts on an I/O page boundary.</summary>
    public const int ChunkFlagPageAligned = 1 << 3;

    /// <summary>Every defined chunk flag.</summary>
    public const int ChunkKnownFlags =
        ChunkFlagShared | ChunkFlagStreaming | ChunkFlagPageContained | ChunkFlagPageAligned;

    /// <summary>Pack holds stripes of large files.</summary>
    public const uint PackFlagStriped = 1 << 0;

    /// <summary>Pack uses the page-aware layout (required).</summary>
    public const uint PackFlagIOPageLayout = 1 << 1;

    /// <summary>Every defined pack flag.</summary>
    public const uint PackKnownFlags = PackFlagStriped | PackFlagIOPageLayout;

    /// <summary>Every defined manifest flag (none).</summary>
    public const uint IndexKnownFlags = 0;

    /// <summary>Smallest block shift (16 KiB).</summary>
    public const int MinBlockShift = 14;

    /// <summary>Largest block shift (1 MiB).</summary>
    public const int MaxBlockShift = 20;

    /// <summary>Smallest I/O page shift (4 KiB).</summary>
    public const int MinIOPageShift = 12;

    /// <summary>Largest I/O page shift (1 MiB).</summary>
    public const int MaxIOPageShift = 20;

    /// <summary>Alignment of every chunk offset.</summary>
    public const int PhysicalChunkAlignment = 64;

    /// <summary>Chunk offsets use 48 bits.</summary>
    public const ulong ChunkOffsetMask = (1UL << 48) - 1;

    /// <summary>Bits of <c>stored_size - 1</c> in a chunk descriptor.</summary>
    public const int ChunkStoredBits = 20;

    /// <summary>Mask of <c>stored_size - 1</c>.</summary>
    public const uint ChunkStoredMask = (1U << ChunkStoredBits) - 1;

    /// <summary>Codec position in a chunk descriptor.</summary>
    public const int ChunkCodecShift = 20;

    /// <summary>Codec mask (after shifting).</summary>
    public const uint ChunkCodecMask = 0x3;

    /// <summary>Flags position in a chunk descriptor.</summary>
    public const int ChunkFlagsShift = 22;

    /// <summary>Flags mask (after shifting).</summary>
    public const uint ChunkFlagsMask = 0xFF;

    /// <summary>Every defined descriptor bit.</summary>
    public const uint ChunkDescriptorKnownMask =
        ChunkStoredMask | (ChunkCodecMask << ChunkCodecShift) | (ChunkFlagsMask << ChunkFlagsShift);

    /// <summary>Return <c>path + ".crc"</c> (Python <c>chunk_crc_path</c>).</summary>
    /// <param name="indexPath">Manifest path.</param>
    /// <returns>Sidecar path.</returns>
    public static string ChunkCrcPath(string indexPath) => indexPath + ".crc";

    /// <summary>Return <c>path + ".runtime"</c>.</summary>
    /// <param name="indexPath">Manifest path.</param>
    /// <returns>Runtime settings path.</returns>
    public static string RuntimePath(string indexPath) => indexPath + ".runtime";

    /// <summary>Round <paramref name="value"/> up to a power-of-two <paramref name="alignment"/>.</summary>
    /// <param name="value">Value.</param>
    /// <param name="alignment">Power of two.</param>
    /// <returns>Aligned value.</returns>
    public static ulong AlignUp(ulong value, ulong alignment)
    {
        CheckAlignment(alignment);
        return checked(value + alignment - 1) & ~(alignment - 1);
    }

    /// <summary>Round <paramref name="value"/> down to a power-of-two <paramref name="alignment"/>.</summary>
    /// <param name="value">Value.</param>
    /// <param name="alignment">Power of two.</param>
    /// <returns>Aligned value.</returns>
    public static ulong AlignDown(ulong value, ulong alignment)
    {
        CheckAlignment(alignment);
        return value & ~(alignment - 1);
    }

    private static void CheckAlignment(ulong alignment)
    {
        if (alignment == 0 || (alignment & (alignment - 1)) != 0)
        {
            throw new ArgumentException($"alignment must be a power of two, got {alignment}");
        }
    }
}
