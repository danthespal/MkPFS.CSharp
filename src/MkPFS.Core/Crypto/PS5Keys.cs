using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MkPFS.Core.Crypto;

/// <summary>
/// PS5 debug-package key material derived from public inputs (content id and passcode), mirroring
/// LibProsperoPkg <c>Crypto.ComputeKeys</c>, <c>ProsperoPfsKeys</c> and <c>Crypto.CreateKeystone</c>.
/// </summary>
public static class PS5Keys
{
    /// <summary>Content id length in characters.</summary>
    public const int ContentIdLength = 36;

    /// <summary>Passcode length in characters.</summary>
    public const int PasscodeLength = 32;

    /// <summary>Key-ladder index of the EKPFS.</summary>
    public const uint EkpfsIndex = 1;

    /// <summary>XTS sector flag for signed (metadata) blocks of the outer image.</summary>
    public const ulong SignedBlockSectorFlag = 1UL << 47;

    /// <summary>Size of <c>sce_sys/keystone</c>.</summary>
    public const int KeystoneSize = 0x60;

    // Published keystone HMAC keys (LibProsperoPkg Util/Keys.cs keystone_hmac_key_ps5 / keystone_mac_data_ps5).
    private static ReadOnlySpan<byte> KeystoneFingerprintKey =>
    [
        0xFE, 0x2F, 0x86, 0xDC, 0x4C, 0x11, 0x0C, 0x93, 0xB1, 0xF6, 0x87, 0xD2, 0xDA, 0x21, 0xB8, 0x78,
        0x62, 0xFD, 0x16, 0xE3, 0x1D, 0xC0, 0xF0, 0xA8, 0x32, 0x10, 0x35, 0xFD, 0xDF, 0x6C, 0x56, 0xB7,
    ];

    private static ReadOnlySpan<byte> KeystoneMacKey =>
    [
        0x4B, 0xAC, 0x10, 0x05, 0x83, 0xDC, 0x7E, 0xD2, 0x17, 0x8D, 0x0C, 0xE1, 0x99, 0xFE, 0xE9, 0xA2,
        0xD7, 0x2C, 0x7B, 0x7B, 0x0A, 0x10, 0xB7, 0x2F, 0xE4, 0xBE, 0x51, 0xD8, 0xBC, 0xA1, 0x88, 0x28,
    ];

    /// <summary>
    /// Package key ladder: <c>H(H(u32be index) || H(content id NUL-padded to 48) || passcode)</c> with
    /// H = SHA3-256 (PS5 outer image) or SHA-256 (older images; the oracle extractor tries both).
    /// </summary>
    /// <param name="contentId">36-character ASCII content id.</param>
    /// <param name="passcode">32-character ASCII passcode.</param>
    /// <param name="index">Ladder index (1 = EKPFS).</param>
    /// <param name="useSha3">SHA3-256 when true, SHA-256 otherwise.</param>
    /// <returns>32-byte key.</returns>
    public static byte[] DeriveKey(string contentId, string passcode, uint index, bool useSha3 = true)
    {
        byte[] id = AsciiField(contentId, ContentIdLength, nameof(contentId));
        byte[] code = AsciiField(passcode, PasscodeLength, nameof(passcode));

        Span<byte> indexBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(indexBytes, index);
        byte[] paddedId = new byte[48];
        id.CopyTo(paddedId, 0);

        byte[] data = new byte[96];
        Hash(indexBytes, data.AsSpan(0, 32), useSha3);
        Hash(paddedId, data.AsSpan(32, 32), useSha3);
        code.CopyTo(data, 64);
        byte[] key = new byte[32];
        Hash(data, key, useSha3);
        return key;
    }

    /// <summary>EKPFS of the outer image (<see cref="DeriveKey"/> with index 1 and SHA3-256).</summary>
    /// <param name="contentId">36-character ASCII content id.</param>
    /// <param name="passcode">32-character ASCII passcode.</param>
    /// <returns>32-byte EKPFS.</returns>
    public static byte[] DeriveEkpfs(string contentId, string passcode) => DeriveKey(contentId, passcode, EkpfsIndex);

    /// <summary>Outer-image XTS key in data‖tweak order (newCrypt ladder, <see cref="PFSKeys.XtsKey"/>).</summary>
    /// <param name="ekpfs">32-byte EKPFS.</param>
    /// <param name="seed">16-byte superblock seed.</param>
    /// <returns>32-byte key for <see cref="XtsAes"/>.</returns>
    public static byte[] XtsKey(ReadOnlySpan<byte> ekpfs, ReadOnlySpan<byte> seed) => PFSKeys.XtsKey(ekpfs, seed, newCrypt: true);

    /// <summary>Outer-image sign key: <c>CryptoKey(HMAC-SHA256(EKPFS, seed), seed, 2)</c>.</summary>
    /// <param name="ekpfs">32-byte EKPFS.</param>
    /// <param name="seed">16-byte superblock seed.</param>
    /// <returns>32-byte key.</returns>
    public static byte[] SignKey(ReadOnlySpan<byte> ekpfs, ReadOnlySpan<byte> seed) =>
        PFSKeys.CryptoKey(HMACSHA256.HashData(ekpfs, seed), seed, 2);

    /// <summary>
    /// XTS data-unit number of an outer-image block. Each 0x10000 block is one data unit; signed blocks
    /// set bit 47 (<c>ProsperoOuterPfsSignature.BlockSector</c>).
    /// </summary>
    /// <param name="blockIndex">Block index inside the outer image.</param>
    /// <param name="signed">Block holds signed metadata.</param>
    /// <returns>Sector number for <see cref="XtsAes"/>.</returns>
    public static ulong OuterBlockSector(long blockIndex, bool signed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(blockIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(blockIndex, (long)uint.MaxValue);
        return (ulong)blockIndex | (signed ? SignedBlockSectorFlag : 0);
    }

    /// <summary>
    /// <c>sce_sys/keystone</c> (version 3): a 0x20-byte header (<c>"keystone"</c>, u16le version 3, zeros),
    /// <c>HMAC-SHA256(fingerprint key, passcode)</c>, then <c>HMAC-SHA256(MAC key, first 0x40 bytes)</c>. The header
    /// is PS5PkgTool's; LibProsperoPkg also writes u16le 1 at offset 10, which Publishing Tools accepts too.
    /// </summary>
    /// <param name="passcode">32-character ASCII passcode.</param>
    /// <returns>96-byte keystone.</returns>
    public static byte[] Keystone(string passcode)
    {
        byte[] code = AsciiField(passcode, PasscodeLength, nameof(passcode));
        byte[] keystone = new byte[KeystoneSize];
        "keystone"u8.CopyTo(keystone);
        BinaryPrimitives.WriteUInt16LittleEndian(keystone.AsSpan(8), 3);
        HMACSHA256.HashData(KeystoneFingerprintKey, code, keystone.AsSpan(0x20, 0x20));
        HMACSHA256.HashData(KeystoneMacKey, keystone.AsSpan(0, 0x40), keystone.AsSpan(0x40, 0x20));
        return keystone;
    }

    private static void Hash(ReadOnlySpan<byte> data, Span<byte> destination, bool useSha3)
    {
        if (useSha3)
        {
            SHA3256.HashData(data, destination);
        }
        else
        {
            SHA256.HashData(data, destination);
        }
    }

    private static byte[] AsciiField(string value, int length, string name)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        if (value.Length != length || !Ascii.IsValid(value))
        {
            throw new ArgumentException($"{name} must be exactly {length} ASCII characters", name);
        }

        return Encoding.ASCII.GetBytes(value);
    }
}
