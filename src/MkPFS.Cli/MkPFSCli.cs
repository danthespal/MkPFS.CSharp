using System.CommandLine;
using System.Reflection;
using MkPFS.Cli.Output;
using MkPFS.Core.Compression;
using MkPFS.Core.Diagnostics;
using MkPFS.Core.PFSC;

namespace MkPFS.Cli;

/// <summary>Command-line wiring for <c>mkpfs</c>. Subcommands arrive phase by phase (see docs/PLAN.md).</summary>
public static class MkPFSCli
{
    /// <summary>Product name; distinct from the Python MkPFS so users can tell the two projects apart.</summary>
    public const string Name = "MkPFS.C#";

    /// <summary>Project URL shown in the header.</summary>
    public const string ProjectUrl = "https://github.com/danthespal/MkPFS.CSharp";

    /// <summary>Informational version of this build (without the source revision suffix).</summary>
    public static string Version
    {
        get
        {
            string? informational = typeof(MkPFSCli).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            string version = informational ?? typeof(MkPFSCli).Assembly.GetName().Version?.ToString() ?? "0.0.0";
            int plus = version.IndexOf('+', StringComparison.Ordinal);
            return plus >= 0 ? version[..plus] : version;
        }
    }

    /// <summary>Header line, same shape as Python <c>get_help_title</c>.</summary>
    public static string Title => $"{Name} {Version} - {ProjectUrl}";

    /// <summary>Build the root command.</summary>
    /// <param name="ctx">Output context.</param>
    /// <returns>Configured root command.</returns>
    public static RootCommand BuildRootCommand(CliContext ctx)
    {
        RootCommand root = new("Create and manage unsigned PFS (PlayStation File System) images with PFSC compression.");
        root.SetAction(_ =>
        {
            ctx.Out.WriteLine(Title);
            ctx.Out.WriteLine("Run 'mkpfs --help' for the available commands.");
            return 0;
        });
        root.Subcommands.Add(Commands.PackCommands.Pack(ctx));
        root.Subcommands.Add(Commands.ReadCommands.Verify(ctx));
        root.Subcommands.Add(Commands.ReadCommands.Inspect(ctx));
        root.Subcommands.Add(Commands.ReadCommands.Tree(ctx));
        root.Subcommands.Add(Commands.ReadCommands.Unpack(ctx));
        root.Subcommands.Add(Commands.BatchCommand.Create(ctx));
        root.Subcommands.Add(Commands.RepairCommand.Create(ctx));
        root.Subcommands.Add(BuildSelfTestCommand());
        root.Subcommands.Add(BuildPFSCBenchCommand());
        return root;
    }

    /// <summary>Parse and run against the process console.</summary>
    /// <param name="args">Process arguments.</param>
    /// <returns>Exit code.</returns>
    public static int Run(string[] args) => Run(args, CliContext.CreateDefault());

    /// <summary>Parse and run with explicit output writers.</summary>
    /// <param name="args">Process arguments.</param>
    /// <param name="ctx">Output context.</param>
    /// <returns>Exit code; 2 for usage errors, like Python argparse.</returns>
    public static int Run(string[] args, CliContext ctx)
    {
        ParseResult parse = BuildRootCommand(ctx).Parse(args);
        int exit = parse.Invoke(new InvocationConfiguration { Output = ctx.Out, Error = ctx.Err });
        return parse.Errors.Count > 0 ? 2 : exit;
    }

    // Hidden diagnostics: PFSC encode throughput for one file (compare with tools/oracle/bench.py).
    private static Command BuildPFSCBenchCommand()
    {
        Argument<FileInfo> input = new("file") { Description = "File to encode" };
        Option<int> workers = new("--cpu-count") { Description = "Compression workers (0 = auto)", DefaultValueFactory = _ => 0 };
        Option<int> level = new("--compression-level") { Description = "zlib level", DefaultValueFactory = _ => Zlib.DefaultLevel };
        Command command = new("bench-pfsc", "Measure PFSC encode throughput for one file.") { Hidden = true };
        command.Arguments.Add(input);
        command.Options.Add(workers);
        command.Options.Add(level);
        command.SetAction(parse =>
        {
            FileInfo file = parse.GetValue(input)!;
            PFSCEncodeOptions options = new()
            {
                Workers = PFSCEncoder.ResolveWorkerCount(parse.GetValue(workers)),
                Level = parse.GetValue(level),
            };
            string temp = Path.Combine(Path.GetTempPath(), $"mkpfs-bench-{Environment.ProcessId}.pfsc");
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            PFSCEncodeResult result;
            using (FileStream source = file.OpenRead())
            using (FileStream output = new(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20, FileOptions.DeleteOnClose))
            {
                result = PFSCEncoder.EncodeFile(source, source.Length, output, 0, options);
            }

            double seconds = watch.Elapsed.TotalSeconds;
            double mib = file.Length / 1048576.0;
            Console.Out.WriteLine(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{mib:F1} MiB, workers={options.Workers}, level={options.Level}: {seconds:F2} s ({mib / seconds:F1} MiB/s), stored {result.StoredSize} bytes, {result.CompressedBlocks}/{result.BlockCount} blocks compressed"));
            return 0;
        });
        return command;
    }

    // Hidden diagnostics: proves the native zlib loads (also in Native AOT builds).
    private static Command BuildSelfTestCommand()
    {
        Command command = new("selftest", "Check that the bundled native zlib loads and round-trips a block.")
        {
            Hidden = true,
        };
        command.SetAction(_ =>
        {
            IMkPFSLog log = ConsoleLog.CreateDefault();
            byte[] block = new byte[0x10000];
            for (int i = 0; i < block.Length; i++)
            {
                block[i] = (byte)(i % 251);
            }

            byte[] compressed = Zlib.Compress(block);
            byte[] restored = new byte[block.Length];
            int length = Zlib.Decompress(compressed, restored);
            bool ok = length == block.Length && restored.AsSpan().SequenceEqual(block);
            log.Info($"zlib {Zlib.NativeVersion}: 65536 -> {compressed.Length} bytes, round-trip {(ok ? "ok" : "FAILED")}");
            return ok ? 0 : 1;
        });
        return command;
    }
}
