using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using MkPFS.Build.AMPRPack;
using MkPFS.Cli.Output;
using MkPFS.Core.AMPR;
using MkPFS.Core.Diagnostics;
using MkPFS.Core.Util;

namespace MkPFS.Cli.Commands;

/// <summary>
/// <c>ampr</c>: AMPR Emu seekable LZ4 asset packs (port of ampr_emu <c>tools/ampr_pack.py</c>). Subcommands, options,
/// JSON (<c>json.dumps(indent=2, ensure_ascii=False, sort_keys=True)</c>) and <c>error: ...</c> lines with exit code 2
/// match the oracle.
/// </summary>
internal static class AmprCommand
{
    /// <summary>Printed to stderr when a pack run packs nothing (an addition to the oracle output).</summary>
    internal const string NothingPackedWarning =
        "warning: no files were packed; without --config or --preset default every file stays loose";

    public static Command Create(CliContext ctx)
    {
        Command command = new("ampr", "Build and manage AMPR Emu seekable LZ4 asset packs for /app0");
        command.Subcommands.Add(Pack(ctx));
        command.Subcommands.Add(Unpack(ctx));
        command.Subcommands.Add(Verify(ctx));
        command.Subcommands.Add(RemoveSources(ctx));
        command.Subcommands.Add(List(ctx));
        command.Subcommands.Add(Inspect(ctx));
        command.Subcommands.Add(RuntimeConfig(ctx));
        return command;
    }

    private static Option<string> Required(string name, string description) => new(name) { Description = description, Required = true };

    private static Option<string[]> Repeated(string name, string description, string? helpName = null) =>
        new(name) { Description = description, HelpName = helpName, AllowMultipleArgumentsPerToken = false, DefaultValueFactory = _ => [] };

    private static Command Pack(CliContext ctx)
    {
        Option<string> root = Required("--root", "deployed /app0 directory");
        Option<string> amprIndex = Required("--ampr-index", "AMPRIDX3 file");
        Option<string> output = Required("--output", "output directory");
        Option<string?> config = new("--config") { Description = "TOML pack configuration" };
        Option<string?> preset = new("--preset") { Description = "built-in rules instead of --config: default compresses every file and keeps executables, modules and system files loose" };
        preset.AcceptOnlyFromAmong([.. AMPRPackConfig.PresetNames]);
        Option<string[]> include = Repeated("--include", "additional include glob");
        Option<string[]> exclude = Repeated("--exclude", "force-loose glob");
        Option<string?> includeFrom = new("--include-from") { Description = "newline-delimited include globs" };
        Option<string?> excludeFrom = new("--exclude-from") { Description = "newline-delimited exclude globs" };
        Option<int?> workers = new("--workers") { Description = "compression workers" };
        Option<bool> selfContained = new("--self-contained") { Description = "do not auto-loose explicitly packed files; store incompressible blocks RAW" };
        Option<string[]> requirePacked = Repeated("--require-packed", "fail if a matching logical file would remain loose (repeatable)", "GLOB");
        Option<bool> allowMissing = new("--allow-missing") { Description = "leave missing selected files loose" };
        Option<bool> noProgress = new("--no-progress") { Description = "suppress build progress on stderr; final JSON still goes to stdout" };
        Command command = new("pack", "build pack index and data volumes")
        {
            root, amprIndex, output, config, preset, include, exclude, includeFrom, excludeFrom, workers, selfContained, requirePacked, allowMissing, noProgress,
        };
        command.SetAction(parse => Run(ctx, () =>
        {
            string? configPath = parse.GetValue(config);
            string? presetName = parse.GetValue(preset);
            AMPRPackConfig loaded = (configPath, presetName) switch
            {
                (not null, not null) => throw new AMPRPackException("--config and --preset cannot be used together"),
                (null, not null) => AMPRPackConfig.LoadPreset(presetName),
                _ => AMPRPackConfig.Load(configPath is null ? null : Resolve(ctx, configPath)),
            };
            if (parse.GetValue(workers) is { } count)
            {
                loaded.Workers = count is >= 1 and <= 256 ? count : throw new AMPRPackException("--workers must be between 1 and 256");
            }

            if (parse.GetValue(selfContained))
            {
                loaded.SelfContained = true;
            }

            if (parse.GetValue(requirePacked) is { Length: > 0 } required)
            {
                loaded.RequiredPacked = [.. loaded.RequiredPacked, .. required];
            }

            List<string> includes = [.. parse.GetValue(include)!, .. PatternFile(ctx, parse.GetValue(includeFrom))];
            List<string> excludes = [.. parse.GetValue(exclude)!, .. PatternFile(ctx, parse.GetValue(excludeFrom))];
            Action<AMPRBuildProgress>? progress = parse.GetValue(noProgress) || !ctx.ProgressEnabled ? null : ProgressReporter(ctx);
            AMPRBuildResult result = AMPRPackBuilder.Build(
                Resolve(ctx, parse.GetValue(root)!),
                Resolve(ctx, parse.GetValue(amprIndex)!),
                Resolve(ctx, parse.GetValue(output)!),
                loaded,
                includes,
                excludes,
                parse.GetValue(allowMissing),
                progress);
            AMPRBuildStats stats = result.Stats;
            PrintJson(ctx, new Dictionary<string, object?>
            {
                ["files_total"] = stats.FilesTotal,
                ["files_packed"] = stats.FilesPacked,
                ["files_loose"] = stats.FilesLoose,
                ["loose_paths"] = stats.LoosePaths,
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
                ["index"] = result.IndexPath,
                ["crc"] = AMPRPackFormat.ChunkCrcPath(result.IndexPath),
                ["compression_ratio"] = stats.LogicalBytes != 0 ? (double)stats.StoredBytes / stats.LogicalBytes : 1.0,
                ["warnings"] = result.Warnings,
            });
            if (stats.FilesPacked == 0)
            {
                // Not in ampr_pack.py: a run without rules leaves every file loose and is easy to start by mistake.
                ctx.Err.WriteLine(NothingPackedWarning);
            }
        }));
        return command;
    }

    private static Command Unpack(CliContext ctx)
    {
        Option<string> index = Required("--index", "pack manifest");
        Option<string> output = Required("--output", "output directory");
        Option<string[]> file = Repeated("--file", "file glob");
        Option<bool> overwrite = new("--overwrite") { Description = "replace existing files" };
        Option<bool> noMTime = new("--no-preserve-mtime") { Description = "do not restore file modification times" };
        Command command = new("unpack", "extract packed files") { index, output, file, overwrite, noMTime };
        command.SetAction(parse => Run(ctx, () =>
        {
            AMPRExtractResult result = AMPRPackTools.Extract(
                Resolve(ctx, parse.GetValue(index)!), Resolve(ctx, parse.GetValue(output)!), parse.GetValue(file), parse.GetValue(overwrite), !parse.GetValue(noMTime));
            PrintJson(ctx, new Dictionary<string, object?> { ["files"] = result.Files, ["bytes"] = result.Bytes });
        }));
        return command;
    }

    private static Command Verify(CliContext ctx)
    {
        Option<string> index = Required("--index", "pack manifest");
        Option<string[]> file = Repeated("--file", "file glob");
        Option<string?> root = new("--root") { Description = "also reconstruct packed files and compare byte-for-byte with this /app0 root" };
        Command command = new("verify", "validate every selected packed block") { index, file, root };
        command.SetAction(parse => Run(ctx, () =>
        {
            string indexPath = Resolve(ctx, parse.GetValue(index)!);
            AMPRVerifyResult verify = AMPRPackTools.Verify(indexPath, parse.GetValue(file));
            Dictionary<string, object?> result = new()
            {
                ["files"] = verify.Files,
                ["physical_chunks"] = verify.PhysicalChunks,
                ["stored_bytes"] = verify.StoredBytes,
                ["raw_bytes"] = verify.RawBytes,
            };
            if (parse.GetValue(root) is { } sourceRoot)
            {
                AMPRSourceCompareResult compare = AMPRPackTools.VerifyAgainstRoot(indexPath, Resolve(ctx, sourceRoot), parse.GetValue(file));
                result["source_compare"] = new Dictionary<string, object?> { ["files"] = compare.Files, ["chunks"] = compare.Chunks, ["bytes"] = compare.Bytes };
            }

            PrintJson(ctx, result);
        }));
        return command;
    }

    private static Command RemoveSources(CliContext ctx)
    {
        Option<string> index = Required("--index", "pack manifest");
        Option<string> root = Required("--root", "/app0 source directory");
        Option<bool> confirm = new("--confirm") { Description = "perform permanent removal; without it, print a read-only plan" };
        Option<bool> removeEmptyDirs = new("--remove-empty-dirs") { Description = "remove directories left empty by source removal, never the /app0 root" };
        Command command = new("remove-sources", "verify, then remove local /app0 sources represented by PACK records") { index, root, confirm, removeEmptyDirs };
        command.Aliases.Add("remove-packed-sources");
        command.SetAction(parse => Run(ctx, () =>
        {
            string indexPath = Resolve(ctx, parse.GetValue(index)!);
            string rootPath = Resolve(ctx, parse.GetValue(root)!);
            if (parse.GetValue(confirm))
            {
                AMPRRemovalResult removed = AMPRPackMaintenance.RemovePackedSources(indexPath, rootPath, parse.GetValue(removeEmptyDirs));
                PrintJson(ctx, new Dictionary<string, object?>
                {
                    ["files"] = removed.Files,
                    ["bytes"] = removed.Bytes,
                    ["directories"] = removed.Directories,
                    ["verified"] = removed.Verified,
                });
                return;
            }

            AMPRRemovalPlan plan = AMPRPackMaintenance.RemovalPlan(indexPath, rootPath);
            PrintJson(ctx, new Dictionary<string, object?>
            {
                ["files"] = plan.Files,
                ["present_files"] = plan.PresentFiles,
                ["missing_files"] = plan.MissingFiles,
                ["bytes"] = plan.Bytes,
                ["paths"] = plan.Paths,
                ["dry_run"] = true,
            });
        }));
        return command;
    }

    private static Command List(CliContext ctx)
    {
        Option<string> index = Required("--index", "pack manifest");
        Option<string[]> file = Repeated("--file", "file glob");
        Option<bool> json = new("--json") { Description = "print JSON" };
        Command command = new("list", "list logical files and placement") { index, file, json };
        command.SetAction(parse => Run(ctx, () =>
        {
            List<AMPRListEntry> rows = AMPRPackTools.List(Resolve(ctx, parse.GetValue(index)!), parse.GetValue(file));
            if (parse.GetValue(json))
            {
                PrintJson(ctx, rows.Select(row => new Dictionary<string, object?>
                {
                    ["file_id"] = row.FileId,
                    ["path"] = row.Path,
                    ["packed"] = row.Packed,
                    ["logical_size"] = row.LogicalSize,
                    ["stored_size"] = row.StoredSize,
                    ["block_size"] = row.BlockSize,
                    ["chunks"] = row.Chunks,
                    ["codecs"] = row.Codecs,
                    ["packs"] = row.Packs,
                    ["io_page_sizes"] = row.IOPageSizes,
                    ["layout"] = row.Layout,
                    ["streaming"] = row.Streaming,
                    ["random_access"] = row.RandomAccess,
                    ["hot"] = row.Hot,
                }).ToList());
                return;
            }

            foreach (AMPRListEntry row in rows)
            {
                ctx.Out.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{row.FileId,8} {(row.Packed ? "PACK" : "LOOSE"),-5} {row.LogicalSize,12} {row.StoredSize,12} {row.BlockSize,8} {row.Path}"));
            }
        }));
        return command;
    }

    private static Command Inspect(CliContext ctx)
    {
        Option<string> index = Required("--index", "pack manifest");
        Command command = new("inspect", "show manifest-level summary") { index };
        command.SetAction(parse => Run(ctx, () =>
        {
            AMPRInspectResult result = AMPRPackTools.Inspect(Resolve(ctx, parse.GetValue(index)!));
            PrintJson(ctx, new Dictionary<string, object?>
            {
                ["build_id"] = result.BuildId,
                ["runtime"] = RuntimeJson(result.Runtime),
                ["files"] = result.Files,
                ["packed_files"] = result.PackedFiles,
                ["loose_files"] = result.LooseFiles,
                ["chunks"] = result.Chunks,
                ["packs"] = result.Packs.Select(pack => new Dictionary<string, object?>
                {
                    ["id"] = pack.Id,
                    ["name"] = pack.Name,
                    ["file_size"] = pack.FileSize,
                    ["payload_bytes"] = pack.PayloadBytes,
                    ["io_page_size"] = pack.IOPageSize,
                    ["io_pages"] = pack.IOPages,
                    ["flags"] = pack.Flags,
                }).ToList(),
            });
        }));
        return command;
    }

    private static Command RuntimeConfig(CliContext ctx)
    {
        Option<string> index = Required("--index", "pack manifest");
        Option<string> config = Required("--config", "TOML file with [runtime]");
        Command command = new("runtime-config", "atomically update per-pack runtime settings without repacking") { index, config };
        command.SetAction(parse => Run(ctx, () =>
        {
            string typed = parse.GetValue(index)!;
            AMPRRuntimeConfigResult result = AMPRPackMaintenance.WriteRuntimeConfig(Resolve(ctx, typed), Resolve(ctx, parse.GetValue(config)!));
            // Python prints Path(str(args.index) + ".runtime"): the path as typed.
            PrintJson(ctx, new Dictionary<string, object?>
            {
                ["path"] = PythonPathText(typed + ".runtime"),
                ["build_id"] = result.BuildId,
                ["runtime"] = RuntimeJson(result.Runtime),
            });
        }));
        return command;
    }

    // Python main(): PackError, ValueError, OSError and RuntimeError print "error: <message>" and exit 2.
    private static int Run(CliContext ctx, Action action)
    {
        try
        {
            action();
            return 0;
        }
        catch (Exception exc) when (exc is AMPRPackException or ArgumentException or InvalidDataException or IOException
            or UnauthorizedAccessException or InvalidOperationException)
        {
            ctx.Err.WriteLine($"error: {exc.Message}");
            return 2;
        }
    }

    private static void PrintJson(CliContext ctx, object value) => ctx.Out.WriteLine(PythonSortedJson.Indented(value));

    private static Dictionary<string, object?>? RuntimeJson(AMPRRuntimeSettings? settings) => settings is null ? null : new()
    {
        ["decoded_cache_bytes"] = settings.DecodedCacheBytes,
        ["physical_cache_bytes"] = settings.PhysicalCacheBytes,
        ["workers"] = settings.Workers,
        ["latency_reserve_workers"] = settings.LatencyReserveWorkers,
    };

    private static string Resolve(CliContext ctx, string path) => Path.GetFullPath(PathRules.ExpandUser(path), ctx.WorkingDirectory);

    private static List<string> PatternFile(CliContext ctx, string? path) =>
        path is null ? [] : AMPRPackConfig.LoadPatternFile(Resolve(ctx, path));

    // str(pathlib.Path(text)): platform separators, no empty or "." components.
    private static string PythonPathText(string text)
    {
        char separator = Path.DirectorySeparatorChar;
        string normalized = OperatingSystem.IsWindows() ? text.Replace('/', '\\') : text;
        string root = Path.GetPathRoot(normalized) ?? string.Empty;
        string[] parts = normalized[root.Length..].Split(separator, StringSplitOptions.RemoveEmptyEntries).Where(p => p != ".").ToArray();
        string joined = root + string.Join(separator, parts);
        return joined.Length == 0 ? "." : joined;
    }

    // Python ConsoleBuildProgress: throttled "[pack  NN%] phase: details, elapsed HH:MM:SS" lines on stderr, which the
    // GUI shows in its log. The GUI bar gets one overall "pack" phase with the same percentage on every event (the
    // sink also cancels the job), so it does not log a misleading "done" line per build phase.
    private static Action<AMPRBuildProgress> ProgressReporter(CliContext ctx)
    {
        IProgressSink? sink = ctx.ExternalProgressSink;
        Stopwatch clock = Stopwatch.StartNew();
        TimeSpan lastOutput = TimeSpan.Zero;
        int lastPercent = -1;
        string lastPhase = string.Empty;
        TimeSpan? packingStarted = null;
        return progress =>
        {
            TimeSpan now = clock.Elapsed;
            int percent = Percent(progress);
            sink?.Step("pack", percent, 100, progress.LogicalBytesDone);
            bool phaseChanged = progress.Phase != lastPhase;
            if (progress.Phase == "packing" && packingStarted is null)
            {
                packingStarted = now;
            }

            if (!phaseChanged && progress.Phase != "complete")
            {
                TimeSpan since = now - lastOutput;
                if (since.TotalSeconds < 0.5 || (percent <= lastPercent && since.TotalSeconds < 5.0))
                {
                    return;
                }
            }

            string details = $"files {progress.FilesDone}/{progress.FilesTotal}";
            if (progress.Phase == "packing")
            {
                details += $", {Size(progress.LogicalBytesDone)}/{Size(progress.LogicalBytesTotal)}";
                double seconds = packingStarted is { } started ? (now - started).TotalSeconds : 0.0;
                if (seconds > 0 && progress.LogicalBytesDone > 0)
                {
                    double rate = progress.LogicalBytesDone / seconds;
                    long eta = rate > 0 ? (long)(Math.Max(0, progress.LogicalBytesTotal - progress.LogicalBytesDone) / rate) : 0;
                    details += $", {Size((long)rate)}/s, ETA {Clock(eta)}";
                }
            }

            if (progress.CurrentPath.Length > 0)
            {
                details += $", current={progress.CurrentPath}";
            }

            ctx.Err.WriteLine($"[pack {percent,3}%] {progress.Phase}: {details}, elapsed {Clock((long)now.TotalSeconds)}");
            ctx.Err.Flush();
            lastOutput = now;
            lastPercent = percent;
            lastPhase = progress.Phase;
        };
    }

    private static int Percent(AMPRBuildProgress progress)
    {
        switch (progress.Phase)
        {
            case "planning":
                double planned = progress.FilesTotal != 0 ? (double)progress.FilesDone / progress.FilesTotal : 1.0;
                return Math.Min(10, (int)(planned * 10));
            case "packing":
                double packed = progress.LogicalBytesTotal != 0
                    ? (double)progress.LogicalBytesDone / progress.LogicalBytesTotal
                    : progress.FilesTotal != 0 ? (double)progress.FilesDone / progress.FilesTotal : 1.0;
                return Math.Min(95, 10 + (int)(packed * 85));
            default:
                return progress.Phase switch { "finalizing" => 96, "publishing" => 99, "complete" => 100, _ => 0 };
        }
    }

    private static string Clock(long seconds) => string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}");

    // Python _progress_size.
    private static string Size(long value)
    {
        double amount = value;
        foreach (string suffix in (string[])["B", "KiB", "MiB", "GiB", "TiB"])
        {
            if (amount < 1024.0 || suffix == "TiB")
            {
                return suffix == "B" ? $"{PythonText.FormatFixed(amount, 0)} B" : $"{PythonText.FormatFixed(amount, 1)} {suffix}";
            }

            amount /= 1024.0;
        }

        throw new UnreachableException();
    }
}
