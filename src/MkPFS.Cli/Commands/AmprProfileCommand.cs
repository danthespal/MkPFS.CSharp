using System.CommandLine;
using System.Globalization;
using MkPFS.Build.AMPRPack;
using MkPFS.Cli.Output;
using MkPFS.Core.AMPR;

namespace MkPFS.Cli.Commands;

/// <summary>
/// <c>ampr profile generate</c>: port of ampr_emu <c>tools/ampr_pack_profile.py generate</c>. Reads APR traces
/// (<c>ampr_commands.bin</c> plus the run's <c>ampr_emu.index</c>) recorded by the debug emulator build and writes a
/// TOML pack profile, optionally the Markdown report and the runtime header. <c>--metrics</c> (JSON) and
/// <c>batch</c> are not ported.
/// </summary>
internal static class AmprProfileCommand
{
    public static Command Create(CliContext ctx)
    {
        Command command = new("profile", "generate pack rules from APR traces of the debug emulator build (ampr_pack_profile.py)");
        command.Subcommands.Add(Generate(ctx));
        return command;
    }

    private static Command Generate(CliContext ctx)
    {
        Argument<string?> input = new("input") { Description = "directory containing ampr_commands.bin and ampr_emu.index", Arity = ArgumentArity.ZeroOrOne };
        Option<string[]> trace = new("--trace")
        {
            Description = "COMMANDS INDEX; repeat to merge several gameplay traces",
            AllowMultipleArgumentsPerToken = true,
            Arity = ArgumentArity.OneOrMore,
            DefaultValueFactory = _ => [],
        };
        Option<string?> name = new("--name");
        Option<string> output = new("--output") { Required = true };
        Option<string?> report = new("--report");
        Option<string?> runtimeHeader = new("--runtime-header") { Description = "optional force-include header with runtime cache/pool/coalescing defines" };
        Option<string> ioPageSize = new("--io-page-size") { DefaultValueFactory = _ => "64KiB" };
        Option<string> indexBudget = new("--pack-index-budget") { DefaultValueFactory = _ => "96MiB" };
        Option<string?> cacheCandidates = new("--cache-candidates") { Description = "comma-separated capacities, e.g. 64MiB,128MiB,256MiB,512MiB", HelpName = "LIST" };
        Option<int> lanes = new("--lanes") { Description = "0 selects 1-4 lanes automatically", DefaultValueFactory = _ => 0 };
        Option<int> workers = new("--workers") { DefaultValueFactory = _ => 8 };
        Option<int> runtimeWorkers = new("--runtime-workers") { Description = "0 selects 2-8 runtime decode/I/O workers from the trace", DefaultValueFactory = _ => 0 };
        Option<string> strategy = new("--strategy") { Description = "trade read amplification against resident index size", DefaultValueFactory = _ => "balanced" };
        strategy.AcceptOnlyFromAmong("conservative", "balanced", "aggressive");
        Option<string> patternMode = new("--pattern-mode") { Description = "exact is safest; hybrid generalises only high-coverage directories", DefaultValueFactory = _ => "exact" };
        patternMode.AcceptOnlyFromAmong("exact", "hybrid", "directory");
        Option<int> minReads = new("--min-reads") { DefaultValueFactory = _ => 1 };
        Option<string> minRequestedBytes = new("--min-requested-bytes") { DefaultValueFactory = _ => "1B" };
        Option<int> maxRuleFiles = new("--max-rule-files") { DefaultValueFactory = _ => 256 };
        Option<int> externalizePaths = new("--externalize-paths")
        {
            Description = "write larger exact include lists to portable include_from sidecars (0 keeps paths inline)",
            DefaultValueFactory = _ => 128,
        };
        Option<double> generalizeCoverage = new("--generalize-coverage") { DefaultValueFactory = _ => 0.80 };
        Option<int> generalizeMinFiles = new("--generalize-min-files") { DefaultValueFactory = _ => 4 };
        Option<bool> cacheSim = new("--cache-sim") { Description = "simulate decoded-cache reuse (default for generate)" };
        Option<bool> noCacheSim = new("--no-cache-sim") { Description = "skip the expensive decoded-cache simulation" };
        Option<long> cacheMaxTouches = new("--cache-max-touches")
        {
            Description = "simulate all decoded-cache block touches up to this limit; larger traces use deterministic contiguous windows (0 = exact)",
            DefaultValueFactory = _ => AMPRProfiler.DefaultCacheMaxTouches,
        };
        Option<string?> contentRoot = new("--content-root") { Description = "optional extracted /app0 root for LZ4 sampling" };
        Option<string> sampleBudget = new("--sample-budget") { DefaultValueFactory = _ => "0B" };
        Option<int> sampleBlocksPerFile = new("--sample-blocks-per-file") { DefaultValueFactory = _ => 4 };
        Option<string> sampleMode = new("--sample-mode") { DefaultValueFactory = _ => "hc" };
        sampleMode.AcceptOnlyFromAmong("fast", "hc");
        Option<int> sampleLevel = new("--sample-level") { DefaultValueFactory = _ => 12 };
        Option<int> sampleAcceleration = new("--sample-acceleration") { DefaultValueFactory = _ => 1 };
        Option<bool> overwrite = new("--overwrite");
        Command command = new("generate", "generate one profile")
        {
            input, trace, name, output, report, runtimeHeader, ioPageSize, indexBudget, cacheCandidates, lanes, workers, runtimeWorkers, strategy,
            patternMode, minReads, minRequestedBytes, maxRuleFiles, externalizePaths, generalizeCoverage, generalizeMinFiles, cacheSim, noCacheSim,
            cacheMaxTouches, contentRoot, sampleBudget, sampleBlocksPerFile, sampleMode, sampleLevel, sampleAcceleration, overwrite,
        };
        command.SetAction(parse => AmprCommand.Run(ctx, () =>
        {
            if (parse.GetValue(cacheSim) && parse.GetValue(noCacheSim))
            {
                throw new AMPRPackException("--cache-sim and --no-cache-sim cannot be used together");
            }

            List<AMPRTraceSpec> traces = [];
            if (parse.GetValue(input) is { } inputPath)
            {
                List<AMPRTraceSpec> discovered = AMPRProfiler.DiscoverTracePairs(AmprCommand.Resolve(ctx, inputPath));
                if (discovered.Count != 1)
                {
                    throw new AMPRPackException($"generate input must contain exactly one trace pair; found {discovered.Count}");
                }

                traces.AddRange(discovered);
            }

            string[] pairs = parse.GetValue(trace)!;
            if (pairs.Length % 2 != 0)
            {
                throw new AMPRPackException("--trace takes two paths: COMMANDS INDEX");
            }

            for (int i = 0; i < pairs.Length; i += 2)
            {
                traces.Add(new AMPRTraceSpec($"trace-{(i / 2) + 1}", AmprCommand.Resolve(ctx, pairs[i]), AmprCommand.Resolve(ctx, pairs[i + 1])));
            }

            if (traces.Count == 0)
            {
                throw new AMPRPackException("provide an input directory or at least one --trace pair");
            }

            foreach (AMPRTraceSpec spec in traces)
            {
                if (!File.Exists(spec.Commands) || !File.Exists(spec.Index))
                {
                    throw new AMPRPackException($"missing trace input: {spec.Commands} / {spec.Index}");
                }
            }

            AMPRProfileOptions options = new()
            {
                Name = parse.GetValue(name) ?? traces[0].Name,
                IOPageSize = AMPRSize.Parse(parse.GetValue(ioPageSize)!),
                IndexBudget = AMPRSize.Parse(parse.GetValue(indexBudget)!),
                CacheCandidates = parse.GetValue(cacheCandidates) is { } list ? CacheCandidates(list) : AMPRProfiler.DefaultCacheCandidates,
                Lanes = parse.GetValue(lanes),
                Workers = parse.GetValue(workers),
                RuntimeWorkers = parse.GetValue(runtimeWorkers),
                Strategy = parse.GetValue(strategy)!,
                PatternMode = parse.GetValue(patternMode)!,
                MinReads = parse.GetValue(minReads),
                MinRequestedBytes = AMPRSize.Parse(parse.GetValue(minRequestedBytes)!),
                MaxRuleFiles = parse.GetValue(maxRuleFiles),
                ExternalizePaths = parse.GetValue(externalizePaths),
                GeneralizeCoverage = parse.GetValue(generalizeCoverage),
                GeneralizeMinFiles = parse.GetValue(generalizeMinFiles),
                CacheSimulation = !parse.GetValue(noCacheSim),
                CacheMaxTouches = parse.GetValue(cacheMaxTouches),
                ContentRoot = parse.GetValue(contentRoot) is { } content ? AmprCommand.Resolve(ctx, content) : null,
                SampleBudget = AMPRSize.Parse(parse.GetValue(sampleBudget)!),
                SampleBlocksPerFile = parse.GetValue(sampleBlocksPerFile),
                SampleMode = parse.GetValue(sampleMode)!,
                SampleLevel = parse.GetValue(sampleLevel),
                SampleAcceleration = parse.GetValue(sampleAcceleration),
            };
            options.Validate();
            AMPRProfileResult result = AMPRProfiler.Build(traces, options);
            string outputText = parse.GetValue(output)!;
            string outputPath = AmprCommand.Resolve(ctx, outputText);
            Dictionary<string, string> sidecars = [];
            string toml = AMPRProfiler.RenderToml(result, options, sidecars, Path.GetFileNameWithoutExtension(outputPath));
            bool replace = parse.GetValue(overwrite);
            WriteBundle(outputPath, toml, sidecars, replace);
            if (parse.GetValue(report) is { } reportPath)
            {
                WriteText(AmprCommand.Resolve(ctx, reportPath), AMPRProfiler.RenderReport(result), replace);
            }

            if (parse.GetValue(runtimeHeader) is { } headerPath)
            {
                WriteText(AmprCommand.Resolve(ctx, headerPath), AMPRProfiler.RenderRuntimeHeader(result), replace);
            }

            ctx.Out.WriteLine(
                $"generated {outputText}: files={result.ObservedFiles} index={AMPRProfiler.HumanSize(result.ProjectedIndexBytes)} " +
                $"cache={AMPRProfiler.HumanSize(result.RecommendedCacheBytes)} pool={AMPRProfiler.HumanSize(result.RecommendedPoolBytes)}");
        }));
        return command;
    }

    // Python _parse_cache_candidates: sizes, sorted, without duplicates.
    private static List<long> CacheCandidates(string value)
    {
        SortedSet<long> result = [];
        foreach (string item in value.Split(','))
        {
            string text = item.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            long parsed = AMPRSize.Parse(text);
            if (parsed <= 0)
            {
                throw new AMPRPackException("cache capacities must be positive");
            }

            result.Add(parsed);
        }

        return result.Count > 0 ? [.. result] : throw new AMPRPackException("at least one cache capacity is required");
    }

    // Python _write_profile_bundle: refuse to replace anything unless --overwrite, sidecars first, the TOML last.
    private static void WriteBundle(string tomlPath, string toml, Dictionary<string, string> sidecars, bool overwrite)
    {
        string directory = Path.GetDirectoryName(tomlPath)!;
        List<string> outputs = [tomlPath, .. sidecars.Keys.Select(name => Path.Combine(directory, name))];
        if (!overwrite && outputs.FirstOrDefault(Path.Exists) is { } existing)
        {
            throw new AMPRPackException($"refusing to overwrite existing file: {existing}");
        }

        foreach ((string name, string text) in sidecars)
        {
            WriteText(Path.Combine(directory, name), text, overwrite: true);
        }

        WriteText(tomlPath, toml, overwrite: true);
    }

    private static void WriteText(string path, string text, bool overwrite)
    {
        if (Path.Exists(path) && !overwrite)
        {
            throw new AMPRPackException($"refusing to overwrite existing file: {path}");
        }

        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, $".{Path.GetFileName(path)}.tmp-{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}");
        File.WriteAllText(temp, text, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
    }
}
