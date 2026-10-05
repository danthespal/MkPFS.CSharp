using System.Text;
using MkPFS.Build;
using MkPFS.Build.AMPRPack;
using MkPFS.Core.AMPR;

namespace MkPFS.Tests.AMPR;

/// <summary>Builder behavior the golden corpus does not cover: placement, sampling, republishing and failures.</summary>
public sealed class AMPRPackBuilderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mkpfs-ampr-builder-" + Guid.NewGuid().ToString("N"));

    public AMPRPackBuilderTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData(0, 5, new long[0])]
    [InlineData(3, 5, new long[] { 0, 1, 2 })]
    [InlineData(5, 5, new long[] { 0, 1, 2, 3, 4 })]
    [InlineData(10, 1, new long[] { 5 })]
    [InlineData(10, 3, new long[] { 0, 4, 9 })]
    [InlineData(7, 2, new long[] { 0, 6 })]
    [InlineData(1000, 7, new long[] { 0, 166, 333, 499, 666, 832, 999 })]
    public void Sample_block_indices_match_python(long total, long samples, long[] expected) =>
        Assert.Equal(expected, AMPRPackPlanner.SampleBlockIndices(total, samples));

    [Fact]
    public void Placement_keeps_small_chunks_inside_one_page_and_large_chunks_page_aligned()
    {
        AMPRPackConfig config = AMPRPackConfig.Load(null);
        AMPRPackLayout layout = new(config, _dir);
        AMPRGroupConfig group = new("default");
        try
        {
            AMPRPackVolumeWriter writer = layout.WriterFor(group, 0, 100, false, "mixed", false);
            Assert.Equal(65536, writer.PayloadOffset);

            (long first, int firstFlags) = writer.Write(new byte[60000], "mixed");
            Assert.Equal((65536L, AMPRPackFormat.ChunkFlagPageContained | AMPRPackFormat.ChunkFlagPageAligned), (first, firstFlags));

            // 60000 rounds up to 60032; 10000 more would cross the page at 131072, so it moves to the next page.
            (long second, _) = writer.Write(new byte[10000], "mixed");
            Assert.Equal(131072, second);

            // A full-page chunk starts on a page boundary; a streaming chunk stays dense at 64-byte alignment.
            (long third, int thirdFlags) = writer.Write(new byte[65536], "mixed");
            Assert.Equal((196608L, AMPRPackFormat.ChunkFlagPageContained | AMPRPackFormat.ChunkFlagPageAligned), (third, thirdFlags));
            (long fourth, int fourthFlags) = writer.Write(new byte[100], "streaming");
            Assert.Equal((262144L, AMPRPackFormat.ChunkFlagPageContained | AMPRPackFormat.ChunkFlagPageAligned), (fourth, fourthFlags));
            (long fifth, int fifthFlags) = writer.Write(new byte[65536], "streaming");
            Assert.Equal((262144L + 128, 0), (fifth, fifthFlags));
        }
        finally
        {
            layout.Abort();
        }
    }

    private string MakeTree(params (string Path, byte[] Data)[] files)
    {
        string root = Path.Combine(_dir, "app0");
        foreach ((string path, byte[] data) in files)
        {
            string full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, data);
        }

        AmprIndex.Build(root, Path.Combine(root, AmprIndex.IndexName));
        return root;
    }

    private string WriteConfig(string toml)
    {
        string path = Path.Combine(_dir, "config.toml");
        File.WriteAllText(path, toml, new UTF8Encoding(false));
        return path;
    }

    private static byte[] Text(int size) => Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("asset texture mesh ", (size / 19) + 1)))[..size];

    [Fact]
    public void Rebuild_replaces_the_set_removes_a_stale_runtime_and_leaves_no_temporaries()
    {
        string root = MakeTree(("data/a.txt", Text(200_000)), ("data/b.txt", Text(1000)));
        string index = Path.Combine(root, AmprIndex.IndexName);
        string output = Path.Combine(_dir, "out");
        AMPRPackConfig withRuntime = AMPRPackConfig.Load(WriteConfig(
            "[pack]\ndefault_action = \"compress\"\n[runtime]\ndecoded_cache_bytes = \"64MiB\"\nphysical_cache_bytes = 0\nworkers = 2\nlatency_reserve_workers = 0\n"));

        AMPRBuildResult first = AMPRPackBuilder.Build(root, index, output, withRuntime);
        Assert.True(File.Exists(first.IndexPath + ".runtime"));
        byte[] firstBuildId = AMPRPackManifest.Load(first.IndexPath).BuildId;

        AMPRPackConfig plain = AMPRPackConfig.Load(WriteConfig("[pack]\ndefault_action = \"compress\"\ndeduplicate = false\n"));
        AMPRBuildResult second = AMPRPackBuilder.Build(root, index, output, plain);

        Assert.False(File.Exists(second.IndexPath + ".runtime"));
        Assert.Equal(["ampr_assets-000.pak", "ampr_assets.index", "ampr_assets.index.crc"], Directory.GetFiles(output).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        AMPRPackManifest manifest = AMPRPackManifest.Load(second.IndexPath);
        Assert.Equal(2, manifest.Files.Count(f => f.IsPacked));
        Assert.NotEqual(firstBuildId, manifest.BuildId);
    }

    [Fact]
    public void Failed_build_keeps_the_previous_set_and_removes_temporaries()
    {
        string root = MakeTree(("data/a.txt", Text(100_000)), ("data/b.txt", Text(5000)));
        string index = Path.Combine(root, AmprIndex.IndexName);
        string output = Path.Combine(_dir, "out");
        AMPRPackConfig config = AMPRPackConfig.Load(WriteConfig("[pack]\ndefault_action = \"compress\"\n"));
        AMPRPackBuilder.Build(root, index, output, config);
        Dictionary<string, byte[]> before = Directory.GetFiles(output).ToDictionary(p => p, File.ReadAllBytes);

        File.Delete(Path.Combine(root, "data", "b.txt"));
        AMPRPackException error = Assert.Throws<AMPRPackException>(() => AMPRPackBuilder.Build(root, index, output, config));

        Assert.Equal("source is missing or unsafe for file id 2: data/b.txt", error.Message);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), Directory.GetFiles(output).Order(StringComparer.Ordinal));
        Assert.All(before, kv => Assert.Equal(kv.Value, File.ReadAllBytes(kv.Key)));
    }

    [Fact]
    public void Missing_source_is_left_loose_with_allow_missing()
    {
        string root = MakeTree(("data/a.txt", Text(100_000)), ("data/b.txt", Text(5000)));
        File.Delete(Path.Combine(root, "data", "b.txt"));
        AMPRPackConfig config = AMPRPackConfig.Load(WriteConfig("[pack]\ndefault_action = \"compress\"\n"));

        AMPRBuildResult result = AMPRPackBuilder.Build(root, Path.Combine(root, AmprIndex.IndexName), Path.Combine(_dir, "out"), config, allowMissing: true);

        Assert.Equal(["file id 2 left loose because source is missing: data/b.txt"], result.Warnings);
        Assert.Equal(["data/b.txt"], result.Stats.LoosePaths);
    }

    [Fact]
    public void Size_mismatch_with_the_index_is_rejected()
    {
        string root = MakeTree(("data/a.txt", Text(1000)));
        File.WriteAllBytes(Path.Combine(root, "data", "a.txt"), Text(999));
        AMPRPackConfig config = AMPRPackConfig.Load(WriteConfig("[pack]\ndefault_action = \"compress\"\n"));

        AMPRPackException error = Assert.Throws<AMPRPackException>(
            () => AMPRPackBuilder.Build(root, Path.Combine(root, AmprIndex.IndexName), Path.Combine(_dir, "out"), config));
        Assert.Equal("AMPRIDX3 size mismatch for data/a.txt: index=1000, disk=999", error.Message);
    }

    [Fact]
    public void Block_larger_than_the_volume_limit_is_rejected()
    {
        string root = MakeTree(("data/r.bin", [.. Enumerable.Range(0, 1 << 20).Select(i => (byte)(i * 2654435761U >> 24))]));
        AMPRPackConfig config = AMPRPackConfig.Load(WriteConfig(
            "[pack]\ndefault_action = \"store\"\ndefault_block_size = \"1MiB\"\n[groups.default]\nmax_pack_size = \"128KiB\"\n"));

        AMPRPackException error = Assert.Throws<AMPRPackException>(
            () => AMPRPackBuilder.Build(root, Path.Combine(root, AmprIndex.IndexName), Path.Combine(_dir, "out"), config));
        Assert.Equal("one block (1048576 bytes) exceeds max pack size for group default", error.Message);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "out")));
    }

    [Fact]
    public void Pattern_without_a_volume_field_fails_on_rollover()
    {
        string root = MakeTree(("data/a.bin", new byte[300_000]), ("data/b.bin", new byte[300_000]));
        AMPRPackConfig config = AMPRPackConfig.Load(WriteConfig(
            "[pack]\ndefault_action = \"store\"\npack_pattern = \"same.pak\"\ndeduplicate = false\n[groups.default]\nmax_pack_size = \"256KiB\"\n"));

        AMPRPackException error = Assert.Throws<AMPRPackException>(
            () => AMPRPackBuilder.Build(root, Path.Combine(root, AmprIndex.IndexName), Path.Combine(_dir, "out"), config));
        Assert.Equal("pack_pattern produced duplicate name: same.pak", error.Message);
    }

    [Fact]
    public void Previous_outputs_inside_the_root_stay_loose()
    {
        string root = MakeTree(("data/a.txt", Text(10_000)), ("ampr_assets.index", [1, 2, 3]), ("ampr_assets-000.pak", [4]), ("x/ampr_assets.index.crc", [5]));
        AMPRPackConfig config = AMPRPackConfig.Load(WriteConfig("[pack]\ndefault_action = \"compress\"\n"));

        AMPRBuildResult result = AMPRPackBuilder.Build(root, Path.Combine(root, AmprIndex.IndexName), Path.Combine(_dir, "out"), config);

        Assert.Equal(["ampr_assets-000.pak", "ampr_assets.index"], result.Stats.LoosePaths.Order(StringComparer.Ordinal));
        Assert.Equal(2, result.Stats.FilesPacked);
    }
}
