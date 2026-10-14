using System.Text;
using MkPFS.Build;
using MkPFS.Build.AMPRPack;
using MkPFS.Cli;
using MkPFS.Cli.Output;
using MkPFS.Core.AMPR;

namespace MkPFS.Tests.AMPR;

/// <summary>Files the PS5 needs on disk (empty files, PlayGo markers, Yotei boot files) stay loose (not in ampr_pack.py).</summary>
public sealed class AMPRDiskFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mkpfs-ampr-disk-" + Guid.NewGuid().ToString("N"));

    public AMPRDiskFilesTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Root => Path.Combine(_dir, "app0");

    private string Output => Path.Combine(_dir, "out");

    private string Index => Path.Combine(Output, "ampr_assets.index");

    private static readonly string[] DiskFiles = ["cache_ps5/pgc_0001_dummy_file", "cache_ps5/playgo.pgm", "data/empty.dat"];

    private static byte[] Text(int size, int seed) =>
        Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(seed, (size / 8) + 1).Select(i => $"blk{i % 97:D4} ")))[..size];

    // Packs data/** and cache_ps5/**: one real asset and the three files the PS5 checks on disk.
    private AMPRBuildResult Build(bool keepOnDisk)
    {
        Dictionary<string, byte[]> files = new()
        {
            ["eboot.bin"] = Text(3000, 1),
            ["data/asset.dat"] = Text(150_000, 2),
            ["data/empty.dat"] = [],
            ["cache_ps5/pgc_0001_dummy_file"] = Text(5000, 3),
            ["cache_ps5/playgo.pgm"] = Text(9000, 4),
        };
        foreach ((string path, byte[] data) in files)
        {
            string full = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, data);
            File.SetLastWriteTimeUtc(full, DateTime.UnixEpoch.AddSeconds(1_600_000_000));
        }

        AmprIndex.Build(Root, Path.Combine(Root, AmprIndex.IndexName));
        string config = Path.Combine(_dir, "config.toml");
        File.WriteAllText(config, "[pack]\ndefault_action = \"loose\"\n[[rule]]\ninclude = [\"data/**\", \"cache_ps5/**\"]\n", new UTF8Encoding(false));
        AMPRPackConfig loaded = AMPRPackConfig.Load(config);
        loaded.KeepOnDisk = keepOnDisk;
        return AMPRPackBuilder.Build(Root, Path.Combine(Root, AmprIndex.IndexName), Output, loaded);
    }

    private List<string> Packed() =>
        [.. AMPRPackTools.List(Index).Where(e => e.Packed).Select(e => AMPRAssetPath.Relative(e.Path)).Order(StringComparer.Ordinal)];

    [Theory]
    [InlineData("data/empty.dat", 0UL, true)]
    [InlineData("cache_ps5/pgc_0001_dummy_file", 10UL, true)]
    [InlineData("cache_ps5/playgo.pgm", 10UL, true)]
    [InlineData("cache_ps5/game.sprig.packman", 10UL, true)]
    [InlineData("sounds/scream/_generated/group_mix.gmx", 10UL, true)]
    [InlineData("/cache_ps5/playgo.pgm", 10UL, true)]
    [InlineData("CACHE_PS5/playgo.pgm", 10UL, false)]
    [InlineData("cache_ps5/pgc_0001.dat", 10UL, false)]
    [InlineData("data/cache_ps5/playgo.pgm", 10UL, false)]
    public void Disk_file_rule_matches_empty_files_and_the_listed_paths(string relative, ulong size, bool expected) =>
        Assert.Equal(expected, AMPRDiskFiles.MustStayOnDisk(relative, size));

    [Fact]
    public void Disk_files_stay_loose_and_are_reported()
    {
        AMPRBuildResult result = Build(keepOnDisk: true);

        Assert.Equal(["data/asset.dat"], Packed());
        Assert.Contains(
            "kept on disk: 3 files the PS5 reads outside the game (empty files, PlayGo markers, boot files) stay loose", result.Warnings);
        Assert.All(DiskFiles, path => Assert.Contains(path, result.Stats.LoosePaths));
    }

    [Fact]
    public void Without_the_rule_disk_files_are_packed_like_ampr_pack()
    {
        AMPRBuildResult result = Build(keepOnDisk: false);

        Assert.Equal([.. DiskFiles.Append("data/asset.dat").Order(StringComparer.Ordinal)], Packed());
        Assert.DoesNotContain(result.Warnings, w => w.StartsWith("kept on disk", StringComparison.Ordinal));
    }

    [Fact]
    public void Required_packed_does_not_fail_on_disk_files()
    {
        string config = Path.Combine(_dir, "required.toml");
        Build(keepOnDisk: true);
        File.WriteAllText(config, "[pack]\ndefault_action = \"loose\"\nrequired_packed = [\"data/*\"]\n[[rule]]\ninclude = [\"data/**\"]\n", new UTF8Encoding(false));

        AMPRBuildResult result = AMPRPackBuilder.Build(Root, Path.Combine(Root, AmprIndex.IndexName), Output, AMPRPackConfig.Load(config));

        Assert.Equal(1, result.Stats.FilesPacked);
    }

    [Fact]
    public void Remove_sources_leaves_disk_files_of_older_pack_sets()
    {
        Build(keepOnDisk: false);

        Assert.Equal(["data/asset.dat"], AMPRPackMaintenance.RemovalPlan(Index, Root).Paths);
        Assert.Equal(4, AMPRPackMaintenance.RemovalPlan(Index, Root, keepOnDisk: false).Files);

        AMPRRemovalResult removed = AMPRPackMaintenance.RemovePackedSources(Index, Root);

        Assert.Equal(1, removed.Files);
        Assert.False(File.Exists(Path.Combine(Root, "data", "asset.dat")));
        Assert.All(DiskFiles, path => Assert.True(File.Exists(Path.Combine(Root, path)), path));
    }

    [Fact]
    public void Restore_writes_missing_disk_files_back_from_the_packs()
    {
        Build(keepOnDisk: false);
        foreach (string path in DiskFiles)
        {
            File.Delete(Path.Combine(Root, path));
        }

        File.WriteAllBytes(Path.Combine(Root, "cache_ps5", "playgo.pgm"), [1, 2, 3]);

        AMPRDiskFilesResult check = AMPRPackTools.RestoreDiskFiles(Index, Root, restore: false);
        Assert.Equal((3L, 0L), (check.Missing, check.Restored));
        Assert.Equal(DiskFiles.Order(StringComparer.Ordinal), check.Paths.Order(StringComparer.Ordinal));
        Assert.False(File.Exists(Path.Combine(Root, "data", "empty.dat")));

        AMPRDiskFilesResult restored = AMPRPackTools.RestoreDiskFiles(Index, Root, restore: true);

        Assert.Equal((3L, 3L), (restored.Missing, restored.Restored));
        Assert.Equal(Text(5000, 3), File.ReadAllBytes(Path.Combine(Root, "cache_ps5", "pgc_0001_dummy_file")));
        Assert.Equal(Text(9000, 4), File.ReadAllBytes(Path.Combine(Root, "cache_ps5", "playgo.pgm")));
        Assert.Empty(File.ReadAllBytes(Path.Combine(Root, "data", "empty.dat")));
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(1_600_000_000), File.GetLastWriteTimeUtc(Path.Combine(Root, "cache_ps5", "playgo.pgm")));
        Assert.Empty(Directory.GetFiles(Root, "*.tmp-*", SearchOption.AllDirectories));
        Assert.Equal(0, AMPRPackTools.RestoreDiskFiles(Index, Root, restore: false).Missing);
    }

    [Fact]
    public void Cli_verify_restores_disk_files_and_pack_opts_out()
    {
        Build(keepOnDisk: true);
        string config = Path.Combine(_dir, "config.toml");
        StringWriter stdout = new() { NewLine = "\n" };
        StringWriter stderr = new() { NewLine = "\n" };
        CliContext ctx = new(stdout, stderr, useColor: false, utf8: false, progress: false);

        int packed = MkPFSCli.Run(
            ["ampr", "pack", "--root", Root, "--ampr-index", Path.Combine(Root, AmprIndex.IndexName), "--output", Output, "--config", config, "--no-keep-on-disk"], ctx);
        Assert.Equal(0, packed);
        Assert.Contains("\"files_packed\": 4,", stdout.ToString(), StringComparison.Ordinal);
        File.Delete(Path.Combine(Root, "data", "empty.dat"));

        stdout.GetStringBuilder().Clear();
        stderr.GetStringBuilder().Clear();
        int verified = MkPFSCli.Run(["ampr", "verify", "--index", Index, "--restore-disk-files", Root], ctx);

        Assert.Equal(0, verified);
        Assert.Contains("\"disk_files\": {", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"restored\": 1\n", stdout.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(Root, "data", "empty.dat")));
        Assert.Equal(string.Empty, stderr.ToString());
    }
}
