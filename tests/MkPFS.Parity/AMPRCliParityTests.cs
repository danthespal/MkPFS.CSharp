using System.Text;
using System.Text.Json;
using MkPFS.Cli;
using MkPFS.Cli.Output;

namespace MkPFS.Parity;

/// <summary>
/// Replays every ampr_pack command recorded by <c>tools/oracle/build_ampr_goldens.py</c> through <c>mkpfs ampr</c>, in
/// the oracle's order and folder layout, and compares stdout, stderr and the exit code with each log.
/// </summary>
public sealed class AMPRCliParityTests
{
    private const long FixedMTime = 1_600_000_000;

    // Execution order in build_ampr_goldens.py (the manifest stores steps with sorted keys).
    private static readonly string[] StepOrder = ["pack", "verify", "list", "list_text", "inspect", "unpack", "remove_plan", "runtime_config"];

    public static TheoryData<string> Cases()
    {
        TheoryData<string> data = [];
        string? manifest = Fixtures.GeneratedRoot is { } root ? Path.Combine(root, "ampr", "goldens", "manifest.json") : null;
        if (manifest is null || !File.Exists(manifest))
        {
            data.Add("(corpus missing)");
            return data;
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(manifest));
        foreach (JsonProperty entry in document.RootElement.GetProperty("cases").EnumerateObject())
        {
            data.Add(entry.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_step_matches_the_oracle_log(string caseName)
    {
        string caseDir = Fixtures.PathOrSkip("ampr", "goldens", caseName);
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(Fixtures.PathOrSkip("ampr", "goldens", "manifest.json")));
        JsonElement entry = manifest.RootElement.GetProperty("cases").GetProperty(caseName);
        string work = Path.Combine(Path.GetTempPath(), "mkpfs-ampr-cli-" + Guid.NewGuid().ToString("N"));
        try
        {
            Prepare(caseDir, entry, work);
            JsonElement steps = entry.GetProperty("steps");
            Assert.All(steps.EnumerateObject(), step => Assert.Contains(step.Name, StepOrder));
            foreach (string stepName in StepOrder.Where(name => steps.TryGetProperty(name, out _)))
            {
                string[] argv = [.. steps.GetProperty(stepName).EnumerateArray().Select(a => a.GetString()!)];
                string expected = File.ReadAllText(Path.Combine(caseDir, stepName + ".log"), Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal);
                StringWriter stdout = new() { NewLine = "\n" };
                StringWriter stderr = new() { NewLine = "\n" };
                CliContext ctx = new(stdout, stderr, useColor: false, utf8: false, progress: true) { WorkingDirectory = work };
                int exit = MkPFSCli.Run(["ampr", .. argv], ctx);

                // The port warns when a pack run packs nothing (ampr_pack.py stays silent): exactly where the oracle
                // reported files_packed 0.
                string warning = Cli.Commands.AmprCommand.NothingPackedWarning + "\n";
                string errors = stderr.ToString();
                Assert.Equal(expected.Contains("\"files_packed\": 0,", StringComparison.Ordinal), errors.Contains(warning, StringComparison.Ordinal));
                errors = errors.Replace(warning, string.Empty, StringComparison.Ordinal);
                string actual = CliParityTests.Normalize(
                    $"$ ampr_pack.py {string.Join(' ', argv)}\nexit={exit}\n--- stdout\n{stdout}\n--- stderr\n{errors}", work);

                Assert.True(expected == actual, $"{caseName}/{stepName}:\n--- expected\n{expected}\n--- actual\n{actual}");
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    // The oracle's case folder: app0 (fixture copy, pinned mtimes, its index, then removals and touches) plus the
    // configuration files.
    private static void Prepare(string caseDir, JsonElement entry, string work)
    {
        string tree = Fixtures.PathOrSkip("ampr", "trees", "ampr_assets");
        string app0 = Path.Combine(work, "app0");
        foreach (string file in Directory.EnumerateFiles(tree, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(app0, Path.GetRelativePath(tree, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            File.SetLastWriteTimeUtc(target, DateTime.UnixEpoch.AddSeconds(FixedMTime));
        }

        File.Copy(Path.Combine(caseDir, "ampr_emu.index"), Path.Combine(app0, "ampr_emu.index"));
        foreach (JsonElement removed in entry.GetProperty("remove_before_pack").EnumerateArray())
        {
            File.Delete(Path.Combine(app0, removed.GetString()!));
        }

        foreach (JsonProperty touched in entry.GetProperty("touch_after_index").EnumerateObject())
        {
            File.SetLastWriteTimeUtc(Path.Combine(app0, touched.Name), DateTime.UnixEpoch.AddSeconds(touched.Value.GetInt64()));
        }

        foreach (string name in (string[])["config.toml", "list.txt", "runtime_alt.toml"])
        {
            if (File.Exists(Path.Combine(caseDir, name)))
            {
                File.Copy(Path.Combine(caseDir, name), Path.Combine(work, name));
            }
        }
    }
}
