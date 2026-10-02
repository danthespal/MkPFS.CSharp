using System.Text.Json;

namespace MkPFS.Core.Util;

/// <summary><c>sce_sys/param.json</c> helpers (port of Python <c>mkpfs/utils.py</c>).</summary>
public static class GameParams
{
    /// <summary>Parse a game <c>param.json</c> file.</summary>
    /// <param name="path">JSON file path.</param>
    /// <returns>Parsed document; the caller disposes it.</returns>
    /// <exception cref="InvalidDataException">The file cannot be read or is not valid JSON.</exception>
    public static JsonDocument ReadParamJson(string path)
    {
        try
        {
            return JsonDocument.Parse(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException($"Failed to parse {path}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Return the trimmed <c>titleId</c> (or <c>title_id</c>) from <c>sce_sys/param.json</c>, or
    /// <see langword="null"/> when missing, blank, not a string, or unreadable.
    /// </summary>
    /// <param name="sourceRoot">Source tree root.</param>
    /// <returns>Title ID or <see langword="null"/>.</returns>
    public static string? TitleIdFromSource(string sourceRoot)
    {
        string paramJson = Path.Combine(sourceRoot, "sce_sys", "param.json");
        if (!File.Exists(paramJson))
        {
            return null;
        }

        try
        {
            using JsonDocument document = ReadParamJson(paramJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // Python: parsed.get("titleId") or parsed.get("title_id") — falls back only when falsy.
            JsonElement value = default;
            bool found = document.RootElement.TryGetProperty("titleId", out value) && !IsFalsy(value);
            if (!found && document.RootElement.TryGetProperty("title_id", out JsonElement alternate))
            {
                value = alternate;
                found = true;
            }

            if (!found || value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string trimmed = PythonText.Strip(value.GetString() ?? string.Empty);
            return trimmed.Length > 0 ? trimmed : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Base name (no extension) for an image built from <paramref name="sourceRoot"/>: the title ID
    /// when present, else the folder name, sanitized; never empty (Python <c>default_image_basename</c>).
    /// </summary>
    /// <param name="sourceRoot">Source tree root.</param>
    /// <returns>Filesystem-safe base name.</returns>
    public static string DefaultImageBasename(string sourceRoot)
    {
        string folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot)));
        string baseName = TitleIdFromSource(sourceRoot) ?? (folderName.Length > 0 ? folderName : "image");
        string sanitized = NameRules.SanitizeNameComponent(baseName);
        return sanitized.Length > 0 ? sanitized : "image";
    }

    /// <summary>Python truthiness of a JSON value (<c>not value</c>).</summary>
    internal static bool IsFalsy(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.False or JsonValueKind.Undefined => true,
        JsonValueKind.String => element.GetString()!.Length == 0,
        JsonValueKind.Number => element.GetDouble() == 0,
        JsonValueKind.Array => element.GetArrayLength() == 0,
        JsonValueKind.Object => !element.EnumerateObject().Any(),
        _ => false,
    };
}
