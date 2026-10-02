using System.Buffers.Binary;
using System.Text.Json;
using MkPFS.Core.PFS;
using MkPFS.Core.PFSC;
using MkPFS.Repair;
using static MkPFS.Tests.Repair.RepairImageFactory;

namespace MkPFS.Tests.Repair;

public sealed class PFSCRepairTests
{
    // Blocks: 0 zlib text, 1 risky (far distance), 2 raw noise, 3 risky, 4 zlib text (partial last block).
    private static (byte[] Image, byte[] Logical, long NestedSize) Sample(bool compressibleRisky = false)
    {
        (byte[] risky1, byte[] decoded1) = FarDistance(compressibleRisky ? Text(11, 32768) : Noise(11)[..32768]);
        (byte[] risky3, byte[] decoded3) = FarDistance(compressibleRisky ? Text(13, 32768) : Noise(13)[..32768]);
        byte[] text0 = Text(1);
        byte[] noise2 = Noise(2);
        byte[] text4 = Text(4);
        text4.AsSpan(40000).Clear();
        long nestedSize = (4L * BlockSize) + 40000;
        byte[] image = BuildImage([ZlibBlock(text0), risky1, noise2, risky3, ZlibBlock(text4)], nestedSize);
        return (image, [.. text0, .. decoded1, .. noise2, .. decoded3, .. text4], nestedSize);
    }

    private static string Write(TempDir dir, byte[] image, string name = "game.ffpfsc")
    {
        string path = Path.Combine(dir.Path, name);
        File.WriteAllBytes(path, image);
        return path;
    }

    private static PFSCRepairOptions Options(RepairMode mode = RepairMode.Copy) => new() { Mode = mode, Workers = 2 };

    [Fact]
    public void Open_reads_the_fixed_wrapper()
    {
        using TempDir dir = new();
        (byte[] image, _, long nestedSize) = Sample();
        using PFSCImage pfsc = PFSCImage.Open(Write(dir, image));

        Assert.Equal(6L * BlockSize, pfsc.FileStart);
        Assert.Equal(5, pfsc.BlockCount);
        Assert.Equal(nestedSize, pfsc.NestedSize);
        Assert.Equal("in.exfat", pfsc.NestedName);
        Assert.Equal(PFSCNestedType.Exfat, pfsc.NestedType);
        Assert.Equal(BlockSize, pfsc.StoredLength(2));
        Assert.Equal(40000, pfsc.CompareLength(4));
    }

    [Fact]
    public void Scan_flags_only_far_distance_blocks_and_leaves_the_file_alone()
    {
        using TempDir dir = new();
        (byte[] image, _, _) = Sample();
        string path = Write(dir, image);

        PFSCRepairResult result = PFSCRepair.Run(path, Options() with { ScanOnly = true }, TestContext.Current.CancellationToken);

        Assert.Equal(RepairStatus.RepairNeeded, result.Status);
        Assert.Equal([1L, 3L], result.MarkedBlocks);
        Assert.Equal(2, result.RiskyBlocks);
        Assert.Equal(4, result.CompressedBlocks);
        using (PFSCImage before = PFSCImage.Open(path))
        {
            Assert.Equal(result.OldStoredSize + (BlockSize - before.StoredLength(1)) + (BlockSize - before.StoredLength(3)), result.NewStoredSize);
        }

        Assert.Equal(image, File.ReadAllBytes(path));
        Assert.True(result.Slack!.Value.Applicable);
    }

    [Fact]
    public void Copy_and_in_place_repairs_produce_identical_verified_images()
    {
        using TempDir dir = new();
        (byte[] image, byte[] logical, _) = Sample();
        string copy = Write(dir, image, "copy.ffpfsc");
        string inPlace = Write(dir, image, "inplace.ffpfsc");

        PFSCRepairResult a = PFSCRepair.Run(copy, Options(RepairMode.Copy), TestContext.Current.CancellationToken);
        PFSCRepairResult b = PFSCRepair.Run(inPlace, Options(RepairMode.InPlace), TestContext.Current.CancellationToken);

        Assert.Equal(RepairStatus.Repaired, a.Status);
        Assert.Equal(RepairStatus.Repaired, b.Status);
        Assert.Equal(RepairMode.Copy, a.AppliedMode);
        Assert.Equal(RepairMode.InPlace, b.AppliedMode);
        Assert.Equal(5, a.PostVerifyBlocks);
        Assert.Equal(File.ReadAllBytes(copy), File.ReadAllBytes(inPlace));
        Assert.False(File.Exists(copy + PFSCRepair.TempSuffix));
        Assert.Equal(logical, DecodePayload(copy));

        using (PFSCImage repaired = PFSCImage.Open(copy))
        {
            Assert.Equal(BlockSize, repaired.StoredLength(1));
            Assert.Equal(BlockSize, repaired.StoredLength(3));
            Assert.Equal(a.NewStoredSize, repaired.StoredSize);
            Assert.Equal(a.NewStoredSize, repaired.Offsets[^1]);
        }

        PFSInspection inspection = PFSInspector.Inspect(copy, new PFSInspectOptions { Checklist = ChecklistMode.Never });
        Assert.Empty(inspection.Errors);
        Assert.Equal(RepairStatus.Noop, PFSCRepair.Run(copy, Options(), TestContext.Current.CancellationToken).Status);
    }

    [Fact]
    public void Recompress_stores_zlib_blocks_and_requires_copy_when_the_payload_shrinks()
    {
        using TempDir dir = new();
        (byte[] image, byte[] logical, _) = Sample(compressibleRisky: true);
        string inPlace = Write(dir, image, "inplace.ffpfsc");
        string copy = Write(dir, image, "copy.ffpfsc");

        PFSCRepairResult refused = PFSCRepair.Run(inPlace, Options(RepairMode.InPlace) with { Recompress = true }, TestContext.Current.CancellationToken);
        PFSCRepairResult repaired = PFSCRepair.Run(copy, Options(RepairMode.Copy) with { Recompress = true }, TestContext.Current.CancellationToken);

        Assert.Equal(RepairStatus.Failed, refused.Status);
        Assert.Contains("in-place", refused.Error, StringComparison.Ordinal);
        Assert.Equal(image, File.ReadAllBytes(inPlace));
        Assert.Equal(RepairStatus.Repaired, repaired.Status);
        Assert.True(repaired.NewStoredSize < repaired.OldStoredSize);
        Assert.Equal(logical, DecodePayload(copy));
        using PFSCImage result = PFSCImage.Open(copy);
        Assert.True(result.StoredLength(1) < BlockSize);
        Assert.True(result.StoredLength(3) < BlockSize);
    }

    [Fact]
    public void Auto_mode_falls_back_to_in_place_without_free_space()
    {
        using TempDir dir = new();
        string path = Write(dir, Sample().Image);

        PFSCRepairResult result = PFSCRepair.Run(path, Options(RepairMode.Auto) with { FreeSpace = _ => 0 }, TestContext.Current.CancellationToken);

        Assert.Equal(RepairStatus.Repaired, result.Status);
        Assert.Equal(RepairMode.InPlace, result.AppliedMode);
        Assert.Equal(PFSCRepair.RequiredFreeBytes(Sample().Image.Length), result.RequiredFreeBytes);
    }

    [Fact]
    public void Bad_block_list_selects_blocks_like_game_compressor()
    {
        using TempDir dir = new();
        (byte[] image, byte[] logical, _) = Sample();
        string path = Write(dir, image);
        string list = dir.File("bad_blocks.tsv", BadBlockList.Header + "\n0\t123\t65536\t17\tabc\tdef\n0\n");

        PFSCRepairResult result = PFSCRepair.Run(path, Options() with { BadBlocksPath = list }, TestContext.Current.CancellationToken);

        Assert.Equal(RepairStatus.Repaired, result.Status);
        Assert.Equal("bad-blocks", result.Selection);
        Assert.Equal([0L], result.MarkedBlocks);
        Assert.Equal(logical, DecodePayload(path));
        using PFSCImage repaired = PFSCImage.Open(path);
        Assert.Equal(BlockSize, repaired.StoredLength(0));
        Assert.True(repaired.StoredLength(1) < BlockSize);
    }

    [Fact]
    public void Bad_block_list_rejects_raw_out_of_range_and_empty_lists()
    {
        using TempDir dir = new();
        string path = Write(dir, Sample().Image);

        PFSCRepairResult raw = PFSCRepair.Run(path, Options() with { BadBlocksPath = dir.File("raw.tsv", "2\n") }, TestContext.Current.CancellationToken);
        Assert.Equal(RepairStatus.Failed, raw.Status);
        Assert.Contains("already stored raw", raw.Error, StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => PFSCRepair.Run(path, Options() with { BadBlocksPath = dir.File("range.tsv", "5\n") }, TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => PFSCRepair.Run(path, Options() with { BadBlocksPath = dir.File("empty.tsv", BadBlockList.Header + "\n") }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Matching_vhash_is_used_and_stays_valid_after_repair()
    {
        using TempDir dir = new();
        (byte[] image, byte[] logical, long nestedSize) = Sample();
        string path = Write(dir, image);
        WriteVHash(path, logical, nestedSize);

        PFSCRepairResult result = PFSCRepair.Run(path, Options(), TestContext.Current.CancellationToken);

        Assert.Equal(RepairStatus.Repaired, result.Status);
        Assert.Equal(PFSCVHashMode.Used, result.HashMode);
        using PFSCImage repaired = PFSCImage.Open(path);
        Assert.Equal(PFSCVHashMode.Used, PFSCVHash.Probe(PFSCVHash.SidecarPath(path), repaired.VHashIdentity));
    }

    [Fact]
    public void Vhash_mismatch_fails_without_writing()
    {
        using TempDir dir = new();
        (byte[] image, byte[] logical, long nestedSize) = Sample();
        string path = Write(dir, image);
        logical[(2 * BlockSize) + 5] ^= 0xFF;
        WriteVHash(path, logical, nestedSize);

        PFSCRepairResult result = PFSCRepair.Run(path, Options(), TestContext.Current.CancellationToken);

        Assert.Equal(RepairStatus.Failed, result.Status);
        Assert.Equal([2L], result.HashMismatches);
        Assert.Equal(image, File.ReadAllBytes(path));
    }

    [Fact]
    public void Undecodable_block_fails_without_writing()
    {
        using TempDir dir = new();
        byte[] image = Sample().Image;
        image[(6 * BlockSize) + 0x10000 + 10] ^= 0xFF; // inside block 0's zlib stream
        string path = Write(dir, image);

        PFSCRepairResult result = PFSCRepair.Run(path, Options(), TestContext.Current.CancellationToken);

        Assert.Equal(RepairStatus.Failed, result.Status);
        Assert.Equal(0, Assert.Single(result.DecodeErrors).Block);
        Assert.Equal(image, File.ReadAllBytes(path));
    }

    [Fact]
    public void Clean_image_is_a_noop_and_slack_is_cleared()
    {
        using TempDir dir = new();
        byte[] image = BuildImage([ZlibBlock(Text(1)), Noise(2)], 2L * BlockSize);
        image[(4 * BlockSize) + 7] = 0xAA; // collision block
        image[(2 * BlockSize) + 60000] = 0xBB; // superroot tail
        image[^1] = 0xCC; // image tail after the payload
        string path = Write(dir, image);

        PFSCRepairResult scan = PFSCRepair.Run(path, Options() with { ScanOnly = true }, TestContext.Current.CancellationToken);
        Assert.Equal(RepairStatus.Noop, scan.Status);
        Assert.Equal(3, scan.Slack!.Value.FixedBytes);
        Assert.Equal(image, File.ReadAllBytes(path));

        PFSCRepairResult result = PFSCRepair.Run(path, Options(), TestContext.Current.CancellationToken);
        Assert.Equal(RepairStatus.Noop, result.Status);
        Assert.Equal(3, result.Slack!.Value.FixedBytes);
        Assert.Equal(0, OuterSlack.Clean(path).FixedBytes);
    }

    [Fact]
    public void Slack_cleanup_skips_images_outside_the_fixed_wrapper()
    {
        using TempDir dir = new();
        byte[] image = BuildImage([ZlibBlock(Text(1))], BlockSize);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(BlockSize + (2 * 0xA8)), 0x41C0); // odd uroot mode
        image[4 * BlockSize] = 1;
        string path = Write(dir, image);

        OuterSlackResult result = OuterSlack.Clean(path);

        Assert.False(result.Applicable);
        Assert.Contains("inodes", result.Reason, StringComparison.Ordinal);
        Assert.Equal(image, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(0x1)] // signed
    [InlineData(0x2)] // 64-bit inodes
    [InlineData(0x4)] // encrypted
    public void Unsupported_header_modes_are_rejected(ushort flag)
    {
        using TempDir dir = new();
        byte[] image = Sample().Image;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x1C), (ushort)(0x8 | flag));
        string path = Write(dir, image);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => PFSCRepair.Run(path, Options(), TestContext.Current.CancellationToken));
        Assert.Contains("unsigned, unencrypted", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Images_with_data_after_the_payload_are_refused()
    {
        using TempDir dir = new();
        byte[] image = [.. Sample().Image, .. new byte[BlockSize]];
        BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(0x38), image.Length / BlockSize);
        string path = Write(dir, image);

        PFSCRepairResult result = PFSCRepair.Run(path, Options(), TestContext.Current.CancellationToken);

        Assert.Equal(RepairStatus.Failed, result.Status);
        Assert.Contains("last region", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Reports_use_game_compressor_formats()
    {
        using TempDir dir = new();
        string path = Write(dir, Sample().Image);
        string reports = Path.Combine(dir.Path, "reports");

        PFSCRepairResult result = PFSCRepair.Run(path, Options() with { ScanOnly = true, ReportDirectory = reports }, TestContext.Current.CancellationToken);

        string tsv = Path.Combine(reports, "bad_blocks.tsv");
        string[] lines = File.ReadAllLines(tsv);
        Assert.Equal(BadBlockList.Header, lines[0]);
        Assert.StartsWith("1\t", lines[1], StringComparison.Ordinal);
        Assert.Equal(result.MarkedBlocks, BadBlockList.Read(tsv, result.BlockCount));

        using JsonDocument summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(reports, "summary.json")));
        Assert.Equal("repair-needed", summary.RootElement.GetProperty("status").GetString());
        Assert.Equal("missing", summary.RootElement.GetProperty("hashMode").GetString());
        Assert.Equal(2, summary.RootElement.GetProperty("repairedBlocks").GetInt64());
    }

    private static void WriteVHash(string imagePath, byte[] logical, long nestedSize)
    {
        long blocks = logical.Length / BlockSize;
        byte[] hashes = new byte[blocks * PFSCVHash.HashSize];
        for (int i = 0; i < blocks; i++)
        {
            int length = (int)Math.Min(BlockSize, nestedSize - ((long)i * BlockSize));
            PFSCVHash.HashBlock(logical.AsSpan(i * BlockSize, length)).CopyTo(hashes, i * PFSCVHash.HashSize);
        }

        PFSCVHash.Write(PFSCVHash.SidecarPath(imagePath), new PFSCVHashIdentity(logical.Length, nestedSize, blocks, "in.exfat", PFSCNestedType.Exfat), hashes);
    }
}

public sealed class RepairCliTests
{
    private static (int Exit, string Out) RunCli(params string[] args)
    {
        StringWriter stdout = new() { NewLine = "\n" };
        StringWriter stderr = new() { NewLine = "\n" };
        int exit = MkPFS.Cli.MkPFSCli.Run(args, new MkPFS.Cli.Output.CliContext(stdout, stderr, useColor: false, utf8: false, progress: false));
        return (exit, stdout.ToString() + stderr.ToString());
    }

    [Fact]
    public void Scan_exits_3_then_repair_exits_0_and_rescan_exits_0()
    {
        using TempDir dir = new();
        string path = Path.Combine(dir.Path, "game.ffpfsc");
        (byte[] risky, _) = FarDistance(Noise(5)[..32768]);
        File.WriteAllBytes(path, BuildImage([ZlibBlock(Text(1)), risky], 2L * BlockSize));

        (int scanExit, string scanOut) = RunCli("repair", path, "--scan");
        (int repairExit, string repairOut) = RunCli("repair", path, "--mode", "copy");
        (int rescanExit, string rescanOut) = RunCli("repair", path, "--scan");

        Assert.Equal(3, scanExit);
        Assert.Contains("1 block(s) need repair: 1", scanOut, StringComparison.Ordinal);
        Assert.Equal(0, repairExit);
        Assert.Contains("Repaired 1 block(s) (stored raw, copy-replace): 1", repairOut, StringComparison.Ordinal);
        Assert.Equal(0, rescanExit);
        Assert.Contains("No blocks need repair.", rescanOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_warns_about_risky_blocks_and_points_to_repair()
    {
        using TempDir dir = new();
        string risky = Path.Combine(dir.Path, "risky.ffpfsc");
        string clean = Path.Combine(dir.Path, "clean.ffpfsc");
        (byte[] far, _) = FarDistance(Noise(5)[..32768]);
        File.WriteAllBytes(risky, BuildImage([ZlibBlock(Text(1)), far, far], 3L * BlockSize));
        File.WriteAllBytes(clean, BuildImage([ZlibBlock(Text(1))], BlockSize));

        (int riskyExit, string riskyOut) = RunCli("verify", risky);
        (int cleanExit, string cleanOut) = RunCli("verify", clean);

        Assert.Equal(0, riskyExit);
        Assert.Contains($"WARN PFSC stream check: 2 compressed block(s) in in.exfat use back-references the PS5 may decode wrongly; run 'mkpfs repair \"{risky}\"' to rewrite them", riskyOut, StringComparison.Ordinal);
        Assert.Contains("Warnings:              0", riskyOut, StringComparison.Ordinal);
        Assert.Equal(0, cleanExit);
        Assert.DoesNotContain("PFSC stream check", cleanOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Unsupported_image_exits_1()
    {
        using TempDir dir = new();
        string path = dir.File("x.ffpfsc", "not an image");

        (int exit, string output) = RunCli("repair", path);

        Assert.Equal(1, exit);
        Assert.Contains("Repair failed:", output, StringComparison.Ordinal);
    }
}
