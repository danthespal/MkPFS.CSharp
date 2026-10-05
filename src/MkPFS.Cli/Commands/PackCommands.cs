using System.CommandLine;
using System.Globalization;
using MkPFS.Build;
using MkPFS.Build.Exfat;
using MkPFS.Build.PFS;
using MkPFS.Cli.Output;
using MkPFS.Core.Diagnostics;
using MkPFS.Core.PFS;
using MkPFS.Core.PFSC;
using MkPFS.Core.Util;

namespace MkPFS.Cli.Commands;

/// <summary><c>pack</c> subcommands (port of Python <c>cli_mkpfs_pack_*_run</c>). <c>folder</c> and <c>file</c> arrive in Phase 6.</summary>
internal static class PackCommands
{
    public static Command Pack(CliContext ctx)
    {
        Command pack = new("pack", "Pack a folder or file into an image");
        pack.Subcommands.Add(FolderCommand(ctx));
        pack.Subcommands.Add(FileCommand(ctx));
        pack.Subcommands.Add(Exfat(ctx));
        return pack;
    }

    // Python cli_mkpfs_create_run + _run_exfat_pack.
    private static Command FolderCommand(CliContext ctx)
    {
        Command command = new("folder", "Build image from a source directory");
        PackOptions options = new(command, "source_dir", "Source app or homebrew folder", includeRequireGameFiles: true);
        Option<bool> raw = new("--raw") { Description = "Pack the folder directly into a PFS image instead of the default exFAT-wrapped .ffpfsc" };
        command.Options.Add(raw);
        AmprCliOptions ampr = new(command);
        command.SetAction(parse =>
        {
            PackArgs args = options.Read(parse);
            try
            {
                string source = FullPath(args.Source);

                // AMPR Emu libraries and index go into the source tree first so the image includes them.
                if (!args.DryRun)
                {
                    AmprLibs.Prepare(source, ampr.Read(parse), ctx.Log);
                }

                return parse.GetValue(raw) ? PackFolderRaw(ctx, args, source) : PackFolderWrapped(ctx, args, source);
            }
            catch (BuildException ex)
            {
                ctx.Log.Error(ex.Message);
                return 1;
            }
        });
        return command;
    }

    private static int PackFolderWrapped(CliContext ctx, PackArgs args, string source)
    {
        (string output, bool changed) = PathRules.NormalizeOutputPath(args.ImageFile, ".ffpfsc", args.AdjustOutputFileExtension);
        output = FullPath(output);
        if (changed)
        {
            ctx.Info("exFAT wrapping mode enabled, adjusting output file extension to .ffpfsc");
        }

        if (args.Signed)
        {
            throw new BuildException("--exfat wrapping does not support --signed images");
        }

        PackBuildConfig config = PackBuildConfig.Resolve(args, 65536);
        WarnBackend(ctx, args);
        string tempFolder = PathRules.ResolveTempRoot(args.TempFolder);
        PackReport.Parameters(ctx, config, source, output, tempFolder, signed: false, requireGameFiles: false, args.DryRun);
        if (args.DryRun)
        {
            if (!Directory.Exists(source))
            {
                throw new BuildException($"source must be an existing directory: {source}");
            }

            long size = ExfatImageWriter.Plan(source).ImageSize;
            ctx.Info($"Dry run: would wrap {source} into a {Sizes.HumanReadable(size)} exFAT and compress it to {output}; nothing written.");
            return 0;
        }

        if (!PackReport.PromptOverwrite(ctx, output))
        {
            ctx.Info("Operation cancelled.");
            return 0;
        }

        BuildStats stats = ExfatWrappedImageBuilder.Build(new SingleFileBuildOptions
        {
            SourceFile = source,
            OutputPath = output,
            BlockSize = config.BlockSize,
            PFSVersion = config.PFSVersion,
            CaseInsensitive = config.CaseInsensitive,
            ZlibLevel = config.ZlibLevel,
            ThresholdGain = config.ThresholdGain,
            CpuCount = PFSCEncoder.ResolveWorkerCount(config.CpuCount),
            Encrypted = config.Encrypted,
            Ekpfs = config.Ekpfs,
            Verbose = args.Verbose,
            Timestamp = PackReport.Timestamp(),
        }, ctx.Log, ctx.CreateProgress());
        stats.InputPath = source;
        PackReport.Summary(ctx, stats);
        if (!args.Verify)
        {
            return 0;
        }

        // Python runs a plain check here (full payload pass, report with banner, warnings without icon).
        ctx.Info("Running post-create check...");
        PFSInspection inspection = PFSInspector.Inspect(output, new PFSInspectOptions
        {
            Ekpfs = config.Ekpfs,
            Checklist = ChecklistMode.Never,
            Progress = ctx.CreateProgress(),
        });
        if (inspection.UrootInode >= 0 && inspection.Header is not null)
        {
            ReadCommands.PrintCheckReport(ctx, output, inspection);
        }

        inspection.Warnings.ForEach(w => ctx.Log.Warning(w));
        inspection.Errors.ForEach(e => ctx.Log.Error(e));
        return inspection.Errors.Count > 0 ? 1 : 0;
    }

    private static readonly string[] AutoFitNames = ["auto-fit", "auto_small_files", "auto-small-files"];

    // Python cli_mkpfs_pack_file_run + _run_stream_pack_file.
    private static Command FileCommand(CliContext ctx)
    {
        Command command = new("file", "Build image from a single source file");
        PackOptions options = new(command, "source_file", "Single source file to pack", includeRequireGameFiles: false);
        Option<bool> useSpool = new("--use-spool") { Description = "Force the legacy staged/spool builder for single-file packing instead of the default direct-to-image streaming" };
        Option<bool> rename = new("--rename-inner-image") { Description = "Rename inner image filename to a safe normalized name (default)" };
        Option<bool> noRename = new("--no-rename-inner-image") { Description = "Disable renaming of the inner image filename" };
        command.Options.Add(useSpool);
        command.Options.Add(rename);
        command.Options.Add(noRename);
        command.SetAction(parse =>
        {
            PackArgs args = options.Read(parse);
            bool renameInner = !parse.GetValue(noRename);
            try
            {
                return PackFile(ctx, args, parse.GetValue(useSpool), renameInner);
            }
            catch (BuildException ex)
            {
                ctx.Log.Error(ex.Message);
                return 1;
            }
        });
        return command;
    }

    private static int PackFile(CliContext ctx, PackArgs args, bool useSpool, bool renameInner)
    {
        string sourceFile = FullPath(args.Source);
        if (!System.IO.File.Exists(sourceFile))
        {
            throw new BuildException($"--source-file must be an existing file: {sourceFile}");
        }

        string externalName = Path.GetFileName(sourceFile);
        string innerName = SingleFileName.Resolve(externalName, renameInner);
        string? spoolReason = args.Signed ? "signed images"
            : args.InodeBits != 32 ? "64-bit inodes"
            : AutoFitNames.Contains(args.BlockSize.Trim().ToLowerInvariant()) ? "auto-fit block size"
            : null;
        if (useSpool || spoolReason is not null)
        {
            // Python stages the file in a temp folder and runs the folder builder; its single-file mode is equivalent.
            if (renameInner && innerName != externalName && spoolReason is not null)
            {
                RenameWarning(ctx, externalName, innerName);
            }

            if (!useSpool)
            {
                ctx.Info($"Direct-to-image streaming unavailable ({spoolReason}); using the spool builder.");
            }

            return PackRaw(ctx, args, new RawSource(sourceFile, innerName), sourceFile, requireGameFiles: false, ".ffpfsc",
                "Single file compression mode enabled, adjusting output file extension to match the container mode .ffpfsc");
        }

        (string output, bool changed) = PathRules.NormalizeOutputPath(args.ImageFile, ".ffpfsc", args.AdjustOutputFileExtension);
        output = FullPath(output);
        if (changed)
        {
            ctx.Info("Single file streaming mode enabled, adjusting output file extension to .ffpfsc");
        }

        PackReport.EnsureOutputIsNotSource(sourceFile, output);

        string blockArg = args.BlockSize.Trim().ToLowerInvariant();
        int blockSize = blockArg is "auto" or "" ? 65536
            : int.TryParse(args.BlockSize, System.Globalization.NumberStyles.AllowLeadingWhite | System.Globalization.NumberStyles.AllowTrailingWhite | System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : throw new BuildException("--block-size must be an integer value or 'auto' for streaming single-file packing");
        PackBuildConfig config = PackBuildConfig.Resolve(args, blockSize);
        WarnBackend(ctx, args);
        string tempFolder = PathRules.ResolveTempRoot(args.TempFolder);
        if (renameInner && innerName != externalName)
        {
            RenameWarning(ctx, externalName, innerName);
        }

        PackReport.Parameters(ctx, config, sourceFile, output, tempFolder, signed: false, requireGameFiles: false, args.DryRun);
        if (!args.DryRun && PackReport.DestinationSpaceError(new FileInfo(sourceFile).Length, output) is { } spaceError)
        {
            ctx.Error(spaceError);
            return 1;
        }

        if (!args.DryRun && !PackReport.PromptOverwrite(ctx, output))
        {
            ctx.Info("Operation cancelled.");
            return 0;
        }

        BuildStats stats = SingleFileImageBuilder.Build(new SingleFileBuildOptions
        {
            SourceFile = sourceFile,
            OutputPath = output,
            InnerFileName = innerName,
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
            Ekpfs = config.Ekpfs,
            DryRun = args.DryRun,
            Verbose = args.Verbose,
            Timestamp = PackReport.Timestamp(),
        }, ctx.Log, ctx.CreateProgress());
        stats.InputPath = sourceFile;
        PackReport.Summary(ctx, stats);
        PackVerificationMode mode = PackReport.VerificationMode(args);
        if (args.DryRun || mode == PackVerificationMode.Skip)
        {
            return 0;
        }

        int rc = PackReport.PostPackVerify(ctx, output, SourceTree.FromSingleFile(sourceFile, innerName), config, mode, requireGameFiles: false);
        if (rc == 0)
        {
            ctx.Log.Info("Image created successfully!", LogIcon.Success);
        }

        return rc;
    }

    private static void RenameWarning(CliContext ctx, string externalName, string innerName) =>
        ctx.Warning($"WARNING: The inner file was renamed to a safer file name to improve compatibility.\r\n\"{externalName}\" -> \"{innerName}\"");

    /// <summary>A folder, or one file packed under an inner name (Python's single-file staging folder).</summary>
    private sealed record RawSource(string Path, string? SingleFileName = null)
    {
        public bool Exists => SingleFileName is null ? Directory.Exists(Path) : System.IO.File.Exists(Path);

        public bool HasGameMarkers => SingleFileName is null
            ? System.IO.File.Exists(System.IO.Path.Combine(Path, "eboot.bin")) || System.IO.File.Exists(System.IO.Path.Combine(Path, "sce_sys", "param.json"))
            : SingleFileName == "eboot.bin";

        // Python sums Path.rglob("*"): every regular file, ignored names included.
        public IReadOnlyList<long> FileSizes() => SingleFileName is not null
            ? [new FileInfo(Path).Length]
            : Directory.Exists(Path)
                ? [.. Directory.EnumerateFiles(Path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true }).Select(f => new FileInfo(f).Length)]
                : [];

        public SourceTree Tree() => SingleFileName is null ? SourceTree.FromDirectory(Path) : SourceTree.FromSingleFile(Path, SingleFileName);
    }

    // Python cli_mkpfs_create_run, --raw branch.
    private static int PackFolderRaw(CliContext ctx, PackArgs args, string source)
    {
        bool hasTitle = GameParams.TitleIdFromSource(source) is not null;
        return PackRaw(ctx, args, new RawSource(source), source, args.RequireGameFiles, hasTitle ? ".ffpfs" : ".ffpfsc", hasTitle
            ? "Raw game files detected inside the source folder, adjusting output file extension to .ffpfs"
            : "The folder does not seem to contain any direct game information, adjusting output file extension to .ffpfsc");
    }

    // Python _run_pack_build.
    private static int PackRaw(CliContext ctx, PackArgs args, RawSource source, string displaySource, bool requireGameFiles, string desiredSuffix, string adjustmentMessage)
    {
        (string output, bool changed) = PathRules.NormalizeOutputPath(args.ImageFile, desiredSuffix, args.AdjustOutputFileExtension);
        output = FullPath(output);
        if (changed)
        {
            ctx.Info(adjustmentMessage);
        }

        PackReport.EnsureOutputIsNotSource(displaySource, output);

        string blockArg = args.BlockSize.Trim().ToLowerInvariant();
        int blockSize;
        if (blockArg == "auto")
        {
            blockSize = 65536;
        }
        else if (AutoFitNames.Contains(blockArg))
        {
            IReadOnlyList<long> sizes = source.FileSizes();
            blockSize = RawFolderImageBuilder.ChooseAutoFitBlockSize([.. sizes]);
            long saved = Math.Max(0, RawFolderImageBuilder.FileDataFootprint(sizes, 65536) - RawFolderImageBuilder.FileDataFootprint(sizes, blockSize));
            ctx.Info($"Auto-fit block size selected: {CliContext.Thousands(blockSize)} bytes ({blockSize / 1024} KiB), estimated file-data saving vs 64 KiB: {Sizes.HumanReadable(saved)}");
        }
        else
        {
            blockSize = int.TryParse(args.BlockSize, System.Globalization.NumberStyles.AllowLeadingWhite | System.Globalization.NumberStyles.AllowTrailingWhite | System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : throw new BuildException("--block-size must be an integer value, 'auto', or 'auto-fit'");
        }

        PackBuildConfig config = PackBuildConfig.Resolve(args, blockSize);
        WarnBackend(ctx, args);
        ValidateInput(source, requireGameFiles);
        string tempFolder = PathRules.ResolveTempRoot(args.TempFolder);
        PackReport.Parameters(ctx, config, displaySource, output, tempFolder, args.Signed, requireGameFiles, args.DryRun);
        if (config.Compress && source.HasGameMarkers)
        {
            ctx.Info(string.Empty);
            ctx.Warning(ReadCommands.GameFolderCompressWarning);
        }

        if (!args.DryRun)
        {
            if (PackReport.DestinationSpaceError(source.FileSizes().Sum(), output) is { } spaceError)
            {
                ctx.Error(spaceError);
                return 1;
            }

            if (config.Compress && TempSpaceError(source, tempFolder) is { } tempError)
            {
                ctx.Error(tempError);
                return 1;
            }

            CleanupTempArtifacts(output, tempFolder);
            if (!PackReport.PromptOverwrite(ctx, output))
            {
                ctx.Info("Operation cancelled.");
                return 0;
            }
        }

        BuildStats stats = RawFolderImageBuilder.Build(new RawFolderBuildOptions
        {
            SourceRoot = source.Path,
            SingleFileName = source.SingleFileName,
            OutputPath = output,
            BlockSize = config.BlockSize,
            PFSVersion = config.PFSVersion,
            InodeBits = config.InodeBits,
            CaseInsensitive = config.CaseInsensitive,
            Signed = args.Signed,
            Compress = config.Compress,
            ThresholdGain = config.ThresholdGain,
            CpuCount = PFSCEncoder.ResolveWorkerCount(config.CpuCount),
            ZlibLevel = config.ZlibLevel,
            DryRun = args.DryRun,
            Verbose = args.Verbose,
            Encrypted = config.Encrypted,
            Ekpfs = config.Ekpfs,
            SkipExecutableCompression = config.SkipExecutableCompression,
            MinFileGain = config.MinFileGain,
            MinCompressSize = config.MinCompressSize,
            TempFolder = tempFolder,
            Timestamp = PackReport.Timestamp(),
        }, ctx.Log, ctx.CreateProgress());
        stats.InputPath = displaySource;
        PackReport.Summary(ctx, stats);
        PackVerificationMode mode = PackReport.VerificationMode(args);
        if (args.DryRun || mode == PackVerificationMode.Skip)
        {
            return 0;
        }

        int rc = PackReport.PostPackVerify(ctx, output, source.Tree(), config, mode, requireGameFiles);
        if (rc == 0)
        {
            ctx.Info(string.Empty);
            ctx.Info(new string('=', 70));
            ctx.Log.Info("Image created successfully!", LogIcon.Success);
            ctx.Info(new string('=', 70));
        }

        return rc;
    }

    // Python validate_input.
    private static void ValidateInput(RawSource source, bool requireGameFiles)
    {
        if (!source.Exists)
        {
            throw new BuildException($"--path must be an existing directory: {source.Path}");
        }

        if (!requireGameFiles || source.SingleFileName is not null)
        {
            return;
        }

        string param = Path.Combine(source.Path, "sce_sys", "param.json");
        if (!System.IO.File.Exists(param))
        {
            throw new BuildException($"Missing required file: {param}");
        }

        if (GameParams.TitleIdFromSource(source.Path) is null)
        {
            throw new BuildException("param.json is missing a valid titleId/title_id");
        }

        string eboot = Path.Combine(source.Path, "eboot.bin");
        if (!System.IO.File.Exists(eboot))
        {
            throw new BuildException($"Missing required file: {eboot}");
        }
    }

    // Python get_temp_space_error_message.
    private static string? TempSpaceError(RawSource source, string tempFolder)
    {
        long required = source.FileSizes().Sum(RawFolderImageBuilder.EstimateSpoolSize);
        long? free = PackEnvironment.FreeBytes(tempFolder);
        return free is null || free >= required
            ? null
            : FormattableString.Invariant($"ERROR: The temp folder does not have enough free space for PFSC compression spool files (requires up to {required / (double)(1L << 30):F1} GB, has {free.Value / (double)(1L << 30):F1} GB).\nUse --temp-folder on a filesystem with more free space or free space in the current temp folder.\nOperation cancelled.");
    }

    // Python cleanup_pack_temp_artifacts: a stale <image>.tmp and spool files older than five minutes.
    private static void CleanupTempArtifacts(string output, string tempFolder)
    {
        TryDelete(output + ".tmp");
        DateTime cutoff = DateTime.UtcNow.AddSeconds(-300);
        foreach (string spool in Directory.EnumerateFiles(tempFolder, "mkpfs-*.pfsc"))
        {
            if (System.IO.File.GetLastWriteTimeUtc(spool) <= cutoff)
            {
                TryDelete(spool);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            System.IO.File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Python suppresses OSError here.
        }
    }

    // Python honours --compression-backend; this port always uses zlib 1.3.1 (ISA-L output misdecodes on the PS5).
    private static void WarnBackend(CliContext ctx, PackArgs args)
    {
        if (args.CompressionBackend is not ("auto" or "zlib"))
        {
            ctx.Warning($"--compression-backend {args.CompressionBackend} is not supported; using zlib 1.3.1");
        }
    }

    private static Command Exfat(CliContext ctx)
    {
        Argument<string> sourceDir = new("source_dir") { Description = "Source app or homebrew folder" };
        Argument<string?> output = new("output")
        {
            Description = "Output .exfat path, or a directory to auto-name <titleId>.exfat (default: alongside the source)",
            Arity = ArgumentArity.ZeroOrOne,
        };
        Option<string> clusterSize = new("--cluster-size")
        {
            Description = "exFAT cluster size in bytes or 'auto' (default: 65536 - SMP/LVD-optimal 64 KiB)",
            DefaultValueFactory = _ => "auto",
        };
        Option<string> freeSpace = new("--free-space")
        {
            Description = "free space to leave inside the image, e.g. 2GiB (default: 0, a tight image); needed by anything " +
                "that writes to the mounted image, like AMPR Emu's debug log and traces on a read-write mount",
            DefaultValueFactory = _ => "0",
        };
        Option<bool> overwrite = new("--overwrite") { Description = "Overwrite an existing output file" };
        Option<bool> verbose = new("--verbose") { Description = "Verbose output" };
        Option<bool> noProgress = new("--no-progress") { Description = "Disable the exFAT packing progress bar on stderr" };
        Command command = new("exfat", "Build a raw exFAT image from a source directory")
        {
            sourceDir, output, clusterSize, freeSpace, overwrite, verbose, noProgress,
        };
        AmprCliOptions ampr = new(command);
        command.SetAction(parse =>
        {
            string source = FullPath(parse.GetValue(sourceDir)!);
            if (!Directory.Exists(source))
            {
                ctx.Log.Error($"source must be an existing directory: {source}");
                return 1;
            }

            if (!TryParseClusterSize(parse.GetValue(clusterSize)!, out int? cluster, out string? clusterError))
            {
                ctx.Log.Error(clusterError!);
                return 1;
            }

            long freeBytes;
            try
            {
                freeBytes = Core.AMPR.AMPRSize.Parse(parse.GetValue(freeSpace)!);
            }
            catch (ArgumentException ex)
            {
                ctx.Log.Error($"--free-space: {ex.Message}");
                return 1;
            }

            // Resolve the final output path first so overwrite can be checked and the path reported.
            string basename = GameParams.DefaultImageBasename(source) + ".exfat";
            string target;
            if (parse.GetValue(output) is { } requestedArg)
            {
                string requested = FullPath(requestedArg);
                target = Directory.Exists(requested) ? Path.Combine(requested, basename) : requested;
            }
            else
            {
                target = Path.Combine(Path.GetDirectoryName(source) ?? source, basename);
            }

            if ((System.IO.File.Exists(target) || Directory.Exists(target)) && !parse.GetValue(overwrite))
            {
                ctx.Log.Error($"output already exists (use --overwrite): {target}");
                return 1;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            ctx.VersionHeader();
            ctx.Info($"Building exFAT image from {source}");
            ctx.Info($"  Output: {target}");

            // Python pack exfat never prepares AMPR Emu (oracle finding 18); the port does, like pack folder.
            try
            {
                AmprLibs.Prepare(source, ampr.Read(parse), ctx.Log);
            }
            catch (BuildException ex)
            {
                ctx.Log.Error(ex.Message);
                return 1;
            }

            IProgressSink? progress = ctx.CreateProgress(!parse.GetValue(noProgress));
            string written = ExfatImageWriter.Write(source, target, cluster, progress, freeBytes);
            ctx.Info($"Successfully wrote {Sizes.HumanReadable(new FileInfo(written).Length)} exFAT image: {written}");
            return 0;
        });
        return command;
    }

    // Python: "auto" or empty means default; otherwise int() and a power of two in 512..32 MiB.
    internal static bool TryParseClusterSize(string text, out int? clusterSize, out string? error)
    {
        clusterSize = null;
        error = null;
        string trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!long.TryParse(trimmed.Replace("_", string.Empty, StringComparison.Ordinal), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value) ||
            trimmed.StartsWith('_') || trimmed.EndsWith('_') || trimmed.Contains("__", StringComparison.Ordinal))
        {
            error = "--cluster-size must be an integer or 'auto'";
            return false;
        }

        if (!Sizes.IsPowerOfTwo(value) || value < 512 || value > 32 * 1024 * 1024)
        {
            error = "--cluster-size must be a power of two between 512 and 33554432";
            return false;
        }

        clusterSize = (int)value;
        return true;
    }

    private static string FullPath(string path) => Path.GetFullPath(PathRules.ExpandUser(path));
}
