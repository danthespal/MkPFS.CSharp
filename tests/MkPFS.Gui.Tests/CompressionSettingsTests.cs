using Avalonia.Media;
using MkPFS.Gui.Jobs;
using MkPFS.Gui.ViewModels;

namespace MkPFS.Gui.Tests;

[Collection(GuiCollection.Name)]
public sealed class CompressionSettingsTests
{
    // Auto passes every core of this machine (the CLI's own default leaves one free and stops at 16).
    internal static readonly string[] AutoCpu = ["--cpu-count", MkPFS.Core.Util.CpuTopology.PhysicalCores.ToString(System.Globalization.CultureInfo.InvariantCulture)];

    private static List<string> Args(CompressionSettingsViewModel settings, bool compress = true)
    {
        List<string> args = [];
        Assert.True(settings.AppendTo(args, compress, out string? error), error);
        return args;
    }

    [Fact]
    public void Defaults_add_only_the_core_count()
    {
        CompressionSettingsViewModel settings = new(allowAutoFit: true, offerSkipExecutables: true);

        Assert.Equal("balanced", settings.Preset.Value);
        Assert.Equal(AutoCpu, Args(settings));
    }

    [Fact]
    public void Cpu_picker_offers_auto_then_every_core()
    {
        CompressionSettingsViewModel settings = new(allowAutoFit: true, offerSkipExecutables: true);

        Assert.Equal(MkPFS.Core.Util.CpuTopology.PhysicalCores + 1, settings.CpuChoices.Count);
        Assert.Equal($"Auto ({MkPFS.Core.Util.CpuTopology.PhysicalCores} cores)", settings.CpuChoices[0].Label);
        Assert.Equal("1", settings.CpuChoices[1].Label);
        Assert.Same(settings.CpuChoices[0], settings.CpuChoice);

        settings.CpuChoice = settings.CpuChoices[1];
        Assert.Equal(1, settings.CpuCount);
        Assert.Equal("low_ram", settings.Preset.Value);
        settings.Preset = settings.Presets.Single(p => p.Value == "balanced");
        Assert.Same(settings.CpuChoices[0], settings.CpuChoice);
    }

    [Theory]
    [InlineData("fast", new[] { "--compression-level", "1" }, true)]
    [InlineData("balanced", new string[0], true)]
    [InlineData("max", new[] { "--compression-level", "9" }, true)]
    [InlineData("low_ram", new[] { "--cpu-count", "1" }, false)]
    public void Presets_set_level_and_cores(string preset, string[] expected, bool autoCpu)
    {
        CompressionSettingsViewModel settings = new(allowAutoFit: true, offerSkipExecutables: true);

        settings.Preset = settings.Presets.Single(p => p.Value == preset);

        Assert.Equal(autoCpu ? [.. expected, .. AutoCpu] : expected, Args(settings));
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
            [.. AutoCpu, "--threshold-gain", "5", "--max-compressed-ratio", "90", "--min-compress-size", "32768", "--skip-executable-compression", "--block-size", "auto-fit"],
            Args(settings));

        // Without compression only the block size still applies.
        Assert.Equal(["--block-size", "auto-fit"], Args(settings, compress: false));
    }

    [Fact]
    public void Auto_fit_and_the_executables_option_can_be_left_out()
    {
        CompressionSettingsViewModel batch = new(allowAutoFit: false, offerSkipExecutables: true);
        CompressionSettingsViewModel file = new(allowAutoFit: true, offerSkipExecutables: false) { SkipExecutables = true };

        Assert.DoesNotContain(batch.BlockSizes, b => b.Value == "auto-fit");
        Assert.Equal(["auto", "4096", "8192", "16384", "32768", "65536", "131072", "262144", "524288", "1048576", "2097152"], batch.BlockSizes.Select(b => b.Value));
        Assert.Equal("64 KiB", batch.BlockSizes.Single(b => b.Value == "65536").Label);
        Assert.Equal(AutoCpu, Args(file));
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
        Assert.Equal(["pack", "file", "D:/a.exfat", "D:/a.ffpfsc", "--compression-level", "1", .. AutoCpu], file.BuildArguments(out _));

        file.Compression.CpuCount = 2;
        Assert.Equal(["pack", "file", "D:/a.exfat", "D:/a.ffpfsc", "--compression-level", "1", "--cpu-count", "2"], file.BuildArguments(out _));

        file.Compression.MinCompressSize = "x";
        Assert.Null(file.BuildArguments(out string? error));
        Assert.Equal("✗ Store Files Smaller Than must be a whole number of bytes.", error);
    }
}
