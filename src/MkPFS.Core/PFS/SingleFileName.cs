using System.Text;
using System.Text.RegularExpressions;

namespace MkPFS.Core.PFS;

/// <summary>Inner file name for single-file images (port of Python <c>resolve_single_file_inner_name</c>, <c>pfs.py:824</c>).</summary>
public static partial class SingleFileName
{
    [GeneratedRegex(@"(?<![A-Z0-9])([A-Z]{4}\d{5})(?![A-Z0-9])")]
    private static partial Regex ModernTitleId();

    [GeneratedRegex(@"([A-Z]{4}\d{5})_\d{2}(?:-[A-Z0-9]+)?")]
    private static partial Regex LongFormTitleId();

    [GeneratedRegex(@"(?<![A-Z0-9-])([A-Z]{4}-\d{5})(?![A-Z0-9-])")]
    private static partial Regex LegacyTitleId();

    /// <summary>
    /// Safe inner name: the title ID found in the stem (PPSA12345, PPSA12345_00-..., CUSA-12345), else the stem
    /// reduced to ASCII letters/digits with collapsed <c>_</c>/<c>-</c> separators (max 15 chars), plus all
    /// suffixes in lower case. An empty stem falls back to <paramref name="fallbackStem"/>.
    /// </summary>
    /// <param name="sourceName">Original file name.</param>
    /// <param name="rename">When <see langword="false"/>, return <paramref name="sourceName"/> unchanged.</param>
    /// <param name="fallbackStem">Stem used when nothing usable remains (Python: 15 random hex chars).</param>
    /// <returns>Inner file name.</returns>
    public static string Resolve(string sourceName, bool rename = true, Func<string>? fallbackStem = null)
    {
        if (!rename)
        {
            return sourceName;
        }

        List<string> suffixes = Suffixes(sourceName);
        string joined = string.Concat(suffixes);
        string extension = joined.ToLowerInvariant();
        string stem = suffixes.Count > 0 ? sourceName[..^joined.Length] : sourceName;

        string? titleId = PreferredTitleId(stem);
        if (titleId is not null)
        {
            return titleId + extension;
        }

        StringBuilder collapsed = new();
        char? pending = null;
        foreach (char c in stem)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                if (pending is not null && collapsed.Length > 0)
                {
                    collapsed.Append(pending.Value);
                }

                collapsed.Append(c);
                pending = null;
            }
            else if (c == ' ')
            {
                pending = '_';
            }
            else if (c is '_' or '-')
            {
                pending = c;
            }
        }

        string safe = collapsed.Length > 15 ? collapsed.ToString(0, 15) : collapsed.ToString();
        if (safe.Length == 0)
        {
            safe = (fallbackStem ?? DefaultFallback)();
        }

        return safe + extension;
    }

    /// <summary>Python <c>PurePath.suffixes</c> for a file name.</summary>
    /// <param name="name">File name.</param>
    /// <returns>Suffixes including the dots.</returns>
    public static List<string> Suffixes(string name)
    {
        if (name.EndsWith('.'))
        {
            return [];
        }

        string trimmed = name.TrimStart('.');
        string[] parts = trimmed.Split('.');
        return [.. parts.Skip(1).Select(p => "." + p)];
    }

    private static string? PreferredTitleId(string stem)
    {
        foreach (Regex regex in new[] { ModernTitleId(), LongFormTitleId(), LegacyTitleId() })
        {
            Match match = regex.Match(stem);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        return null;
    }

    private static string DefaultFallback() => Guid.NewGuid().ToString("N").ToUpperInvariant()[..15];
}
