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

    [Fact]
    public void Unknown_preset_is_rejected() =>
        Assert.Equal("unknown preset: max", Assert.Throws<AMPRPackException>(() => AMPRPackConfig.LoadPreset("max")).Message);

    private string Game()
    {
        string root = Path.Combine(_dir, "app0");
        foreach ((string path, string text) in new[] { ("eboot.bin", "eboot"), ("sce_sys/param.json", "{}"), ("lib.prx", "prx"), ("data/a.dat", new string('a', 50_000)) })
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

    [Fact]
    public void Cli_preset_packs_and_the_removal_plan_accepts_it()
    {
        string root = Game();
        string output = Path.Combine(_dir, "out");

        (int exit, string stdout, string stderr) = Run(
            "pack", "--root", root, "--ampr-index", Path.Combine(root, AmprIndex.IndexName), "--output", output, "--preset", "default");

        Assert.Equal(0, exit);
        Assert.Contains("\"files_packed\": 1,", stdout, StringComparison.Ordinal);
        Assert.Empty(stderr);
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
}
