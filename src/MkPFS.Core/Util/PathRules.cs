namespace MkPFS.Core.Util;

/// <summary>Path helpers with Python <c>pathlib</c> semantics (port of Python <c>mkpfs/utils.py</c>).</summary>
public static class PathRules
{
    /// <summary>
    /// Python <c>PurePath.suffix</c>: text from the last dot of the file name, empty for dot-files
    /// (<c>.bashrc</c>) and names ending in a dot.
    /// </summary>
    /// <param name="path">Path or file name.</param>
    /// <returns>Suffix including the dot, or empty.</returns>
    public static string Suffix(string path)
    {
        string name = Path.GetFileName(path);
        int dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 ? name[dot..] : string.Empty;
    }

    /// <summary>Python <c>PurePath.with_suffix</c>.</summary>
    /// <param name="path">Original path.</param>
    /// <param name="suffix">New suffix including the dot.</param>
    /// <returns>Path with the suffix replaced or appended.</returns>
    public static string WithSuffix(string path, string suffix)
    {
        string name = Path.GetFileName(path);
        if (name.Length == 0)
        {
            throw new ArgumentException($"path has an empty name: {path}", nameof(path));
        }

        string current = Suffix(name);
        string stem = name[..(name.Length - current.Length)];
        string directory = path[..(path.Length - name.Length)];
        return directory + stem + suffix;
    }

    /// <summary>
    /// Replace the output suffix when <paramref name="adjust"/> is set and the current suffix
    /// differs (case-insensitive) from <paramref name="desiredSuffix"/> (Python <c>normalize_output_path</c>).
    /// </summary>
    /// <param name="path">User-provided output path.</param>
    /// <param name="desiredSuffix">Wanted suffix including the dot.</param>
    /// <param name="adjust">When <see langword="false"/>, return the path unchanged.</param>
    /// <returns>The normalized path and whether it changed.</returns>
    public static (string Path, bool Changed) NormalizeOutputPath(string path, string desiredSuffix, bool adjust = true)
    {
        if (!adjust || string.Equals(Suffix(path), desiredSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return (path, false);
        }

        return (WithSuffix(path, desiredSuffix), true);
    }

    /// <summary>Resolve the temp root for pack artifacts, creating a caller-provided folder (Python <c>resolve_temp_root</c>).</summary>
    /// <param name="tempFolder">Optional folder; system temp when <see langword="null"/>.</param>
    /// <returns>Existing directory path.</returns>
    public static string ResolveTempRoot(string? tempFolder = null)
    {
        if (tempFolder is null)
        {
            return Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        }

        string full = Path.GetFullPath(ExpandUser(tempFolder));
        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>Python <c>Path.expanduser</c> for a leading <c>~</c>.</summary>
    /// <param name="path">Path that may start with <c>~</c>.</param>
    /// <returns>Path with the home directory substituted.</returns>
    public static string ExpandUser(string path)
    {
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return home + path[1..];
        }

        return path;
    }
}
