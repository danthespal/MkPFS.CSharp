using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using MkPFS.Gui.Controls;
using MkPFS.Gui.Jobs;
using MkPFS.Gui.ViewModels;
using MkPFS.Repair;
using static MkPFS.Tests.Repair.RepairImageFactory;

namespace MkPFS.Gui.Tests;

[Collection(GuiCollection.Name)]
public sealed class RepairPanelTests
{
    private static JobRunner Sync() => new(action => action());

    /// <summary>Five blocks: zlib, risky (far distance), raw, risky, zlib.</summary>
    internal static string RiskyImage(TempDir dir)
    {
        (byte[] risky1, _) = FarDistance(Text(11, 32768));
        (byte[] risky3, _) = FarDistance(Noise(13)[..32768]);
        string path = Path.Combine(dir.Path, "game.ffpfsc");
        File.WriteAllBytes(path, BuildImage([ZlibBlock(Text(1)), risky1, Noise(2), risky3, ZlibBlock(Text(4))], 5L * BlockSize));
        return path;
    }

    [Fact]
    public void Repair_builds_cli_arguments()
    {
        RepairPanelViewModel panel = new(Colors.Red, Sync());
        Assert.Null(panel.BuildArguments(out string? error));
        Assert.Equal("✗ Image path is required.", error);

        panel.Image = "D:/a.ffpfsc";
        Assert.Equal(["repair", "D:/a.ffpfsc"], panel.BuildArguments(out _));
        panel.BadBlocks = "D:/bad_blocks.tsv";
        panel.Recompress = true;
        panel.Mode = "copy";
        panel.ReportDir = "D:/report";
        panel.CleanSlack = false;
        Assert.Equal(
            ["repair", "D:/a.ffpfsc", "--bad-blocks", "D:/bad_blocks.tsv", "--recompress", "--mode", "copy", "--report-dir", "D:/report", "--no-slack-cleanup"],
            panel.BuildArguments(out _));
    }

    [AvaloniaFact]
    public async Task Scan_logs_the_cli_report_and_draws_the_block_map()
    {
        using TempDir dir = new();
        string image = RiskyImage(dir);
        RepairPanelViewModel panel = new(Colors.Red, Sync()) { Image = image };

        await panel.ScanCommand.ExecuteAsync(null);

        Assert.Equal($"$ mkpfs repair {image} --scan", panel.Job.Lines[0].Text);
        Assert.Contains(panel.Job.Lines, l => l.Text == "Risky:   2 block(s)");
        Assert.Contains(panel.Job.Lines, l => l.Tone == LogTone.Warning && l.Text.Contains("2 block(s) need repair: 1, 3", StringComparison.Ordinal));
        Assert.Equal("✓ Completed successfully.", panel.Job.Lines[^1].Text); // exit 3 is a scan result, not a failure

        PFSCBlockMap map = Assert.IsType<PFSCBlockMap>(panel.Map);
        Assert.Equal(
            [BlockState.Zlib, BlockState.Risky, BlockState.Raw, BlockState.Risky, BlockState.Zlib],
            Enumerable.Range(0, 5).Select(i => BlockMap.StateOf(map, i)));
        Assert.Equal("5 blocks · 4 compressed · 2 risky", panel.MapSummary);
        Assert.Equal(32768, map.MaxDistances[1]);

        panel.Selection = new BlockRange(1, 1);
        Assert.Equal("Block 1", panel.DetailsTitle);
        Assert.Equal("Max distance: 32,768 (limit 32,506)", panel.Details[^1]);
        Assert.EndsWith("(risky on PS5)", panel.Details[1], StringComparison.Ordinal);

        panel.Selection = new BlockRange(0, 2);
        Assert.Equal("Blocks 0–2", panel.DetailsTitle);
        Assert.Equal(["1 zlib · 1 raw · 1 risky on PS5", "Block 1:"], panel.Details.Take(2));
    }

    [AvaloniaFact]
    public async Task Repair_fixes_the_blocks_and_clears_the_stale_map()
    {
        using TempDir dir = new();
        string image = RiskyImage(dir);
        RepairPanelViewModel panel = new(Colors.Red, Sync()) { Image = image };

        await panel.RunCommand.ExecuteAsync(null);

        Assert.Equal("✓ Completed successfully.", panel.Job.Lines[^1].Text);
        Assert.Contains(panel.Job.Lines, l => l.Text.StartsWith("Repaired 2 block(s)", StringComparison.Ordinal));
        Assert.Null(panel.Map);

        await panel.ScanCommand.ExecuteAsync(null);
        Assert.Contains(panel.Job.Lines, l => l.Text == "No blocks need repair.");
        Assert.Equal(0, panel.Map!.Risky.Count(r => r));
    }

    [AvaloniaFact]
    public async Task Block_map_groups_blocks_and_selects_cells()
    {
        using TempDir dir = new();
        RepairPanelViewModel panel = new(Colors.Red, Sync()) { Image = RiskyImage(dir) };
        await panel.ScanCommand.ExecuteAsync(null);
        BlockMap control = new() { Map = panel.Map };
        BlockRange? picked = null;
        control.CellSelected += (_, range) => picked = range;

        control.Measure(new Size(BlockMap.Pitch * 2, double.PositiveInfinity)); // 2 columns × 24 rows: one block per cell
        Assert.Equal(1, control.BlocksPerCell);
        Assert.True(control.SelectAt(new Point(BlockMap.Pitch + 1, BlockMap.Pitch + 1)));
        Assert.Equal(new BlockRange(3, 3), picked);
        Assert.False(control.SelectAt(new Point(1, BlockMap.Pitch * 3 + 1)));

        control.Measure(new Size(BlockMap.Pitch * 2, double.PositiveInfinity));
        Assert.Equal(new BlockRange(4, 4), control.RangeOf(4));
    }

    [AvaloniaFact]
    public void Large_images_group_blocks_per_cell_with_the_worst_state()
    {
        // 100 columns × 24 rows = 2,400 cells for 10,000 blocks: 5 blocks per cell.
        PFSCBlockMap map = SyntheticMap(10_000, risky: [7_777]);
        BlockMap control = new() { Map = map };

        control.Measure(new Size(BlockMap.Pitch * 100, double.PositiveInfinity));

        Assert.Equal(5, control.BlocksPerCell);
        Assert.Equal(new BlockRange(7_775, 7_779), control.RangeOf(1_555));
        Assert.Equal(BlockMap.Pitch * 20, control.DesiredSize.Height); // 2,000 cells / 100 columns
    }

    // Compressed blocks of 30,000 bytes each, with the given risky blocks.
    private static PFSCBlockMap SyntheticMap(int blocks, int[] risky)
    {
        long[] offsets = [.. Enumerable.Range(0, blocks + 1).Select(i => i * 30_000L)];
        bool[] riskyFlags = new bool[blocks];
        foreach (int block in risky)
        {
            riskyFlags[block] = true;
        }

        return new PFSCBlockMap(offsets, new int[blocks], riskyFlags, new bool[blocks]);
    }
}
