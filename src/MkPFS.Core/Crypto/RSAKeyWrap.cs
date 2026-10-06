using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace MkPFS.Core.Crypto;

/// <summary>
/// RSA public-key encryption with EME-PKCS#1 v1.5 (type 2) padding whose padding string comes from a
/// seed instead of a random source, so package builds are reproducible. Used for the CNT key entries
/// (0x0010, 0x0020) and the CNT+0x1000 header-digest seal. The derivation is fixed by the FPKG oracle
/// hooks (<c>tools/oracle-fpkg/README.md</c>): PS = the nonzero bytes of
/// <c>SHA-256(seed || modulus || message || u32le counter)</c> for counter = 0, 1, ...
/// </summary>
public static class RSAKeyWrap
{
    private const int MinPadding = 11;

    /// <summary>Encrypt <paramref name="message"/>.</summary>
    /// <param name="modulus">Big-endian modulus; its length is the output length.</param>
    /// <param name="exponent">Big-endian public exponent.</param>
    /// <param name="message">At most <c>modulus.Length - 11</c> bytes.</param>
    /// <param name="seed">Padding seed (the package seed).</param>
    /// <returns>Big-endian ciphertext, <c>modulus.Length</c> bytes.</returns>
    public static byte[] Encrypt(ReadOnlySpan<byte> modulus, ReadOnlySpan<byte> exponent, ReadOnlySpan<byte> message, ReadOnlySpan<byte> seed)
    {
        int k = modulus.Length;
        if (message.Length > k - MinPadding)
        {
            throw new ArgumentException("message too long for the RSA modulus", nameof(message));
        }

        // EM = 00 02 PS 00 M.
        byte[] em = new byte[k];
        em[1] = 0x02;
        int psLength = k - 3 - message.Length;
        byte[] input = [.. seed, .. modulus, .. message, 0, 0, 0, 0];
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        int filled = 0;
        for (uint counter = 0; filled < psLength; counter++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(input.Length - 4), counter);
            SHA256.HashData(input, digest);
            foreach (byte b in digest)
            {
                if (b != 0 && filled < psLength)
                {
                    em[2 + filled++] = b;
                }
            }
        }

        message.CopyTo(em.AsSpan(3 + psLength));

        BigInteger n = new(modulus, isUnsigned: true, isBigEndian: true);
        BigInteger e = new(exponent, isUnsigned: true, isBigEndian: true);
        BigInteger m = new(em, isUnsigned: true, isBigEndian: true);
        byte[] c = BigInteger.ModPow(m, e, n).ToByteArray(isUnsigned: true, isBigEndian: true);
        byte[] result = new byte[k];
        c.CopyTo(result, k - c.Length);
        return result;
    }
}
