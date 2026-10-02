using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MkPFS.Cli;
using MkPFS.Cli.Output;

namespace MkPFS.Parity;

/// <summary>Replays the Python <c>batch</c> runs (convert, rerun with skips, dry run): same output and images.</summary>
public sealed partial class BatchParityTests
{
    static BatchParityTests()
    {
        Environment.SetEnvironmentVariable("SOURCE_DATE_EPOCH", "1700000000");
    }

    public static TheoryData<string> Cases()
    {
        TheoryData<string> data = [];
        if (Fixtures.GeneratedRoot is null || !File.Exists(Path.Combine(Fixtures.GeneratedRoot, "goldens", "manifest.json")))
        {
            data.Add("(corpus missing)");
            return data;
        }

        using JsonDocument manifest = Fixtures.Manifest();
        foreach (JsonProperty entry in manifest.RootElement.GetProperty("cases").EnumerateObject())
        {
            if (entry.Value.GetProperty("kind").GetString() == "batch")
            {
                data.Add(entry.Name);
            }
        }

        return data;
    }

    // Same as build_goldens.py BATCH_TIMING (wall-clock times), plus the pre-stats version line: Python always
    // prints PS4 there (oracle finding 14).
    [GeneratedRegex(@"((?:  in |^Total elapsed: ))\d+\.\ds$", RegexOptions.Multiline)]
    private static partial Regex Timing();

    [GeneratedRegex(@"^  Version : PS[45]$", RegexOptions.Multiline)]
    private static partial Regex VersionLine();

    [Theory]
    [MemberData(nameof(Cases))]
    public void Batch_matches_python(string caseName)
    {
        string caseDir = Fixtures.PathOrSkip("goldens", caseName);
        using JsonDocument manifest = Fixtures.Manifest();
        JsonElement entry = manifest.RootElement.GetProperty("cases").GetProperty(caseName);
        string work = Path.Combine(Path.GetTempPath(), $"mkpfs-batch-{Guid.NewGuid():N}");
        try
        {
            CopyTree(Path.Combine(caseDir, "batch"), Path.Combine(work, "batch"));
            foreach (string step in new[] { "build", "rerun", "dry" })
            {
                string[] argv = [.. entry.GetProperty("steps").GetProperty(step).EnumerateArray().Select(a => a.GetString()!)];
                string[] resolved = [.. argv.Select((arg, i) => i is 1 or 2 ? Path.Combine(work, arg) : arg)]; // source_dir, output_dir
                StringWriter stdout = new() { NewLine = "\n" };
                StringWriter stderr = new() { NewLine = "\n" };
                int exit = MkPFSCli.Run(resolved, new CliContext(stdout, stderr, useColor: false, utf8: false, progress: true));
                string raw = $"$ mkpfs {string.Join(' ', argv)}\nexit={exit}\n--- stdout\n{stdout}\n--- stderr\n{stderr}".Replace(work, Path.GetFullPath(caseDir), StringComparison.Ordinal);
                string expected = File.ReadAllText(Path.Combine(caseDir, $"{step}.log"), Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal);

                Assert.Equal(Neutral(expected), Neutral(CliParityTests.Normalize(raw, caseDir)));
            }

            foreach (JsonProperty hash in entry.GetProperty("sha256").EnumerateObject().Where(h => h.Name.StartsWith("out/", StringComparison.Ordinal)))
            {
                using FileStream image = File.OpenRead(Path.Combine(work, hash.Name));
                Assert.Equal(hash.Value.GetString(), Convert.ToHexStringLower(SHA256.HashData(image)));
            }
        }
        finally
        {
            if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }
        }
    }

    private static string Neutral(string text) =>
        CliParityTests.NeutralSeparators(VersionLine().Replace(Timing().Replace(text, "$1<t>s"), "  Version : <profile>"));

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        }

        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
        }
    }
}
