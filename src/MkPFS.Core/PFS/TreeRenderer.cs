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
        void Render(DirectoryInfo dir, string prefix)
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
                if (children[i] is DirectoryInfo child)
                {
                    Render(child, prefix + (last ? "    " : "|   "));
                }
            }
        }

        Render(new DirectoryInfo(root), string.Empty);
        return lines;
    }
}
