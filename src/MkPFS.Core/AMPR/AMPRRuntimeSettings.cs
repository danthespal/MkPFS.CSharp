using System.Buffers.Binary;
using MkPFS.Core.Util;

namespace MkPFS.Core.AMPR;

/// <summary>
/// Per-title runtime settings (<c>&lt;manifest&gt;.runtime</c>, AMPRCFG1, 64 bytes, <c>&lt;8sII16sQQIIII</c>) bound to
/// a manifest build id (Python <c>RuntimeSettings</c>, <c>read_runtime_settings</c>).
/// </summary>
/// <param name="DecodedCacheBytes">Decoded-block cache size.</param>
/// <param name="PhysicalCacheBytes">Physical-page cache size.</param>
/// <param name="Workers">Decode workers, 1..16.</param>
/// <param name="LatencyReserveWorkers">Workers reserved for latency-class requests, below <paramref name="Workers"/>.</param>
public sealed record AMPRRuntimeSettings(long DecodedCacheBytes, long PhysicalCacheBytes, long Workers, long LatencyReserveWorkers)
{
    /// <summary>Check the value ranges.</summary>
    /// <exception cref="ArgumentException">A value is out of range.</exception>
    public void Validate()
    {
        if (Workers < 1 || Workers > 16 || LatencyReserveWorkers < 0 || LatencyReserveWorkers >= Workers)
        {
            throw new ArgumentException("runtime workers must be 1..16 and reserve must be smaller");
        }

        foreach (long value in (ReadOnlySpan<long>)[DecodedCacheBytes, PhysicalCacheBytes])
        {
            if (value < 0 || value % 16384 != 0)
            {
                throw new ArgumentException("runtime cache sizes must be nonnegative 16 KiB multiples");
            }
        }
    }

    /// <summary>Serialize for a manifest build id.</summary>
    /// <param name="buildId">Manifest build id.</param>
    /// <returns>64 bytes.</returns>
    /// <exception cref="ArgumentException">A value is out of range or the build id is not 16 bytes.</exception>
    public byte[] Encode(ReadOnlySpan<byte> buildId)
    {
        Validate();
        if (buildId.Length != AMPRPackFormat.BuildIdSize)
        {
            throw new ArgumentException("runtime profile needs a 16-byte build ID");
        }

        byte[] data = new byte[AMPRPackFormat.RuntimeSize];
        Span<byte> span = data;
        AMPRPackFormat.RuntimeMagic.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], AMPRPackFormat.RuntimeVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], AMPRPackFormat.RuntimeSize);
        buildId.CopyTo(span[16..]);
        BinaryPrimitives.WriteUInt64LittleEndian(span[32..], (ulong)DecodedCacheBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(span[40..], (ulong)PhysicalCacheBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(span[48..], (uint)Workers);
        BinaryPrimitives.WriteUInt32LittleEndian(span[52..], (uint)LatencyReserveWorkers);
        BinaryPrimitives.WriteUInt32LittleEndian(span[56..], Crc32.Update(0, span));
        return data;
    }

    /// <summary>Read settings bound to <paramref name="buildId"/>; <see langword="null"/> when the file does not exist.</summary>
    /// <param name="path">Settings path.</param>
    /// <param name="buildId">Manifest build id.</param>
    /// <returns>Settings, or <see langword="null"/>.</returns>
    /// <exception cref="InvalidDataException">The file is malformed or belongs to another build.</exception>
    public static AMPRRuntimeSettings? Read(string path, ReadOnlySpan<byte> buildId)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        return Parse(data, buildId);
    }

    /// <summary>Parse settings bytes bound to <paramref name="buildId"/>.</summary>
    /// <param name="data">Settings bytes.</param>
    /// <param name="buildId">Manifest build id.</param>
    /// <returns>Settings.</returns>
    /// <exception cref="InvalidDataException">The bytes are malformed or belong to another build.</exception>
    public static AMPRRuntimeSettings Parse(ReadOnlySpan<byte> data, ReadOnlySpan<byte> buildId)
    {
        if (data.Length != AMPRPackFormat.RuntimeSize)
        {
            throw new InvalidDataException("invalid runtime profile size");
        }

        byte[] copy = data.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(56), 0);
        if (!data[..8].SequenceEqual(AMPRPackFormat.RuntimeMagic)
            || BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) != AMPRPackFormat.RuntimeVersion
            || BinaryPrimitives.ReadUInt32LittleEndian(data[12..]) != AMPRPackFormat.RuntimeSize
            || !data.Slice(16, AMPRPackFormat.BuildIdSize).SequenceEqual(buildId)
            || BinaryPrimitives.ReadUInt32LittleEndian(data[60..]) != 0
            || Crc32.Update(0, copy) != BinaryPrimitives.ReadUInt32LittleEndian(data[56..]))
        {
            throw new InvalidDataException("invalid runtime profile header, build ID or CRC");
        }

        ulong decoded = BinaryPrimitives.ReadUInt64LittleEndian(data[32..]);
        ulong physical = BinaryPrimitives.ReadUInt64LittleEndian(data[40..]);
        if (decoded > long.MaxValue || physical > long.MaxValue)
        {
            // Python accepts values below 2**64; no 16 KiB-aligned cache that large is meaningful.
            throw new InvalidDataException("runtime cache sizes must be nonnegative 16 KiB multiples");
        }

        AMPRRuntimeSettings result = new(
            (long)decoded,
            (long)physical,
            BinaryPrimitives.ReadUInt32LittleEndian(data[48..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[52..]));
        try
        {
            result.Validate();
        }
        catch (ArgumentException exc)
        {
            throw new InvalidDataException(exc.Message, exc);
        }

        return result;
    }
}
