using System.CommandLine;
using MkPFS.Build.PFS;
using MkPFS.Core.Diagnostics;
using MkPFS.Core.Crypto;
using MkPFS.Core.PFS;
using MkPFS.Core.PFSC;
using MkPFS.Core.Util;

namespace MkPFS.Cli.Commands;

/// <summary>Raw values of the shared pack options (Python <c>cli_mkpfs_add_create_args</c>).</summary>
internal sealed record PackArgs
{
    public required string Source { get; init; }

    public required string ImageFile { get; init; }

    public bool AdjustOutputFileExtension { get; init; } = true;

    public bool NoCompress { get; init; }

    public int ThresholdGain { get; init; }

    public string BlockSize { get; init; } = "auto";

    public string? TempFolder { get; init; }

    public string Version { get; init; } = "PS5";

    public int InodeBits { get; init; } = 32;

    public bool CaseSensitive { get; init; }

    public bool CaseInsensitive { get; init; }

    public int CpuCount { get; init; }

    public int CompressionLevel { get; init; } = 7;

    public string CompressionBackend { get; init; } = "auto";

    public int MaxCompressedRatio { get; init; } = 100;

    public int MinCompressSize { get; init; }

    public bool SkipExecutableCompression { get; init; } = true;

    public bool Signed { get; init; }

    public bool Encrypted { get; init; }

    public string? EkpfsKey { get; init; }

    public bool RequireGameFiles { get; init; }

    public bool Verbose { get; init; }

    public bool DryRun { get; init; }

    public bool Verify { get; init; }

    public bool VerifyStructure { get; init; } = true;

    public bool SkipVerification { get; init; }
}

/// <summary>Validated settings shared by every pack flow (Python <c>PackBuildConfig</c>).</summary>
internal sealed record PackBuildConfig(
    int BlockSize,
    bool Compress,
    int ThresholdGain,
    int MinFileGain,
    int MaxCompressedRatio,
    long MinCompressSize,
    bool CaseInsensitive,
    long PFSVersion,
    bool Encrypted,
    byte[] Ekpfs,
    int ZlibLevel,
    int CpuCount,
    bool SkipExecutableCompression,
    int InodeBits)
{
    /// <summary>Validate shared options (Python <c>_resolve_pack_build_config</c>).</summary>
    public static PackBuildConfig Resolve(PackArgs args, int blockSize)
    {
        if (!Sizes.IsPowerOfTwo(blockSize))
        {
            throw new BuildException("--block-size must be a power of two");
        }

        if (blockSize < 0x1000 || blockSize > 0x200000)
        {
            throw new BuildException("--block-size must be between 4096 and 2097152");
        }

        if (args.ThresholdGain is < 0 or > 100)
        {
            throw new BuildException("--threshold-gain must be within 0..100");
        }

        if (args.CpuCount < 0)
        {
            throw new BuildException("--cpu-count must be non-negative");
        }

        if (args.CompressionLevel is < 0 or > 9)
        {
            throw new BuildException("--compression-level must be within 0..9");
        }

        if (args.MaxCompressedRatio is < 0 or > 100)
        {
            throw new BuildException("--max-compressed-ratio must be within 0..100");
        }

        if (args.MinCompressSize < 0)
        {
            throw new BuildException("--min-compress-size must be non-negative");
        }

        byte[] ekpfs;
        try
        {
            ekpfs = PFSKeys.ParseEkpfsHex(args.EkpfsKey);
        }
        catch (FormatException ex)
        {
            throw new BuildException(ex.Message);
        }

        if (!string.IsNullOrEmpty(args.EkpfsKey) && !args.Encrypted)
        {
            throw new BuildException("--ekpfs-key requires --encrypted");
        }

        return new PackBuildConfig(
            blockSize,
            !args.NoCompress,
            args.ThresholdGain,
            100 - args.MaxCompressedRatio,
            args.MaxCompressedRatio,
            args.MinCompressSize > 0 ? args.MinCompressSize : blockSize,
            args.CaseInsensitive || !args.CaseSensitive,
            args.Version == "PS5" ? PFSConstants.PFSVersionPS5 : PFSConstants.PFSVersionPS4,
            args.Encrypted,
            ekpfs,
            args.CompressionLevel,
            args.CpuCount,
            args.SkipExecutableCompression,
            args.InodeBits);
    }
}

/// <summary>Post-pack verification depth (Python <c>PackVerificationMode</c>).</summary>
internal enum PackVerificationMode
{
    Skip,
    Structure,
    Full,
}

/// <summary>The shared pack options on one command, plus reading them back.</summary>
internal sealed class PackOptions
{
    private readonly Argument<string> _source;
    private readonly Argument<string> _image = new("image_file") { Description = "Output image file path" };
    private readonly Option<bool> _adjust = new("--adjust-output-file-extension") { Description = "Automatically adjust the output extension to match the pack mode (default)" };
    private readonly Option<bool> _noAdjust = new("--no-adjust-output-file-extension") { Description = "Keep the requested output file name unchanged" };
    private readonly Option<bool> _compress = new("--compress") { Description = "Enable PFSC block compression (default)" };
    private readonly Option<bool> _noCompress = new("--no-compress") { Description = "Disable PFSC block compression" };
    private readonly Option<int> _thresholdGain = new("--threshold-gain") { Description = "Minimum per-block gain percent to keep PFSC-compressed blocks (default: 0)" };
    private readonly Option<string> _blockSize = new("--block-size") { Description = "PFS block size in bytes, 'auto' (65536), or 'auto-fit' to minimize estimated file-data padding", DefaultValueFactory = _ => "auto" };
    private readonly Option<string?> _tempFolder = new("--temp-folder") { Description = "Directory for temporary pack artifacts (defaults to the system temp folder)" };
    private readonly Option<string> _version = new("--version") { Description = "PFS profile version (default: PS5)", DefaultValueFactory = _ => "PS5" };
    private readonly Option<int> _inodeBits = new("--inode-bits") { Description = "Inode width mode bit (32 or 64, default: 32)", DefaultValueFactory = _ => 32 };
    private readonly Option<bool> _caseSensitive = new("--case-sensitive") { Description = "Build a case-sensitive image" };
    private readonly Option<bool> _caseInsensitive = new("--case-insensitive") { Description = "Set case-insensitive mode bit (default)" };
    private readonly Option<int> _cpuCount = new("--cpu-count") { Description = "Number of CPU cores for PFSC compression (0 = auto min(16, max(1, cpu_count() - 1)), non-zero = max(1, user value))" };
    private readonly Option<int> _level = new("--compression-level") { Description = "Zlib compression level (0-9, default: 7)", DefaultValueFactory = _ => 7 };
    private readonly Option<string> _backend = new("--compression-backend") { Description = "Accepted for compatibility; this port always uses zlib 1.3.1", DefaultValueFactory = _ => "auto" };
    private readonly Option<int> _maxRatio = new("--max-compressed-ratio") { Description = "Maximum PFSC size as percent of the raw file size (0-100, default: 100)", DefaultValueFactory = _ => 100 };
    private readonly Option<int> _minCompressSize = new("--min-compress-size") { Description = "Store files smaller than this many bytes raw without trying PFSC compression (default: resolved --block-size value, 65536 for auto, auto-fit result when used)" };
    private readonly Option<bool> _skipExecutables = new("--skip-executable-compression") { Description = "Skip compression in important executable files" };
    private readonly Option<bool> _signed = new("--signed") { Description = "Build a signed PFS image using zero EKPFS/seed" };
    private readonly Option<bool> _encrypted = new("--encrypted") { Description = "Encrypt filesystem blocks with AES-XTS" };
    private readonly Option<string?> _ekpfsKey = new("--ekpfs-key") { Description = "Optional 64-hex EKPFS key, defaults to all zeros when omitted" };
    private readonly Option<bool>? _requireGameFiles;
    private readonly Option<bool> _verbose = new("--verbose") { Description = "Verbose per-file decisions" };
    private readonly Option<bool> _dryRun = new("--dry-run") { Description = "Scan/layout/report only; do not write image" };
    private readonly Option<bool> _verify = new("--verify") { Description = "Run full verification after a successful pack" };
    private readonly Option<bool> _verifyStructure = new("--verify-structure") { Description = "Run quick structure verification after a successful pack (default)" };
    private readonly Option<bool> _noVerifyStructure = new("--no-verify-structure") { Description = "Disable the default quick structure verification after a successful pack" };
    private readonly Option<bool> _skipVerification = new("--skip-verification") { Description = "Skip all post-pack verification" };

    /// <summary>Add the options to <paramref name="command"/> (Python <c>cli_mkpfs_add_create_args</c>).</summary>
    public PackOptions(Command command, string sourceName, string sourceHelp, bool includeRequireGameFiles)
    {
        _source = new Argument<string>(sourceName) { Description = sourceHelp };
        _version.AcceptOnlyFromAmong("PS4", "PS5");
        _inodeBits.AcceptOnlyFromAmong("32", "64");
        _backend.AcceptOnlyFromAmong("auto", "zlib-ng", "zlib", "isal");
        if (includeRequireGameFiles)
        {
            _requireGameFiles = new Option<bool>("--require-game-files") { Description = "Require sce_sys/param.json and eboot.bin before packing" };
        }

        command.Arguments.Add(_source);
        command.Arguments.Add(_image);
        foreach (Option option in new Option?[]
                 {
                     _adjust, _noAdjust, _compress, _noCompress, _thresholdGain, _blockSize, _tempFolder, _version, _inodeBits,
                     _caseSensitive, _caseInsensitive, _cpuCount, _level, _backend, _maxRatio, _minCompressSize, _skipExecutables,
                     _signed, _encrypted, _ekpfsKey, _requireGameFiles, _verbose, _dryRun, _verify, _verifyStructure,
                     _noVerifyStructure, _skipVerification,
                 }.OfType<Option>())
        {
            command.Options.Add(option);
        }

        // argparse mutually exclusive groups.
        (Option<bool>, Option<bool>)[] exclusive = [(_adjust, _noAdjust), (_compress, _noCompress), (_caseSensitive, _caseInsensitive), (_verifyStructure, _noVerifyStructure)];
        command.Validators.Add(result =>
        {
            foreach ((Option<bool> a, Option<bool> b) in exclusive)
            {
                if (result.GetResult(a) is { Implicit: false } && result.GetResult(b) is { Implicit: false })
                {
                    result.AddError($"argument {b.Name}: not allowed with argument {a.Name}");
                }
            }
        });
    }

    /// <summary>Read the values.</summary>
    public PackArgs Read(ParseResult parse) => new()
    {
        Source = parse.GetValue(_source)!,
        ImageFile = parse.GetValue(_image)!,
        AdjustOutputFileExtension = !parse.GetValue(_noAdjust),
        NoCompress = parse.GetValue(_noCompress),
        ThresholdGain = parse.GetValue(_thresholdGain),
        BlockSize = parse.GetValue(_blockSize)!,
        TempFolder = parse.GetValue(_tempFolder),
        Version = parse.GetValue(_version)!,
        InodeBits = parse.GetValue(_inodeBits),
        CaseSensitive = parse.GetValue(_caseSensitive),
        CaseInsensitive = parse.GetValue(_caseInsensitive),
        CpuCount = parse.GetValue(_cpuCount),
        CompressionLevel = parse.GetValue(_level),
        CompressionBackend = parse.GetValue(_backend)!,
        MaxCompressedRatio = parse.GetValue(_maxRatio),
        MinCompressSize = parse.GetValue(_minCompressSize),
        Signed = parse.GetValue(_signed),
        Encrypted = parse.GetValue(_encrypted),
        EkpfsKey = parse.GetValue(_ekpfsKey),
        RequireGameFiles = _requireGameFiles is not null && parse.GetValue(_requireGameFiles),
        Verbose = parse.GetValue(_verbose),
        DryRun = parse.GetValue(_dryRun),
        Verify = parse.GetValue(_verify),
        VerifyStructure = !parse.GetValue(_noVerifyStructure),
        SkipVerification = parse.GetValue(_skipVerification),
    };
}

/// <summary>Console output shared by the pack flows (Python <c>print_build_parameters</c>, <c>print_summary</c>, ...).</summary>
internal static class PackReport
{
    /// <summary>Python <c>print_build_parameters</c>.</summary>
    public static void Parameters(Output.CliContext ctx, PackBuildConfig config, string sourcePath, string outputPath, string tempFolder, bool signed, bool requireGameFiles, bool dryRun)
    {
        ctx.VersionHeader();
        ushort mode = PFSWriter.Mode(config.InodeBits, config.CaseInsensitive, signed, config.Encrypted);
        string yes(bool value) => value ? "yes" : "no";
        ctx.Info(new string('=', 70));
        ctx.Info("PFS Image Builder - Parameters");
        ctx.Info(new string('=', 70));
        ctx.Info($"  Source path:       {sourcePath}");
        ctx.Info($"  Output path:       {outputPath}");
        ctx.Info($"  Temp folder:       {tempFolder}");
        ctx.Info($"  Version:           {config.PFSVersion} ({(config.PFSVersion == PFSConstants.PFSVersionPS5 ? "PS5" : "PS4")})");
        ctx.Info($"  Header magic:      {ReadCommands.DescribeMagic(PFSConstants.PFSMagic)}");
        ctx.Info($"  Compression Setup: {(config.Compress ? ReadCommands.DescribeMagic(PFSConstants.PFSCMagic) : "none")}");
        ctx.Info($"  Block size:        {config.BlockSize / 1024} KiB ({Output.CliContext.Thousands(config.BlockSize)} bytes)");
        ctx.Info($"  Inode width:       {config.InodeBits}-bit");
        ctx.Info($"  PFS mode:          0x{mode:X4}  (Bit 0=signed, Bit 1=64-bit inodes, Bit 2=encrypted, Bit 3=case insensitive)");
        ctx.Info($"    Signed:          {yes((mode & PFSConstants.PFSModeSigned) != 0)}");
        ctx.Info($"    64-bit inodes:   {yes((mode & PFSConstants.PFSMode64BitInodes) != 0)}");
        ctx.Info($"    Encrypted:       {yes((mode & PFSConstants.PFSModeEncrypted) != 0)}");
        ctx.Info("    New crypt:       no");
        ctx.Info($"    Case insensitive: {yes((mode & PFSConstants.PFSModeCaseInsensitive) != 0)}");
        ctx.Info($"  Compression:       {(config.Compress ? "enabled" : "disabled")}");
        if (config.Compress)
        {
            ctx.Info($"    Skip executables: {yes(config.SkipExecutableCompression)}");
        }

        ctx.Info($"  Game-file checks:  {(requireGameFiles ? "required" : "disabled")}");
        if (config.Compress)
        {
            ctx.Info($"  Threshold gain:    {config.ThresholdGain}%");
            string cpu = config.CpuCount == 0 ? $"{PFSCEncoder.ResolveWorkerCount(0)} (auto)" : Math.Max(1, config.CpuCount).ToString(System.Globalization.CultureInfo.InvariantCulture);
            ctx.Info($"  CPU cores:         {cpu}");
            ctx.Info($"  Zlib level:        {config.ZlibLevel}");
            ctx.Info($"  Max PFSC ratio:    {config.MaxCompressedRatio}%");
            ctx.Info($"  Min compress size: {Sizes.HumanReadable(config.MinCompressSize)}");
        }

        ctx.Info($"  Dry run:           {yes(dryRun)}");
        ctx.Info(new string('=', 70));
    }

    /// <summary>Python <c>print_summary</c>.</summary>
    public static void Summary(Output.CliContext ctx, BuildStats stats)
    {
        string inv(FormattableString text) => FormattableString.Invariant(text);
        ctx.Info("\n" + new string('=', 70));
        ctx.Info("Build Summary");
        ctx.Info(new string('=', 70));
        ctx.Info($"  Input path:              {stats.InputPath}");
        ctx.Info($"  Output path:             {stats.OutputPath}");
        ctx.Info($"  Total files:             {Output.CliContext.Thousands(stats.TotalFiles)}");
        ctx.Info($"  Uncompressed size:       {Sizes.HumanReadable(stats.UncompressedTotalSize)} ({Output.CliContext.Thousands(stats.UncompressedTotalSize)} bytes)");
        ctx.Info($"  Stored size:             {Sizes.HumanReadable(stats.StoredTotalSize)} ({Output.CliContext.Thousands(stats.StoredTotalSize)} bytes)");
        long imageSize = File.Exists(stats.OutputPath) ? new FileInfo(stats.OutputPath).Length : 0;
        ctx.Info($"  Final image size:        {Sizes.HumanReadable(imageSize)} ({Output.CliContext.Thousands(imageSize)} bytes)");
        if (stats.CompressionEnabled)
        {
            ctx.Info("\n  Compression Statistics:");
            ctx.Info($"    Compressed files:      {Output.CliContext.Thousands(stats.CompressedFiles)}");
            ctx.Info($"    Uncompressed files:    {Output.CliContext.Thousands(stats.UncompressedFiles)}");
            ctx.Info(inv($"    Actual gain achieved:  {stats.ActualGainPercent:F2}%"));
            ctx.Info(inv($"    All-PFSC gain:         {stats.MaxPossibleGainPercent:F2}%  ({Sizes.HumanReadable(stats.AllCompressedTotalSize)} if every file used PFSC)"));
        }
        else
        {
            ctx.Info("\n  Compression:             disabled");
        }

        long aligned = stats.StoredTotalSize + stats.BlockAlignmentWaste;
        double wastePercent = aligned > 0 ? (double)stats.BlockAlignmentWaste / aligned * 100.0 : 0.0;
        ctx.Info("\n  Block Alignment Waste:");
        ctx.Info($"    Block size:            {stats.BlockSize / 1024} KiB ({Output.CliContext.Thousands(stats.BlockSize)} bytes)");
        ctx.Info(inv($"    Wasted space:          {Sizes.HumanReadable(stats.BlockAlignmentWaste)} ({wastePercent:F2}% of file data blocks)"));
        ctx.Info(inv($"\n  Elapsed time:            {stats.ElapsedSeconds:F2}s"));
        if (stats.TotalFiles > 0)
        {
            double throughput = stats.UncompressedTotalSize / (stats.ElapsedSeconds + 0.001);
            ctx.Info($"  Throughput:              {Sizes.HumanReadable((long)throughput)}/s");
        }

        ctx.Info(new string('=', 70) + "\n");
    }

    /// <summary>Python <c>_resolve_pack_verification_mode</c>.</summary>
    public static PackVerificationMode VerificationMode(PackArgs args)
    {
        if (args.SkipVerification && args.Verify)
        {
            throw new BuildException("--verify and --skip-verification cannot be used together");
        }

        // Python rejects --skip-verification whenever --verify-structure is on, which is the default
        // (oracle finding 1); here only an explicit --verify-structure conflicts.
        if (args.SkipVerification)
        {
            return PackVerificationMode.Skip;
        }

        return args.Verify ? PackVerificationMode.Full : args.VerifyStructure ? PackVerificationMode.Structure : PackVerificationMode.Skip;
    }

    /// <summary>Python <c>_run_post_pack_verify</c>: structure or full check, then warnings and errors.</summary>
    /// <returns>1 when errors were found.</returns>
    public static int PostPackVerify(Output.CliContext ctx, string image, SourceTree source, PackBuildConfig config, PackVerificationMode mode, bool requireGameFiles)
    {
        bool full = mode == PackVerificationMode.Full;
        ctx.Info($"Running post-pack {(full ? "full verification" : "structure verification")}...\n");
        PFSInspection inspection = PFSInspector.Inspect(image, new PFSInspectOptions
        {
            Ekpfs = config.Ekpfs,
            VerifyPayloads = full,
            Checklist = requireGameFiles ? ChecklistMode.Always : ChecklistMode.Never,
            Source = source,
            CompareSourceContents = full,
            Progress = ctx.CreateProgress(),
        });
        if (inspection.UrootInode >= 0 && inspection.Header is not null)
        {
            ReadCommands.PrintCheckReport(ctx, image, inspection, full ? "PFS Full Verify Report" : "PFS Structure Verify Report", hideHeaders: true);
        }

        inspection.Warnings.ForEach(ctx.Warning);
        inspection.Errors.ForEach(e => ctx.Log.Error(e));
        return inspection.Errors.Count > 0 ? 1 : 0;
    }

    /// <summary>Python <c>get_destination_space_error_message</c>.</summary>
    public static string? DestinationSpaceError(long requiredBytes, string outputPath)
    {
        long? free = PackEnvironment.FreeBytes(Path.GetDirectoryName(outputPath) ?? outputPath);
        return free is null || free >= requiredBytes
            ? null
            : FormattableString.Invariant($"ERROR: The destination file is on a disk that does not have enough space ({requiredBytes / (double)(1L << 30):F1} GB) to perform the operation.\nOperation cancelled.");
    }

    /// <summary>
    /// Python <c>prompt_overwrite</c>: ask before replacing an existing image and remove it (and a stale
    /// <c>.tmp</c>) on yes. End of input counts as no.
    /// </summary>
    /// <returns><see langword="true"/> to proceed.</returns>
    public static bool PromptOverwrite(Output.CliContext ctx, string outputPath)
    {
        if (!File.Exists(outputPath) && !Directory.Exists(outputPath))
        {
            return true;
        }

        ctx.Info($"Output file already exists: {outputPath}");
        while (true)
        {
            ctx.Out.Write("Overwrite? [Y/n] ");
            ctx.Out.Flush();
            string? response = ctx.In.ReadLine()?.Trim().ToLowerInvariant();
            if (response is "y" or "yes" or "")
            {
                TryDelete(outputPath);
                TryDelete(outputPath + ".tmp");
                return true;
            }

            if (response is null or "n" or "no")
            {
                return false;
            }

            ctx.Info("Please enter 'y' or 'n'");
        }
    }

    /// <summary>Build timestamp: <c>SOURCE_DATE_EPOCH</c> when set (reproducible builds), else now.</summary>
    public static long Timestamp() =>
        long.TryParse(Environment.GetEnvironmentVariable("SOURCE_DATE_EPOCH"), out long epoch) && epoch >= 0
            ? epoch
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Python suppresses OSError here; the build then fails when it cannot write.
        }
    }
}
