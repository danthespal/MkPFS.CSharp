using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MkPFS.Build;
using MkPFS.Cli;
using MkPFS.Cli.Output;

namespace MkPFS.Parity;

/// <summary>Replays every Python <c>pack</c> build: identical image bytes and identical console output.</summary>
public sealed partial class PackParityTests
{
    // Builders ported so far, with the output file each kind writes.
    private static readonly Dictionary<string, string> Outputs = new(StringComparer.Ordinal)
    {
        ["exfat"] = "out.exfat",
        ["file"] = "out.ffpfsc",
        ["folder"] = "out.ffpfsc",
        ["raw"] = "out.ffpfs",
    };

    // file_app_isal: ISA-L streams cannot be reproduced (the port always uses zlib).
    // raw_non_ascii: Python crashes with a traceback; the port reports the same message (see PackRawTests).
    private static readonly HashSet<string> Excluded = new(StringComparer.Ordinal) { "file_app_isal", "raw_non_ascii" };

    static PackParityTests()
    {
        // The oracle pins time.time() to this epoch; images embed it in the header and inodes.
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
            if (Outputs.ContainsKey(entry.Value.GetProperty("kind").GetString()!) && !Excluded.Contains(entry.Name))
            {
                data.Add(entry.Name);
            }
        }

        return data;
    }

    // Machine-specific lines: the temp folder, the auto CPU count, and timings (Python's pinned clock makes them 0).
    [GeneratedRegex(@"^(  Temp folder:       |  CPU cores:         |  Elapsed time:            |  Throughput:              ).*$", RegexOptions.Multiline)]
    private static partial Regex VolatileLine();

    [GeneratedRegex(@" using \d+ CPU cores?\.\.\.$", RegexOptions.Multiline)]
    private static partial Regex CpuCores();

    [Theory]
    [MemberData(nameof(Cases))]
    public void Pack_matches_python(string caseName)
    {
        string caseDir = Fixtures.PathOrSkip("goldens", caseName);
        using JsonDocument manifest = Fixtures.Manifest();
        JsonElement entry = manifest.RootElement.GetProperty("cases").GetProperty(caseName);
        string outputName = Outputs[entry.GetProperty("kind").GetString()!];
        string expected = File.ReadAllText(Path.Combine(caseDir, "build.log"), Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal);
        string[] argv = expected[..expected.IndexOf('\n', StringComparison.Ordinal)]["$ mkpfs ".Length..].Split(' ');

        // The case's own src may be absent (CI ships it only for AMPR cases); the fixture tree has the same content.
        string caseSource = Path.Combine(caseDir, "src");
        string source = Directory.Exists(caseSource) ? caseSource : Path.Combine(Fixtures.GeneratedRoot!, "trees", CliParityTests.TreeOf(caseName));
        string outDir = Path.Combine(Path.GetTempPath(), $"mkpfs-pack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);
        try
        {
            // pack folder regenerates ampr_emu.index in the source, and the index stores file mtimes: pack a copy whose
            // mtimes are restored from the index Python wrote (artifacts and checkouts do not keep them).
            string pythonIndex = Path.Combine(source, AmprIndex.IndexName);
            if (File.Exists(pythonIndex) && entry.GetProperty("steps").GetProperty("build")[1].GetString() == "folder")
            {
                string copy = Path.Combine(outDir, "src");
                CopyTree(source, copy);
                foreach (AmprIndex.Row row in AmprIndex.ReadRows(File.ReadAllBytes(pythonIndex)))
                {
                    File.SetLastWriteTimeUtc(Path.Combine(copy, row.Path["/app0/".Length..]), DateTime.UnixEpoch.AddSeconds(row.MTime));
                }

                source = copy;
            }

            string[] resolved = [.. argv.Select(arg => arg switch
            {
                "src" => source,
                "in.exfat" => Path.Combine(caseDir, arg),
                _ when arg == outputName => Path.Combine(outDir, arg),
                _ => arg,
            })];
            StringWriter stdout = new() { NewLine = "\n" };
            StringWriter stderr = new() { NewLine = "\n" };
            int exit = MkPFSCli.Run(resolved, new CliContext(stdout, stderr, useColor: false, utf8: false, progress: true));
            string raw = $"$ mkpfs {string.Join(' ', argv)}\nexit={exit}\n--- stdout\n{stdout}\n--- stderr\n{stderr}"
                .Replace(Path.GetFullPath(source), Path.Combine(caseDir, "src"), StringComparison.Ordinal)
                .Replace(outDir, Path.GetFullPath(caseDir), StringComparison.Ordinal);
            string actual = CliParityTests.Normalize(raw, caseDir);

            Assert.Equal(Neutral(expected), Neutral(actual));
            string expectedHash = entry.GetProperty("sha256").GetProperty(outputName).GetString()!;
            using FileStream image = File.OpenRead(Path.Combine(outDir, outputName));
            Assert.Equal(expectedHash, Convert.ToHexStringLower(SHA256.HashData(image)));
        }
        finally
        {
            Directory.Delete(outDir, recursive: true);
        }
    }

    private static void CopyTree(string from, string to)
    {
        foreach (string dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        }

        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
        }
    }

    private static string Neutral(string text) =>
        CliParityTests.NeutralSeparators(CpuCores().Replace(VolatileLine().Replace(text, "$1<varies>"), " using <n> CPU cores..."));
}
