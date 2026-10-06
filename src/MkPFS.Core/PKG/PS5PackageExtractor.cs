using MkPFS.Core.Diagnostics;
using MkPFS.Core.PFS.PS5;

namespace MkPFS.Core.PKG;

/// <summary>Outcome of <see cref="PS5PackageExtractor.Extract"/>.</summary>
/// <param name="FilesWritten">Files written (inner files plus <c>sce_sys</c> entries).</param>
/// <param name="DirectoriesCreated">Directories created.</param>
/// <param name="BytesWritten">Bytes written.</param>
/// <param name="EntriesWritten">CNT entries written under <c>sce_sys</c>.</param>
public sealed record PS5ExtractionResult(int FilesWritten, int DirectoriesCreated, long BytesWritten, int EntriesWritten);

/// <summary>
/// Extracts a PS5 package to a folder: every inner file, plus the named CNT entries (param.json, icons,
/// PlayGo files, ...) under <c>sce_sys/</c>, where they live in a source folder. Inner files win when both
/// exist. Paths that would leave the output folder are rejected.
/// </summary>
public static class PS5PackageExtractor
{
    /// <summary>Extract <paramref name="package"/> into <paramref name="outputDir"/>.</summary>
    /// <param name="package">Opened package.</param>
    /// <param name="outputDir">Destination (created when missing).</param>
    /// <param name="includeEntries">Also write named CNT entries under <c>sce_sys/</c>.</param>
    /// <param name="progress">Optional progress sink.</param>
    /// <returns>Counts.</returns>
    /// <exception cref="InvalidDataException">An entry name escapes the output folder.</exception>
    public static PS5ExtractionResult Extract(PS5Package package, string outputDir, bool includeEntries = true, IProgressSink? progress = null)
    {
        string root = Path.GetFullPath(outputDir);
        int files = 0;
        int entries = 0;
        long bytes = 0;
        HashSet<string> created = new(StringComparer.Ordinal);

        string Target(string relative)
        {
            string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"path escapes the output folder: {relative}");
            }

            return full;
        }

        void EnsureDirectory(string dir)
        {
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                created.Add(dir);
            }
        }

        EnsureDirectory(root);
        List<PS5InnerFile> tree = package.Inner.ReadTree();
        long total = tree.Where(f => f.Size > 0).Sum(f => f.Size);
        HashSet<string> innerPaths = new(StringComparer.Ordinal);

        // Directories first, in tree order; then the files in parallel, each worker decoding through its own
        // stream. A path listed twice is written once, from its last entry, as a serial walk would leave it.
        List<(PS5InnerFile File, string Target)> toWrite = [];
        Dictionary<string, int> slot = new(StringComparer.Ordinal);
        foreach (PS5InnerFile file in tree)
        {
            string target = Target(file.Path);
            if (file.Size < 0)
            {
                EnsureDirectory(target);
                continue;
            }

            EnsureDirectory(Path.GetDirectoryName(target)!);
            if (slot.TryGetValue(target, out int at))
            {
                toWrite[at] = (file, target);
            }
            else
            {
                slot[target] = toWrite.Count;
                toWrite.Add((file, target));
            }

            innerPaths.Add(file.Path);
            files++;
        }

        Lock gate = new();
        Parallel.ForEach(toWrite, package.OpenInnerStream, (item, _, stored) =>
        {
            using (FileStream output = new(item.Target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                package.Inner.CopyFile(item.File, output, stored);
            }

            lock (gate)
            {
                bytes += item.File.Size;
                progress?.Step("unpack", bytes, total, bytes);
            }

            return stored;
        }, stored => stored.Dispose());

        if (includeEntries)
        {
            foreach (CNTEntry entry in package.Package.Entries)
            {
                string relative = "sce_sys/" + entry.Name;
                if (entry.Name.Length == 0 || entry.IsEncrypted || innerPaths.Contains(relative))
                {
                    continue;
                }

                string target = Target(relative);
                EnsureDirectory(Path.GetDirectoryName(target)!);
                byte[] data = package.Package.ReadEntry(entry);
                File.WriteAllBytes(target, data);
                entries++;
                files++;
                bytes += data.Length;
            }
        }

        return new PS5ExtractionResult(files, created.Count, bytes, entries);
    }
}
