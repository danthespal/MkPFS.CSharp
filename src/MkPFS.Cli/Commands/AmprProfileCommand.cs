using System.CommandLine;
using System.Globalization;
using System.Runtime.ExceptionServices;
using MkPFS.Build.AMPRPack;
using MkPFS.Cli.Output;
using MkPFS.Core.AMPR;
using MkPFS.Core.Util;

namespace MkPFS.Cli.Commands;

/// <summary>
/// <c>ampr profile</c>: port of ampr_emu <c>tools/ampr_pack_profile.py</c>. <c>generate</c> reads APR traces
/// (<c>ampr_commands.bin</c> plus the run's <c>ampr_emu.index</c>) recorded by the debug emulator build and writes a
/// TOML pack profile, optionally the Markdown report, the metrics JSON and the runtime header; <c>batch</c> writes
/// one profile per trace run found in a folder or ZIP support bundle.
/// </summary>
internal static class AmprProfileCommand
{
    /// <summary>Python <c>DEFAULT_BATCH_JOBS</c>.</summary>
    private static readonly int DefaultBatchJobs = Math.Min(4, Math.Max(1, Environment.ProcessorCount / 2));

    public static Command Create(CliContext ctx)
    {
        Command command = new("profile", "generate pack rules from APR traces of the debug emulator build (ampr_pack_profile.py)");
        command.Subcommands.Add(Generate(ctx));
        command.Subcommands.Add(Batch(ctx));
        return command;
    }

    // Python _add_common_options; batch defaults to hybrid patterns without the cache simulation.
    private sealed class CommonOptions
    {
        private readonly Option<string> _ioPageSize = new("--io-page-size") { DefaultValueFactory = _ => "64KiB" };
        private readonly Option<string> _indexBudget = new("--pack-index-budget") { DefaultValueFactory = _ => "96MiB" };
        private readonly Option<string?> _cacheCandidates = new("--cache-candidates") { Description = "comma-separated capacities, e.g. 64MiB,128MiB,256MiB,512MiB", HelpName = "LIST" };
        private readonly Option<int> _lanes = new("--lanes") { Description = "0 selects 1-4 lanes automatically", DefaultValueFactory = _ => 0 };
        private readonly Option<int> _workers = new("--workers") { DefaultValueFactory = _ => 8 };
        private readonly Option<int> _runtimeWorkers = new("--runtime-workers") { Description = "0 selects 2-8 runtime decode/I/O workers from the trace", DefaultValueFactory = _ => 0 };
        private readonly Option<string> _strategy = new("--strategy") { Description = "trade read amplification against resident index size", DefaultValueFactory = _ => "balanced" };
        private readonly Option<string> _patternMode;
        private readonly Option<int> _minReads = new("--min-reads") { DefaultValueFactory = _ => 1 };
        private readonly Option<string> _minRequestedBytes = new("--min-requested-bytes") { DefaultValueFactory = _ => "1B" };
        private readonly Option<int> _maxRuleFiles = new("--max-rule-files") { DefaultValueFactory = _ => 256 };
        private readonly Option<int> _externalizePaths = new("--externalize-paths")
        {
            Description = "write larger exact include lists to portable include_from sidecars (0 keeps paths inline)",
            DefaultValueFactory = _ => 128,
        };
        private readonly Option<double> _generalizeCoverage = new("--generalize-coverage") { DefaultValueFactory = _ => 0.80 };
        private readonly Option<int> _generalizeMinFiles = new("--generalize-min-files") { DefaultValueFactory = _ => 4 };
        private readonly Option<bool> _cacheSim = new("--cache-sim") { Description = "simulate decoded-cache reuse (default for generate)" };
        private readonly Option<bool> _noCacheSim = new("--no-cache-sim") { Description = "skip the expensive decoded-cache simulation" };
        private readonly Option<long> _cacheMaxTouches = new("--cache-max-touches")
        {
            Description = "simulate all decoded-cache block touches up to this limit; larger traces use deterministic contiguous windows (0 = exact)",
            DefaultValueFactory = _ => AMPRProfiler.DefaultCacheMaxTouches,
        };
        private readonly Option<string?> _contentRoot = new("--content-root") { Description = "optional extracted /app0 root for LZ4 sampling" };
        private readonly Option<string> _sampleBudget = new("--sample-budget") { DefaultValueFactory = _ => "0B" };
        private readonly Option<int> _sampleBlocksPerFile = new("--sample-blocks-per-file") { DefaultValueFactory = _ => 4 };
        private readonly Option<string> _sampleMode = new("--sample-mode") { DefaultValueFactory = _ => "hc" };
        private readonly Option<int> _sampleLevel = new("--sample-level") { DefaultValueFactory = _ => 12 };
        private readonly Option<int> _sampleAcceleration = new("--sample-acceleration") { DefaultValueFactory = _ => 1 };
        private readonly bool _cacheSimulationDefault;

        public CommonOptions(string patternModeDefault, bool cacheSimulationDefault)
        {
            _patternMode = new("--pattern-mode") { Description = "exact is safest; hybrid generalises only high-coverage directories", DefaultValueFactory = _ => patternModeDefault };
            _patternMode.AcceptOnlyFromAmong("exact", "hybrid", "directory");
            _strategy.AcceptOnlyFromAmong("conservative", "balanced", "aggressive");
            _sampleMode.AcceptOnlyFromAmong("fast", "hc");
            _cacheSimulationDefault = cacheSimulationDefault;
        }

        public Option<bool> FullMetrics { get; } = new("--full-metrics") { Description = "include all seven block-size candidates for every file in JSON" };

        public Option<bool> Overwrite { get; } = new("--overwrite");

        public void AddTo(Command command)
        {
            foreach (Option option in (Option[])[
                _ioPageSize, _indexBudget, _cacheCandidates, _lanes, _workers, _runtimeWorkers, _strategy, _patternMode, _minReads, _minRequestedBytes,
                _maxRuleFiles, _externalizePaths, _generalizeCoverage, _generalizeMinFiles, _cacheSim, _noCacheSim, _cacheMaxTouches, _contentRoot,
                _sampleBudget, _sampleBlocksPerFile, _sampleMode, _sampleLevel, _sampleAcceleration, FullMetrics, Overwrite])
            {
                command.Options.Add(option);
            }
        }

        // Python _profile_options_from_args.
        public AMPRProfileOptions Read(CliContext ctx, ParseResult parse, string name)
        {
            if (parse.GetValue(_cacheSim) && parse.GetValue(_noCacheSim))
            {
                throw new AMPRPackException("--cache-sim and --no-cache-sim cannot be used together");
            }

            return new AMPRProfileOptions
            {
                Name = name,
                IOPageSize = AMPRSize.Parse(parse.GetValue(_ioPageSize)!),
                IndexBudget = AMPRSize.Parse(parse.GetValue(_indexBudget)!),
                CacheCandidates = parse.GetValue(_cacheCandidates) is { } list ? CacheCandidates(list) : AMPRProfiler.DefaultCacheCandidates,
                Lanes = parse.GetValue(_lanes),
                Workers = parse.GetValue(_workers),
                RuntimeWorkers = parse.GetValue(_runtimeWorkers),
                Strategy = parse.GetValue(_strategy)!,
                PatternMode = parse.GetValue(_patternMode)!,
                MinReads = parse.GetValue(_minReads),
                MinRequestedBytes = AMPRSize.Parse(parse.GetValue(_minRequestedBytes)!),
                MaxRuleFiles = parse.GetValue(_maxRuleFiles),
                ExternalizePaths = parse.GetValue(_externalizePaths),
                GeneralizeCoverage = parse.GetValue(_generalizeCoverage),
                GeneralizeMinFiles = parse.GetValue(_generalizeMinFiles),
                CacheSimulation = parse.GetValue(_cacheSim) || (!parse.GetValue(_noCacheSim) && _cacheSimulationDefault),
                CacheMaxTouches = parse.GetValue(_cacheMaxTouches),
                ContentRoot = parse.GetValue(_contentRoot) is { } content ? AmprCommand.Resolve(ctx, content) : null,
                SampleBudget = AMPRSize.Parse(parse.GetValue(_sampleBudget)!),
                SampleBlocksPerFile = parse.GetValue(_sampleBlocksPerFile),
                SampleMode = parse.GetValue(_sampleMode)!,
                SampleLevel = parse.GetValue(_sampleLevel),
                SampleAcceleration = parse.GetValue(_sampleAcceleration),
            };
        }
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
        Option<string?> metrics = new("--metrics");
        Option<string?> runtimeHeader = new("--runtime-header") { Description = "optional force-include header with runtime cache/pool/coalescing defines" };
        Option<bool> untracedTypes = new("--pack-untraced-types") { Description = AmprCommand.UntracedTypesDescription + " (MkPFS extension)" };
        CommonOptions common = new("exact", cacheSimulationDefault: true);
        Command command = new("generate", "generate one profile") { input, trace, name, output, report, metrics, runtimeHeader, untracedTypes };
        common.AddTo(command);
        command.SetAction(parse => AmprCommand.Run(ctx, () =>
        {
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

            AMPRProfileOptions options = common.Read(ctx, parse, parse.GetValue(name) ?? traces[0].Name);
            options.Validate();
            AMPRProfileResult result = AMPRProfiler.Build(traces, options);
            string outputText = parse.GetValue(output)!;
            string outputPath = AmprCommand.Resolve(ctx, outputText);
            Dictionary<string, string> sidecars = [];
            AMPRProfiler.UntracedAddition? added = null;
            string toml;
            if (parse.GetValue(untracedTypes))
            {
                toml = AMPRProfiler.RenderToml(result, options, traces, sidecars, Path.GetFileNameWithoutExtension(outputPath), out AMPRProfiler.UntracedAddition addition);
                added = addition;
            }
            else
            {
                toml = AMPRProfiler.RenderToml(result, options, sidecars, Path.GetFileNameWithoutExtension(outputPath));
            }

            bool replace = parse.GetValue(common.Overwrite);
            WriteBundle(outputPath, toml, sidecars, replace);
            if (parse.GetValue(report) is { } reportPath)
            {
                WriteText(AmprCommand.Resolve(ctx, reportPath), AMPRProfiler.RenderReport(result), replace);
            }

            if (parse.GetValue(metrics) is { } metricsPath)
            {
                WriteText(AmprCommand.Resolve(ctx, metricsPath), AMPRProfiler.RenderMetricsJson(result, parse.GetValue(common.FullMetrics)), replace);
            }

            if (parse.GetValue(runtimeHeader) is { } headerPath)
            {
                WriteText(AmprCommand.Resolve(ctx, headerPath), AMPRProfiler.RenderRuntimeHeader(result), replace);
            }

            ctx.Out.WriteLine(
                $"generated {outputText}: files={result.ObservedFiles} index={AMPRProfiler.HumanSize(result.ProjectedIndexBytes)} " +
                $"cache={AMPRProfiler.HumanSize(result.RecommendedCacheBytes)} pool={AMPRProfiler.HumanSize(result.RecommendedPoolBytes)}");
            if (added is { } extra)
            {
                // Not in ampr_pack_profile.py.
                ctx.Out.WriteLine(
                    $"added {extra.Files} untraced files of traced types: {AMPRProfiler.HumanSize(extra.Bytes)}, " +
                    $"index +{AMPRProfiler.HumanSize(extra.IndexBytes)}");
            }
        }));
        return command;
    }

    private static Command Batch(CliContext ctx)
    {
        Argument<string> input = new("input");
        Option<string> outputDir = new("--output-dir") { Required = true };
        Option<string?> summary = new("--summary");
        Option<int> batchJobs = new("--batch-jobs")
        {
            Description = $"parallel title profiles (0 = auto, currently {DefaultBatchJobs}); reduce this for memory-constrained hosts",
            DefaultValueFactory = _ => 0,
        };
        CommonOptions common = new("hybrid", cacheSimulationDefault: false);
        Command command = new("batch", "scan a directory or ZIP and generate one profile per subdirectory") { input, outputDir, summary, batchJobs };
        common.AddTo(command);
        command.SetAction(parse => AmprCommand.Run(ctx, () =>
        {
            string inputPath = AmprCommand.Resolve(ctx, parse.GetValue(input)!);
            string output = AmprCommand.Resolve(ctx, parse.GetValue(outputDir)!);
            Directory.CreateDirectory(output);
            string temp = Directory.CreateTempSubdirectory("ampr-profile-").FullName;
            try
            {
                string scanRoot = File.Exists(inputPath) ? AMPRProfiler.ExtractTraceArchive(inputPath, temp) : inputPath;
                List<AMPRTraceSpec> pairs = AMPRProfiler.DiscoverTracePairs(scanRoot);
                if (pairs.Count == 0)
                {
                    throw new AMPRPackException($"no trace pairs found under {inputPath}");
                }

                Dictionary<string, int> usedNames = [];
                List<(AMPRTraceSpec Pair, AMPRProfileOptions Options)> tasks = [];
                foreach (AMPRTraceSpec pair in pairs)
                {
                    string baseName = pair.Name.Length > 0 ? pair.Name : Path.GetFileName(Path.GetDirectoryName(pair.Commands)!) is { Length: > 0 } dir ? dir : "profile";
                    int count = usedNames[baseName] = usedNames.GetValueOrDefault(baseName) + 1;
                    AMPRProfileOptions options = common.Read(ctx, parse, count == 1 ? baseName : $"{baseName}-{count}");
                    // Batch content roots are ambiguous; sampling needs generate with one trace.
                    options.ContentRoot = null;
                    options.SampleBudget = 0;
                    options.Validate();
                    tasks.Add((pair, options));
                }

                bool fullMetrics = parse.GetValue(common.FullMetrics);
                bool replace = parse.GetValue(common.Overwrite);
                int jobs = parse.GetValue(batchJobs) is > 0 and int requested ? requested : DefaultBatchJobs;
                jobs = Math.Min(jobs, tasks.Count);
                PythonJsonObject[] rows = new PythonJsonObject[tasks.Count];
                if (jobs <= 1)
                {
                    for (int i = 0; i < tasks.Count; i++)
                    {
                        rows[i] = BuildBatchProfile(tasks[i].Pair, tasks[i].Options, output, fullMetrics, replace);
                        ctx.Out.WriteLine($"generated {tasks[i].Options.Name}.toml");
                    }
                }
                else
                {
                    // Titles are independent; results and the first error come in input order, like
                    // ProcessPoolExecutor.map.
                    Exception?[] errors = new Exception?[tasks.Count];
                    Parallel.For(0, tasks.Count, new ParallelOptions { MaxDegreeOfParallelism = jobs }, i =>
                    {
                        try
                        {
                            rows[i] = BuildBatchProfile(tasks[i].Pair, tasks[i].Options, output, fullMetrics, replace);
                        }
                        catch (Exception exc)
                        {
                            errors[i] = exc;
                        }
                    });
                    for (int i = 0; i < tasks.Count; i++)
                    {
                        if (errors[i] is { } error)
                        {
                            ExceptionDispatchInfo.Throw(error);
                        }

                        ctx.Out.WriteLine($"generated {tasks[i].Options.Name}.toml");
                    }
                }

                string summaryPath = parse.GetValue(summary) is { } path ? AmprCommand.Resolve(ctx, path) : Path.Combine(output, "summary.json");
                WriteText(summaryPath, PythonSortedJson.Indented(rows) + "\n", replace);
            }
            finally
            {
                Directory.Delete(temp, recursive: true);
            }
        }));
        return command;
    }

    // Python _build_batch_profile: TOML (+ sidecars), report, metrics JSON and runtime header per title.
    private static PythonJsonObject BuildBatchProfile(AMPRTraceSpec pair, AMPRProfileOptions options, string output, bool fullMetrics, bool overwrite)
    {
        AMPRProfileResult result = AMPRProfiler.Build([pair], options);
        string name = options.Name;
        string tomlPath = Path.Combine(output, $"{name}.toml");
        Dictionary<string, string> sidecars = [];
        string toml = AMPRProfiler.RenderToml(result, options, sidecars, Path.GetFileNameWithoutExtension(tomlPath));
        WriteBundle(tomlPath, toml, sidecars, overwrite);
        WriteText(Path.Combine(output, $"{name}.md"), AMPRProfiler.RenderReport(result), overwrite);
        WriteText(Path.Combine(output, $"{name}.json"), AMPRProfiler.RenderMetricsJson(result, fullMetrics), overwrite);
        WriteText(Path.Combine(output, $"{name}.runtime.h"), AMPRProfiler.RenderRuntimeHeader(result), overwrite);
        return AMPRProfiler.BatchSummaryRow(result);
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
