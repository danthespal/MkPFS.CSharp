using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MkPFS.Cli;
using MkPFS.Cli.Output;

namespace MkPFS.Parity;

/// <summary>Replays every Python <c>pack exfat</c> build: identical image bytes and identical console output.</summary>
public sealed class PackExfatParityTests
{
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
            if (entry.Value.GetProperty("kind").GetString() == "exfat")
            {
                data.Add(entry.Name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Pack_exfat_matches_python(string caseName)
    {
        string caseDir = Fixtures.PathOrSkip("goldens", caseName);
        string expected = File.ReadAllText(Path.Combine(caseDir, "build.log"), Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal);
        string[] argv = expected[..expected.IndexOf('\n', StringComparison.Ordinal)]["$ mkpfs ".Length..].Split(' ');

        // The case's own src may be absent (CI ships it only for AMPR cases); the fixture tree has the same content.
        string caseSource = Path.Combine(caseDir, "src");
        string source = Directory.Exists(caseSource) ? caseSource : Path.Combine(Fixtures.GeneratedRoot!, "trees", CliParityTests.TreeOf(caseName));
        string outDir = Path.Combine(Path.GetTempPath(), $"mkpfs-pack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);
        try
        {
            string[] resolved = [.. argv.Select(arg => arg switch
            {
                "src" => source,
                "out.exfat" => Path.Combine(outDir, "out.exfat"),
                _ => arg,
            })];
            StringWriter stdout = new() { NewLine = "\n" };
            StringWriter stderr = new() { NewLine = "\n" };
            int exit = MkPFSCli.Run(resolved, new CliContext(stdout, stderr, useColor: false, utf8: false, progress: true));
            string raw = $"$ mkpfs {string.Join(' ', argv)}\nexit={exit}\n--- stdout\n{stdout}\n--- stderr\n{stderr}"
                .Replace(Path.GetFullPath(source), Path.Combine(caseDir, "src"), StringComparison.Ordinal)
                .Replace(outDir, Path.GetFullPath(caseDir), StringComparison.Ordinal);
            string actual = CliParityTests.Normalize(raw, caseDir);

            Assert.Equal(CliParityTests.NeutralSeparators(expected), CliParityTests.NeutralSeparators(actual));
            using JsonDocument manifest = Fixtures.Manifest();
            string expectedHash = manifest.RootElement.GetProperty("cases").GetProperty(caseName).GetProperty("sha256").GetProperty("out.exfat").GetString()!;
            using FileStream image = File.OpenRead(Path.Combine(outDir, "out.exfat"));
            Assert.Equal(expectedHash, Convert.ToHexStringLower(SHA256.HashData(image)));
        }
        finally
        {
            Directory.Delete(outDir, recursive: true);
        }
    }
}
