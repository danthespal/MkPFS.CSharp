// Injected into the patched LibProsperoPkg copy by build_fpkg_goldens.py. Not part of MkPFS.
// Replaces the oracle's nondeterministic inputs (random outer seed, wall clock) and adds a
// raw-only switch so MkPFS can be compared against a build that skips Kraken for file payloads.

using System;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("FPKGOracle")]

namespace LibProsperoPkg;

/// <summary>Deterministic overrides set by the FPKG oracle runner.</summary>
public static class OracleHooks
{
    /// <summary>Outer PFS crypt seed. Null keeps the oracle's random seed.</summary>
    public static byte[]? OuterSeed { get; set; }

    /// <summary>Clock used for inode times and the package timestamp.</summary>
    public static DateTime UtcNow { get; set; } = DateTime.UnixEpoch;

    /// <summary>When true every inner file payload is stored verbatim (the metadata region is unchanged).</summary>
    public static bool StoreAllRaw { get; set; }

    /// <summary>
    /// RSA (e = 65537 unless given) encryption with EME-PKCS#1 v1.5 type-2 padding whose padding string is derived
    /// from <see cref="OuterSeed"/> instead of a random source: the nonzero bytes of
    /// SHA-256(seed || modulus || data || u32le counter), counter = 0, 1, ... Returns null (oracle keeps
    /// its random padding) when no seed is set. MkPFS uses the same derivation for its CNT key wraps.
    /// </summary>
    internal static byte[]? RsaPkcs1Encrypt(byte[] modulus, byte[] data, byte[]? exponent = null)
    {
        if (OuterSeed is not { Length: 16 } seed)
            return null;

        int k = modulus.Length;
        if (data.Length > k - 11)
            throw new ArgumentException("RSA PKCS#1 message too long.", nameof(data));

        byte[] em = new byte[k];
        em[1] = 0x02;
        int psLength = k - 3 - data.Length;
        int filled = 0;
        byte[] input = new byte[seed.Length + modulus.Length + data.Length + 4];
        seed.CopyTo(input, 0);
        modulus.CopyTo(input, seed.Length);
        data.CopyTo(input, seed.Length + modulus.Length);
        for (uint counter = 0; filled < psLength; counter++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(input.Length - 4), counter);
            foreach (byte b in System.Security.Cryptography.SHA256.HashData(input))
            {
                if (b != 0 && filled < psLength)
                    em[2 + filled++] = b;
            }
        }
        data.CopyTo(em, 3 + psLength);

        var n = new System.Numerics.BigInteger(modulus, isUnsigned: true, isBigEndian: true);
        var m = new System.Numerics.BigInteger(em, isUnsigned: true, isBigEndian: true);
        byte[] c = System.Numerics.BigInteger.ModPow(m, exponent is null ? 65537 : new System.Numerics.BigInteger(exponent, isUnsigned: true, isBigEndian: true), n).ToByteArray(isUnsigned: true, isBigEndian: true);
        byte[] result = new byte[k];
        c.CopyTo(result, k - c.Length);
        return result;
    }

    internal static byte[] NextOuterSeed() =>
        OuterSeed is { Length: 16 } seed
            ? (byte[])seed.Clone()
            : System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);

    internal static LibProsperoPkg.PFS.ProsperoInnerFilePolicy Policy(LibProsperoPkg.PFS.ProsperoInnerFilePolicy policy) =>
        StoreAllRaw && policy == LibProsperoPkg.PFS.ProsperoInnerFilePolicy.Compress
            ? LibProsperoPkg.PFS.ProsperoInnerFilePolicy.StoreVerbatim
            : policy;
}
