using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MkPFS.Core.AMPR;

/// <summary>Deterministic 16-byte build id (Python <c>deterministic_build_id</c>).</summary>
public static class AMPRBuildId
{
    /// <summary>SHA-256 over <c>"AMPRPACK4\0"</c> and each part prefixed by its u64 length; first 16 bytes.</summary>
    /// <param name="parts">Parts in order.</param>
    /// <returns>16 bytes.</returns>
    public static byte[] Compute(IEnumerable<byte[]> parts)
    {
        using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        digest.AppendData("AMPRPACK4\0"u8);
        Span<byte> length = stackalloc byte[8];
        foreach (byte[] part in parts)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(length, (ulong)part.LongLength);
            digest.AppendData(length);
            digest.AppendData(part);
        }

        return digest.GetHashAndReset()[..AMPRPackFormat.BuildIdSize];
    }
}
