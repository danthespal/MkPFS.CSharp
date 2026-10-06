using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MkPFS.Build.FPKG;
using MkPFS.Build.PFS;
using MkPFS.Cli.Output;
using MkPFS.Core.Compression.Kraken;
using MkPFS.Core.Crypto;
using MkPFS.Core.Metadata;
using MkPFS.Core.Util;

namespace MkPFS.Cli.Commands;

/// <summary><c>pack fpkg</c>: a PS5 fake-signed debug package, as PS5PkgTool builds it (FPKG plan F10).</summary>
internal static class FPKGPackCommand
{
    public static Command Create(CliContext ctx)
    {
        Command command = new("fpkg", "Build a PS5 fake-signed debug package (.pkg) from an application folder");
        Argument<string> source = new("source_dir") { Description = "Application folder (sce_sys/param.json, eboot.bin)" };
        Argument<string?> output = new("output")
        {
            Description = "Package file (default: <content-id>-A<version>-V0100.pkg in the current folder)",
            Arity = ArgumentArity.ZeroOrOne,
        };
        Option<string?> contentId = new("--content-id") { Description = "36-character content id (default: contentId of sce_sys/param.json)" };
        Option<string?> passcode = PKGCommands.PasscodeOption();
        Option<string> compression = new("--compression")
        {
            Description = "auto: Kraken-compress where it pays (default, as PS5PkgTool); fast: the same with a quicker parse (about twice as fast, a few percent larger); stored: no compression",
            DefaultValueFactory = _ => "auto",
        };
        compression.AcceptOnlyFromAmong("auto", "fast", "stored");
        Option<string?> seed = new("--seed") { Description = "Outer PFS seed, 32 hex digits (default: derived from the content id and passcode)" };
        Option<long?> timestamp = new("--timestamp") { Description = "Unix time for every inode (default: SOURCE_DATE_EPOCH or now)" };
        Option<bool> noFakeSign = new("--no-fake-sign") { Description = "Pack plain ELF modules as they are instead of fake-signing them" };
        Option<string?> title = new("--title") { Description = "Title of a generated param.json (when the source has none)" };
        Option<string> appVersion = new("--app-version") { Description = "Master version NN.NN of a generated param.json (default: 01.00)", DefaultValueFactory = _ => "01.00" };
        Option<string> drmType = new("--drm-type") { Description = "applicationDrmType of a generated param.json (default: free)", DefaultValueFactory = _ => "free" };
        drmType.AcceptOnlyFromAmong([.. PS5ParamJson.DrmTypes]);
        Option<bool> dryRun = new("--dry-run") { Description = "Check the source and report what would be packed; write nothing" };
        Option<bool> verify = new("--verify") { Description = "Verify the package and compare it with the source after building" };
        Option<bool> json = new("--json") { Description = "Print the result as JSON" };
        Option<bool> verbose = new("--verbose") { Description = "Log every file's placement and compression and the package layout" };
        Option<string?> tempFolder = new("--temp-folder")
        {
            Description = "Folder for the staged inner image, a temporary file about as large as the game (default: next to the package)",
        };
        Option<int> cpuCount = new("--cpu-count")
        {
            Description = "Logical processors for Kraken compression, 0 to 256 (0 = auto, every logical processor)",
            DefaultValueFactory = _ => 0,
        };
        command.Arguments.Add(source);
        command.Arguments.Add(output);
        foreach (Option option in new Option[] { contentId, passcode, compression, seed, timestamp, noFakeSign, title, appVersion, drmType, cpuCount, tempFolder, dryRun, verify, verbose, json })
        {
            command.Options.Add(option);
        }

        command.SetAction(parse =>
        {
            string sourceDir = Path.GetFullPath(PathRules.ExpandUser(parse.GetValue(source)!));
            if (!Directory.Exists(sourceDir))
            {
                ctx.Error($"source must be an existing directory: {sourceDir}");
                return 1;
            }

            // ---- Content id and output name: from param.json unless given. ----
            string paramPath = Path.Combine(sourceDir, "sce_sys", "param.json");
            byte[]? param = File.Exists(paramPath) ? File.ReadAllBytes(paramPath) : null;
            string? paramContentId = param is null ? null : PS5ParamJson.ReadString(param, "contentId");
            string? id = parse.GetValue(contentId) ?? paramContentId;
            if (!PS5ParamJson.IsContentId(id))
            {
                ctx.Error(id is null
                    ? "no content id: sce_sys/param.json has none; pass --content-id UP0000-PPSA00000_00-XXXXXXXXXXXXXXXX"
                    : $"content id must look like UP0000-PPSA00000_00-XXXXXXXXXXXXXXXX: {id}");
                return 1;
            }

            if (paramContentId is not null && paramContentId != id)
            {
                ctx.Warning($"--content-id {id} differs from param.json contentId {paramContentId}; param.json is packed unchanged");
            }

            string code = parse.GetValue(passcode) ?? new string('0', PS5Keys.PasscodeLength);
            if (code.Length != PS5Keys.PasscodeLength)
            {
                ctx.Error($"--passcode must be {PS5Keys.PasscodeLength} characters");
                return 1;
            }

            byte[]? seedBytes = null;
            if (parse.GetValue(seed) is { } seedHex)
            {
                try
                {
                    seedBytes = Convert.FromHexString(seedHex);
                }
                catch (FormatException)
                {
                }

                if (seedBytes is not { Length: 16 })
                {
                    ctx.Error("--seed must be 32 hex digits");
                    return 1;
                }
            }

            string version = (param is null ? null : PS5ParamJson.ReadString(param, "masterVersion")) ?? parse.GetValue(appVersion)!;
            string outputPath = Path.GetFullPath(PathRules.ExpandUser(parse.GetValue(output)
                ?? Path.Combine(ctx.WorkingDirectory, $"{id}-A{version.Replace(".", string.Empty, StringComparison.Ordinal)}-V0100.pkg")));
            string mode = parse.GetValue(compression)!;
            bool compress = mode != "stored";
            if (parse.GetValue(cpuCount) is < 0 or > 256)
            {
                // Each worker holds its own encoder arrays and batch share; 256 is AMPR's --workers limit too.
                ctx.Error("--cpu-count must be between 0 and 256");
                return 1;
            }

            long time = parse.GetValue(timestamp) ?? PackReport.Timestamp();
            FPKGBuildOptions options = new()
            {
                SourceDir = sourceDir,
                ContentId = id!,
                Passcode = code,
                Time = DateTimeOffset.FromUnixTimeSeconds(time),
                Seed = seedBytes,
                Compress = compress,
                Fast = mode == "fast",
                FakeSign = !parse.GetValue(noFakeSign),
                Title = parse.GetValue(title),
                Version = parse.GetValue(appVersion)!,
                DrmType = parse.GetValue(drmType)!,
                TempFolder = parse.GetValue(tempFolder) is { Length: > 0 } temp ? Path.GetFullPath(PathRules.ExpandUser(temp)) : null,
                Verbose = parse.GetValue(verbose),
                CpuCount = parse.GetValue(cpuCount) is > 0 and int count ? count : PS5KrakenChunk.MaxParallelism,
            };

            return parse.GetValue(dryRun)
                ? DryRun(ctx, options, outputPath)
                : Build(ctx, options, outputPath, parse.GetValue(verify), parse.GetValue(json));
        });
        return command;
    }

    private static int DryRun(CliContext ctx, FPKGBuildOptions options, string outputPath)
    {
        FPKGSource view = Prepare(options);
        Report(ctx, options, outputPath, view.Warnings, view.Modules);
        ctx.Info($"Inner files:   {CliContext.Thousands(view.InnerFiles.Count)} ({Sizes.HumanReadable(view.InnerFiles.Sum(f => f.Size))})");
        ctx.Info($"CNT entries:   {string.Join(", ", view.Entries.Select(e => e.Path))}");
        view.Errors.ToList().ForEach(ctx.Error);
        ctx.Info(view.Errors.Count == 0 ? "Dry run: nothing written." : "Dry run: the source cannot be packaged.");
        return view.Errors.Count == 0 ? 0 : 1;
    }

    private static int Build(CliContext ctx, FPKGBuildOptions options, string outputPath, bool verify, bool json)
    {
        if (!json)
        {
            ctx.VersionHeader();
        }

        // The staged inner image and the package each take about the size of the game.
        long payload = Directory.EnumerateFiles(options.SourceDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        string staging = options.TempFolder ?? Path.GetDirectoryName(outputPath)!;
        long destination = options.TempFolder is null ? 2 * payload : payload;
        if (PackReport.DestinationSpaceError(destination, outputPath) is { } spaceError)
        {
            ctx.Error(spaceError);
            return 1;
        }

        if (options.TempFolder is not null && PackEnvironment.FreeBytes(staging) is { } free && free < payload)
        {
            ctx.Error(FormattableString.Invariant($"The temp folder {staging} has {free / (double)(1L << 30):F1} GB free; the staged inner image needs about {payload / (double)(1L << 30):F1} GB."));
            return 1;
        }

        if (!PackReport.PromptOverwrite(ctx, outputPath))
        {
            ctx.Info("Operation cancelled.");
            return 0;
        }

        if (!json)
        {
            Header(ctx, options, outputPath);
        }

        Stopwatch clock = Stopwatch.StartNew();
        FPKGBuildResult result;
        try
        {
            result = FPKGBuilder.Build(options, outputPath, json ? null : ctx.Info, json ? null : ctx.CreateProgress());
        }
        catch (InvalidDataException ex)
        {
            foreach (string line in ex.Message.Split(Environment.NewLine))
            {
                ctx.Error(line);
            }

            return 1;
        }

        if (json)
        {
            using MemoryStream buffer = new();
            using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("output", outputPath);
                writer.WriteNumber("size", result.Size);
                writer.WriteString("content_id", options.ContentId);
                writer.WriteString("compression", !options.Compress ? "stored" : options.Fast ? "fast" : "auto");
                writer.WriteNumber("seconds", Math.Round(clock.Elapsed.TotalSeconds, 3));
                writer.WriteStartArray("warnings");
                result.Warnings.ToList().ForEach(writer.WriteStringValue);
                writer.WriteEndArray();
                writer.WriteStartArray("modules");
                foreach (FPKGModule module in result.Modules)
                {
                    writer.WriteStartObject();
                    writer.WriteString("path", module.Path);
                    writer.WriteString("source", module.SourceKind.ToString());
                    writer.WriteString("packed", module.PackedKind.ToString());
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            ctx.Out.Write(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
            ctx.Out.Write(ctx.Out.NewLine);
            return 0;
        }

        Findings(ctx, result.Warnings, result.Modules);
        ctx.Info($"Package:       {CliContext.Thousands(result.Size)} bytes in {clock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s");
        return verify ? PKGCommands.Verify(ctx, outputPath, options.Passcode, null, options.SourceDir) : 0;
    }

    private static FPKGSource Prepare(FPKGBuildOptions options) => FPKGSource.Prepare(new FPKGSourceOptions
    {
        SourceDir = options.SourceDir,
        ContentId = options.ContentId,
        Passcode = options.Passcode,
        Title = options.Title,
        Version = options.Version,
        DrmType = options.DrmType,
        FakeSign = options.FakeSign,
    });

    private static void Report(CliContext ctx, FPKGBuildOptions options, string outputPath, IReadOnlyList<string> warnings, IReadOnlyList<FPKGModule> modules)
    {
        Header(ctx, options, outputPath);
        Findings(ctx, warnings, modules);
    }

    private static void Header(CliContext ctx, FPKGBuildOptions options, string outputPath)
    {
        ctx.Info(new string('=', 70));
        ctx.Info("PS5 Debug Package (fake-signed)");
        ctx.Info(new string('=', 70));
        ctx.Info($"Source:        {options.SourceDir}");
        ctx.Info($"Output:        {outputPath}");
        ctx.Info($"Content ID:    {options.ContentId}");
        ctx.Info($"Compression:   {(!options.Compress ? "stored" : options.Fast ? "fast (Kraken where it pays, quicker parse)" : "auto (Kraken where it pays)")}");
        ctx.Info($"CPU cores:     {options.CpuCount}");
        ctx.Info($"Temp folder:   {options.TempFolder ?? Path.GetDirectoryName(outputPath) + " (next to the package)"}");
    }

    private static void Findings(CliContext ctx, IReadOnlyList<string> warnings, IReadOnlyList<FPKGModule> modules)
    {
        foreach (FPKGModule module in modules)
        {
            string line = $"Module:        {module.Path}: {module.SourceKind}{(module.PackedKind != module.SourceKind ? $" -> {module.PackedKind}" : string.Empty)}";
            if (module.Runs)
            {
                ctx.Info(line);
            }
            else
            {
                ctx.Warning(line + " (will not start on a debug-mode console)");
            }
        }

        foreach (string warning in warnings)
        {
            ctx.Warning(warning);
        }
    }
}
