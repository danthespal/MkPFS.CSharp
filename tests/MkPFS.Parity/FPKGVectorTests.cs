using System.Security.Cryptography;
using System.Text.Json;
using MkPFS.Core.Crypto;
using MkPFS.Core.PFS.PS5;
using MkPFS.Core.Util;

namespace MkPFS.Parity;

/// <summary>
/// FPKG plan F1: crypto and hash primitives against the LibProsperoPkg oracle vectors written by
/// <c>tools/oracle-fpkg/build_fpkg_goldens.py</c> (<c>fpkg/vectors/vectors.json</c>).
/// </summary>
public sealed class FPKGVectorTests
{
    private static JsonElement Vectors(string section)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(Fixtures.PathOrSkip("fpkg", "vectors", "vectors.json")));
        JsonElement items = doc.RootElement.GetProperty(section).Clone();
        Assert.NotEqual(0, items.GetArrayLength());
        return items;
    }

    private static byte[] Hex(JsonElement e, string name) => Convert.FromHexString(e.GetProperty(name).GetString()!);

    private static ulong HexU64(JsonElement e, string name) => Convert.ToUInt64(e.GetProperty(name).GetString(), 16);

    private static byte[] Pattern(int length) => Enumerable.Range(0, length).Select(i => (byte)((i * 31 + 7) & 0xFF)).ToArray();

    [Fact]
    public void Key_ladder_matches_the_oracle()
    {
        foreach (JsonElement v in Vectors("keys").EnumerateArray())
        {
            string contentId = v.GetProperty("content_id").GetString()!;
            string passcode = v.GetProperty("passcode").GetString()!;
            byte[] seed = Hex(v, "seed");
            byte[] ekpfs = PS5Keys.DeriveEkpfs(contentId, passcode);
            Assert.Equal(Hex(v, "ekpfs_sha3"), ekpfs);
            Assert.Equal(Hex(v, "ekpfs_sha256"), PS5Keys.DeriveKey(contentId, passcode, 1, useSha3: false));
            Assert.Equal([.. Hex(v, "xts_data_key"), .. Hex(v, "xts_tweak_key")], PS5Keys.XtsKey(ekpfs, seed));
            Assert.Equal(Hex(v, "sign_key"), PS5Keys.SignKey(ekpfs, seed));
        }
    }

    [Fact]
    public void Outer_name_hash_matches_the_oracle()
    {
        foreach (JsonElement v in Vectors("flt_path_hash").EnumerateArray())
        {
            Assert.Equal(HexU64(v, "hash"), PS5PathHash.HashName(v.GetProperty("name").GetString()!));
        }
    }

    [Fact]
    public void Inner_path_hash_matches_the_oracle()
    {
        foreach (JsonElement v in Vectors("inner_path_hash").EnumerateArray())
        {
            string path = v.GetProperty("path").GetString()!;
            Assert.True(HexU64(v, "hash") == PS5PathHash.HashPath(path), path);
        }
    }

    [Fact]
    public void SHA3_and_CRC32C_match_the_oracle()
    {
        foreach (JsonElement v in Vectors("digests").EnumerateArray())
        {
            byte[] input = Pattern(v.GetProperty("length").GetInt32());
            Assert.Equal(Hex(v, "sha3_256"), SHA3256.HashData(input));
            Assert.Equal(HexU64(v, "crc32c"), Crc32C.Update(0, input));
        }
    }

    [Fact]
    public void Outer_block_XTS_matches_the_oracle()
    {
        JsonElement keys = Vectors("keys")[0];
        byte[] ekpfs = PS5Keys.DeriveEkpfs(keys.GetProperty("content_id").GetString()!, keys.GetProperty("passcode").GetString()!);
        using XtsAes xts = new(PS5Keys.XtsKey(ekpfs, Hex(keys, "seed")));
        foreach (JsonElement v in Vectors("outer_xts").EnumerateArray())
        {
            ulong sector = PS5Keys.OuterBlockSector(v.GetProperty("block").GetInt32(), v.GetProperty("signed").GetBoolean());
            Assert.Equal(HexU64(v, "sector"), sector);
            byte[] block = Pattern(0x10000);
            xts.Encrypt(block, sector);
            Assert.Equal(Hex(v, "ciphertext_head"), block[..64]);
            Assert.Equal(Hex(v, "ciphertext_sha256"), SHA256.HashData(block));
        }
    }

    [Fact]
    public void RSA_key_wrap_matches_the_oracle_hook()
    {
        foreach (JsonElement v in Vectors("rsa_pkcs1").EnumerateArray())
        {
            byte[] produced = RSAKeyWrap.Encrypt(Hex(v, "modulus"), Hex(v, "exponent"), Hex(v, "message"), Hex(v, "seed"));
            Assert.Equal(Hex(v, "ciphertext"), produced);
        }
    }

    [Fact]
    public void Oracle_fself_and_dds_vectors_are_present()
    {
        // Consumed in F3; checked here so a corpus without them fails early.
        foreach (string section in new[] { "fself", "dds" })
        {
            foreach (JsonElement v in Vectors(section).EnumerateArray())
            {
                Assert.False(v.TryGetProperty("error", out _), $"{section} {v.GetProperty("source").GetString()}");
                Assert.True(File.Exists(Fixtures.PathOrSkip("fpkg", "vectors", v.GetProperty("vector").GetString()!)));
            }
        }
    }
}
