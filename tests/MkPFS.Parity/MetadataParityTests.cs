using System.Security.Cryptography;
using System.Text.Json;
using MkPFS.Core.Metadata;

namespace MkPFS.Parity;

/// <summary>Python <c>read_game_metadata</c> results for every golden image and its source folder.</summary>
public sealed class MetadataParityTests
{
    public static TheoryData<string, string> Cases()
    {
        TheoryData<string, string> data = [];
        if (Fixtures.GeneratedRoot is null || !Directory.Exists(Path.Combine(Fixtures.GeneratedRoot, "goldens")))
        {
            data.Add("(corpus missing)", "-");
            return data;
        }

        foreach (string caseDir in Directory.EnumerateDirectories(Path.Combine(Fixtures.GeneratedRoot, "goldens")).Order(StringComparer.Ordinal))
        {
            foreach (string record in new[] { "metadata.json", "metadata_src.json" })
            {
                if (File.Exists(Path.Combine(caseDir, record)))
                {
                    data.Add(Path.GetFileName(caseDir), record);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Metadata_matches_python(string caseName, string record)
    {
        string caseDir = Fixtures.PathOrSkip("goldens", caseName);
        using JsonDocument expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(caseDir, record)));
        JsonElement e = expected.RootElement;
        string target;
        bool exactName = true;
        if (record == "metadata.json")
        {
            target = Path.Combine(caseDir, e.GetProperty("file_name").GetString()!);
        }
        else
        {
            // CI ships the case src only for AMPR cases; the fixture tree has the same files under another name.
            target = Path.Combine(caseDir, "src");
            if (!Directory.Exists(target))
            {
                target = Path.Combine(Fixtures.GeneratedRoot!, "trees", CliParityTests.TreeOf(caseName));
                exactName = false;
            }
        }

        GameMetadata actual = GameMetadataReader.Read(target);

        if (exactName)
        {
            Assert.Equal(e.GetProperty("file_name").GetString(), actual.FileName);
        }

        Assert.Equal(e.GetProperty("file_size").GetInt64(), actual.FileSize);
        Assert.Equal(e.GetProperty("game_title").GetString(), actual.GameTitle);
        Assert.Equal(e.GetProperty("content_id").GetString(), actual.ContentId);
        Assert.Equal(e.GetProperty("title_id").GetString(), actual.TitleId);
        Assert.Equal(e.GetProperty("package_type").GetString(), actual.PackageType);
        Assert.Equal(e.GetProperty("version").GetString(), actual.Version);
        Assert.Equal(e.GetProperty("region").GetString(), actual.Region);
        Assert.Equal(e.GetProperty("has_apr_emu").GetBoolean(), actual.HasAprEmu);
        Assert.Equal(e.GetProperty("size_display").GetString(), actual.SizeDisplay);
        Assert.Equal(e.GetProperty("error").GetString(), actual.Error);
        string? icon = e.GetProperty("icon_sha256").ValueKind == JsonValueKind.Null ? null : e.GetProperty("icon_sha256").GetString();
        Assert.Equal(icon, actual.IconBytes is null ? null : Convert.ToHexStringLower(SHA256.HashData(actual.IconBytes)));
    }
}
