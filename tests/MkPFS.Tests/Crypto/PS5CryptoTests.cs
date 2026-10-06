using System.Security.Cryptography;
using System.Text;
using MkPFS.Core.Crypto;
using MkPFS.Core.PFS.PS5;
using MkPFS.Core.Util;

namespace MkPFS.Tests.Crypto;

/// <summary>FPKG plan F1: SHA3-256, CRC-32C, PS5 path hash, PS5 keys and the deterministic RSA wrap.</summary>
public sealed class PS5CryptoTests
{
    private const string ContentId = "UP9000-PPSA99999_00-MKPFSORACLE00000";
    private static readonly string Passcode = new('0', 32);

    // FIPS 202 / NIST CSRC example values.
    [Theory]
    [InlineData("", "a7ffc6f8bf1ed76651c14756a061d662f580ff4de43b49fa82d80a4b80f8434a")]
    [InlineData("abc", "3a985da74fe225b2045c172d6bd390bd855f086e3e9d525b46bfe24511431532")]
    [InlineData("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq",
        "41c0dba2a9d6240849100376a8235e2c82e1b9998a999e21db32dd97496d3376")]
    public void SHA3256_matches_NIST_examples(string input, string expected) =>
        Assert.Equal(expected, Convert.ToHexStringLower(SHA3256.HashData(Encoding.ASCII.GetBytes(input))));

    [Fact]
    public void SHA3256_matches_NIST_200_bytes_of_A3()
    {
        byte[] input = Enumerable.Repeat((byte)0xA3, 200).ToArray();
        Assert.Equal("79f38adec5c20307a98ef76e8324afbfd46cfd81b22e3973c65fa1bd9de31787",
            Convert.ToHexStringLower(SHA3256.HashData(input)));
    }

    [Fact]
    public void SHA3256_incremental_equals_one_shot_for_every_split()
    {
        byte[] data = new byte[3 * 136 + 7];
        new Random(5).NextBytes(data);
        byte[] expected = SHA3256.HashData(data);
        SHA3256 hasher = new();
        foreach (int chunk in new[] { 1, 7, 135, 136, 137, 300 })
        {
            for (int offset = 0; offset < data.Length; offset += chunk)
            {
                hasher.Append(data.AsSpan(offset, Math.Min(chunk, data.Length - offset)));
            }

            Assert.Equal(expected, hasher.GetHashAndReset());
        }
    }

    [Fact]
    public void SHA3256_matches_platform_SHA3_when_available()
    {
        if (!SHA3_256.IsSupported)
        {
            Assert.Skip("platform SHA3-256 unavailable");
        }

        // The managed implementation (the fallback where the platform has no SHA3) against the platform's.
        Random random = new(7);
        SHA3256 managed = new(platform: false);
        for (int length = 0; length < 700; length += 13)
        {
            byte[] data = new byte[length];
            random.NextBytes(data);
            managed.Append(data.AsSpan(0, length / 3));
            managed.Append(data.AsSpan(length / 3));
            Assert.Equal(SHA3_256.HashData(data), managed.GetHashAndReset());
            Assert.Equal(SHA3_256.HashData(data), SHA3256.HashData(data));
        }
    }

    [Fact]
    public void Crc32C_matches_the_check_value_and_chains()
    {
        byte[] check = "123456789"u8.ToArray();
        Assert.Equal(0xE3069283u, Crc32C.Update(0, check));
        Assert.Equal(0u, Crc32C.Update(0, []));
        Assert.Equal(Crc32C.Update(0, check), Crc32C.Update(Crc32C.Update(0, check.AsSpan(0, 5)), check.AsSpan(5)));
    }

    [Theory]
    [InlineData("/sce_sys/keystone", "sce_sys/keystone")]
    [InlineData("/eboot.bin", "EBOOT.BIN")]
    public void PS5PathHash_strips_one_slash_and_ignores_case(string a, string b) =>
        Assert.Equal(PS5PathHash.HashPath(a), PS5PathHash.HashPath(b));

    [Fact]
    public void PS5PathHash_name_and_path_agree_for_ascii() =>
        Assert.Equal(PS5PathHash.HashName("pfs_image.dat"), PS5PathHash.HashPath("/pfs_image.dat"));

    [Fact]
    public void Keystone_has_the_version_3_header()
    {
        byte[] keystone = PS5Keys.Keystone(Passcode);
        Assert.Equal(PS5Keys.KeystoneSize, keystone.Length);
        Assert.Equal("keystone"u8.ToArray(), keystone[..8]);
        Assert.Equal(new byte[] { 3, 0, 0, 0 }, keystone[8..12]);
        Assert.All(keystone[12..0x20], b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData("UP9000-PPSA99999_00-SHORT", "00000000000000000000000000000000")]
    [InlineData("UP9000-PPSA99999_00-MKPFSORACLE0000é", "00000000000000000000000000000000")]
    [InlineData("UP9000-PPSA99999_00-MKPFSORACLE00000", "0000")]
    public void DeriveKey_rejects_bad_identifiers(string contentId, string passcode) =>
        Assert.Throws<ArgumentException>(() => PS5Keys.DeriveKey(contentId, passcode, 1));

    [Fact]
    public void OuterBlockSector_sets_bit_47_for_signed_blocks()
    {
        Assert.Equal(5UL, PS5Keys.OuterBlockSector(5, signed: false));
        Assert.Equal(0x8000_0000_0005UL, PS5Keys.OuterBlockSector(5, signed: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => PS5Keys.OuterBlockSector(-1, signed: false));
    }

    [Fact]
    public void XtsKey_and_SignKey_use_the_new_crypt_ladder()
    {
        byte[] ekpfs = PS5Keys.DeriveEkpfs(ContentId, Passcode);
        byte[] seed = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        byte[] baseKey = HMACSHA256.HashData(ekpfs, seed);
        Assert.Equal(PFSKeys.CryptoKey(baseKey, seed, 2), PS5Keys.SignKey(ekpfs, seed));
        byte[] enc = PFSKeys.CryptoKey(baseKey, seed, 1);
        Assert.Equal([.. enc[16..], .. enc[..16]], PS5Keys.XtsKey(ekpfs, seed));
    }

    [Fact]
    public void RSAKeyWrap_decrypts_with_the_private_key_and_is_deterministic()
    {
        using RSA rsa = RSA.Create(3072);
        RSAParameters pub = rsa.ExportParameters(false);
        byte[] seed = new byte[16];
        byte[] message = Encoding.ASCII.GetBytes(Passcode);
        byte[] wrapped = RSAKeyWrap.Encrypt(pub.Modulus, pub.Exponent, message, seed);
        Assert.Equal(384, wrapped.Length);
        Assert.Equal(wrapped, RSAKeyWrap.Encrypt(pub.Modulus, pub.Exponent, message, seed));
        Assert.Equal(message, rsa.Decrypt(wrapped, RSAEncryptionPadding.Pkcs1));
        Assert.NotEqual(wrapped, RSAKeyWrap.Encrypt(pub.Modulus, pub.Exponent, message, new byte[] { 1 }));
        Assert.Throws<ArgumentException>(() => RSAKeyWrap.Encrypt(pub.Modulus, pub.Exponent, new byte[374], seed));
    }
}
