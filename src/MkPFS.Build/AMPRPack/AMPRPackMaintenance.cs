using MkPFS.Core.AMPR;

namespace MkPFS.Build.AMPRPack;

/// <summary>Read-only plan of <see cref="AMPRPackMaintenance.RemovePackedSources"/>.</summary>
/// <param name="Files">Packed files.</param>
/// <param name="PresentFiles">Packed files present under the root.</param>
/// <param name="MissingFiles">Packed files already absent.</param>
/// <param name="Bytes">Bytes of present files.</param>
/// <param name="Paths">Relative paths in file-id order.</param>
public sealed record AMPRRemovalPlan(long Files, long PresentFiles, long MissingFiles, long Bytes, IReadOnlyList<string> Paths);

/// <summary>Result of <see cref="AMPRPackMaintenance.RemovePackedSources"/>.</summary>
/// <param name="Files">Files removed.</param>
/// <param name="Bytes">Bytes removed.</param>
/// <param name="Directories">Empty directories removed.</param>
/// <param name="Verified">Always <see langword="true"/>: the set was verified before removal.</param>
public sealed record AMPRRemovalResult(long Files, long Bytes, long Directories, bool Verified);

/// <summary>Result of <see cref="AMPRPackMaintenance.WriteRuntimeConfig"/>.</summary>
/// <param name="Path">Written <c>.runtime</c> file.</param>
/// <param name="BuildId">Manifest build id, lower-case hex.</param>
/// <param name="Runtime">Written settings.</param>
public sealed record AMPRRuntimeConfigResult(string Path, string BuildId, AMPRRuntimeSettings Runtime);

/// <summary>
/// Pack-set maintenance that changes files: removing packed sources from <c>/app0</c> and replacing runtime
/// settings (ampr_pack <c>remove-packed-sources</c>, <c>runtime-config</c>).
/// </summary>
public static class AMPRPackMaintenance
{
    private static readonly HashSet<string> ProtectedDirectories = new(StringComparer.Ordinal) { "mods", "save", "sce_module", "sce_sys", "system" };

    private static readonly HashSet<string> ProtectedNames = new(StringComparer.Ordinal)
    {
        "ampr_assets.index", "ampr_assets.index.crc", "ampr_assets.index.runtime", "ampr_emu.index", "eboot.bin", "nptitle.dat", "param.sfo",
    };

    private static readonly HashSet<string> ProtectedSuffixes = new(StringComparer.Ordinal) { ".elf", ".prx", ".self", ".sprx" };

    /// <summary>
    /// Whether a relative path must never be removed: system and save directories, executables and modules,
    /// indexes and pack volumes (Python <c>_removal_path_is_protected</c>).
    /// </summary>
    /// <param name="relative">Path relative to <c>/app0</c>.</param>
    /// <returns><see langword="true"/> when protected.</returns>
    public static bool IsProtected(string relative)
    {
        string[] parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        string name = parts[^1].ToLowerInvariant();
        int dot = name.LastIndexOf('.');
        // PurePosixPath.suffix: empty for names that only start with a dot.
        string suffix = dot > 0 && dot < name.Length - 1 ? name[dot..] : string.Empty;
        return ProtectedDirectories.Contains(parts[0].ToLowerInvariant())
            || ProtectedNames.Contains(name)
            || ProtectedSuffixes.Contains(suffix)
            || (name.StartsWith("ampr_assets-", StringComparison.Ordinal) && name.EndsWith(".pak", StringComparison.Ordinal));
    }

    /// <summary>The exact safe source set the manifest packs; nothing is changed (Python <c>packed_source_removal_plan</c>).</summary>
    /// <param name="indexPath">Manifest path.</param>
    /// <param name="root"><c>/app0</c> source tree.</param>
    /// <returns>Plan.</returns>
    /// <exception cref="AMPRPackException">The manifest packs a protected file or a source is unsafe.</exception>
    public static AMPRRemovalPlan RemovalPlan(string indexPath, string root)
    {
        AMPRPackManifest manifest = AMPRPackManifest.Load(indexPath);
        root = RequireRoot(root);
        List<string> paths = [];
        long present = 0;
        long missing = 0;
        for (int fileId = 1; fileId <= manifest.Files.Count; fileId++)
        {
            AMPRFileRecord record = manifest.Files[fileId - 1];
            if (!record.IsPacked)
            {
                continue;
            }

            string relative = AMPRAssetPath.Relative(manifest.FilePath(fileId));
            if (IsProtected(relative))
            {
                throw new AMPRPackException($"manifest marks a protected game/runtime file as packed: {relative}");
            }

            paths.Add(relative);
            if (ValidatedSource(root, relative, record.LogicalSize, allowMissing: true) is null)
            {
                missing++;
            }
            else
            {
                present += (long)record.LogicalSize;
            }
        }

        return new AMPRRemovalPlan(paths.Count, paths.Count - missing, missing, present, paths);
    }

    /// <summary>
    /// Verify the pack set and every source byte, then permanently delete the sources of packed files
    /// (Python <c>remove_packed_sources</c>). Offline verification cannot prove the title never reads a file
    /// through an unhooked path; the caller decides that.
    /// </summary>
    /// <param name="indexPath">Manifest path.</param>
    /// <param name="root"><c>/app0</c> source tree.</param>
    /// <param name="removeEmptyDirs">Remove directories left empty, never the root.</param>
    /// <returns>Counters.</returns>
    /// <exception cref="AMPRPackException">A source is missing, protected, unsafe or differs from the packed data.</exception>
    public static AMPRRemovalResult RemovePackedSources(string indexPath, string root, bool removeEmptyDirs = false)
    {
        AMPRRemovalPlan initial = RemovalPlan(indexPath, root);
        if (initial.MissingFiles > 0)
        {
            throw new AMPRPackException($"cannot verify before removal: {initial.MissingFiles} packed source files are missing");
        }

        // Check the pack payload and every source byte immediately before the irreversible phase.
        AMPRPackTools.Verify(indexPath);
        AMPRPackTools.VerifyAgainstRoot(indexPath, root);

        AMPRPackManifest manifest = AMPRPackManifest.Load(indexPath);
        root = RequireRoot(root);
        List<(string Path, ulong Size)> candidates = [];
        for (int fileId = 1; fileId <= manifest.Files.Count; fileId++)
        {
            AMPRFileRecord record = manifest.Files[fileId - 1];
            if (!record.IsPacked)
            {
                continue;
            }

            string relative = AMPRAssetPath.Relative(manifest.FilePath(fileId));
            if (IsProtected(relative))
            {
                throw new AMPRPackException($"refusing to remove protected path: {relative}");
            }

            candidates.Add((ValidatedSource(root, relative, record.LogicalSize, allowMissing: false)!, record.LogicalSize));
        }

        long bytes = 0;
        HashSet<string> parents = new(StringComparer.Ordinal);
        foreach ((string path, ulong size) in candidates)
        {
            File.Delete(path);
            bytes += (long)size;
            parents.Add(Path.GetDirectoryName(path)!);
        }

        long removedDirs = 0;
        if (removeEmptyDirs)
        {
            HashSet<string> expanded = new(StringComparer.Ordinal);
            foreach (string directory in parents)
            {
                for (string current = directory; !SamePath(current, root); current = Path.GetDirectoryName(current)!)
                {
                    expanded.Add(current);
                }
            }

            foreach (string directory in expanded.OrderByDescending(d => d.Split(Path.DirectorySeparatorChar).Length))
            {
                try
                {
                    Directory.Delete(directory);
                    removedDirs++;
                }
                catch (IOException)
                {
                    // Non-empty directories and directories reused concurrently are kept.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        return new AMPRRemovalResult(candidates.Count, bytes, removedDirs, true);
    }

    /// <summary>Atomically replace <c>&lt;index&gt;.runtime</c> from a TOML <c>[runtime]</c> section without repacking.</summary>
    /// <param name="indexPath">Manifest path.</param>
    /// <param name="configPath">TOML file with <c>[runtime]</c>.</param>
    /// <returns>What was written.</returns>
    /// <exception cref="AMPRPackException">The configuration has no <c>[runtime]</c>.</exception>
    public static AMPRRuntimeConfigResult WriteRuntimeConfig(string indexPath, string configPath)
    {
        AMPRPackManifest manifest = AMPRPackManifest.Load(indexPath);
        AMPRRuntimeSettings settings = AMPRPackConfig.Load(configPath).Runtime
            ?? throw new AMPRPackException("configuration must contain [runtime]");
        string destination = AMPRPackFormat.RuntimePath(indexPath);
        string temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!, $".runtime-{Guid.NewGuid():N}");
        try
        {
            using (FileStream handle = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                handle.Write(settings.Encode(manifest.BuildId));
                handle.Flush(flushToDisk: true);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }

        return new AMPRRuntimeConfigResult(destination, Convert.ToHexStringLower(manifest.BuildId), settings);
    }

    private static string RequireRoot(string root)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(full))
        {
            throw File.Exists(full)
                ? new AMPRPackException($"source root is not a directory: {full}")
                : new DirectoryNotFoundException($"No such file or directory: '{root}'");
        }

        return full;
    }

    // Python _validated_removal_source: a regular, non-link file inside the root with the manifest size.
    private static string? ValidatedSource(string root, string relative, ulong expectedSize, bool allowMissing)
    {
        string candidate = AMPRAssetPath.SafeOutputPath(root, relative);
        FileInfo info = new(candidate);
        bool isDirectory = Directory.Exists(candidate);
        if (!info.Exists && !isDirectory && info.LinkTarget is null)
        {
            return allowMissing ? null : throw new AMPRPackException($"packed source is missing: {relative}");
        }

        FileSystemInfo entry = isDirectory ? new DirectoryInfo(candidate) : info;
        if (entry.LinkTarget is not null || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new AMPRPackException($"refusing to remove a symlink/reparse point: {relative}");
        }

        if (isDirectory)
        {
            throw new AMPRPackException($"packed source is not a regular file: {relative}");
        }

        string parent = Path.GetDirectoryName(Path.GetFullPath(candidate))!;
        if (!SamePath(parent, root) && !IsUnder(ResolveDirectory(parent), ResolveDirectory(root)))
        {
            throw new AMPRPackException($"packed source resolves outside /app0: {relative}");
        }

        return (ulong)info.Length == expectedSize
            ? candidate
            : throw new AMPRPackException($"packed source size changed for {relative}: manifest={expectedSize}, disk={info.Length}");
    }

    // Directory path with linked components resolved.
    private static string ResolveDirectory(string path)
    {
        string full = Path.GetFullPath(path);
        string current = Path.GetPathRoot(full)!;
        foreach (string part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            string next = Path.Combine(current, part);
            DirectoryInfo info = new(next);
            current = info.Exists && info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target
                ? ResolveDirectory(target.FullName)
                : next;
        }

        return Path.TrimEndingDirectorySeparator(current);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool SamePath(string a, string b) =>
        Path.TrimEndingDirectorySeparator(a).Equals(Path.TrimEndingDirectorySeparator(b), PathComparison);

    private static bool IsUnder(string path, string root) =>
        SamePath(path, root) || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, PathComparison);
}
