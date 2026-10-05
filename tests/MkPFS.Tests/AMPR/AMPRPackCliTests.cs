using MkPFS.Build;
using MkPFS.Build.AMPRPack;
using MkPFS.Cli;
using MkPFS.Cli.Output;
using MkPFS.Core.AMPR;

namespace MkPFS.Tests.AMPR;

/// <summary>The rules options of <c>ampr pack</c> and <c>ampr game</c> and their warnings (extensions over ampr_pack.py).</summary>
public sealed class AMPRPackCliTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mkpfs-ampr-cli-" + Guid.NewGuid().ToString("N"));

    public AMPRPackCliTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // Compress every file but the ones remove-sources protects (the generic profile shared outside MkPFS).
    private const string CompressAll = """
        [pack]
        default_action = "compress"

        [[rule]]
        action = "loose"
        include = ["eboot.bin", "*.prx", "*.sprx", "sce_sys/*", "fakelib/*", "fakelib2/*", "ampr_emu.index"]
        """;

    private string Profile()
    {
        string path = Path.Combine(_dir, "profile.toml");
        File.WriteAllText(path, CompressAll);
        return path;
    }

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

    [Fact]
    public void Cli_profile_packs_and_the_removal_plan_accepts_it()
    {
        string root = Game();
        string output = Path.Combine(_dir, "out");

        (int exit, string stdout, string stderr) = Run(
            "pack", "--root", root, "--ampr-index", Path.Combine(root, AmprIndex.IndexName), "--output", output, "--config", Profile());

        Assert.Equal(0, exit);
        Assert.Contains("\"files_packed\": 1,", stdout, StringComparison.Ordinal);
        Assert.Equal(string.Empty, stderr);
        Assert.Equal(["data/a.dat"], AMPRPackMaintenance.RemovalPlan(Path.Combine(output, "ampr_assets.index"), root).Paths);
    }

    [Fact]
    public void Cli_warns_when_nothing_is_packed_and_rejects_config_with_traces()
    {
        string root = Game();
        string index = Path.Combine(root, AmprIndex.IndexName);
        string output = Path.Combine(_dir, "out");

        (int exit, string stdout, string stderr) = Run("pack", "--root", root, "--ampr-index", index, "--output", output);
        Assert.Equal(0, exit);
        Assert.Contains("\"files_packed\": 0,", stdout, StringComparison.Ordinal);
        Assert.Equal(global::MkPFS.Cli.Commands.AmprCommand.NothingPackedWarning + "\n", stderr);

        (exit, stdout, stderr) = Run("pack", "--root", root, "--ampr-index", index, "--output", output, "--config", Profile(), "--traces", _dir);
        Assert.Equal((2, string.Empty, "error: --traces cannot be used with --config\n"), (exit, stdout, stderr));
    }

    [Fact]
    public void Cli_game_needs_a_profile_or_traces()
    {
        (int exit, string stdout, string stderr) = Run("game", "--root", Game(), "--output", Path.Combine(_dir, "out"));

        Assert.Equal(2, exit);
        Assert.Equal(global::MkPFS.Cli.Commands.AmprCommand.ExperimentalWarning + "\n", stdout);
        Assert.EndsWith("error: " + global::MkPFS.Cli.Commands.AmprCommand.NoRulesError + "\n", stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_dir, "out")));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("fakelib", false)]
    [InlineData("fakelib2", false)]
    public void Cli_warns_when_packs_are_built_for_a_folder_without_ampr_emu(string? emulator, bool warns)
    {
        string root = Game(emulator);

        (int exit, _, string stderr) = Run(
            "pack", "--root", root, "--ampr-index", Path.Combine(root, AmprIndex.IndexName), "--output", Path.Combine(_dir, "out"), "--config", Profile());

        Assert.Equal(0, exit);
        Assert.Equal(warns ? global::MkPFS.Cli.Commands.AmprCommand.NoEmulatorWarning + "\n" : string.Empty, stderr);
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
