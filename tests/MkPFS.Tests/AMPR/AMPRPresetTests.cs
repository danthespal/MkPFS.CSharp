using MkPFS.Build;
using MkPFS.Build.AMPRPack;
using MkPFS.Cli;
using MkPFS.Cli.Output;
using MkPFS.Core.AMPR;

namespace MkPFS.Tests.AMPR;

/// <summary>The built-in <c>default</c> preset and the CLI around it (extensions over ampr_pack.py).</summary>
public sealed class AMPRPresetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mkpfs-ampr-preset-" + Guid.NewGuid().ToString("N"));

    public AMPRPresetTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("data/level.dat", "compress")]
    [InlineData("d/0/texture", "compress")]
    [InlineData("eboot.bin", "loose")]
    [InlineData("bin/eboot.bin", "loose")]
    [InlineData("libcohtml.Prospero.prx", "loose")]
    [InlineData("fakelib/libSceAmpr.sprx", "loose")]
    [InlineData("fakelib2/libSceAmpr.sprx", "loose")]
    [InlineData("sce_sys/param.json", "loose")]
    [InlineData("sce_module/libc.prx", "loose")]
    [InlineData("tools/run.elf", "loose")]
    [InlineData("system/x.bin", "loose")]
    [InlineData("ampr_emu.index", "loose")]
    [InlineData("Media/Metadata/global-metadata.dat", "loose")]
    [InlineData("Media/Metadata/other.dat", "compress")]
    public void Default_preset_packs_data_and_keeps_protected_files_loose(string relative, string action)
    {
        AMPRPackConfig preset = AMPRPackConfig.LoadPreset("default");
        Assert.Equal(action, preset.SelectRule(relative).Action);
        // Whatever the preset packs, remove-sources must be allowed to remove.
        Assert.False(action == "compress" && AMPRPackMaintenance.IsProtected(relative));
    }

    [Theory]
    [InlineData("Media/StreamingAssets/aa/PS5/level.bundle", "compress")]
    [InlineData("Media/StreamingAssets/Dialogue.assets.bank", "compress")]
    [InlineData("StreamingAssets/movie.mp4", "compress")]
    [InlineData("Media/sharedassets3.assets.resS", "loose")]
    [InlineData("Media/globalgamemanagers", "loose")]
    [InlineData("Media/level158", "loose")]
    [InlineData("Media/Metadata/global-metadata.dat", "loose")]
    [InlineData("Media/StreamingAssets/Plugins/x.prx", "loose")]
    [InlineData("eboot.bin", "loose")]
    [InlineData("fakelib/libSceAmpr.sprx", "loose")]
    public void Unity_preset_packs_only_streaming_assets(string relative, string action)
    {
        AMPRPackConfig preset = AMPRPackConfig.LoadPreset("unity");
        Assert.Equal(action, preset.SelectRule(relative).Action);
        Assert.False(action == "compress" && AMPRPackMaintenance.IsProtected(relative));
    }

    [Theory]
    [InlineData("Media/globalgamemanagers", "unity")]
    [InlineData("Game_Data/data.unity3d", "unity")]
    [InlineData("Media/Metadata/global-metadata.dat", "unity")]
    [InlineData("data/level.dat", "default")]
    public void Auto_picks_unity_rules_only_for_unity_games(string file, string expected)
    {
        string root = Path.Combine(_dir, "game");
        string full = Path.Combine(root, file);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "x");

        (string preset, string? marker) = AMPRPackConfig.DetectPreset(root);

        Assert.Equal(expected, preset);
        Assert.Equal(expected == "unity" ? file : null, marker);
        AMPRPackConfig auto = AMPRPackConfig.LoadPreset("auto", root);
        Assert.Equal(expected == "unity" ? "loose" : "compress", auto.SelectRule("Media/level158").Action);
    }

    [Fact]
    public void Cli_auto_reports_the_rules_it_picked()
    {
        string root = Game();
        File.WriteAllText(Path.Combine(root, "data", "globalgamemanagers"), "unity");
        AmprIndex.Build(root, Path.Combine(root, AmprIndex.IndexName));

        (int exit, string stdout, string stderr) = Run(
            "pack", "--root", root, "--ampr-index", Path.Combine(root, AmprIndex.IndexName), "--output", Path.Combine(_dir, "out"), "--preset", "auto");

        Assert.Equal(0, exit);
        Assert.Equal(
            "Profile found: unity (data/globalgamemanagers); only StreamingAssets is packed. Tested: runs on God of War Sons of Sparta (PPSA28997).\n"
                + global::MkPFS.Cli.Commands.AmprCommand.NothingPackedWarning + "\n",
            stderr);
        Assert.Contains("\"files_packed\": 0,", stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("auto", "No profile found for this game: generic rules (every file but executables, modules and system files is packed). Untested: the game may not start on the PS5; keep the original.")]
    [InlineData("default", DefaultRulesLine)]
    [InlineData("unity", "Rules: unity (--preset); only StreamingAssets is packed. Tested: runs on God of War Sons of Sparta (PPSA28997).")]
    public void Cli_says_when_no_profile_matches_or_the_rules_were_chosen(string preset, string expected)
    {
        string root = Game();
        AmprIndex.Build(root, Path.Combine(root, AmprIndex.IndexName));

        (int exit, _, string stderr) = Run(
            "pack", "--root", root, "--ampr-index", Path.Combine(root, AmprIndex.IndexName), "--output", Path.Combine(_dir, "out"), "--preset", preset);

        Assert.Equal(0, exit);
        Assert.StartsWith(expected + "\n", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_preset_is_rejected() =>
        Assert.Equal("unknown preset: max", Assert.Throws<AMPRPackException>(() => AMPRPackConfig.LoadPreset("max")).Message);

    private string Game(string? emulator = "fakelib")
    {
        string root = Path.Combine(_dir, "app0");
        List<(string, string)> files = [("eboot.bin", "eboot"), ("sce_sys/param.json", "{}"), ("lib.prx", "prx"), ("data/a.dat", new string('a', 50_000))];
        if (emulator is not null)
        {
            files.Add(($"{emulator}/libSceAmpr.sprx", "sprx"));
        }

        foreach ((string path, string text) in files)
        {
            string full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        AmprIndex.Build(root, Path.Combine(root, AmprIndex.IndexName));
        return root;
    }

    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        StringWriter stdout = new() { NewLine = "\n" };
        StringWriter stderr = new() { NewLine = "\n" };
        int exit = MkPFSCli.Run(["ampr", .. args], new CliContext(stdout, stderr, useColor: false, utf8: false, progress: false));
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private const string DefaultRulesLine =
        "Rules: default (--preset); every file but executables, modules and system files is packed. Untested: the game may not start on the PS5; keep the original.";

    [Fact]
    public void Cli_preset_packs_and_the_removal_plan_accepts_it()
    {
        string root = Game();
        string output = Path.Combine(_dir, "out");

        (int exit, string stdout, string stderr) = Run(
            "pack", "--root", root, "--ampr-index", Path.Combine(root, AmprIndex.IndexName), "--output", output, "--preset", "default");

        Assert.Equal(0, exit);
        Assert.Contains("\"files_packed\": 1,", stdout, StringComparison.Ordinal);
        Assert.Equal(DefaultRulesLine + "\n", stderr);
        Assert.Equal(["data/a.dat"], AMPRPackMaintenance.RemovalPlan(Path.Combine(output, "ampr_assets.index"), root).Paths);
    }

    [Fact]
    public void Cli_warns_when_nothing_is_packed_and_rejects_config_with_preset()
    {
        string root = Game();
        string index = Path.Combine(root, AmprIndex.IndexName);
        string output = Path.Combine(_dir, "out");

        (int exit, string stdout, string stderr) = Run("pack", "--root", root, "--ampr-index", index, "--output", output);
        Assert.Equal(0, exit);
        Assert.Contains("\"files_packed\": 0,", stdout, StringComparison.Ordinal);
        Assert.Equal(global::MkPFS.Cli.Commands.AmprCommand.NothingPackedWarning + "\n", stderr);

        string config = Path.Combine(_dir, "c.toml");
        File.WriteAllText(config, "[pack]\n");
        (exit, stdout, stderr) = Run("pack", "--root", root, "--ampr-index", index, "--output", output, "--config", config, "--preset", "default");
        Assert.Equal((2, string.Empty, "error: --config and --preset cannot be used together\n"), (exit, stdout, stderr));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("fakelib", false)]
    [InlineData("fakelib2", false)]
    public void Cli_warns_when_packs_are_built_for_a_folder_without_ampr_emu(string? emulator, bool warns)
    {
        string root = Game(emulator);

        (int exit, _, string stderr) = Run(
            "pack", "--root", root, "--ampr-index", Path.Combine(root, AmprIndex.IndexName), "--output", Path.Combine(_dir, "out"), "--preset", "default");

        Assert.Equal(0, exit);
        Assert.Equal(DefaultRulesLine + "\n" + (warns ? global::MkPFS.Cli.Commands.AmprCommand.NoEmulatorWarning + "\n" : string.Empty), stderr);
    }

    [Fact]
    public void Runtime_limits_of_the_default_emulator_build_are_reported()
    {
        AMPRBuildStats stats = new() { FilesTotal = AMPRBuildResult.RuntimeMaxFiles, Chunks = AMPRBuildResult.RuntimeMaxChunks };
        Assert.Empty(new AMPRBuildResult("i", stats, [], AMPRBuildResult.RuntimeMaxVolumes).RuntimeLimitWarnings());

        stats = new() { FilesTotal = AMPRBuildResult.RuntimeMaxFiles + 1, Chunks = AMPRBuildResult.RuntimeMaxChunks + 1 };
        IReadOnlyList<string> warnings = new AMPRBuildResult("i", stats, [], AMPRBuildResult.RuntimeMaxVolumes + 1).RuntimeLimitWarnings();
        Assert.Equal(3, warnings.Count);
        Assert.StartsWith("2000001 files exceed", warnings[0], StringComparison.Ordinal);
        Assert.StartsWith("16000001 chunks exceed", warnings[1], StringComparison.Ordinal);
        Assert.StartsWith("1025 pack volumes exceed", warnings[2], StringComparison.Ordinal);
    }
}
