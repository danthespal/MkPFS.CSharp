using System.Text;
using MkPFS.Build;
using MkPFS.Build.AMPRPack;
using MkPFS.Core.AMPR;

namespace MkPFS.Tests.AMPR;

/// <summary>verify, unpack and source removal on small pack sets built in a temporary directory.</summary>
public sealed class AMPRPackOperationsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mkpfs-ampr-ops-" + Guid.NewGuid().ToString("N"));

    public AMPRPackOperationsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Root => Path.Combine(_dir, "app0");

    private static byte[] Text(int size, int seed = 0) =>
        Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(seed, (size / 8) + 1).Select(i => $"blk{i % 97:D4} ")))[..size];

    // Packs data/** (two files in nested folders), keeps eboot.bin and sce_sys loose.
    private string BuildSet(string extraToml = "")
    {
        Dictionary<string, byte[]> files = new()
        {
            ["eboot.bin"] = Text(3000, 1),
            ["sce_sys/param.json"] = Text(100, 2),
            ["data/a/one.dat"] = Text(150_000, 3),
            ["data/a/b/two.dat"] = Text(70_000, 4),
        };
        foreach ((string path, byte[] data) in files)
        {
            string full = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, data);
        }

        AmprIndex.Build(Root, Path.Combine(Root, AmprIndex.IndexName));
        string config = Path.Combine(_dir, "config.toml");
        File.WriteAllText(config, "[pack]\ndefault_action = \"loose\"\n[[rule]]\ninclude = \"data/**\"\n" + extraToml, new UTF8Encoding(false));
        return AMPRPackBuilder.Build(Root, Path.Combine(Root, AmprIndex.IndexName), Path.Combine(_dir, "out"), AMPRPackConfig.Load(config)).IndexPath;
    }

    [Theory]
    [InlineData("eboot.bin", true)]
    [InlineData("sce_sys/param.json", true)]
    [InlineData("SCE_MODULE/x.bin", true)]
    [InlineData("data/lib.SPRX", true)]
    [InlineData("data/x.self", true)]
    [InlineData("data/ampr_assets-007.pak", true)]
    [InlineData("data/ampr_emu.index", true)]
    [InlineData("data/.prx", false)]
    [InlineData("data/a.prx.bak", false)]
    [InlineData("data/eboot.bin.txt", false)]
    public void Protected_paths_match_python(string relative, bool expected) =>
        Assert.Equal(expected, AMPRPackMaintenance.IsProtected(relative));

    [Fact]
    public void Remove_packed_sources_verifies_then_deletes_only_packed_files()
    {
        string index = BuildSet();
        AMPRRemovalPlan plan = AMPRPackMaintenance.RemovalPlan(index, Root);
        Assert.Equal(["data/a/b/two.dat", "data/a/one.dat"], plan.Paths.Order(StringComparer.Ordinal));
        Assert.True(File.Exists(Path.Combine(Root, "data", "a", "one.dat")));

        AMPRRemovalResult result = AMPRPackMaintenance.RemovePackedSources(index, Root, removeEmptyDirs: true);

        Assert.Equal(new AMPRRemovalResult(2, 220_000, 3, true), result);
        Assert.False(Directory.Exists(Path.Combine(Root, "data")));
        Assert.True(File.Exists(Path.Combine(Root, "eboot.bin")));
        Assert.True(File.Exists(Path.Combine(Root, "sce_sys", "param.json")));

        // The packs still reproduce the removed files exactly.
        string restored = Path.Combine(_dir, "restored");
        Assert.Equal(new AMPRExtractResult(2, 220_000), AMPRPackTools.Extract(index, restored));
        Assert.Equal(Text(150_000, 3), File.ReadAllBytes(Path.Combine(restored, "data", "a", "one.dat")));
        Assert.Equal(Text(70_000, 4), File.ReadAllBytes(Path.Combine(restored, "data", "a", "b", "two.dat")));
    }

    [Fact]
    public void Remove_packed_sources_refuses_a_changed_source_and_deletes_nothing()
    {
        string index = BuildSet();
        string one = Path.Combine(Root, "data", "a", "one.dat");
        byte[] changed = File.ReadAllBytes(one);
        changed[70_000] ^= 0xFF;
        File.WriteAllBytes(one, changed);

        AMPRPackException error = Assert.Throws<AMPRPackException>(() => AMPRPackMaintenance.RemovePackedSources(index, Root));

        Assert.StartsWith("packed/source mismatch for data/a/one.dat: logical=0x11170, chunk=1, pack=0, physical=0x", error.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(one));
        Assert.True(File.Exists(Path.Combine(Root, "data", "a", "b", "two.dat")));
    }

    [Fact]
    public void Remove_packed_sources_refuses_when_a_source_is_missing()
    {
        string index = BuildSet();
        File.Delete(Path.Combine(Root, "data", "a", "b", "two.dat"));

        Assert.Equal(1, AMPRPackMaintenance.RemovalPlan(index, Root).MissingFiles);
        Assert.Equal(
            "cannot verify before removal: 1 packed source files are missing",
            Assert.Throws<AMPRPackException>(() => AMPRPackMaintenance.RemovePackedSources(index, Root)).Message);
        Assert.True(File.Exists(Path.Combine(Root, "data", "a", "one.dat")));
    }

    [Fact]
    public void Removal_plan_refuses_a_manifest_that_packs_protected_files()
    {
        string index = BuildSet("[[rule]]\ninclude = \"eboot.bin\"\n");
        Assert.Equal(
            "manifest marks a protected game/runtime file as packed: eboot.bin",
            Assert.Throws<AMPRPackException>(() => AMPRPackMaintenance.RemovalPlan(index, Root)).Message);
    }

    [Fact]
    public void Verify_detects_a_corrupted_volume()
    {
        string index = BuildSet();
        AMPRPackManifest manifest = AMPRPackManifest.Load(index);
        AMPRChunkRecord chunk = manifest.Chunks[0];
        string volume = Path.Combine(Path.GetDirectoryName(index)!, manifest.PackName(chunk.PackId));
        byte[] data = File.ReadAllBytes(volume);
        data[(int)chunk.Offset + chunk.StoredSize - 1] ^= 0x55;
        File.WriteAllBytes(volume, data);

        AMPRPackException error = Assert.Throws<AMPRPackException>(() => AMPRPackTools.Verify(index));
        Assert.Contains(error.Message, new[] { "raw chunk CRC/size mismatch", "LZ4 chunk decompression failed" });
    }

    [Fact]
    public void Readers_require_the_crc_sidecar()
    {
        string index = BuildSet();
        File.Delete(AMPRPackFormat.ChunkCrcPath(index));

        Assert.StartsWith(
            "invalid or missing chunk CRC sidecar: ",
            Assert.Throws<AMPRPackException>(() => AMPRPackTools.Verify(index)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Unpack_selects_files_and_refuses_to_overwrite_unless_asked()
    {
        string index = BuildSet();
        string output = Path.Combine(_dir, "unpacked");

        Assert.Equal(new AMPRExtractResult(1, 70_000), AMPRPackTools.Extract(index, output, ["data/a/b/*"]));
        AMPRPackException error = Assert.Throws<AMPRPackException>(() => AMPRPackTools.Extract(index, output));
        Assert.Equal($"destination exists: {Path.Combine(output, "data", "a", "b", "two.dat")}", error.Message);
        Assert.Equal(new AMPRExtractResult(2, 220_000), AMPRPackTools.Extract(index, output, overwrite: true, preserveMTime: false));
        Assert.Empty(Directory.GetFiles(output, "*.tmp-*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Runtime_config_requires_a_runtime_section()
    {
        string index = BuildSet();
        string config = Path.Combine(_dir, "noruntime.toml");
        File.WriteAllText(config, "[pack]\n");

        Assert.Equal(
            "configuration must contain [runtime]",
            Assert.Throws<AMPRPackException>(() => AMPRPackMaintenance.WriteRuntimeConfig(index, config)).Message);
    }
}
