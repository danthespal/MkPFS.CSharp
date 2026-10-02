using Avalonia.Headless.XUnit;
using Avalonia.Media;
using MkPFS.Cli;
using MkPFS.Cli.Output;
using MkPFS.Gui.Jobs;
using MkPFS.Gui.ViewModels;

namespace MkPFS.Gui.Tests;

[Collection(GuiCollection.Name)]
public sealed class CheckPanelTests
{
    private static JobRunner Sync() => new(action => action());

    private static string Image(TempDir dir)
    {
        string game = BuildPanelTests.Game(dir);
        string image = Path.Combine(dir.Path, "game.ffpfsc");
        Assert.Equal(0, MkPFSCli.Run(["pack", "folder", game, image, "--cpu-count", "1"], new CliContext(TextWriter.Null, TextWriter.Null, false, false, false)));
        return image;
    }

    [Fact]
    public void Verify_builds_python_arguments()
    {
        VerifyPanelViewModel panel = new(Colors.Green, Sync());
        Assert.Null(panel.BuildArguments(out string? error));
        Assert.Equal("✗ Image path is required.", error);

        panel.Image = "D:/a.ffpfsc";
        Assert.Equal(["verify", "D:/a.ffpfsc"], panel.BuildArguments(out _));
        panel.SourceDir = "D:/src";
        panel.Crc32 = "7F528D1F";
        panel.Sha256 = new string('a', 64);
        panel.Ekpfs = new string('0', 64);
        panel.NewCrypt = true;
        Assert.Equal(
            ["verify", "D:/a.ffpfsc", "--source-dir", "D:/src", "--expect-crc32", "7F528D1F", "--expect-manifest-sha256", new string('a', 64),
                "--ekpfs-key", new string('0', 64), "--new-crypt"],
            panel.BuildArguments(out _));
    }

    [Fact]
    public void Inspect_and_tree_build_python_arguments()
    {
        InspectPanelViewModel inspect = new(Colors.Purple, Sync());
        Assert.Null(inspect.BuildArguments(out string? error));
        Assert.Equal("✗ Image path is required.", error);
        inspect.Image = "D:/a.ffpfs";
        Assert.Equal(["inspect", "D:/a.ffpfs", "--format", "text"], inspect.BuildArguments(out _));
        inspect.Format = "json";
        inspect.Ekpfs = "00";
        Assert.Equal(["inspect", "D:/a.ffpfs", "--format", "json", "--ekpfs-key", "00"], inspect.BuildArguments(out _));

        TreePanelViewModel tree = new(Colors.Orange, Sync()) { Image = "D:/a.ffpfsc" };
        Assert.Equal(["tree", "D:/a.ffpfsc", "--deep"], tree.BuildArguments(out _));
        tree.Image = "D:/game";
        Assert.Equal(["tree", "D:/game", "--deep"], tree.BuildArguments(out _));
        tree.Image = "D:/data.EXFAT";
        tree.NewCrypt = true;
        Assert.Equal(["tree", "D:/data.EXFAT", "--new-crypt"], tree.BuildArguments(out _)); // a bare exFAT has no inner image
    }

    [AvaloniaFact]
    public void Unpack_suggests_a_folder_and_avoids_existing_ones()
    {
        using TempDir dir = new();
        string image = dir.File("Game [EU].ffpfsc");
        UnpackPanelViewModel panel = new(Colors.Pink, Sync());
        Assert.Null(panel.BuildArguments(out string? error));
        Assert.Equal("✗ Image and output folder are both required.", error);

        panel.Image = image;
        Assert.Equal(Path.Combine(dir.Path, "Game EU"), panel.Output);
        Assert.Equal(["unpack", image, Path.Combine(dir.Path, "Game EU")], panel.BuildArguments(out _));

        // An existing folder gets a subfolder named after the image, unless --overwrite.
        panel.Output = dir.Path;
        Assert.Equal(["unpack", image, Path.Combine(dir.Path, "Game EU")], panel.BuildArguments(out _));
        Assert.Equal($"→ Output subfolder: {Path.Combine(dir.Path, "Game EU")}", panel.Job.Lines[^1].Text);
        panel.Overwrite = true;
        panel.Ekpfs = "ab";
        Assert.Equal(["unpack", image, dir.Path, "--overwrite", "--ekpfs-key", "ab"], panel.BuildArguments(out _));
    }

    [AvaloniaFact]
    public async Task Pages_verify_inspect_list_and_unpack_a_real_image()
    {
        using TempDir dir = new();
        string image = Image(dir);

        VerifyPanelViewModel verify = new(Colors.Green, Sync()) { Image = image };
        await verify.RunCommand.ExecuteAsync(null);
        Assert.Equal("✓ Completed successfully.", verify.Job.Lines[^1].Text);

        InspectPanelViewModel inspect = new(Colors.Purple, Sync()) { Image = image, Format = "json" };
        await inspect.RunCommand.ExecuteAsync(null);
        Assert.Equal("✓ Completed successfully.", inspect.Job.Lines[^1].Text);
        Assert.Contains(inspect.Job.Lines, l => l.Text.TrimStart().StartsWith('{'));

        TreePanelViewModel tree = new(Colors.Orange, Sync()) { Image = image };
        await tree.RunCommand.ExecuteAsync(null);
        Assert.Equal("✓ Completed successfully.", tree.Job.Lines[^1].Text);
        Assert.Contains(tree.Job.Lines, l => l.Text.Contains("eboot.bin", StringComparison.Ordinal));

        UnpackPanelViewModel unpack = new(Colors.Pink, Sync()) { Image = image };
        await unpack.RunCommand.ExecuteAsync(null);
        Assert.Equal("✓ Completed successfully.", unpack.Job.Lines[^1].Text);
        Assert.Equal(Path.Combine(dir.Path, "game"), unpack.Output);
        Assert.NotEmpty(Directory.EnumerateFiles(unpack.Output, "*.exfat")); // like Python, the GUI unpacks without --deep
    }
}
