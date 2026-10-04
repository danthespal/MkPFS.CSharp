using System.Security.Cryptography;
using System.Text.Json;
using MkPFS.Build.AMPRPack;
using MkPFS.Core.AMPR;

namespace MkPFS.Parity;

/// <summary>
/// Rebuilds every golden pack set (<c>tools/oracle/build_ampr_goldens.py</c>) with <see cref="AMPRPackBuilder"/> and
/// compares each output file byte for byte (SHA-256), plus the stats and warnings ampr_pack printed.
/// </summary>
public sealed class AMPRPackBuilderParityTests
{
    private const long FixedMTime = 1_600_000_000;

    public static TheoryData<string> BuiltCases => AMPRPackFormatParityTests.BuiltCases;

    private sealed record CaseInfo(
        List<string> Flags, List<string> Removed, Dictionary<string, long> Touched, bool HasConfig, bool HasRuntimeConfigStep);

    private static CaseInfo ReadCase(string caseName)
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(Fixtures.PathOrSkip("ampr", "goldens", "manifest.json")));
        JsonElement entry = manifest.RootElement.GetProperty("cases").GetProperty(caseName);
        return new CaseInfo(
            [.. entry.GetProperty("flags").EnumerateArray().Select(e => e.GetString()!)],
            [.. entry.GetProperty("remove_before_pack").EnumerateArray().Select(e => e.GetString()!)],
            entry.GetProperty("touch_after_index").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt64()),
            entry.GetProperty("toml").GetBoolean(),
            entry.GetProperty("steps").TryGetProperty("runtime_config", out _));
    }

    // The oracle copies trees/ampr_assets, pins mtimes, builds the index, then removes or touches files.
    private static string PrepareRoot(CaseInfo info, string work)
    {
        string tree = Fixtures.PathOrSkip("ampr", "trees", "ampr_assets");
        if (info.Removed.Count == 0 && info.Touched.Count == 0)
        {
            return tree;
        }

        string root = Path.Combine(work, "app0");
        foreach (string file in Directory.EnumerateFiles(tree, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(root, Path.GetRelativePath(tree, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            File.SetLastWriteTimeUtc(target, DateTime.UnixEpoch.AddSeconds(FixedMTime));
        }

        foreach (string removed in info.Removed)
        {
            File.Delete(Path.Combine(root, removed));
        }

        foreach ((string relative, long mtime) in info.Touched)
        {
            File.SetLastWriteTimeUtc(Path.Combine(root, relative), DateTime.UnixEpoch.AddSeconds(mtime));
        }

        return root;
    }

    private static AMPRBuildResult Run(string caseName, string outDir, string work)
    {
        CaseInfo info = ReadCase(caseName);
        string root = PrepareRoot(info, work);
        string caseDir = Fixtures.PathOrSkip("ampr", "goldens", caseName);
        AMPRPackConfig config = AMPRPackConfig.Load(info.HasConfig ? Path.Combine(caseDir, "config.toml") : null);
        List<string> include = [];
        List<string> exclude = [];
        bool allowMissing = false;
        for (int i = 0; i < info.Flags.Count; i++)
        {
            switch (info.Flags[i])
            {
                case "--workers":
                    config.Workers = long.Parse(info.Flags[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--self-contained":
                    config.SelfContained = true;
                    break;
                case "--require-packed":
                    config.RequiredPacked = [.. config.RequiredPacked, info.Flags[++i]];
                    break;
                case "--include":
                    include.Add(info.Flags[++i]);
                    break;
                case "--exclude":
                    exclude.Add(info.Flags[++i]);
                    break;
                case "--allow-missing":
                    allowMissing = true;
                    break;
                default:
                    throw new InvalidOperationException($"unhandled flag {info.Flags[i]}");
            }
        }

        return AMPRPackBuilder.Build(root, Path.Combine(caseDir, "ampr_emu.index"), outDir, config, include, exclude, allowMissing);
    }

    private static JsonElement OracleOutput(string caseName)
    {
        string log = File.ReadAllText(Fixtures.PathOrSkip("ampr", "goldens", caseName, "pack.log"));
        int start = log.IndexOf("--- stdout\n", StringComparison.Ordinal) + "--- stdout\n".Length;
        int end = log.IndexOf("\n--- stderr", StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(log[start..end]);
        return document.RootElement.Clone();
    }

    [Theory]
    [MemberData(nameof(BuiltCases))]
    public void Pack_set_matches_the_oracle_byte_for_byte(string caseName)
    {
        string work = Path.Combine(Path.GetTempPath(), "mkpfs-ampr-build-" + Guid.NewGuid().ToString("N"));
        string outDir = Path.Combine(work, "out");
        try
        {
            AMPRBuildResult result = Run(caseName, outDir, work);

            Dictionary<string, string> expected = OracleHashesUnder(caseName, "out/");
            Dictionary<string, string> actual = Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories).ToDictionary(
                p => "out/" + Path.GetRelativePath(outDir, p).Replace('\\', '/'),
                p => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p))),
                StringComparer.Ordinal);
            if (ReadCase(caseName).HasRuntimeConfigStep)
            {
                // The oracle's later runtime-config step rewrote .runtime; check the build's own settings instead.
                const string runtime = "out/ampr_assets.index.runtime";
                AMPRPackConfig config = AMPRPackConfig.Load(Fixtures.PathOrSkip("ampr", "goldens", caseName, "config.toml"));
                byte[] buildId = Core.AMPR.AMPRPackManifest.Load(result.IndexPath).BuildId;
                Assert.Equal(config.Runtime!.Encode(buildId), File.ReadAllBytes(Path.Combine(outDir, "ampr_assets.index.runtime")));
                expected.Remove(runtime);
                actual.Remove(runtime);
            }

            Assert.Equal(expected.OrderBy(kv => kv.Key, StringComparer.Ordinal), actual.OrderBy(kv => kv.Key, StringComparer.Ordinal));

            JsonElement oracle = OracleOutput(caseName);
            AMPRBuildStats stats = result.Stats;
            Assert.Equal(
                Numbers(oracle),
                new Dictionary<string, long>
                {
                    ["files_total"] = stats.FilesTotal,
                    ["files_packed"] = stats.FilesPacked,
                    ["files_loose"] = stats.FilesLoose,
                    ["files_auto_loose"] = stats.FilesAutoLoose,
                    ["auto_loose_logical_bytes"] = stats.AutoLooseLogicalBytes,
                    ["auto_loose_sampled_bytes"] = stats.AutoLooseSampledBytes,
                    ["chunks"] = stats.Chunks,
                    ["chunks_lz4"] = stats.ChunksLZ4,
                    ["chunks_raw"] = stats.ChunksRaw,
                    ["chunks_shared"] = stats.ChunksShared,
                    ["logical_bytes"] = stats.LogicalBytes,
                    ["stored_bytes"] = stats.StoredBytes,
                    ["padding_bytes"] = stats.PaddingBytes,
                    ["io_pages_touched"] = stats.IOPagesTouched,
                    ["io_page_safe_chunks"] = stats.IOPageSafeChunks,
                    ["dense_streaming_chunks"] = stats.DenseStreamingChunks,
                });
            Assert.Equal(Strings(oracle, "loose_paths"), stats.LoosePaths);
            Assert.Equal(Strings(oracle, "warnings"), result.Warnings);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Theory]
    [InlineData("neg_missing")]
    [InlineData("neg_require_packed")]
    public void Build_errors_match_the_oracle_and_leave_no_output(string caseName)
    {
        string log = File.ReadAllText(Fixtures.PathOrSkip("ampr", "goldens", caseName, "pack.log"));
        string expected = log.Split('\n').Single(line => line.StartsWith("error: ", StringComparison.Ordinal))["error: ".Length..];
        string work = Path.Combine(Path.GetTempPath(), "mkpfs-ampr-build-" + Guid.NewGuid().ToString("N"));
        string outDir = Path.Combine(work, "out");
        try
        {
            AMPRPackException error = Assert.Throws<AMPRPackException>(() => Run(caseName, outDir, work));
            Assert.Equal(expected, error.Message);
            Assert.Empty(Directory.EnumerateFileSystemEntries(outDir));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static Dictionary<string, string> OracleHashesUnder(string caseName, string prefix)
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(Fixtures.PathOrSkip("ampr", "goldens", "manifest.json")));
        return manifest.RootElement.GetProperty("cases").GetProperty(caseName).GetProperty("sha256").EnumerateObject()
            .Where(p => p.Name.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
    }

    private static Dictionary<string, long> Numbers(JsonElement oracle) => oracle.EnumerateObject()
        .Where(p => p.Value.ValueKind == JsonValueKind.Number && p.Name != "compression_ratio")
        .ToDictionary(p => p.Name, p => p.Value.GetInt64());

    private static List<string> Strings(JsonElement oracle, string name) =>
        [.. oracle.GetProperty(name).EnumerateArray().Select(e => e.GetString()!)];
}
