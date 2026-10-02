using System.Diagnostics;
using MkPFS.Build.PFS;
using MkPFS.Core.Diagnostics;
using MkPFS.Core.PFS;
using MkPFS.Core.Util;

namespace MkPFS.Build;

/// <summary>Kind of batch item.</summary>
public enum BatchItemKind
{
    /// <summary>Folder, packed exFAT-wrapped.</summary>
    Folder,

    /// <summary>Image file (<c>.exfat</c>, <c>.ffpkg</c>, <c>.ffpfs</c>, <c>.ffpfsc</c>), packed as a single file.</summary>
    File,
}

/// <summary>Outcome of one batch item.</summary>
public enum BatchStatus
{
    /// <summary>Packed.</summary>
    Converted,

    /// <summary>Output existed and <c>--overwrite</c> was not given.</summary>
    Skipped,

    /// <summary><c>--dry-run</c>: nothing written.</summary>
    DryRun,

    /// <summary>Build or verification failed.</summary>
    Error,
}

/// <summary>A packable entry directly inside the batch source folder (Python <c>BatchItem</c>).</summary>
/// <param name="Name">Entry name.</param>
/// <param name="Source">Full path.</param>
/// <param name="Kind">Folder or file.</param>
public sealed record BatchItem(string Name, string Source, BatchItemKind Kind);

/// <summary>Result of one item (Python <c>BatchItemResult</c>).</summary>
public sealed class BatchItemResult
{
    /// <summary>Item.</summary>
    public required BatchItem Item { get; init; }

    /// <summary>Outcome.</summary>
    public BatchStatus Status { get; set; }

    /// <summary>Image path.</summary>
    public string? OutputPath { get; init; }

    /// <summary>Input size.</summary>
    public long RawSize { get; init; }

    /// <summary>Image size.</summary>
    public long CompressedSize { get; init; }

    /// <summary>Time spent on the item.</summary>
    public double ElapsedSeconds { get; init; }

    /// <summary>Failure reason.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Percent saved.</summary>
    public double SavingsPercent => RawSize == 0 ? 0.0 : (double)(RawSize - CompressedSize) / RawSize * 100.0;
}

/// <summary>Results of a batch run (Python <c>BatchSummary</c>).</summary>
/// <param name="Results">Per item, in order.</param>
/// <param name="ElapsedSeconds">Total time.</param>
public sealed record BatchSummary(IReadOnlyList<BatchItemResult> Results, double ElapsedSeconds)
{
    /// <summary>Items converted.</summary>
    public int Converted => Results.Count(r => r.Status == BatchStatus.Converted);

    /// <summary>Items skipped.</summary>
    public int Skipped => Results.Count(r => r.Status == BatchStatus.Skipped);

    /// <summary>Items planned by a dry run.</summary>
    public int DryRun => Results.Count(r => r.Status == BatchStatus.DryRun);

    /// <summary>Items that failed.</summary>
    public int Errors => Results.Count(r => r.Status == BatchStatus.Error);

    /// <summary>Raw size of converted and skipped items.</summary>
    public long TotalRawSize => Results.Where(r => r.Status is BatchStatus.Converted or BatchStatus.Skipped).Sum(r => r.RawSize);

    /// <summary>Image size of converted and skipped items.</summary>
    public long TotalCompressedSize => Results.Where(r => r.Status is BatchStatus.Converted or BatchStatus.Skipped).Sum(r => r.CompressedSize);
}

/// <summary>Options for <see cref="Batch.Run"/>.</summary>
public sealed record BatchOptions
{
    /// <summary>Folder whose direct entries are packed.</summary>
    public required string SourceDir { get; init; }

    /// <summary>Folder that receives <c>&lt;name&gt;.ffpfsc</c>.</summary>
    public required string OutputDir { get; init; }

    /// <summary>Replace existing outputs instead of skipping them.</summary>
    public bool Overwrite { get; init; }

    /// <summary>Plan only.</summary>
    public bool DryRun { get; init; }

    /// <summary>Verify each image after packing.</summary>
    public bool Verify { get; init; }

    /// <summary>Settings for the per-item builders (source, output and inner name are set per item).</summary>
    public required SingleFileBuildOptions Build { get; init; }
}

/// <summary>
/// Packs every folder and image file directly inside a folder into <c>&lt;name&gt;.ffpfsc</c> (port of Python
/// <c>mkpfs/batch.py</c>): folders exFAT-wrapped, image files as single-file images.
/// </summary>
public static class Batch
{
    private static readonly string[] FileSuffixes = [".exfat", ".ffpkg", ".ffpfs", ".ffpfsc"];

    /// <summary>
    /// Packable entries directly in <paramref name="sourceDir"/>, sorted by lower-cased name: folders, and files with
    /// an image suffix; dot-names and OS metadata are skipped (Python <c>discover_batch_items</c>).
    /// </summary>
    /// <param name="sourceDir">Folder.</param>
    /// <param name="log">Warnings for unreadable entries.</param>
    /// <returns>Items.</returns>
    /// <exception cref="BuildException">The folder cannot be read.</exception>
    public static List<BatchItem> Discover(string sourceDir, IMkPFSLog log)
    {
        List<FileSystemInfo> entries;
        try
        {
            entries = [.. new DirectoryInfo(sourceDir).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 })];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BuildException($"unable to read source directory: {sourceDir}: {ex.Message}");
        }

        List<BatchItem> items = [];
        foreach (FileSystemInfo entry in entries)
        {
            if (entry.Name.StartsWith('.') || NameRules.IsIgnoredName(entry.Name))
            {
                continue;
            }

            try
            {
                if (entry is DirectoryInfo)
                {
                    items.Add(new BatchItem(entry.Name, entry.FullName, BatchItemKind.Folder));
                }
                else if (FileSuffixes.Contains(PathRules.Suffix(entry.Name).ToLowerInvariant()))
                {
                    items.Add(new BatchItem(entry.Name, entry.FullName, BatchItemKind.File));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Warning($"Skipping unreadable entry '{entry.Name}': {ex.Message}");
            }
        }

        return [.. items.OrderBy(i => i.Name.ToLowerInvariant(), StringComparer.Ordinal)];
    }

    /// <summary>
    /// Pack <paramref name="items"/> one after another; a failed item is recorded and the batch continues
    /// (Python <c>run_batch</c>).
    /// </summary>
    /// <param name="options">Options.</param>
    /// <param name="items">Items from <see cref="Discover"/>, taken up front so outputs written into the source are ignored.</param>
    /// <param name="log">Builder log.</param>
    /// <param name="progress">Builder progress.</param>
    /// <param name="converting">Called before an item is built (index from 1).</param>
    /// <param name="finished">Called with each result.</param>
    /// <param name="failed">Called when a build throws (index from 1, message).</param>
    /// <returns>Summary.</returns>
    /// <exception cref="BuildException">The source is missing or the output folder is inside it.</exception>
    public static BatchSummary Run(
        BatchOptions options, IReadOnlyList<BatchItem> items, IMkPFSLog log, IProgressSink? progress,
        Action<int, BatchItem, string>? converting = null, Action<int, BatchItemResult>? finished = null, Action<int, BatchItem, string>? failed = null)
    {
        string source = Path.GetFullPath(options.SourceDir);
        string output = Path.GetFullPath(options.OutputDir);
        if (!Directory.Exists(source))
        {
            throw new BuildException($"source directory does not exist or is not a directory: {options.SourceDir}");
        }

        string relative = Path.GetRelativePath(source, output);
        if (relative != "." && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
        {
            throw new BuildException("output directory cannot be inside the source directory");
        }

        Stopwatch batchWatch = Stopwatch.StartNew();
        List<BatchItemResult> results = [];
        for (int i = 0; i < items.Count; i++)
        {
            BatchItem item = items[i];
            int index = i + 1;
            string outputPath = Path.Combine(output, item.Name + ".ffpfsc");
            if (File.Exists(outputPath) && !options.Overwrite)
            {
                results.Add(new BatchItemResult
                {
                    Item = item, Status = BatchStatus.Skipped, OutputPath = outputPath,
                    RawSize = EstimateRawSize(item), CompressedSize = new FileInfo(outputPath).Length,
                });
                finished?.Invoke(index, results[^1]);
                continue;
            }

            if (options.DryRun)
            {
                results.Add(new BatchItemResult { Item = item, Status = BatchStatus.DryRun, OutputPath = outputPath, RawSize = EstimateRawSize(item) });
                finished?.Invoke(index, results[^1]);
                continue;
            }

            converting?.Invoke(index, item, outputPath);
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                Directory.CreateDirectory(output);
                SingleFileBuildOptions build = options.Build with { SourceFile = item.Source, OutputPath = outputPath, InnerFileName = null, DryRun = false };
                BuildStats stats = item.Kind == BatchItemKind.Folder
                    ? ExfatWrappedImageBuilder.Build(build, log, progress)
                    : SingleFileImageBuilder.Build(build, log, progress);
                BatchItemResult result = new()
                {
                    Item = item, Status = BatchStatus.Converted, OutputPath = outputPath,
                    RawSize = stats.UncompressedTotalSize, CompressedSize = new FileInfo(outputPath).Length, ElapsedSeconds = watch.Elapsed.TotalSeconds,
                };
                results.Add(result);
                if (options.Verify && VerifyItem(item, outputPath, options.Build) is { Count: > 0 } errors)
                {
                    result.Status = BatchStatus.Error;
                    result.ErrorMessage = string.Join("; ", errors.Take(3));
                }

                finished?.Invoke(index, result);
            }
            catch (Exception ex) when (ex is BuildException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                results.Add(new BatchItemResult
                {
                    Item = item, Status = BatchStatus.Error, OutputPath = outputPath, ElapsedSeconds = watch.Elapsed.TotalSeconds, ErrorMessage = ex.Message,
                });
                failed?.Invoke(index, item, ex.Message);
            }
        }

        return new BatchSummary(results, batchWatch.Elapsed.TotalSeconds);
    }

    /// <summary>Input size: the file size, or the sum of a folder's files without dot-names and OS metadata.</summary>
    /// <param name="item">Item.</param>
    /// <returns>Bytes.</returns>
    public static long EstimateRawSize(BatchItem item) =>
        item.Kind == BatchItemKind.File
            ? new FileInfo(item.Source).Length
            : Directory.EnumerateFiles(item.Source, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true })
                .Select(f => new FileInfo(f))
                .Where(f => !f.Name.StartsWith('.') && !NameRules.IsIgnoredName(f.Name))
                .Sum(f => f.Length);

    // Python verifies the image against the folder itself, which never matches an exFAT-wrapped image (oracle
    // finding 15); here the image payloads are checked and, for folders, the inner exFAT is compared with the folder.
    private static List<string> VerifyItem(BatchItem item, string image, SingleFileBuildOptions build)
    {
        PFSInspection inspection = PFSInspector.Inspect(image, new PFSInspectOptions { Ekpfs = build.Ekpfs, NewCrypt = build.NewCrypt });
        List<string> errors = [.. inspection.Errors];
        if (errors.Count == 0 && item.Kind == BatchItemKind.Folder &&
            PFSExtractor.OpenInnerExfat(image, build.Ekpfs, build.NewCrypt) is { } inner)
        {
            using (inner.Image)
            {
                errors.AddRange(PFSExtractor.VerifyExfat(inner.View, item.Source).Errors);
            }
        }

        return errors;
    }
}
