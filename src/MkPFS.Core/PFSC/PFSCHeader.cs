using System.Buffers.Binary;
using MkPFS.Core.PFS;
using MkPFS.Core.Util;

namespace MkPFS.Core.PFSC;

/// <summary>
/// Fixed 0x30-byte PFSC header (Python <c>PFSCHeader</c>, layout <c>&lt;iiiiqqQq</c>).
/// </summary>
/// <param name="LogicalBlockSize">Logical block size (written twice: 0x0C as u32 and 0x10 as u64).</param>
/// <param name="BlockOffsetsOffset">Offset of the block offset table (0x400).</param>
/// <param name="DataOffset">Start of block data = header size.</param>
/// <param name="DataLength">Block-rounded logical size (block count × block size).</param>
public readonly record struct PFSCHeader(int LogicalBlockSize, long BlockOffsetsOffset, long DataOffset, long DataLength)
{
    /// <summary>Number of logical blocks described by <see cref="DataLength"/>.</summary>
    public long BlockCount => DataLength / LogicalBlockSize;

    /// <summary>Header span (header + offset table, block aligned) for <paramref name="blockCount"/> blocks.</summary>
    /// <param name="blockCount">Number of logical blocks.</param>
    /// <param name="logicalBlockSize">Logical block size.</param>
    /// <returns>Data offset: 0x10000 plus whole blocks when the offset table outgrows the first block.</returns>
    public static long HeaderSize(long blockCount, int logicalBlockSize = PFSConstants.PFSCLogicalBlockSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(blockCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(logicalBlockSize);
        long tableSize = checked((blockCount + 1) * PFSConstants.PFSCOffsetEntrySize);
        long extraBytes = Math.Max(0, tableSize - PFSConstants.PFSCInitialOffsetTableCapacity);
        long extraBlocks = extraBytes > 0 ? Sizes.CeilDiv(extraBytes, logicalBlockSize) : 0;
        return checked(PFSConstants.PFSCInitialDataOffset + (extraBlocks * logicalBlockSize));
    }

    /// <summary>Header for a payload of <paramref name="blockCount"/> blocks.</summary>
    /// <param name="blockCount">Number of logical blocks.</param>
    /// <param name="logicalBlockSize">Logical block size.</param>
    /// <returns>Header with standard offsets.</returns>
    public static PFSCHeader ForBlocks(long blockCount, int logicalBlockSize = PFSConstants.PFSCLogicalBlockSize) =>
        new(logicalBlockSize, PFSConstants.PFSCBlockOffsetsOffset, HeaderSize(blockCount, logicalBlockSize), checked(blockCount * logicalBlockSize));

    /// <summary>Serialize the 0x30-byte header into <paramref name="destination"/>.</summary>
    /// <param name="destination">At least <see cref="PFSConstants.PFSCHeaderSize"/> bytes.</param>
    public void Write(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, PFSConstants.PFSCMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0x04..], PFSConstants.PFSCUnk4);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0x08..], PFSConstants.PFSCUnk8);
        BinaryPrimitives.WriteInt32LittleEndian(destination[0x0C..], LogicalBlockSize);
        BinaryPrimitives.WriteInt64LittleEndian(destination[0x10..], LogicalBlockSize);
        BinaryPrimitives.WriteInt64LittleEndian(destination[0x18..], BlockOffsetsOffset);
        BinaryPrimitives.WriteInt64LittleEndian(destination[0x20..], DataOffset);
        BinaryPrimitives.WriteInt64LittleEndian(destination[0x28..], DataLength);
    }

    /// <summary>Parse and validate a header with the same rules as Python <c>_parse_pfsc_header</c>.</summary>
    /// <param name="source">At least <see cref="PFSConstants.PFSCHeaderSize"/> bytes.</param>
    /// <returns>Validated header.</returns>
    /// <exception cref="InvalidDataException">A field is invalid.</exception>
    public static PFSCHeader Parse(ReadOnlySpan<byte> source)
    {
        if (source.Length < PFSConstants.PFSCHeaderSize)
        {
            throw new InvalidDataException("PFSC payload is too small for header");
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(source);
        int unk4 = BinaryPrimitives.ReadInt32LittleEndian(source[0x04..]);
        int unk8 = BinaryPrimitives.ReadInt32LittleEndian(source[0x08..]);
        int blockSize = BinaryPrimitives.ReadInt32LittleEndian(source[0x0C..]);
        long blockSize2 = BinaryPrimitives.ReadInt64LittleEndian(source[0x10..]);
        long offsetsOffset = BinaryPrimitives.ReadInt64LittleEndian(source[0x18..]);
        long dataOffset = BinaryPrimitives.ReadInt64LittleEndian(source[0x20..]);
        long logicalSize = BinaryPrimitives.ReadInt64LittleEndian(source[0x28..]);

        if (magic != PFSConstants.PFSCMagic)
        {
            throw new InvalidDataException($"invalid PFSC magic 0x{magic:X8}");
        }

        if (unk4 != PFSConstants.PFSCUnk4)
        {
            throw new InvalidDataException($"invalid PFSC unk4 value {unk4}, expected {PFSConstants.PFSCUnk4}");
        }

        if (unk8 != PFSConstants.PFSCUnk8)
        {
            throw new InvalidDataException($"invalid PFSC unk8 value {unk8}, expected {PFSConstants.PFSCUnk8}");
        }

        if (blockSize != PFSConstants.PFSCLogicalBlockSize)
        {
            throw new InvalidDataException($"invalid PFSC logical block size {blockSize}, expected {PFSConstants.PFSCLogicalBlockSize}");
        }

        if (blockSize2 != blockSize)
        {
            throw new InvalidDataException("PFSC block size mismatch between block_sz and block_sz2");
        }

        if (logicalSize < 0)
        {
            throw new InvalidDataException("PFSC logical size is negative");
        }

        if (logicalSize % blockSize != 0)
        {
            throw new InvalidDataException("PFSC logical size is not aligned to the logical block size");
        }

        if (offsetsOffset < PFSConstants.PFSCHeaderSize)
        {
            throw new InvalidDataException("PFSC block offset table overlaps header");
        }

        if (offsetsOffset != PFSConstants.PFSCBlockOffsetsOffset)
        {
            throw new InvalidDataException($"invalid PFSC block offset table pointer {offsetsOffset}, expected {PFSConstants.PFSCBlockOffsetsOffset}");
        }

        if (dataOffset < PFSConstants.PFSCInitialDataOffset)
        {
            throw new InvalidDataException("PFSC data offset is smaller than the minimum compatible header span");
        }

        return new PFSCHeader(blockSize, offsetsOffset, dataOffset, logicalSize);
    }
}

/// <summary>Per-block and whole-file keep rules (Python <c>_should_store_pfsc_block_compressed</c>).</summary>
public static class PFSCBlockPolicy
{
    /// <summary>
    /// Percent saved by compression, computed exactly like Python
    /// (<c>((size - compressed) / size) * 100.0</c> in double precision).
    /// </summary>
    /// <param name="size">Uncompressed size.</param>
    /// <param name="compressedSize">Compressed size.</param>
    /// <returns>Gain percent (negative when compression grew the data).</returns>
    public static double GainPercent(long size, long compressedSize) =>
        ((double)(size - compressedSize) / size) * 100.0;

    /// <summary>
    /// Store a block compressed only when it is strictly smaller than the logical block (decoders treat a
    /// full-size span as raw) and its gain reaches <paramref name="thresholdGain"/>.
    /// </summary>
    /// <param name="compressedSize">zlib stream length.</param>
    /// <param name="logicalBlockSize">Logical block size.</param>
    /// <param name="thresholdGain">Minimum gain percent, 0..100.</param>
    /// <returns><see langword="true"/> to keep the compressed bytes.</returns>
    public static bool ShouldStoreCompressed(int compressedSize, int logicalBlockSize, int thresholdGain) =>
        compressedSize < logicalBlockSize && GainPercent(logicalBlockSize, compressedSize) >= thresholdGain;
}
