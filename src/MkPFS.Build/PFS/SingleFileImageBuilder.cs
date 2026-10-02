using System.Diagnostics;
using MkPFS.Build.Exfat;
using MkPFS.Core.Diagnostics;
using MkPFS.Core.PFS;
using MkPFS.Core.PFSC;
using MkPFS.Core.Util;

namespace MkPFS.Build.PFS;

/// <summary>Options for the single-file image builders (Python <c>build_pfs_stream_single_file</c> arguments).</summary>
public sealed record SingleFileBuildOptions
{
    /// <summary>Source file (<c>pack file</c>) or source folder (<c>pack folder</c>, exFAT-wrapped).</summary>
    public required string SourceFile { get; init; }

    /// <summary>Final image path.</summary>
    public required string OutputPath { get; init; }

    /// <summary>File name inside the image (defaults to the source name).</summary>
    public string? InnerFileName { get; init; }

    /// <summary>Filesystem block size.</summary>
    public int BlockSize { get; init; } = 65536;

    /// <summary>PFS version (1 = PS4, 2 = PS5).</summary>
    public long PFSVersion { get; init; } = PFSConstants.PFSVersionPS5;

    /// <summary>Case-insensitive mode bit.</summary>
    public bool CaseInsensitive { get; init; } = true;

    /// <summary>PFSC compression enabled.</summary>
    public bool Compress { get; init; } = true;

    /// <summary>zlib level.</summary>
    public int ZlibLevel { get; init; } = 7;

    /// <summary>Minimum per-block gain percent.</summary>
    public int ThresholdGain { get; init; }

    /// <summary>Minimum whole-file gain percent.</summary>
    public int MinFileGain { get; init; }

    /// <summary>Files below this size stay raw.</summary>
    public long MinCompressSize { get; init; }

    /// <summary>Resolved worker count (≥ 1).</summary>
    public int CpuCount { get; init; } = 1;

    /// <summary>Executable-like names stay raw.</summary>
    public bool SkipExecutableCompression { get; init; }

    /// <summary>Encrypt filesystem blocks with AES-XTS.</summary>
    public bool Encrypted { get; init; }

    /// <summary>Alternate key derivation.</summary>
    public bool NewCrypt { get; init; }

    /// <summary>EKPFS key (32 bytes), all zeros by default.</summary>
    public byte[]? Ekpfs { get; init; }

    /// <summary>Report the layout without writing.</summary>
    public bool DryRun { get; init; }

    /// <summary>Emit the per-file decision line.</summary>
    public bool Verbose { get; init; }

    /// <summary>Header and inode timestamp (Unix seconds).</summary>
    public long Timestamp { get; init; }

    /// <summary>Inner exFAT cluster size for <see cref="ExfatWrappedImageBuilder"/>, or <see langword="null"/> for the default.</summary>
    public int? ClusterSize { get; init; }
}

/// <summary>
/// Builds an unsigned single-file PFS from one file (<c>pack file</c>, port of Python <c>build_pfs_stream_single_file</c>).
/// The payload is PFSC when compression pays off, otherwise raw.
/// </summary>
public static class SingleFileImageBuilder
{
    /// <summary>Files at least this large compress with several workers (Python <c>PFSC_SINGLE_FILE_PARALLEL_MIN_SIZE</c>).</summary>
    public const long ParallelMinSize = 256L * 1024 * 1024;

    /// <summary>Build the image.</summary>
    /// <param name="options">Options.</param>
    /// <param name="log">Log for the verbose line and warnings.</param>
    /// <param name="progress">Progress and status lines.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Build statistics.</returns>
    /// <exception cref="BuildException">Invalid input or failed post-write validation.</exception>
    public static BuildStats Build(SingleFileBuildOptions options, IMkPFSLog log, IProgressSink? progress = null, CancellationToken cancellationToken = default)
    {
        Stopwatch watch = Stopwatch.StartNew();
        if (!File.Exists(options.SourceFile))
        {
            throw new BuildException($"source file does not exist: {options.SourceFile}");
        }

        string innerName = options.InnerFileName ?? Path.GetFileName(options.SourceFile);
        long rawSize = new FileInfo(options.SourceFile).Length;
        bool shouldCompress = options.Compress && rawSize > 0 && rawSize >= options.MinCompressSize &&
            !(options.SkipExecutableCompression && CompressionRules.ShouldSkipExecutable(innerName, innerName));
        int blockWorkers = rawSize < ParallelMinSize ? 1 : Math.Max(1, options.CpuCount);
        if (shouldCompress && blockWorkers > 1 &&
            PackEnvironment.NonLocalVolumeWarning(options.SourceFile, options.OutputPath, PathRules.ResolveTempRoot()) is { } nonLocal)
        {
            log.Warning(nonLocal, LogIcon.Warning);
        }

        PFSCEncodeOptions encodeOptions = new()
        {
            Level = options.ZlibLevel,
            ThresholdGain = options.ThresholdGain,
            MinFileGain = options.MinFileGain,
            Workers = blockWorkers,
        };

        if (options.DryRun)
        {
            PFSCEncodeResult? dry = null;
            if (shouldCompress)
            {
                using FileStream source = OpenSource(options.SourceFile);
                dry = PFSCEncoder.EncodeFile(source, rawSize, Stream.Null, 0, encodeOptions, cancellationToken: cancellationToken);
            }

            bool dryCompressed = dry?.IsCompressed == true;
            return WrapperImageWriter.Stats(options, rawSize, dryCompressed ? dry!.StoredSize : rawSize, dryCompressed,
                dry?.HypotheticalAllCompressedSize ?? 0, dry?.GainPercent ?? 0.0, watch, log);
        }

        WrapperImageWriter.Payload payload = WrapperImageWriter.Write(
            options, innerName, "single-file streaming builder",
            [$"\nWriting PFS image to {options.OutputPath} (streaming, no spool)..."],
            (output, payloadBase) =>
            {
                WrapperImageWriter.Payload result = new(rawSize, false, 0.0, 0);
                if (shouldCompress)
                {
                    progress?.Status($"\nCompressing 1 file ({Sizes.HumanReadable(rawSize)}) using {blockWorkers} CPU core{(blockWorkers != 1 ? "s" : "")}...");
                    using FileStream source = OpenSource(options.SourceFile);
                    PFSCEncodeResult encoded = PFSCEncoder.EncodeFile(source, rawSize, output, payloadBase, encodeOptions,
                        WrapperImageWriter.CompressProgress(progress, rawSize), cancellationToken);
                    result = new(encoded.StoredSize, encoded.IsCompressed, encoded.GainPercent, encoded.HypotheticalAllCompressedSize);
                }

                // Disabled, too small, or not worth compressing: store raw over the same region.
                if (!result.IsCompressed)
                {
                    using FileStream source = OpenSource(options.SourceFile);
                    output.Seek(payloadBase, SeekOrigin.Begin);
                    CopyExactly(source, output, rawSize);

                    // Python pads only up to the provisional single block; the final truncate zero-fills the rest.
                    if (rawSize < options.BlockSize)
                    {
                        output.Write(new byte[options.BlockSize - rawSize]);
                    }

                    result = result with { StoredSize = rawSize };
                }

                return result;
            },
            rawSize, progress);
        return WrapperImageWriter.Stats(options, rawSize, payload.StoredSize, payload.IsCompressed, payload.Hypothetical, payload.Gain, watch, log);
    }

    private static void CopyExactly(Stream source, Stream destination, long count)
    {
        byte[] buffer = new byte[1 << 20];
        for (long done = 0; done < count;)
        {
            int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, count - done));
            if (read == 0)
            {
                throw new BuildException("source file is shorter than expected (it changed while packing)");
            }

            destination.Write(buffer, 0, read);
            done += read;
        }
    }

    private static FileStream OpenSource(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
}

/// <summary>
/// Wraps a folder in an exFAT volume and compresses it straight into a single-file PFS (<c>pack folder</c> default,
/// port of Python <c>build_pfs_stream_from_exfat</c>). The exFAT streams forward into the PFSC encoder, so no
/// temporary <c>.exfat</c> is written. The inner file is <c>&lt;titleId&gt;.exfat</c>.
/// </summary>
public static class ExfatWrappedImageBuilder
{
    /// <summary>Build the image; <see cref="SingleFileBuildOptions.SourceFile"/> is the source folder.</summary>
    /// <param name="options">Options (compression and file filters do not apply; the payload is always PFSC).</param>
    /// <param name="log">Log for the verbose line.</param>
    /// <param name="progress">Progress and status lines.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Build statistics.</returns>
    public static BuildStats Build(SingleFileBuildOptions options, IMkPFSLog log, IProgressSink? progress = null, CancellationToken cancellationToken = default)
    {
        Stopwatch watch = Stopwatch.StartNew();
        string source = options.SourceFile;
        if (!Directory.Exists(source))
        {
            throw new BuildException($"source must be an existing directory: {source}");
        }

        string innerName = GameParams.DefaultImageBasename(source) + ".exfat";
        ExfatImageWriter exfat = ExfatImageWriter.Plan(source, options.ClusterSize);
        long rawSize = exfat.ImageSize;
        PFSCEncodeOptions encodeOptions = new()
        {
            Level = options.ZlibLevel,
            ThresholdGain = options.ThresholdGain,
            Workers = Math.Max(1, options.CpuCount),
        };

        WrapperImageWriter.Payload payload = WrapperImageWriter.Write(
            options, innerName, "exFAT streaming builder",
            [$"\nWrapping {source} into an exFAT and compressing to {options.OutputPath} (no temp image)...", $"Inner image: {innerName} ({Sizes.HumanReadable(rawSize)})"],
            (output, payloadBase) =>
            {
                progress?.Status($"\nCompressing inner exFAT ({Sizes.HumanReadable(rawSize)})...");
                using Stream stream = exfat.OpenRead();
                PFSCEncodeResult encoded = PFSCEncoder.EncodeStream(stream, rawSize, output, payloadBase, encodeOptions,
                    WrapperImageWriter.CompressProgress(progress, rawSize), cancellationToken);
                return new WrapperImageWriter.Payload(encoded.StoredSize, true, encoded.GainPercent, encoded.HypotheticalAllCompressedSize);
            },
            rawSize, progress);
        double gain = rawSize > 0 ? (double)(rawSize - payload.StoredSize) / rawSize * 100.0 : 0.0;
        return WrapperImageWriter.Stats(options, rawSize, payload.StoredSize, true, payload.Hypothetical, gain, watch, log);
    }
}

/// <summary>Shared layout of the four-inode single-file wrapper (superroot, flat_path_table, uroot, file).</summary>
internal static class WrapperImageWriter
{
    /// <summary>Payload outcome.</summary>
    public readonly record struct Payload(long StoredSize, bool IsCompressed, double Gain, long Hypothetical);

    /// <summary>
    /// Write header, inode table and directory blocks with provisional sizes, let <paramref name="writePayload"/> fill the
    /// payload region, then back-patch the file inode and <c>final_ndblock</c>, truncate, encrypt and validate.
    /// </summary>
    public static Payload Write(
        SingleFileBuildOptions options, string innerName, string builderName, string[] statuses,
        Func<Stream, long, Payload> writePayload, long rawSize, IProgressSink? progress)
    {
        int blockSize = options.BlockSize;
        long now = options.Timestamp;
        byte[] ekpfs = options.Ekpfs ?? PFSConstants.ZeroEkpfs.ToArray();
        WriteInode superRoot = new()
        {
            Number = 0, Mode = PFSConstants.InodeModeDir | PFSConstants.InodeRxOnly, Nlink = 1,
            Flags = PFSConstants.InodeFlagInternal | PFSConstants.InodeFlagReadOnly, Size = blockSize, SizeCompressed = blockSize, Blocks = 1, TimeSec = now,
        };
        WriteInode fpt = new()
        {
            Number = 1, Mode = PFSConstants.InodeModeFile | PFSConstants.InodeRxOnly, Nlink = 1,
            Flags = PFSConstants.InodeFlagInternal | PFSConstants.InodeFlagReadOnly, Size = 0, SizeCompressed = 0, Blocks = 1, TimeSec = now,
        };
        WriteInode uroot = new()
        {
            Number = 2, Mode = PFSConstants.InodeModeDir | PFSConstants.InodeRxOnly, Nlink = 3,
            Flags = PFSConstants.InodeFlagReadOnly, Size = blockSize, SizeCompressed = blockSize, Blocks = 1, TimeSec = now,
        };
        WriteInode file = new()
        {
            Number = 3, Mode = PFSConstants.InodeModeFile | PFSConstants.InodeRxOnly, Nlink = 1,
            Flags = PFSConstants.InodeFlagReadOnly, Size = 0, SizeCompressed = 0, Blocks = 1, TimeSec = now,
        };
        WriteInode[] inodes = [superRoot, fpt, uroot, file];

        (byte[] fptBlob, _, bool collision) = PFSWriter.FlatPathTables([("/" + innerName, file.Number, false)], options.CaseInsensitive);
        if (collision)
        {
            throw new BuildException($"unexpected FPT collision in {builderName}");
        }

        byte[] urootBlob = [
            .. PFSWriter.Dirent(uroot.Number, PFSConstants.DirentTypeDot, "."),
            .. PFSWriter.Dirent(uroot.Number, PFSConstants.DirentTypeDotDot, ".."),
            .. PFSWriter.Dirent(file.Number, PFSConstants.DirentTypeFile, innerName),
        ];
        byte[] superRootBlob = [
            .. PFSWriter.Dirent(fpt.Number, PFSConstants.DirentTypeFile, "flat_path_table"),
            .. PFSWriter.Dirent(uroot.Number, PFSConstants.DirentTypeDirectory, "uroot"),
        ];

        // Front layout: header, inode table, superroot, flat_path_table, reserved empty block, uroot, payload.
        int inodeBlockCount = (int)Sizes.CeilDiv(inodes.Length, blockSize / PFSConstants.InodeD32Size);
        long ndblock = 1 + inodeBlockCount;
        superRoot.Db[0] = ndblock;
        ndblock += superRoot.Blocks;
        fpt.Size = fptBlob.Length;
        fpt.SizeCompressed = fptBlob.Length;
        fpt.Blocks = (uint)Math.Max(1, Sizes.CeilDiv(fptBlob.Length, blockSize));
        SetContiguous(fpt, ndblock);
        ndblock += fpt.Blocks;
        ndblock++; // reserved empty block (no collision resolver)
        HashSet<long> reservedEmpty = [ndblock - 1];
        uroot.Blocks = (uint)Math.Max(1, Sizes.CeilDiv(urootBlob.Length, blockSize));
        uroot.Size = uroot.Blocks * blockSize;
        uroot.SizeCompressed = uroot.Size;
        SetContiguous(uroot, ndblock);
        ndblock += uroot.Blocks;
        SetContiguous(file, ndblock);
        long payloadBase = file.Db[0] * blockSize;
        ushort mode = PFSWriter.Mode(32, options.CaseInsensitive, signed: false, options.Encrypted);
        byte[] seed = PFSConstants.ZeroPFSSeed.ToArray();

        foreach (string status in statuses)
        {
            progress?.Status(status);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!);
        string tmpPath = options.OutputPath + ".tmp";
        Payload payload;
        long finalNdblock;
        try
        {
            using (FileStream output = new(tmpPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20))
            {
                // Provisional metadata; final_ndblock and the file inode are patched after the payload.
                output.Write(PFSWriter.HeaderBlock(blockSize, options.PFSVersion, mode, 1, inodes.Length, 0, inodeBlockCount, now, false, options.Encrypted, seed));
                output.Seek(blockSize, SeekOrigin.Begin);
                PFSWriter.WriteInodeTable(output, inodes, signed: false, 32, blockSize);
                WriteAt(output, superRoot.Db[0] * blockSize, superRootBlob);
                WriteAt(output, fpt.Db[0] * blockSize, fptBlob);
                WriteAt(output, uroot.Db[0] * blockSize, urootBlob);

                payload = writePayload(output, payloadBase);
                long stored = payload.StoredSize;
                file.Blocks = (uint)(stored > 0 ? Math.Max(1, Sizes.CeilDiv(stored, blockSize)) : 1);
                file.Size = stored;
                file.Flags = PFSConstants.InodeFlagReadOnly | (payload.IsCompressed ? PFSConstants.InodeFlagCompressed : 0);
                file.SizeCompressed = payload.IsCompressed ? rawSize : stored;
                finalNdblock = file.Db[0] + file.Blocks;
                if (finalNdblock > PFSConstants.Int32Max)
                {
                    throw new BuildException($"Image requires block index {finalNdblock}, exceeds D32 pointer limit {PFSConstants.Int32Max}");
                }

                output.Seek(0, SeekOrigin.Begin);
                output.Write(PFSWriter.HeaderBlock(blockSize, options.PFSVersion, mode, 1, inodes.Length, finalNdblock, inodeBlockCount, now, false, options.Encrypted, seed));
                output.Seek(blockSize, SeekOrigin.Begin);
                PFSWriter.WriteInodeTable(output, inodes, signed: false, 32, blockSize);
                output.SetLength(finalNdblock * blockSize);
                if (options.Encrypted)
                {
                    PFSWriter.EncryptFilesystem(output, blockSize, finalNdblock, ekpfs, seed, options.NewCrypt, reservedEmpty);
                }

                output.Flush(flushToDisk: true);
            }

            PFSWriter.ValidateQuick(tmpPath, blockSize, mode, options.PFSVersion, options.Encrypted ? ekpfs : null, options.NewCrypt);
            File.Move(tmpPath, options.OutputPath, overwrite: true);
            progress?.Status($"Successfully wrote {Sizes.HumanReadable(finalNdblock * blockSize)} image");
        }
        catch
        {
            // Remove the partial temp image on any failure, then rethrow.
            File.Delete(tmpPath);
            throw;
        }

        return payload;
    }

    /// <summary>Progress callback for the <c>compress</c> phase.</summary>
    public static Action<int> CompressProgress(IProgressSink? progress, long rawSize)
    {
        long processed = 0;
        long total = Math.Max(rawSize, 1);
        return delta =>
        {
            processed += delta;
            progress?.Report("compress", Math.Min(processed, total), total, processed);
        };
    }

    /// <summary>Statistics and the verbose decision line (Python <c>_single_file_build_stats</c>).</summary>
    public static BuildStats Stats(SingleFileBuildOptions options, long rawSize, long storedSize, bool isCompressed, long hypothetical, double gain, Stopwatch watch, IMkPFSLog log)
    {
        int blockSize = options.BlockSize;
        if (options.Verbose)
        {
            log.Info(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"[file] {Path.GetFileName(Path.TrimEndingDirectorySeparator(options.SourceFile))}: raw={rawSize} stored={storedSize} gain={gain:F2}% mode={(isCompressed ? "compressed" : "raw")}"), LogIcon.File);
        }

        return new BuildStats
        {
            InputPath = options.SourceFile,
            OutputPath = options.OutputPath,
            TotalFiles = 1,
            UncompressedTotalSize = rawSize,
            StoredTotalSize = storedSize,
            AllCompressedTotalSize = hypothetical,
            CompressedFiles = isCompressed ? 1 : 0,
            UncompressedFiles = isCompressed ? 0 : 1,
            BlockSize = blockSize,
            BlockAlignmentWaste = storedSize > 0 ? (Sizes.CeilDiv(storedSize, blockSize) * blockSize) - storedSize : blockSize,
            ElapsedSeconds = watch.Elapsed.TotalSeconds,
        };
    }

    private static void SetContiguous(WriteInode inode, long firstBlock)
    {
        inode.Db[0] = firstBlock;
        for (int i = 1; i < PFSConstants.MaxDirectBlocks; i++)
        {
            inode.Db[i] = -1;
        }
    }

    private static void WriteAt(Stream output, long offset, byte[] data)
    {
        output.Seek(offset, SeekOrigin.Begin);
        output.Write(data);
    }
}
