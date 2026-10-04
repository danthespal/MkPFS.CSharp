using System.Buffers.Binary;
using MkPFS.Build;
using MkPFS.Build.AMPRPack;
using MkPFS.Cli;
using MkPFS.Cli.Output;
using MkPFS.Core.AMPR;

namespace MkPFS.Tests.AMPR;

/// <summary>
/// <c>ampr profile</c> and <c>--traces</c>: the port of ampr_pack_profile.py. Byte parity with the Python tool is
/// checked by <c>tools/oracle/check_ampr_profile.py</c>; these tests cover the decoder and the integration.
/// </summary>
public sealed class AMPRProfilerTests
{
    /// <summary>Writes AMPRCMD1 journals like the debug emulator build.</summary>
    internal sealed class TraceWriter
    {
        private readonly MemoryStream _data = new();
        private ulong _sequence;

        public static byte[] ReadFile(int fileId, long offset, long length)
        {
            bool wide = offset >= 1L << 32;
            byte[] command = new byte[wide ? 24 : 20];
            uint dwords = wide ? 6u : 5u;
            BinaryPrimitives.WriteUInt32LittleEndian(command, 40 | ((dwords - 1) << 8) | (uint)((offset & 0x3FFFF) << 12));
            BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(4), (uint)(length - 1));
            BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(8), (uint)fileId);
            BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(16), (uint)(offset & 0xFFFC0000));
            if (wide)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(20), (uint)((offset >> 32) & 0xFF));
            }

            return command;
        }

        public TraceWriter Record(byte[] payload, uint priority = 0, uint domain = 1)
        {
            byte[] header = new byte[AMPRCommandLog.HeaderSize];
            "AMPRCMD1"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), AMPRCommandLog.HeaderSize);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)(AMPRCommandLog.HeaderSize + payload.Length));
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(16), ++_sequence);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(32), 1_000_000 * _sequence);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(56), AMPRCommandLog.Fnv1a64(payload));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(64), (uint)payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(76), priority);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(80), domain);
            _data.Write(header);
            _data.Write(payload);
            return this;
        }

        public void Save(string path) => File.WriteAllBytes(path, _data.ToArray());
    }

    private static readonly string Level = string.Concat(Enumerable.Repeat("terrain mesh texture vertex ", 20_000));

    // A game folder and one trace run of it: the game read data/level0.dat (randomly) and eboot.bin, nothing else.
    private static (string Game, string Traces) TracedGame(TempDir dir)
    {
        dir.File("game/sce_sys/param.json", """{"titleId":"PPSA01234","contentVersion":"01.000.000"}""");
        dir.File("game/eboot.bin", "eboot");
        dir.File("game/data/level0.dat", Level);
        dir.File("game/data/level1.dat", Level + "1");
        dir.File("game/fakelib/libSceAmpr.sprx", "ELF...AMPRPAK4.../app0/ampr_assets.index");
        string game = Path.Combine(dir.Path, "game");
        string run = dir.Dir("traces/startup");
        AmprIndex.Build(game, Path.Combine(run, "ampr_emu.index"));
        Dictionary<int, AMPRTraceIndexEntry> index = AMPRCommandLog.LoadIndex(Path.Combine(run, "ampr_emu.index"));
        int Id(string path) => index.Single(e => e.Value.Path == path).Key;
        TraceWriter trace = new();
        Random rng = new(7);
        for (int i = 0; i < 40; i++)
        {
            trace.Record(TraceWriter.ReadFile(Id("/app0/data/level0.dat"), rng.Next(0, Level.Length - 9000), 8192));
        }

        trace.Record(TraceWriter.ReadFile(Id("/app0/eboot.bin"), 0, 5));
        trace.Save(Path.Combine(run, "ampr_commands.bin"));
        return (game, Path.Combine(dir.Path, "traces"));
    }

    [Theory]
    [InlineData(new uint[] { 0x12345428, 99, 7, 0, 0xABCC1234 }, "AprReadFile", 5, 7, 0xABCD2345L, 100)]
    [InlineData(new uint[] { 0x1528, 0, 0x80000003, 0, 0x40000, 0x12 }, "AprReadFile", 6, 3, 0x12_0004_0001L, 1)]
    [InlineData(new uint[] { 0x5229, 9, 3 }, "AprReadGather", 3, 0, 5 | (3L << 18), 10)]
    [InlineData(new uint[] { 0x22A, 15, 0 }, "AprReadScatter", 3, 0, 0, 16)]
    [InlineData(new uint[] { 0x32B, 0, 0, 0x40000 }, "AprReadGatherScatter", 4, 0, 0x40000, 1)]
    [InlineData(new uint[] { 47 }, "AprResetGatherScatterState", 1, 0, 0, 0)]
    [InlineData(new uint[] { 0x5452110F, 0x6968 }, "MarkerSet", 2, 0, 0, 0)]
    [InlineData(new uint[] { 0x325, 1, 2, 3 }, "Amm", 4, 0, 0, 0)]
    [InlineData(new uint[] { 0xBBC }, "Unknown", 1, 0, 0, 0)]
    public void Commands_decode_like_parse_ampr_command_log(uint[] words, string name, int dwords, long fileId, long fileOffset, long length)
    {
        byte[] data = new byte[words.Length * 4];
        for (int i = 0; i < words.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i * 4), words[i]);
        }

        Assert.Equal(new AMPRCommand(name, dwords, fileId, fileOffset, length), AMPRCommandLog.Decode(data, 0));
    }

    [Theory]
    [InlineData(new uint[] { 0x228, 0, 0 }, "invalid AprReadFile length")]
    [InlineData(new uint[] { 0x701 }, "invalid WaitOnAddress length")]
    [InlineData(new uint[] { 0x302, 0, 0, 0 }, "invalid WaitOnCounter length")]
    [InlineData(new uint[] { 1032, 0 }, "truncated WriteKernelEventQueue")]
    [InlineData(new uint[] { 0x5452500F }, "marker with color has no color dword")]
    public void Invalid_commands_raise_the_parser_message(uint[] words, string message)
    {
        byte[] data = new byte[words.Length * 4];
        for (int i = 0; i < words.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i * 4), words[i]);
        }

        Assert.Equal(message, Assert.Throws<InvalidDataException>(() => AMPRCommandLog.Decode(data, 0)).Message);
        Assert.Equal("unaligned or truncated command", Assert.Throws<InvalidDataException>(() => AMPRCommandLog.Decode(data, 2)).Message);
    }

    [Fact]
    public void A_profile_packs_only_what_the_game_read_and_keeps_executables_loose()
    {
        using TempDir dir = new();
        (_, string traces) = TracedGame(dir);
        List<AMPRTraceSpec> runs = AMPRProfiler.DiscoverTracePairs(traces);
        Assert.Equal("startup", Assert.Single(runs).Name);

        AMPRProfileOptions options = new() { Name = "game" };
        AMPRProfileResult result = AMPRProfiler.Build(runs, options);

        Assert.Equal(2, result.ObservedFiles);
        Assert.Equal(1, result.PackedFiles);
        string toml = AMPRProfiler.RenderToml(result, options);
        Assert.Contains("include = [\"data/level0.dat\"]", toml, StringComparison.Ordinal);
        Assert.DoesNotContain("level1", toml, StringComparison.Ordinal);
        AMPRPackConfig config = AMPRProfiler.ToConfig(result, options);
        Assert.Equal("compress", config.SelectRule("data/level0.dat").Action);
        Assert.Equal("loose", config.SelectRule("data/level1.dat").Action);
        Assert.Equal("loose", config.SelectRule("eboot.bin").Action);
        Assert.NotNull(config.Runtime);
    }

    [Fact]
    public void Trace_folders_are_found_in_path_order_and_named_after_their_folders()
    {
        using TempDir dir = new();
        foreach (string run in (string[])["b/2", "a", "b/10", "c"])
        {
            dir.File($"t/{run}/ampr_commands.bin");
            if (run != "c")
            {
                dir.File($"t/{run}/ampr_emu.index");
            }
        }

        Assert.Equal(["a", "b-10", "b-2"], AMPRProfiler.DiscoverTracePairs(Path.Combine(dir.Path, "t")).Select(r => r.Name));
        Assert.Empty(AMPRProfiler.DiscoverTracePairs(Path.Combine(dir.Path, "missing")));
    }

    [Fact]
    public void Cli_game_with_traces_packs_the_traced_files_and_says_so()
    {
        using TempDir dir = new();
        (string game, string traces) = TracedGame(dir);
        StringWriter stdout = new() { NewLine = "\n" };
        StringWriter stderr = new() { NewLine = "\n" };
        CliContext ctx = new(stdout, stderr, useColor: false, utf8: false, progress: false);
        string output = Path.Combine(dir.Path, "out");

        int exit = MkPFSCli.Run(["ampr", "game", "--root", game, "--output", output, "--traces", traces], ctx);

        Assert.True(exit == 0, stdout.ToString() + stderr.ToString());
        Assert.Contains($"\nRules: from 1 trace run in {traces}; 1 file the game read through APR are packed (", stdout.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(output, "data", "level0.dat")));
        Assert.True(File.Exists(Path.Combine(output, "data", "level1.dat")));
        Assert.True(File.Exists(Path.Combine(output, "ampr_assets.index.runtime")));

        exit = MkPFSCli.Run(["ampr", "game", "--root", game, "--output", Path.Combine(dir.Path, "out2"), "--traces", traces, "--preset", "unity"], ctx);
        Assert.Equal(2, exit);
        Assert.EndsWith("error: --traces cannot be used with --config or --preset\n", stderr.ToString(), StringComparison.Ordinal);

        exit = MkPFSCli.Run(["ampr", "game", "--root", game, "--output", Path.Combine(dir.Path, "out3"), "--traces", game], ctx);
        Assert.Equal(2, exit);
        Assert.EndsWith($"error: no traces found in {game}: each run needs ampr_commands.bin and its ampr_emu.index side by side\n", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_profile_generate_writes_the_toml_report_and_header_and_refuses_to_overwrite()
    {
        using TempDir dir = new();
        (_, string traces) = TracedGame(dir);
        StringWriter stdout = new() { NewLine = "\n" };
        StringWriter stderr = new() { NewLine = "\n" };
        CliContext ctx = new(stdout, stderr, useColor: false, utf8: false, progress: false);
        string toml = Path.Combine(dir.Path, "p", "game.toml");
        string[] args = ["ampr", "profile", "generate", Path.Combine(traces, "startup"), "--output", toml, "--report", Path.Combine(dir.Path, "p", "game.md"),
            "--runtime-header", Path.Combine(dir.Path, "p", "game.h")];

        Assert.Equal(0, MkPFSCli.Run(args, ctx));
        Assert.StartsWith($"generated {toml}: files=2 index=", stdout.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("# Generated by ampr_pack_profile.py 4.1 from APR traces.\n", File.ReadAllText(toml), StringComparison.Ordinal);
        Assert.Contains("# AMPR trace-derived pack profile: startup", File.ReadAllText(Path.Combine(dir.Path, "p", "game.md")), StringComparison.Ordinal);
        Assert.Contains("#define AMPR_EMU_PACK_WORKERS ", File.ReadAllText(Path.Combine(dir.Path, "p", "game.h")), StringComparison.Ordinal);
        Assert.Equal("compress", AMPRPackConfig.Load(toml).SelectRule("data/level0.dat").Action);

        Assert.Equal(2, MkPFSCli.Run(args, ctx));
        Assert.EndsWith($"error: refusing to overwrite existing file: {toml}\n", stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, MkPFSCli.Run([.. args, "--overwrite"], ctx));
    }
}
