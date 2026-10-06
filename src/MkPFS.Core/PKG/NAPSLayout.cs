using System.Buffers.Binary;

namespace MkPFS.Core.PKG;

/// <summary>
/// One 9-byte CblockInfo record of <c>naps_pkg_layout.dat</c> (72-bit little-endian value).
/// A run-base record (bit 18 set) re-anchors the on-disk cursor; a standard record describes one chunk
/// of at most 256 KiB of the inner mount.
/// </summary>
/// <param name="Raw">Low 64 bits.</param>
/// <param name="High">Bits 64..71.</param>
public readonly record struct NAPSCblock(ulong Raw, byte High)
{
    /// <summary>Run-base record.</summary>
    public bool IsRunBase => ((Raw >> 18) & 1) != 0;

    /// <summary>Low 18 bits: compressed cursor modulo 256 KiB (start for a chunk, end of the previous run for a base).</summary>
    public uint CoffsetMod256K => (uint)(Raw & 0x3FFFF);

    /// <summary>Chunk: 2 × (logical start modulo 256 KiB), 19 bits (1 on the terminator).</summary>
    public uint UoffsetStart => (uint)((Raw >> 19) & 0x7FFFF);

    /// <summary>
    /// Chunk: 16-bit length field. Kraken: first 128 KiB sub-chunk stored length − 1. Raw: length − 1
    /// up to 64 KiB, length − 0x10001 up to 128 KiB, 0xFFFF above.
    /// </summary>
    public uint EvenLengthMinus1 => (uint)((Raw >> 38) & 0xFFFF);

    /// <summary>Chunk: even flag.</summary>
    public bool Even => ((Raw >> 54) & 1) != 0;

    /// <summary>Chunk: odd flag.</summary>
    public bool Odd => ((Raw >> 55) & 1) != 0;

    /// <summary>Chunk: block kind (0 raw ≤ 128 KiB, 2/3 Kraken, 4 zero fill or raw over 128 KiB).</summary>
    public int Kde => (int)((Raw >> 56) & 0x7);

    /// <summary>
    /// Chunk: kind of the second 128 KiB Kraken sub-chunk (0 none, 2 raw literals, 3 sub literals), like
    /// <see cref="Kde"/> for the first. Publishing Tools output has no shuffle patterns; LibProsperoPkg
    /// reads these bits as a shuffle index.
    /// </summary>
    public int SecondKde => (int)((Raw >> 59) & 0x7);

    /// <summary>Run base: on-disk position in 32 KiB units.</summary>
    public uint TweakIndex => (uint)((Raw >> 19) & 0xFFFFFFF);

    /// <summary>Run base: key-table slot.</summary>
    public int KeyIndex => (int)((Raw >> 47) & 0x3);

    /// <summary>Run base: 2 × (on-disk position / 256 KiB).</summary>
    public uint CoffsetStart256K => (uint)(((Raw >> 49) & 0x7FFF) | ((ulong)(High & 0x1FF) << 15));
}

/// <summary>
/// <c>naps_pkg_layout.dat</c>: the map from the inner mount (logical) to the stored, compressed
/// <c>pfs_image.dat</c> (on disk). Layout verified against Publishing Tools output: a 16-byte header,
/// 8-byte outer-block and shuffle records, 6-byte fidx records and 10-byte u2c records packed back to back,
/// then the 9-byte CblockInfo records starting at an 8-byte boundary.
/// </summary>
public sealed class NAPSLayout
{
    /// <summary>Name of the outer-PFS file.</summary>
    public const string FileName = "naps_pkg_layout.dat";

    /// <summary>Uncompressed block (ublock) size.</summary>
    public const int UBlockSize = 0x40000;

    /// <summary>fidx count (files plus the data-end, metadata-base and mount-end markers).</summary>
    public required int FileOffsetCount { get; init; }

    /// <summary>Compression type (2 = Kraken).</summary>
    public required int CompressionType { get; init; }

    /// <summary>Key count.</summary>
    public required int KeyCount { get; init; }

    /// <summary>Ublock count (ceil(mount size / 256 KiB)).</summary>
    public required int UBlockCount { get; init; }

    /// <summary>Outer-block records (8 bytes each).</summary>
    public required IReadOnlyList<byte[]> OuterBlocks { get; init; }

    /// <summary>Shuffle patterns (8 bytes each).</summary>
    public required IReadOnlyList<byte[]> ShufflePatterns { get; init; }

    /// <summary>fidx: logical start of each file, then data end, metadata base and mount end.</summary>
    public required IReadOnlyList<long> FileOffsets { get; init; }

    /// <summary>fidx type bytes (0x40 on the mount-end record).</summary>
    public required IReadOnlyList<byte> FileOffsetTypes { get; init; }

    /// <summary>Per ublock: index of the first standard cblock whose logical start is at or after the ublock start.</summary>
    public required IReadOnlyList<int> FirstCblockByUBlock { get; init; }

    /// <summary>CblockInfo records.</summary>
    public required IReadOnlyList<NAPSCblock> Cblocks { get; init; }

    /// <summary>Logical mount size (last fidx record).</summary>
    public long MountSize => FileOffsets[^1];

    /// <summary>Logical offset of the inner metadata (superblock).</summary>
    public long MetadataBase => FileOffsets[^2];

    /// <summary>Parse a layout.</summary>
    /// <param name="data">File bytes (may carry trailing zero padding).</param>
    /// <returns>Layout.</returns>
    /// <exception cref="InvalidDataException">Malformed layout.</exception>
    public static NAPSLayout Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16)
        {
            throw new InvalidDataException("naps layout is shorter than its header");
        }

        ulong w0 = BinaryPrimitives.ReadUInt64LittleEndian(data);
        ulong w1 = BinaryPrimitives.ReadUInt64LittleEndian(data[8..]);
        int fileCount = (int)(w0 & 0xFFFFFF) + 1;
        int compression = (int)((w0 >> 24) & 0x3);
        int keys = (int)((w0 >> 26) & 0x3) + 1;
        int shuffles = (int)((w0 >> 28) & 0xF);
        int ublocks = (int)((w0 >> 32) & 0xFFFFFF);
        int outer = (int)(w1 & 0xFFFFFF);
        int cblocks = (int)((w1 >> 24) & 0xFFFFFF) + 2;
        int u2cGroups = (ublocks + 8) >> 3;

        long p = 16;
        long outerAt = p;
        p += (long)outer * 8;
        long shuffleAt = p;
        p += (long)shuffles * 8;
        long fidxAt = p;
        p += (long)fileCount * 6;
        long u2cAt = p;
        p += (long)u2cGroups * 10;
        long cblockAt = (p + 7) & ~7L;
        long end = cblockAt + ((long)cblocks * 9);
        if (end > data.Length)
        {
            throw new InvalidDataException($"naps layout needs {end} bytes but has {data.Length}");
        }

        List<byte[]> outerRecords = [];
        for (int i = 0; i < outer; i++)
        {
            outerRecords.Add(data.Slice((int)outerAt + (i * 8), 8).ToArray());
        }

        List<byte[]> shuffleRecords = [];
        for (int i = 0; i < shuffles; i++)
        {
            shuffleRecords.Add(data.Slice((int)shuffleAt + (i * 8), 8).ToArray());
        }

        // fidx: 40-bit little-endian offset + type byte.
        List<long> offsets = [];
        List<byte> types = [];
        for (int i = 0; i < fileCount; i++)
        {
            ReadOnlySpan<byte> e = data.Slice((int)fidxAt + (i * 6), 6);
            long value = 0;
            for (int k = 0; k < 5; k++)
            {
                value |= (long)e[k] << (8 * k);
            }

            offsets.Add(value);
            types.Add(e[5]);
        }

        // u2c: per group of 8 ublocks, a 24-bit base index followed by seven byte deltas.
        List<int> firstByUBlock = [];
        for (int g = 0; g < u2cGroups; g++)
        {
            ReadOnlySpan<byte> e = data.Slice((int)u2cAt + (g * 10), 10);
            int baseIndex = e[0] | (e[1] << 8) | (e[2] << 16);
            for (int k = 0; k < 8 && firstByUBlock.Count < ublocks; k++)
            {
                firstByUBlock.Add(k == 0 ? baseIndex : baseIndex + e[2 + k]);
            }
        }

        List<NAPSCblock> records = [];
        for (int i = 0; i < cblocks; i++)
        {
            ReadOnlySpan<byte> e = data.Slice((int)cblockAt + (i * 9), 9);
            records.Add(new NAPSCblock(BinaryPrimitives.ReadUInt64LittleEndian(e), e[8]));
        }

        if (offsets.Count < 3)
        {
            throw new InvalidDataException("naps layout has fewer than three fidx records");
        }

        return new NAPSLayout
        {
            FileOffsetCount = fileCount,
            CompressionType = compression,
            KeyCount = keys,
            UBlockCount = ublocks,
            OuterBlocks = outerRecords,
            ShufflePatterns = shuffleRecords,
            FileOffsets = offsets,
            FileOffsetTypes = types,
            FirstCblockByUBlock = firstByUBlock,
            Cblocks = records,
        };
    }
}
