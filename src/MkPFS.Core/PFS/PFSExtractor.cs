using System.Security.Cryptography;
using MkPFS.Core.Diagnostics;
using MkPFS.Core.Exfat;
using MkPFS.Core.Util;

namespace MkPFS.Core.PFS;

/// <summary>Image kind for verify/unpack/tree.</summary>
public enum ImageFormat
{
    /// <summary>Detect from extension and boot sector.</summary>
    Auto,

    /// <summary>PFS image.</summary>
    PFS,

    /// <summary>Raw exFAT volume.</summary>
    Exfat,
}

/// <summary>Result of an extraction (Python <c>PFSExtractionResult</c>).</summary>
public sealed class ExtractionResult
{
    /// <summary>Source image.</summary>
    public required string ImagePath { get; init; }

    /// <summary>Destination directory.</summary>
    public required string OutputPath { get; init; }

    /// <summary>Errors.</summary>
    public List<string> Errors { get; } = [];

    /// <summary>Warnings.</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>Files written.</summary>
    public int FilesWritten { get; set; }

    /// <summary>Directories created or ensured.</summary>
    public int DirectoriesCreated { get; set; }

    /// <summary>Logical bytes written.</summary>
    public long BytesWritten { get; set; }
}

/// <summary>Options for <see cref="PFSExtractor.ExtractPFS"/>.</summary>
public sealed record PFSExtractOptions
{
    /// <summary>EKPFS key; all zeros when <see langword="null"/>.</summary>
    public byte[]? Ekpfs { get; init; }

    /// <summary>newCrypt key derivation.</summary>
    public bool NewCrypt { get; init; }

    /// <summary>Extract the files inside a wrapped exFAT instead of the inner image.</summary>
    public bool Deep { get; init; }

    /// <summary>With <see cref="Deep"/>: inner paths (files or directory prefixes) to extract.</summary>
    public IReadOnlyList<string>? Selectors { get; init; }

    /// <summary>Progress sink.</summary>
    public IProgressSink? Progress { get; init; }
}

/// <summary>
/// Extraction, exFAT verification and format detection (port of Python <c>extract_pfs_image</c>,
/// <c>_extract_inner_exfat</c>, <c>extract_exfat_image</c>, <c>verify_exfat_image</c>, <c>detect_image_format</c>).
/// </summary>
public static class PFSExtractor
{
    private const long ProgressInterval = 8L * 1024 * 1024;

    /// <summary>
    /// Resolve the effective format: an explicit hint wins; otherwise <c>.exfat</c> extension or an exFAT boot
    /// signature means exFAT, anything else PFS.
    /// </summary>
    /// <param name="imagePath">Image path.</param>
    /// <param name="hint">Requested format.</param>
    /// <returns>Effective format.</returns>
    public static ImageFormat DetectFormat(string imagePath, ImageFormat hint = ImageFormat.Auto)
    {
        if (hint != ImageFormat.Auto)
        {
            return hint;
        }

        if (string.Equals(PathRules.Suffix(imagePath), ".exfat", StringComparison.OrdinalIgnoreCase))
        {
            return ImageFormat.Exfat;
        }

        try
        {
            using FileStream stream = File.OpenRead(imagePath);
            byte[] vbr = new byte[11];
            int got = stream.ReadAtLeast(vbr, vbr.Length, throwOnEndOfStream: false);
            return got >= 11 && vbr.AsSpan(3).SequenceEqual(ExfatReader.Signature) ? ImageFormat.Exfat : ImageFormat.PFS;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ImageFormat.PFS;
        }
    }

    /// <summary>
    /// Open the single unsigned inner file of an image as an exFAT volume (Python <c>open_inner_file_view</c> plus
    /// the signature check). Returns <see langword="null"/> when the image has errors, holds more or fewer than one
    /// file, the file is signed/empty, or it is not exFAT.
    /// </summary>
    /// <param name="imagePath">Image path.</param>
    /// <param name="ekpfs">EKPFS key.</param>
    /// <param name="newCrypt">newCrypt derivation.</param>
    /// <returns>Open image, inner exFAT view and inner name; dispose the image when done.</returns>
    public static (PFSImage Image, Stream View, string InnerName)? OpenInnerExfat(string imagePath, byte[]? ekpfs, bool newCrypt)
    {
        PFSInspection inspection = PFSInspector.Inspect(imagePath, new PFSInspectOptions { Ekpfs = ekpfs, NewCrypt = newCrypt, VerifyPayloads = false });
        if (inspection.Errors.Count > 0 || inspection.Header is null || inspection.FileInodes.Count != 1)
        {
            return null;
        }

        (string name, long number) = inspection.FileInodes.First();
        PFSInode inode = inspection.Inodes[(int)number];
        if (inode.IsSigned || inode.Blocks <= 0 || inode.LogicalSize <= 0)
        {
            return null;
        }

        PFSImage image = PFSImage.Open(imagePath, ekpfs, newCrypt);
        try
        {
            Stream view = image.OpenLogical(inode);
            byte[] head = new byte[11];
            if (view.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length || !head.AsSpan(3).SequenceEqual(ExfatReader.Signature))
            {
                image.Dispose();
                return null;
            }

            return (image, view, name);
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    /// <summary>Extract every logical file of a PFS image (or, with <see cref="PFSExtractOptions.Deep"/>, the wrapped exFAT).</summary>
    /// <param name="imagePath">Image path.</param>
    /// <param name="outputPath">Destination directory.</param>
    /// <param name="options">Options.</param>
    /// <returns>Extraction result.</returns>
    public static ExtractionResult ExtractPFS(string imagePath, string outputPath, PFSExtractOptions? options = null)
    {
        options ??= new PFSExtractOptions();
        ExtractionResult result = new() { ImagePath = imagePath, OutputPath = outputPath };
        if (options.Selectors is { Count: > 0 } && !options.Deep)
        {
            result.Warnings.Add("path selection is only supported with --deep; extracting everything");
        }

        // Deep mode descends into a wrapped exFAT without a temporary inner image.
        if (options.Deep)
        {
            (PFSImage Image, Stream View, string InnerName)? inner = OpenInnerExfat(imagePath, options.Ekpfs, options.NewCrypt);
            if (inner is { } opened)
            {
                using (opened.Image)
                using (opened.View)
                {
                    return ExtractExfatVolume(new ExfatReader(opened.View), result, options.Selectors, options.Progress, "inner exFAT", $"\nExtracting {{0}} files from inner exFAT to {outputPath}...");
                }
            }

            result.Warnings.Add("--deep: no inner exFAT found; extracting image contents as-is");
        }

        PFSInspection inspection = PFSInspector.Inspect(imagePath, new PFSInspectOptions { Ekpfs = options.Ekpfs, NewCrypt = options.NewCrypt, VerifyPayloads = false });
        result.Warnings.AddRange(inspection.Warnings);
        result.Errors.AddRange(inspection.Errors);
        if (result.Errors.Count > 0)
        {
            return result;
        }

        if (inspection.Header is null)
        {
            result.Errors.Add("image header is not available");
            return result;
        }

        if (File.Exists(outputPath))
        {
            result.Errors.Add($"output path exists and is not a directory: {outputPath}");
            return result;
        }

        List<string> directories = [.. inspection.DirInodes.Keys
            .Where(d => d.Length > 0)
            .OrderBy(d => d.Count(c => c == '/'))
            .ThenBy(d => d.ToLowerInvariant(), StringComparer.Ordinal)
            .ThenBy(d => d, StringComparer.Ordinal)];
        List<(string Rel, string Target, long Inode)> files = [];
        foreach ((string rel, long inode) in inspection.FileInodes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!TryTarget(outputPath, rel, out string target))
            {
                result.Errors.Add($"unsafe path in image: {rel}");
                continue;
            }

            files.Add((rel, target, inode));
        }

        List<string> directoryTargets = [];
        foreach (string dir in directories)
        {
            if (!TryTarget(outputPath, dir, out string target))
            {
                result.Errors.Add($"unsafe path in image: {dir}");
                continue;
            }

            directoryTargets.Add(target);
            if (File.Exists(target))
            {
                result.Errors.Add($"output path conflicts with a file: {target}");
            }
        }

        foreach ((string _, string target, long _) in files)
        {
            if (File.Exists(target) || Directory.Exists(target))
            {
                result.Errors.Add($"output file already exists: {target}");
            }
        }

        if (result.Errors.Count > 0)
        {
            return result;
        }

        Directory.CreateDirectory(outputPath);
        options.Progress?.Status($"\nExtracting {files.Count} files to {outputPath}...");
        try
        {
            using PFSImage image = PFSImage.Open(imagePath, options.Ekpfs, options.NewCrypt);
            foreach (string target in directoryTargets)
            {
                if (!Directory.Exists(target))
                {
                    Directory.CreateDirectory(target);
                    result.DirectoriesCreated++;
                }
            }

            long total = files.Sum(f => Math.Max(0, inspection.Inodes[(int)f.Inode].LogicalSize));
            ByteProgress progress = new(options.Progress, "extract", total);
            foreach ((string rel, string target, long number) in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                try
                {
                    using FileStream output = new(target, FileMode.Create, FileAccess.Write);
                    result.BytesWritten += image.CopyLogicalTo(inspection.Inodes[(int)number], output, progress.Add);
                }
                catch (InvalidDataException ex)
                {
                    result.Errors.Add($"failed to decode file '{rel}' payload: {ex.Message}");
                    return result;
                }

                result.FilesWritten++;
            }

            progress.Finish();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            result.Errors.Add($"failed to extract image: {ex.Message}");
        }

        return result;
    }

    /// <summary>Extract a raw exFAT image (Python <c>extract_exfat_image</c>).</summary>
    /// <param name="imagePath">exFAT image.</param>
    /// <param name="outputPath">Destination directory.</param>
    /// <param name="progress">Progress sink.</param>
    /// <param name="selectors">Optional paths to extract.</param>
    /// <returns>Extraction result.</returns>
    public static ExtractionResult ExtractExfat(string imagePath, string outputPath, IProgressSink? progress = null, IReadOnlyList<string>? selectors = null)
    {
        ExtractionResult result = new() { ImagePath = imagePath, OutputPath = outputPath };
        FileStream stream;
        ExfatReader reader;
        try
        {
            stream = File.OpenRead(imagePath);
            reader = new ExfatReader(stream);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            result.Errors.Add($"failed to parse exFAT image: {ex.Message}");
            return result;
        }

        using (stream)
        {
            return ExtractExfatVolume(reader, result, selectors, progress, "exFAT", $"\nExtracting {{0}} files from exFAT image to {outputPath}...");
        }
    }

    /// <summary>
    /// Verify a raw exFAT image: walk the tree, or compare file hashes with a source directory using
    /// case-insensitive keys and skipping dot-files, Thumbs.db and desktop.ini (Python <c>verify_exfat_image</c>).
    /// </summary>
    /// <param name="imagePath">exFAT image.</param>
    /// <param name="source">Optional source directory.</param>
    /// <returns>Errors and warnings.</returns>
    public static (List<string> Errors, List<string> Warnings) VerifyExfat(string imagePath, string? source)
    {
        try
        {
            using FileStream stream = File.OpenRead(imagePath);
            return VerifyExfat(stream, source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ([$"failed to parse exFAT image: {ex.Message}"], []);
        }
    }

    /// <summary>Same as <see cref="VerifyExfat(string, string?)"/> over an exFAT volume stream (for example a wrapped image's inner file).</summary>
    /// <param name="stream">Seekable exFAT volume.</param>
    /// <param name="source">Optional source directory.</param>
    /// <returns>Errors and warnings.</returns>
    public static (List<string> Errors, List<string> Warnings) VerifyExfat(Stream stream, string? source)
    {
        List<string> errors = [];
        List<string> warnings = [];
        try
        {
            ExfatReader reader = new(stream);
            if (source is null)
            {
                try
                {
                    _ = reader.RootEntries();
                }
                catch (InvalidDataException ex)
                {
                    errors.Add($"failed to walk exFAT directory tree: {ex.Message}");
                }

                return (errors, warnings);
            }

            string root = Path.GetFullPath(PathRules.ExpandUser(source));
            if (!Directory.Exists(root))
            {
                errors.Add($"source directory does not exist: {root}");
                return (errors, warnings);
            }

            static bool IsMetadata(string rel)
            {
                string name = rel[(rel.LastIndexOf('/') + 1)..];
                return name.StartsWith('.') || name.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase) || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);
            }

            Dictionary<string, string> sourceHashes = new(StringComparer.Ordinal);
            foreach (string file in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true }).Order(StringComparer.Ordinal))
            {
                string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (!IsMetadata(rel))
                {
                    using FileStream fs = File.OpenRead(file);
                    sourceHashes[rel.ToLowerInvariant()] = Convert.ToHexStringLower(SHA256.HashData(fs));
                }
            }

            Dictionary<string, string> imageHashes = new(StringComparer.Ordinal);
            foreach (ExfatEntry entry in reader.EnumerateFiles())
            {
                string rel = entry.RelPath.Replace('\\', '/').TrimStart('/');
                if (IsMetadata(rel))
                {
                    continue;
                }

                using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                foreach (ReadOnlyMemory<byte> chunk in reader.ReadFile(entry))
                {
                    hash.AppendData(chunk.Span);
                }

                imageHashes[rel.ToLowerInvariant()] = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            List<string> missing = [.. sourceHashes.Keys.Where(k => !imageHashes.ContainsKey(k)).Order(StringComparer.Ordinal)];
            List<string> extra = [.. imageHashes.Keys.Where(k => !sourceHashes.ContainsKey(k)).Order(StringComparer.Ordinal)];
            if (missing.Count > 0)
            {
                errors.Add("missing files in exFAT image: " + string.Join(", ", missing.Take(20)) + (missing.Count > 20 ? " ..." : string.Empty));
            }

            if (extra.Count > 0)
            {
                errors.Add("extra files in exFAT image: " + string.Join(", ", extra.Take(20)) + (extra.Count > 20 ? " ..." : string.Empty));
            }

            foreach (string key in sourceHashes.Keys.Where(imageHashes.ContainsKey).Order(StringComparer.Ordinal))
            {
                if (sourceHashes[key] != imageHashes[key])
                {
                    errors.Add($"content mismatch for {key}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            errors.Add($"failed to parse exFAT image: {ex.Message}");
        }

        return (errors, warnings);
    }

    /// <summary>Normalize <c>--only</c> selectors: <c>/</c> separators, no leading/trailing slashes, no blanks.</summary>
    /// <param name="selectors">Raw selectors.</param>
    /// <returns>Cleaned selectors, or <see langword="null"/> for "everything".</returns>
    public static List<string>? NormalizeSelectors(IReadOnlyList<string>? selectors)
    {
        if (selectors is null || selectors.Count == 0)
        {
            return null;
        }

        List<string> cleaned = [.. selectors.Select(s => s.Trim().Replace('\\', '/').Trim('/')).Where(s => s.Length > 0)];
        return cleaned.Count > 0 ? cleaned : null;
    }

    private static ExtractionResult ExtractExfatVolume(
        ExfatReader reader,
        ExtractionResult result,
        IReadOnlyList<string>? selectors,
        IProgressSink? progressSink,
        string label,
        string statusFormat)
    {
        List<ExfatEntry> directories = [];
        List<ExfatEntry> files = [];
        try
        {
            void Flatten(List<ExfatEntry> nodes)
            {
                foreach (ExfatEntry node in ExfatReader.SortByLowerPath(nodes))
                {
                    if (node.IsDir)
                    {
                        directories.Add(node);
                        Flatten(node.Children);
                    }
                    else
                    {
                        files.Add(node);
                    }
                }
            }

            Flatten(reader.RootEntries());
        }
        catch (InvalidDataException ex)
        {
            result.Errors.Add($"failed to parse exFAT image: {ex.Message}");
            return result;
        }

        if (File.Exists(result.OutputPath))
        {
            result.Errors.Add($"output path exists and is not a directory: {result.OutputPath}");
            return result;
        }

        List<string>? normalized = NormalizeSelectors(selectors);
        HashSet<string> matched = new(StringComparer.Ordinal);
        bool Selected(string rel)
        {
            if (normalized is null)
            {
                return true;
            }

            bool hit = false;
            foreach (string selector in normalized)
            {
                if (rel == selector || rel.StartsWith(selector + "/", StringComparison.Ordinal))
                {
                    matched.Add(selector);
                    hit = true;
                }
            }

            return hit;
        }

        directories = [.. directories.Where(d => Selected(d.RelPath))];
        files = [.. files.Where(f => Selected(f.RelPath))];
        Directory.CreateDirectory(result.OutputPath);
        foreach (ExfatEntry directory in directories)
        {
            if (!TryTarget(result.OutputPath, directory.RelPath, out string target))
            {
                result.Errors.Add($"unsafe path in image: {directory.RelPath}");
                return result;
            }

            Directory.CreateDirectory(target);
            result.DirectoriesCreated++;
        }

        if (normalized is not null)
        {
            foreach (string selector in normalized.Where(s => !matched.Contains(s)))
            {
                result.Warnings.Add($"--only: no {label} entry matched '{selector}'");
            }
        }

        long total = files.Sum(f => (long)f.Length);
        progressSink?.Status(string.Format(System.Globalization.CultureInfo.InvariantCulture, statusFormat, files.Count));
        ByteProgress progress = new(progressSink, "extract", total);
        foreach (ExfatEntry file in files)
        {
            if (!TryTarget(result.OutputPath, file.RelPath, out string target))
            {
                result.Errors.Add($"unsafe path in image: {file.RelPath}");
                return result;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try
            {
                using FileStream output = new(target, FileMode.Create, FileAccess.Write);
                result.BytesWritten += reader.CopyFile(file, output, progress.Add);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                result.Errors.Add($"failed to extract '{file.RelPath}': {ex.Message}");
                return result;
            }

            result.FilesWritten++;
        }

        progress.Finish();
        return result;
    }

    /// <summary>
    /// Map a relative image path into <paramref name="root"/>, refusing paths that escape it (Python does not
    /// check; a crafted image could otherwise write outside the output folder).
    /// </summary>
    private static bool TryTarget(string root, string rel, out string target)
    {
        string fullRoot = Path.GetFullPath(root);
        target = Path.GetFullPath(Path.Combine(fullRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        return target.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>Byte-based progress throttled to one update per 8 MiB.</summary>
    private sealed class ByteProgress(IProgressSink? sink, string phase, long total)
    {
        private long _done;
        private long _lastReported;

        public void Add(int bytes)
        {
            _done += bytes;
            if (sink is not null && _done - _lastReported >= ProgressInterval)
            {
                _lastReported = _done;
                sink.Report(phase, Math.Min(_done, total), Math.Max(total, 1), _done);
            }
        }

        public void Finish() => sink?.Report(phase, Math.Max(total, 1), Math.Max(total, 1), _done);
    }
}
