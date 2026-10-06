using System.Text.Json;
using MkPFS.Core.PFS.PS5;
using MkPFS.Core.PKG;

namespace MkPFS.Parity;

/// <summary>
/// FPKG plan F2: the PS5 package reader against Publishing Tools references
/// (<c>tools/oracle-fpkg/build_sdk_refs.py</c>, <c>fpkg/sdk</c>) and LibProsperoPkg output
/// (<c>fpkg/goldens</c>).
/// </summary>
public sealed class PS5PackageReadTests
{
    // Files Publishing Tools (or its GP5 wrapper) adds to every application.
    private static readonly string[] SdkAddedFiles = ["sce_sys/about/right.sprx", "sce_sys/pfs-version.dat", "playgo-languages/01-en-US.bin"];

    // Source files carried as CNT entries rather than inner files.
    private static readonly string[] OuterOnlySources = ["sce_sys/param.json", "sce_sys/icon0.png", "sce_sys/pic0.png"];

    // out.pkg is the GP5 wrapper's 100-chunk project; single/free the one-chunk rebuilds (standard/free DRM).
    public static TheoryData<string, string> SdkTrees()
    {
        TheoryData<string, string> data = [];
        string? root = Fixtures.GeneratedRoot;
        string manifest = root is null ? string.Empty : Path.Combine(root, "fpkg", "sdk", "manifest.json");
        if (!File.Exists(manifest))
        {
            data.Add("(missing)", "out");
            return data;
        }

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(manifest));
        foreach (JsonProperty tree in doc.RootElement.GetProperty("trees").EnumerateObject())
        {
            if (tree.Value.GetProperty("exit").GetInt32() != 0)
            {
                continue;
            }

            data.Add(tree.Name, "out");
            foreach (string variant in (string[])["single", "free"])
            {
                if (tree.Value.TryGetProperty(variant, out JsonElement v) && v.GetProperty("exit").GetInt32() == 0)
                {
                    data.Add(tree.Name, variant);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SdkTrees))]
    public void Sdk_reference_passes_every_check(string tree, string variant)
    {
        string pkg = Fixtures.PathOrSkip("fpkg", "sdk", tree, variant + ".pkg");
        using PS5Package package = PS5Package.Open(pkg);
        Assert.True(package.Outer.IsPlaintext);
        List<PS5Check> checks = PS5PackageVerifier.Verify(package);
        Assert.All(checks, c => Assert.True(c.Passed, $"{c.Name}: {c.Detail}"));
    }

    [Theory]
    [MemberData(nameof(SdkTrees))]
    public void Sdk_reference_unpacks_to_its_source(string tree, string variant)
    {
        string pkg = Fixtures.PathOrSkip("fpkg", "sdk", tree, variant + ".pkg");
        string source = Fixtures.PathOrSkip("fpkg", "sdk", tree, "src");
        string output = Path.Combine(Path.GetTempPath(), "mkpfs-f2-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (PS5Package package = PS5Package.Open(pkg))
            {
                PS5PackageExtractor.Extract(package, output);
            }

            Dictionary<string, string> expected = HashTree(source);
            Dictionary<string, string> actual = HashTree(output);
            foreach ((string path, string hash) in expected)
            {
                Assert.True(actual.TryGetValue(path, out string? got), $"missing {path}");
                if (!OuterOnlySources.Contains(path))
                {
                    Assert.True(hash == got, $"content differs: {path}");
                }
            }

            Assert.All(SdkAddedFiles.Where(f => variant == "out" || !f.StartsWith("playgo-languages/", StringComparison.Ordinal)), f => Assert.True(actual.ContainsKey(f), $"missing {f}"));
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("hb_min_kraken")]
    [InlineData("hb_min_raw")]
    [InlineData("app_multi_kraken")]
    [InlineData("app_multi_raw")]
    [InlineData("hb_min_passcode")]
    public void Oracle_package_decrypts_but_its_naps_layout_does_not_conform(string name)
    {
        string pkg = Fixtures.PathOrSkip("fpkg", "goldens", name, "out.pkg");
        string? passcode = name == "hb_min_passcode" ? "abcdefghijklmnopqrstuvwxyz012345" : null;
        using PS5Package package = PS5Package.Open(pkg, passcode);
        Assert.False(package.Outer.IsPlaintext);
        Dictionary<string, PS5Check> checks = PS5PackageVerifier.Verify(package).ToDictionary(c => c.Name);

        // LibProsperoPkg 748eabf matches Publishing Tools on every digest except the FIH copy at CNT+0x460,
        // and its naps layout does not map the inner mount (one cblock for the whole gap, its own u2c bytes).
        string[] known = ["CNT FIH digest", "naps layout"];
        Assert.All(checks.Values.Where(c => !known.Contains(c.Name)), c => Assert.True(c.Passed, $"{c.Name}: {c.Detail}"));
        Assert.All(known, k => Assert.False(checks[k].Passed, $"{k} now passes; update tools/oracle-fpkg/README.md findings"));
    }

    [Fact]
    public void Game_metadata_reads_param_json_and_icon_from_the_cnt()
    {
        string pkg = Fixtures.PathOrSkip("fpkg", "sdk", "app_multi", "out.pkg");
        Core.Metadata.GameMetadata meta = Core.Metadata.GameMetadataReader.Read(pkg);
        Assert.Equal("PS5 PKG (debug)", meta.PackageType);
        Assert.Equal("UP9000-PPSA99999_00-MKPFSORACLE00000", meta.ContentId);
        Assert.Equal("PPSA99999", meta.TitleId);
        Assert.Equal("MkPFS Oracle Multi", meta.GameTitle);
        Assert.NotNull(meta.IconBytes);
    }

    [Fact]
    public void Wrong_passcode_is_reported()
    {
        string pkg = Fixtures.PathOrSkip("fpkg", "goldens", "hb_min_passcode", "out.pkg");
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => PS5Package.Open(pkg));
        Assert.Contains("does not open", ex.Message, StringComparison.Ordinal);
    }

    private static Dictionary<string, string> HashTree(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'),
            f => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))),
            StringComparer.Ordinal);
}
