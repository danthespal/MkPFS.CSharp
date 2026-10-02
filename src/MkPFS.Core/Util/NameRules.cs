using System.Text;

namespace MkPFS.Core.Util;

/// <summary>File-name rules shared by scanners and builders (port of Python <c>mkpfs/utils.py</c>).</summary>
public static class NameRules
{
    /// <summary>
    /// OS-generated metadata never packed into an image. Matched case-insensitively against the
    /// base name; AppleDouble forks (<c>._*</c>) are matched by prefix in <see cref="IsIgnoredName"/>.
    /// </summary>
    public static readonly IReadOnlySet<string> IgnoredNames = new HashSet<string>(StringComparer.Ordinal)
    {
        // macOS
        ".ds_store",
        ".spotlight-v100",
        ".trashes",
        ".fseventsd",
        ".temporaryitems",
        ".documentrevisions-v100",
        ".apdisk",
        "__macosx",
        ".volumeicon.icns",

        // Windows
        "thumbs.db",
        "ehthumbs.db",
        "desktop.ini",
        "$recycle.bin",
        "system volume information",
    };

    // Characters ui_sanitize_basename replaces with a space.
    private const string UiProblemChars = "()[]{}*&^%$#@!±§|\\/:;\"'<>,?";

    /// <summary>Return whether <paramref name="name"/> is OS metadata to exclude from images.</summary>
    /// <param name="name">A single path component.</param>
    /// <returns><see langword="true"/> for known macOS/Windows metadata and <c>._*</c> forks.</returns>
    public static bool IsIgnoredName(string name)
    {
        if (name.StartsWith("._", StringComparison.Ordinal))
        {
            return true;
        }

        return IgnoredNames.Contains(name.ToLowerInvariant());
    }

    /// <summary>
    /// Replace every code point that is not Python-alphanumeric or one of <c>._-</c> with <c>_</c>
    /// (Python <c>_sanitize_name_component</c>).
    /// </summary>
    /// <param name="name">Name component.</param>
    /// <returns>Filesystem-safe name.</returns>
    public static string SanitizeNameComponent(string name)
    {
        StringBuilder builder = new(name.Length);
        foreach (Rune rune in name.EnumerateRunes())
        {
            bool keep = PythonText.IsAlnum(rune) || rune.Value is '.' or '_' or '-';
            if (keep)
            {
                builder.Append(rune.ToString());
            }
            else
            {
                builder.Append('_');
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Sanitize a GUI-generated output basename: problematic characters become spaces, whitespace
    /// runs collapse to one space, and the result is trimmed. Spaces are kept on purpose.
    /// </summary>
    /// <param name="name">Proposed name.</param>
    /// <returns>Sanitized name, or empty.</returns>
    public static string UiSanitizeBasename(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        StringBuilder builder = new(name.Length);
        foreach (char c in name)
        {
            builder.Append(UiProblemChars.Contains(c) ? ' ' : c);
        }

        return PythonText.Strip(string.Join(' ', PythonText.SplitWhitespace(builder.ToString())));
    }
}
