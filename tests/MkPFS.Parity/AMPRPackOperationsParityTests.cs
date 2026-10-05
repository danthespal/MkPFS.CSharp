using System.Security.Cryptography;
using System.Text.Json;
using MkPFS.Build.AMPRPack;
using MkPFS.Core.AMPR;

namespace MkPFS.Parity;

/// <summary>
/// Runs verify, list, inspect, unpack, the removal plan and runtime-config on every golden pack set and compares
/// the results with the JSON ampr_pack printed (<c>verify.log</c>, <c>list.log</c>, ...).
/// </summary>
public sealed class AMPRPackOperationsParityTests
{
    public static TheoryData<string> BuiltCases => AMPRPackFormatParityTests.BuiltCases;

    private static string Index(string caseName) => Fixtures.PathOrSkip("ampr", "goldens", caseName, "out", "ampr_assets.index");

    // Removed files only ever affect loose entries, so the shared fixture tree serves as every case's /app0.
    private static string Root() => Fixtures.PathOrSkip("ampr", "trees", "ampr_assets");

    private static JsonElement Stdout(string caseName, string step)
    {
        string log = File.ReadAllText(Fixtures.PathOrSkip("ampr", "goldens", caseName, step + ".log"));
        int start = log.IndexOf("--- stdout\n", StringComparison.Ordinal) + "--- stdout\n".Length;
        int end = log.IndexOf("\n--- stderr", StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(log[start..end]);
        return document.RootElement.Clone();
    }

    private static string? Error(string caseName, string step) =>
        File.ReadAllText(Fixtures.PathOrSkip("ampr", "goldens", caseName, step + ".log")).Split('\n')
            .FirstOrDefault(line => line.StartsWith("error: ", StringComparison.Ordinal))?["error: ".Length..];

    [Theory]
    [MemberData(nameof(BuiltCases))]
    public void Verify_and_source_compare_match(string caseName)
    {
        JsonElement oracle = Stdout(caseName, "verify");
        AMPRVerifyResult verify = AMPRPackTools.Verify(Index(caseName));
        AMPRSourceCompareResult compare = AMPRPackTools.VerifyAgainstRoot(Index(caseName), Root());

        Assert.Equal(
            (oracle.GetProperty("files").GetInt64(), oracle.GetProperty("physical_chunks").GetInt64(),
                oracle.GetProperty("stored_bytes").GetInt64(), oracle.GetProperty("raw_bytes").GetInt64()),
            (verify.Files, verify.PhysicalChunks, verify.StoredBytes, verify.RawBytes));
        JsonElement source = oracle.GetProperty("source_compare");
        Assert.Equal(
            (source.GetProperty("files").GetInt64(), source.GetProperty("chunks").GetInt64(), source.GetProperty("bytes").GetInt64()),
            (compare.Files, compare.Chunks, compare.Bytes));
    }

    [Theory]
    [MemberData(nameof(BuiltCases))]
    public void List_matches(string caseName)
    {
        JsonElement[] oracle = [.. Stdout(caseName, "list").EnumerateArray()];
        List<AMPRListEntry> rows = AMPRPackTools.List(Index(caseName));

        Assert.Equal(oracle.Length, rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            JsonElement o = oracle[i];
            AMPRListEntry r = rows[i];
            Assert.Equal(o.GetProperty("file_id").GetInt32(), r.FileId);
            Assert.Equal(o.GetProperty("path").GetString(), r.Path);
            Assert.Equal(o.GetProperty("packed").GetBoolean(), r.Packed);
            Assert.Equal(o.GetProperty("logical_size").GetUInt64(), r.LogicalSize);
            Assert.Equal(o.GetProperty("stored_size").GetInt64(), r.StoredSize);
            Assert.Equal(o.GetProperty("block_size").GetInt64(), r.BlockSize);
            Assert.Equal(o.GetProperty("chunks").GetUInt32(), r.Chunks);
            Assert.Equal(o.GetProperty("codecs").EnumerateArray().Select(e => e.GetString()!), r.Codecs);
            Assert.Equal(o.GetProperty("packs").EnumerateArray().Select(e => e.GetInt32()), r.Packs);
            Assert.Equal(o.GetProperty("io_page_sizes").EnumerateArray().Select(e => e.GetUInt32()), r.IOPageSizes);
            Assert.Equal(o.GetProperty("layout").GetString(), r.Layout);
            Assert.Equal(o.GetProperty("streaming").GetBoolean(), r.Streaming);
            Assert.Equal(o.GetProperty("random_access").GetBoolean(), r.RandomAccess);
            Assert.Equal(o.GetProperty("hot").GetBoolean(), r.Hot);
        }
    }

    [Theory]
    [MemberData(nameof(BuiltCases))]
    public void Inspect_matches(string caseName)
    {
        JsonElement oracle = Stdout(caseName, "inspect");
        AMPRInspectResult result = AMPRPackTools.Inspect(Index(caseName));

        Assert.Equal(oracle.GetProperty("build_id").GetString(), result.BuildId);
        Assert.Equal(
            (oracle.GetProperty("files").GetInt64(), oracle.GetProperty("packed_files").GetInt64(),
                oracle.GetProperty("loose_files").GetInt64(), oracle.GetProperty("chunks").GetInt64()),
            (result.Files, result.PackedFiles, result.LooseFiles, result.Chunks));
        // The oracle ran inspect before its runtime-config step rewrote .runtime; expect the final settings.
        JsonElement runtime = File.Exists(Path.Combine(Fixtures.PathOrSkip("ampr", "goldens", caseName), "runtime_config.log"))
            ? Stdout(caseName, "runtime_config").GetProperty("runtime")
            : oracle.GetProperty("runtime");
        if (runtime.ValueKind == JsonValueKind.Null)
        {
            Assert.Null(result.Runtime);
        }
        else
        {
            Assert.Equal(
                new AMPRRuntimeSettings(
                    runtime.GetProperty("decoded_cache_bytes").GetInt64(),
                    runtime.GetProperty("physical_cache_bytes").GetInt64(),
                    runtime.GetProperty("workers").GetInt64(),
                    runtime.GetProperty("latency_reserve_workers").GetInt64()),
                result.Runtime);
        }

        Assert.Equal(
            oracle.GetProperty("packs").EnumerateArray().Select(p => new AMPRInspectPack(
                p.GetProperty("id").GetInt32(),
                p.GetProperty("name").GetString()!,
                p.GetProperty("file_size").GetUInt64(),
                p.GetProperty("payload_bytes").GetUInt64(),
                p.GetProperty("io_page_size").GetUInt32(),
                p.GetProperty("io_pages").GetUInt64(),
                p.GetProperty("flags").GetUInt32())),
            result.Packs);
    }

    [Theory]
    [MemberData(nameof(BuiltCases))]
    public void Unpack_matches_counts_hashes_and_mtimes(string caseName)
    {
        JsonElement oracle = Stdout(caseName, "unpack");
        string output = Path.Combine(Path.GetTempPath(), "mkpfs-ampr-unpack-" + Guid.NewGuid().ToString("N"));
        try
        {
            AMPRExtractResult result = AMPRPackTools.Extract(Index(caseName), output);
            Assert.Equal((oracle.GetProperty("files").GetInt64(), oracle.GetProperty("bytes").GetInt64()), (result.Files, result.Bytes));

            using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(Fixtures.PathOrSkip("ampr", "goldens", "manifest.json")));
            Dictionary<string, string> expected = manifest.RootElement.GetProperty("cases").GetProperty(caseName).GetProperty("sha256")
                .EnumerateObject().Where(p => p.Name.StartsWith("unpacked/", StringComparison.Ordinal))
                .ToDictionary(p => p.Name["unpacked/".Length..], p => p.Value.GetString()!, StringComparer.Ordinal);
            Dictionary<string, string> actual = Directory.Exists(output)
                ? Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).ToDictionary(
                    p => Path.GetRelativePath(output, p).Replace('\\', '/'),
                    p => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p))),
                    StringComparer.Ordinal)
                : [];
            Assert.Equal(expected.OrderBy(kv => kv.Key, StringComparer.Ordinal), actual.OrderBy(kv => kv.Key, StringComparer.Ordinal));

            AMPRPackManifest pack = AMPRPackManifest.Load(Index(caseName));
            for (int fileId = 1; fileId <= pack.Files.Count; fileId++)
            {
                if (pack.Files[fileId - 1].IsPacked)
                {
                    string path = Path.Combine(output, AMPRAssetPath.Relative(pack.FilePath(fileId)));
                    Assert.Equal(DateTime.UnixEpoch.AddSeconds(pack.Files[fileId - 1].MTime), File.GetLastWriteTimeUtc(path));
                }
            }
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
    [MemberData(nameof(BuiltCases))]
    public void Removal_plan_matches(string caseName)
    {
        string? error = Error(caseName, "remove_plan");
        if (error is not null)
        {
            Assert.Equal(error, Assert.Throws<AMPRPackException>(() => AMPRPackMaintenance.RemovalPlan(Index(caseName), Root())).Message);
            return;
        }

        JsonElement oracle = Stdout(caseName, "remove_plan");
        AMPRRemovalPlan plan = AMPRPackMaintenance.RemovalPlan(Index(caseName), Root());
        Assert.True(oracle.GetProperty("dry_run").GetBoolean());
        Assert.Equal(
            (oracle.GetProperty("files").GetInt64(), oracle.GetProperty("present_files").GetInt64(),
                oracle.GetProperty("missing_files").GetInt64(), oracle.GetProperty("bytes").GetInt64()),
            (plan.Files, plan.PresentFiles, plan.MissingFiles, plan.Bytes));
        Assert.Equal(oracle.GetProperty("paths").EnumerateArray().Select(e => e.GetString()!), plan.Paths);
    }

    [Fact]
    public void Runtime_config_rewrites_the_runtime_like_the_oracle()
    {
        string caseDir = Fixtures.PathOrSkip("ampr", "goldens", "runtime");
        JsonElement oracle = Stdout("runtime", "runtime_config");
        string work = Path.Combine(Path.GetTempPath(), "mkpfs-ampr-runtime-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(work);
            string index = Path.Combine(work, "ampr_assets.index");
            File.Copy(Path.Combine(caseDir, "out", "ampr_assets.index"), index);

            AMPRRuntimeConfigResult result = AMPRPackMaintenance.WriteRuntimeConfig(index, Path.Combine(caseDir, "runtime_alt.toml"));

            Assert.Equal(File.ReadAllBytes(Path.Combine(caseDir, "out", "ampr_assets.index.runtime")), File.ReadAllBytes(index + ".runtime"));
            Assert.Equal(oracle.GetProperty("build_id").GetString(), result.BuildId);
            JsonElement runtime = oracle.GetProperty("runtime");
            Assert.Equal(
                new AMPRRuntimeSettings(
                    runtime.GetProperty("decoded_cache_bytes").GetInt64(),
                    runtime.GetProperty("physical_cache_bytes").GetInt64(),
                    runtime.GetProperty("workers").GetInt64(),
                    runtime.GetProperty("latency_reserve_workers").GetInt64()),
                result.Runtime);
            Assert.Empty(Directory.GetFiles(work, ".runtime-*"));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }
}
