using System.Security.Cryptography;
using MkPFS.Build;
using MkPFS.Build.AMPRPack;
using MkPFS.Build.PFS;
using MkPFS.Cli;
using MkPFS.Cli.Output;
using MkPFS.Core.AMPR;
using MkPFS.Core.Diagnostics;

namespace MkPFS.Tests.AMPR;

/// <summary><c>ampr compress</c>: the PS5 LZ4 Game Compressor universal profile and workflow.</summary>
public sealed class AMPRLZ4CompressorTests
{
    private sealed class ListLog : IMkPFSLog
    {
        public List<string> Lines { get; } = [];

        public void Log(LogLevel level, string message, LogIcon icon = LogIcon.None) => Lines.Add(message);
    }

    // A stand-in for AMPR Emu 0.4.2.1+: what matters is the embedded manifest magic.
    private const string PackCapable = "ELF...AMPRPAK4.../app0/ampr_assets.index";

    private static readonly string Level = string.Concat(Enumerable.Repeat("terrain mesh texture vertex ", 20_000));

    private static readonly string[] Tree =
    [
        "eboot.bin", "sce_sys/param.json", "readme.txt", "data.001", "chunk12.pak", ".hidden",
        "Sound/music.bin", "data/sound/voice.bin", "Movies/intro.mp4", "data/level.res", "data/x.soundbank",
        "data/soundbank.pck", "data/wem_01.bin", "data/texture.dds", "fakelib2/notes.md", "sce_module/libc.prx",
    ];

    private static string Game(TempDir dir)
    {
        dir.File("game/sce_sys/param.json", """{"titleId":"PPSA01234","contentVersion":"01.000.000"}""");
        dir.File("game/eboot.bin", "eboot");
        dir.File("game/sce_module/libc.prx", "module");
        dir.File("game/data/level0.res", Level);
        dir.File("game/data/sub/sound/voice.bin", Level + "voice");
        dir.File("game/data/intro.mp4", Level);
        dir.File("game/fakelib/libSceAmpr.sprx", "old emulator without packs");
        dir.File("libs/libSceAmpr.sprx", PackCapable);
        return Path.Combine(dir.Path, "game");
    }

    private static Dictionary<string, string> Hashes(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(root, f), f => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))));

    [Fact]
    public void Profile_rules_follow_the_folder_structure_and_the_safety_rule_wins()
    {
        AMPRPackConfig config = AMPRUniversalProfile.ToConfig(AMPRUniversalProfile.RenderToml(Tree, AMPRUniversalProfile.DefaultLevel));

        // Nested, numbered, asset types, audio, video, streams, soundbank, safety.
        Assert.Equal(8, config.Rules.Count);
        Assert.Equal(["*/**"], config.Rules[0].Include);
        Assert.Equal(["chunk12.pak", "data.001"], config.Rules[1].Include);
        Assert.Equal(["**/*.bin", "**/*.dds", "**/*.res"], config.Rules[2].Include);
        Assert.Equal(AMPRUniversalProfile.SafetyPatterns, config.Rules[^1].Include);
        Assert.Equal("loose", config.DefaultAction);
        Assert.Equal(("hc", 12L), (config.CompressionMode, config.CompressionLevel));
        Assert.Equal("ampr_assets-{group}-lane{lane:02d}-vol{volume:02d}-{id:03d}.pak", config.PackPattern);
        Assert.Equal(new AMPRGroupConfig("assets", 4, "balanced", 16L << 30), config.Groups["assets"]);
        Assert.Equal(new AMPRRuntimeSettings(256L << 20, 64L << 20, 8, 1), config.Runtime);
        Assert.Equal(Core.Util.CpuCores.Max, config.Workers);

        AMPRRule Rule(string path) => config.SelectRule(path);
        Assert.Equal(("compress", "mixed", 16, "hc", 12L), (Rule("data/level.res").Action, Rule("data/level.res").Layout, Rule("data/level.res").BlockShift, Rule("data/level.res").Mode, Rule("data/level.res").Level));
        Assert.Equal("compress", Rule("data.001").Action);
        Assert.Equal(("compress", "random", 14, true), (Rule("data/sound/voice.bin").Action, Rule("data/sound/voice.bin").Layout, Rule("data/sound/voice.bin").BlockShift, Rule("data/sound/voice.bin").Hot));
        Assert.Equal(("random", true), (Rule("data/soundbank.pck").Layout, Rule("data/soundbank.pck").Hot));
        // Folders directly under /app0 are not matched by "**/sound/**", as in the compressor.
        Assert.Equal("mixed", Rule("Sound/music.bin").Layout);
        foreach (string loose in (string[])["eboot.bin", "sce_sys/param.json", "readme.txt", "Movies/intro.mp4", "data/wem_01.bin", "data/texture.dds",
                     "fakelib2/notes.md", "sce_module/libc.prx", "data/movies/clip.bik", "ampr_assets-assets-lane00-vol00-000.pak"])
        {
            Assert.True(Rule(loose).Action == "loose", loose);
        }
    }

    [Theory]
    [InlineData(1, "fast")]
    [InlineData(4, "fast")]
    [InlineData(5, "hc")]
    [InlineData(12, "hc")]
    public void Low_levels_use_the_fast_encoder(int level, string mode)
    {
        AMPRPackConfig config = AMPRUniversalProfile.ToConfig(AMPRUniversalProfile.RenderToml(["eboot.bin"], level));

        Assert.Equal((mode, (long)level), (config.CompressionMode, config.CompressionLevel));
        Assert.All(config.Rules, rule => Assert.Equal(mode, rule.Mode));
    }

    [Fact]
    public void Profile_without_nested_files_keeps_only_the_stream_and_safety_rules()
    {
        string toml = AMPRUniversalProfile.RenderToml(["readme.txt", "param.sfo"], 9);
        AMPRPackConfig config = AMPRUniversalProfile.ToConfig(toml);

        Assert.StartsWith(AMPRUniversalProfile.Header + "\n", toml, StringComparison.Ordinal);
        Assert.Equal(2, config.Rules.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => AMPRUniversalProfile.RenderToml(["eboot.bin"], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => AMPRUniversalProfile.RenderToml(["eboot.bin"], 13));
    }

    [Fact]
    public void Output_estimate_matches_the_compressor()
    {
        AMPRLZ4Compressor.SourceFile[] files = [new("a", 1), new("dir/b", 65537), new("ampr_emu.index", 999), new("empty", 0)];

        // Pages 65536 + 131072; metadata 512 + (128+2+16) + (128+10+80) + (128+10); 5 reserve pages; 16 MiB margin.
        Assert.Equal(196608 + 1014 + (5 * 65536) + (16L << 20), AMPRLZ4Compressor.EstimateOutputBytes(files));
    }

    [Fact]
    public void Game_folder_is_packed_with_the_generated_rules_and_the_source_is_untouched()
    {
        using TempDir dir = new();
        string game = Game(dir);
        Dictionary<string, string> before = Hashes(game);
        string output = Path.Combine(dir.Path, "out");
        ListLog log = new();

        AMPRGameResult result = AMPRLZ4Compressor.Build(game, output, new AMPRLZ4CompressorOptions { LibsDir = Path.Combine(dir.Path, "libs") }, log);

        Assert.Equal(before, Hashes(game));
        Assert.Contains(log.Lines, l => l.StartsWith("Rules: LZ4 Game Compressor universal profile (LZ4 HC level 12), 4 rules", StringComparison.Ordinal));
        Assert.Contains("[5/5] Verifying", log.Lines);
        Assert.Contains(log.Lines, l => l.EndsWith(" can be packed (large files sampled)", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Lines, l => l.StartsWith("warning: only ", StringComparison.Ordinal) || l.Contains("Unreal", StringComparison.Ordinal));
        Assert.Equal(2, result.Pack.Stats.FilesPacked);
        Assert.True(result.Pack.Stats.ChunksLZ4 > 0);
        Assert.Equal(PackCapable, File.ReadAllText(Path.Combine(output, "fakelib", "libSceAmpr.sprx")));
        Assert.False(File.Exists(Path.Combine(output, "data", "level0.res")));
        Assert.False(File.Exists(Path.Combine(output, "data", "sub", "sound", "voice.bin")));
        foreach (string loose in (string[])["eboot.bin", "sce_module/libc.prx", "sce_sys/param.json", "data/intro.mp4"])
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(game, loose)), File.ReadAllBytes(Path.Combine(output, loose)));
        }

        // Balanced over four lanes: each packed file gets its own volume; the runtime settings are written.
        Assert.Equal(2, Directory.GetFiles(output, "ampr_assets-assets-lane*-vol00-*.pak").Length);
        Assert.True(File.Exists(Path.Combine(output, "ampr_assets.index.runtime")));
        Assert.Equal(2, AMPRPackTools.VerifyAgainstRoot(result.Pack.IndexPath, game).Files);
    }

    [Fact]
    public void Fast_level_and_skipped_verification_are_honored()
    {
        using TempDir dir = new();
        string game = Game(dir);
        ListLog log = new();

        AMPRLZ4Compressor.Build(
            game, Path.Combine(dir.Path, "out"), new AMPRLZ4CompressorOptions { Level = 2, Workers = 1, Verify = false, LibsDir = Path.Combine(dir.Path, "libs") }, log);

        Assert.Contains(log.Lines, l => l.StartsWith("Rules: LZ4 Game Compressor universal profile (LZ4 fast)", StringComparison.Ordinal));
        Assert.Contains("[5/5] Verification skipped", log.Lines);
    }

    [Fact]
    public void Inputs_that_are_not_an_uncompressed_game_folder_are_rejected_before_anything_is_written()
    {
        using TempDir dir = new();
        string game = Game(dir);
        string output = Path.Combine(dir.Path, "out");
        AMPRLZ4CompressorOptions options = new() { LibsDir = Path.Combine(dir.Path, "libs") };
        string Error(AMPRLZ4CompressorOptions o) =>
            Assert.Throws<BuildException>(() => AMPRLZ4Compressor.Build(game, output, o, new ListLog())).Message;

        Assert.Equal("compression level must be between 1 and 12, got 13", Error(options with { Level = 13 }));
        Assert.Equal("workers must not be negative", Error(options with { Workers = -1 }));
        Assert.StartsWith("game folder not found", Assert.Throws<BuildException>(
            () => AMPRLZ4Compressor.Build(Path.Combine(dir.Path, "nope"), output, options, new ListLog())).Message, StringComparison.Ordinal);

        dir.File("game/ampr_assets.index.crc", "old sidecar");
        Assert.Equal("the game folder already has pack metadata (ampr_assets.index.crc); select the original uncompressed game folder", Error(options));
        File.Delete(Path.Combine(game, "ampr_assets.index.crc"));

        File.Delete(Path.Combine(game, "eboot.bin"));
        Assert.Equal("the game folder has no eboot.bin; select an extracted PS5 game folder (/app0)", Error(options));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Cli_compress_builds_the_folder_and_validates_its_options()
    {
        using TempDir dir = new();
        string game = Game(dir);
        StringWriter stdout = new() { NewLine = "\n" };
        StringWriter stderr = new() { NewLine = "\n" };
        CliContext ctx = new(stdout, stderr, useColor: false, utf8: false, progress: false);

        int exit = MkPFSCli.Run(["ampr", "compress", "--root", game, "--output", Path.Combine(dir.Path, "out"), "--fakelib", Path.Combine(dir.Path, "libs"), "--level", "3"], ctx);

        Assert.True(exit == 0, stdout.ToString() + stderr.ToString());
        Assert.StartsWith(
            global::MkPFS.Cli.Commands.AmprCommand.ExperimentalWarning + "\nRules: LZ4 Game Compressor universal profile (LZ4 fast), ", stdout.ToString(), StringComparison.Ordinal);
        foreach (string step in (string[])["[1/5] Libraries: fakelib/", "[2/5] Writing ampr_emu.index", "[3/5] Packing", "[4/5] Copying loose files", "[5/5] Verifying"])
        {
            Assert.Contains(step + "\n", stdout.ToString(), StringComparison.Ordinal);
        }

        (int, string) Fails(params string[] extra)
        {
            stderr.GetStringBuilder().Clear();
            int code = MkPFSCli.Run(["ampr", "compress", "--root", game, "--output", Path.Combine(dir.Path, "out2"), .. extra], ctx);
            return (code, stderr.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1]);
        }

        Assert.Equal((2, "error: --level must be between 1 and 12"), Fails("--level", "13"));
        Assert.Equal((2, "error: --workers must not be negative"), Fails("--workers", "-1"));
        Assert.Equal((2, "error: --exfat-free-space needs --exfat"), Fails("--exfat-free-space", "1GiB"));
        Assert.Equal(
            (2, "error: fakelib/libSceAmpr.sprx cannot read asset packs; use AMPR Emu 0.4.2.1 or newer from https://github.com/drakmor/ampr_emu/releases"),
            Fails());
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "out2")));
    }

    [Fact]
    public void A_game_with_nothing_to_pack_is_reported_before_the_build()
    {
        // Only loose types: a video, the loader and system files.
        using TempDir dir = new();
        dir.File("game/sce_sys/param.json", """{"titleId":"PPSA01234"}""");
        dir.File("game/eboot.bin", "eboot");
        dir.File("game/movies/intro.bk2", Level);
        dir.File("libs/libSceAmpr.sprx", PackCapable);
        ListLog log = new();

        AMPRLZ4Compressor.Build(Path.Combine(dir.Path, "game"), Path.Combine(dir.Path, "out"), new AMPRLZ4CompressorOptions { LibsDir = Path.Combine(dir.Path, "libs") }, log);

        int warning = log.Lines.FindIndex(l => l.StartsWith("warning: only 0.00 B of ", StringComparison.Ordinal));
        Assert.True(warning >= 0, string.Join('\n', log.Lines));
        Assert.EndsWith("can be packed: the large files do not compress with LZ4 (their data is already compressed), so the packed game would be about as large as the original", log.Lines[warning], StringComparison.Ordinal);
        Assert.True(warning < log.Lines.IndexOf("[1/5] Libraries: fakelib/"), "the warning comes before the build");
    }

    [Fact]
    public void Remedy_pack_tables_stay_loose_and_their_blobs_are_packed()
    {
        // Alan Wake 2 (PPSA02572) opens data_pack2/*.rmdtoc with plain sceKernelOpen and reads the blobs through APR.
        string[] remedy =
        [
            "eboot.bin", "data_pack2/prospero/base-generic.rmdtoc", "data_pack2/prospero/base-generic-000.rmdblob",
            "data_pack2/prospero/base-en.rmdtoc", "data_pack2/generic/base-generic-000.rmdblob",
        ];
        AMPRPackConfig config = AMPRUniversalProfile.ToConfig(AMPRUniversalProfile.RenderToml(remedy, 12));

        Assert.Equal("loose", config.SelectRule("data_pack2/prospero/base-generic.rmdtoc").Action);
        Assert.Equal("loose", config.SelectRule("data_pack2/prospero/base-en.rmdtoc").Action);
        Assert.Equal("compress", config.SelectRule("data_pack2/prospero/base-generic-000.rmdblob").Action);
        Assert.Equal("compress", config.SelectRule("data_pack2/generic/base-generic-000.rmdblob").Action);
    }

    [Fact]
    public void Unreal_games_say_why_their_containers_stay_loose()
    {
        string[] unreal = ["eboot.bin", "game/content/paks/game-ps5.utoc", "game/content/paks/game-ps5.ucas", "game/content/paks/game-ps5.pak"];
        Assert.True(AMPRUniversalProfile.IsUnrealIoStore(unreal));
        Assert.False(AMPRUniversalProfile.IsUnrealIoStore(unreal.Where(f => !f.EndsWith(".utoc", StringComparison.Ordinal))));

        string toml = AMPRUniversalProfile.RenderToml(unreal, 12);
        Assert.Contains("# Unreal Engine IoStore: .utoc tables", toml, StringComparison.Ordinal);
        Assert.DoesNotContain("Unreal", AMPRUniversalProfile.RenderToml(Tree, 12), StringComparison.Ordinal);

        // The comment explains the rules; it does not change them.
        AMPRPackConfig config = AMPRUniversalProfile.ToConfig(toml);
        Assert.Equal("loose", config.SelectRule("game/content/paks/game-ps5.utoc").Action);
        Assert.Equal("loose", config.SelectRule("game/content/paks/game-ps5.pak").Action);
        Assert.Equal("compress", config.SelectRule("game/content/paks/game-ps5.ucas").Action);

        using TempDir dir = new();
        dir.File("game/sce_sys/param.json", """{"titleId":"PPSA01234"}""");
        foreach (string file in unreal)
        {
            dir.File("game/" + file, Level);
        }

        dir.File("libs/libSceAmpr.sprx", PackCapable);
        ListLog log = new();
        AMPRLZ4Compressor.Build(Path.Combine(dir.Path, "game"), Path.Combine(dir.Path, "out"), new AMPRLZ4CompressorOptions { LibsDir = Path.Combine(dir.Path, "libs") }, log);
        Assert.Contains("  Unreal Engine IoStore: .utoc tables and .pak archives stay loose; .ucas containers are packed only if they compress", log.Lines);
    }
}
