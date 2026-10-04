using System.Buffers.Binary;
using MkPFS.Core.Util;

namespace MkPFS.Core.AMPR;

/// <summary>
/// AMPRDAT3 data volume header (64 bytes, <c>&lt;8sIIII16sQQII</c>) at offset 0 of every <c>.pak</c> volume
/// (Python <c>build_data_header</c>, <c>validate_data_header</c>).
/// </summary>
/// <param name="PackId">Pack id.</param>
/// <param name="BuildId">16-byte build id.</param>
/// <param name="PayloadOffset">Payload start in the volume.</param>
/// <param name="PayloadBytes">Payload length.</param>
/// <param name="Flags"><c>PackFlag*</c> bits.</param>
public readonly record struct AMPRDataHeader(uint PackId, byte[] BuildId, ulong PayloadOffset, ulong PayloadBytes, uint Flags = 0)
{
    /// <summary>Serialize the header with its CRC.</summary>
    /// <returns>64 bytes.</returns>
    /// <exception cref="ArgumentException">The build id is not 16 bytes.</exception>
    public byte[] ToBytes()
    {
        if (BuildId.Length != AMPRPackFormat.BuildIdSize)
        {
            throw new ArgumentException("build id must contain 16 bytes");
        }

        byte[] data = new byte[AMPRPackFormat.DataHeaderSize];
        Span<byte> span = data;
        AMPRPackFormat.DataMagic.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], AMPRPackFormat.DataVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], AMPRPackFormat.DataHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], PackId);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], Flags);
        BuildId.CopyTo(span[24..]);
        BinaryPrimitives.WriteUInt64LittleEndian(span[40..], PayloadOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(span[48..], PayloadBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(span[56..], Crc32.Update(0, span));
        return data;
    }

    /// <summary>Parse and check a header; optional expectations are compared after the CRC.</summary>
    /// <param name="header">Exactly 64 bytes.</param>
    /// <param name="expectedPackId">Pack id the manifest assigns, or <see langword="null"/>.</param>
    /// <param name="expectedBuildId">Manifest build id, or <see langword="null"/>.</param>
    /// <param name="expectedFlags">Manifest pack flags, or <see langword="null"/>.</param>
    /// <returns>The header.</returns>
    /// <exception cref="InvalidDataException">The header is malformed or does not match.</exception>
    public static AMPRDataHeader Parse(
        ReadOnlySpan<byte> header,
        uint? expectedPackId = null,
        ReadOnlySpan<byte> expectedBuildId = default,
        uint? expectedFlags = null)
    {
        if (header.Length != AMPRPackFormat.DataHeaderSize)
        {
            throw new InvalidDataException("pack data header is truncated");
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        if (!header[..8].SequenceEqual(AMPRPackFormat.DataMagic) || version != AMPRPackFormat.DataVersion || headerSize != AMPRPackFormat.DataHeaderSize)
        {
            throw new InvalidDataException("unsupported pack data header");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(header[60..]) != 0)
        {
            throw new InvalidDataException("invalid pack data header");
        }

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
        if ((flags & ~AMPRPackFormat.PackKnownFlags) != 0)
        {
            throw new InvalidDataException("unknown pack data flags");
        }

        byte[] copy = header.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(56), 0);
        if (Crc32.Update(0, copy) != BinaryPrimitives.ReadUInt32LittleEndian(header[56..]))
        {
            throw new InvalidDataException("pack data header CRC mismatch");
        }

        AMPRDataHeader result = new(
            BinaryPrimitives.ReadUInt32LittleEndian(header[16..]),
            header.Slice(24, AMPRPackFormat.BuildIdSize).ToArray(),
            BinaryPrimitives.ReadUInt64LittleEndian(header[40..]),
            BinaryPrimitives.ReadUInt64LittleEndian(header[48..]),
            flags);
        if (expectedPackId is { } packId && result.PackId != packId)
        {
            throw new InvalidDataException("pack id mismatch");
        }

        if (!expectedBuildId.IsEmpty && !expectedBuildId.SequenceEqual(result.BuildId))
        {
            throw new InvalidDataException("pack build id mismatch");
        }

        return expectedFlags is { } expected && flags != expected
            ? throw new InvalidDataException("pack flags mismatch")
            : result;
    }
}
