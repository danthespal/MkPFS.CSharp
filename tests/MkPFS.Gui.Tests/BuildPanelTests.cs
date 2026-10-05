using Avalonia.Headless.XUnit;
using Avalonia.Media;
using MkPFS.Gui.Jobs;
using MkPFS.Gui.ViewModels;

namespace MkPFS.Gui.Tests;

[Collection(GuiCollection.Name)]
public sealed class BuildPanelTests
{
    // 1×1 PNG.
    internal static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private static JobRunner Sync() => new(action => action());

    /// <summary>A small game folder with param.json and a cover.</summary>
    internal static string Game(TempDir dir, string name = "PPSA01234-app")
    {
        dir.File($"{name}/sce_sys/param.json", """{"titleId": "PPSA01234", "contentId": "UP0001-PPSA01234_00-ASTROBOT00000000", "contentVersion": "01.004.000", "localizedParameters": {"en-US": {"titleName": "Astro Test"}}}""");
        File.WriteAllBytes(Path.Combine(dir.Path, name, "sce_sys", "icon0.png"), Png);
        dir.File($"{name}/eboot.bin", string.Concat(Enumerable.Repeat("eboot ", 20_000)));
        return Path.Combine(dir.Path, name);
    }

    [Fact]
    public void Pack_exfat_output_is_optional()
    {
        PackExfatPanelViewModel panel = new(Colors.Orange, Sync());

        Assert.Null(Build(panel, out string? error));
        Assert.Equal("✗ Source folder is required.", error);
        panel.Source = "D:/games/x";
        Assert.Equal(["pack", "exfat", "D:/games/x"], Build(panel, out _));
        panel.Output = "D:/x.exfat";
        panel.Overwrite = true;
        panel.Ampr.LibsDir = "L:/ampr";
        Assert.Equal(["pack", "exfat", "D:/games/x", "D:/x.exfat", "--overwrite", "--ampr-libs", "L:/ampr"], Build(panel, out _));
    }

    [Fact]
    public void Pack_file_builds_python_arguments()
    {
        PackFilePanelViewModel file = new(Colors.Cyan, Sync()) { Source = "D:/a.exfat" };
        Assert.Null(Build(file, out string? error));
        Assert.Equal("✗ Source file and output path are required.", error);
        file.Output = "D:/a.ffpfsc";
        file.Compress = false;
        file.TempFolder = "T:/";
        Assert.Equal(["pack", "file", "D:/a.exfat", "D:/a.ffpfsc", "--no-compress", "--temp-folder", "T:/"], Build(file, out _));
    }

    [Fact]
    public void Advanced_options_map_to_cli_flags()
    {
        PackExfatPanelViewModel exfat = new(Colors.Orange, Sync()) { Source = "D:/g", Output = "D:/g.exfat" };
        Assert.Equal("auto", exfat.ClusterSize.Value);
        exfat.ClusterSize = exfat.ClusterSizes.Single(c => c.Label == "32 MiB");
        exfat.Verbose = true;
        Assert.Equal(["pack", "exfat", "D:/g", "D:/g.exfat", "--cluster-size", "33554432", "--verbose"], Build(exfat, out _));

        PackFilePanelViewModel file = new(Colors.Cyan, Sync()) { Source = "D:/a.exfat", Output = "D:/a.ffpfsc" };
        file.Signed = file.DryRun = file.UseSpool = file.KeepInnerName = true;
        file.PFS.InodeBits = file.PFS.InodeWidths[1];
        Assert.Equal(
            ["pack", "file", "D:/a.exfat", "D:/a.ffpfsc", "--signed", "--dry-run", "--use-spool", "--no-rename-inner-image", "--inode-bits", "64"],
            Build(file, out _));
    }

    [Fact]
    public void Ampr_settings_map_to_cli_flags()
    {
        AmprSettingsViewModel ampr = new();
        List<string> args = [];
        ampr.ForceAprTitle = true; // needs a libs folder
        ampr.AppendTo(args);
        Assert.Empty(args);

        ampr.LibsDir = " L:/ampr ";
        ampr.KeepValidIndex = ampr.ForceRegen = true;
        ampr.AppendTo(args);
        Assert.Equal(["--ampr-libs", "L:/ampr", "--ampr-title", "--ampr-skip-regen-if-exists", "--ampr-force-regen"], args);

        args.Clear();
        ampr.GenerateIndex = false;
        ampr.AppendTo(args);
        Assert.Equal(["--ampr-libs", "L:/ampr", "--ampr-title", "--no-ampr-index"], args);
    }

    [AvaloniaFact]
    public void A_source_with_an_index_keeps_it_by_default()
    {
        using TempDir dir = new();
        string indexed = Game(dir, "indexed");
        dir.File("indexed/ampr_emu.index", "idx");
        string plain = Game(dir, "plain");

        PackExfatPanelViewModel exfat = new(Colors.Orange, Sync()) { Source = indexed };
        Assert.True(exfat.Ampr.HasExistingIndex);
        Assert.Equal(["pack", "exfat", indexed, exfat.Output, "--ampr-skip-regen-if-exists"], Build(exfat, out _));
        exfat.Source = plain; // the automatic choice follows the source
        Assert.False(exfat.Ampr.HasExistingIndex);
        Assert.False(exfat.Ampr.KeepValidIndex);
        PackExfatPanelViewModel chosen = new(Colors.Orange, Sync());
        chosen.Ampr.KeepValidIndex = true; // the user's choice stays
        chosen.Source = indexed;
        chosen.Source = plain;
        Assert.True(chosen.Ampr.KeepValidIndex);
    }

    [AvaloniaFact]
    public void Choosing_a_source_suggests_a_sanitized_output_once()
    {
        using TempDir dir = new();
        string game = Game(dir, "My Game (EU)!");
        string exfat = dir.File("Data [v2].exfat");

        PackExfatPanelViewModel image = new(Colors.Orange, Sync()) { Source = game + Path.DirectorySeparatorChar };
        Assert.Equal(Path.Combine(dir.Path, "My Game EU.exfat"), image.Output);
        image.Source = Path.Combine(dir.Path, "other");
        Assert.Equal(Path.Combine(dir.Path, "My Game EU.exfat"), image.Output); // a filled output is kept

        Assert.Equal(Path.Combine(dir.Path, "Data v2.ffpfsc"), new PackFilePanelViewModel(Colors.Cyan, Sync()) { Source = exfat }.Output);
        Assert.Equal(string.Empty, new PackFilePanelViewModel(Colors.Cyan, Sync()) { Source = game }.Output); // folder: not a file
        Assert.Equal(string.Empty, new PackExfatPanelViewModel(Colors.Orange, Sync()) { Source = Path.Combine(dir.Path, "missing") }.Output);
    }

    [Fact]
    public async Task Run_clears_the_previous_log()
    {
        PackExfatPanelViewModel panel = new(Colors.Orange, Sync());
        panel.Job.Append(new LogLine("old", LogTone.Normal));

        await panel.RunCommand.ExecuteAsync(null);

        Assert.Equal(["✗ Source folder is required."], panel.Job.Lines.Select(l => l.Text));
    }

    [AvaloniaFact]
    public async Task Pack_exfat_runs_end_to_end()
    {
        using TempDir dir = new();
        string game = Game(dir);
        PackExfatPanelViewModel panel = new(Colors.Orange, Sync()) { Source = game };

        await panel.RunCommand.ExecuteAsync(null);

        Assert.Equal($"$ mkpfs pack exfat {game} {Path.Combine(dir.Path, "PPSA01234-app.exfat")}", panel.Job.Lines[0].Text);
        Assert.Equal("✓ Completed successfully.", panel.Job.Lines[^1].Text);
        Assert.True(File.Exists(Path.Combine(dir.Path, "PPSA01234-app.exfat")));
    }

    private static IReadOnlyList<string>? Build(PanelViewModel panel, out string? error) => panel.BuildArguments(out error);
}
