using System.CommandLine;
using System.Globalization;
using MkPFS.Cli.Output;
using MkPFS.Core.Crypto;
using MkPFS.Core.Exfat;
using MkPFS.Core.PFS;
using MkPFS.Core.PFSC;
using MkPFS.Core.Util;

namespace MkPFS.Cli.Commands;

/// <summary>
/// Read-side commands: <c>inspect</c>, <c>tree</c>, <c>unpack</c>, <c>verify</c> (port of Python
/// <c>cli_mkpfs_inspect_run</c>, <c>cli_mkpfs_ls_run</c>, <c>cli_mkpfs_extract_run</c>, <c>cli_mkpfs_check_run</c>).
/// Text output matches Python line for line.
/// </summary>
internal static class ReadCommands
{
    internal const string GameFolderCompressWarning =
        "IMPORTANT: Do not pack an application/game folder directly with compression enabled.\n" +
        "Although image creation and verification may succeed, the console often misreads compressed files.\n" +
        "Either turn off compression (--no-compress) or create the image using the wrapper-based packaging flow.\n" +
        "See: https://github.com/PSBrew/MkPFS/issues/49\n";

    public static Command Inspect(CliContext ctx)
    {
        Argument<string> image = new("image_file") { Description = "Path to input .ffpfs image" };
        Option<string> format = new("--format") { Description = "Output format for inspection report", DefaultValueFactory = _ => "text" };
        format.AcceptOnlyFromAmong("text", "json");
        (Option<string?> key, Option<bool> newCrypt) = KeyOptions();
        Command command = new("inspect", "Inspect image metadata and integrity summary") { image, format, key, newCrypt };
        command.SetAction(parse =>
        {
            if (!TryKey(ctx, parse.GetValue(key), out byte[] ekpfs))
            {
                return 2;
            }

            string path = FullPath(parse.GetValue(image)!);
            PFSInspection inspection = PFSInspector.Inspect(path, new PFSInspectOptions { Ekpfs = ekpfs, NewCrypt = parse.GetValue(newCrypt) });
            if (parse.GetValue(format) == "json")
            {
                ctx.Out.Write(PythonJson.Dump(new Dictionary<string, object?>
                {
                    ["image"] = path,
                    ["has_header"] = inspection.Header is not null,
                    ["version"] = inspection.Header?.Version,
                    ["block_size"] = inspection.Header is null ? null : (long)inspection.Header.BlockSize,
                    ["warnings"] = inspection.Warnings,
                    ["errors"] = inspection.Errors,
                }));
                ctx.Out.Write(ctx.Out.NewLine);
            }
            else
            {
                ctx.VersionHeader();
                ctx.Info(new string('=', 70));
                ctx.Info("PFS Image Inspection");
                ctx.Info(new string('=', 70));
                ctx.Info($"Image:    {path}");
                if (inspection.Header is { } header)
                {
                    ctx.Info($"Version:  {header.Version} ({header.VersionLabel})");
                    ctx.Info($"Magic:    {DescribeMagic(header.Magic)}");
                    ctx.Info($"Block:    {header.BlockSize}");
                }

                ctx.Info($"Warnings: {inspection.Warnings.Count}");
                ctx.Info($"Errors:   {inspection.Errors.Count}");
                inspection.Warnings.ForEach(w => ctx.Warning($"Warning: {w}"));
                inspection.Errors.ForEach(e => ctx.Error($"Error: {e}"));
            }

            return inspection.Errors.Count > 0 ? 1 : 0;
        });
        return command;
    }

    public static Command Tree(CliContext ctx)
    {
        Argument<string> image = new("image_file") { Description = "Path to input folder or image (.ffpfs, .ffpfsc, or .exfat)" };
        Option<bool> deep = new("--deep") { Description = "If the image wraps a single exFAT, list the files inside it" };
        Option<string> format = FormatOption();
        (Option<string?> key, Option<bool> newCrypt) = KeyOptions();
        Command command = new("tree", "Print source folder or image tree representation") { image, deep, format, key, newCrypt };
        command.SetAction(parse =>
        {
            if (!TryKey(ctx, parse.GetValue(key), out byte[] ekpfs))
            {
                return 2;
            }

            string path = FullPath(parse.GetValue(image)!);
            bool useNewCrypt = parse.GetValue(newCrypt);
            if (Directory.Exists(path))
            {
                ctx.VersionHeader();
                ctx.Info("/");
                TreeRenderer.RenderFolder(path).ForEach(ctx.Info);
                return 0;
            }

            if (PFSExtractor.DetectFormat(path, ParseFormat(parse.GetValue(format))) == ImageFormat.Exfat)
            {
                using FileStream stream = File.OpenRead(path);
                List<string> lines = TreeRenderer.RenderExfat(new ExfatReader(stream).RootEntries());
                ctx.VersionHeader();
                ctx.Info("/");
                lines.ForEach(ctx.Info);
                return 0;
            }

            if (parse.GetValue(deep))
            {
                (PFSImage Image, Stream View, string InnerName)? inner = PFSExtractor.OpenInnerExfat(path, ekpfs, useNewCrypt);
                if (inner is { } opened)
                {
                    using (opened.Image)
                    using (opened.View)
                    {
                        List<string> lines = TreeRenderer.RenderExfat(new ExfatReader(opened.View).RootEntries());
                        ctx.VersionHeader();
                        ctx.Info("/");
                        lines.ForEach(ctx.Info);
                        return 0;
                    }
                }

                ctx.Info("No wrapped exFAT detected; showing the PFS image tree.");
            }

            PFSInspection inspection = PFSInspector.Inspect(path, new PFSInspectOptions { Ekpfs = ekpfs, NewCrypt = useNewCrypt, VerifyPayloads = false, Checklist = ChecklistMode.Never });
            if (inspection.Errors.Count > 0)
            {
                inspection.Errors.ForEach(ctx.Error);
                return 1;
            }

            ctx.VersionHeader();
            ctx.Info("/");
            TreeRenderer.RenderPFS(inspection.DirentsByInode, inspection.UrootInode).ForEach(ctx.Info);
            return 0;
        });
        return command;
    }

    public static Command Unpack(CliContext ctx)
    {
        Argument<string> image = new("image_file") { Description = "Path to input image (.ffpfs or .exfat)" };
        Argument<string> output = new("output_dir") { Description = "Destination directory for extraction" };
        Option<bool> overwrite = new("--overwrite") { Description = "Overwrite existing output path" };
        Option<bool> deep = new("--deep") { Description = "If the image wraps a single exFAT inside a PFS, extract the files inside it" };
        Option<string[]> only = new("--only") { Description = "With --deep, extract only this inner exFAT path (file or directory); repeatable", Arity = ArgumentArity.ZeroOrMore };
        (Option<string?> key, Option<bool> newCrypt) = KeyOptions();
        Option<string> format = FormatOption();
        Option<bool> noProgress = new("--no-progress") { Description = "Disable the extraction progress bar on stderr" };
        Command command = new("unpack", "Extract files from image to destination directory") { image, output, overwrite, deep, only, key, newCrypt, format, noProgress };
        command.SetAction(parse =>
        {
            if (!TryKey(ctx, parse.GetValue(key), out byte[] ekpfs))
            {
                return 2;
            }

            string path = FullPath(parse.GetValue(image)!);
            string outputPath = FullPath(parse.GetValue(output)!);
            bool isDeep = parse.GetValue(deep);
            string[] selectors = parse.GetValue(only) ?? [];
            if (selectors.Length > 0 && !isDeep)
            {
                ctx.Info("--only requires --deep (it selects entries inside the wrapped exFAT)");
                return 2;
            }

            if ((File.Exists(outputPath) || Directory.Exists(outputPath)) && !parse.GetValue(overwrite))
            {
                ctx.Info($"output path {outputPath} exists (use --overwrite to force)");
                return 2;
            }

            Core.Diagnostics.IProgressSink? progress = ctx.CreateProgress(!parse.GetValue(noProgress));
            ExtractionResult result;
            if (PFSExtractor.DetectFormat(path, ParseFormat(parse.GetValue(format))) == ImageFormat.Exfat)
            {
                if (isDeep)
                {
                    ctx.Info("--deep has no effect for raw exFAT images; extracting image contents");
                }

                if (selectors.Length > 0)
                {
                    ctx.Info("--only is not supported for raw exFAT images; extracting everything");
                }

                result = PFSExtractor.ExtractExfat(path, outputPath, progress);
            }
            else
            {
                result = PFSExtractor.ExtractPFS(path, outputPath, new PFSExtractOptions
                {
                    Ekpfs = ekpfs,
                    NewCrypt = parse.GetValue(newCrypt),
                    Deep = isDeep,
                    Selectors = selectors,
                    Progress = progress,
                });
            }

            result.Warnings.ForEach(ctx.Info);
            result.Errors.ForEach(ctx.Info);
            if (result.Errors.Count > 0)
            {
                return 1;
            }

            ctx.VersionHeader();
            ctx.Info("Extraction complete:");
            ctx.Info($"  Output:       {result.OutputPath}");
            ctx.Info($"  Files written: {result.FilesWritten}");
            ctx.Info($"  Dirs created:  {result.DirectoriesCreated}");
            ctx.Info($"  Bytes written: {result.BytesWritten}");
            return 0;
        });
        return command;
    }

    public static Command Verify(CliContext ctx)
    {
        Argument<string> image = new("image_file") { Description = "Path to input image (.ffpfs or .exfat)" };
        Option<string?> sourceDir = new("--source-dir") { Description = "Optional source folder for hierarchy and payload comparison" };
        Option<string?> sourceFile = new("--source-file") { Description = "Optional source file for single-file image comparison" };
        Option<string?> expectCrc = new("--expect-crc32") { Description = "Expected cumulative payload CRC32 (hex)" };
        Option<string?> expectManifest = new("--expect-manifest-sha256") { Description = "Expected manifest SHA256 digest (64 hex)" };
        (Option<string?> key, Option<bool> newCrypt) = KeyOptions();
        Option<string> format = FormatOption();
        Option<bool> requireGameFiles = new("--require-game-files") { Description = "Enable the PS5 game-file checklist (warn on missing sce_sys/param.json, eboot.bin, pfs-version.dat)" };
        Command command = new("verify", "Validate image structure and payload checksums") { image, sourceDir, sourceFile, expectCrc, expectManifest, key, newCrypt, format, requireGameFiles };
        command.SetAction(parse =>
        {
            string path = FullPath(parse.GetValue(image)!);
            string? dirArg = parse.GetValue(sourceDir);
            string? fileArg = parse.GetValue(sourceFile);
            if (dirArg is not null && fileArg is not null)
            {
                ctx.Info("--source-dir and --source-file cannot be used together");
                return 2;
            }

            if (PFSExtractor.DetectFormat(path, ParseFormat(parse.GetValue(format))) == ImageFormat.Exfat)
            {
                if (fileArg is not null)
                {
                    ctx.Info("--source-file is not supported for exFAT verify; use --source-dir");
                    return 2;
                }

                (List<string> errors, List<string> warnings) = PFSExtractor.VerifyExfat(path, dirArg is null ? null : FullPath(dirArg));
                warnings.ForEach(ctx.Warning);
                errors.ForEach(ctx.Error);
                return errors.Count > 0 ? 1 : 0;
            }

            SourceTree? source = null;
            if (dirArg is not null)
            {
                source = SourceTree.FromDirectory(FullPath(dirArg));
            }
            else if (fileArg is not null)
            {
                string file = FullPath(fileArg);
                if (!File.Exists(file))
                {
                    ctx.Info($"--source-file must be an existing file: {file}");
                    return 2;
                }

                string external = Path.GetFileName(file);
                string inner = SingleFileName.Resolve(external);
                if (inner != external)
                {
                    ctx.Warning($"WARNING: The external file name does not match the internal file name. Comparing {external} with {inner} as the same file.");
                }

                source = SourceTree.FromSingleFile(file, inner);
            }

            if (!TryParseCrc(ctx, parse.GetValue(expectCrc), out uint? crc) ||
                !TryParseManifest(ctx, parse.GetValue(expectManifest), out string? manifest) ||
                !TryKey(ctx, parse.GetValue(key), out byte[] ekpfs))
            {
                return 2;
            }

            PFSInspection inspection = PFSInspector.Inspect(path, new PFSInspectOptions
            {
                Ekpfs = ekpfs,
                NewCrypt = parse.GetValue(newCrypt),
                Checklist = parse.GetValue(requireGameFiles) ? ChecklistMode.Always : ChecklistMode.Never,
                Source = source,
                ExpectedCrc32 = crc,
                ExpectedManifestSha256 = manifest,
                Progress = ctx.CreateProgress(),
            });
            if (inspection.UrootInode >= 0 && inspection.Header is not null)
            {
                PrintCheckReport(ctx, path, inspection);
            }

            inspection.Warnings.ForEach(ctx.Warning);
            inspection.Errors.ForEach(ctx.Error);
            return inspection.Errors.Count > 0 ? 1 : 0;
        });
        return command;
    }

    /// <summary>The <c>verify</c> report (Python <c>run_image_check</c> with <c>emit_report</c>).</summary>
    internal static void PrintCheckReport(CliContext ctx, string path, PFSInspection inspection, string title = "PFS Check Report", bool hideHeaders = false)
    {
        PFSHeader header = inspection.Header!;
        bool gameMarkers = inspection.FileInodes.ContainsKey("eboot.bin") || inspection.FileInodes.ContainsKey("sce_sys/param.json");
        if (!hideHeaders && inspection.CompressedFiles > 0 && gameMarkers)
        {
            ctx.Info(string.Empty);
            ctx.Warning(GameFolderCompressWarning);
        }

        long imageSize = File.Exists(path) ? new FileInfo(path).Length : 0;
        string yes(bool value) => value ? "yes" : "no";
        if (!hideHeaders)
        {
            ctx.VersionHeader();
        }

        ctx.Info(new string('=', 70));
        ctx.Info(title);
        ctx.Info(new string('=', 70));
        ctx.Info($"Image:                 {path}");
        ctx.Info($"Version:               {header.Version} ({header.VersionLabel})");
        ctx.Info($"Header magic:          {DescribeMagic(header.Magic)}");
        ctx.Info($"Compression Setup:     {(inspection.CompressedFiles > 0 ? DescribeMagic(Core.PFS.PFSConstants.PFSCMagic) : "none")}");
        ctx.Info($"Read-only:             {yes(header.ReadOnly != 0)}");
        ctx.Info($"Mode:                  0x{header.Mode:X4}  (Bit 0=signed, Bit 1=64-bit inodes, Bit 2=encrypted, Bit 3=case insensitive)");
        ctx.Info($"  Signed:              {yes(header.IsSigned)}");
        ctx.Info($"  64-bit inodes:       {yes(header.Is64BitInodes)}");
        ctx.Info($"  Encrypted:           {yes(header.IsEncrypted)}");
        ctx.Info($"  Case insensitive:    {yes(header.IsCaseInsensitive)}");
        ctx.Info($"Block size:            {header.BlockSize / 1024} KiB ({CliContext.Thousands(header.BlockSize)} bytes)");
        ctx.Info($"Inodes:                {CliContext.Thousands(inspection.Inodes.Count)}");
        ctx.Info($"Directories:           {CliContext.Thousands(inspection.DirInodes.Count)}");
        ctx.Info($"Files:                 {CliContext.Thousands(inspection.FileInodes.Count)}");
        ctx.Info($"Compressed files:      {CliContext.Thousands(inspection.CompressedFiles)}");
        ctx.Info($"Files hash-checked:    {CliContext.Thousands(inspection.CheckedFiles)}");
        ctx.Info($"Data CRC32:            0x{inspection.DataCrc32:X8}");
        ctx.Info($"Manifest SHA256:       {inspection.ManifestSha256}");
        ctx.Info($"Logical file bytes:    {Sizes.HumanReadable(inspection.LogicalFileBytes)} ({CliContext.Thousands(inspection.LogicalFileBytes)} bytes)");
        ctx.Info($"Stored file bytes:     {Sizes.HumanReadable(inspection.StoredFileBytes)} ({CliContext.Thousands(inspection.StoredFileBytes)} bytes)");
        ctx.Info($"Final image size:      {Sizes.HumanReadable(imageSize)} ({CliContext.Thousands(imageSize)} bytes)");
        ctx.Info($"flat_path_table keys:  {CliContext.Thousands(inspection.FptMap.Count)}");
        ctx.Info($"Warnings:              {inspection.Warnings.Count}");
        ctx.Info($"Errors:                {inspection.Errors.Count}");
        ctx.Info(new string('=', 70));
    }

    /// <summary>Python <c>describe_magic</c>.</summary>
    internal static string DescribeMagic(long magic)
    {
        if (magic == Core.PFS.PFSConstants.PFSMagic)
        {
            return $"PFS ({magic})";
        }

        if (magic == Core.PFS.PFSConstants.PFSCMagic)
        {
            return $"PFSC (0x{magic:X8})";
        }

        // Python f"0x{magic:016X}" puts the sign inside the zero padding for negative values.
        return magic < 0
            ? "0x-" + ((ulong)(-(magic + 1)) + 1).ToString("X15", CultureInfo.InvariantCulture)
            : $"0x{magic:X16}";
    }

    private static (Option<string?> Key, Option<bool> NewCrypt) KeyOptions() =>
        (new Option<string?>("--ekpfs-key") { Description = "Optional 64-hex EKPFS key for encrypted images" },
         new Option<bool>("--new-crypt") { Description = "Use alternate newCrypt EKPFS derivation" });

    private static Option<string> FormatOption()
    {
        Option<string> format = new("--format") { Description = "Image format (default: auto-detect)", DefaultValueFactory = _ => "auto" };
        format.AcceptOnlyFromAmong("auto", "pfs", "exfat");
        return format;
    }

    private static ImageFormat ParseFormat(string? value) => value switch
    {
        "pfs" => ImageFormat.PFS,
        "exfat" => ImageFormat.Exfat,
        _ => ImageFormat.Auto,
    };

    private static string FullPath(string path) => Path.GetFullPath(PathRules.ExpandUser(path));

    private static bool TryKey(CliContext ctx, string? hex, out byte[] key)
    {
        try
        {
            key = PFSKeys.ParseEkpfsHex(hex);
            return true;
        }
        catch (FormatException ex)
        {
            ctx.Error(ex.Message);
            key = [];
            return false;
        }
    }

    private static bool TryParseCrc(CliContext ctx, string? text, out uint? crc)
    {
        crc = null;
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        string value = text.Trim().ToLowerInvariant();
        if (value.StartsWith("0x", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        if (value.Length is 0 or > 8)
        {
            ctx.Info("--expected-crc32 must be a 32-bit hex value");
            return false;
        }

        if (!uint.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint parsed))
        {
            ctx.Info("--expected-crc32 must be hex (example: 7F528D1F or 0x7F528D1F)");
            return false;
        }

        crc = parsed;
        return true;
    }

    private static bool TryParseManifest(CliContext ctx, string? text, out string? manifest)
    {
        manifest = null;
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        string digest = text.Trim().ToLowerInvariant();
        if (digest.Length != 64 || digest.Any(c => !char.IsAsciiHexDigitLower(c) && !char.IsAsciiDigit(c)))
        {
            ctx.Info("--expected-manifest-sha256 must be a 64-hex SHA256 digest");
            return false;
        }

        manifest = digest;
        return true;
    }
}
