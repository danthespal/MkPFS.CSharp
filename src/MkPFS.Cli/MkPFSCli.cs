using System.CommandLine;
using System.Reflection;
using MkPFS.Cli.Output;
using MkPFS.Core.Compression;
using MkPFS.Core.Diagnostics;

namespace MkPFS.Cli;

/// <summary>Command-line wiring for <c>mkpfs</c>. Subcommands arrive phase by phase (see docs/PLAN.md).</summary>
public static class MkPFSCli
{
    /// <summary>Project URL shown in the header.</summary>
    public const string ProjectUrl = "https://github.com/PSBrew/MkPFS";

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
    public static string Title => $"MkPFS {Version} - {ProjectUrl}";

    /// <summary>Build the root command.</summary>
    /// <returns>Configured root command.</returns>
    public static RootCommand BuildRootCommand()
    {
        RootCommand root = new("Create and manage unsigned PFS (PlayStation File System) images with PFSC compression.");
        root.SetAction(_ =>
        {
            Console.Out.WriteLine(Title);
            Console.Out.WriteLine("No commands available yet. Run 'mkpfs --help'.");
            return 0;
        });
        root.Subcommands.Add(BuildSelfTestCommand());
        return root;
    }

    /// <summary>Parse and run.</summary>
    /// <param name="args">Process arguments.</param>
    /// <returns>Exit code.</returns>
    public static int Run(string[] args) => BuildRootCommand().Parse(args).Invoke();

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
