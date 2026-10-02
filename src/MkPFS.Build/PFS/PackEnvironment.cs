namespace MkPFS.Build.PFS;

/// <summary>Which files are stored raw regardless of compression settings.</summary>
public static class CompressionRules
{
    /// <summary>
    /// Executable-like payloads stay uncompressed (Python <c>should_skip_executable_compression</c>). Markers such as
    /// <c>sce_module</c> may appear in directory components, so the relative path is checked too.
    /// </summary>
    /// <param name="fileName">Base name.</param>
    /// <param name="filePath">Relative path inside the image.</param>
    /// <returns><see langword="true"/> to keep the file raw.</returns>
    public static bool ShouldSkipExecutable(string fileName, string filePath)
    {
        string name = fileName.ToLowerInvariant();
        string path = filePath.ToLowerInvariant();
        return (name.StartsWith("eboot", StringComparison.Ordinal) && name.EndsWith(".bin", StringComparison.Ordinal)) ||
            (name.StartsWith("param", StringComparison.Ordinal) && name.EndsWith(".sfx", StringComparison.Ordinal)) ||
            name.EndsWith(".prx", StringComparison.Ordinal) ||
            name.EndsWith(".sprx", StringComparison.Ordinal) ||
            name.EndsWith(".json", StringComparison.Ordinal) ||
            name.EndsWith(".txt", StringComparison.Ordinal) ||
            name.EndsWith(".png", StringComparison.Ordinal) ||
            name.EndsWith("keystone", StringComparison.Ordinal) ||
            path.Contains("sce_module", StringComparison.Ordinal) ||
            path.Contains("sce_sys", StringComparison.Ordinal);
    }
}

/// <summary>Storage checks done before packing.</summary>
public static class PackEnvironment
{
    /// <summary>
    /// Warn when the source, output or temp folder is on a network or removable drive, where parallel compression
    /// can be slow (Python <c>get_non_local_volume_warning</c>, which parses <c>mount</c>; this uses the drive type).
    /// </summary>
    /// <param name="source">Source path.</param>
    /// <param name="output">Output image path.</param>
    /// <param name="temp">Temp folder.</param>
    /// <returns>Warning text, or <see langword="null"/>.</returns>
    public static string? NonLocalVolumeWarning(string source, string output, string temp)
    {
        List<string> labels = [];
        foreach ((string label, string path) in new[] { ("source", source), ("output", Path.GetDirectoryName(Path.GetFullPath(output)) ?? output), ("temp", temp) })
        {
            if (DriveFor(path)?.DriveType is { } type && type is DriveType.Network or DriveType.Removable)
            {
                labels.Add($"{label} path looks non-local via drive type '{type.ToString().ToLowerInvariant()}'");
            }
        }

        return labels.Count == 0
            ? null
            : $"PFSC compression may run slowly because a build path looks non-local ({string.Join("; ", labels)}). " +
              "Try --cpu-count 1 or use local SSD paths for source, output, and temp data.";
    }

    /// <summary>Nearest existing folder for a path that may not exist yet.</summary>
    /// <param name="path">Path.</param>
    /// <returns>Existing directory.</returns>
    public static string ExistingParent(string path)
    {
        string? probe = Path.GetFullPath(path);
        while (probe is not null && !Directory.Exists(probe))
        {
            probe = Path.GetDirectoryName(probe);
        }

        return probe ?? Path.GetPathRoot(Path.GetFullPath(path)) ?? "/";
    }

    /// <summary>Free bytes on the volume holding <paramref name="path"/>, or <see langword="null"/> when unknown.</summary>
    /// <param name="path">Any path on the volume.</param>
    /// <returns>Free bytes.</returns>
    public static long? FreeBytes(string path)
    {
        try
        {
            return DriveFor(path)?.AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Windows: the drive root. Unix: the mount with the longest matching root directory.
    private static DriveInfo? DriveFor(string path)
    {
        try
        {
            string existing = ExistingParent(path);
            if (OperatingSystem.IsWindows())
            {
                return new DriveInfo(Path.GetPathRoot(existing)!);
            }

            return DriveInfo.GetDrives()
                .Where(d => existing.StartsWith(d.RootDirectory.FullName, StringComparison.Ordinal))
                .MaxBy(d => d.RootDirectory.FullName.Length);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
