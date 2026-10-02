using MkPFS.Core.Util;

namespace MkPFS.Core.IO;

/// <summary>Fast source-tree file gathering (port of Python <c>mkpfs/gather.py</c>).</summary>
public static class SourceScanner
{
    // Hidden and system files are real game content; .NET skips them by default.
    private static readonly EnumerationOptions Options = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    /// <summary>
    /// Return every regular file under <paramref name="root"/> (absolute paths, traversal order).
    /// </summary>
    /// <remarks>
    /// Rules, same as Python <c>gather_files_scandir</c>:
    /// ignored names (<see cref="NameRules.IsIgnoredName"/>) are skipped for files and directories;
    /// directory links are never followed (avoids loops; Windows junctions included); file links
    /// are included when their target is an existing file; unreadable or vanished entries are skipped.
    /// A missing or non-directory root returns an empty list. The caller sorts.
    /// </remarks>
    /// <param name="root">Source directory.</param>
    /// <returns>Absolute file paths.</returns>
    public static List<string> GatherFiles(string root)
    {
        List<string> files = [];
        Stack<string> stack = new();
        stack.Push(root);

        while (stack.Count > 0)
        {
            string directory = stack.Pop();
            List<FileSystemInfo> entries;
            try
            {
                entries = [.. new DirectoryInfo(directory).EnumerateFileSystemInfos("*", Options)];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Missing root, a file passed as root, or a directory that vanished or is locked.
                continue;
            }

            foreach (FileSystemInfo entry in entries)
            {
                if (NameRules.IsIgnoredName(entry.Name))
                {
                    continue;
                }

                try
                {
                    bool isLink = entry.LinkTarget is not null;
                    if (entry is DirectoryInfo)
                    {
                        if (!isLink)
                        {
                            stack.Push(Path.Combine(directory, entry.Name));
                        }

                        continue;
                    }

                    if (!isLink || entry.ResolveLinkTarget(returnFinalTarget: true) is FileInfo { Exists: true })
                    {
                        files.Add(Path.Combine(directory, entry.Name));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The entry disappeared or cannot be inspected.
                }
            }
        }

        return files;
    }
}
