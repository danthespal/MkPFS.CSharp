using MkPFS.Core.Exfat;

namespace MkPFS.Core.PFS;

/// <summary>ASCII trees with <c>|-- </c> / <c>`-- </c> markers (Python <c>render_tree</c>, <c>render_exfat_tree</c>).</summary>
public static class TreeRenderer
{
    /// <summary>Render a PFS directory tree: directories first, then case-insensitive name, then name.</summary>
    /// <param name="direntsByInode">Directory entries per inode.</param>
    /// <param name="inode">Directory to render.</param>
    /// <returns>Lines (without the leading <c>/</c>).</returns>
    public static List<string> RenderPFS(IReadOnlyDictionary<long, List<PFSDirent>> direntsByInode, long inode)
    {
        List<string> lines = [];
        void Render(long dir, string prefix)
        {
            List<PFSDirent> entries = [.. (direntsByInode.GetValueOrDefault(dir) ?? [])
                .Where(e => e.Name is not ("." or ".."))
                .OrderBy(e => e.TypeCode != PFSConstants.DirentTypeDirectory)
                .ThenBy(e => e.Name.ToLowerInvariant(), StringComparer.Ordinal)
                .ThenBy(e => e.Name, StringComparer.Ordinal)];
            for (int i = 0; i < entries.Count; i++)
            {
                bool last = i == entries.Count - 1;
                lines.Add(prefix + (last ? "`-- " : "|-- ") + entries[i].Name);
                if (entries[i].TypeCode == PFSConstants.DirentTypeDirectory)
                {
                    Render(entries[i].InodeNumber, prefix + (last ? "    " : "|   "));
                }
            }
        }

        Render(inode, string.Empty);
        return lines;
    }

    /// <summary>Render an exFAT tree with the same ordering rules.</summary>
    /// <param name="entries">Entries at one level.</param>
    /// <returns>Lines.</returns>
    /// <summary>Render a list of slash-separated paths in the same style as <see cref="RenderPFS"/>.</summary>
    /// <param name="entries">Paths and whether each is a directory.</param>
    /// <returns>Tree lines.</returns>
    public static List<string> RenderPaths(IEnumerable<(string Path, bool IsDirectory)> entries)
    {
        Dictionary<string, List<(string Name, bool IsDirectory)>> children = new(StringComparer.Ordinal);
        foreach ((string path, bool isDirectory) in entries)
        {
            int slash = path.LastIndexOf('/');
            string parent = slash < 0 ? string.Empty : path[..slash];
            if (!children.TryGetValue(parent, out List<(string Name, bool IsDirectory)>? list))
            {
                children[parent] = list = [];
            }

            list.Add((path[(slash + 1)..], isDirectory));
        }

        List<string> lines = [];
        void Render(string dir, string prefix)
        {
            List<(string Name, bool IsDirectory)> ordered = [.. (children.GetValueOrDefault(dir) ?? [])
                .OrderBy(e => !e.IsDirectory)
                .ThenBy(e => e.Name.ToLowerInvariant(), StringComparer.Ordinal)
                .ThenBy(e => e.Name, StringComparer.Ordinal)];
            for (int i = 0; i < ordered.Count; i++)
            {
                bool last = i == ordered.Count - 1;
                lines.Add(prefix + (last ? "`-- " : "|-- ") + ordered[i].Name);
                if (ordered[i].IsDirectory)
                {
                    Render(dir.Length == 0 ? ordered[i].Name : dir + "/" + ordered[i].Name, prefix + (last ? "    " : "|   "));
                }
            }
        }

        Render(string.Empty, string.Empty);
        return lines;
    }

    public static List<string> RenderExfat(IEnumerable<ExfatEntry> entries)
    {
        List<string> lines = [];
        void Render(IEnumerable<ExfatEntry> level, string prefix)
        {
            List<ExfatEntry> ordered = [.. level
                .OrderBy(e => !e.IsDir)
                .ThenBy(e => e.Name.ToLowerInvariant(), StringComparer.Ordinal)
                .ThenBy(e => e.Name, StringComparer.Ordinal)];
            for (int i = 0; i < ordered.Count; i++)
            {
                bool last = i == ordered.Count - 1;
                lines.Add(prefix + (last ? "`-- " : "|-- ") + ordered[i].Name);
                if (ordered[i].IsDir)
                {
                    Render(ordered[i].Children, prefix + (last ? "    " : "|   "));
                }
            }
        }

        Render(entries, string.Empty);
        return lines;
    }

    /// <summary>Render a local folder: directories first, then case-insensitive name (Python <c>render_source_tree</c>).</summary>
    /// <param name="root">Folder.</param>
    /// <returns>Lines.</returns>
    public static List<string> RenderFolder(string root)
    {
        List<string> lines = [];
        StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        // Real paths of the directories being rendered. Links are followed like Python, except one that leads back
        // to an ancestor: Python then repeats the loop until the OS path limit; here the link is listed, not entered.
        HashSet<string> ancestors = new(comparer);
        void Render(DirectoryInfo dir, string realPath, string prefix)
        {
            List<FileSystemInfo> children;
            try
            {
                children = [.. dir.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, ReturnSpecialDirectories = false })
                    .OrderBy(c => c is not DirectoryInfo)
                    .ThenBy(c => c.Name.ToLowerInvariant(), StringComparer.Ordinal)
                    .ThenBy(c => c.Name, StringComparer.Ordinal)];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lines.Add($"[unreadable] {dir.Name}: {ex.Message}");
                return;
            }

            for (int i = 0; i < children.Count; i++)
            {
                bool last = i == children.Count - 1;
                lines.Add(prefix + (last ? "`-- " : "|-- ") + children[i].Name);
                if (children[i] is DirectoryInfo child && RealPath(child, realPath) is { } childReal && ancestors.Add(childReal))
                {
                    Render(child, childReal, prefix + (last ? "    " : "|   "));
                    ancestors.Remove(childReal);
                }
            }
        }

        DirectoryInfo top = new(root);
        string topReal = RealPath(top, null) ?? Path.TrimEndingDirectorySeparator(top.FullName);
        ancestors.Add(topReal);
        Render(top, topReal, string.Empty);
        return lines;
    }

    // Final target of a directory link, or the parent's real path plus the name; null when the link cannot be
    // resolved (Python's is_dir() is then false, so it is not entered).
    private static string? RealPath(DirectoryInfo dir, string? parentReal)
    {
        try
        {
            if (dir.LinkTarget is not null)
            {
                return dir.ResolveLinkTarget(returnFinalTarget: true) is { } target
                    ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.FullName))
                    : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return parentReal is null ? Path.TrimEndingDirectorySeparator(dir.FullName) : Path.Combine(parentReal, dir.Name);
    }
}
