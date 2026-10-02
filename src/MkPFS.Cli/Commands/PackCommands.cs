using System.CommandLine;
using System.Globalization;
using MkPFS.Build.Exfat;
using MkPFS.Cli.Output;
using MkPFS.Core.Diagnostics;
using MkPFS.Core.Util;

namespace MkPFS.Cli.Commands;

/// <summary><c>pack</c> subcommands (port of Python <c>cli_mkpfs_pack_*_run</c>). <c>folder</c> and <c>file</c> arrive in Phase 6.</summary>
internal static class PackCommands
{
    public static Command Pack(CliContext ctx)
    {
        Command pack = new("pack", "Pack a folder or file into an image");
        pack.Subcommands.Add(Exfat(ctx));
        return pack;
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
        Option<bool> overwrite = new("--overwrite") { Description = "Overwrite an existing output file" };
        Option<bool> verbose = new("--verbose") { Description = "Verbose output" };
        Option<bool> noProgress = new("--no-progress") { Description = "Disable the exFAT packing progress bar on stderr" };
        Command command = new("exfat", "Build a raw exFAT image from a source directory")
        {
            sourceDir, output, clusterSize, overwrite, verbose, noProgress,
        };
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

            if ((File.Exists(target) || Directory.Exists(target)) && !parse.GetValue(overwrite))
            {
                ctx.Log.Error($"output already exists (use --overwrite): {target}");
                return 1;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            ctx.VersionHeader();
            ctx.Info($"Building exFAT image from {source}");
            ctx.Info($"  Output: {target}");
            IProgressSink? progress = ctx.CreateProgress(!parse.GetValue(noProgress));
            string written = ExfatImageWriter.Write(source, target, cluster, progress);
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
