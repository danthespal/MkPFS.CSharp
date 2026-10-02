using System.Buffers.Binary;
using System.Security.Cryptography;
using MkPFS.Core.PFS;

namespace MkPFS.Core.Crypto;

/// <summary>PFS key derivation (port of Python <c>pfs_gen_*</c>, <c>pfs.py:163-300</c>).</summary>
public static class PFSKeys
{
    /// <summary><c>HMAC-SHA256(key, u32le(index) || seed)</c> (Python <c>pfs_gen_crypto_key</c>).</summary>
    /// <param name="key">Base key.</param>
    /// <param name="seed">Image seed.</param>
    /// <param name="index">Key index (1 = encryption, 2 = signing).</param>
    /// <returns>32-byte key.</returns>
    public static byte[] CryptoKey(ReadOnlySpan<byte> key, ReadOnlySpan<byte> seed, uint index)
    {
        byte[] data = new byte[4 + seed.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data, index);
        seed.CopyTo(data.AsSpan(4));
        return HMACSHA256.HashData(key, data);
    }

    /// <summary>Signing key = <c>CryptoKey(EKPFS, seed, 2)</c>.</summary>
    /// <param name="ekpfs">EKPFS key.</param>
    /// <param name="seed">Image seed.</param>
    /// <returns>32-byte HMAC key.</returns>
    public static byte[] SignKey(ReadOnlySpan<byte> ekpfs, ReadOnlySpan<byte> seed) => CryptoKey(ekpfs, seed, 2);

    /// <summary>
    /// XTS key in data‖tweak order. <c>enc = CryptoKey(base, seed, 1)</c>, tweak = enc[0..16], data = enc[16..32];
    /// base is EKPFS, or <c>HMAC-SHA256(EKPFS, seed)</c> with <paramref name="newCrypt"/>.
    /// </summary>
    /// <param name="ekpfs">EKPFS key (32 bytes).</param>
    /// <param name="seed">Image seed.</param>
    /// <param name="newCrypt">Use the alternate newCrypt derivation.</param>
    /// <returns>32-byte XTS key.</returns>
    public static byte[] XtsKey(ReadOnlySpan<byte> ekpfs, ReadOnlySpan<byte> seed, bool newCrypt = false)
    {
        byte[] baseKey = newCrypt ? HMACSHA256.HashData(ekpfs, seed) : ekpfs.ToArray();
        byte[] enc = CryptoKey(baseKey, seed, 1);
        return [.. enc.AsSpan(16, 16), .. enc.AsSpan(0, 16)];
    }

    /// <summary>
    /// Parse a 64-hex EKPFS key; empty or <see langword="null"/> gives the all-zero key
    /// (Python <c>parse_ekpfs_key_hex</c>).
    /// </summary>
    /// <param name="hex">Hex text.</param>
    /// <returns>32-byte key.</returns>
    /// <exception cref="FormatException">Not exactly 64 hex characters.</exception>
    public static byte[] ParseEkpfsHex(string? hex)
    {
        string normalized = (hex ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return PFSConstants.ZeroEkpfs.ToArray();
        }

        if (normalized.Length != 64 || normalized.Any(c => !Uri.IsHexDigit(c)))
        {
            throw new FormatException("--ekpfs-key must be exactly 64 hexadecimal characters");
        }

        return Convert.FromHexString(normalized);
    }
}
