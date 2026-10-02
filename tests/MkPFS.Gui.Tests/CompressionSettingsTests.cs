using Avalonia.Media;
using MkPFS.Gui.Jobs;
using MkPFS.Gui.ViewModels;

namespace MkPFS.Gui.Tests;

[Collection(GuiCollection.Name)]
public sealed class CompressionSettingsTests
{
    private static List<string> Args(CompressionSettingsViewModel settings, bool compress = true)
    {
        List<string> args = [];
        Assert.True(settings.AppendTo(args, compress, out string? error), error);
        return args;
    }

    [Fact]
    public void Defaults_add_no_arguments()
    {
        CompressionSettingsViewModel settings = new(allowAutoFit: true, offerSkipExecutables: true);

        Assert.Equal("balanced", settings.Preset.Value);
        Assert.Empty(Args(settings));
    }

    [Theory]
    [InlineData("fast", new[] { "--compression-level", "1" })]
    [InlineData("balanced", new string[0])]
    [InlineData("max", new[] { "--compression-level", "9" })]
    [InlineData("low_ram", new[] { "--cpu-count", "1" })]
    public void Presets_set_level_and_cores(string preset, string[] expected)
    {
        CompressionSettingsViewModel settings = new(allowAutoFit: true, offerSkipExecutables: true);

        settings.Preset = settings.Presets.Single(p => p.Value == preset);

        Assert.Equal(expected, Args(settings));
    }

    [Fact]
    public void Editing_values_selects_the_matching_preset_or_custom()
    {
        CompressionSettingsViewModel settings = new(allowAutoFit: true, offerSkipExecutables: true);

        settings.Level = 9;
        Assert.Equal("max", settings.Preset.Value);
        settings.CpuCount = 4;
        Assert.Equal("custom", settings.Preset.Value);
        Assert.Equal(["--compression-level", "9", "--cpu-count", "4"], Args(settings));
        settings.Level = 7;
        settings.CpuCount = 1;
        Assert.Equal("low_ram", settings.Preset.Value);
    }

    [Fact]
    public void Block_size_and_keep_rules_become_arguments()
    {
        CompressionSettingsViewModel settings = new(allowAutoFit: true, offerSkipExecutables: true)
        {
            ThresholdGain = 5,
            MaxRatio = 90,
            MinCompressSize = " 32768 ",
            SkipExecutables = true,
        };
        settings.BlockSize = settings.BlockSizes.Single(b => b.Value == "auto-fit");

        Assert.Equal(
            ["--threshold-gain", "5", "--max-compressed-ratio", "90", "--min-compress-size", "32768", "--skip-executable-compression", "--block-size", "auto-fit"],
            Args(settings));

        // Without compression only the block size still applies.
        Assert.Equal(["--block-size", "auto-fit"], Args(settings, compress: false));
    }

    [Fact]
    public void Batch_offers_no_auto_fit_and_files_no_executables_option()
    {
        CompressionSettingsViewModel batch = new(allowAutoFit: false, offerSkipExecutables: true);
        CompressionSettingsViewModel file = new(allowAutoFit: true, offerSkipExecutables: false) { SkipExecutables = true };

        Assert.DoesNotContain(batch.BlockSizes, b => b.Value == "auto-fit");
        Assert.Equal(["auto", "4096", "8192", "16384", "32768", "65536", "131072", "262144", "524288", "1048576", "2097152"], batch.BlockSizes.Select(b => b.Value));
        Assert.Equal("64 KiB", batch.BlockSizes.Single(b => b.Value == "65536").Label);
        Assert.Empty(Args(file));
    }

    [Fact]
    public void An_invalid_minimum_size_is_reported()
    {
        CompressionSettingsViewModel settings = new(allowAutoFit: true, offerSkipExecutables: false) { MinCompressSize = "64k" };

        Assert.False(settings.AppendTo([], compress: true, out string? error));
        Assert.Equal("✗ Store Files Smaller Than must be a whole number of bytes.", error);
    }

    [Fact]
    public void Pages_append_the_tuning_to_their_arguments()
    {
        JobRunner sync = new(action => action());
        PackFilePanelViewModel file = new(Colors.Cyan, sync) { Source = "D:/a.exfat", Output = "D:/a.ffpfsc" };
        file.Compression.Preset = file.Compression.Presets.Single(p => p.Value == "fast");
        Assert.Equal(["pack", "file", "D:/a.exfat", "D:/a.ffpfsc", "--compression-level", "1"], file.BuildArguments(out _));

        BatchPanelViewModel batch = new(Colors.Teal, sync) { Source = "D:/in", Output = "D:/out" };
        batch.Compression.CpuCount = 2;
        Assert.Equal(["batch", "D:/in", "D:/out", "--cpu-count", "2"], batch.BuildArguments(out _));

        PackFolderPanelViewModel folder = new(Colors.Blue, sync) { Source = "D:/g", Output = "D:/g.ffpfsc" };
        folder.Compression.MinCompressSize = "x";
        Assert.Null(folder.BuildArguments(out string? error));
        Assert.Equal("✗ Store Files Smaller Than must be a whole number of bytes.", error);
    }
}
