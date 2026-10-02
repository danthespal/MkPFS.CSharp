using System.Buffers.Binary;
using System.Text.Json;
using MkPFS.Core.Diagnostics;
using MkPFS.Core.PFSC;

namespace MkPFS.Repair;

/// <summary>How repaired data is written.</summary>
public enum RepairMode
{
    /// <summary>Copy-replace when free space allows (GC <c>repair_choose_strategy</c>), else in-place.</summary>
    Auto,

    /// <summary>Rewrite the image file itself; only possible when no block moves toward the start.</summary>
    InPlace,

    /// <summary>Write <c>&lt;image&gt;.repair.tmp</c> and replace the image when done.</summary>
    Copy,
}

/// <summary>Repair outcome, named like GC <c>summary.json</c> statuses.</summary>
public enum RepairStatus
{
    /// <summary>No block needs repair.</summary>
    Noop,

    /// <summary>Scan only: blocks need repair.</summary>
    RepairNeeded,

    /// <summary>Blocks were rewritten and verified.</summary>
    Repaired,

    /// <summary>The image cannot be repaired (see <see cref="PFSCRepairResult.Error"/>).</summary>
    Failed,
}

/// <summary>Options for <see cref="PFSCRepair.Run"/>.</summary>
public sealed record PFSCRepairOptions
{
    /// <summary>GC <c>bad_blocks.tsv</c> to repair instead of the risky blocks found by the scan.</summary>
    public string? BadBlocksPath { get; init; }

    /// <summary>Report only; never write the image.</summary>
    public bool ScanOnly { get; init; }

    /// <summary>Re-encode marked blocks with zlib 1.3.1 level 7 instead of storing them raw.</summary>
    public bool Recompress { get; init; }

    /// <summary>Write strategy.</summary>
    public RepairMode Mode { get; init; } = RepairMode.Auto;

    /// <summary>Decode and encode threads.</summary>
    public int Workers { get; init; } = 1;

    /// <summary>Folder for <c>summary.json</c> and <c>bad_blocks.tsv</c>, or <see langword="null"/>.</summary>
    public string? ReportDirectory { get; init; }

    /// <summary>Zero the fixed-wrapper slack after repair (GC runs this before every validation).</summary>
    public bool CleanSlack { get; init; } = true;

    /// <summary>Progress sink.</summary>
    public IProgressSink? Progress { get; init; }

    /// <summary>Free bytes on the image volume; replaceable for tests.</summary>
    public Func<string, long> FreeSpace { get; init; } = PFSCRepair.AvailableFreeSpace;
}

/// <summary>Result of <see cref="PFSCRepair.Run"/>.</summary>
public sealed class PFSCRepairResult
{
    /// <summary>Outcome.</summary>
    public RepairStatus Status { get; internal set; }

    /// <summary>Failure reason.</summary>
    public string? Error { get; internal set; }

    /// <summary>Image path.</summary>
    public string Path { get; internal set; } = "";

    /// <summary>Nested file name.</summary>
    public string NestedName { get; internal set; } = "";

    /// <summary>Nested image kind.</summary>
    public PFSCNestedType NestedType { get; internal set; }

    /// <summary>PFSC logical size.</summary>
    public long LogicalSize { get; internal set; }

    /// <summary>Nested file size.</summary>
    public long NestedSize { get; internal set; }

    /// <summary>Number of blocks.</summary>
    public long BlockCount { get; internal set; }

    /// <summary>Blocks stored compressed.</summary>
    public long CompressedBlocks { get; internal set; }

    /// <summary>Compressed blocks the PS5 may decode wrongly.</summary>
    public long RiskyBlocks { get; internal set; }

    /// <summary>Blocks selected for repair.</summary>
    public IReadOnlyList<long> MarkedBlocks { get; internal set; } = [];

    /// <summary>Where the selection came from: <c>risky</c> or <c>bad-blocks</c>.</summary>
    public string Selection { get; internal set; } = "risky";

    /// <summary><c>.vhash</c> lookup result.</summary>
    public PFSCVHashMode HashMode { get; internal set; }

    /// <summary>Blocks whose decoded content differs from the sidecar.</summary>
    public IReadOnlyList<long> HashMismatches { get; internal set; } = [];

    /// <summary>Blocks that failed to decode.</summary>
    public IReadOnlyList<BlockError> DecodeErrors { get; internal set; } = [];

    /// <summary>Strategy used, or <see langword="null"/> when nothing was written.</summary>
    public RepairMode? AppliedMode { get; internal set; }

    /// <summary>Marked blocks are recompressed.</summary>
    public bool Recompress { get; internal set; }

    /// <summary>Stored payload size before.</summary>
    public long OldStoredSize { get; internal set; }

    /// <summary>Stored payload size after.</summary>
    public long NewStoredSize { get; internal set; }

    /// <summary>Payload bytes written.</summary>
    public long BytesMoved { get; internal set; }

    /// <summary>Blocks decoded and compared after repair.</summary>
    public long PostVerifyBlocks { get; internal set; }

    /// <summary>Image size required free for copy-replace (1.2 × image).</summary>
    public long RequiredFreeBytes { get; internal set; }

    /// <summary>Free bytes found on the volume.</summary>
    public long AvailableFreeBytes { get; internal set; }

    /// <summary>Outer slack cleanup result, when it ran.</summary>
    public OuterSlackResult? Slack { get; internal set; }
}

/// <summary>
/// Offline PFSC repair for single-file <c>.ffpfsc</c> images (port of PS5 Game Compressor <c>pfs_repair.c</c>).
/// GC finds bad blocks by comparing against the image mounted on a PS5; offline, blocks come from the stream
/// lint (<see cref="MkPFS.Core.Compression.DeflateInspector.IsRiskyForPS5"/>) or from a GC <c>bad_blocks.tsv</c>. Marked blocks are stored raw
/// (or recompressed with zlib), which the PS5 always decodes correctly.
/// </summary>
public static class PFSCRepair
{
    /// <summary>Suffix of the copy-replace temporary file.</summary>
    public const string TempSuffix = ".repair.tmp";

    /// <summary>Repair or scan an image.</summary>
    /// <param name="path">Image path.</param>
    /// <param name="options">Options.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Result; <see cref="RepairStatus.Failed"/> for images that cannot be repaired.</returns>
    /// <exception cref="InvalidDataException">The image is not a supported single-file <c>.ffpfsc</c>.</exception>
    public static PFSCRepairResult Run(string path, PFSCRepairOptions options, CancellationToken cancellationToken = default)
    {
        PFSCRepairResult result = new() { Path = path, Recompress = options.Recompress };
        int workers = Math.Max(1, options.Workers);
        RepairScanResult scan;
        bool[] marked;
        RepairPlan? plan = null;
        PFSCImage image = PFSCImage.Open(path);
        try
        {
            result.NestedName = image.NestedName;
            result.NestedType = image.NestedType;
            result.LogicalSize = image.LogicalSize;
            result.NestedSize = image.NestedSize;
            result.BlockCount = image.BlockCount;
            result.OldStoredSize = image.StoredSize;
            result.NewStoredSize = image.StoredSize;

            scan = RepairScanner.Scan(image, PFSCVHash.SidecarPath(path), workers, options.Progress, cancellationToken);
            result.CompressedBlocks = scan.CompressedBlocks;
            result.RiskyBlocks = scan.RiskyCount;
            result.HashMode = scan.HashMode;
            result.HashMismatches = scan.HashMismatches;
            result.DecodeErrors = scan.DecodeErrors;
            if (scan.DecodeErrors.Count > 0)
            {
                BlockError first = scan.DecodeErrors[0];
                return Fail(result, options, image, $"{scan.DecodeErrors.Count} block(s) do not decode (first: block {first.Block}: {first.Message}); the stored data is damaged and cannot be repaired offline");
            }

            if (scan.HashMismatches.Count > 0)
            {
                return Fail(result, options, image, $"{scan.HashMismatches.Count} block(s) differ from the .vhash sidecar (first: block {scan.HashMismatches[0]}); the stored data is damaged and cannot be repaired offline");
            }

            if (options.BadBlocksPath is not null)
            {
                result.Selection = "bad-blocks";
                marked = new bool[image.BlockCount];
                foreach (long block in BadBlockList.Read(options.BadBlocksPath, image.BlockCount))
                {
                    if (image.StoredLength(block) == PFSCImage.BlockSize)
                    {
                        // GC raw_mismatch: a raw block that reads differently cannot be fixed by storing it raw.
                        return Fail(result, options, image, $"block {block} is already stored raw; storing it raw again cannot change how it reads");
                    }

                    marked[block] = true;
                }
            }
            else
            {
                marked = scan.Risky;
            }

            result.MarkedBlocks = Enumerable.Range(0, marked.Length).Where(i => marked[i]).Select(i => (long)i).ToList();
            if (result.MarkedBlocks.Count > 0)
            {
                plan = RepairPlan.Create(image, marked, options.Recompress, workers);
                result.NewStoredSize = plan.NewStoredSize;
            }

            if (result.MarkedBlocks.Count == 0 || options.ScanOnly)
            {
                result.Status = result.MarkedBlocks.Count == 0 ? RepairStatus.Noop : RepairStatus.RepairNeeded;
                WriteReports(result, options, image);
                image.Dispose();
                if (options.CleanSlack)
                {
                    result.Slack = OuterSlack.Clean(path, dryRun: options.ScanOnly);
                }

                WriteSummary(result, options);
                return result;
            }

            if (CheckPayloadIsLast(image) is { } layoutError)
            {
                return Fail(result, options, image, layoutError);
            }

            System.Diagnostics.Debug.Assert(plan is not null, "marked blocks always have a plan");
            WriteReports(result, options, image);
            result.RequiredFreeBytes = RequiredFreeBytes(image.OuterSize);
            result.AvailableFreeBytes = options.FreeSpace(path);
            RepairMode mode = options.Mode;
            bool inPlaceOk = plan.SupportsInPlace(image);
            if (mode == RepairMode.Auto)
            {
                mode = result.AvailableFreeBytes >= result.RequiredFreeBytes ? RepairMode.Copy : RepairMode.InPlace;
            }

            if (mode == RepairMode.InPlace && !inPlaceOk)
            {
                return Fail(result, options, image, options.Mode == RepairMode.InPlace
                    ? "recompressed blocks shrink the payload, which in-place repair cannot do; use --mode copy or drop --recompress"
                    : $"not enough free space for copy-replace (need {result.RequiredFreeBytes} bytes, have {result.AvailableFreeBytes}) and recompressed blocks shrink the payload, which in-place repair cannot do");
            }

            result.AppliedMode = mode;
            if (mode == RepairMode.Copy)
            {
                string temp = path + TempSuffix;
                try
                {
                    result.BytesMoved = RepairApplier.CopyReplace(image, plan, temp, options.Progress, cancellationToken);
                    image.Dispose();
                    File.Move(temp, path, overwrite: true);
                }
                catch
                {
                    image.Dispose();
                    File.Delete(temp);
                    throw;
                }
            }
            else
            {
                image.Dispose();
                image = PFSCImage.Open(path, writable: true);
                result.BytesMoved = RepairApplier.ApplyInPlace(image, plan, options.Progress, cancellationToken);
                image.Dispose();
            }
        }
        finally
        {
            image.Dispose();
        }

        if (PostVerify(path, scan, plan, workers, options.Progress, cancellationToken) is { } verifyError)
        {
            result.Status = RepairStatus.Failed;
            result.Error = verifyError;
            WriteSummary(result, options);
            return result;
        }

        result.PostVerifyBlocks = result.BlockCount;
        if (options.CleanSlack)
        {
            result.Slack = OuterSlack.Clean(path);
        }

        result.Status = RepairStatus.Repaired;
        WriteSummary(result, options);
        return result;
    }

    /// <summary>Free space GC requires for copy-replace: 1.2 × image size, rounded up.</summary>
    /// <param name="sourceSize">Image size.</param>
    /// <returns>Bytes.</returns>
    public static long RequiredFreeBytes(long sourceSize) => checked(((sourceSize * 12) + 9) / 10);

    /// <summary>Free bytes available to the user on the volume that holds <paramref name="path"/>.</summary>
    /// <param name="path">File path.</param>
    /// <returns>Bytes, or 0 when unknown.</returns>
    public static long AvailableFreeSpace(string path)
    {
        try
        {
            string? root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path));
            return root is null ? 0 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Status name used in <c>summary.json</c> (GC strings).</summary>
    /// <param name="status">Status.</param>
    /// <returns>Name.</returns>
    public static string StatusName(RepairStatus status) => status switch
    {
        RepairStatus.Noop => "noop",
        RepairStatus.RepairNeeded => "repair-needed",
        RepairStatus.Repaired => "repaired",
        _ => "failed",
    };

    /// <summary>Hash mode name used in <c>summary.json</c> (GC <c>pfs_vhash_mode_name</c>).</summary>
    /// <param name="mode">Mode.</param>
    /// <returns>Name.</returns>
    public static string HashModeName(PFSCVHashMode mode) => mode switch
    {
        PFSCVHashMode.Used => "used",
        PFSCVHashMode.Stale => "stale",
        PFSCVHashMode.Invalid => "invalid",
        _ => "missing",
    };

    /// <summary>Strategy name used in <c>summary.json</c> (GC <c>repair_mode_name</c>).</summary>
    /// <param name="mode">Mode.</param>
    /// <returns>Name.</returns>
    public static string ModeName(RepairMode mode) => mode == RepairMode.Copy ? "copy-replace" : "in-place";

    // Repair rewrites final_ndblock from the payload end, so nothing may follow the payload.
    private static string? CheckPayloadIsLast(PFSCImage image)
    {
        byte[] raw = new byte[8];
        image.ReadAt(0x38, raw);
        long finalBlocks = BinaryPrimitives.ReadInt64LittleEndian(raw);
        long expected = RepairApplier.FinalSize(image, image.StoredSize) / PFSCImage.BlockSize;
        return finalBlocks == expected && image.OuterSize == expected * PFSCImage.BlockSize
            ? null
            : "the nested image is not the last region of the outer PFS; only single-file images are supported";
    }

    // Decode every block of the repaired image and compare with the content scanned before the repair.
    private static string? PostVerify(string path, RepairScanResult before, RepairPlan? plan, int workers, IProgressSink? progress, CancellationToken cancellationToken)
    {
        using PFSCImage image = PFSCImage.Open(path);
        if (image.BlockCount != before.BlockCount)
        {
            return $"post-repair verify: block count changed from {before.BlockCount} to {image.BlockCount}";
        }

        RepairScanResult after = RepairScanner.Scan(image, null, workers, progress, cancellationToken, "verify");
        if (after.DecodeErrors.Count > 0)
        {
            return $"post-repair verify: block {after.DecodeErrors[0].Block} does not decode: {after.DecodeErrors[0].Message}";
        }

        for (long i = 0; i < image.BlockCount; i++)
        {
            if (!after.HashOf(i).SequenceEqual(before.HashOf(i)))
            {
                return $"post-repair verify: block {i} content changed";
            }

            if (plan is not null && plan.Marked[i] && after.Risky[i])
            {
                return $"post-repair verify: repaired block {i} is still risky";
            }
        }

        return null;
    }

    private static PFSCRepairResult Fail(PFSCRepairResult result, PFSCRepairOptions options, PFSCImage image, string error)
    {
        result.Status = RepairStatus.Failed;
        result.Error = error;
        image.Dispose();
        WriteSummary(result, options);
        return result;
    }

    private static void WriteReports(PFSCRepairResult result, PFSCRepairOptions options, PFSCImage image)
    {
        if (options.ReportDirectory is null)
        {
            return;
        }

        Directory.CreateDirectory(options.ReportDirectory);
        byte[] stored = new byte[PFSCImage.BlockSize];
        byte[] decoded = new byte[PFSCImage.BlockSize];
        List<ulong> fnv = new(result.MarkedBlocks.Count);
        foreach (long block in result.MarkedBlocks)
        {
            image.DecodeBlock(block, stored, decoded);
            fnv.Add(PFSCImage.Fnv1a64(decoded.AsSpan(0, image.CompareLength(block))));
        }

        BadBlockList.Write(System.IO.Path.Combine(options.ReportDirectory, "bad_blocks.tsv"), image, result.MarkedBlocks, fnv);
    }

    private static void WriteSummary(PFSCRepairResult result, PFSCRepairOptions options)
    {
        if (options.ReportDirectory is null)
        {
            return;
        }

        Directory.CreateDirectory(options.ReportDirectory);
        using FileStream file = File.Create(System.IO.Path.Combine(options.ReportDirectory, "summary.json"));
        using Utf8JsonWriter json = new(file, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("status", StatusName(result.Status));
        json.WriteString("error", result.Error ?? "");
        json.WriteString("path", result.Path);
        json.WriteString("repairMode", result.AppliedMode is { } mode ? ModeName(mode) : "");
        json.WriteString("hashMode", HashModeName(result.HashMode));
        json.WriteString("nestedName", result.NestedName);
        json.WriteString("nestedType", result.NestedType switch { PFSCNestedType.PFS => "pfs", PFSCNestedType.Exfat => "exfat", _ => "unknown" });
        json.WriteString("selection", result.Selection);
        json.WriteBoolean("recompress", result.Recompress);
        json.WriteNumber("requiredFreeBytes", result.RequiredFreeBytes);
        json.WriteNumber("availableFreeBytes", result.AvailableFreeBytes);
        json.WriteNumber("logicalSize", result.LogicalSize);
        json.WriteNumber("nestedSize", result.NestedSize);
        json.WriteNumber("blockCount", result.BlockCount);
        json.WriteNumber("compressedBlocks", result.CompressedBlocks);
        json.WriteNumber("riskyBlocks", result.RiskyBlocks);
        json.WriteNumber("repairedBlocks", result.MarkedBlocks.Count);
        json.WriteNumber("hashCheckedBlocks", result.HashMode == PFSCVHashMode.Used ? result.BlockCount : 0);
        json.WriteNumber("hashMismatchedBlocks", result.HashMismatches.Count);
        json.WriteNumber("decodeErrors", result.DecodeErrors.Count);
        json.WriteNumber("postVerifyBlocks", result.PostVerifyBlocks);
        json.WriteNumber("oldStoredSize", result.OldStoredSize);
        json.WriteNumber("newStoredSize", result.NewStoredSize);
        json.WriteNumber("bytesMoved", result.BytesMoved);
        json.WriteBoolean("noop", result.Status == RepairStatus.Noop);
        if (result.Slack is { } slack)
        {
            json.WriteStartObject("outerSlack");
            json.WriteBoolean("applicable", slack.Applicable);
            json.WriteString("reason", slack.Reason ?? "");
            json.WriteNumber("fixedBytes", slack.FixedBytes);
            json.WriteEndObject();
        }

        json.WriteEndObject();
    }
}
