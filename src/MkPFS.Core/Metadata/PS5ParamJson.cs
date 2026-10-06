using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MkPFS.Core.Metadata;

/// <summary>
/// Minimal <c>sce_sys/param.json</c> for a package source that has none. The field set is the one
/// Publishing Tools accepts as input (<c>tools/oracle-fpkg</c> fixtures); keys are sorted and lines end
/// with LF so the bytes do not depend on the platform. A supplied param.json is always used verbatim.
/// </summary>
public static partial class PS5ParamJson
{
    /// <summary><c>applicationDrmType</c> values.</summary>
    public static IReadOnlyList<string> DrmTypes { get; } = ["free", "standard", "freemium"];

    /// <summary>Generate the document.</summary>
    /// <param name="contentId">36-character content id (title id at offset 7).</param>
    /// <param name="title">Title shown on the console; the title id when empty.</param>
    /// <param name="version">Master version <c>NN.NN</c>.</param>
    /// <param name="drmType">One of <see cref="DrmTypes"/>.</param>
    /// <returns>UTF-8 bytes without BOM.</returns>
    /// <exception cref="ArgumentException">Invalid content id, version or DRM type.</exception>
    public static byte[] Create(string contentId, string? title, string version, string drmType)
    {
        if (contentId.Length != 36 || !ContentIdPattern().IsMatch(contentId))
        {
            throw new ArgumentException($"content id must look like UP9000-PPSA00000_00-ABCDEFGHIJKLMNOP: {contentId}", nameof(contentId));
        }

        if (!VersionPattern().IsMatch(version))
        {
            throw new ArgumentException($"version must be NN.NN: {version}", nameof(version));
        }

        if (!DrmTypes.Contains(drmType))
        {
            throw new ArgumentException($"applicationDrmType must be one of {string.Join(", ", DrmTypes)}: {drmType}", nameof(drmType));
        }

        string titleId = contentId.Substring(7, 9);
        using MemoryStream stream = new();
        using (Utf8JsonWriter json = new(stream, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            json.WriteStartObject();
            json.WriteNumber("applicationCategoryType", 0);
            json.WriteString("applicationDrmType", drmType);
            json.WriteNumber("attribute", 0);
            json.WriteNumber("attribute2", 0);
            json.WriteNumber("attribute3", 0);
            json.WriteString("conceptId", titleId[4..]);
            json.WriteString("contentId", contentId);
            json.WriteString("contentVersion", ContentVersion(version));
            json.WriteNumber("downloadDataSize", 0);
            json.WriteStartObject("localizedParameters");
            json.WriteString("defaultLanguage", "en-US");
            json.WriteStartObject("en-US");
            json.WriteString("titleName", string.IsNullOrWhiteSpace(title) ? titleId : title);
            json.WriteEndObject();
            json.WriteEndObject();
            json.WriteString("masterVersion", version);
            json.WriteString("requiredSystemSoftwareVersion", "0x0000000000000000");
            json.WriteString("sdkVersion", "0x0000000000000000");
            json.WriteString("titleId", titleId);
            json.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    /// <summary><c>NN.NN</c> → <c>NN.NN0.000</c> (<c>01.00</c> → <c>01.000.000</c>).</summary>
    /// <param name="version">Master version.</param>
    /// <returns>Content version.</returns>
    public static string ContentVersion(string version) => $"{version}0.000";

    /// <summary>Read <c>contentVersion</c> from a param.json document.</summary>
    /// <param name="paramJson">Document bytes.</param>
    /// <returns>The value, or null when absent or malformed.</returns>
    public static string? ReadContentVersion(byte[] paramJson) => ReadString(paramJson, "contentVersion");

    /// <summary>Read a top-level string field (<c>contentId</c>, <c>masterVersion</c>, ...) from a param.json document.</summary>
    /// <param name="paramJson">Document bytes.</param>
    /// <param name="name">Field name.</param>
    /// <returns>The value, or null when absent or malformed.</returns>
    public static string? ReadString(byte[] paramJson, string name)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(paramJson.AsMemory(paramJson.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? 3 : 0));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(name, out JsonElement v)
                && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether <paramref name="contentId"/> has the 36-character content id shape.</summary>
    /// <param name="contentId">Candidate.</param>
    /// <returns>True when valid.</returns>
    public static bool IsContentId(string? contentId) => contentId is { Length: 36 } && ContentIdPattern().IsMatch(contentId);

    [GeneratedRegex("^[A-Z]{2}[0-9]{4}-[A-Z]{4}[0-9]{5}_[0-9]{2}-[A-Z0-9]{16}$")]
    private static partial Regex ContentIdPattern();

    [GeneratedRegex("^[0-9]{2}\\.[0-9]{2}$")]
    private static partial Regex VersionPattern();
}
