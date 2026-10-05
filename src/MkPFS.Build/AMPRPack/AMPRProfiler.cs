using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using MkPFS.Core.AMPR;
using MkPFS.Core.Compression;
using MkPFS.Core.Util;

namespace MkPFS.Build.AMPRPack;

/// <summary>One trace run: the APR journal and the index it was recorded with (Python <c>TraceSpec</c>).</summary>
/// <param name="Name">Run name, used in the report and in warnings.</param>
/// <param name="Commands"><c>ampr_commands.bin</c>.</param>
/// <param name="Index">The run's <c>ampr_emu.index</c>.</param>
public sealed record AMPRTraceSpec(string Name, string Commands, string Index);

/// <summary>Options of <c>ampr_pack_profile.py generate</c> (Python <c>ProfileOptions</c>), with its defaults.</summary>
public sealed class AMPRProfileOptions
{
    /// <summary>Profile name.</summary>
    public required string Name { get; set; }

    /// <summary><c>--io-page-size</c>.</summary>
    public long IOPageSize { get; set; } = AMPRProfiler.DefaultIOPage;

    /// <summary><c>--pack-index-budget</c>.</summary>
    public long IndexBudget { get; set; } = 96L * 1024 * 1024;

    /// <summary><c>--cache-candidates</c>, ascending.</summary>
    public IReadOnlyList<long> CacheCandidates { get; set; } = AMPRProfiler.DefaultCacheCandidates;

    /// <summary><c>--lanes</c>; 0 picks 1 to 4.</summary>
    public int Lanes { get; set; }

    /// <summary><c>--workers</c> written to <c>[pack]</c>.</summary>
    public int Workers { get; set; } = 8;

    /// <summary><c>--runtime-workers</c>; 0 picks 2 to 8 from the trace.</summary>
    public int RuntimeWorkers { get; set; }

    /// <summary><c>--strategy</c>: conservative, balanced or aggressive.</summary>
    public string Strategy { get; set; } = "balanced";

    /// <summary><c>--pattern-mode</c>: exact, hybrid or directory.</summary>
    public string PatternMode { get; set; } = "exact";

    /// <summary><c>--min-reads</c>.</summary>
    public int MinReads { get; set; } = 1;

    /// <summary><c>--min-requested-bytes</c>.</summary>
    public long MinRequestedBytes { get; set; } = 1;

    /// <summary><c>--max-rule-files</c>.</summary>
    public int MaxRuleFiles { get; set; } = 256;

    /// <summary><c>--externalize-paths</c>; 0 keeps every path inline.</summary>
    public int ExternalizePaths { get; set; } = 128;

    /// <summary><c>--generalize-coverage</c>.</summary>
    public double GeneralizeCoverage { get; set; } = 0.80;

    /// <summary><c>--generalize-min-files</c>.</summary>
    public int GeneralizeMinFiles { get; set; } = 4;

    /// <summary><c>--cache-sim</c> / <c>--no-cache-sim</c>.</summary>
    public bool CacheSimulation { get; set; } = true;

    /// <summary><c>--cache-max-touches</c>; 0 simulates every touch.</summary>
    public long CacheMaxTouches { get; set; } = AMPRProfiler.DefaultCacheMaxTouches;

    /// <summary><c>--content-root</c> for LZ4 sampling, or <see langword="null"/>.</summary>
    public string? ContentRoot { get; set; }

    /// <summary><c>--sample-budget</c>; 0 disables sampling.</summary>
    public long SampleBudget { get; set; }

    /// <summary><c>--sample-blocks-per-file</c>.</summary>
    public int SampleBlocksPerFile { get; set; } = 4;

    /// <summary><c>--sample-mode</c>: hc or fast.</summary>
    public string SampleMode { get; set; } = "hc";

    /// <summary><c>--sample-level</c>.</summary>
    public int SampleLevel { get; set; } = 12;

    /// <summary><c>--sample-acceleration</c>.</summary>
    public int SampleAcceleration { get; set; } = 1;

    /// <summary>Python <c>_validate_options</c>.</summary>
    /// <exception cref="AMPRPackException">An option is out of range.</exception>
    public void Validate()
    {
        if (IOPageSize < 4096 || (IOPageSize & (IOPageSize - 1)) != 0)
        {
            throw new AMPRPackException("io-page-size must be a power of two and at least 4 KiB");
        }

        string? error = this switch
        {
            { IndexBudget: < 1024 * 1024 } => "pack-index-budget must be at least 1 MiB",
            { Lanes: < 0 or > 64 } => "lanes must be between 0 and 64",
            { Workers: < 1 or > 256 } => "workers must be between 1 and 256",
            { RuntimeWorkers: < 0 or > 16 } => "runtime-workers must be between 0 and 16",
            { MinReads: < 1 } => "min-reads must be at least 1",
            { MaxRuleFiles: < 1 } => "max-rule-files must be at least 1",
            { ExternalizePaths: < 0 } => "externalize-paths must be non-negative",
            { GeneralizeCoverage: < 0.0 or > 1.0 } => "generalize-coverage must be in [0,1]",
            { GeneralizeMinFiles: < 2 } => "generalize-min-files must be at least 2",
            _ => null,
        };
        if (error is not null)
        {
            throw new AMPRPackException(error);
        }
    }
}

/// <summary>Per-trace statistics (Python <c>TraceSummary</c>).</summary>
public sealed class AMPRTraceSummary
{
    /// <summary>Run name.</summary>
    public required string Name { get; init; }

    /// <summary>Journal path.</summary>
    public required string Commands { get; init; }

    /// <summary>Index path.</summary>
    public required string Index { get; init; }

    /// <summary>SHA-256 of the journal, lowercase hex.</summary>
    public required string CommandSha256 { get; init; }

    /// <summary>SHA-256 of the index, lowercase hex.</summary>
    public required string IndexSha256 { get; init; }

    /// <summary>Journal records read.</summary>
    public long Records { get; init; }

    /// <summary>APR reads kept.</summary>
    public long Reads { get; init; }

    /// <summary>Parser warnings.</summary>
    public required List<string> Warnings { get; init; }

    /// <summary>Records whose commands could not all be decoded.</summary>
    public long DecodeErrors { get; init; }

    /// <summary>Monotonic time between the first and last record.</summary>
    public long DurationNs { get; init; }
}

/// <summary>Result of <see cref="AMPRProfiler.Build"/> (Python <c>ProfileResult</c>).</summary>
public sealed class AMPRProfileResult
{
    internal AMPRProfileResult()
    {
    }

    /// <summary>Profile name.</summary>
    public required string Name { get; init; }

    /// <summary>Trace runs.</summary>
    public required List<AMPRTraceSummary> Traces { get; init; }

    /// <summary>Observed files and what to do with each, by path.</summary>
    internal List<AMPRProfiler.FileRecommendation> Recommendations { get; init; } = [];

    /// <summary>Observed index rows, by relative path, in first-read order.</summary>
    internal Dictionary<string, AMPRTraceIndexEntry> IndexEntries { get; init; } = [];

    internal List<AMPRProfiler.CacheSimulation> CacheSimulations { get; init; } = [];

    /// <summary>Projected resident size of the pack manifest.</summary>
    public long ProjectedIndexBytes { get; init; }

    /// <summary>The index budget the block sizes were fitted to.</summary>
    public long IndexBudget { get; init; }

    /// <summary>Recommended decoded cache.</summary>
    public long RecommendedCacheBytes { get; init; }

    /// <summary>Recommended physical-page cache.</summary>
    public long RecommendedPhysicalCacheBytes { get; init; }

    /// <summary>Recommended runtime workers.</summary>
    public int RecommendedRuntimeWorkers { get; init; }

    /// <summary>Recommended latency-reserved workers.</summary>
    public int RecommendedLatencyReserveWorkers { get; init; }

    /// <summary>Recommended AMPR internal pool.</summary>
    public long RecommendedPoolBytes { get; init; }

    /// <summary>Warnings from the traces and the budget.</summary>
    public required List<string> Warnings { get; init; }

    internal List<(string Name, int PackCount, long MaxPackSize)> Groups { get; init; } = [];

    /// <summary>Observed files that will be packed (not loose).</summary>
    public int PackedFiles => Recommendations.Count(r => r.Action != "loose");

    /// <summary>Total size of the observed files that will be packed.</summary>
    public long PackedFileBytes => Recommendations.Where(r => r.Action != "loose").Sum(r => r.Metrics.FileSize);

    /// <summary>Observed files.</summary>
    public int ObservedFiles => Recommendations.Count;
}

/// <summary>
/// Port of ampr_emu <c>tools/ampr_pack_profile.py</c> 4.1: turns APR traces recorded by the debug
/// emulator build into an <c>ampr_pack</c> TOML profile. Files the traces show the game reading through APR are
/// packed with block sizes and layouts fitted to the observed reads; every other file stays loose. The TOML, the
/// Markdown report, the metrics JSON and the runtime header match the Python output.
/// </summary>
public static class AMPRProfiler
{
    /// <summary>Upstream tool version written into the outputs.</summary>
    public const string ToolVersion = "4.1";

    /// <summary>Default <c>--io-page-size</c>.</summary>
    public const long DefaultIOPage = 64 * 1024;

    /// <summary>Default <c>--cache-max-touches</c>.</summary>
    public const long DefaultCacheMaxTouches = 5_000_000;

    /// <summary>Default <c>--cache-candidates</c>.</summary>
    public static readonly IReadOnlyList<long> DefaultCacheCandidates = [32L << 20, 64L << 20, 128L << 20, 256L << 20, 512L << 20];

    private const int ChunkRecordBytes = 12;
    private const int FileRecordBytes = 48;
    private const int PackRecordBytes = 32;
    private const long RuntimePipelineSlots = 32;
    private const long RuntimePipelineIOBytes = 2 * 1024 * 1024;
    private const long RuntimeWorkerScratchBytes = 1 * 1024 * 1024;
    private const long RuntimePostCacheReserveBytes = 32 * 1024 * 1024;
    private const long K64 = 64 * 1024;

    private static readonly long[] BlockSizes = [16 * 1024, 32 * 1024, 64 * 1024, 128 * 1024, 256 * 1024, 512 * 1024, 1024 * 1024];
    private static readonly HashSet<string> KnownLooseBasenames =
        ["eboot.bin", "ampr_emu.index", "ampr_assets.index", "ampr_assets.index.crc", "ampr_assets.index.runtime", "param.sfo", "nptitle.dat"];
    private static readonly HashSet<string> KnownLooseSuffixes = [".prx", ".sprx", ".self", ".elf"];
    private static readonly HashSet<string> KnownStreamStoreSuffixes =
        [".bik", ".bk2", ".mp4", ".m4v", ".webm", ".m2v", ".mpg", ".mpeg", ".avi", ".wem", ".opus", ".ogg", ".mp3", ".aac", ".flac"];

    private static readonly string[] StandardLoosePatterns =
    [
        "sce_module/**", "system/**", "mods/**", "save/**", "**/*.prx", "**/*.sprx", "eboot.bin", "ampr_emu.index",
        "ampr_assets.index", "ampr_assets.index.crc", "ampr_assets.index.runtime", "ampr_assets-*.pak",
    ];

    internal sealed record ReadEvent(
        int TraceOrdinal, long Sequence, int CommandOrdinal, long MonotonicNs, long Priority, string Path, string Relative,
        int FileId, long FileSize, long Offset, long Length)
    {
        public long End => Offset + Length;
    }

    internal sealed record CandidateMetrics(
        long BlockSize, long TouchedBytes, long TotalBlockTouches, long UniqueBlocks, double Amplification, double AverageBlocksPerRead,
        double RepeatRatio, double OffsetAlignmentRatio, double LengthAlignmentRatio, long MetadataBytes, double LocalScore);

    internal sealed class FileMetrics
    {
        public required string Path { get; init; }
        public required string Relative { get; init; }
        public long FileSize { get; init; }
        public int TraceCount { get; init; }
        public required List<ReadEvent> Events { get; init; }
        public int ReadCount { get; init; }
        public long RequestedBytes { get; init; }
        public long UniqueRequestedBytes { get; init; }
        public double CoverageRatio { get; init; }
        public double P50 { get; init; }
        public double P75 { get; init; }
        public double P90 { get; init; }
        public double P95 { get; init; }
        public double P99 { get; init; }
        public double Tiny4kRatio { get; init; }
        public double Small16kRatio { get; init; }
        public double Small64kRatio { get; init; }
        public double Large256kRatio { get; init; }
        public double ExactSequentialRatio { get; init; }
        public double NearSequentialRatio { get; init; }
        public double RandomSeekRatio { get; init; }
        public required Dictionary<long, CandidateMetrics> Candidates { get; init; }
        public double Confidence { get; init; }
        public required string ConfidenceLabel { get; init; }
    }

    internal sealed class FileRecommendation
    {
        public required FileMetrics Metrics { get; init; }
        public required string Action { get; set; }
        public required string Layout { get; set; }
        public long BlockSize { get; set; }
        public bool Hot { get; set; }
        public required string Group { get; set; }
        public required string Reason { get; set; }
        public double? SampledRatio { get; set; }
        public long SampledBytes { get; set; }

        public ProfileKey Key => new(Action, Layout, BlockSize, Hot, Group);

        public long MetadataBytes => Action == "loose" ? 0 : CeilDiv(Metrics.FileSize, BlockSize) * ChunkRecordBytes;
    }

    // Python tuple (action, layout, block_size, hot, group), compared element by element.
    internal readonly record struct ProfileKey(string Action, string Layout, long BlockSize, bool Hot, string Group) : IComparable<ProfileKey>
    {
        public int CompareTo(ProfileKey other)
        {
            int c = PythonCodePointComparer.Instance.Compare(Action, other.Action);
            if (c == 0)
            {
                c = PythonCodePointComparer.Instance.Compare(Layout, other.Layout);
            }

            if (c == 0)
            {
                c = BlockSize.CompareTo(other.BlockSize);
            }

            if (c == 0)
            {
                c = Hot.CompareTo(other.Hot);
            }

            return c != 0 ? c : PythonCodePointComparer.Instance.Compare(Group, other.Group);
        }
    }

    internal sealed record CacheSimulation(
        long Capacity, long RequestedBytes, long HitBytes, long Misses, long Hits, long SampledTouches, long TotalTouches, long SampledWindows)
    {
        public double HitRatio => RequestedBytes != 0 ? (double)HitBytes / RequestedBytes : 0.0;
    }

    private sealed record RuleEmission(ProfileKey Key, IReadOnlyList<string> Include, IReadOnlyList<string> Comments);

    /// <summary>
    /// Trace pairs under a folder (Python <c>discover_trace_pairs</c>): the folder itself when it holds
    /// <c>ampr_commands.bin</c> and <c>ampr_emu.index</c>, otherwise every subfolder that does, in path order.
    /// </summary>
    /// <param name="root">Folder.</param>
    /// <returns>Trace runs.</returns>
    /// <exception cref="AMPRPackException"><paramref name="root"/> is a file.</exception>
    public static List<AMPRTraceSpec> DiscoverTracePairs(string root)
    {
        if (File.Exists(root))
        {
            throw new AMPRPackException($"expected a directory, got file: {root}");
        }

        string commands = Path.Combine(root, "ampr_commands.bin");
        string index = Path.Combine(root, "ampr_emu.index");
        if (File.Exists(commands) && File.Exists(index))
        {
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
            return [new AMPRTraceSpec(name.Length > 0 ? name : "trace", commands, index)];
        }

        if (!Directory.Exists(root))
        {
            return [];
        }

        List<AMPRTraceSpec> pairs = [];
        // Like Path.rglob: hidden and system files count too (copies off the console can carry those attributes),
        // and names match with the platform's case rules.
        EnumerationOptions options = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0, MatchCasing = MatchCasing.PlatformDefault };
        List<string[]> found = [.. Directory.EnumerateFiles(root, "ampr_commands.bin", options)
            .Select(f => Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar))];
        found.Sort(ComparePathParts);
        foreach (string[] parts in found)
        {
            string file = Path.Combine([root, .. parts]);
            string sibling = Path.Combine(Path.GetDirectoryName(file)!, "ampr_emu.index");
            if (File.Exists(sibling))
            {
                string name = string.Join('-', parts[..^1]);
                pairs.Add(new AMPRTraceSpec(name.Length > 0 ? name : Path.GetFileName(Path.GetDirectoryName(file)!), file, sibling));
            }
        }

        return pairs;
    }

    /// <summary>Analyse the traces and choose what to pack (Python <c>build_profile</c>).</summary>
    /// <param name="traces">Trace runs, in order.</param>
    /// <param name="options">Options.</param>
    /// <returns>The profile.</returns>
    /// <exception cref="AMPRPackException">A journal is not a supported AMPRCMD1 file.</exception>
    public static AMPRProfileResult Build(IReadOnlyList<AMPRTraceSpec> traces, AMPRProfileOptions options)
    {
        (List<ReadEvent> events, List<AMPRTraceSummary> summaries, Dictionary<string, AMPRTraceIndexEntry> indexEntries, List<string> warnings) =
            LoadTraceEvents(traces);
        List<FileMetrics> files = BuildFileMetrics(events);
        List<FileRecommendation> recommendations = InitialRecommendations(files, options);
        SampleCompressibility(recommendations, options, warnings);
        int provisionalLanes = ChooseLanes(recommendations, options.Lanes);
        int provisionalPackCount = BuildGroups(recommendations, provisionalLanes).Sum(g => g.PackCount);
        long mandatoryIndexBytes = EstimateIndexBytes([], provisionalPackCount, indexEntries);
        long chunkBudget = Math.Max(0, options.IndexBudget - mandatoryIndexBytes);
        EnforceIndexBudget(recommendations, chunkBudget, options.Strategy);
        int lanes = ChooseLanes(recommendations, options.Lanes);
        List<(string Name, int PackCount, long MaxPackSize)> groups = BuildGroups(recommendations, lanes);
        int packCount = groups.Sum(g => g.PackCount);
        long projectedIndex = EstimateIndexBytes(recommendations, packCount, indexEntries);
        if (mandatoryIndexBytes > options.IndexBudget)
        {
            warnings.Add($"mandatory AMPRPAK4 file/string tables {HumanSize(mandatoryIndexBytes)} exceed index budget " +
                $"{HumanSize(options.IndexBudget)} before any packed chunks");
        }

        if (projectedIndex > options.IndexBudget)
        {
            warnings.Add($"projected pack index {HumanSize(projectedIndex)} still exceeds budget {HumanSize(options.IndexBudget)}");
        }

        Dictionary<string, FileRecommendation> byPath = [];
        foreach (FileRecommendation recommendation in recommendations)
        {
            byPath[recommendation.Metrics.Relative] = recommendation;
        }

        List<CacheSimulation> simulations = options.CacheSimulation ? SimulateCache(events, byPath, options.CacheCandidates, options.CacheMaxTouches) : [];
        long cacheSize = ChooseCacheSize(simulations);
        long physicalPageCache = ChoosePhysicalPageCacheSize(recommendations);
        int runtimeWorkers = ChooseRuntimeWorkers(events, recommendations, options.RuntimeWorkers);
        int latencyReserveWorkers = ChooseLatencyReserveWorkers(events, recommendations, runtimeWorkers);
        long amprIndexBytes = summaries.Where(s => s.Index.Length > 0).Select(s => new FileInfo(s.Index).Length).DefaultIfEmpty(0).Max();
        long poolSize = RoundPoolSize(projectedIndex + amprIndexBytes + cacheSize + physicalPageCache + (RuntimePipelineSlots * RuntimePipelineIOBytes)
            + (runtimeWorkers * RuntimeWorkerScratchBytes) + RuntimePostCacheReserveBytes);
        return new AMPRProfileResult
        {
            Name = options.Name,
            Traces = summaries,
            Recommendations = recommendations,
            IndexEntries = indexEntries,
            ProjectedIndexBytes = projectedIndex,
            IndexBudget = options.IndexBudget,
            CacheSimulations = simulations,
            RecommendedCacheBytes = cacheSize,
            RecommendedPhysicalCacheBytes = physicalPageCache,
            RecommendedRuntimeWorkers = runtimeWorkers,
            RecommendedLatencyReserveWorkers = latencyReserveWorkers,
            RecommendedPoolBytes = poolSize,
            Warnings = warnings,
            Groups = groups,
        };
    }

    /// <summary>
    /// The TOML profile (Python <c>render_toml</c>). With <paramref name="externalLists"/>, exact include lists longer
    /// than <see cref="AMPRProfileOptions.ExternalizePaths"/> go to <c>&lt;stem&gt;.rule-NNN.include.txt</c>
    /// sidecars (added to the dictionary) referenced by <c>include_from</c>; without it every path is inline.
    /// </summary>
    /// <param name="result">Profile.</param>
    /// <param name="options">Options it was built with.</param>
    /// <param name="externalLists">Receives sidecar names and contents, or <see langword="null"/>.</param>
    /// <param name="profileStem">Sidecar name prefix (the TOML file name without extension).</param>
    /// <returns>TOML text.</returns>
    public static string RenderToml(AMPRProfileResult result, AMPRProfileOptions options, Dictionary<string, string>? externalLists = null, string profileStem = "profile")
    {
        List<RuleEmission> emissions = DirectoryGeneralization(result.Recommendations, result.IndexEntries.Keys, options);
        string io = SizeLiteral(options.IOPageSize);
        List<string> lines =
        [
            $"# Generated by ampr_pack_profile.py {ToolVersion} from APR traces.",
            "# IMPORTANT: traces contain only APR reads observed in recorded scenarios.",
            "# Adapt this config to the real game by adding remaining files/directories",
            "# or deliberately keep them loose. A trace cannot prove complete coverage.",
            $"# Projected resident pack index: {HumanSize(result.ProjectedIndexBytes)}.",
            $"# Recommended decoded cache: {HumanSize(result.RecommendedCacheBytes)}.",
            $"# Recommended physical-page cache: {HumanSize(result.RecommendedPhysicalCacheBytes)}.",
            $"# Recommended runtime workers: {result.RecommendedRuntimeWorkers}.",
            $"# Recommended latency-reserved workers: {result.RecommendedLatencyReserveWorkers}.",
            $"# Recommended AMPR internal pool: {HumanSize(result.RecommendedPoolBytes)}.",
            "",
            "[pack]",
            "index_name = \"ampr_assets.index\"",
            "pack_pattern = \"ampr_assets-{group}-lane{lane:02d}-vol{volume:02d}-{id:03d}.pak\"",
            "default_action = \"loose\"",
            "default_block_size = \"64KiB\"",
            $"io_page_size = \"{io}\"",
            $"payload_alignment = \"{io}\"",
            "chunk_alignment = \"64B\"",
            $"workers = {options.Workers}",
            "compression_mode = \"hc\"",
            "compression_level = 12",
            "acceleration = 1",
            "deduplicate = true",
            "deduplicate_scope = \"lane\"",
            "deduplicate_streaming = false",
            "min_savings_bytes = 64",
            "min_savings_ratio = 0.01",
            "io_neutral_min_savings_bytes = \"8KiB\"",
            "io_neutral_min_savings_ratio = 0.125",
            "auto_loose_large_files = true",
            "auto_loose_hot_files = false",
            "auto_loose_min_file_size = \"64MiB\"",
            "auto_loose_sample_blocks = 32",
            "auto_loose_sample_bytes = \"16MiB\"",
            "auto_loose_min_savings_ratio = 0.05",
            "auto_loose_max_raw_ratio = 0.90",
            "preserve_mtime = true",
            "validate_index_metadata = true",
            "",
            "[runtime]",
            $"decoded_cache_bytes = {result.RecommendedCacheBytes}",
            $"physical_cache_bytes = {result.RecommendedPhysicalCacheBytes}",
            $"workers = {result.RecommendedRuntimeWorkers}",
            $"latency_reserve_workers = {result.RecommendedLatencyReserveWorkers}",
            "",
        ];
        foreach ((string name, int packCount, long maxPackSize) in result.Groups)
        {
            lines.AddRange(
            [
                $"[groups.{name}]",
                $"pack_count = {packCount}",
                $"assignment = {TomlString("balanced")}",
                $"max_pack_size = {TomlString(SizeLiteral(maxPackSize))}",
                "stripe_large_files = false",
                $"io_page_size = {TomlString(io)}",
                "",
            ]);
        }

        for (int emissionIndex = 1; emissionIndex <= emissions.Count; emissionIndex++)
        {
            RuleEmission emission = emissions[emissionIndex - 1];
            ProfileKey key = emission.Key;
            lines.AddRange(emission.Comments.Select(c => $"# {c}"));
            lines.Add("[[rule]]");
            lines.Add($"action = {TomlString(key.Action)}");
            bool isExactList = emission.Include.All(p => p.IndexOfAny(['*', '?', '[']) < 0);
            bool externalize = externalLists is not null && options.ExternalizePaths > 0 && emission.Include.Count > options.ExternalizePaths && isExactList;
            if (externalize)
            {
                string listName = $"{profileStem}.rule-{emissionIndex:000}.include.txt";
                externalLists![listName] = string.Concat(emission.Include.Select(p => p + "\n"));
                lines.Add($"include_from = [{TomlString(listName)}]");
            }
            else if (emission.Include.Count == 1)
            {
                lines.Add($"include = [{TomlString(emission.Include[0])}]");
            }
            else
            {
                lines.Add("include = [");
                lines.AddRange(emission.Include.Select(p => $"  {TomlString(p)},"));
                lines.Add("]");
            }

            lines.Add($"block_size = {TomlString(SizeLiteral(key.BlockSize))}");
            lines.Add($"group = {TomlString(key.Group)}");
            lines.Add($"layout = {TomlString(key.Layout)}");
            if (key.Hot)
            {
                lines.Add("hot = true");
                if (key.BlockSize <= 32 * 1024)
                {
                    lines.AddRange(
                    [
                        "min_savings_bytes = 16",
                        "min_savings_ratio = 0.0025",
                        "io_neutral_min_savings_bytes = \"2KiB\"",
                        "io_neutral_min_savings_ratio = 0.125",
                    ]);
                }
            }

            if (key.Layout == "streaming")
            {
                lines.Add("streaming = true");
            }

            lines.Add("");
        }

        lines.AddRange(["# Safety exclusions. The last matching rule wins.", "[[rule]]", "action = \"loose\"", "include = ["]);
        lines.AddRange(StandardLoosePatterns.Select(p => $"  {TomlString(p)},"));
        lines.AddRange(["]", ""]);
        return string.Join('\n', lines);
    }

    /// <summary>Untraced files <see cref="RenderToml(AMPRProfileResult, AMPRProfileOptions, IReadOnlyList{AMPRTraceSpec}, Dictionary{string, string}?, string)"/> added.</summary>
    /// <param name="Files">Files added.</param>
    /// <param name="Bytes">Their total size.</param>
    /// <param name="IndexBytes">Their chunk records in the pack manifest.</param>
    public readonly record struct UntracedAddition(int Files, long Bytes, long IndexBytes);

    /// <summary>
    /// <see cref="RenderToml(AMPRProfileResult, AMPRProfileOptions, Dictionary{string, string}?, string)"/> plus a
    /// MkPFS extension (not in ampr_pack_profile.py): every file of the traced games' indexes that no trace read, but
    /// whose extension a packed traced file has, is packed too, with the layout, block size and group most common for
    /// that extension among the traced files. A trace session rarely visits every level or mode, and archives of the
    /// same type are read the same way. Files without an extension, executables and modules, <c>sce_sys</c>,
    /// <c>sce_module</c> and <c>fakelib</c> are never added. The rules go before the safety exclusions.
    /// </summary>
    /// <param name="result">Profile.</param>
    /// <param name="options">Options it was built with.</param>
    /// <param name="traces">The trace runs; their indexes list the game's files.</param>
    /// <param name="externalLists">Receives sidecars, as for the upstream rules, or <see langword="null"/>.</param>
    /// <param name="profileStem">Sidecar name prefix.</param>
    /// <param name="added">What was added.</param>
    /// <returns>TOML text.</returns>
    public static string RenderToml(
        AMPRProfileResult result, AMPRProfileOptions options, IReadOnlyList<AMPRTraceSpec> traces, Dictionary<string, string>? externalLists,
        string profileStem, out UntracedAddition added)
    {
        string toml = RenderToml(result, options, externalLists, profileStem);

        // The most common packing of each extension among the packed traced files (ties: smaller block first).
        Dictionary<string, ProfileKey> byType = [];
        foreach (IGrouping<string, FileRecommendation> type in result.Recommendations
            .Where(r => r.Action != "loose")
            .GroupBy(r => Suffix(r.Metrics.Relative[(r.Metrics.Relative.LastIndexOf('/') + 1)..]).ToLowerInvariant())
            .Where(g => g.Key.Length > 0))
        {
            byType[type.Key] = type
                .GroupBy(r => new ProfileKey(r.Action, r.Layout, r.BlockSize, false, r.Group))
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key.BlockSize).ThenBy(g => g.Key)
                .First().Key;
        }

        Dictionary<string, long> files = [];
        foreach (AMPRTraceSpec spec in traces)
        {
            foreach (AMPRTraceIndexEntry entry in AMPRCommandLog.LoadIndex(spec.Index).Values)
            {
                try
                {
                    string relative = CanonicalRelative(entry.Path);
                    files[relative] = Math.Max(files.GetValueOrDefault(relative), entry.Size);
                }
                catch (AMPRPackException)
                {
                    // Unsafe paths are reported by the trace reader already.
                }
            }
        }

        Dictionary<ProfileKey, List<string>> rules = [];
        long bytes = 0;
        long indexBytes = 0;
        foreach ((string relative, long size) in files.OrderBy(f => f.Key, PythonCodePointComparer.Instance))
        {
            string name = relative[(relative.LastIndexOf('/') + 1)..];
            string top = relative.Split('/')[0];
            if (result.IndexEntries.ContainsKey(relative) || KnownAction(relative) == "loose"
                || top is "sce_sys" or "sce_module" or "fakelib" or "fakelib2"
                || !byType.TryGetValue(Suffix(name).ToLowerInvariant(), out ProfileKey key))
            {
                continue;
            }

            if (!rules.TryGetValue(key, out List<string>? list))
            {
                rules[key] = list = [];
            }

            list.Add(relative);
            bytes += size;
            indexBytes += CeilDiv(size, key.BlockSize) * ChunkRecordBytes;
        }

        added = new UntracedAddition(rules.Values.Sum(l => l.Count), bytes, indexBytes);
        if (rules.Count == 0)
        {
            return toml;
        }

        StringBuilder text = new();
        int ruleNumber = 0;
        foreach ((ProfileKey key, List<string> paths) in rules.OrderBy(r => r.Key))
        {
            ruleNumber++;
            string types = string.Join(", ", paths.Select(p => Suffix(p[(p.LastIndexOf('/') + 1)..]).ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal));
            text.Append($"# MkPFS: {paths.Count} untraced {types} files ({HumanSize(paths.Sum(p => files[p]))}), packed like most traced {types} files\n");
            text.Append("[[rule]]\n").Append($"action = {TomlString(key.Action)}\n");
            if (externalLists is not null && options.ExternalizePaths > 0 && paths.Count > options.ExternalizePaths)
            {
                string listName = $"{profileStem}.untraced-{ruleNumber:000}.include.txt";
                externalLists[listName] = string.Concat(paths.Select(p => p + "\n"));
                text.Append($"include_from = [{TomlString(listName)}]\n");
            }
            else
            {
                text.Append("include = [\n");
                foreach (string path in paths)
                {
                    text.Append($"  {TomlString(path)},\n");
                }

                text.Append("]\n");
            }

            text.Append($"block_size = {TomlString(SizeLiteral(key.BlockSize))}\n").Append($"group = {TomlString(key.Group)}\n")
                .Append($"layout = {TomlString(key.Layout)}\n");
            if (key.Layout == "streaming")
            {
                text.Append("streaming = true\n");
            }

            text.Append('\n');
        }

        const string safety = "# Safety exclusions. The last matching rule wins.\n";
        int at = toml.LastIndexOf(safety, StringComparison.Ordinal);
        return toml.Insert(at, text.ToString());
    }

    /// <summary>The pack configuration of <see cref="RenderToml"/> with every path inline, without writing it.</summary>
    /// <param name="result">Profile.</param>
    /// <param name="options">Options it was built with.</param>
    /// <returns>Configuration.</returns>
    public static AMPRPackConfig ToConfig(AMPRProfileResult result, AMPRProfileOptions options) => ToConfig(RenderToml(result, options));

    /// <summary>Parse rendered profile TOML that has every path inline.</summary>
    /// <param name="toml">TOML text from <see cref="RenderToml(AMPRProfileResult, AMPRProfileOptions, Dictionary{string, string}?, string)"/>.</param>
    /// <returns>Configuration.</returns>
    public static AMPRPackConfig ToConfig(string toml) =>
        AMPRPackConfig.FromTable(AMPRToml.Parse(toml, "trace profile"), Directory.GetCurrentDirectory());

    /// <summary>The Markdown report (Python <c>render_report</c>).</summary>
    /// <param name="result">Profile.</param>
    /// <returns>Report text.</returns>
    public static string RenderReport(AMPRProfileResult result)
    {
        List<FileRecommendation> selected = [.. result.Recommendations.Where(r => r.Action != "loose")];
        long totalReads = selected.Sum(r => (long)r.Metrics.ReadCount);
        long submitted = selected.Sum(r => r.Metrics.RequestedBytes);
        long fileSize = selected.Sum(r => r.Metrics.FileSize);
        List<string> lines =
        [
            $"# AMPR trace-derived pack profile: {result.Name}",
            "",
            "## Summary",
            "",
            $"- Trace files: **{result.Traces.Count}**",
            $"- Selected files: **{selected.Count}**",
            $"- Read commands: **{Thousands(totalReads)}**",
            $"- Submitted bytes: **{HumanSize(submitted)}**",
            $"- Selected logical file size: **{HumanSize(fileSize)}**",
            $"- Projected pack index: **{HumanSize(result.ProjectedIndexBytes)}** of {HumanSize(result.IndexBudget)} budget",
            $"- Recommended decoded cache: **{HumanSize(result.RecommendedCacheBytes)}**",
            $"- Recommended physical-page cache: **{HumanSize(result.RecommendedPhysicalCacheBytes)}**",
            $"- Runtime workers: **{result.RecommendedRuntimeWorkers}**",
            $"- Latency-reserved workers: **{result.RecommendedLatencyReserveWorkers}**",
            $"- Recommended AMPR internal pool: **{HumanSize(result.RecommendedPoolBytes)}**",
            "",
            "The recommendation describes access behaviour, not entropy. Unless content sampling was enabled, `compress` relies on the packer's per-block RAW fallback.",
            "",
            "## Profile distribution",
            "",
            "| Dimension | Distribution |",
            "|---|---|",
            "| Actions | " + Distribution(selected.Select(r => r.Action), StringKey, k => k) + " |",
            "| Layouts | " + Distribution(selected.Select(r => r.Layout), StringKey, k => k) + " |",
            "| Block sizes | " + Distribution(selected.Select(r => r.BlockSize), Comparer<long>.Default, SizeLiteral) + " |",
            "",
            "## Decoded-cache model",
            "",
            "The model uses byte-weighted LRU with second-touch admission; streaming rules bypass the main cache, matching the runtime policy.",
            "",
            "| Capacity | Hit bytes | Hit ratio | Block hits | Misses | Touches |",
            "|---:|---:|---:|---:|---:|---:|",
        ];
        if (result.CacheSimulations.Count > 0)
        {
            CacheSimulation first = result.CacheSimulations[0];
            if (first.SampledTouches < first.TotalTouches)
            {
                lines.AddRange(
                [
                    $"Cache simulation sampled {Thousands(first.SampledTouches)} of {Thousands(first.TotalTouches)} block touches in {first.SampledWindows} contiguous windows.",
                    "",
                ]);
            }

            lines.AddRange(result.CacheSimulations.Select(s =>
                $"| {HumanSize(s.Capacity)} | {HumanSize(s.HitBytes)} | {Percent(s.HitRatio, 1)} | {Thousands(s.Hits)} | {Thousands(s.Misses)} | " +
                $"{Thousands(s.SampledTouches)}/{Thousands(s.TotalTouches)} |"));
        }
        else
        {
            lines.Add("| disabled | — | — | — | — | — |");
        }

        lines.AddRange(
        [
            "",
            "## Highest-impact files",
            "",
            "| Path | Reads | Submitted | p50 | Seq. | Repeat 64K | Recommendation | Amplification | Index | Confidence |",
            "|---|---:|---:|---:|---:|---:|---|---:|---:|---|",
        ]);
        foreach (FileRecommendation recommendation in selected
            .OrderByDescending(r => (r.Metrics.RequestedBytes, r.Metrics.ReadCount)).Take(80))
        {
            FileMetrics metrics = recommendation.Metrics;
            CandidateMetrics candidate = metrics.Candidates[recommendation.BlockSize];
            lines.Add(
                $"| `{metrics.Relative.Replace("|", "\\|", StringComparison.Ordinal)}` | {Thousands(metrics.ReadCount)} | {HumanSize(metrics.RequestedBytes)} | " +
                $"{HumanSize(metrics.P50)} | {Percent(metrics.ExactSequentialRatio + metrics.NearSequentialRatio, 0)} | " +
                $"{Percent(metrics.Candidates[K64].RepeatRatio, 0)} | " +
                $"{recommendation.Action}/{recommendation.Layout}/{SizeLiteral(recommendation.BlockSize)}{(recommendation.Hot ? "/hot" : "")} | " +
                $"{PythonText.FormatFixed(candidate.Amplification, 2)}x | {HumanSize(recommendation.MetadataBytes)} | {metrics.ConfidenceLabel} |");
        }

        lines.AddRange(["", "## Trace inputs", ""]);
        foreach (AMPRTraceSummary trace in result.Traces)
        {
            lines.AddRange(
            [
                $"### {trace.Name}",
                "",
                $"- Commands: `{trace.Commands}`",
                $"- Index: `{trace.Index}`",
                $"- Records: {Thousands(trace.Records)}; reads: {Thousands(trace.Reads)}; duration: {PythonText.FormatFixed(trace.DurationNs / 1e9, 1)} s",
                $"- Sequence/parser warnings: {trace.Warnings.Count}; decode issues: {trace.DecodeErrors}",
                $"- Command SHA-256: `{trace.CommandSha256}`",
                $"- Index SHA-256: `{trace.IndexSha256}`",
                "",
            ]);
        }

        if (result.Warnings.Count > 0)
        {
            lines.AddRange(["## Warnings", ""]);
            lines.AddRange(result.Warnings.Take(200).Select(w => $"- {w}"));
            if (result.Warnings.Count > 200)
            {
                lines.Add($"- {result.Warnings.Count - 200} additional warnings omitted");
            }

            lines.Add("");
        }

        lines.AddRange(
        [
            "## Operational guidance",
            "",
            "1. Treat this profile as a starting point: traces contain only observed APR reads. Review the real game and add required files/directories from unrecorded levels, modes, languages and DLC, or deliberately keep them loose.",
            "2. Build packs, run `ampr_pack.py verify`, then test with loose source files still available.",
            "3. Measure LZ4 ratio. Archives that remain almost entirely RAW and show little cache reuse may be better left loose.",
            "4. Re-run this profiler when the game or resource layout changes.",
            "",
        ]);
        return string.Join('\n', lines);
    }

    /// <summary>
    /// The metrics document (Python <c>json.dumps(result_as_json(...), ensure_ascii=False, indent=2)</c> plus a
    /// newline): profile settings, traces, cache simulations, warnings and every observed file.
    /// </summary>
    /// <param name="result">Profile.</param>
    /// <param name="fullCandidates">Include all seven block-size candidates per file (<c>--full-metrics</c>).</param>
    /// <returns>JSON text.</returns>
    public static string RenderMetricsJson(AMPRProfileResult result, bool fullCandidates = false)
    {
        PythonJsonObject groups = [];
        foreach ((string name, int packCount, long maxPackSize) in result.Groups)
        {
            groups.Add(name, new PythonJsonObject
            {
                { "pack_count", packCount },
                { "assignment", "balanced" },
                { "max_pack_size", maxPackSize },
                { "stripe_large_files", false },
            });
        }

        PythonJsonObject document = new()
        {
            { "tool_version", ToolVersion },
            { "name", result.Name },
            { "traces", result.Traces.Select(t => new PythonJsonObject
                {
                    { "name", t.Name },
                    { "commands", t.Commands },
                    { "index", t.Index },
                    { "command_sha256", t.CommandSha256 },
                    { "index_sha256", t.IndexSha256 },
                    { "records", t.Records },
                    { "reads", t.Reads },
                    { "warnings", t.Warnings },
                    { "decode_errors", t.DecodeErrors },
                    { "duration_ns", t.DurationNs },
                }).ToList() },
            { "projected_index_bytes", result.ProjectedIndexBytes },
            { "index_budget", result.IndexBudget },
            { "recommended_cache_bytes", result.RecommendedCacheBytes },
            { "recommended_physical_cache_bytes", result.RecommendedPhysicalCacheBytes },
            { "recommended_runtime_workers", result.RecommendedRuntimeWorkers },
            { "recommended_latency_reserve_workers", result.RecommendedLatencyReserveWorkers },
            { "recommended_pool_bytes", result.RecommendedPoolBytes },
            { "groups", groups },
            { "cache_simulations", result.CacheSimulations.Select(c => new PythonJsonObject
                {
                    { "capacity", c.Capacity },
                    { "requested_bytes", c.RequestedBytes },
                    { "hit_bytes", c.HitBytes },
                    { "misses", c.Misses },
                    { "hits", c.Hits },
                    { "sampled_touches", c.SampledTouches },
                    { "total_touches", c.TotalTouches },
                    { "sampled_windows", c.SampledWindows },
                    { "hit_ratio", c.HitRatio },
                }).ToList() },
            { "warnings", result.Warnings },
            { "files", result.Recommendations.OrderByDescending(r => r.Metrics.RequestedBytes).Select(r => RecommendationJson(r, fullCandidates)).ToList() },
        };
        return PythonSortedJson.Indented(document) + "\n";
    }

    // Python _recommendation_json.
    private static PythonJsonObject RecommendationJson(FileRecommendation recommendation, bool fullCandidates)
    {
        FileMetrics m = recommendation.Metrics;
        CandidateMetrics selected = m.Candidates[recommendation.BlockSize];
        PythonJsonObject json = new()
        {
            { "path", m.Path },
            { "relative", m.Relative },
            { "file_size", m.FileSize },
            { "read_count", m.ReadCount },
            { "requested_bytes", m.RequestedBytes },
            { "unique_requested_bytes", m.UniqueRequestedBytes },
            { "coverage_ratio", m.CoverageRatio },
            { "request_percentiles", new PythonJsonObject { { "p50", m.P50 }, { "p75", m.P75 }, { "p90", m.P90 }, { "p95", m.P95 }, { "p99", m.P99 } } },
            { "sequential_ratio", m.ExactSequentialRatio + m.NearSequentialRatio },
            { "random_seek_ratio", m.RandomSeekRatio },
            { "repeat_ratio_64k", m.Candidates[K64].RepeatRatio },
            { "confidence", m.Confidence },
            { "confidence_label", m.ConfidenceLabel },
            { "recommendation", new PythonJsonObject
                {
                    { "action", recommendation.Action },
                    { "layout", recommendation.Layout },
                    { "block_size", recommendation.BlockSize },
                    { "hot", recommendation.Hot },
                    { "group", recommendation.Group },
                    { "reason", recommendation.Reason },
                    { "metadata_bytes", recommendation.MetadataBytes },
                    { "amplification", selected.Amplification },
                    { "average_blocks_per_read", selected.AverageBlocksPerRead },
                    { "sampled_ratio", recommendation.SampledRatio },
                    { "sampled_bytes", recommendation.SampledBytes },
                } },
        };
        if (fullCandidates)
        {
            PythonJsonObject candidates = [];
            foreach (CandidateMetrics c in m.Candidates.Values.OrderBy(c => c.BlockSize))
            {
                candidates.Add(c.BlockSize.ToString(CultureInfo.InvariantCulture), new PythonJsonObject
                {
                    { "block_size", c.BlockSize },
                    { "touched_bytes", c.TouchedBytes },
                    { "total_block_touches", c.TotalBlockTouches },
                    { "unique_blocks", c.UniqueBlocks },
                    { "amplification", c.Amplification },
                    { "average_blocks_per_read", c.AverageBlocksPerRead },
                    { "repeat_ratio", c.RepeatRatio },
                    { "offset_alignment_ratio", c.OffsetAlignmentRatio },
                    { "length_alignment_ratio", c.LengthAlignmentRatio },
                    { "metadata_bytes", c.MetadataBytes },
                    { "local_score", c.LocalScore },
                });
            }

            json.Add("candidates", candidates);
        }

        return json;
    }

    /// <summary>One row of the batch <c>summary.json</c> (Python <c>_build_batch_profile</c>).</summary>
    /// <param name="result">Profile.</param>
    /// <returns>JSON object.</returns>
    public static PythonJsonObject BatchSummaryRow(AMPRProfileResult result)
    {
        CacheSimulation? first = result.CacheSimulations.Count > 0 ? result.CacheSimulations[0] : null;
        return new PythonJsonObject
        {
            { "name", result.Name },
            { "files", result.Recommendations.Count },
            { "reads", result.Recommendations.Sum(r => (long)r.Metrics.ReadCount) },
            { "submitted_bytes", result.Recommendations.Sum(r => r.Metrics.RequestedBytes) },
            { "selected_file_bytes", result.Recommendations.Sum(r => r.Metrics.FileSize) },
            { "projected_index_bytes", result.ProjectedIndexBytes },
            { "recommended_cache_bytes", result.RecommendedCacheBytes },
            { "recommended_physical_cache_bytes", result.RecommendedPhysicalCacheBytes },
            { "recommended_runtime_workers", result.RecommendedRuntimeWorkers },
            { "recommended_latency_reserve_workers", result.RecommendedLatencyReserveWorkers },
            { "recommended_pool_bytes", result.RecommendedPoolBytes },
            { "cache_sampled_touches", first?.SampledTouches ?? 0 },
            { "cache_total_touches", first?.TotalTouches ?? 0 },
            { "warnings", result.Warnings.Count },
        };
    }

    /// <summary>
    /// Extract the trace inputs of a ZIP support bundle (Python <c>_extract_trace_archive</c>): only
    /// <c>ampr_commands.bin</c> and <c>ampr_emu.index</c> members, under <c>&lt;tempRoot&gt;/traces</c>.
    /// </summary>
    /// <param name="path">ZIP file.</param>
    /// <param name="tempRoot">Scratch folder.</param>
    /// <returns>The folder to scan for trace pairs.</returns>
    /// <exception cref="AMPRPackException">Not a ZIP, an unsafe member, or no trace inputs.</exception>
    public static string ExtractTraceArchive(string path, string tempRoot)
    {
        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(path);
        }
        catch (InvalidDataException)
        {
            throw new AMPRPackException($"unsupported archive (only ZIP is accepted): {path}");
        }

        string destination = Path.Combine(tempRoot, "traces");
        Directory.CreateDirectory(destination);
        int extracted = 0;
        using (archive)
        {
            foreach (ZipArchiveEntry member in archive.Entries)
            {
                string name = member.FullName;
                string[] parts = [.. name.Split('/').Where(p => p.Length > 0 && p != ".")];
                if (name.StartsWith('/') || parts.Contains(".."))
                {
                    throw new AMPRPackException($"unsafe archive member: {PythonText.Repr(name)}");
                }

                // Support bundles also carry multi-gigabyte decoded logs; the profiler needs only these two.
                if (name.EndsWith('/') || parts.Length == 0 || parts[^1] is not ("ampr_commands.bin" or "ampr_emu.index"))
                {
                    continue;
                }

                // On Windows a part can still hold "..\" or a drive ("C:"); the target must stay inside the folder.
                string target = Path.GetFullPath(Path.Combine([destination, .. parts]));
                if (!target.StartsWith(Path.GetFullPath(destination) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    throw new AMPRPackException($"unsafe archive member: {PythonText.Repr(name)}");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                member.ExtractToFile(target, overwrite: true);
                extracted++;
            }
        }

        return extracted > 0 ? destination : throw new AMPRPackException($"archive contains no AMPR trace inputs: {path}");
    }

    /// <summary>The runtime header with the recommended emulator defines (Python <c>render_runtime_header</c>).</summary>
    /// <param name="result">Profile.</param>
    /// <returns>C header text.</returns>
    public static string RenderRuntimeHeader(AMPRProfileResult result)
    {
        List<string> lines =
        [
            "/*",
            $" * Generated by ampr_pack_profile.py {ToolVersion}.",
            $" * Profile: {result.Name}",
            " * Inputs:",
            .. result.Traces.Select(t => $" *   {t.Name}: commands={t.CommandSha256} index={t.IndexSha256}"),
            " */",
            "#pragma once",
            "",
            $"#define AMPR_EMU_INTERNAL_AMM_POOL_SIZE 0x{result.RecommendedPoolBytes:x}ull",
            $"#define AMPR_EMU_PACK_DECODED_CACHE_BYTES 0x{result.RecommendedCacheBytes:x}ull",
            $"#define AMPR_EMU_PACK_PHYSICAL_CACHE_BYTES 0x{result.RecommendedPhysicalCacheBytes:x}ull",
            $"#define AMPR_EMU_PACK_WORKERS {result.RecommendedRuntimeWorkers}u",
            $"#define AMPR_EMU_PACK_LATENCY_RESERVE_WORKERS {result.RecommendedLatencyReserveWorkers}u",
            "",
        ];
        return string.Join('\n', lines);
    }

    /// <summary>Python <c>_human_size</c>: <c>N B</c>, or two decimals with KiB to TiB.</summary>
    /// <param name="value">Size.</param>
    /// <returns>Text.</returns>
    public static string HumanSize(double value)
    {
        double size = value;
        foreach (string suffix in (string[])["B", "KiB", "MiB", "GiB", "TiB"])
        {
            if (Math.Abs(size) < 1024.0 || suffix == "TiB")
            {
                return suffix == "B"
                    ? $"{(long)Math.Round(size, MidpointRounding.ToEven)} B"
                    : $"{PythonText.FormatFixed(size, 2)} {suffix}";
            }

            size /= 1024.0;
        }

        return $"{PythonText.FormatFixed(size, 2)} TiB";
    }

    /// <summary>Python <c>_size_literal</c>: the largest exact GiB, MiB or KiB unit, else bytes.</summary>
    /// <param name="value">Size.</param>
    /// <returns>TOML size literal.</returns>
    public static string SizeLiteral(long value)
    {
        foreach ((long divisor, string suffix) in (ReadOnlySpan<(long, string)>)[(1L << 30, "GiB"), (1L << 20, "MiB"), (1024, "KiB")])
        {
            if (value != 0 && value % divisor == 0)
            {
                return $"{value / divisor}{suffix}";
            }
        }

        return $"{value}B";
    }

    // ---- trace loading -------------------------------------------------------------------------------------------

    private static void AppendTraceWarning(List<string> warnings, string message, int limit = 200)
    {
        if (warnings.Count < limit)
        {
            warnings.Add(message);
        }
        else if (warnings.Count == limit)
        {
            warnings.Add("additional trace warnings omitted");
        }
    }

    // Python _canonical_relative: "/app0/" stripped, PurePosixPath parts (no empty or "." parts), ".." rejected.
    private static string CanonicalRelative(string path)
    {
        string value = path.Replace('\\', '/');
        if (value.Length >= 6 && AsciiLower(value[..6]) == "/app0/")
        {
            value = value[6..];
        }
        else if (AsciiLower(value) == "/app0")
        {
            value = string.Empty;
        }

        string[] parts = [.. value.TrimStart('/').Split('/').Where(p => p.Length > 0 && p != ".")];
        if (parts.Contains(".."))
        {
            throw new AMPRPackException($"unsafe indexed path: {PythonText.Repr(path)}");
        }

        return parts.Length == 0 ? "." : string.Join('/', parts);
    }

    private static string AsciiLower(string value) => string.Create(value.Length, value, static (span, source) =>
    {
        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            span[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
        }
    });

    private sealed class GatherState
    {
        public int FileId { get; init; }
        public long NextFileOffset { get; set; }
    }

    private static (List<ReadEvent> Events, AMPRTraceSummary Summary, Dictionary<string, AMPRTraceIndexEntry> Entries, List<string> Warnings) StreamOneTrace(
        AMPRTraceSpec spec, int traceOrdinal, Dictionary<int, AMPRTraceIndexEntry> index)
    {
        List<ReadEvent> events = [];
        Dictionary<string, AMPRTraceIndexEntry> pathEntries = [];
        List<string> warnings = [];
        Dictionary<long, GatherState> gatherState = [];
        long records = 0;
        long readCount = 0;
        long decodeErrors = 0;
        ulong? previousSequence = null;
        ulong minMonotonic = 0;
        ulong maxMonotonic = 0;

        using (FileStream handle = new(spec.Commands, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
        {
            long fileOffset = 0;
            byte[] rawHeader = new byte[AMPRCommandLog.HeaderSize];
            while (true)
            {
                int got = handle.ReadAtLeast(rawHeader, rawHeader.Length, throwOnEndOfStream: false);
                if (got == 0)
                {
                    break;
                }

                if (got != AMPRCommandLog.HeaderSize)
                {
                    AppendTraceWarning(warnings, $"truncated header at 0x{fileOffset:x}: {got}/{AMPRCommandLog.HeaderSize}");
                    break;
                }

                if (!rawHeader.AsSpan(0, 8).SequenceEqual(AMPRCommandLog.Magic))
                {
                    throw new AMPRPackException($"{spec.Name}: invalid command-log magic at 0x{fileOffset:x}");
                }

                AMPRCommandLog.Header header = AMPRCommandLog.ReadHeader(rawHeader);
                if (header.Version != AMPRCommandLog.Version)
                {
                    throw new AMPRPackException($"{spec.Name}: unsupported command-log version {header.Version}");
                }

                if (header.HeaderBytes < AMPRCommandLog.HeaderSize)
                {
                    throw new AMPRPackException($"{spec.Name}: invalid header size {header.HeaderBytes} at 0x{fileOffset:x}");
                }

                if (header.RecordBytes < header.HeaderBytes || header.RecordBytes - header.HeaderBytes != header.PayloadBytes)
                {
                    throw new AMPRPackException($"{spec.Name}: inconsistent record sizes at 0x{fileOffset:x}");
                }

                int extraBytes = header.HeaderBytes - AMPRCommandLog.HeaderSize;
                if (extraBytes > 0)
                {
                    byte[] extra = new byte[extraBytes];
                    if (handle.ReadAtLeast(extra, extraBytes, throwOnEndOfStream: false) != extraBytes)
                    {
                        AppendTraceWarning(warnings, "truncated extended header");
                        break;
                    }
                }

                // A truncated journal: read what is left (Python read(n) returns short) instead of allocating
                // the declared size.
                long available = handle.Length - handle.Position;
                byte[] payload = new byte[Math.Min(header.PayloadBytes, available)];
                int payloadRead = handle.ReadAtLeast(payload, payload.Length, throwOnEndOfStream: false);
                if (payloadRead != header.PayloadBytes)
                {
                    AppendTraceWarning(warnings, $"truncated payload for seq={header.Sequence}: {payloadRead}/{header.PayloadBytes}");
                    break;
                }

                records++;
                ulong sequence = header.Sequence;
                if (previousSequence is null)
                {
                    if (sequence != 1)
                    {
                        AppendTraceWarning(warnings, $"sequence starts at {sequence}, expected 1");
                    }
                }
                else if (sequence != previousSequence.Value + 1)
                {
                    AppendTraceWarning(warnings, $"sequence gap/non-monotonic: previous={previousSequence.Value}, current={sequence}");
                }

                previousSequence = sequence;
                if (AMPRCommandLog.Fnv1a64(payload) != header.PayloadHash)
                {
                    AppendTraceWarning(warnings, $"payload hash mismatch for seq={sequence}");
                }

                if (header.MonotonicNs != 0)
                {
                    minMonotonic = minMonotonic == 0 ? header.MonotonicNs : Math.Min(minMonotonic, header.MonotonicNs);
                    maxMonotonic = Math.Max(maxMonotonic, header.MonotonicNs);
                }

                if (header.Domain != AMPRCommandLog.DomainApr)
                {
                    fileOffset += header.RecordBytes;
                    continue;
                }

                int offset = 0;
                int ordinal = 0;
                while (offset < payload.Length)
                {
                    AMPRCommand command;
                    try
                    {
                        command = AMPRCommandLog.Decode(payload, offset);
                    }
                    catch (InvalidDataException exc)
                    {
                        decodeErrors++;
                        AppendTraceWarning(warnings, $"seq={sequence} decode error at 0x{offset:x}: {exc.Message}");
                        break;
                    }

                    long commandBytes = command.Dwords * 4L;
                    if (commandBytes <= 0 || offset + commandBytes > payload.Length)
                    {
                        decodeErrors++;
                        AppendTraceWarning(warnings, $"seq={sequence} decoder returned invalid size {commandBytes}");
                        break;
                    }

                    void Emit(int fileId, long fileReadOffset, long length)
                    {
                        if (!index.TryGetValue(fileId, out AMPRTraceIndexEntry? entry))
                        {
                            AppendTraceWarning(warnings, $"fileId {fileId} absent from AMPRIDX3 at seq={sequence}");
                            return;
                        }

                        string relative;
                        try
                        {
                            relative = CanonicalRelative(entry.Path);
                        }
                        catch (AMPRPackException exc)
                        {
                            AppendTraceWarning(warnings, exc.Message);
                            return;
                        }

                        if (length <= 0 || fileReadOffset < 0)
                        {
                            return;
                        }

                        if (fileReadOffset >= entry.Size)
                        {
                            AppendTraceWarning(warnings, $"read beyond {relative}: offset=0x{fileReadOffset:x} size=0x{entry.Size:x}");
                            return;
                        }

                        if (length > entry.Size - fileReadOffset)
                        {
                            AppendTraceWarning(warnings, $"clamped read for {relative}: offset=0x{fileReadOffset:x} length=0x{length:x} size=0x{entry.Size:x}");
                            length = entry.Size - fileReadOffset;
                        }

                        pathEntries.TryAdd(relative, entry);
                        events.Add(new ReadEvent(traceOrdinal, (long)sequence, ordinal, (long)header.MonotonicNs, header.Priority, entry.Path, relative,
                            fileId, entry.Size, fileReadOffset, length));
                        readCount++;
                    }

                    switch (command.Name)
                    {
                        case "AprReadFile":
                            gatherState[header.Priority] = new GatherState { FileId = (int)command.FileId, NextFileOffset = command.FileOffset + command.Length };
                            Emit((int)command.FileId, command.FileOffset, command.Length);
                            break;
                        case "AprReadGather" or "AprReadGatherScatter":
                            if (gatherState.TryGetValue(header.Priority, out GatherState? state))
                            {
                                Emit(state.FileId, command.FileOffset, command.Length);
                                state.NextFileOffset = command.FileOffset + command.Length;
                            }
                            else
                            {
                                AppendTraceWarning(warnings, $"{command.Name} without active state at seq={sequence}");
                            }

                            break;
                        case "AprReadScatter":
                            if (gatherState.TryGetValue(header.Priority, out GatherState? scatter))
                            {
                                long start = scatter.NextFileOffset;
                                Emit(scatter.FileId, start, command.Length);
                                scatter.NextFileOffset = start + command.Length;
                            }
                            else
                            {
                                AppendTraceWarning(warnings, $"AprReadScatter without active state at seq={sequence}");
                            }

                            break;
                        case "AprResetGatherScatterState":
                            gatherState.Remove(header.Priority);
                            break;
                        default:
                            break;
                    }

                    offset += (int)commandBytes;
                    ordinal++;
                }

                if (header.CommandCount != 0 && ordinal != header.CommandCount)
                {
                    decodeErrors++;
                    AppendTraceWarning(warnings, $"seq={sequence} header command_count={header.CommandCount}, decoded={ordinal}");
                }

                fileOffset += header.RecordBytes;
            }
        }

        AMPRTraceSummary summary = new()
        {
            Name = spec.Name,
            Commands = spec.Commands,
            Index = spec.Index,
            CommandSha256 = Sha256(spec.Commands),
            IndexSha256 = Sha256(spec.Index),
            Records = records,
            Reads = readCount,
            Warnings = [.. warnings],
            DecodeErrors = decodeErrors,
            DurationNs = minMonotonic != 0 && maxMonotonic >= minMonotonic ? (long)(maxMonotonic - minMonotonic) : 0,
        };
        return (events, summary, pathEntries, warnings);
    }

    private static (List<ReadEvent>, List<AMPRTraceSummary>, Dictionary<string, AMPRTraceIndexEntry>, List<string>) LoadTraceEvents(IReadOnlyList<AMPRTraceSpec> traces)
    {
        List<ReadEvent> allEvents = [];
        List<AMPRTraceSummary> summaries = [];
        Dictionary<string, AMPRTraceIndexEntry> pathEntries = [];
        List<string> warnings = [];
        for (int ordinal = 0; ordinal < traces.Count; ordinal++)
        {
            AMPRTraceSpec spec = traces[ordinal];
            Dictionary<int, AMPRTraceIndexEntry> index = AMPRCommandLog.LoadIndex(spec.Index);
            (List<ReadEvent> events, AMPRTraceSummary summary, Dictionary<string, AMPRTraceIndexEntry> traceEntries, List<string> traceWarnings) =
                StreamOneTrace(spec, ordinal, index);
            allEvents.AddRange(events);
            summaries.Add(summary);
            foreach ((string relative, AMPRTraceIndexEntry entry) in traceEntries)
            {
                if (pathEntries.TryGetValue(relative, out AMPRTraceIndexEntry? existing) && existing.Size != entry.Size)
                {
                    warnings.Add($"{spec.Name}: indexed size changed for {relative}: {existing.Size} -> {entry.Size}; using the larger entry");
                    if (entry.Size > existing.Size)
                    {
                        pathEntries[relative] = entry;
                    }
                }
                else
                {
                    pathEntries.TryAdd(relative, entry);
                }
            }

            warnings.AddRange(traceWarnings.Select(w => $"{spec.Name}: {w}"));
        }

        return ([.. allEvents.OrderBy(e => (e.TraceOrdinal, e.Sequence, e.CommandOrdinal))], summaries, pathEntries, warnings);
    }

    // ---- per-file metrics ----------------------------------------------------------------------------------------

    private static double Percentile(List<long> values, double pct)
    {
        if (values.Count == 0)
        {
            return 0.0;
        }

        if (values.Count == 1)
        {
            return values[0];
        }

        List<long> ordered = [.. values.Order()];
        double position = (ordered.Count - 1) * pct / 100.0;
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return ordered[lower];
        }

        double fraction = position - lower;
        return (ordered[lower] * (1.0 - fraction)) + (ordered[upper] * fraction);
    }

    private static long UnionLength(IEnumerable<(long Start, long End)> ranges)
    {
        long total = 0;
        long? currentStart = null;
        long currentEnd = 0;
        foreach ((long start, long end) in ranges.Order())
        {
            if (end <= start)
            {
                continue;
            }

            if (currentStart is null)
            {
                (currentStart, currentEnd) = (start, end);
                continue;
            }

            if (start <= currentEnd)
            {
                currentEnd = Math.Max(currentEnd, end);
            }
            else
            {
                total += currentEnd - currentStart.Value;
                (currentStart, currentEnd) = (start, end);
            }
        }

        return currentStart is null ? total : total + (currentEnd - currentStart.Value);
    }

    private static long CeilDiv(long value, long divisor) => (long)Math.Ceiling((double)value / divisor);

    private static CandidateMetrics CandidateFor(List<ReadEvent> events, long fileSize, long blockSize, string layout, double randomness)
    {
        long requestedBytes = events.Sum(e => e.Length);
        long touchedBytes = 0;
        long totalTouches = 0;
        List<(long, long)> blockRanges = new(events.Count);
        long offsetAligned = 0;
        long lengthAligned = 0;
        foreach (ReadEvent e in events)
        {
            long first = e.Offset / blockSize;
            long lastExclusive = (e.End + blockSize - 1) / blockSize;
            long start = first * blockSize;
            long end = Math.Min(lastExclusive * blockSize, fileSize);
            touchedBytes += Math.Max(0, end - start);
            totalTouches += Math.Max(0, lastExclusive - first);
            blockRanges.Add((first, lastExclusive));
            offsetAligned += e.Offset % blockSize == 0 ? 1 : 0;
            lengthAligned += e.Length % blockSize == 0 ? 1 : 0;
        }

        long uniqueBlocks = UnionLength(blockRanges);
        double amplification = requestedBytes != 0 ? (double)touchedBytes / requestedBytes : 1.0;
        double averageBlocks = events.Count != 0 ? (double)totalTouches / events.Count : 0.0;
        double repeatRatio = totalTouches != 0 ? Math.Max(0.0, 1.0 - ((double)uniqueBlocks / totalTouches)) : 0.0;
        long metadataBytes = CeilDiv(fileSize, blockSize) * ChunkRecordBytes;

        double logAmp = Math.Log2(Math.Max(1.0, amplification));
        double logOps = Math.Log2(Math.Max(1.0, averageBlocks));
        double sizeLog = Math.Log2((double)blockSize / K64);
        double score;
        if (layout == "random")
        {
            score = (3.8 * logAmp) + (0.12 * logOps);
            if (blockSize > K64)
            {
                score += 0.55 * Math.Log2((double)blockSize / K64);
            }

            if (blockSize < 16 * 1024)
            {
                score += 10.0;
            }
        }
        else if (layout == "streaming")
        {
            score = (0.65 * logAmp) + (0.95 * logOps);
            if (blockSize < 128 * 1024)
            {
                score += 0.35 * Math.Log2(128.0 * 1024 / blockSize);
            }

            if (blockSize > 512 * 1024)
            {
                score += 0.15 * Math.Log2((double)blockSize / (512 * 1024));
            }
        }
        else
        {
            score = (2.15 * logAmp) + (0.38 * logOps);
            score += Math.Abs(sizeLog) * 0.06;
            if (blockSize > 128 * 1024)
            {
                score += 0.28 * Math.Log2((double)blockSize / (128 * 1024));
            }

            if (blockSize > K64)
            {
                score += randomness * 0.42 * Math.Log2((double)blockSize / K64);
            }

            if (blockSize < 32 * 1024)
            {
                score += 0.20 * Math.Log2(32.0 * 1024 / blockSize);
            }
        }

        score += (double)metadataBytes / Math.Max(fileSize, 1) * 3.0;
        return new CandidateMetrics(blockSize, touchedBytes, totalTouches, uniqueBlocks, amplification, averageBlocks, repeatRatio,
            events.Count != 0 ? (double)offsetAligned / events.Count : 0.0, events.Count != 0 ? (double)lengthAligned / events.Count : 0.0,
            metadataBytes, score);
    }

    private static string ClassifyLayout(FileMetricsInput m)
    {
        double sequential = m.ExactSequentialRatio + m.NearSequentialRatio;
        bool microIndex = m.P50 <= 256 && m.ReadCount >= 64 && (m.Repeat64k >= 0.10 || sequential >= 0.70);
        if (microIndex)
        {
            return "random";
        }

        if (m.P50 >= K64 && sequential >= 0.76 && m.RandomSeekRatio <= 0.20 && m.Repeat64k <= 0.18)
        {
            return "streaming";
        }

        if (m.P90 >= 256 * 1024 && m.Large256kRatio >= 0.65 && sequential >= 0.86 && m.Repeat64k <= 0.12)
        {
            return "streaming";
        }

        return m.Tiny4kRatio >= 0.50 || m.Small16kRatio >= 0.70 || (m.Small64kRatio >= 0.82 && m.RandomSeekRatio >= 0.30)
            || (m.Repeat64k >= 0.28 && m.P50 <= K64)
            ? "random"
            : "mixed";
    }

    private readonly record struct FileMetricsInput(
        double P50, double P90, double Tiny4kRatio, double Small16kRatio, double Small64kRatio, double Large256kRatio,
        double ExactSequentialRatio, double NearSequentialRatio, double RandomSeekRatio, double Repeat64k, int ReadCount);

    private static FileMetricsInput LayoutInput(FileMetrics m) => new(
        m.P50, m.P90, m.Tiny4kRatio, m.Small16kRatio, m.Small64kRatio, m.Large256kRatio, m.ExactSequentialRatio, m.NearSequentialRatio,
        m.RandomSeekRatio, m.Candidates[K64].RepeatRatio, m.ReadCount);

    private static List<FileMetrics> BuildFileMetrics(List<ReadEvent> events)
    {
        Dictionary<string, List<ReadEvent>> grouped = [];
        foreach (ReadEvent e in events)
        {
            if (!grouped.TryGetValue(e.Relative, out List<ReadEvent>? list))
            {
                grouped[e.Relative] = list = [];
            }

            list.Add(e);
        }

        List<FileMetrics> results = [];
        foreach (string relative in grouped.Keys.Order(PythonCodePointComparer.Instance))
        {
            List<ReadEvent> fileEvents = [.. grouped[relative].OrderBy(e => (e.TraceOrdinal, e.Sequence, e.CommandOrdinal))];
            List<long> lengths = [.. fileEvents.Select(e => e.Length)];
            long requested = lengths.Sum();
            long fileSize = fileEvents.Max(e => e.FileSize);
            long uniqueRequested = UnionLength(fileEvents.Select(e => (e.Offset, e.End)));

            long transitionBytes = 0;
            long exactBytes = 0;
            long nearBytes = 0;
            long randomBytes = 0;
            Dictionary<int, ReadEvent> previousByTrace = [];
            foreach (ReadEvent e in fileEvents)
            {
                previousByTrace.TryGetValue(e.TraceOrdinal, out ReadEvent? previous);
                previousByTrace[e.TraceOrdinal] = e;
                if (previous is null)
                {
                    continue;
                }

                long weight = e.Length;
                transitionBytes += weight;
                if (e.Offset == previous.End)
                {
                    exactBytes += weight;
                    continue;
                }

                long gap = e.Offset - previous.End;
                long nearLimit = Math.Max(DefaultIOPage, Math.Min(previous.Length, 512 * 1024));
                if (gap >= 0 && gap <= nearLimit)
                {
                    nearBytes += weight;
                }
                else if (e.Offset < previous.Offset)
                {
                    randomBytes += weight;
                }
                else if (e.Offset < previous.End)
                {
                    // overlap: counted by Python's overlap ratio, which nothing here uses
                }
                else
                {
                    randomBytes += weight;
                }
            }

            double Share(Func<long, bool> predicate) => (double)lengths.Where(predicate).Sum() / requested;
            double Ratio(long part) => transitionBytes != 0 ? (double)part / transitionBytes : 0.0;
            double tiny4k = Share(l => l <= 4 * 1024);
            double small16k = Share(l => l <= 16 * 1024);
            double small64k = Share(l => l <= K64);
            double large256k = Share(l => l >= 256 * 1024);
            double exactRatio = Ratio(exactBytes);
            double nearRatio = Ratio(nearBytes);
            double randomRatio = Ratio(randomBytes);

            CandidateMetrics provisional64 = CandidateFor(fileEvents, fileSize, K64, "mixed", randomRatio);
            double p50 = Percentile(lengths, 50);
            double p90 = Percentile(lengths, 90);
            string layout = ClassifyLayout(new FileMetricsInput(
                p50, p90, tiny4k, small16k, small64k, large256k, exactRatio, nearRatio, randomRatio, provisional64.RepeatRatio, fileEvents.Count));
            Dictionary<long, CandidateMetrics> candidates = BlockSizes.ToDictionary(size => size, size => CandidateFor(fileEvents, fileSize, size, layout, randomRatio));

            double evidenceReads = Math.Min(1.0, Math.Log10(fileEvents.Count + 1) / 3.0);
            double evidenceBytes = Math.Min(1.0, requested / (64.0 * 1024 * 1024));
            int traceCount = fileEvents.Select(e => e.TraceOrdinal).Distinct().Count();
            double multiTrace = Math.Min(1.0, Math.Max(0, traceCount - 1) / 2.0);
            double confidence = (0.45 * evidenceReads) + (0.35 * evidenceBytes) + (0.20 * multiTrace);
            string confidenceLabel = confidence >= 0.72 ? "high" : confidence >= 0.42 ? "medium" : "low";

            results.Add(new FileMetrics
            {
                Path = fileEvents[0].Path,
                Relative = relative,
                FileSize = fileSize,
                TraceCount = traceCount,
                Events = fileEvents,
                ReadCount = fileEvents.Count,
                RequestedBytes = requested,
                UniqueRequestedBytes = uniqueRequested,
                CoverageRatio = fileSize != 0 ? (double)uniqueRequested / fileSize : 0.0,
                P50 = p50,
                P75 = Percentile(lengths, 75),
                P90 = p90,
                P95 = Percentile(lengths, 95),
                P99 = Percentile(lengths, 99),
                Tiny4kRatio = tiny4k,
                Small16kRatio = small16k,
                Small64kRatio = small64k,
                Large256kRatio = large256k,
                ExactSequentialRatio = exactRatio,
                NearSequentialRatio = nearRatio,
                RandomSeekRatio = randomRatio,
                Candidates = candidates,
                Confidence = confidence,
                ConfidenceLabel = confidenceLabel,
            });
        }

        return results;
    }

    // ---- recommendations -----------------------------------------------------------------------------------------

    // Python PurePosixPath.suffix: from the last dot, unless the name starts with it or ends with it.
    private static string Suffix(string name)
    {
        int i = name.LastIndexOf('.');
        return i > 0 && i < name.Length - 1 ? name[i..] : string.Empty;
    }

    private static string KnownAction(string relative)
    {
        string name = relative[(relative.LastIndexOf('/') + 1)..];
        string lowerName = name.ToLowerInvariant();
        string suffix = Suffix(name).ToLowerInvariant();
        if (KnownLooseBasenames.Contains(lowerName) || KnownLooseSuffixes.Contains(suffix))
        {
            return "loose";
        }

        return KnownStreamStoreSuffixes.Contains(suffix) ? "store" : "compress";
    }

    private static List<FileRecommendation> InitialRecommendations(List<FileMetrics> files, AMPRProfileOptions options)
    {
        if (files.Count == 0)
        {
            return [];
        }

        long readHotThreshold = Math.Max(32, (long)Percentile([.. files.Select(f => (long)f.ReadCount)], 90));
        long byteHotThreshold = Math.Max(16L * 1024 * 1024, (long)Percentile([.. files.Select(f => f.RequestedBytes)], 90));
        List<FileRecommendation> recommendations = [];
        foreach (FileMetrics metrics in files)
        {
            if (metrics.ReadCount < options.MinReads || metrics.RequestedBytes < options.MinRequestedBytes)
            {
                continue;
            }

            string layout = ClassifyLayout(LayoutInput(metrics));
            string action = KnownAction(metrics.Relative);
            if (action == "store")
            {
                layout = "streaming";
            }

            double repeat64k = metrics.Candidates[K64].RepeatRatio;
            bool hot = layout != "streaming"
                && ((metrics.ReadCount >= 16 && repeat64k >= 0.22) || metrics.ReadCount >= readHotThreshold
                    || (metrics.ReadCount >= 8 && metrics.RequestedBytes >= byteHotThreshold));

            IEnumerable<long> allowed = layout switch
            {
                "random" => BlockSizes.Where(s => s <= 128 * 1024),
                "mixed" => BlockSizes.Where(s => s is >= 32 * 1024 and <= 256 * 1024),
                _ => BlockSizes.Where(s => s >= K64),
            };
            long selected = 0;
            (double Score, double Distance, long Size)? best = null;
            foreach (long size in allowed)
            {
                (double, double, long) key = (metrics.Candidates[size].LocalScore, Math.Abs(Math.Log2((double)size / K64)), size);
                if (best is null || key.CompareTo(best.Value) < 0)
                {
                    best = key;
                    selected = size;
                }
            }

            if (metrics.P50 <= 256 && metrics.ReadCount >= 64)
            {
                selected = 16 * 1024;
                layout = "random";
                hot = true;
            }

            string group = action == "loose" ? "loose" : action == "store" || layout == "streaming" ? "stream" : layout == "random" ? "metadata" : "bulk";
            CandidateMetrics chosen = metrics.Candidates[selected];
            string reason = $"{layout}; p50={HumanSize(metrics.P50)}, sequential={Percent(metrics.ExactSequentialRatio + metrics.NearSequentialRatio, 0)}, " +
                $"repeat64={Percent(repeat64k, 0)}, amp={PythonText.FormatFixed(chosen.Amplification, 2)}x";
            recommendations.Add(new FileRecommendation
            {
                Metrics = metrics,
                Action = action,
                Layout = layout,
                BlockSize = selected,
                Hot = hot,
                Group = group,
                Reason = reason,
            });
        }

        return recommendations;
    }

    private static void EnforceIndexBudget(List<FileRecommendation> recommendations, long budget, string strategy)
    {
        List<FileRecommendation> packed = [.. recommendations.Where(r => r.Action != "loose")];
        if (packed.Count == 0)
        {
            return;
        }

        double strategyWeight = strategy switch
        {
            "conservative" => 1.35,
            "balanced" => 1.0,
            "aggressive" => 0.70,
            _ => throw new AMPRPackException($"unknown strategy: {strategy}"),
        };
        PriorityQueue<(int Index, long NextSize), (double Ratio, int Generation, int Index, long NextSize)> heap = new(Comparer<(double, int, int, long)>.Default);
        int[] generations = new int[packed.Count];

        void Push(int index)
        {
            FileRecommendation recommendation = packed[index];
            int currentPos = Array.IndexOf(BlockSizes, recommendation.BlockSize);
            if (currentPos < 0 || currentPos + 1 >= BlockSizes.Length)
            {
                return;
            }

            long nextSize = BlockSizes[currentPos + 1];
            CandidateMetrics current = recommendation.Metrics.Candidates[recommendation.BlockSize];
            CandidateMetrics next = recommendation.Metrics.Candidates[nextSize];
            long saved = current.MetadataBytes - next.MetadataBytes;
            if (saved <= 0)
            {
                return;
            }

            double scoreCost = Math.Max(0.0, next.LocalScore - current.LocalScore);
            double importance = 1.0;
            if (recommendation.Hot)
            {
                importance *= 2.5;
            }

            if (recommendation.Layout == "random")
            {
                importance *= 1.8;
            }
            else if (recommendation.Layout == "streaming")
            {
                importance *= 0.55;
            }

            importance *= 0.75 + (0.5 * recommendation.Metrics.Confidence);
            double ratio = scoreCost * importance * strategyWeight / saved;
            heap.Enqueue((index, nextSize), (ratio, generations[index], index, nextSize));
        }

        for (int index = 0; index < packed.Count; index++)
        {
            Push(index);
        }

        long total = packed.Sum(r => r.MetadataBytes);
        while (total > budget && heap.TryDequeue(out (int Index, long NextSize) item, out (double, int Generation, int, long) priority))
        {
            if (priority.Generation != generations[item.Index])
            {
                continue;
            }

            FileRecommendation recommendation = packed[item.Index];
            if (item.NextSize <= recommendation.BlockSize)
            {
                continue;
            }

            total -= recommendation.MetadataBytes;
            recommendation.BlockSize = item.NextSize;
            total += recommendation.MetadataBytes;
            generations[item.Index]++;
            Push(item.Index);
        }
    }

    private static List<long> SamplePositions(long fileSize, long blockSize, int count)
    {
        if (fileSize <= 0 || count <= 0)
        {
            return [];
        }

        if (fileSize <= blockSize || count == 1)
        {
            return [0];
        }

        long maxStart = Math.Max(0, fileSize - blockSize);
        SortedSet<long> positions = [];
        for (int index = 0; index < count; index++)
        {
            positions.Add((long)Math.Round((double)maxStart * index / (count - 1), MidpointRounding.ToEven));
        }

        return [.. positions.Select(p => p - (p % blockSize)).Order()];
    }

    private static void SampleCompressibility(List<FileRecommendation> recommendations, AMPRProfileOptions options, List<string> warnings)
    {
        if (options.ContentRoot is null || options.SampleBudget <= 0)
        {
            return;
        }

        using LZ4Encoder codec = new();
        string root = Path.GetFullPath(options.ContentRoot);
        long remaining = options.SampleBudget;
        foreach (FileRecommendation recommendation in recommendations.Where(r => r.Action == "compress")
            .OrderByDescending(r => (r.Metrics.RequestedBytes, r.Metrics.ReadCount)))
        {
            if (remaining <= 0)
            {
                break;
            }

            string source = Path.GetFullPath(Path.Combine(root, recommendation.Metrics.Relative));
            string relativeToRoot = Path.GetRelativePath(root, source);
            if (relativeToRoot == ".." || relativeToRoot.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relativeToRoot))
            {
                warnings.Add($"content sample escaped root: {source}");
                continue;
            }

            if (!File.Exists(source))
            {
                continue;
            }

            long rawTotal = 0;
            long storedTotal = 0;
            try
            {
                using FileStream handle = File.OpenRead(source);
                foreach (long position in SamplePositions(recommendation.Metrics.FileSize, recommendation.BlockSize, options.SampleBlocksPerFile))
                {
                    if (remaining <= 0)
                    {
                        break;
                    }

                    int length = (int)Math.Min(recommendation.BlockSize, remaining);
                    handle.Seek(position, SeekOrigin.Begin);
                    byte[] buffer = new byte[length];
                    int got = handle.ReadAtLeast(buffer, length, throwOnEndOfStream: false);
                    if (got == 0)
                    {
                        continue;
                    }

                    ReadOnlySpan<byte> raw = buffer.AsSpan(0, got);
                    byte[] compressed = options.SampleMode == "hc"
                        ? codec.CompressHC(raw, Math.Clamp(options.SampleLevel, 1, 12))
                        : codec.CompressFast(raw, Math.Max(1, options.SampleAcceleration));
                    rawTotal += got;
                    storedTotal += Math.Min(got, compressed.Length);
                    remaining -= got;
                }
            }
            catch (IOException exc)
            {
                warnings.Add($"cannot sample {recommendation.Metrics.Relative}: {exc.Message}");
                continue;
            }

            if (rawTotal == 0)
            {
                continue;
            }

            double ratio = (double)storedTotal / rawTotal;
            recommendation.SampledRatio = ratio;
            recommendation.SampledBytes = rawTotal;
            if (ratio >= 0.94)
            {
                if (recommendation.Metrics.FileSize >= 64L * 1024 * 1024)
                {
                    recommendation.Action = "loose";
                    recommendation.Hot = false;
                    recommendation.Reason += $"; sampled LZ4 ratio={Percent(ratio, 1)}, auto-loose";
                }
                else
                {
                    recommendation.Action = "store";
                    recommendation.Layout = recommendation.Layout == "streaming" ? "streaming" : "mixed";
                    recommendation.Group = recommendation.Layout == "streaming" ? "stream" : "bulk";
                    recommendation.Hot = recommendation.Layout != "streaming" && recommendation.Hot;
                    recommendation.Reason += $"; sampled LZ4 ratio={Percent(ratio, 1)}, store";
                }
            }
            else
            {
                recommendation.Reason += $"; sampled LZ4 ratio={Percent(ratio, 1)}";
            }
        }
    }

    private static long EstimateIndexBytes(IReadOnlyList<FileRecommendation> recommendations, int packCount, Dictionary<string, AMPRTraceIndexEntry> indexEntries)
    {
        long chunkBytes = recommendations.Where(r => r.Action != "loose").Sum(r => r.MetadataBytes);
        long fileBytes = (long)indexEntries.Count * FileRecordBytes;
        long stringBytes = indexEntries.Keys.Sum(p => (long)Encoding.UTF8.GetByteCount(p) + 1);
        return chunkBytes + fileBytes + ((long)packCount * PackRecordBytes) + stringBytes + 4096;
    }

    private static int ChooseLanes(List<FileRecommendation> recommendations, int requested)
    {
        if (requested > 0)
        {
            return requested;
        }

        List<FileRecommendation> packed = [.. recommendations.Where(r => r.Action != "loose")];
        long totalSize = packed.Sum(r => r.Metrics.FileSize);
        int fileCount = packed.Count;
        return fileCount <= 1 ? 1
            : totalSize < 2L * 1024 * 1024 * 1024 && fileCount < 64 ? 2
            : totalSize < 16L * 1024 * 1024 * 1024 && fileCount < 1024 ? 3
            : 4;
    }

    private static List<(string Name, int PackCount, long MaxPackSize)> BuildGroups(List<FileRecommendation> recommendations, int lanes)
    {
        List<(string, int, long)> result = [];
        foreach (string groupName in (string[])["metadata", "bulk", "stream"])
        {
            List<FileRecommendation> items = [.. recommendations.Where(r => r.Action != "loose" && r.Group == groupName)];
            if (items.Count == 0)
            {
                continue;
            }

            long totalSize = items.Sum(i => i.Metrics.FileSize);
            (int packCount, long maxPackSize) = groupName switch
            {
                "bulk" => (Math.Min(lanes, Math.Max(1, items.Count)), 16L << 30),
                "metadata" => (Math.Min(Math.Max(1, Math.Min(lanes, totalSize < 8L << 30 ? 2 : 4)), items.Count), 8L << 30),
                _ => (Math.Min(Math.Max(1, Math.Min(lanes, 2)), items.Count), 16L << 30),
            };
            result.Add((groupName, packCount, maxPackSize));
        }

        return result;
    }

    // ---- decoded-cache model -------------------------------------------------------------------------------------

    private static IEnumerable<(long BlockIndex, long RawSize, long Overlap)> BlockOverlaps(ReadEvent e, long blockSize)
    {
        long first = e.Offset / blockSize;
        long last = (e.End - 1) / blockSize;
        for (long blockIndex = first; blockIndex <= last; blockIndex++)
        {
            long blockStart = blockIndex * blockSize;
            long blockEnd = Math.Min(blockStart + blockSize, e.FileSize);
            long overlap = Math.Max(0, Math.Min(e.End, blockEnd) - Math.Max(e.Offset, blockStart));
            if (overlap != 0)
            {
                yield return (blockIndex, blockEnd - blockStart, overlap);
            }
        }
    }

    private static long BlockTouchCount(ReadEvent e, long blockSize) =>
        e.Length <= 0 || blockSize <= 0 ? 0 : ((e.End - 1) / blockSize) - (e.Offset / blockSize) + 1;

    private sealed class AccessStream
    {
        public List<ulong> Keys { get; } = [];
        public List<long> RawSizes { get; } = [];
        public List<long> Overlaps { get; } = [];
        public List<bool> Hot { get; } = [];
        public List<bool> ResetBefore { get; } = [];
        public long TotalTouches { get; set; }
        public long SampledWindows { get; set; }
    }

    private static AccessStream BuildAccessStream(List<ReadEvent> events, Dictionary<string, FileRecommendation> byPath, long maxTouches)
    {
        List<(ReadEvent Event, FileRecommendation Recommendation, long Touches)> eligible = [];
        List<long> cumulativeEnds = [];
        long totalTouches = 0;
        Dictionary<string, ulong> fileOrdinals = [];
        foreach (ReadEvent e in events)
        {
            if (!byPath.TryGetValue(e.Relative, out FileRecommendation? recommendation) || recommendation.Action == "loose" || recommendation.Layout == "streaming")
            {
                continue;
            }

            long touches = BlockTouchCount(e, recommendation.BlockSize);
            if (touches <= 0)
            {
                continue;
            }

            fileOrdinals.TryAdd(e.Relative, (ulong)fileOrdinals.Count + 1);
            totalTouches += touches;
            eligible.Add((e, recommendation, touches));
            cumulativeEnds.Add(totalTouches);
        }

        AccessStream stream = new();
        if (eligible.Count == 0)
        {
            return stream;
        }

        long AppendSlice(ReadEvent e, FileRecommendation recommendation, long firstTouch, long touchCount, bool reset)
        {
            long appended = 0;
            ulong fileOrdinal = fileOrdinals[e.Relative];
            long stopTouch = firstTouch + touchCount;
            long localIndex = 0;
            foreach ((long blockIndex, long rawSize, long overlap) in BlockOverlaps(e, recommendation.BlockSize))
            {
                if (localIndex >= stopTouch)
                {
                    break;
                }

                if (localIndex++ < firstTouch)
                {
                    continue;
                }

                if (blockIndex >= 1L << 48)
                {
                    throw new AMPRPackException($"cache simulation block index is too large: {e.Relative} block={blockIndex}");
                }

                stream.Keys.Add((fileOrdinal << 48) | (ulong)blockIndex);
                stream.RawSizes.Add(rawSize);
                stream.Overlaps.Add(overlap);
                stream.Hot.Add(recommendation.Hot);
                stream.ResetBefore.Add(reset && appended == 0);
                appended++;
            }

            return appended;
        }

        if (maxTouches <= 0 || totalTouches <= maxTouches)
        {
            bool first = true;
            foreach ((ReadEvent e, FileRecommendation recommendation, long touches) in eligible)
            {
                AppendSlice(e, recommendation, 0, touches, first);
                first = false;
            }

            stream.SampledWindows = 1;
        }
        else
        {
            long windowCount = Math.Min(64, Math.Max(1, maxTouches / 4096));
            long windowBudget = Math.Max(1, maxTouches / windowCount);
            long sampledWindows = 0;
            for (long windowIndex = 0; windowIndex < windowCount; windowIndex++)
            {
                long segmentStart = (long)((Int128)totalTouches * windowIndex / windowCount);
                long segmentEnd = (long)((Int128)totalTouches * (windowIndex + 1) / windowCount);
                long segmentSize = Math.Max(0, segmentEnd - segmentStart);
                if (segmentSize == 0)
                {
                    continue;
                }

                long budget = Math.Min(windowBudget, segmentSize);
                long startTouch = segmentStart + ((segmentSize - budget) / 2);
                long endTouch = startTouch + budget;
                int eventIndex = BisectRight(cumulativeEnds, startTouch);
                bool windowFirst = true;
                while (eventIndex < eligible.Count)
                {
                    long eventStart = eventIndex != 0 ? cumulativeEnds[eventIndex - 1] : 0;
                    if (eventStart >= endTouch)
                    {
                        break;
                    }

                    (ReadEvent e, FileRecommendation recommendation, long touches) = eligible[eventIndex];
                    long localStart = Math.Max(0, startTouch - eventStart);
                    long localEnd = Math.Min(touches, endTouch - eventStart);
                    if (localEnd > localStart && AppendSlice(e, recommendation, localStart, localEnd - localStart, windowFirst) != 0)
                    {
                        windowFirst = false;
                    }

                    eventIndex++;
                }

                if (!windowFirst)
                {
                    sampledWindows++;
                }
            }

            stream.SampledWindows = sampledWindows;
        }

        stream.TotalTouches = totalTouches;
        return stream;
    }

    private static int BisectRight(List<long> values, long x)
    {
        int lo = 0;
        int hi = values.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (x < values[mid])
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return lo;
    }

    private static List<CacheSimulation> SimulateCache(List<ReadEvent> events, Dictionary<string, FileRecommendation> byPath, IReadOnlyList<long> capacities, long maxTouches)
    {
        AccessStream stream = BuildAccessStream(events, byPath, maxTouches);
        List<CacheSimulation> simulations = [];
        int sampledTouches = stream.Keys.Count;
        foreach (long capacity in capacities)
        {
            LinkedList<(ulong Key, long Size)> order = [];
            Dictionary<ulong, LinkedListNode<(ulong Key, long Size)>> cache = [];
            HashSet<ulong> seenOnce = [];
            long resident = 0;
            long requested = 0;
            long hitBytes = 0;
            long hits = 0;
            long misses = 0;
            for (int index = 0; index < sampledTouches; index++)
            {
                if (stream.ResetBefore[index])
                {
                    order.Clear();
                    cache.Clear();
                    seenOnce.Clear();
                    resident = 0;
                }

                ulong key = stream.Keys[index];
                long rawSize = stream.RawSizes[index];
                long overlap = stream.Overlaps[index];
                requested += overlap;
                if (cache.TryGetValue(key, out LinkedListNode<(ulong Key, long Size)>? node))
                {
                    order.Remove(node);
                    order.AddLast(node);
                    hitBytes += overlap;
                    hits++;
                    continue;
                }

                misses++;
                bool admit = stream.Hot[index] || seenOnce.Contains(key);
                if (!admit)
                {
                    seenOnce.Add(key);
                    continue;
                }

                seenOnce.Remove(key);
                if (rawSize > capacity)
                {
                    continue;
                }

                while (resident + rawSize > capacity && order.First is { } victim)
                {
                    order.RemoveFirst();
                    cache.Remove(victim.Value.Key);
                    resident -= victim.Value.Size;
                }

                if (resident + rawSize <= capacity)
                {
                    cache[key] = order.AddLast((key, rawSize));
                    resident += rawSize;
                }
            }

            simulations.Add(new CacheSimulation(capacity, requested, hitBytes, misses, hits, sampledTouches, stream.TotalTouches, stream.SampledWindows));
        }

        return simulations;
    }

    private static long ChooseCacheSize(List<CacheSimulation> simulations)
    {
        if (simulations.Count == 0)
        {
            return 64L * 1024 * 1024;
        }

        double bestRatio = simulations.Max(s => s.HitRatio);
        if (bestRatio < 0.08)
        {
            return simulations.Where(s => s.Capacity >= 64L * 1024 * 1024).Select(s => (long?)s.Capacity).Min() ?? simulations[0].Capacity;
        }

        long Nearest(long target) => simulations.MinBy(s => (Math.Abs(s.Capacity - target), s.Capacity))!.Capacity;
        if (bestRatio < 0.15)
        {
            return Nearest(128L * 1024 * 1024);
        }

        if (bestRatio < 0.25)
        {
            return Nearest(256L * 1024 * 1024);
        }

        CacheSimulation? previous = null;
        foreach (CacheSimulation simulation in simulations)
        {
            if (simulation.Capacity < 64L * 1024 * 1024)
            {
                previous = simulation;
                continue;
            }

            bool withinBest = bestRatio - simulation.HitRatio <= 0.03;
            double marginal = previous is not null ? simulation.HitRatio - previous.HitRatio : simulation.HitRatio;
            if (withinBest || (simulation.HitRatio >= 0.30 && marginal < 0.05))
            {
                return simulation.Capacity;
            }

            previous = simulation;
        }

        return simulations[^1].Capacity;
    }

    // Python >= 3.12 sum() of floats: Neumaier compensated summation.
    private static double PythonFloatSum(IEnumerable<double> values)
    {
        double result = 0.0;
        double compensation = 0.0;
        bool first = true;
        foreach (double x in values)
        {
            if (first)
            {
                result = 0 + x;
                first = false;
                continue;
            }

            double t = result + x;
            compensation += Math.Abs(result) >= Math.Abs(x) ? result - t + x : x - t + result;
            result = t;
        }

        return compensation != 0 && double.IsFinite(compensation) ? result + compensation : result;
    }

    private static long ChoosePhysicalPageCacheSize(List<FileRecommendation> recommendations)
    {
        List<FileRecommendation> eligible = [.. recommendations.Where(r => r.Action != "loose" && r.Layout != "streaming")];
        if (eligible.Count == 0)
        {
            return 8L * 1024 * 1024;
        }

        long requested = eligible.Sum(i => i.Metrics.RequestedBytes);
        if (requested <= 0)
        {
            return 8L * 1024 * 1024;
        }

        double weightedRepeat = PythonFloatSum(eligible.Select(i => i.Metrics.RequestedBytes * i.Metrics.Candidates[K64].RepeatRatio)) / requested;
        double weightedSmall = PythonFloatSum(eligible.Select(i => i.Metrics.RequestedBytes * i.Metrics.Small64kRatio)) / requested;
        double weightedRandom = PythonFloatSum(eligible.Select(i => i.Metrics.RequestedBytes * i.Metrics.RandomSeekRatio)) / requested;
        double pressure = weightedRepeat + (0.5 * weightedSmall * weightedRandom);
        if (requested >= 16L * 1024 * 1024 * 1024 && pressure >= 0.20)
        {
            return 64L * 1024 * 1024;
        }

        return requested >= 2L * 1024 * 1024 * 1024 || pressure >= 0.10 ? 32L * 1024 * 1024 : 16L * 1024 * 1024;
    }

    private static int ChooseRuntimeWorkers(List<ReadEvent> events, List<FileRecommendation> recommendations, int requested)
    {
        if (requested > 0)
        {
            return requested;
        }

        HashSet<string> selectedPaths = [.. recommendations.Where(r => r.Action != "loose").Select(r => r.Metrics.Relative)];
        List<ReadEvent> selectedEvents = [.. events.Where(e => selectedPaths.Contains(e.Relative))];
        if (selectedEvents.Count == 0)
        {
            return 2;
        }

        Dictionary<string, long> bytesByPath = [];
        long switches = 0;
        string? previous = null;
        foreach (ReadEvent e in selectedEvents)
        {
            bytesByPath[e.Relative] = bytesByPath.GetValueOrDefault(e.Relative) + e.Length;
            if (previous is not null && previous != e.Relative)
            {
                switches++;
            }

            previous = e.Relative;
        }

        long totalBytes = bytesByPath.Values.Sum();
        double dominantShare = totalBytes != 0 ? (double)bytesByPath.Values.Max() / totalBytes : 1.0;
        double switchRatio = (double)switches / Math.Max(1, selectedEvents.Count - 1);
        int fileCount = bytesByPath.Count;
        return fileCount >= 1000 && switchRatio >= 0.70 ? 8
            : fileCount >= 128 && switchRatio >= 0.50 ? 6
            : dominantShare >= 0.85 && fileCount <= 8 ? 2
            : 4;
    }

    private static int ChooseLatencyReserveWorkers(List<ReadEvent> events, List<FileRecommendation> recommendations, int runtimeWorkers)
    {
        if (runtimeWorkers <= 1)
        {
            return 0;
        }

        HashSet<string> selected = [.. recommendations.Where(r => r.Action != "loose").Select(r => r.Metrics.Relative)];
        List<ReadEvent> selectedEvents = [.. events.Where(e => selected.Contains(e.Relative))];
        if (selectedEvents.Count == 0)
        {
            return 0;
        }

        double total = selectedEvents.Count;
        double smallRatio = selectedEvents.Count(e => e.Length <= K64) / total;
        double tinyRatio = selectedEvents.Count(e => e.Length <= 16 * 1024) / total;
        double bulkRatio = selectedEvents.Count(e => e.Length >= 256 * 1024) / total;
        return smallRatio >= 0.45 || tinyRatio >= 0.20 || (smallRatio >= 0.25 && bulkRatio >= 0.20) ? 1 : 0;
    }

    private static long RoundPoolSize(long required)
    {
        const long quantum = 128L * 1024 * 1024;
        return Math.Max(256L * 1024 * 1024, CeilDiv(required, quantum) * quantum);
    }

    // ---- rule emission -------------------------------------------------------------------------------------------

    private static List<string> ProfileComment(IEnumerable<FileRecommendation> recommendations)
    {
        List<FileRecommendation> items = [.. recommendations];
        long reads = items.Sum(r => (long)r.Metrics.ReadCount);
        long requested = items.Sum(r => r.Metrics.RequestedBytes);
        string confidence = string.Join(", ", items.GroupBy(r => r.Metrics.ConfidenceLabel)
            .OrderBy(g => g.Key, PythonCodePointComparer.Instance).Select(g => $"{g.Key}:{g.Count()}"));
        return [$"observed files={items.Count}, reads={reads}, submitted={HumanSize(requested)}", "confidence=" + confidence];
    }

    private static List<RuleEmission> DirectoryGeneralization(List<FileRecommendation> recommendations, IEnumerable<string> allIndexPaths, AMPRProfileOptions options)
    {
        Dictionary<string, FileRecommendation> byPath = [];
        foreach (FileRecommendation recommendation in recommendations.Where(r => r.Action != "loose"))
        {
            byPath[recommendation.Metrics.Relative] = recommendation;
        }

        List<RuleEmission> emissions = [];

        void AppendPaths(ProfileKey key, List<string> paths)
        {
            if (options.ExternalizePaths != 0 && paths.Count > options.ExternalizePaths)
            {
                emissions.Add(new RuleEmission(key, paths, ProfileComment(paths.Select(p => byPath[p]))));
                return;
            }

            for (int start = 0; start < paths.Count; start += options.MaxRuleFiles)
            {
                List<string> subset = paths.GetRange(start, Math.Min(options.MaxRuleFiles, paths.Count - start));
                emissions.Add(new RuleEmission(key, subset, ProfileComment(subset.Select(p => byPath[p]))));
            }
        }

        void AppendByProfile(IEnumerable<FileRecommendation> items)
        {
            Dictionary<ProfileKey, List<FileRecommendation>> byProfile = [];
            foreach (FileRecommendation recommendation in items)
            {
                if (!byProfile.TryGetValue(recommendation.Key, out List<FileRecommendation>? list))
                {
                    byProfile[recommendation.Key] = list = [];
                }

                list.Add(recommendation);
            }

            foreach (ProfileKey key in byProfile.Keys.Order())
            {
                AppendPaths(key, [.. byProfile[key].Select(r => r.Metrics.Relative).Order(PythonCodePointComparer.Instance)]);
            }
        }

        if (options.PatternMode == "exact")
        {
            AppendByProfile(byPath.Values);
            return emissions;
        }

        Dictionary<string, int> totalByPrefix = [];
        foreach (string relative in allIndexPaths)
        {
            foreach (string prefix in Prefixes(relative))
            {
                totalByPrefix[prefix] = totalByPrefix.GetValueOrDefault(prefix) + 1;
            }
        }

        // Insertion-ordered counters, like Python's dict and Counter.
        Dictionary<string, List<(ProfileKey Key, int Count)>> profileByPrefix = [];
        Dictionary<(string, ProfileKey), List<string>> pathsByPrefixProfile = [];
        foreach ((string relative, FileRecommendation recommendation) in byPath)
        {
            foreach (string prefix in Prefixes(relative))
            {
                if (!profileByPrefix.TryGetValue(prefix, out List<(ProfileKey Key, int Count)>? counts))
                {
                    profileByPrefix[prefix] = counts = [];
                }

                int at = counts.FindIndex(c => c.Key == recommendation.Key);
                if (at < 0)
                {
                    counts.Add((recommendation.Key, 1));
                }
                else
                {
                    counts[at] = (recommendation.Key, counts[at].Count + 1);
                }

                if (!pathsByPrefixProfile.TryGetValue((prefix, recommendation.Key), out List<string>? paths))
                {
                    pathsByPrefixProfile[(prefix, recommendation.Key)] = paths = [];
                }

                paths.Add(relative);
            }
        }

        double threshold = options.PatternMode == "directory" ? Math.Min(options.GeneralizeCoverage, 0.50) : options.GeneralizeCoverage;
        List<(int Savings, int Depth, string Prefix, ProfileKey Key, HashSet<string> Covered)> candidates = [];
        foreach ((string prefix, List<(ProfileKey Key, int Count)> counts) in profileByPrefix)
        {
            int total = totalByPrefix.GetValueOrDefault(prefix);
            if (total <= 0)
            {
                continue;
            }

            (ProfileKey key, int matching) = counts.MaxBy(c => c.Count);
            int observed = counts.Sum(c => c.Count);
            double coverage = (double)observed / total;
            double purity = (double)matching / observed;
            if (matching >= options.GeneralizeMinFiles && coverage >= threshold && purity >= 0.90)
            {
                HashSet<string> covered = [.. pathsByPrefixProfile[(prefix, key)]];
                candidates.Add((covered.Count - 1, prefix.Split('/').Length, prefix, key, covered));
            }
        }

        HashSet<string> coveredPaths = [];
        foreach ((_, _, string prefix, ProfileKey key, HashSet<string> covered) in candidates.OrderByDescending(c => (c.Savings, c.Depth)))
        {
            int remaining = covered.Count(p => !coveredPaths.Contains(p));
            if (remaining < options.GeneralizeMinFiles)
            {
                continue;
            }

            emissions.Add(new RuleEmission(key, [$"{prefix}/**"],
            [
                $"generalised from {remaining} observed files under {prefix}",
                $"indexed coverage={Percent((double)remaining / Math.Max(1, totalByPrefix[prefix]), 0)}",
            ]));
            coveredPaths.UnionWith(covered);
        }

        AppendByProfile(byPath.Where(p => !coveredPaths.Contains(p.Key)).Select(p => p.Value));
        return emissions;
    }

    // "a/b/c" -> "a", "a/b" (PurePosixPath parents below the full path).
    private static IEnumerable<string> Prefixes(string relative)
    {
        string[] parts = relative.Split('/');
        for (int depth = 1; depth < parts.Length; depth++)
        {
            yield return string.Join('/', parts[..depth]);
        }
    }

    // ---- formatting ----------------------------------------------------------------------------------------------

    private static readonly PythonCodePointComparer StringKey = PythonCodePointComparer.Instance;

    private static string Distribution<T>(IEnumerable<T> values, IComparer<T> order, Func<T, string> format) where T : notnull =>
        string.Join(", ", values.GroupBy(v => v).OrderBy(g => g.Key, order).Select(g => $"{format(g.Key)}: {g.Count()}"));

    private static string Percent(double value, int decimals) => PythonText.FormatFixed(value * 100.0, decimals) + "%";

    private static string Thousands(long value) => value.ToString("#,0", CultureInfo.InvariantCulture);

    private static string TomlString(string value) => PythonSortedJson.Indented(value);

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    // Python Path ordering of discovered journals: by path components, code point order.
    private static int ComparePathParts(string[] a, string[] b)
    {
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            int c = PythonCodePointComparer.Instance.Compare(a[i], b[i]);
            if (c != 0)
            {
                return c;
            }
        }

        return a.Length.CompareTo(b.Length);
    }
}
