using System.Buffers.Binary;

namespace MkPFS.Core.AMPR;

/// <summary>
/// AMPRPAK4 file record (48 bytes, <c>&lt;QQqIIIIIBBH</c>). One per AMPRIDX3 file, so
/// <c>fileId == recordIndex + 1</c>. Loose files have no chunks and zero flags.
/// </summary>
/// <param name="PathHash">FNV-1a 64 of the ASCII-folded canonical path.</param>
/// <param name="LogicalSize">Decoded file size.</param>
/// <param name="MTime">Modification time, Unix seconds.</param>
/// <param name="FirstChunk">Index of the first chunk record.</param>
/// <param name="ChunkCount">Number of chunk records.</param>
/// <param name="PathOffset">Path offset in the string table.</param>
/// <param name="PathLength">Path length in bytes (without NUL).</param>
/// <param name="Flags"><c>FileFlag*</c> bits.</param>
/// <param name="BlockShift">log2 of the block size (0 for loose files).</param>
/// <param name="PackingClass">Index of the packing group in sorted group order.</param>
/// <param name="Reserved">Must be 0.</param>
public readonly record struct AMPRFileRecord(
    ulong PathHash,
    ulong LogicalSize,
    long MTime,
    uint FirstChunk,
    uint ChunkCount,
    uint PathOffset,
    uint PathLength,
    uint Flags,
    byte BlockShift,
    byte PackingClass = 0,
    ushort Reserved = 0)
{
    /// <summary>Whether the file is stored in pack volumes.</summary>
    public bool IsPacked => (Flags & AMPRPackFormat.FileFlagPacked) != 0;

    /// <summary>Write the record.</summary>
    /// <param name="destination">At least 48 bytes.</param>
    public void Write(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(destination, PathHash);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], LogicalSize);
        BinaryPrimitives.WriteInt64LittleEndian(destination[16..], MTime);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[24..], FirstChunk);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[28..], ChunkCount);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[32..], PathOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[36..], PathLength);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[40..], Flags);
        destination[44] = BlockShift;
        destination[45] = PackingClass;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[46..], Reserved);
    }

    /// <summary>Read a record.</summary>
    /// <param name="source">At least 48 bytes.</param>
    /// <returns>The record.</returns>
    public static AMPRFileRecord Read(ReadOnlySpan<byte> source) => new(
        BinaryPrimitives.ReadUInt64LittleEndian(source),
        BinaryPrimitives.ReadUInt64LittleEndian(source[8..]),
        BinaryPrimitives.ReadInt64LittleEndian(source[16..]),
        BinaryPrimitives.ReadUInt32LittleEndian(source[24..]),
        BinaryPrimitives.ReadUInt32LittleEndian(source[28..]),
        BinaryPrimitives.ReadUInt32LittleEndian(source[32..]),
        BinaryPrimitives.ReadUInt32LittleEndian(source[36..]),
        BinaryPrimitives.ReadUInt32LittleEndian(source[40..]),
        source[44],
        source[45],
        BinaryPrimitives.ReadUInt16LittleEndian(source[46..]));
}

/// <summary>
/// AMPRPAK4 chunk record (12 bytes, <c>&lt;QI</c>): a 64-bit location (48-bit offset, 16-bit pack id) and a
/// 32-bit descriptor (20-bit <c>stored_size - 1</c>, 2-bit codec, 8-bit flags). The decoded size is not stored;
/// <see cref="AMPRPackManifest"/> derives <see cref="RawSize"/> from the owning file's block geometry.
/// </summary>
/// <param name="Offset">Absolute offset in the pack volume.</param>
/// <param name="StoredSize">Stored bytes (1 .. 1 MiB).</param>
/// <param name="RawSize">Decoded bytes (0 until derived).</param>
/// <param name="PackId">Pack volume index.</param>
/// <param name="Codec"><c>ChunkCodecRaw</c> or <c>ChunkCodecLZ4</c>.</param>
/// <param name="Flags"><c>ChunkFlag*</c> bits.</param>
public readonly record struct AMPRChunkRecord(ulong Offset, int StoredSize, int RawSize, int PackId, int Codec, int Flags = 0)
{
    /// <summary>Write the record (Python <c>ChunkRecord.pack</c>).</summary>
    /// <param name="destination">At least 12 bytes.</param>
    /// <exception cref="ArgumentException">A field is outside its bit domain.</exception>
    public void Write(Span<byte> destination)
    {
        if (Offset > AMPRPackFormat.ChunkOffsetMask)
        {
            throw new ArgumentException("chunk offset exceeds the 48-bit AMPRPAK4 domain");
        }

        if (PackId is < 0 or > 0xFFFF)
        {
            throw new ArgumentException("chunk pack id exceeds uint16");
        }

        if (StoredSize < 1 || StoredSize > 1 << AMPRPackFormat.MaxBlockShift)
        {
            throw new ArgumentException("chunk stored size is outside the AMPRPAK4 domain");
        }

        if (Codec < 0 || Codec > AMPRPackFormat.ChunkCodecMask)
        {
            throw new ArgumentException("chunk codec is outside the compact descriptor domain");
        }

        if ((Flags & ~AMPRPackFormat.ChunkKnownFlags) != 0)
        {
            throw new ArgumentException("chunk contains unknown flags");
        }

        ulong location = Offset | ((ulong)PackId << 48);
        uint descriptor = (uint)(StoredSize - 1)
            | ((uint)Codec << AMPRPackFormat.ChunkCodecShift)
            | ((uint)Flags << AMPRPackFormat.ChunkFlagsShift);
        BinaryPrimitives.WriteUInt64LittleEndian(destination, location);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], descriptor);
    }

    /// <summary>Read a record; <see cref="RawSize"/> is 0 (Python <c>ChunkRecord.unpack_from</c>).</summary>
    /// <param name="source">At least 12 bytes.</param>
    /// <returns>The record.</returns>
    /// <exception cref="InvalidDataException">Reserved descriptor bits are set.</exception>
    public static AMPRChunkRecord Read(ReadOnlySpan<byte> source)
    {
        ulong location = BinaryPrimitives.ReadUInt64LittleEndian(source);
        uint descriptor = BinaryPrimitives.ReadUInt32LittleEndian(source[8..]);
        if ((descriptor & ~AMPRPackFormat.ChunkDescriptorKnownMask) != 0)
        {
            throw new InvalidDataException("chunk descriptor contains reserved bits");
        }

        return new AMPRChunkRecord(
            location & AMPRPackFormat.ChunkOffsetMask,
            (int)(descriptor & AMPRPackFormat.ChunkStoredMask) + 1,
            0,
            (int)(location >> 48),
            (int)((descriptor >> AMPRPackFormat.ChunkCodecShift) & AMPRPackFormat.ChunkCodecMask),
            (int)((descriptor >> AMPRPackFormat.ChunkFlagsShift) & AMPRPackFormat.ChunkFlagsMask));
    }
}

/// <summary>AMPRPAK4 pack (data volume) record (32 bytes, <c>&lt;QQIIII</c>).</summary>
/// <param name="PayloadBytes">Payload bytes at the end of the volume.</param>
/// <param name="FileSize">Volume file size.</param>
/// <param name="NameOffset">Volume file name offset in the string table.</param>
/// <param name="NameLength">Volume file name length in bytes.</param>
/// <param name="Flags"><c>PackFlag*</c> bits.</param>
/// <param name="IOPageSize">Filesystem I/O page size the layout was planned for.</param>
public readonly record struct AMPRPackRecord(
    ulong PayloadBytes,
    ulong FileSize,
    uint NameOffset,
    uint NameLength,
    uint Flags,
    uint IOPageSize)
{
    /// <summary>Write the record.</summary>
    /// <param name="destination">At least 32 bytes.</param>
    public void Write(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(destination, PayloadBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], FileSize);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[16..], NameOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[20..], NameLength);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[24..], Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[28..], IOPageSize);
    }

    /// <summary>Read a record.</summary>
    /// <param name="source">At least 32 bytes.</param>
    /// <returns>The record.</returns>
    public static AMPRPackRecord Read(ReadOnlySpan<byte> source) => new(
        BinaryPrimitives.ReadUInt64LittleEndian(source),
        BinaryPrimitives.ReadUInt64LittleEndian(source[8..]),
        BinaryPrimitives.ReadUInt32LittleEndian(source[16..]),
        BinaryPrimitives.ReadUInt32LittleEndian(source[20..]),
        BinaryPrimitives.ReadUInt32LittleEndian(source[24..]),
        BinaryPrimitives.ReadUInt32LittleEndian(source[28..]));
}
