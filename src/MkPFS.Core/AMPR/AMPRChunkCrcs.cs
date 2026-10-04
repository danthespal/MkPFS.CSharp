using System.Buffers.Binary;
using MkPFS.Core.Util;

namespace MkPFS.Core.AMPR;

/// <summary>
/// AMPRCRC1 sidecar (<c>&lt;manifest&gt;.crc</c>): a 48-byte header (<c>&lt;8sII16sQII</c>) and one CRC-32 of the
/// decoded bytes per chunk, in chunk order. Offline only; the PRX never loads it
/// (Python <c>build_chunk_crc_bytes</c>, <c>load_chunk_crcs</c>).
/// </summary>
public static class AMPRChunkCrcs
{
    /// <summary>The sidecar payload: one little-endian CRC per chunk (Python <c>encode_chunk_crcs</c>; a build-id input).</summary>
    /// <param name="checksums">Decoded CRC-32 per chunk.</param>
    /// <returns>Payload bytes.</returns>
    public static byte[] EncodePayload(IReadOnlyList<uint> checksums)
    {
        byte[] payload = new byte[checked(checksums.Count * 4)];
        for (int i = 0; i < checksums.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(i * 4), checksums[i]);
        }

        return payload;
    }

    /// <summary>Serialize a sidecar.</summary>
    /// <param name="buildId">Manifest build id.</param>
    /// <param name="checksums">Decoded CRC-32 per chunk.</param>
    /// <returns>Sidecar bytes.</returns>
    /// <exception cref="ArgumentException">The build id is not 16 bytes.</exception>
    public static byte[] Build(ReadOnlySpan<byte> buildId, IReadOnlyList<uint> checksums)
    {
        if (buildId.Length != AMPRPackFormat.BuildIdSize)
        {
            throw new ArgumentException("CRC sidecar build ID must contain 16 bytes");
        }

        byte[] data = new byte[checked(AMPRPackFormat.CrcHeaderSize + (checksums.Count * 4))];
        Span<byte> span = data;
        EncodePayload(checksums).CopyTo(span[AMPRPackFormat.CrcHeaderSize..]);

        AMPRPackFormat.CrcMagic.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], AMPRPackFormat.CrcVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], AMPRPackFormat.CrcHeaderSize);
        buildId.CopyTo(span[16..]);
        BinaryPrimitives.WriteUInt64LittleEndian(span[32..], (ulong)checksums.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], Crc32.Update(0, span[AMPRPackFormat.CrcHeaderSize..]));
        BinaryPrimitives.WriteUInt32LittleEndian(span[44..], Crc32.Update(0, span[..AMPRPackFormat.CrcHeaderSize]));
        return data;
    }

    /// <summary>Load and check a sidecar file.</summary>
    /// <param name="path">Sidecar path.</param>
    /// <param name="expectedBuildId">Manifest build id.</param>
    /// <param name="expectedChunkCount">Manifest chunk count.</param>
    /// <returns>One CRC per chunk.</returns>
    /// <exception cref="InvalidDataException">The sidecar is malformed or belongs to another build.</exception>
    public static uint[] Load(string path, ReadOnlySpan<byte> expectedBuildId, long expectedChunkCount) =>
        Parse(File.ReadAllBytes(path), expectedBuildId, expectedChunkCount);

    /// <summary>Parse and check sidecar bytes.</summary>
    /// <param name="data">Sidecar bytes.</param>
    /// <param name="expectedBuildId">Manifest build id.</param>
    /// <param name="expectedChunkCount">Manifest chunk count.</param>
    /// <returns>One CRC per chunk.</returns>
    /// <exception cref="InvalidDataException">The sidecar is malformed or belongs to another build.</exception>
    public static uint[] Parse(ReadOnlySpan<byte> data, ReadOnlySpan<byte> expectedBuildId, long expectedChunkCount)
    {
        if (data.Length < AMPRPackFormat.CrcHeaderSize)
        {
            throw new InvalidDataException("chunk CRC sidecar is truncated");
        }

        if (!data[..8].SequenceEqual(AMPRPackFormat.CrcMagic)
            || BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) != AMPRPackFormat.CrcVersion
            || BinaryPrimitives.ReadUInt32LittleEndian(data[12..]) != AMPRPackFormat.CrcHeaderSize)
        {
            throw new InvalidDataException("unsupported chunk CRC sidecar");
        }

        ulong chunkCount = BinaryPrimitives.ReadUInt64LittleEndian(data[32..]);
        if (!data.Slice(16, AMPRPackFormat.BuildIdSize).SequenceEqual(expectedBuildId) || chunkCount != (ulong)expectedChunkCount)
        {
            throw new InvalidDataException("chunk CRC sidecar build ID or chunk count mismatch");
        }

        byte[] header = data[..AMPRPackFormat.CrcHeaderSize].ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44), 0);
        if (Crc32.Update(0, header) != BinaryPrimitives.ReadUInt32LittleEndian(data[44..]))
        {
            throw new InvalidDataException("chunk CRC sidecar header CRC mismatch");
        }

        ReadOnlySpan<byte> payload = data[AMPRPackFormat.CrcHeaderSize..];
        if ((ulong)payload.Length != chunkCount * 4 || Crc32.Update(0, payload) != BinaryPrimitives.ReadUInt32LittleEndian(data[40..]))
        {
            throw new InvalidDataException("chunk CRC sidecar payload CRC mismatch");
        }

        uint[] values = new uint[chunkCount];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadUInt32LittleEndian(payload[(i * 4)..]);
        }

        return values;
    }
}
