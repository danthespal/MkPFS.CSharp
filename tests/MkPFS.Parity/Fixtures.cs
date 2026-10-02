using System.Text.Json;

namespace MkPFS.Parity;

/// <summary>Locates the Phase 0 oracle corpus in <c>tests/fixtures/generated</c>.</summary>
internal static class Fixtures
{
    private const string RegenerateHint =
        "oracle corpus missing; run: uv run --project ../MkPFS python tools/oracle/build_goldens.py";

    /// <summary>Absolute path of <c>tests/fixtures/generated</c>, or <see langword="null"/> when absent.</summary>
    public static string? GeneratedRoot { get; } = FindGeneratedRoot();

    /// <summary>Return a path inside the corpus, or skip the test when the corpus is missing.</summary>
    public static string PathOrSkip(params string[] parts)
    {
        if (GeneratedRoot is null)
        {
            Assert.Skip(RegenerateHint);
        }

        string path = Path.Combine([GeneratedRoot, .. parts]);
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            Assert.Skip($"{RegenerateHint} (missing {path})");
        }

        return path;
    }

    /// <summary>Load <c>goldens/manifest.json</c>.</summary>
    public static JsonDocument Manifest() =>
        JsonDocument.Parse(File.ReadAllBytes(PathOrSkip("goldens", "manifest.json")));

    private static string? FindGeneratedRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MkPFS.slnx")))
            {
                string generated = Path.Combine(dir.FullName, "tests", "fixtures", "generated");
                return Directory.Exists(generated) ? generated : null;
            }
        }

        return null;
    }
}
