using MkPFS.Build.AMPRPack;
using MkPFS.Cli;
using MkPFS.Cli.Output;

namespace MkPFS.Tests.AMPR;

/// <summary>Profiles tested on the PS5 shipped with MkPFS, and the choice of one for a game (not in ampr_pack.py).</summary>
public sealed class AMPRBundledProfilesTests
{
    // A stand-in for AMPR Emu 0.4.2.1+: what matters is the embedded manifest magic.
    private const string PackCapable = "ELF...AMPRPAK4.../app0/ampr_assets.index";

    private static string ParamJson(string titleId, string name) =>
        $$$$"""{"titleId":"{{{{titleId}}}}","localizedParameters":{"defaultLanguage":"en-US","en-US":{"titleName":"{{{{name}}}}"}}}""";

    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        StringWriter stdout = new() { NewLine = "\n" };
        StringWriter stderr = new() { NewLine = "\n" };
        int exit = MkPFSCli.Run(["ampr", .. args], new CliContext(stdout, stderr, useColor: false, utf8: false, progress: false));
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void Every_bundled_profile_loads_and_keeps_executables_loose()
    {
        Assert.Equal(["astrobot", "ufc6", "insomniac", "unity"], AMPRBundledProfiles.All.Select(p => p.Id));
        foreach (AMPRBundledProfile profile in AMPRBundledProfiles.All)
        {
            AMPRPackConfig config = profile.Load();
            Assert.Equal("loose", config.SelectRule("eboot.bin").Action);
            Assert.Equal("loose", config.SelectRule("sce_sys/param.json").Action);
            Assert.Equal("loose", config.SelectRule("fakelib/libSceAmpr.sprx").Action);
            Assert.Contains(config.Rules, r => r.Action == "compress");
        }

        Assert.Equal("compress", AMPRBundledProfiles.Find("insomniac")!.Load().SelectRule("d/level_01.archive").Action);
        Assert.Equal("loose", AMPRBundledProfiles.Find("INSOMNIAC")!.Load().SelectRule("d/x/bootload_00.archive").Action);
        Assert.Null(AMPRBundledProfiles.Find("frostbite"));
    }

    [Fact]
    public void Match_prefers_the_title_id_then_the_name()
    {
        using TempDir dir = new();
        dir.File("game/sce_sys/param.json", ParamJson("PPSA21567", "Something Else"));
        Assert.Equal(("astrobot", AMPRBundledMatchKind.TitleId), Kind(AMPRBundledProfiles.Match(Path.Combine(dir.Path, "game"))));

        // Another region: a different Title ID, but the name gives the game away.
        dir.File("game/sce_sys/param.json", ParamJson("PPSA99999", "EA SPORTS™ UFC® 6"));
        Assert.Equal(("ufc6", AMPRBundledMatchKind.Name), Kind(AMPRBundledProfiles.Match(Path.Combine(dir.Path, "game"))));

        dir.File("game/sce_sys/param.json", ParamJson("PPSA99999", "Another Game"));
        Assert.Null(AMPRBundledProfiles.Match(Path.Combine(dir.Path, "game")));
        Assert.Null(AMPRBundledProfiles.Match(Path.Combine(dir.Path, "missing")));
    }

    [Theory]
    [InlineData(new[] { "toc", "dag", "d/a.archive" }, "insomniac")]
    [InlineData(new[] { "toc", "d/a.archive" }, null)]
    [InlineData(new[] { "StreamingAssets/aa/catalog.json", "Data/globalgamemanagers" }, "unity")]
    [InlineData(new[] { "Media/StreamingAssets/x.bundle", "Media/Data/data.unity3d" }, "unity")]
    [InlineData(new[] { "StreamingAssets/x.bundle", "Media/Data/Managed/Metadata/global-metadata.dat" }, "unity")]
    [InlineData(new[] { "StreamingAssets/x.bundle" }, null)]
    public void Engine_is_told_apart_by_the_file_layout(string[] files, string? engine)
    {
        using TempDir dir = new();
        dir.File("game/sce_sys/param.json", ParamJson("PPSA99999", "Another Game"));
        foreach (string file in files)
        {
            dir.File("game/" + file);
        }

        string root = Path.Combine(dir.Path, "game");
        Assert.Equal(engine, AMPRBundledProfiles.DetectEngine(root));
        Assert.Equal(engine is null ? null : (engine, AMPRBundledMatchKind.Engine), Kind(AMPRBundledProfiles.Match(root)));
    }

    [Fact]
    public void Cli_game_picks_the_bundled_profile_for_the_game()
    {
        using TempDir dir = new();
        dir.File("game/sce_sys/param.json", ParamJson("PPSA28997", "God of War Sons of Sparta"));
        dir.File("game/eboot.bin", "eboot");
        dir.File("game/Data/globalgamemanagers", "unity");
        dir.File("game/StreamingAssets/aa/level.bundle", string.Concat(Enumerable.Repeat("terrain mesh texture vertex ", 20_000)));
        dir.File("libs/libSceAmpr.sprx", PackCapable);
        string output = Path.Combine(dir.Path, "out");

        (int exit, string stdout, string stderr) = Run(
            "game", "--root", Path.Combine(dir.Path, "game"), "--output", output, "--fakelib", Path.Combine(dir.Path, "libs"));

        Assert.True(exit == 0, stdout + stderr);
        Assert.Contains(
            "Rules: bundled profile unity (Unity engine), tested on God of War Sons of Sparta (PPSA28997, 01.008.001); made for this Title ID",
            stdout, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(output, "StreamingAssets", "aa", "level.bundle")));
        Assert.True(File.Exists(Path.Combine(output, "Data", "globalgamemanagers")));
    }

    [Fact]
    public void Cli_game_uses_auto_threads_unless_told_otherwise_whatever_the_profile_says()
    {
        using TempDir dir = new();
        dir.File("game/sce_sys/param.json", ParamJson("PPSA99999", "Another Game"));
        dir.File("game/eboot.bin", "eboot");
        dir.File("game/data/level.dat", string.Concat(Enumerable.Repeat("terrain mesh texture vertex ", 20_000)));
        dir.File("libs/libSceAmpr.sprx", PackCapable);
        string config = dir.File("profile.toml", "[pack]\nworkers = 1\ndefault_action = \"compress\"\n[[rule]]\naction = \"loose\"\ninclude = [\"eboot.bin\", \"sce_sys/*\", \"fakelib/*\"]\n");
        string[] Args(string output, params string[] extra) =>
            ["game", "--root", Path.Combine(dir.Path, "game"), "--output", Path.Combine(dir.Path, output), "--fakelib", Path.Combine(dir.Path, "libs"), "--config", config, .. extra];

        (int exit, string stdout, string stderr) = Run(Args("a"));
        Assert.True(exit == 0, stdout + stderr);
        Assert.Contains($"  {Core.Util.CpuCores.Max} compression threads (", stdout, StringComparison.Ordinal);

        (exit, stdout, stderr) = Run(Args("b", "--cpu-count", "1"));
        Assert.True(exit == 0, stdout + stderr);
        Assert.Contains("  1 compression threads (", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_game_profile_option_is_checked()
    {
        using TempDir dir = new();
        string game = dir.File("game/sce_sys/param.json", ParamJson("PPSA99999", "Another Game"));
        string root = Path.Combine(dir.Path, "game");

        (int exit, _, string stderr) = Run("game", "--root", root, "--output", Path.Combine(dir.Path, "out"), "--profile", "nope");
        Assert.Equal(2, exit);
        Assert.EndsWith("error: unknown bundled profile: nope (known: astrobot, ufc6, insomniac, unity)\n", stderr, StringComparison.Ordinal);

        (exit, _, stderr) = Run("game", "--root", root, "--output", Path.Combine(dir.Path, "out"), "--profile", "unity", "--config", game);
        Assert.Equal(2, exit);
        Assert.EndsWith("error: --profile cannot be used with --config or --traces\n", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_profile_bundled_lists_matches_and_exports()
    {
        using TempDir dir = new();
        dir.File("game/toc");
        dir.File("game/dag");
        dir.File("game/d/a.archive");
        string export = Path.Combine(dir.Path, "profiles");

        (int exit, string stdout, string stderr) = Run("profile", "bundled", "--root", Path.Combine(dir.Path, "game"), "--export", export);

        Assert.True(exit == 0, stderr);
        Assert.Contains("insomniac  Insomniac engine: tested on Marvel's Wolverine (PPSA03671, 01.001.005); also picked for other Insomniac engine games", stdout, StringComparison.Ordinal);
        Assert.Contains("ampr game picks insomniac: picked by the engine's file layout\n", stdout, StringComparison.Ordinal);
        Assert.Equal(AMPRBundledProfiles.Find("ufc6")!.Toml, File.ReadAllText(Path.Combine(export, "ufc6-PPSA23566.toml")));
        Assert.Equal(4, Directory.GetFiles(export, "*.toml").Length);

        // Exported files are never overwritten.
        Assert.Equal(2, Run("profile", "bundled", "--export", export).Exit);
    }

    private static (string, AMPRBundledMatchKind)? Kind(AMPRBundledMatch? match) =>
        match is null ? null : (match.Profile.Id, match.Kind);
}
