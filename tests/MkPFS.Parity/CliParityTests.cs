using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MkPFS.Cli;
using MkPFS.Cli.Output;

namespace MkPFS.Parity;

/// <summary>
/// Replays every recorded Python <c>inspect</c>/<c>tree</c>/<c>verify</c> command through the C# CLI and compares
/// the complete output (stdout, stderr, exit code) with the oracle log.
/// </summary>
public sealed partial class CliParityTests
{
    private static readonly string[] LogNames = ["inspect.log", "tree.log", "tree_deep.log", "verify.log"];

    [GeneratedRegex(@"^\s*\[[#-]+\]\s+\d+%")]
    private static partial Regex ProgressLine();

    // Python prints its own name and URL; this port prints MkPFS.C# and its repository on purpose.
    [GeneratedRegex(@"(?:MkPFS \S+ - https://github\.com/PSBrew/MkPFS|MkPFS\.C# \S+ - https://github\.com/danthespal/MkPFS\.CSharp)")]
    private static partial Regex TitleLine();

    // A path below the case folder, raw or JSON-escaped: <CASE>\out.ffpfs, <CASE>\\out.ffpfs, <CASE>/out.ffpfs.
    [GeneratedRegex(@"<CASE>(?:(?:\\\\|\\|/)[^\\/\s""]+)+")]
    private static partial Regex CasePath();

    public static TheoryData<string, string> Logs()
    {
        TheoryData<string, string> data = [];
        if (Fixtures.GeneratedRoot is null || !File.Exists(Path.Combine(Fixtures.GeneratedRoot, "goldens", "manifest.json")))
        {
            data.Add("(corpus missing)", "-");
            return data;
        }

        foreach (string caseDir in Directory.EnumerateDirectories(Path.Combine(Fixtures.GeneratedRoot, "goldens")).Order(StringComparer.Ordinal))
        {
            foreach (string log in LogNames)
            {
                if (File.Exists(Path.Combine(caseDir, log)))
                {
                    data.Add(Path.GetFileName(caseDir), log);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Logs))]
    public void CLI_output_matches_python(string caseName, string logName)
    {
        string caseDir = Fixtures.PathOrSkip("goldens", caseName);
        string tree = TreeOf(caseName);
        // Python wrote the logs in text mode, so they carry the platform's line endings.
        string expected = File.ReadAllText(Path.Combine(caseDir, logName), Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal);
        string[] argv = expected[..expected.IndexOf('\n', StringComparison.Ordinal)]["$ mkpfs ".Length..].Split(' ');

        // Relative paths resolve against the case folder. "src" is the case's own source copy (packing may
        // have added files such as ampr_emu.index), else the pristine fixture tree.
        string caseSource = Path.Combine(caseDir, "src");
        string source = Directory.Exists(caseSource) ? caseSource : Path.Combine(Fixtures.GeneratedRoot!, "trees", tree);
        string[] resolved = [.. argv.Select(arg => arg == "src"
            ? source
            : File.Exists(Path.Combine(caseDir, arg)) || Directory.Exists(Path.Combine(caseDir, arg)) ? Path.Combine(caseDir, arg) : arg)];

        StringWriter stdout = new() { NewLine = "\n" };
        StringWriter stderr = new() { NewLine = "\n" };
        int exit = MkPFSCli.Run(resolved, new CliContext(stdout, stderr, useColor: false, utf8: false, progress: true));
        string actual = Normalize($"$ mkpfs {string.Join(' ', argv)}\nexit={exit}\n--- stdout\n{stdout}\n--- stderr\n{stderr}", caseDir);

        // verify adds a stream-check warning Python does not have: required for the ISA-L image, absent for zlib ones.
        string streamCheck = "WARN " + Cli.Commands.ReadCommands.StreamCheckPrefix;
        List<string> lines = [.. actual.Split('\n')];
        bool hasStreamCheck = lines.RemoveAll(line => line.StartsWith(streamCheck, StringComparison.Ordinal)) > 0;
        Assert.Equal(logName == "verify.log" && caseName.Contains("isal", StringComparison.Ordinal), hasStreamCheck);
        actual = string.Join('\n', lines);

        // The oracle corpus is recorded on Windows; compare paths below the case folder separator-neutral.
        Assert.Equal(NeutralSeparators(expected), NeutralSeparators(actual));
    }

    internal static string NeutralSeparators(string text) =>
        CasePath().Replace(text, match => match.Value.Replace(@"\\", "/", StringComparison.Ordinal).Replace('\\', '/'));

    internal static string TreeOf(string caseName)
    {
        using JsonDocument manifest = Fixtures.Manifest();
        return manifest.RootElement.GetProperty("cases").GetProperty(caseName).GetProperty("tree").GetString()!;
    }

    // Same normalization as tools/oracle/build_goldens.py: case folder -> <CASE>, progress lines dropped,
    // Python splitlines() semantics; plus the version in the banner (Python 1.0.0 vs this port).
    internal static string Normalize(string text, string caseDir)
    {
        string root = Path.GetFullPath(caseDir);
        text = text.Replace(root.Replace("\\", "\\\\", StringComparison.Ordinal), "<CASE>", StringComparison.Ordinal)
            .Replace(root, "<CASE>", StringComparison.Ordinal);
        text = TitleLine().Replace(text, "MkPFS 1.0.0 - https://github.com/PSBrew/MkPFS");
        IEnumerable<string> lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split(['\n', '\r'])
            .Where(line => !ProgressLine().IsMatch(line));
        List<string> list = [.. lines];
        if (list.Count > 0 && list[^1].Length == 0)
        {
            list.RemoveAt(list.Count - 1);
        }

        return string.Join('\n', list) + "\n";
    }
}
