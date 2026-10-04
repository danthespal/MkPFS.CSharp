using System.Text;
using MkPFS.Core.AMPR;
using MkPFS.Core.Util;
using Tomlyn.Model;

namespace MkPFS.Build.AMPRPack;

/// <summary>A packing group (<c>[groups.&lt;name&gt;]</c>, Python <c>GroupConfig</c>).</summary>
/// <param name="Name">Group name.</param>
/// <param name="PackCount">Pack lanes (1..65535).</param>
/// <param name="Assignment"><c>balanced</c>, <c>hash</c> or <c>round_robin</c>.</param>
/// <param name="MaxPackSize">Volume size limit; 0 means unlimited.</param>
/// <param name="StripeLargeFiles">Stripe large files across lanes.</param>
/// <param name="StripeThreshold">Smallest file that is striped.</param>
/// <param name="StripeGroupBlocks">Blocks per stripe.</param>
/// <param name="IOPageSize">Per-group I/O page size; 0 uses the global one.</param>
public sealed record AMPRGroupConfig(
    string Name,
    long PackCount = 1,
    string Assignment = "balanced",
    long MaxPackSize = 0,
    bool StripeLargeFiles = false,
    long StripeThreshold = 256L * 1024 * 1024,
    long StripeGroupBlocks = 8,
    long IOPageSize = 0);

/// <summary>An include/exclude rule (<c>[[rule]]</c>, Python <c>Rule</c>).</summary>
/// <param name="Action"><c>compress</c>, <c>store</c> or <c>loose</c>.</param>
/// <param name="Include">Include globs.</param>
/// <param name="Exclude">Exclude globs.</param>
/// <param name="BlockShift">log2 of the block size.</param>
/// <param name="Group">Packing group.</param>
/// <param name="Mode">LZ4 mode, <c>fast</c> or <c>hc</c>.</param>
/// <param name="Level">HC level.</param>
/// <param name="Acceleration">Fast-mode acceleration.</param>
/// <param name="MinSavingsBytes">Smallest saving that keeps a compressed block.</param>
/// <param name="MinSavingsRatio">Smallest saving ratio that keeps a compressed block.</param>
/// <param name="IONeutralMinSavingsBytes">Smallest saving when RAW would use the same I/O pages.</param>
/// <param name="IONeutralMinSavingsRatio">Smallest saving ratio when RAW would use the same I/O pages.</param>
/// <param name="Layout"><c>auto</c>, <c>random</c>, <c>mixed</c> or <c>streaming</c>.</param>
/// <param name="Streaming">Hint for the <c>auto</c> layout.</param>
/// <param name="Hot">Hint for the <c>auto</c> layout and immediate cache admission.</param>
/// <param name="ForcePack">Never auto-loose matched files.</param>
public sealed record AMPRRule(
    string Action,
    IReadOnlyList<string> Include,
    IReadOnlyList<string> Exclude,
    int BlockShift = 16,
    string Group = "default",
    string Mode = "hc",
    long Level = 12,
    long Acceleration = 1,
    long MinSavingsBytes = 64,
    double MinSavingsRatio = 0.01,
    long IONeutralMinSavingsBytes = 8192,
    double IONeutralMinSavingsRatio = 0.125,
    string Layout = "auto",
    bool Streaming = false,
    bool Hot = false,
    bool ForcePack = false)
{
    /// <summary>A loose rule (Python <c>Rule(action="loose", block_shift=...)</c>).</summary>
    /// <param name="blockShift">Block shift to keep.</param>
    /// <returns>The rule.</returns>
    public static AMPRRule Loose(int blockShift) => new("loose", ["**"], [], blockShift);

    /// <summary>Whether a relative path is included and not excluded.</summary>
    /// <param name="relativePath">Path relative to <c>/app0</c>.</param>
    /// <returns><see langword="true"/> when the rule applies.</returns>
    public bool Matches(string relativePath) =>
        AMPRGlob.Matches(relativePath, Include) && !AMPRGlob.Matches(relativePath, Exclude);

    /// <summary>The layout with <c>auto</c> resolved: streaming, then hot (random), else mixed.</summary>
    /// <returns>Layout.</returns>
    public string ResolvedLayout() => Layout != "auto" ? Layout : Streaming ? "streaming" : Hot ? "random" : "mixed";
}

/// <summary>
/// Asset-pack build configuration (Python <c>BuildConfig</c> and <c>load_config</c>). Keys are read leniently like
/// the oracle (unknown keys are ignored) and converted with Python semantics; validation order and messages match.
/// </summary>
public sealed class AMPRPackConfig
{
    /// <summary>ampr_pack tool version; part of the build id.</summary>
    public const string ToolVersion = "4.0";

    /// <summary>Default manifest name.</summary>
    public const string DefaultIndexName = "ampr_assets.index";

    /// <summary>Default volume name pattern.</summary>
    public const string DefaultPackPattern = "ampr_assets-{id:03d}.pak";

    private static readonly string[] RuntimeKeys = ["decoded_cache_bytes", "physical_cache_bytes", "workers", "latency_reserve_workers"];

    /// <summary>Runtime settings written as <c>&lt;index&gt;.runtime</c>, or <see langword="null"/>.</summary>
    public AMPRRuntimeSettings? Runtime { get; set; }

    /// <summary>Manifest file name.</summary>
    public string IndexName { get; set; } = DefaultIndexName;

    /// <summary>Volume name pattern.</summary>
    public string PackPattern { get; set; } = DefaultPackPattern;

    /// <summary>Action for files no rule matches.</summary>
    public string DefaultAction { get; set; } = "loose";

    /// <summary>Default block shift.</summary>
    public int DefaultBlockShift { get; set; } = 16;

    /// <summary>Filesystem I/O page size.</summary>
    public long IOPageSize { get; set; } = 64 * 1024;

    /// <summary>Volume payload alignment.</summary>
    public long PayloadAlignment { get; set; } = 64 * 1024;

    /// <summary>Chunk alignment inside a page.</summary>
    public long ChunkAlignment { get; set; } = 64;

    /// <summary>Compression worker threads (not part of the build id).</summary>
    public long Workers { get; set; } = Math.Max(1, Math.Min(8, Environment.ProcessorCount));

    /// <summary>Default LZ4 mode.</summary>
    public string CompressionMode { get; set; } = "hc";

    /// <summary>Default HC level.</summary>
    public long CompressionLevel { get; set; } = 12;

    /// <summary>Default fast-mode acceleration.</summary>
    public long Acceleration { get; set; } = 1;

    /// <summary>Default minimum saving in bytes.</summary>
    public long MinSavingsBytes { get; set; } = 64;

    /// <summary>Default minimum saving ratio.</summary>
    public double MinSavingsRatio { get; set; } = 0.01;

    /// <summary>Default minimum saving in bytes when the page count does not change.</summary>
    public long IONeutralMinSavingsBytes { get; set; } = 8 * 1024;

    /// <summary>Default minimum saving ratio when the page count does not change.</summary>
    public double IONeutralMinSavingsRatio { get; set; } = 0.125;

    /// <summary>Deduplicate identical blocks.</summary>
    public bool Deduplicate { get; set; } = true;

    /// <summary><c>lane</c> or <c>group</c>.</summary>
    public string DeduplicateScope { get; set; } = "lane";

    /// <summary>Also deduplicate inside streaming files.</summary>
    public bool DeduplicateStreaming { get; set; }

    /// <summary>Store the source mtime instead of the AMPRIDX3 one.</summary>
    public bool PreserveMTime { get; set; } = true;

    /// <summary>Check source sizes against AMPRIDX3.</summary>
    public bool ValidateIndexMetadata { get; set; } = true;

    /// <summary>Never auto-loose explicitly packed files.</summary>
    public bool SelfContained { get; set; }

    /// <summary>Globs that must end up packed.</summary>
    public IReadOnlyList<string> RequiredPacked { get; set; } = [];

    /// <summary>Leave large incompressible files loose.</summary>
    public bool AutoLooseLargeFiles { get; set; } = true;

    /// <summary>Also auto-loose files from hot rules.</summary>
    public bool AutoLooseHotFiles { get; set; }

    /// <summary>Smallest file considered for auto-loose.</summary>
    public long AutoLooseMinFileSize { get; set; } = 64L * 1024 * 1024;

    /// <summary>Blocks sampled per candidate.</summary>
    public long AutoLooseSampleBlocks { get; set; } = 32;

    /// <summary>Byte budget of the sample.</summary>
    public long AutoLooseSampleBytes { get; set; } = 16L * 1024 * 1024;

    /// <summary>Saving ratio below which a file goes loose.</summary>
    public double AutoLooseMinSavingsRatio { get; set; } = 0.05;

    /// <summary>RAW sample ratio above which a file goes loose.</summary>
    public double AutoLooseMaxRawRatio { get; set; } = 0.90;

    /// <summary>Groups in declaration order; <c>default</c> is added last when missing.</summary>
    public IReadOnlyDictionary<string, AMPRGroupConfig> Groups { get; set; } = new Dictionary<string, AMPRGroupConfig>();

    /// <summary>Rules in order (last match wins).</summary>
    public IReadOnlyList<AMPRRule> Rules { get; set; } = [];

    /// <summary>The rule for files no rule matches (Python <c>_default_rule</c>).</summary>
    /// <returns>Rule.</returns>
    public AMPRRule DefaultRule() => new(
        DefaultAction,
        ["**"],
        [],
        DefaultBlockShift,
        Mode: CompressionMode,
        Level: CompressionLevel,
        Acceleration: Acceleration,
        MinSavingsBytes: MinSavingsBytes,
        MinSavingsRatio: MinSavingsRatio,
        IONeutralMinSavingsBytes: IONeutralMinSavingsBytes,
        IONeutralMinSavingsRatio: IONeutralMinSavingsRatio);

    /// <summary>The last matching rule, else the default rule (Python <c>select_rule</c>).</summary>
    /// <param name="relative">Path relative to <c>/app0</c>.</param>
    /// <returns>Rule.</returns>
    public AMPRRule SelectRule(string relative)
    {
        AMPRRule selected = DefaultRule();
        foreach (AMPRRule rule in Rules)
        {
            if (rule.Matches(relative))
            {
                selected = rule;
            }
        }

        return selected;
    }

    /// <summary>
    /// Build-id input: <c>json.dumps(payload, sort_keys=True, separators=(",", ":"))</c> of the content-relevant
    /// settings, groups (sorted by name) and rules (Python <c>_canonical_config_bytes</c>).
    /// </summary>
    /// <returns>UTF-8 bytes.</returns>
    public byte[] CanonicalBytes()
    {
        Dictionary<string, object?> payload = new()
        {
            ["tool_version"] = ToolVersion,
            ["index_name"] = IndexName,
            ["pack_pattern"] = PackPattern,
            ["default_action"] = DefaultAction,
            ["default_block_shift"] = (long)DefaultBlockShift,
            ["io_page_size"] = IOPageSize,
            ["payload_alignment"] = PayloadAlignment,
            ["chunk_alignment"] = ChunkAlignment,
            ["compression_mode"] = CompressionMode,
            ["compression_level"] = CompressionLevel,
            ["acceleration"] = Acceleration,
            ["min_savings_bytes"] = MinSavingsBytes,
            ["min_savings_ratio"] = MinSavingsRatio,
            ["io_neutral_min_savings_bytes"] = IONeutralMinSavingsBytes,
            ["io_neutral_min_savings_ratio"] = IONeutralMinSavingsRatio,
            ["deduplicate"] = Deduplicate,
            ["deduplicate_scope"] = DeduplicateScope,
            ["deduplicate_streaming"] = DeduplicateStreaming,
            ["auto_loose_large_files"] = AutoLooseLargeFiles,
            ["auto_loose_hot_files"] = AutoLooseHotFiles,
            ["auto_loose_min_file_size"] = AutoLooseMinFileSize,
            ["auto_loose_sample_blocks"] = AutoLooseSampleBlocks,
            ["auto_loose_sample_bytes"] = AutoLooseSampleBytes,
            ["auto_loose_min_savings_ratio"] = AutoLooseMinSavingsRatio,
            ["auto_loose_max_raw_ratio"] = AutoLooseMaxRawRatio,
            ["groups"] = SortedGroupNames().Select(name => (object?)GroupPayload(Groups[name])).ToList(),
            ["rules"] = Rules.Select(rule => (object?)RulePayload(rule)).ToList(),
        };
        return Encoding.UTF8.GetBytes(PythonSortedJson.Compact(payload));
    }

    /// <summary>Group names sorted by code point (Python <c>sorted(config.groups)</c>); the index is the packing class.</summary>
    /// <returns>Names.</returns>
    public List<string> SortedGroupNames() => [.. Groups.Keys.OrderBy(name => name, PythonCodePointComparer.Instance)];

    /// <summary>
    /// Load a configuration, or the defaults when <paramref name="path"/> is <see langword="null"/> (Python
    /// <c>load_config</c>).
    /// </summary>
    /// <param name="path">TOML file.</param>
    /// <returns>Configuration.</returns>
    /// <exception cref="AMPRPackException">The configuration is invalid.</exception>
    /// <exception cref="ArgumentException">A value cannot be converted (Python <c>ValueError</c>).</exception>
    public static AMPRPackConfig Load(string? path)
    {
        TomlTable raw = [];
        string configDir = Directory.GetCurrentDirectory();
        if (path is not null)
        {
            string resolved = Path.GetFullPath(path);
            configDir = Path.GetDirectoryName(resolved)!;
            raw = AMPRToml.Load(resolved);
        }

        return FromTable(raw, configDir);
    }

    /// <summary>Built-in rule sets for <see cref="LoadPreset"/> (an extension; ampr_pack has none).</summary>
    public static IReadOnlyList<string> PresetNames { get; } = ["default"];

    /// <summary>
    /// The <c>default</c> preset: compress every file, but keep loose everything <c>remove-sources</c> protects
    /// (executables, modules, system and save folders, indexes) and Unity IL2CPP <c>global-metadata.dat</c>, which
    /// IL2CPP memory-maps (ampr_emu does not intercept mmap). <c>*</c> also matches <c>/</c>, so <c>*.prx</c> covers
    /// every depth.
    /// </summary>
    public const string DefaultPresetToml = """
        [pack]
        default_action = "compress"

        [[rule]]
        action = "loose"
        include = [
          "eboot.bin", "*/eboot.bin", "param.sfo", "*/param.sfo", "nptitle.dat", "*/nptitle.dat",
          "ampr_emu.index", "*/ampr_emu.index", "*.prx", "*.sprx", "*.elf", "*.self",
          "sce_sys/*", "sce_module/*", "fakelib/*", "fakelib2/*", "mods/*", "save/*", "system/*",
          "*/global-metadata.dat",
        ]
        """;

    /// <summary>Load a built-in preset.</summary>
    /// <param name="name">Preset name (see <see cref="PresetNames"/>).</param>
    /// <returns>Configuration.</returns>
    /// <exception cref="AMPRPackException">The preset does not exist.</exception>
    public static AMPRPackConfig LoadPreset(string name) => name switch
    {
        "default" => FromTable(AMPRToml.Parse(DefaultPresetToml, "preset default"), Directory.GetCurrentDirectory()),
        _ => throw new AMPRPackException($"unknown preset: {name}"),
    };

    /// <summary>
    /// Read pattern lines: stripped, without blank lines and <c>#</c> comments (Python <c>_load_pattern_file</c>).
    /// The file is decoded as UTF-8 without BOM detection, like Python <c>read_text(encoding="utf-8")</c>.
    /// </summary>
    /// <param name="path">File, or <see langword="null"/> for none.</param>
    /// <returns>Patterns.</returns>
    public static List<string> LoadPatternFile(string? path)
    {
        if (path is null)
        {
            return [];
        }

        string text;
        using (StreamReader reader = new(path, new UTF8Encoding(false, throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: false))
        {
            text = reader.ReadToEnd();
        }

        List<string> patterns = [];
        foreach (string line in PythonText.SplitLines(text))
        {
            string stripped = PythonText.Strip(line);
            if (stripped.Length > 0 && !stripped.StartsWith('#'))
            {
                patterns.Add(stripped);
            }
        }

        return patterns;
    }

    /// <summary>Build a configuration from a parsed TOML document.</summary>
    /// <param name="raw">Root table.</param>
    /// <param name="configDir">Directory that <c>include_from</c>/<c>exclude_from</c> are relative to.</param>
    /// <returns>Configuration.</returns>
    internal static AMPRPackConfig FromTable(TomlTable raw, string configDir)
    {
        object? packValue = raw.TryGetValue("pack", out object? p) ? p : new TomlTable();
        if (packValue is not TomlTable pack)
        {
            throw new AMPRPackException("[pack] must be a table");
        }

        AMPRRuntimeSettings? runtime = raw.TryGetValue("runtime", out object? runtimeValue) ? LoadRuntime(runtimeValue) : null;

        // Python evaluates BuildConfig(...) arguments in this order; the first failing conversion wins.
        int defaultBlock = PythonValue.BlockShift(Get(pack, "default_block_size", "64KiB"));
        long ioPageSize = PythonValue.Size(Get(pack, "io_page_size", "64KiB"));
        AMPRPackConfig config = new()
        {
            Runtime = runtime,
            IndexName = PythonValue.Str(Get(pack, "index_name", DefaultIndexName)),
            PackPattern = PythonValue.Str(Get(pack, "pack_pattern", DefaultPackPattern)),
            DefaultAction = Action(Get(pack, "default_action", "loose")),
            DefaultBlockShift = defaultBlock,
            IOPageSize = ioPageSize,
            PayloadAlignment = PythonValue.Size(Get(pack, "payload_alignment", ioPageSize)),
            ChunkAlignment = PythonValue.Size(Get(pack, "chunk_alignment", "64B")),
            Workers = PythonValue.Int(Get(pack, "workers", (long)Math.Max(1, Math.Min(8, Environment.ProcessorCount)))),
            CompressionMode = PythonValue.Str(Get(pack, "compression_mode", "hc")).ToLowerInvariant(),
            CompressionLevel = PythonValue.Int(Get(pack, "compression_level", 12L)),
            Acceleration = PythonValue.Int(Get(pack, "acceleration", 1L)),
            MinSavingsBytes = PythonValue.Size(Get(pack, "min_savings_bytes", 64L)),
            MinSavingsRatio = PythonValue.Float(Get(pack, "min_savings_ratio", 0.01)),
            IONeutralMinSavingsBytes = PythonValue.Size(Get(pack, "io_neutral_min_savings_bytes", "8KiB")),
            IONeutralMinSavingsRatio = PythonValue.Float(Get(pack, "io_neutral_min_savings_ratio", 0.125)),
            Deduplicate = PythonValue.Bool(Get(pack, "deduplicate", true)),
            DeduplicateScope = PythonValue.Str(Get(pack, "deduplicate_scope", "lane")).ToLowerInvariant(),
            DeduplicateStreaming = PythonValue.Bool(Get(pack, "deduplicate_streaming", false)),
            PreserveMTime = PythonValue.Bool(Get(pack, "preserve_mtime", true)),
            ValidateIndexMetadata = PythonValue.Bool(Get(pack, "validate_index_metadata", true)),
            SelfContained = PythonValue.Bool(Get(pack, "self_contained", false)),
            RequiredPacked = ListOfStrings(Get(pack, "required_packed", new TomlArray()), "pack.required_packed"),
            AutoLooseLargeFiles = PythonValue.Bool(Get(pack, "auto_loose_large_files", true)),
            AutoLooseHotFiles = PythonValue.Bool(Get(pack, "auto_loose_hot_files", false)),
            AutoLooseMinFileSize = PythonValue.Size(Get(pack, "auto_loose_min_file_size", "64MiB")),
            AutoLooseSampleBlocks = PythonValue.Int(Get(pack, "auto_loose_sample_blocks", 32L)),
            AutoLooseSampleBytes = PythonValue.Size(Get(pack, "auto_loose_sample_bytes", "16MiB")),
            AutoLooseMinSavingsRatio = PythonValue.Float(Get(pack, "auto_loose_min_savings_ratio", 0.05)),
            AutoLooseMaxRawRatio = PythonValue.Float(Get(pack, "auto_loose_max_raw_ratio", 0.90)),
        };
        config.ValidatePackSection();
        config.Groups = LoadGroups(raw, config);
        config.Rules = LoadRules(raw, config, configDir);
        return config;
    }

    private void ValidatePackSection()
    {
        if (Workers is < 1 or > 256)
        {
            throw new AMPRPackException("workers must be between 1 and 256");
        }

        if (IOPageSize < 1L << AMPRPackFormat.MinIOPageShift || (IOPageSize & (IOPageSize - 1)) != 0 || IOPageSize > 1L << AMPRPackFormat.MaxIOPageShift)
        {
            throw new AMPRPackException("io_page_size must be a power of two between 4 KiB and 1 MiB");
        }

        if (PayloadAlignment < AMPRPackFormat.DataHeaderSize
            || (PayloadAlignment & (PayloadAlignment - 1)) != 0
            || PayloadAlignment > 1L << AMPRPackFormat.MaxBlockShift)
        {
            throw new AMPRPackException(
                "payload_alignment must be a power of two between 64 B and 1 MiB; the writer automatically raises it to the selected io_page_size");
        }

        if (ChunkAlignment < AMPRPackFormat.PhysicalChunkAlignment
            || (ChunkAlignment & (ChunkAlignment - 1)) != 0
            || ChunkAlignment > IOPageSize
            || IOPageSize % ChunkAlignment != 0)
        {
            throw new AMPRPackException("chunk_alignment must be a power of two between 64 B and io_page_size");
        }

        if (CompressionMode is not ("fast" or "hc"))
        {
            throw new AMPRPackException("compression_mode must be fast or hc");
        }

        if (DeduplicateScope is not ("lane" or "group"))
        {
            throw new AMPRPackException("deduplicate_scope must be lane or group");
        }

        if (CompressionLevel is < 1 or > 12)
        {
            throw new AMPRPackException("compression_level must be between 1 and 12");
        }

        if (Acceleration < 1)
        {
            throw new AMPRPackException("acceleration must be at least 1");
        }

        CheckRatio(MinSavingsRatio, "min_savings_ratio must be in [0, 1)");
        CheckRatio(IONeutralMinSavingsRatio, "io_neutral_min_savings_ratio must be in [0, 1)");
        if (AutoLooseSampleBlocks is < 1 or > 4096)
        {
            throw new AMPRPackException("auto_loose_sample_blocks must be between 1 and 4096");
        }

        if (AutoLooseSampleBytes < 1)
        {
            throw new AMPRPackException("auto_loose_sample_bytes must be at least 1 byte");
        }

        CheckRatio(AutoLooseMinSavingsRatio, "auto_loose_min_savings_ratio must be in [0, 1)");
        if (!(AutoLooseMaxRawRatio >= 0.0 && AutoLooseMaxRawRatio <= 1.0))
        {
            throw new AMPRPackException("auto_loose_max_raw_ratio must be in [0, 1]");
        }

        try
        {
            AMPRAssetPath.SafeOutputPath(".", IndexName.Replace('\\', '/'));
        }
        catch (ArgumentException exc)
        {
            throw new AMPRPackException($"unsafe index_name: {PythonText.Repr(IndexName)}", exc);
        }

        AMPRPackPattern.Parse(PackPattern);
    }

    private static Dictionary<string, AMPRGroupConfig> LoadGroups(TomlTable raw, AMPRPackConfig config)
    {
        Dictionary<string, AMPRGroupConfig> groups = new(StringComparer.Ordinal);
        object? groupsRaw = raw.TryGetValue("groups", out object? g) ? g : null;
        if (PythonValue.Bool(groupsRaw) && groupsRaw is not TomlTable)
        {
            throw new AMPRPackException("[groups] must be a table");
        }

        if (groupsRaw is TomlTable groupTable)
        {
            foreach (KeyValuePair<string, object> entry in groupTable)
            {
                string name = entry.Key;
                if (entry.Value is not TomlTable value)
                {
                    throw new AMPRPackException($"[groups.{name}] must be a table");
                }

                AMPRGroupConfig group = new(
                    name,
                    PythonValue.Int(Get(value, "pack_count", 1L)),
                    Assignment(Get(value, "assignment", "balanced")),
                    PythonValue.Size(Get(value, "max_pack_size", 0L)),
                    PythonValue.Bool(Get(value, "stripe_large_files", false)),
                    PythonValue.Size(Get(value, "stripe_threshold", "256MiB")),
                    PythonValue.Int(Get(value, "stripe_group_blocks", 8L)),
                    PythonValue.Size(Get(value, "io_page_size", 0L)));
                if (group.PackCount is < 1 or > 0xFFFF)
                {
                    throw new AMPRPackException($"groups.{name}.pack_count must be between 1 and 65535");
                }

                long groupPage = group.IOPageSize != 0 ? group.IOPageSize : config.IOPageSize;
                if (groupPage < 1L << AMPRPackFormat.MinIOPageShift
                    || groupPage > 1L << AMPRPackFormat.MaxIOPageShift
                    || (groupPage & (groupPage - 1)) != 0
                    || groupPage % config.ChunkAlignment != 0)
                {
                    throw new AMPRPackException(
                        $"groups.{name}.io_page_size must be a power of two between 4 KiB and 1 MiB and a multiple of chunk_alignment");
                }

                long minimumPayloadOffset = (long)AMPRPackFormat.AlignUp(AMPRPackFormat.DataHeaderSize, (ulong)Math.Max(config.PayloadAlignment, groupPage));
                long minimumVolumeSize = minimumPayloadOffset + groupPage;
                if (group.MaxPackSize != 0 && group.MaxPackSize < minimumVolumeSize)
                {
                    throw new AMPRPackException(
                        $"groups.{name}.max_pack_size must be at least one complete I/O page beyond the payload offset ({minimumVolumeSize} bytes)");
                }

                if (group.StripeGroupBlocks < 1)
                {
                    throw new AMPRPackException($"groups.{name}.stripe_group_blocks must be at least 1");
                }

                groups[name] = group;
            }
        }

        groups.TryAdd("default", new AMPRGroupConfig("default"));
        return groups.Count > 256 ? throw new AMPRPackException("the binary format supports at most 256 packing groups") : groups;
    }

    private static List<AMPRRule> LoadRules(TomlTable raw, AMPRPackConfig config, string configDir)
    {
        object? rulesRaw = raw.TryGetValue("rule", out object? r) ? r : new TomlArray();
        List<object?> items = rulesRaw switch
        {
            TomlTableArray tables => [.. tables],
            TomlArray array => [.. array],
            _ => throw new AMPRPackException("[[rule]] entries must form an array"),
        };

        List<AMPRRule> rules = [];
        for (int index = 0; index < items.Count; index++)
        {
            if (items[index] is not TomlTable value)
            {
                throw new AMPRPackException($"rule {index} must be a table");
            }

            string group = PythonValue.Str(Get(value, "group", "default"));
            if (!config.Groups.ContainsKey(group))
            {
                throw new AMPRPackException($"rule {index} references unknown group {PythonText.Repr(group)}");
            }

            int shift = PythonValue.BlockShift(Get(value, "block_size", 1L << config.DefaultBlockShift));
            List<string> includeFrom = LoadRulePatternFiles(Get(value, "include_from", new TomlArray()), "include_from", configDir);
            List<string> excludeFrom = LoadRulePatternFiles(Get(value, "exclude_from", new TomlArray()), "exclude_from", configDir);
            object includeDefault = includeFrom.Count > 0 ? new TomlArray() : new TomlArray { "**" };
            List<string> include = [.. ListOfStrings(Get(value, "include", includeDefault), "include"), .. includeFrom];
            List<string> exclude = [.. ListOfStrings(Get(value, "exclude", new TomlArray()), "exclude"), .. excludeFrom];
            AMPRRule rule = new(
                Action(Get(value, "action", "compress")),
                include,
                exclude,
                shift,
                group,
                PythonValue.Str(Get(value, "compression_mode", config.CompressionMode)).ToLowerInvariant(),
                PythonValue.Int(Get(value, "compression_level", config.CompressionLevel)),
                PythonValue.Int(Get(value, "acceleration", config.Acceleration)),
                PythonValue.Size(Get(value, "min_savings_bytes", config.MinSavingsBytes)),
                PythonValue.Float(Get(value, "min_savings_ratio", config.MinSavingsRatio)),
                PythonValue.Size(Get(value, "io_neutral_min_savings_bytes", config.IONeutralMinSavingsBytes)),
                PythonValue.Float(Get(value, "io_neutral_min_savings_ratio", config.IONeutralMinSavingsRatio)),
                Layout(Get(value, "layout", "auto")),
                PythonValue.Bool(Get(value, "streaming", false)),
                PythonValue.Bool(Get(value, "hot", false)),
                PythonValue.Bool(Get(value, "force_pack", false)));
            if (rule.Include.Count == 0)
            {
                throw new AMPRPackException($"rule {index} include list cannot be empty");
            }

            if (rule.Mode is not ("fast" or "hc"))
            {
                throw new AMPRPackException($"rule {index} compression_mode must be fast or hc");
            }

            if (rule.Level is < 1 or > 12)
            {
                throw new AMPRPackException($"rule {index} compression_level must be between 1 and 12");
            }

            if (rule.Acceleration < 1)
            {
                throw new AMPRPackException($"rule {index} acceleration must be at least 1");
            }

            CheckRatio(rule.MinSavingsRatio, $"rule {index} min_savings_ratio must be in [0, 1)");
            CheckRatio(rule.IONeutralMinSavingsRatio, $"rule {index} io_neutral_min_savings_ratio must be in [0, 1)");
            rules.Add(rule);
        }

        return rules;
    }

    private static List<string> LoadRulePatternFiles(object? value, string field, string configDir)
    {
        List<string> patterns = [];
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configDir));
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (string name in ListOfStrings(value, field))
        {
            if (Path.IsPathFullyQualified(name))
            {
                throw new AMPRPackException($"{field} entries must be relative to the TOML file");
            }

            string candidate = Path.GetFullPath(Path.Combine(root, name));
            string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            if (!candidate.Equals(root, comparison) && !candidate.StartsWith(prefix, comparison))
            {
                throw new AMPRPackException($"{field} entry escapes the TOML directory: {name}");
            }

            try
            {
                patterns.AddRange(LoadPatternFile(candidate));
            }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
            {
                throw new AMPRPackException($"cannot read {field} file {name}: {exc.Message}", exc);
            }
        }

        return patterns;
    }

    private static AMPRRuntimeSettings LoadRuntime(object? value)
    {
        if (value is not TomlTable runtime
            || runtime.Count != RuntimeKeys.Length
            || !RuntimeKeys.All(runtime.ContainsKey))
        {
            throw new AMPRPackException(
                "[runtime] requires exactly decoded_cache_bytes, physical_cache_bytes, workers, latency_reserve_workers");
        }

        // RuntimeSettings(parse_size(...), parse_size(...), workers, reserve).validate(): only real ints pass.
        object? decodedRaw = runtime["decoded_cache_bytes"];
        object? physicalRaw = runtime["physical_cache_bytes"];
        long decoded = PythonValue.Size(decodedRaw);
        long physical = PythonValue.Size(physicalRaw);
        if (decodedRaw is bool || physicalRaw is bool || runtime["workers"] is not long workers || runtime["latency_reserve_workers"] is not long reserve)
        {
            throw new ArgumentException("runtime settings must be integers");
        }

        AMPRRuntimeSettings settings = new(decoded, physical, workers, reserve);
        settings.Validate();
        return settings;
    }

    private static object? Get(TomlTable table, string key, object? fallback) =>
        table.TryGetValue(key, out object? value) ? value : fallback;

    private static List<string> ListOfStrings(object? value, string key) => value switch
    {
        null => [],
        string s => [s],
        TomlArray array when array.All(item => item is string) => [.. array.Cast<string>()],
        _ => throw new AMPRPackException($"{key} must be a string or list of strings"),
    };

    private static string Action(object? value) => Choice(value, ["compress", "store", "loose"], "action must be compress, store or loose");

    private static string Layout(object? value) => Choice(value, ["auto", "random", "mixed", "streaming"], "layout must be auto, random, mixed or streaming");

    private static string Assignment(object? value) => Choice(value, ["balanced", "hash", "round_robin"], "assignment must be balanced, hash or round_robin");

    private static string Choice(object? value, string[] allowed, string message)
    {
        string result = PythonValue.Str(value).ToLowerInvariant();
        return allowed.Contains(result, StringComparer.Ordinal) ? result : throw new AMPRPackException($"{message}; got {PythonText.Repr(result)}");
    }

    private static void CheckRatio(double value, string message)
    {
        if (!(value >= 0.0 && value < 1.0))
        {
            throw new AMPRPackException(message);
        }
    }

    private static Dictionary<string, object?> GroupPayload(AMPRGroupConfig group) => new()
    {
        ["name"] = group.Name,
        ["pack_count"] = group.PackCount,
        ["assignment"] = group.Assignment,
        ["max_pack_size"] = group.MaxPackSize,
        ["stripe_large_files"] = group.StripeLargeFiles,
        ["stripe_threshold"] = group.StripeThreshold,
        ["stripe_group_blocks"] = group.StripeGroupBlocks,
        ["io_page_size"] = group.IOPageSize,
    };

    private static Dictionary<string, object?> RulePayload(AMPRRule rule) => new()
    {
        ["action"] = rule.Action,
        ["include"] = rule.Include,
        ["exclude"] = rule.Exclude,
        ["block_shift"] = (long)rule.BlockShift,
        ["group"] = rule.Group,
        ["mode"] = rule.Mode,
        ["level"] = rule.Level,
        ["acceleration"] = rule.Acceleration,
        ["min_savings_bytes"] = rule.MinSavingsBytes,
        ["min_savings_ratio"] = rule.MinSavingsRatio,
        ["io_neutral_min_savings_bytes"] = rule.IONeutralMinSavingsBytes,
        ["io_neutral_min_savings_ratio"] = rule.IONeutralMinSavingsRatio,
        ["layout"] = rule.Layout,
        ["streaming"] = rule.Streaming,
        ["hot"] = rule.Hot,
        ["force_pack"] = rule.ForcePack,
    };
}
