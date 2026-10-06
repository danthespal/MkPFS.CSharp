using System.Text.Json;

namespace MkPFS.Parity;

/// <summary>The PS5PkgTool goldens (<c>tools/oracle-ppt</c>) and the fixture trees they were built from.</summary>
internal static class PPTCorpus
{
    /// <summary>Content id the goldens use.</summary>
    public const string ContentId = "UP9000-PPSA99999_00-MKPFSORACLE00000";

    /// <summary>Passcode the goldens use.</summary>
    public const string Passcode = "00000000000000000000000000000000";

    /// <summary>Seed the goldens use.</summary>
    public static byte[] Seed => Convert.FromHexString("000102030405060708090a0b0c0d0e0f");

    /// <summary>Clock PS5PkgTool fixes for a seeded build (1781638585.35).</summary>
    public static DateTimeOffset Time => DateTimeOffset.FromUnixTimeSeconds(1781638585).AddTicks(3_500_000);

    /// <summary>Trees with a <c>stored</c> golden; "(missing)" when the corpus is absent.</summary>
    /// <returns>Theory data.</returns>
    public static TheoryData<string> StoredTrees() => Trees(_ => true);

    /// <summary>Stored trees MkPFS builds (it refuses <c>app_unicode</c>'s non-ASCII paths).</summary>
    /// <returns>Theory data.</returns>
    public static TheoryData<string> BuildableTrees() => Trees(tree => tree != "app_unicode");

    /// <summary>Source folder of a tree.</summary>
    /// <param name="tree">Tree name.</param>
    /// <returns>Path.</returns>
    public static string TreeDir(string tree)
    {
        foreach (string kind in new[] { "trees", "sdk-trees", "ppt-trees" })
        {
            string dir = Path.Combine(Fixtures.GeneratedRoot ?? string.Empty, "fpkg", kind, tree);
            if (Directory.Exists(dir))
            {
                return dir;
            }
        }

        return Fixtures.PathOrSkip("fpkg", "trees", tree);
    }

    /// <summary>PS5PkgTool's stored package of a tree.</summary>
    /// <param name="tree">Tree name.</param>
    /// <returns>Path.</returns>
    public static string StoredPackage(string tree) => Fixtures.PathOrSkip("fpkg", "ppt", tree + "_stored", "out.pkg");

    private static TheoryData<string> Trees(Func<string, bool> filter)
    {
        TheoryData<string> data = [];
        string? root = Fixtures.GeneratedRoot;
        string manifest = root is null ? string.Empty : Path.Combine(root, "fpkg", "ppt", "manifest.json");
        if (!File.Exists(manifest))
        {
            data.Add("(missing)");
            return data;
        }

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(manifest));
        foreach (JsonProperty c in doc.RootElement.GetProperty("cases").EnumerateObject())
        {
            if (c.Name.EndsWith("_stored", StringComparison.Ordinal) && c.Value.GetProperty("exit").GetInt32() == 0
                && c.Name[..^"_stored".Length] is var tree && filter(tree))
            {
                data.Add(tree);
            }
        }

        return data;
    }
}
