using System.CommandLine;
using System.Globalization;
using System.Text;
using MkPFS.Build;
using MkPFS.Build.PFS;
using MkPFS.Cli.Output;
using MkPFS.Core.Diagnostics;
using MkPFS.Core.PFS;
using MkPFS.Core.PFSC;
using MkPFS.Core.Util;

namespace MkPFS.Cli.Commands;

/// <summary><c>batch</c>: pack every folder and image file in a folder into <c>.ffpfsc</c> (port of Python <c>cli_mkpfs_batch_run</c>).</summary>
internal static class BatchCommand
{
    public static Command Create(CliContext ctx)
    {
        Argument<string> sourceDir = new("source_dir") { Description = "Directory containing items to convert" };
        Argument<string> outputDir = new("output_dir") { Description = "Directory where .ffpfsc images are written" };
        Option<bool> verify = new("--verify") { Description = "Run full verification after a successful pack" };
        Option<bool> overwrite = new("--overwrite") { Description = "Overwrite existing output files (default: skip)" };
        Option<bool> dryRun = new("--dry-run") { Description = "Report what would be done without writing" };
        Option<bool> compress = new("--compress") { Description = "Enable PFSC block compression (default)" };
        Option<bool> noCompress = new("--no-compress") { Description = "Disable PFSC block compression" };
        Option<int> thresholdGain = new("--threshold-gain") { Description = "Minimum per-block gain percent to keep PFSC-compressed blocks (default: 0)" };
        Option<string> blockSize = new("--block-size") { Description = "PFS block size in bytes or 'auto' (default: 65536)", DefaultValueFactory = _ => "auto" };
        Option<string> version = new("--version") { Description = "PFS profile version (default: PS5)", DefaultValueFactory = _ => "PS5" };
        version.AcceptOnlyFromAmong("PS4", "PS5");
        Option<int> inodeBits = new("--inode-bits") { Description = "Inode width mode bit (32 or 64, default: 32)", DefaultValueFactory = _ => 32 };
        inodeBits.AcceptOnlyFromAmong("32", "64");
        Option<bool> caseSensitive = new("--case-sensitive") { Description = "Build a case-sensitive image" };
        Option<bool> caseInsensitive = new("--case-insensitive") { Description = "Set case-insensitive mode bit (default)" };
        Option<int> cpuCount = new("--cpu-count") { Description = "Number of CPU cores for PFSC compression (0 = auto)" };
        Option<int> level = new("--compression-level") { Description = "Zlib compression level (0-9, default: 7)", DefaultValueFactory = _ => 7 };
        Option<string> backend = new("--compression-backend") { Description = "Accepted for compatibility; this port always uses zlib 1.3.1", DefaultValueFactory = _ => "auto" };
        backend.AcceptOnlyFromAmong("auto", "zlib-ng", "zlib", "isal");
        Option<int> maxRatio = new("--max-compressed-ratio") { Description = "Maximum PFSC size as percent of raw file (0-100, default: 100)", DefaultValueFactory = _ => 100 };
        Option<int> minCompressSize = new("--min-compress-size") { Description = "Store files smaller than N bytes raw (default: resolved --block-size)" };
        Option<bool> skipExecutables = new("--skip-executable-compression") { Description = "Skip compression in executable files" };
        Option<bool> verbose = new("--verbose") { Description = "Verbose per-file decisions" };
        Option<bool> encrypted = new("--encrypted") { Description = "Encrypt filesystem blocks with AES-XTS" };
        Option<string?> ekpfsKey = new("--ekpfs-key") { Description = "Optional 64-hex EKPFS key for encrypted images" };
        Option<bool> newCrypt = new("--new-crypt") { Description = "Use alternate newCrypt EKPFS derivation" };
        Command command = new("batch", "Batch convert multiple items in a directory into .ffpfsc images")
        {
            sourceDir, outputDir, verify, overwrite, dryRun, compress, noCompress, thresholdGain, blockSize, version, inodeBits,
            caseSensitive, caseInsensitive, cpuCount, level, backend, maxRatio, minCompressSize, skipExecutables, verbose,
            encrypted, ekpfsKey, newCrypt,
        };
        command.Validators.Add(result =>
        {
            foreach ((Option<bool> a, Option<bool> b) in new[] { (compress, noCompress), (caseSensitive, caseInsensitive) })
            {
                if (result.GetResult(a) is { Implicit: false } && result.GetResult(b) is { Implicit: false })
                {
                    result.AddError($"argument {b.Name}: not allowed with argument {a.Name}");
                }
            }
        });
        command.SetAction(parse =>
        {
            string source = FullPath(parse.GetValue(sourceDir)!);
            string output = FullPath(parse.GetValue(outputDir)!);
            ctx.VersionHeader();
            try
            {
                string blockArg = parse.GetValue(blockSize)!;
                int resolvedBlock = blockArg.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase) ? 65536
                    : int.TryParse(blockArg, NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int parsed)
                        ? parsed
                        : throw new BuildException("--block-size must be an integer value or 'auto' for batch conversion");
                PackArgs args = new()
                {
                    Source = source,
                    ImageFile = output,
                    NoCompress = parse.GetValue(noCompress),
                    ThresholdGain = parse.GetValue(thresholdGain),
                    Version = parse.GetValue(version)!,
                    InodeBits = parse.GetValue(inodeBits),
                    CaseSensitive = parse.GetValue(caseSensitive),
                    CaseInsensitive = parse.GetValue(caseInsensitive),
                    CpuCount = parse.GetValue(cpuCount),
                    CompressionLevel = parse.GetValue(level),
                    CompressionBackend = parse.GetValue(backend)!,
                    MaxCompressedRatio = parse.GetValue(maxRatio),
                    MinCompressSize = parse.GetValue(minCompressSize),
                    Encrypted = parse.GetValue(encrypted),
                    EkpfsKey = parse.GetValue(ekpfsKey),
                    NewCrypt = parse.GetValue(newCrypt),
                    Verbose = parse.GetValue(verbose),
                    DryRun = parse.GetValue(dryRun),
                    Verify = parse.GetValue(verify),
                };
                return Run(ctx, args, PackBuildConfig.Resolve(args, resolvedBlock), source, output, parse.GetValue(overwrite));
            }
            catch (BuildException ex)
            {
                ctx.Log.Error(ex.Message);
                return 1;
            }
        });
        return command;
    }

    private static int Run(CliContext ctx, PackArgs args, PackBuildConfig config, string source, string output, bool overwrite)
    {
        if (args.CompressionBackend is not ("auto" or "zlib"))
        {
            ctx.Warning($"--compression-backend {args.CompressionBackend} is not supported; using zlib 1.3.1");
        }

        List<BatchItem> items = Batch.Discover(source, ctx.Log);
        if (items.Count == 0)
        {
            ctx.Info($"No packable items found in {source}");
            return 0;
        }

        PreStats(ctx, source, output, items, config);
        int total = items.Count;
        BatchSummary summary = Batch.Run(
            new BatchOptions
            {
                SourceDir = source,
                OutputDir = output,
                Overwrite = overwrite,
                DryRun = args.DryRun,
                Verify = args.Verify,
                Build = new SingleFileBuildOptions
                {
                    SourceFile = source,
                    OutputPath = output,
                    BlockSize = config.BlockSize,
                    PFSVersion = config.PFSVersion,
                    CaseInsensitive = config.CaseInsensitive,
                    Compress = config.Compress,
                    ZlibLevel = config.ZlibLevel,
                    ThresholdGain = config.ThresholdGain,
                    MinFileGain = config.MinFileGain,
                    MinCompressSize = config.MinCompressSize,
                    CpuCount = PFSCEncoder.ResolveWorkerCount(config.CpuCount),
                    SkipExecutableCompression = config.SkipExecutableCompression,
                    Encrypted = config.Encrypted,
                    NewCrypt = config.NewCrypt,
                    Ekpfs = config.Ekpfs,
                    Verbose = args.Verbose,
                    Timestamp = PackReport.Timestamp(),
                },
            },
            items, ctx.Log, ctx.CreateProgress(),
            converting: (index, item, path) => ctx.Info($"  [{index}/{total}] 📦 Converting {(item.Kind == BatchItemKind.Folder ? "folder" : "file")} '{item.Name}' → {path}"),
            finished: (index, result) => ItemStatus(ctx, index, total, result),
            failed: (index, item, message) => ctx.Log.Warning($"  [{index}/{total}] ❌ {item.Name} failed: {message}"));
        Summary(ctx, summary);
        ctx.Info(FormattableString.Invariant($"Total elapsed: {summary.ElapsedSeconds:F1}s"));
        ctx.Info($"Output directory: {output}");
        return summary.Errors == 0 ? 0 : 1;
    }

    // Python print_batch_pre_stats. Python compares the version with 0x5000000 and always prints PS4 (oracle
    // finding 14); this prints the real profile.
    private static void PreStats(CliContext ctx, string source, string output, List<BatchItem> items, PackBuildConfig config)
    {
        int folders = items.Count(i => i.Kind == BatchItemKind.Folder);
        int files = items.Count - folders;
        ctx.Info("╔═══════════════════════════════════════╗");
        ctx.Info("║          BATCH CONVERSION             ║");
        ctx.Info("╚═══════════════════════════════════════╝");
        ctx.Info($"  Source  : {source}");
        ctx.Info($"  Output  : {output}");
        ctx.Info($"  Items   : {folders} folder(s), {files} file(s) — {items.Count} total");
        string compress = config.Compress ? $"yes  (level {config.ZlibLevel})"
            : folders > 0 && files == 0 ? "yes  (folders always compressed)"
            : folders > 0 ? "mixed  (folders compressed, files raw)"
            : "no";
        ctx.Info($"  Version : {(config.PFSVersion == PFSConstants.PFSVersionPS5 ? "PS5" : "PS4")}");
        ctx.Info($"  Compress: {compress}");
        ctx.Info($"  CPUs    : {(config.CpuCount != 0 ? config.CpuCount.ToString(CultureInfo.InvariantCulture) : "auto")}");
        ctx.Info(string.Empty);
    }

    // Python print_item_status.
    private static void ItemStatus(CliContext ctx, int index, int total, BatchItemResult result)
    {
        string icon = result.Status switch
        {
            BatchStatus.Converted => "✅",
            BatchStatus.Skipped => "⏭",
            BatchStatus.DryRun => "🔍",
            _ => "❌",
        };
        List<string> parts = [$"[{index}/{total}] {icon} {result.Item.Name}"];
        switch (result.Status)
        {
            case BatchStatus.Converted:
                parts.Add($"→ {Sizes.HumanReadable(result.CompressedSize)}");
                if (result.SavingsPercent > 0)
                {
                    parts.Add(FormattableString.Invariant($"({result.SavingsPercent:F1}% saved)"));
                }

                parts.Add(FormattableString.Invariant($"in {result.ElapsedSeconds:F1}s"));
                break;
            case BatchStatus.Skipped:
                parts.Add("→ skipped (output exists)");
                break;
            case BatchStatus.Error:
                string message = result.ErrorMessage ?? "unknown error";
                parts.Add($"→ {(CodePoints(message) > 80 ? Slice(message, 77) + "..." : message)}");
                break;
            default:
                parts.Add("→ dry-run (no output written)");
                break;
        }

        ctx.Info("  " + string.Join("  ", parts));
    }

    // Python print_batch_summary (widths in code points, like Python's str.ljust/rjust).
    private static void Summary(CliContext ctx, BatchSummary summary)
    {
        int total = summary.Results.Count;
        if (total == 0)
        {
            return;
        }

        int nameWidth = Math.Max(summary.Results.Max(r => CodePoints(r.Item.Name)) + 2, 20);
        const int sizeWidth = 11;
        string line(char left, char mid, char right) =>
            $"{left}{new string('─', nameWidth)}{mid}{new string('─', 10)}{mid}{new string('─', sizeWidth)}{mid}{new string('─', sizeWidth)}{mid}{new string('─', 9)}{right}";
        string sep = line('├', '┼', '┤');
        ctx.Info(line('┌', '┬', '┐'));
        ctx.Info($"│ {Ljust("Name", nameWidth - 2)}│ {Ljust("Status", 8)}│ {Rjust("Raw", sizeWidth - 2)}│ {Rjust("Compressed", sizeWidth - 2)}│ {Rjust("Savings", 7)}│");
        ctx.Info(sep);
        foreach (BatchItemResult r in summary.Results)
        {
            string status = r.Status switch
            {
                BatchStatus.Converted => "✅ Done",
                BatchStatus.Skipped => "⏭ Skipped",
                BatchStatus.DryRun => "🔍 Planned",
                _ => "❌ Error",
            };
            string raw = r.RawSize > 0 ? Sizes.HumanReadable(r.RawSize) : "—";
            string compressed = r.CompressedSize > 0 ? Sizes.HumanReadable(r.CompressedSize) : string.Empty;
            string savings = r.Status == BatchStatus.Converted && r.SavingsPercent > 0 ? FormattableString.Invariant($"{r.SavingsPercent:F1}%") : "—";
            ctx.Info($"│ {Ljust(Slice(r.Item.Name, nameWidth - 2), nameWidth - 2)}│ {Ljust(status, 8)}│ {Rjust(raw, sizeWidth - 2)}│ {Rjust(compressed, sizeWidth - 2)}│ {Rjust(savings, 7)}│");
        }

        ctx.Info(sep);
        string done = $"{summary.Converted} done";
        if (summary.DryRun > 0)
        {
            done += $", {summary.DryRun} planned";
        }

        if (summary.Skipped > 0)
        {
            done += $", {summary.Skipped} skipped";
        }

        if (summary.Errors > 0)
        {
            done += $", {summary.Errors} error{(summary.Errors != 1 ? "s" : string.Empty)}";
        }

        string totalRaw = summary.TotalRawSize > 0 ? Sizes.HumanReadable(summary.TotalRawSize) : "—";
        string totalCompressed = summary.TotalCompressedSize > 0 ? Sizes.HumanReadable(summary.TotalCompressedSize) : "—";
        double overall = summary.TotalRawSize > 0 && summary.TotalCompressedSize > 0
            ? (double)(summary.TotalRawSize - summary.TotalCompressedSize) / summary.TotalRawSize * 100.0
            : 0.0;
        string overallText = overall > 0 ? FormattableString.Invariant($"{overall:F1}%") : "—";
        int combined = nameWidth + 10;
        string label = $"TOTALS ({total} items) — {done}";
        bool tooLong = CodePoints(label) > combined - 2;
        if (tooLong)
        {
            label = $"TOTALS ({total} items)";
        }

        ctx.Info($"│ {Ljust(label, combined - 2)}│ {Rjust(totalRaw, sizeWidth - 2)}│ {Rjust(totalCompressed, sizeWidth - 2)}│ {Rjust(overallText, 7)}│");
        ctx.Info(line('└', '┴', '┘'));
        if (tooLong)
        {
            ctx.Info($"  {done}");
        }
    }

    private static int CodePoints(string text) => text.EnumerateRunes().Count();

    private static string Slice(string text, int codePoints)
    {
        StringBuilder result = new();
        foreach (Rune rune in text.EnumerateRunes().Take(Math.Max(0, codePoints)))
        {
            result.Append(rune.ToString());
        }

        return result.ToString();
    }

    private static string Ljust(string text, int width) => text + new string(' ', Math.Max(0, width - CodePoints(text)));

    private static string Rjust(string text, int width) => new string(' ', Math.Max(0, width - CodePoints(text))) + text;

    private static string FullPath(string path) => Path.GetFullPath(PathRules.ExpandUser(path));
}
