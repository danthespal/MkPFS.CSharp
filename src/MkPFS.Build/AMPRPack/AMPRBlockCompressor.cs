using System.Buffers.Binary;
using System.Security.Cryptography;
using MkPFS.Core.AMPR;
using MkPFS.Core.Compression;
using MkPFS.Core.Util;

namespace MkPFS.Build.AMPRPack;

/// <summary>One encoded block (Python <c>CompressedBlock</c>).</summary>
/// <param name="Raw">Decoded bytes.</param>
/// <param name="Stored">Bytes written to the volume.</param>
/// <param name="Codec"><c>ChunkCodecRaw</c> or <c>ChunkCodecLZ4</c>.</param>
/// <param name="RawCrc">CRC-32 of <paramref name="Raw"/>.</param>
/// <param name="Fingerprint">Deduplication key of the decoded bytes.</param>
public sealed record AMPRCompressedBlock(byte[] Raw, byte[] Stored, int Codec, uint RawCrc, byte[] Fingerprint);

/// <summary>Per-block codec decision (Python <c>_compress_block</c>).</summary>
public static class AMPRBlockCompressor
{
    /// <summary>
    /// Compress one block with the rule's LZ4 mode and keep it only if it saves enough. Random/mixed blocks whose
    /// I/O page count does not drop must also meet the stricter I/O-neutral thresholds.
    /// </summary>
    /// <param name="encoder">Encoder owned by the calling worker.</param>
    /// <param name="raw">Block bytes (not empty).</param>
    /// <param name="rule">Effective rule.</param>
    /// <param name="ioPageSize">I/O page size of the target group.</param>
    /// <returns>The block as it will be stored.</returns>
    public static AMPRCompressedBlock Compress(LZ4Encoder encoder, byte[] raw, AMPRRule rule, long ioPageSize)
    {
        uint rawCrc = Crc32.Update(0, raw);
        // Python uses crc32 + length + BLAKE2b-128; any collision-resistant digest gives the same dedup decisions.
        byte[] fingerprint = new byte[12 + 32];
        BinaryPrimitives.WriteUInt32LittleEndian(fingerprint, rawCrc);
        BinaryPrimitives.WriteInt64LittleEndian(fingerprint.AsSpan(4), raw.Length);
        SHA256.HashData(raw, fingerprint.AsSpan(12));
        if (rule.Action == "store")
        {
            return new AMPRCompressedBlock(raw, raw, AMPRPackFormat.ChunkCodecRaw, rawCrc, fingerprint);
        }

        byte[] compressed = rule.Mode == "hc"
            ? encoder.CompressHC(raw, (int)Math.Clamp(rule.Level, 1, 12))
            : encoder.CompressFast(raw, (int)Math.Clamp(rule.Acceleration, 1, int.MaxValue));
        long saved = raw.Length - compressed.Length;
        double ratio = raw.Length > 0 ? (double)saved / raw.Length : 0.0;
        long requiredBytes = rule.MinSavingsBytes;
        double requiredRatio = rule.MinSavingsRatio;
        // An isolated cold read of a random/mixed chunk costs whole I/O pages; require a stronger gain when
        // compression does not reduce the page count.
        if (rule.ResolvedLayout() != "streaming")
        {
            long rawPages = (raw.Length + ioPageSize - 1) / ioPageSize;
            long compressedPages = (compressed.Length + ioPageSize - 1) / ioPageSize;
            if (compressedPages >= rawPages)
            {
                requiredBytes = Math.Max(requiredBytes, rule.IONeutralMinSavingsBytes);
                requiredRatio = Math.Max(requiredRatio, rule.IONeutralMinSavingsRatio);
            }
        }

        return saved < requiredBytes || ratio < requiredRatio
            ? new AMPRCompressedBlock(raw, raw, AMPRPackFormat.ChunkCodecRaw, rawCrc, fingerprint)
            : new AMPRCompressedBlock(raw, compressed, AMPRPackFormat.ChunkCodecLZ4, rawCrc, fingerprint);
    }
}
