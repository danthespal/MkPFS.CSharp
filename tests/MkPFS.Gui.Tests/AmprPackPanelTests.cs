using Avalonia.Headless.XUnit;
using Avalonia.Media;
using MkPFS.Build;
using MkPFS.Gui.Jobs;
using MkPFS.Gui.ViewModels;

namespace MkPFS.Gui.Tests;

[Collection(GuiCollection.Name)]
public sealed class AmprPackPanelTests
{
    private static JobRunner Sync() => new(action => action());

    private static AmprPackPanelViewModel Panel(string action)
    {
        AmprPackPanelViewModel panel = new(Colors.YellowGreen, Sync());
        panel.Action = panel.Actions.Single(a => a.Value == action);
        return panel;
    }

    [Fact]
    public void Pack_builds_arguments_and_validates()
    {
        AmprPackPanelViewModel panel = Panel("pack");
        Assert.Null(panel.BuildArguments(out string? error));
        Assert.Equal("✗ Game folder and output folder are both required.", error);

        panel.Root = "D:/game";
        panel.Output = "D:/out";
        Assert.Equal(Path.Combine("D:/out", "ampr_assets.index"), panel.Manifest);
        Assert.Null(panel.BuildArguments(out error));
        Assert.Equal("✗ Choose a TOML profile or a trace folder: without rules nothing is packed.", error);
        panel.Config = "D:/rules.toml";
        Assert.Equal(
            ["ampr", "pack", "--root", "D:/game", "--ampr-index", Path.Combine("D:/game", "ampr_emu.index"), "--output", "D:/out", "--config", "D:/rules.toml"],
            panel.BuildArguments(out _));

        panel.AmprIndex = "D:/idx/ampr_emu.index";
        panel.Config = "D:/rules.toml";
        panel.Workers = "4";
        panel.SelfContained = true;
        panel.AllowMissing = true;
        Assert.Equal(
            ["ampr", "pack", "--root", "D:/game", "--ampr-index", "D:/idx/ampr_emu.index", "--output", "D:/out", "--config", "D:/rules.toml",
                "--workers", "4", "--self-contained", "--allow-missing"],
            panel.BuildArguments(out _));

        panel.Workers = "0";
        Assert.Null(panel.BuildArguments(out error));
        Assert.Equal("✗ Workers must be a whole number from 1 to 256.", error);
    }

    [Fact]
    public void Game_is_the_default_action_and_builds_arguments()
    {
        AmprPackPanelViewModel panel = new(Colors.YellowGreen, Sync());
        Assert.Equal("game", panel.Action.Value);
        Assert.Null(panel.BuildArguments(out string? error));
        Assert.Equal("✗ Game folder and output folder are both required.", error);

        panel.Root = "D:/game";
        panel.Output = "D:/games/out";
        Assert.Equal(Path.Combine("D:/games/out", "ampr_assets.index"), panel.Manifest);
        Assert.Null(panel.BuildArguments(out error));
        Assert.Equal("✗ Choose a TOML profile or a trace folder: without rules nothing is packed.", error);
        panel.Config = "D:/rules.toml";
        Assert.Equal(["ampr", "game", "--root", "D:/game", "--output", "D:/games/out", "--config", "D:/rules.toml"], panel.BuildArguments(out _));

        panel.Libs = "D:/emu";
        panel.Workers = "4";
        panel.SelfContained = true;
        panel.VerifyGame = false;
        panel.Exfat = true;
        Assert.Equal(
            ["ampr", "game", "--root", "D:/game", "--output", "D:/games/out", "--fakelib", "D:/emu", "--config", "D:/rules.toml", "--workers", "4",
                "--self-contained", "--skip-verify", "--exfat", Path.GetDirectoryName(Path.GetFullPath("D:/games/out"))!],
            panel.BuildArguments(out _));

        panel.ExfatPath = "E:/PPSA01234.exfat";
        Assert.Equal(["--exfat", "E:/PPSA01234.exfat"], panel.BuildArguments(out _)!.TakeLast(2));
    }

    [Fact]
    public void Other_actions_build_arguments_and_validate()
    {
        AmprPackPanelViewModel verify = Panel("verify");
        Assert.Null(verify.BuildArguments(out string? error));
        Assert.Equal("✗ Pack manifest is required.", error);
        verify.Manifest = "D:/out/ampr_assets.index";
        Assert.Equal(["ampr", "verify", "--index", "D:/out/ampr_assets.index"], verify.BuildArguments(out _));
        verify.Root = "D:/game";
        Assert.Equal(["ampr", "verify", "--index", "D:/out/ampr_assets.index", "--root", "D:/game"], verify.BuildArguments(out _));

        AmprPackPanelViewModel unpack = Panel("unpack");
        unpack.Manifest = "m.index";
        Assert.Null(unpack.BuildArguments(out error));
        Assert.Equal("✗ Pack manifest and output folder are both required.", error);
        unpack.Output = "D:/x";
        unpack.Overwrite = true;
        Assert.Equal(["ampr", "unpack", "--index", "m.index", "--output", "D:/x", "--overwrite"], unpack.BuildArguments(out _));

        AmprPackPanelViewModel list = Panel("list");
        list.Manifest = "m.index";
        list.Json = true;
        Assert.Equal(["ampr", "list", "--index", "m.index", "--json"], list.BuildArguments(out _));
        Assert.Equal(["ampr", "inspect", "--index", "m.index"], Panel("inspect") is var inspect && (inspect.Manifest = "m.index") is not null ? inspect.BuildArguments(out _) : null);

        AmprPackPanelViewModel runtime = Panel("runtime-config");
        runtime.Manifest = "m.index";
        Assert.Null(runtime.BuildArguments(out error));
        Assert.Equal("✗ Pack manifest and TOML configuration are both required.", error);
        runtime.Config = "r.toml";
        Assert.Equal(["ampr", "runtime-config", "--index", "m.index", "--config", "r.toml"], runtime.BuildArguments(out _));

        AmprPackPanelViewModel remove = Panel("remove-sources");
        remove.Manifest = "m.index";
        Assert.Null(remove.BuildArguments(out error));
        Assert.Equal("✗ Pack manifest and game folder are both required.", error);
        remove.Root = "D:/game";
        Assert.Equal(["ampr", "remove-sources", "--index", "m.index", "--root", "D:/game"], remove.BuildArguments(out _));
        remove.Confirm = true;
        Assert.Equal(["ampr", "remove-sources", "--index", "m.index", "--root", "D:/game", "--confirm"], remove.BuildArguments(out _));
    }

    [Theory]
    [InlineData("game", true, false, true, false, true, true, false, false, false)]
    [InlineData("pack", true, true, true, false, true, true, false, false, false)]
    [InlineData("verify", true, false, false, true, false, false, false, false, false)]
    [InlineData("unpack", false, false, true, true, false, false, true, false, false)]
    [InlineData("list", false, false, false, true, false, false, false, true, false)]
    [InlineData("inspect", false, false, false, true, false, false, false, false, false)]
    [InlineData("runtime-config", false, false, false, true, true, false, false, false, false)]
    [InlineData("remove-sources", true, false, false, true, false, false, false, false, true)]
    public void Each_action_shows_only_its_fields(
        string action, bool root, bool packPaths, bool output, bool manifest, bool config, bool packOptions, bool overwrite, bool json, bool confirm)
    {
        AmprPackPanelViewModel panel = Panel(action);
        Assert.Equal(
            (root, packPaths, output, manifest, config, packOptions, overwrite, json, confirm),
            (panel.ShowRoot, panel.ShowPackPaths, panel.ShowOutput, panel.ShowManifest, panel.ShowConfig, panel.ShowPackOptions,
                panel.ShowOverwrite, panel.ShowJson, panel.ShowConfirm));
        Assert.Equal(action == "game", panel.ShowGameOptions);
        Assert.Equal(action == "pack", panel.ShowAllowMissing);
        Assert.Equal(action == "pack", panel.ShowPackNote);
        Assert.False(panel.ShowExfatPath);
        panel.Exfat = true;
        Assert.Equal(action == "game", panel.ShowExfatPath);
    }

    [Fact]
    public void Page_asks_for_a_profile_or_traces()
    {
        AmprPackPanelViewModel panel = Panel("game");
        Assert.True(panel.ShowRulesMissing);
        Assert.False(panel.ShowRulesConfig);

        panel.Config = "rules.toml";
        Assert.False(panel.ShowRulesMissing);
        Assert.True(panel.ShowRulesConfig);

        panel.Config = string.Empty;
        panel.Traces = "traces";
        Assert.False(panel.ShowRulesMissing);

        panel.Action = panel.Actions.Single(a => a.Value == "verify");
        panel.Traces = string.Empty;
        Assert.False(panel.ShowRulesMissing || panel.ShowRulesConfig);
    }

    [Fact]
    public void Page_takes_rules_from_a_trace_folder()
    {
        using TempDir dir = new();
        AmprPackPanelViewModel panel = Panel("game");
        panel.Root = Directory.CreateDirectory(Path.Combine(dir.Path, "game")).FullName;
        panel.Output = Path.Combine(dir.Path, "out");
        string traces = Directory.CreateDirectory(Path.Combine(dir.Path, "traces")).FullName;

        panel.Traces = traces;
        Assert.True(panel.ShowNoTraces);
        Assert.False(panel.ShowRulesTraces || panel.ShowRulesMissing);

        // Runs copied in after the folder was picked are found when the page runs; hidden files count.
        dir.File("traces/startup/ampr_commands.bin");
        string index = dir.File("traces/startup/ampr_emu.index");
        File.SetAttributes(index, FileAttributes.Hidden);
        Assert.NotNull(panel.BuildArguments(out _));
        Assert.True(panel.ShowRulesTraces);
        Assert.StartsWith("✓ Rules from traces: 1 recorded run(s) found.", panel.RulesTracesText, StringComparison.Ordinal);

        dir.File("traces/level1/ampr_commands.bin");
        dir.File("traces/level1/ampr_emu.index");
        panel.Traces = traces + Path.DirectorySeparatorChar;
        Assert.True(panel.ShowRulesTraces);
        Assert.False(panel.ShowNoTraces);
        Assert.StartsWith("✓ Rules from traces: 2 recorded run(s) found.", panel.RulesTracesText, StringComparison.Ordinal);
        Assert.Equal(["--traces", traces + Path.DirectorySeparatorChar], panel.BuildArguments(out _)!.SkipWhile(a => a != "--traces").Take(2));

        Assert.DoesNotContain("--pack-untraced-types", panel.BuildArguments(out _)!);
        panel.UntracedTypes = true;
        Assert.Contains("--pack-untraced-types", panel.BuildArguments(out _)!);

        panel.Action = panel.Actions.Single(a => a.Value == "pack");
        Assert.Equal(["--traces", traces + Path.DirectorySeparatorChar], panel.BuildArguments(out _)!.SkipWhile(a => a != "--traces").Take(2));
        Assert.Contains("--pack-untraced-types", panel.BuildArguments(out _)!);

        panel.Config = "rules.toml";
        Assert.False(panel.ShowRulesTraces || panel.ShowRulesConfig);
        Assert.Null(panel.BuildArguments(out string? error));
        Assert.Equal("Use either a TOML file or a trace folder, not both.", error);
    }

    // Compress every file but executables, modules, system files and the libraries.
    private static string CompressAll(TempDir dir) => dir.File(
        "compress-all.toml",
        "[pack]\ndefault_action = \"compress\"\n[[rule]]\naction = \"loose\"\n"
            + "include = [\"eboot.bin\", \"*.prx\", \"*.sprx\", \"sce_sys/*\", \"sce_module/*\", \"fakelib/*\", \"ampr_emu.index\"]\n");

    [AvaloniaFact]
    public async Task Page_packs_with_a_toml_profile_and_logs_cli_progress()
    {
        using TempDir dir = new();
        string game = BuildPanelTests.Game(dir);
        dir.File("PPSA01234-app/data/level.dat", string.Concat(Enumerable.Repeat("terrain mesh texture ", 20_000)));
        dir.File("PPSA01234-app/sce_module/libc.prx", "module");
        AmprIndex.Build(game, Path.Combine(game, AmprIndex.IndexName));

        AmprPackPanelViewModel panel = Panel("pack");
        panel.Root = game;
        panel.Output = Path.Combine(dir.Path, "packs");
        panel.Config = CompressAll(dir);
        await panel.RunCommand.ExecuteAsync(null);

        Assert.Equal("✓ Completed successfully.", panel.Job.Lines[^1].Text);
        Assert.Contains(panel.Job.Lines, l => l.Text.Contains("\"files_packed\": 1", StringComparison.Ordinal));
        Assert.Contains(panel.Job.Lines, l => l.Text.StartsWith("[pack 100%] complete: ", StringComparison.Ordinal));
        // One overall phase: no per-phase "✓ planning: N%" lines that look like finished steps.
        Assert.DoesNotContain(panel.Job.Lines, l => l.Text.StartsWith("✓ planning", StringComparison.Ordinal) || l.Text.StartsWith("✓ packing", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Page_builds_a_playable_game_folder()
    {
        using TempDir dir = new();
        string game = BuildPanelTests.Game(dir);
        dir.File("PPSA01234-app/data/level.dat", string.Concat(Enumerable.Repeat("terrain mesh texture ", 20_000)));
        dir.File("emu/libSceAmpr.sprx", "AMPR Emu with AMPRPAK4 support");

        AmprPackPanelViewModel panel = new(Colors.YellowGreen, Sync());
        panel.Root = game;
        panel.Output = Path.Combine(dir.Path, "playable");
        panel.Libs = Path.Combine(dir.Path, "emu");
        panel.Config = CompressAll(dir);
        await panel.RunCommand.ExecuteAsync(null);

        Assert.True(panel.Job.Lines[^1].Text == "✓ Completed successfully.", string.Join('\n', panel.Job.Lines.Select(l => l.Text)));
        Assert.Contains(panel.Job.Lines, l => l.Text == "[5/5] Verifying");
        Assert.True(File.Exists(Path.Combine(panel.Output, "fakelib", "libSceAmpr.sprx")));
        Assert.True(File.Exists(Path.Combine(panel.Output, "ampr_emu.index")));
        Assert.True(File.Exists(panel.Manifest));
        Assert.False(File.Exists(Path.Combine(panel.Output, "data", "level.dat")));
    }

    [AvaloniaFact]
    public async Task Page_packs_verifies_and_plans_removal_for_a_real_folder()
    {
        using TempDir dir = new();
        string game = BuildPanelTests.Game(dir);
        dir.File("PPSA01234-app/data/level.dat", string.Concat(Enumerable.Repeat("terrain mesh texture ", 20_000)));
        AmprIndex.Build(game, Path.Combine(game, AmprIndex.IndexName));
        string config = dir.File("rules.toml", "[pack]\ndefault_action = \"loose\"\n[[rule]]\ninclude = \"data/**\"\n");

        AmprPackPanelViewModel panel = Panel("pack");
        panel.Root = game;
        panel.Output = Path.Combine(dir.Path, "packs");
        panel.Config = config;
        await panel.RunCommand.ExecuteAsync(null);
        Assert.Equal("✓ Completed successfully.", panel.Job.Lines[^1].Text);
        Assert.Contains(panel.Job.Lines, l => l.Text.Contains("\"files_packed\": 1", StringComparison.Ordinal));
        Assert.True(File.Exists(panel.Manifest));

        panel.Action = panel.Actions.Single(a => a.Value == "verify");
        await panel.RunCommand.ExecuteAsync(null);
        Assert.Equal("✓ Completed successfully.", panel.Job.Lines[^1].Text);
        Assert.Contains(panel.Job.Lines, l => l.Text.Contains("\"source_compare\"", StringComparison.Ordinal));

        panel.Action = panel.Actions.Single(a => a.Value == "remove-sources");
        await panel.RunCommand.ExecuteAsync(null);
        Assert.Equal("✓ Completed successfully.", panel.Job.Lines[^1].Text);
        Assert.Contains(panel.Job.Lines, l => l.Text.Contains("\"dry_run\": true", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(game, "data", "level.dat")));
    }
}
