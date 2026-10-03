using System.Buffers.Binary;
using System.Text;
using MkPFS.Cli;
using MkPFS.Cli.Commands;
using MkPFS.Cli.Output;
using MkPFS.Core.Crypto;
using MkPFS.Core.Exfat;
using MkPFS.Core.PFS;
using MkPFS.Core.Util;

namespace MkPFS.Tests.PFS;

/// <summary>Read-side building blocks that need no fixture corpus.</summary>
public sealed class ReaderUnitTests
{
    [Fact]
    public void Xts_matches_IEEE_1619_vector_2_and_round_trips()
    {
        byte[] key = [.. Enumerable.Repeat((byte)0x11, 16), .. Enumerable.Repeat((byte)0x22, 16)];
        byte[] data = [.. Enumerable.Repeat((byte)0x44, 32)];
        using XtsAes xts = new(key);
        xts.Encrypt(data, 0x3333333333);
        Assert.Equal("c454185e6a16936e39334038acef838bfb186fff7480adc4289382ecd6d394f0", Convert.ToHexStringLower(data));
        xts.Decrypt(data, 0x3333333333);
        Assert.All(data, b => Assert.Equal(0x44, b));
    }

    [Fact]
    public void Xts_matches_python_cryptography_on_a_full_PFS_sector()
    {
        byte[] sector = new byte[4096];
        for (int i = 0; i < sector.Length; i++)
        {
            sector[i] = (byte)i;
        }

        using XtsAes xts = new([.. Enumerable.Range(0, 32).Select(i => (byte)i)]);
        xts.Encrypt(sector, 7);
        Assert.Equal("56776aa2d3d9badad526c90b4b5db8c072f68b27481b5224d3cd8f2974e18220", Convert.ToHexStringLower(sector.AsSpan(0, 32)));
        Assert.Equal("a37b487dec596eae250ff25e46d221923b49b9d71fbbfacf4c1510c829c4bcaf", Convert.ToHexStringLower(sector.AsSpan(4064)));
    }

    [Fact]
    public void Key_derivation_matches_python()
    {
        byte[] zero = new byte[32];
        byte[] seed = new byte[16];
        Assert.Equal("1ea6f351aa8eba9de7a8662a687c97ff13661e2b683985466569a7be6c10940d", Convert.ToHexStringLower(PFSKeys.XtsKey(zero, seed)));
        Assert.Equal("14e08a26b9822aa319f5ad0d3a72e2bc3761268705c0fd758fb3cd2f115d4595", Convert.ToHexStringLower(PFSKeys.SignKey(zero, seed)));
        Assert.NotEqual(PFSKeys.XtsKey(zero, seed), PFSKeys.XtsKey(zero, seed, newCrypt: true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_ekpfs_key_is_all_zeros(string? hex) => Assert.Equal(new byte[32], PFSKeys.ParseEkpfsHex(hex));

    [Theory]
    [InlineData("00")]
    [InlineData("zz112233445566778899aabbccddeeff00112233445566778899aabbccddeeff")]
    public void Invalid_ekpfs_key_is_rejected(string hex) => Assert.Throws<FormatException>(() => PFSKeys.ParseEkpfsHex(hex));

    private static byte[] Dirent(uint inode, int type, string name, int? entrySize = null, int? nameLength = null)
    {
        int size = entrySize ?? ((name.Length + 17 + 7) / 8 * 8);
        byte[] bytes = new byte[Math.Max(size, 16 + name.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, inode);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), type);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), nameLength ?? name.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), size);
        Encoding.Latin1.GetBytes(name).CopyTo(bytes, 16);
        return bytes;
    }

    [Fact]
    public void Dirents_parse_until_the_zero_terminator()
    {
        byte[] blob = [.. Dirent(2, 4, "."), .. Dirent(2, 5, ".."), .. Dirent(7, 2, "eboot.bin"), .. new byte[32]];
        (List<PFSDirent> entries, List<string> errors) = PFSDirent.ParseAll(blob, strict: true);

        Assert.Empty(errors);
        Assert.Equal([new PFSDirent(2, 4, "."), new PFSDirent(2, 5, ".."), new PFSDirent(7, 2, "eboot.bin")], entries);
    }

    [Fact]
    public void Malformed_dirents_stop_parsing_and_report_in_strict_mode()
    {
        Assert.Equal(["invalid dirent size 20 at offset 0"], PFSDirent.ParseAll(Dirent(1, 2, "a", entrySize: 20), strict: true).Errors);
        Assert.Equal(["invalid dirent name length 99 at offset 0"], PFSDirent.ParseAll(Dirent(1, 2, "a", nameLength: 99), strict: true).Errors);
        Assert.Equal(["dirent at offset 0 exceeds payload boundary"], PFSDirent.ParseAll(Dirent(1, 2, "a", entrySize: 64)[..24], strict: true).Errors);
        Assert.Empty(PFSDirent.ParseAll(Dirent(1, 2, "a", entrySize: 20), strict: false).Errors);

        (List<PFSDirent> entries, List<string> errors) = PFSDirent.ParseAll(Dirent(1, 2, "café"), strict: true);
        Assert.Equal(["non-ascii dirent name at offset 0"], errors);
        Assert.Equal("caf�", entries[0].Name);
    }

    [Fact]
    public void Inode_blob_size_is_checked_per_layout()
    {
        Assert.Throws<InvalidDataException>(() => PFSInode.Parse(new byte[0xA7], 0, signed: false));
        Assert.Throws<InvalidDataException>(() => PFSInode.Parse(new byte[0xA8], 0, signed: true));
        Assert.Equal(0, PFSInode.Parse(new byte[0x2C8], 0, signed: true, inodeBits: 32).Db[0]);
        Assert.Equal(12, PFSInode.Parse(new byte[0x310], 0, signed: true, inodeBits: 64).DbSig.Length);
    }

    [Theory]
    [InlineData("PPSA12345-app.exfat", "PPSA12345.exfat")]
    [InlineData("UP0000-PPSA12345_00-0000000000000000.exfat", "PPSA12345.exfat")]
    [InlineData("CUSA-12345 game.pkg", "CUSA-12345.pkg")]
    [InlineData("My Game (EU) v1.00.EXFAT", "My_Game_EU_v1.00.exfat")]
    [InlineData("in.exfat", "in.exfat")]
    [InlineData("a_very_long_game_name_here.exfat", "a_very_long_gam.exfat")]
    [InlineData("--__  .exfat", "FALLBACK.exfat")]
    public void Single_file_inner_name_follows_python_rules(string source, string expected) =>
        Assert.Equal(expected, SingleFileName.Resolve(source, fallbackStem: () => "FALLBACK"));

    [Fact]
    public void Single_file_suffixes_follow_pathlib()
    {
        Assert.Equal([".tar", ".gz"], SingleFileName.Suffixes("a.tar.gz"));
        Assert.Empty(SingleFileName.Suffixes("name."));
        Assert.Empty(SingleFileName.Suffixes(".hidden"));
        Assert.Equal("keep me.bin", SingleFileName.Resolve("keep me.bin", rename: false));
    }

    [Fact]
    public void Flat_path_table_hash_matches_python()
    {
        // Python: fpt_hash("/sce_sys/param.json") with and without case folding.
        Assert.Equal(FlatPathTable.Hash("/SCE_SYS/PARAM.JSON", caseInsensitive: false), FlatPathTable.Hash("/sce_sys/param.json"));
        Assert.NotEqual(FlatPathTable.Hash("/a", caseInsensitive: false), FlatPathTable.Hash("/A", caseInsensitive: false));
        Assert.Equal(FlatPathTable.Hash("/col/dataAZ.bin"), FlatPathTable.Hash("/col/dataB;.bin"));
        Assert.Equal((uint)(('/' * 31) + 'A'), FlatPathTable.Hash("/a"));
    }

    [Fact]
    public void Crc32_matches_zlib()
    {
        Assert.Equal(0xCBF43926u, Crc32.Update(0, "123456789"u8));
        Assert.Equal(Crc32.Update(0, "123456789"u8), Crc32.Update(Crc32.Update(0, "1234"u8), "56789"u8));
    }

    [Fact]
    public void Tree_renderer_puts_directories_first_then_case_insensitive_names()
    {
        Dictionary<long, List<PFSDirent>> dirents = new()
        {
            [2] = [new(2, 4, "."), new(2, 5, ".."), new(5, 2, "b.txt"), new(6, 2, "A.txt"), new(3, 3, "zdir"), new(7, 2, "a.txt")],
            [3] = [new(3, 4, "."), new(2, 5, ".."), new(8, 2, "inner")],
        };
        Assert.Equal(["|-- zdir", "|   `-- inner", "|-- A.txt", "|-- a.txt", "`-- b.txt"], TreeRenderer.RenderPFS(dirents, 2));
    }

    [Fact]
    public void Json_output_matches_python_json_dumps()
    {
        string json = PythonJson.Dump(new Dictionary<string, object?>
        {
            ["warnings"] = new List<string> { "café \"x\"\\" },
            ["errors"] = new List<string>(),
            ["version"] = (long?)2,
            ["block_size"] = null,
            ["has_header"] = true,
        });
        Assert.Equal("{\n  \"block_size\": null,\n  \"errors\": [],\n  \"has_header\": true,\n  \"version\": 2,\n  \"warnings\": [\n    \"caf\\u00e9 \\\"x\\\"\\\\\"\n  ]\n}", json);
    }

    [Theory]
    [InlineData(20130315L, "PFS (20130315)")]
    [InlineData(0x43534650L, "PFSC (0x43534650)")]
    [InlineData(255L, "0x00000000000000FF")]
    [InlineData(-1L, "0x-000000000000001")]
    public void Magic_values_are_described_like_python(long magic, string expected) =>
        Assert.Equal(expected, ReadCommands.DescribeMagic(magic));

    private static (int Exit, string Out, string Err) RunCli(params string[] args)
    {
        StringWriter stdout = new() { NewLine = "\n" };
        StringWriter stderr = new() { NewLine = "\n" };
        int exit = MkPFSCli.Run(args, new CliContext(stdout, stderr, useColor: false, utf8: false, progress: false));
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void Cli_rejects_invalid_argument_combinations_with_exit_2()
    {
        using TempDir temp = new();
        string image = temp.File("x.ffpfs", "not an image");
        string existing = temp.Dir("out");

        Assert.Equal((2, "--only requires --deep (it selects entries inside the wrapped exFAT)\n", string.Empty), RunCli("unpack", image, Path.Combine(temp.Path, "o2"), "--only", "data"));
        Assert.Equal((2, $"output path {existing} exists (use --overwrite to force)\n", string.Empty), RunCli("unpack", image, existing));
        Assert.Equal(2, RunCli("verify", image, "--source-dir", temp.Path, "--source-file", image).Exit);
        Assert.Equal((2, "--expect-crc32 must be a 32-bit hex value\n", string.Empty), RunCli("verify", image, "--expect-crc32", "0x123456789"));
        Assert.Equal((2, "--expect-manifest-sha256 must be a 64-hex SHA256 digest\n", string.Empty), RunCli("verify", image, "--expect-manifest-sha256", "abc"));
        Assert.Equal(2, RunCli("inspect", image, "--ekpfs-key", "123").Exit);
    }

    [Fact]
    public void Cli_reports_errors_for_non_PFS_input()
    {
        using TempDir temp = new();
        string image = temp.File("x.ffpfs", new string('x', 2048));

        (int exit, string stdout, string stderr) = RunCli("inspect", image, "--format", "json");
        Assert.Equal(1, exit);
        Assert.Contains("\"has_header\": true", stdout, StringComparison.Ordinal);
        Assert.Contains("failed to parse inode table: truncated read", stdout, StringComparison.Ordinal);
        Assert.Equal(string.Empty, stderr);

        (exit, _, stderr) = RunCli("tree", image);
        Assert.Equal(1, exit);
        Assert.StartsWith("ERROR ", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Tree_of_a_folder_lists_directories_first()
    {
        using TempDir temp = new();
        temp.File("b.txt");
        temp.File("Zdir/inner.txt");
        temp.File("a.txt");

        (int exit, string stdout, _) = RunCli("tree", temp.Path);
        Assert.Equal(0, exit);
        Assert.EndsWith("/\n|-- Zdir\n|   `-- inner.txt\n|-- a.txt\n`-- b.txt\n", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Exfat_reader_rejects_non_exfat_volumes()
    {
        using MemoryStream small = new(new byte[100]);
        Assert.Equal("source too small for an exFAT boot sector", Assert.Throws<InvalidDataException>(() => new ExfatReader(small)).Message);
        using MemoryStream unsigned = new(new byte[512]);
        Assert.Equal("missing exFAT file system signature", Assert.Throws<InvalidDataException>(() => new ExfatReader(unsigned)).Message);
    }
}
