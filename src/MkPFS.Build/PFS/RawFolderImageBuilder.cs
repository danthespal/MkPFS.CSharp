using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using MkPFS.Core.Crypto;
using MkPFS.Core.Diagnostics;
using MkPFS.Core.IO;
using MkPFS.Core.PFS;
using MkPFS.Core.PFSC;
using MkPFS.Core.Util;

namespace MkPFS.Build.PFS;

/// <summary>Options for <see cref="RawFolderImageBuilder"/> (Python <c>build_pfs</c> arguments).</summary>
public sealed record RawFolderBuildOptions
{
    /// <summary>Source directory (or the source file when <see cref="SingleFileName"/> is set).</summary>
    public required string SourceRoot { get; init; }

    /// <summary>
    /// Pack <see cref="SourceRoot"/> as one file named this, like Python's single-file staging folder
    /// (<c>pack file</c> with options only the folder builder supports).
    /// </summary>
    public string? SingleFileName { get; init; }

    /// <summary>Final image path.</summary>
    public required string OutputPath { get; init; }

    /// <summary>Filesystem block size.</summary>
    public int BlockSize { get; init; } = 65536;

    /// <summary>PFS version (1 = PS4, 2 = PS5).</summary>
    public long PFSVersion { get; init; } = PFSConstants.PFSVersionPS5;

    /// <summary>Inode width mode bit (32 or 64); signed images then use S64 inodes.</summary>
    public int InodeBits { get; init; } = 32;

    /// <summary>Case-insensitive mode bit and flat_path_table hashing.</summary>
    public bool CaseInsensitive { get; init; } = true;

    /// <summary>Signed image (HMAC-SHA256 signatures, S32/S64 inodes).</summary>
    public bool Signed { get; init; }

    /// <summary>PFSC compression enabled.</summary>
    public bool Compress { get; init; } = true;

    /// <summary>Minimum per-block gain percent.</summary>
    public int ThresholdGain { get; init; }

    /// <summary>Resolved worker count (≥ 1).</summary>
    public int CpuCount { get; init; } = 1;

    /// <summary>zlib level.</summary>
    public int ZlibLevel { get; init; } = 7;

    /// <summary>Report the layout without writing.</summary>
    public bool DryRun { get; init; }

    /// <summary>Emit one decision line per file.</summary>
    public bool Verbose { get; init; }

    /// <summary>Encrypt filesystem blocks with AES-XTS.</summary>
    public bool Encrypted { get; init; }

    /// <summary>Alternate key derivation.</summary>
    public bool NewCrypt { get; init; }

    /// <summary>EKPFS key (32 bytes), all zeros by default.</summary>
    public byte[]? Ekpfs { get; init; }

    /// <summary>Executable-like files stay raw.</summary>
    public bool SkipExecutableCompression { get; init; }

    /// <summary>Minimum whole-file gain percent.</summary>
    public int MinFileGain { get; init; }

    /// <summary>Files below this size stay raw.</summary>
    public long MinCompressSize { get; init; }

    /// <summary>Folder for PFSC spool files (system temp when <see langword="null"/>).</summary>
    public string? TempFolder { get; init; }

    /// <summary>Header and inode timestamp (Unix seconds).</summary>
    public long Timestamp { get; init; }
}

/// <summary>
/// Builds a PFS image straight from a folder (<c>pack folder --raw</c>, port of Python <c>build_pfs</c>): one inode per
/// directory and file, flat_path_table (plus collision resolver when hashes collide), optional per-file PFSC,
/// signed (S32/S64 with HMAC-SHA256 block signatures) and encrypted (AES-XTS) variants.
/// </summary>
public static class RawFolderImageBuilder
{
    /// <summary>Block sizes tried by <c>--block-size auto-fit</c>.</summary>
    public static readonly int[] AutoFitCandidates = [0x1000, 0x2000, 0x4000, 0x8000, 0x10000];

    private const int SpoolCopyChunk = 1 << 20;

    /// <summary>Bytes the files occupy in whole blocks (empty files take one block).</summary>
    /// <param name="fileSizes">File sizes.</param>
    /// <param name="blockSize">Block size.</param>
    /// <returns>Footprint.</returns>
    public static long FileDataFootprint(IEnumerable<long> fileSizes, int blockSize) =>
        fileSizes.Sum(size => size > 0 ? Sizes.CeilDiv(size, blockSize) * blockSize : blockSize);

    /// <summary>Block size with the smallest footprint; ties go to the larger size (Python <c>choose_auto_fit_block_size</c>).</summary>
    /// <param name="fileSizes">Sizes of every file under the source.</param>
    /// <returns>Block size.</returns>
    public static int ChooseAutoFitBlockSize(IReadOnlyCollection<long> fileSizes) =>
        fileSizes.Count == 0
            ? PFSConstants.PFSCLogicalBlockSize
            : AutoFitCandidates.MinBy(candidate => (FileDataFootprint(fileSizes, candidate), -candidate));

    /// <summary>Upper bound of the PFSC spool for one file (Python <c>estimate_pfsc_spool_size</c>).</summary>
    /// <param name="rawSize">File size.</param>
    /// <returns>Bytes.</returns>
    public static long EstimateSpoolSize(long rawSize)
    {
        if (rawSize == 0)
        {
            return 0;
        }

        long blocks = Sizes.CeilDiv(rawSize, PFSConstants.PFSCLogicalBlockSize);
        return PFSCHeader.HeaderSize(blocks) + (blocks * PFSConstants.PFSCLogicalBlockSize);
    }

    /// <summary>Build the image.</summary>
    /// <param name="options">Options.</param>
    /// <param name="log">Log for warnings and verbose lines.</param>
    /// <param name="progress">Progress and status lines.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Build statistics.</returns>
    /// <exception cref="BuildException">Invalid input, a layout limit, or failed post-write validation.</exception>
    public static BuildStats Build(RawFolderBuildOptions options, IMkPFSLog log, IProgressSink? progress = null, CancellationToken cancellationToken = default)
    {
        Stopwatch watch = Stopwatch.StartNew();
        int blockSize = options.BlockSize;
        bool signed = options.Signed;
        string tempRoot = PathRules.ResolveTempRoot(options.TempFolder);
        int signedBits = signed && options.InodeBits == 64 ? 64 : 32;
        byte[] ekpfs = options.Ekpfs ?? PFSConstants.ZeroEkpfs.ToArray();
        byte[] seed = PFSConstants.ZeroPFSSeed.ToArray();
        if (options.MinFileGain is < 0 or > 100)
        {
            throw new BuildException("min_file_gain must be within 0..100");
        }

        if (options.MinCompressSize < 0)
        {
            throw new BuildException("min_compress_size must be non-negative");
        }

        (Dictionary<string, DirNode> dirs, Dictionary<string, FileNode> files) = ScanSourceTree(options.SourceRoot, options.SingleFileName, progress);
        List<DirNode> dirsSorted = [.. dirs.Values.OrderBy(d => d.RelDir.ToLowerInvariant(), StringComparer.Ordinal)];
        List<FileNode> filesSorted = [.. files.Values.OrderBy(f => f.RelPath.ToLowerInvariant(), StringComparer.Ordinal)];
        List<string> spools = [];
        try
        {
            PlanStorage(options, filesSorted, tempRoot, spools, log, progress, cancellationToken);
            return Write(options, log, progress, watch, dirs, files, dirsSorted, filesSorted, signedBits, ekpfs, seed, spools);
        }
        finally
        {
            foreach (string spool in spools)
            {
                try
                {
                    File.Delete(spool);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Preserve the build failure and attempt cleanup of every remaining spool.
                }
            }
        }
    }

    private static BuildStats Write(
        RawFolderBuildOptions options, IMkPFSLog log, IProgressSink? progress, Stopwatch watch,
        Dictionary<string, DirNode> dirs, Dictionary<string, FileNode> files, List<DirNode> dirsSorted, List<FileNode> filesSorted,
        int signedBits, byte[] ekpfs, byte[] seed, List<string> spools)
    {
        int blockSize = options.BlockSize;
        bool signed = options.Signed;
        long now = options.Timestamp;
        uint readOnly = signed ? 0 : PFSConstants.InodeFlagReadOnly;
        uint signedExtra = signed ? PFSConstants.InodeFlagSignedExtra : 0;

        // Inodes: superroot, flat_path_table, uroot, directories, files (collision_resolver inserted below).
        WriteInode superRoot = new()
        {
            Number = 0, Mode = PFSConstants.InodeModeDir | PFSConstants.InodeRxOnly, Nlink = 1,
            Flags = PFSConstants.InodeFlagInternal | readOnly | signedExtra, Size = blockSize, SizeCompressed = blockSize, Blocks = 1, TimeSec = now,
        };
        WriteInode fpt = new()
        {
            Number = 1, Mode = PFSConstants.InodeModeFile | PFSConstants.InodeRxOnly, Nlink = 1,
            Flags = PFSConstants.InodeFlagInternal | readOnly | signedExtra, Size = 0, SizeCompressed = 0, Blocks = 1, TimeSec = now,
        };
        WriteInode uroot = new()
        {
            Number = 2, Mode = PFSConstants.InodeModeDir | PFSConstants.InodeRxOnly, Nlink = 3,
            Flags = readOnly | signedExtra, Size = blockSize, SizeCompressed = blockSize, Blocks = 1, TimeSec = now,
        };
        List<WriteInode> inodes = [superRoot, fpt, uroot];
        dirs[""].Inode = uroot;
        long next = 3;
        List<DirNode> nonRootDirs = [.. dirsSorted.Where(d => d.RelDir.Length > 0)];
        foreach (DirNode d in nonRootDirs)
        {
            d.Inode = new WriteInode
            {
                Number = next++, Mode = PFSConstants.InodeModeDir | PFSConstants.InodeRxOnly, Nlink = 2,
                Flags = PFSConstants.InodeFlagReadOnly | signedExtra, Size = blockSize, SizeCompressed = blockSize, Blocks = 1, TimeSec = now,
            };
            inodes.Add(d.Inode);
        }

        foreach (FileNode f in filesSorted)
        {
            f.Inode = new WriteInode
            {
                Number = next++, Mode = PFSConstants.InodeModeFile | PFSConstants.InodeRxOnly, Nlink = 1,
                Flags = PFSConstants.InodeFlagReadOnly | (f.Compressed ? PFSConstants.InodeFlagCompressed : 0) | signedExtra,
                Size = f.StoredSize, SizeCompressed = f.Compressed ? f.RawSize : f.StoredSize,
                Blocks = (uint)(f.StoredSize > 0 ? Math.Max(1, Sizes.CeilDiv(f.StoredSize, blockSize)) : 1), TimeSec = now,
            };
            inodes.Add(f.Inode);
        }

        // Directory entries reference inode objects so a later renumbering carries through.
        foreach (DirNode d in dirsSorted)
        {
            DirNode parent = dirs[d.ParentRelDir ?? string.Empty];
            d.Dirents.Add((d.Inode!, PFSConstants.DirentTypeDot, "."));
            d.Dirents.Add((d.RelDir.Length > 0 ? parent.Inode! : d.Inode!, PFSConstants.DirentTypeDotDot, ".."));
            foreach (string child in d.ChildrenDirs)
            {
                d.Dirents.Add((dirs[child].Inode!, PFSConstants.DirentTypeDirectory, dirs[child].Name));
                d.Inode!.Nlink++;
            }

            foreach (string child in d.ChildrenFiles)
            {
                d.Dirents.Add((files[child].Inode!, PFSConstants.DirentTypeFile, files[child].Name));
            }
        }

        List<(string Path, WriteInode Inode, bool IsDir)> pathEntries =
        [
            .. nonRootDirs.Select(d => ("/" + d.RelDir, d.Inode!, true)),
            .. filesSorted.Select(f => ("/" + f.RelPath, f.Inode!, false)),
        ];
        WriteInode? collision = null;
        if (pathEntries.Select(e => FlatPathTable.Hash(e.Path, options.CaseInsensitive)).Distinct().Count() != pathEntries.Count)
        {
            collision = new WriteInode
            {
                Number = next, Mode = PFSConstants.InodeModeFile | PFSConstants.InodeRxOnly, Nlink = 1,
                Flags = PFSConstants.InodeFlagInternal | readOnly | signedExtra, Size = 0, SizeCompressed = 0, Blocks = 1, TimeSec = now,
            };

            // collision_resolver takes inode 2; everything after it shifts by one.
            inodes = [superRoot, fpt, collision, .. inodes.Skip(2)];
        }

        // Number inodes by table position (shifts everything after collision_resolver).
        for (int i = 0; i < inodes.Count; i++)
        {
            inodes[i].Number = i;
        }

        (byte[] fptBlob, byte[]? collisionBlob, bool hasCollision) = PFSWriter.FlatPathTables(
            pathEntries.Select(e => (e.Path, e.Inode.Number, e.IsDir)), options.CaseInsensitive);
        if (collision is not null)
        {
            collision.Size = collisionBlob?.Length ?? 0;
            collision.SizeCompressed = collision.Size;
            collision.Blocks = (uint)Math.Max(1, Sizes.CeilDiv(collision.Size, blockSize));
        }

        List<byte[]> superRootDirents = [PFSWriter.Dirent(fpt.Number, PFSConstants.DirentTypeFile, "flat_path_table")];
        if (hasCollision && collision is not null)
        {
            superRootDirents.Add(PFSWriter.Dirent(collision.Number, PFSConstants.DirentTypeFile, "collision_resolver"));
        }

        superRootDirents.Add(PFSWriter.Dirent(uroot.Number, PFSConstants.DirentTypeDirectory, "uroot"));

        int inodeSize = PFSWriter.InodeSize(signed, signedBits);
        long inodeBlockCount = Sizes.CeilDiv(inodes.Count, blockSize / inodeSize);

        // Payload list: root directory, other directories, then files.
        List<PayloadNode> nodes = [new(dirs[""].Inode!, DirentBlob(dirs[""]), null, true)];
        nodes.AddRange(nonRootDirs.Select(d => new PayloadNode(d.Inode!, DirentBlob(d), null, true)));
        nodes.AddRange(filesSorted.Select(f => new PayloadNode(f.Inode!, null, f, false)));

        // Block layout.
        List<SignatureTarget> targets = [];
        Dictionary<long, List<long>> indirectRecords = [];
        HashSet<long> reservedEmpty = [];
        long ndblock;
        if (signed)
        {
            long maxSigned = SignedCapacityBytes(blockSize, signedBits);
            if (maxSigned <= 0)
            {
                throw new BuildException("Block size too small for signed PFS layout");
            }

            foreach (FileNode f in filesSorted.Where(f => f.StoredSize > maxSigned))
            {
                throw new BuildException($"Signed mode cannot represent file '{f.RelPath}' with block size {blockSize}; max supported stored payload is {maxSigned} bytes");
            }

            ndblock = 1;
            for (int i = 0; i < inodeBlockCount; i++)
            {
                targets.Add(new SignatureTarget(1 + i, 0xB8 + (40 * i), blockSize, 3));
            }

            ndblock += inodeBlockCount;
            superRoot.Blocks = 1;
            ndblock = AssignSigned(superRoot, superRoot.Blocks, blockSize, signedBits, ndblock, targets, indirectRecords);
            fpt.Size = fptBlob.Length;
            fpt.SizeCompressed = fptBlob.Length;
            fpt.Blocks = (uint)Math.Max(1, Sizes.CeilDiv(fptBlob.Length, blockSize));
            ndblock = AssignSigned(fpt, fpt.Blocks, blockSize, signedBits, ndblock, targets, indirectRecords);
            if (hasCollision && collision is not null)
            {
                ndblock = AssignSigned(collision, collision.Blocks, blockSize, signedBits, ndblock, targets, indirectRecords);
            }

            ndblock += 2;
            reservedEmpty.Add(ndblock - 2);
            reservedEmpty.Add(ndblock - 1);
            foreach (PayloadNode node in nodes)
            {
                SetPayloadSize(node, blockSize);
                ndblock = AssignSigned(node.Inode, node.Inode.Blocks, blockSize, signedBits, ndblock, targets, indirectRecords);
            }

            targets.Add(new SignatureTarget(0, PFSConstants.HeaderDigestOffset, PFSConstants.HeaderDigestSize, 4));
        }
        else
        {
            ndblock = 1 + inodeBlockCount;
            superRoot.Db[0] = ndblock;
            ndblock += superRoot.Blocks;
            fpt.Size = fptBlob.Length;
            fpt.SizeCompressed = fptBlob.Length;
            fpt.Blocks = (uint)Math.Max(1, Sizes.CeilDiv(fptBlob.Length, blockSize));
            SetContiguous(fpt, ndblock);
            ndblock += fpt.Blocks;
            if (hasCollision && collision is not null)
            {
                SetContiguous(collision, ndblock);
                ndblock += collision.Blocks;
            }
            else
            {
                ndblock++;
                reservedEmpty.Add(ndblock - 1);
            }

            foreach (PayloadNode node in nodes)
            {
                SetPayloadSize(node, blockSize);
                SetContiguous(node.Inode, ndblock);
                ndblock += node.Inode.Blocks;
            }
        }

        long finalNdblock = ndblock;
        ValidateD32Ranges(inodes, finalNdblock);

        BuildStats stats = new()
        {
            InputPath = options.SourceRoot,
            OutputPath = options.OutputPath,
            TotalFiles = filesSorted.Count,
            UncompressedTotalSize = filesSorted.Sum(f => f.RawSize),
            StoredTotalSize = filesSorted.Sum(f => f.StoredSize),
            AllCompressedTotalSize = filesSorted.Sum(f => f.Hypothetical),
            CompressedFiles = filesSorted.Count(f => f.Compressed),
            BlockSize = blockSize,
            BlockAlignmentWaste = filesSorted.Sum(f => f.StoredSize > 0 ? (Sizes.CeilDiv(f.StoredSize, blockSize) * blockSize) - f.StoredSize : blockSize),
        };
        stats.UncompressedFiles = stats.TotalFiles - stats.CompressedFiles;
        if (options.Verbose)
        {
            foreach (FileNode f in filesSorted)
            {
                log.Info(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"[file] {f.RelPath}: raw={f.RawSize} stored={f.StoredSize} gain={f.Gain:F2}% mode={(f.Compressed ? "compressed" : "raw")}"), LogIcon.File);
            }
        }

        if (options.DryRun)
        {
            stats.ElapsedSeconds = watch.Elapsed.TotalSeconds;
            return stats;
        }

        ushort mode = PFSWriter.Mode(options.InodeBits, options.CaseInsensitive, signed, options.Encrypted);
        progress?.Status($"\nWriting PFS image to {options.OutputPath}...");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!);
        string tmpPath = options.OutputPath + ".tmp";
        long imageSize = finalNdblock * blockSize;
        try
        {
            using (FileStream output = new(tmpPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20))
            {
                output.SetLength(imageSize);
                output.Write(PFSWriter.HeaderBlock(blockSize, options.PFSVersion, mode, 1, inodes.Count, finalNdblock, inodeBlockCount, now, signed, options.Encrypted, seed));
                output.Seek(blockSize, SeekOrigin.Begin);
                PFSWriter.WriteInodeTable(output, inodes, signed, signedBits, blockSize);
                output.Seek(blockSize * (inodeBlockCount + 1), SeekOrigin.Begin);
                foreach (byte[] dirent in superRootDirents)
                {
                    output.Write(dirent);
                }

                if (signed)
                {
                    WriteToBlocks(output, fptBlob, SignedBlocks(fpt, blockSize, indirectRecords, signedBits), blockSize);
                    if (hasCollision && collision is not null && collisionBlob is not null)
                    {
                        WriteToBlocks(output, collisionBlob, SignedBlocks(collision, blockSize, indirectRecords, signedBits), blockSize);
                    }

                    foreach ((long block, List<long> records) in indirectRecords)
                    {
                        output.Seek(block * blockSize, SeekOrigin.Begin);
                        output.Write(SigRecordsBlob(records, blockSize, signedBits));
                    }
                }
                else
                {
                    WriteAt(output, fpt.Db[0] * blockSize, fptBlob);
                    if (hasCollision && collision is not null && collisionBlob is not null)
                    {
                        WriteAt(output, collision.Db[0] * blockSize, collisionBlob);
                    }
                }

                long totalWrite = nodes.Sum(n => n.PayloadSize);
                long written = 0;
                foreach (PayloadNode node in nodes)
                {
                    List<long>? blocks = signed ? SignedBlocks(node.Inode, blockSize, indirectRecords, signedBits) : null;
                    if (node.Bytes is not null)
                    {
                        if (blocks is not null)
                        {
                            WriteToBlocks(output, node.Bytes, blocks, blockSize);
                        }
                        else
                        {
                            WriteAt(output, node.Inode.Db[0] * blockSize, node.Bytes);
                        }
                    }
                    else
                    {
                        CopySource(output, node.File!.StoredSourcePath!, node.PayloadSize, blocks ?? [node.Inode.Db[0]], blocks is null ? long.MaxValue : blockSize, blockSize);
                    }

                    written += node.PayloadSize;
                    progress?.Report("write", written, totalWrite, written);
                }

                if (signed)
                {
                    Sign(output, targets, PFSKeys.SignKey(ekpfs, seed), blockSize);
                }

                if (options.Encrypted)
                {
                    PFSWriter.EncryptFilesystem(output, blockSize, finalNdblock, ekpfs, seed, options.NewCrypt, reservedEmpty);
                }

                output.Flush(flushToDisk: true);
            }

            PFSWriter.ValidateQuick(tmpPath, blockSize, mode, options.PFSVersion, options.Encrypted ? ekpfs : null, options.NewCrypt);
            File.Move(tmpPath, options.OutputPath, overwrite: true);
            progress?.Status($"Successfully wrote {Sizes.HumanReadable(imageSize)} image");
        }
        catch
        {
            File.Delete(tmpPath);
            throw;
        }

        stats.ElapsedSeconds = watch.Elapsed.TotalSeconds;
        return stats;
    }

    // Python scan_source_tree: sorted by lower-cased relative path; ASCII names only.
    private static (Dictionary<string, DirNode> Dirs, Dictionary<string, FileNode> Files) ScanSourceTree(string root, string? singleFileName, IProgressSink? progress)
    {
        progress?.Status("\nDiscovering files...");
        string fullRoot = Path.GetFullPath(root);
        List<(string Abs, string Rel)> found = singleFileName is not null
            ? [(fullRoot, singleFileName)]
            : [.. SourceScanner.GatherFiles(fullRoot)
                .Select(abs => (abs, Path.GetRelativePath(fullRoot, abs).Replace('\\', '/')))
                .OrderBy(e => e.Item2.ToLowerInvariant(), StringComparer.Ordinal)];
        List<string> nonAscii = [.. found.Where(e => !Ascii.IsValid(e.Rel)).Select(e => e.Rel)];
        if (nonAscii.Count > 0)
        {
            throw new BuildException($"Source tree contains {nonAscii.Count} file(s) with non-ASCII names. PFS images only support ASCII filenames:\n  {string.Join("\n  ", nonAscii)}");
        }

        Dictionary<string, DirNode> dirs = new(StringComparer.Ordinal) { [""] = new DirNode(string.Empty, "uroot", null) };
        Dictionary<string, FileNode> files = new(StringComparer.Ordinal);
        long totalBytes = 0;
        int index = 0;
        foreach ((string abs, string rel) in found)
        {
            string[] parts = rel.Split('/');
            string current = string.Empty;
            foreach (string part in parts[..^1])
            {
                string nextRel = current.Length > 0 ? $"{current}/{part}" : part;
                if (!dirs.ContainsKey(nextRel))
                {
                    dirs[nextRel] = new DirNode(nextRel, part, current);
                    dirs[current].ChildrenDirs.Add(nextRel);
                }

                current = nextRel;
            }

            long size = new FileInfo(abs).Length;
            totalBytes += size;
            files[rel] = new FileNode(rel, abs, parts[^1], size);
            dirs[current].ChildrenFiles.Add(rel);
            progress?.Report("scan", ++index, found.Count, totalBytes);
        }

        foreach (DirNode d in dirs.Values)
        {
            d.ChildrenDirs.Sort((a, b) => string.CompareOrdinal(a.ToLowerInvariant(), b.ToLowerInvariant()));
            d.ChildrenFiles.Sort((a, b) => string.CompareOrdinal(a.ToLowerInvariant(), b.ToLowerInvariant()));
        }

        return (dirs, files);
    }

    // Decide raw or PFSC per file; compressed payloads go to spool files in the temp folder.
    private static void PlanStorage(RawFolderBuildOptions options, List<FileNode> filesSorted, string tempRoot, List<string> spools, IMkPFSLog log, IProgressSink? progress, CancellationToken cancellationToken)
    {
        List<FileNode> candidates = [];
        if (options.Compress)
        {
            foreach (FileNode f in filesSorted)
            {
                if ((options.SkipExecutableCompression && CompressionRules.ShouldSkipExecutable(f.Name, f.RelPath)) ||
                    (options.MinCompressSize > 0 && f.RawSize < options.MinCompressSize))
                {
                    f.StoreRaw(hypothetical: f.RawSize);
                }
                else
                {
                    candidates.Add(f);
                }
            }
        }

        // No compression, or nothing eligible: Python then stores every file raw and reports no PFSC estimate.
        if (candidates.Count == 0)
        {
            if (filesSorted.Count == 0)
            {
                return;
            }

            long total = filesSorted.Sum(f => f.RawSize);
            progress?.Status($"\nReading {filesSorted.Count} files ({Sizes.HumanReadable(total)})...");
            long done = 0;
            foreach (FileNode f in filesSorted)
            {
                f.StoreRaw(hypothetical: 0);
                done += f.RawSize;
                progress?.Report("read", done, total, done);
            }

            return;
        }

        long totalBytes = candidates.Sum(f => f.RawSize);
        int workers = Math.Max(1, options.CpuCount);
        if (workers > 1 && PackEnvironment.NonLocalVolumeWarning(options.SourceRoot, options.OutputPath, tempRoot) is { } nonLocal)
        {
            log.Warning(nonLocal, LogIcon.Warning);
        }

        progress?.Status($"\nCompressing {candidates.Count} files ({Sizes.HumanReadable(totalBytes)}) using {workers} CPU core{(workers != 1 ? "s" : "")}...");
        long progressTotal = totalBytes > 0 ? totalBytes : candidates.Count;
        long processed = 0;
        object gate = new();
        progress?.Report("compress", 0, progressTotal, 0);

        // Files compress in parallel; results do not depend on the order or on worker counts.
        // One file at a time per worker; range partitioning of the list can leave large files queued on one thread.
        Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(candidates, System.Collections.Concurrent.EnumerablePartitionerOptions.NoBuffering), new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken }, f =>
        {
            if (f.RawSize == 0)
            {
                f.StoreRaw(hypothetical: f.RawSize);
            }
            else
            {
                PFSCEncodeOptions encode = new()
                {
                    Level = options.ZlibLevel,
                    ThresholdGain = options.ThresholdGain,
                    MinFileGain = options.MinFileGain,
                    Workers = f.RawSize >= SingleFileImageBuilder.ParallelMinSize ? workers : 1,
                };
                void Report(int delta)
                {
                    lock (gate)
                    {
                        processed += delta;
                        progress?.Report("compress", Math.Min(processed, progressTotal), progressTotal, totalBytes > 0 ? processed : 0);
                    }
                }

                using FileStream source = new(f.AbsPath, FileMode.Open, FileAccess.Read, FileShare.Read, SpoolCopyChunk, FileOptions.SequentialScan);
                PFSCEncodeResult result;
                if (options.DryRun)
                {
                    result = PFSCEncoder.EncodeFile(source, f.RawSize, Stream.Null, 0, encode, Report, cancellationToken);
                    f.StoredSourcePath = f.AbsPath;
                }
                else
                {
                    string spool = Path.Combine(tempRoot, $"mkpfs-{Path.GetFileName(f.AbsPath).Replace(' ', '_')}.{Guid.NewGuid():N}.pfsc");
                    lock (spools)
                    {
                        spools.Add(spool);
                    }

                    using (FileStream output = new(spool, FileMode.Create, FileAccess.ReadWrite, FileShare.None, SpoolCopyChunk))
                    {
                        result = PFSCEncoder.EncodeFile(source, f.RawSize, output, 0, encode, Report, cancellationToken);
                        output.SetLength(result.StoredSize);
                    }

                    if (result.IsCompressed)
                    {
                        f.StoredSourcePath = spool;
                    }
                    else
                    {
                        File.Delete(spool);
                        f.StoredSourcePath = f.AbsPath;
                    }
                }

                f.StoredSize = result.StoredSize;
                f.Compressed = result.IsCompressed;
                f.Gain = result.GainPercent;
                f.Hypothetical = result.HypotheticalAllCompressedSize;
            }

            // Without bytes to count (only empty files), progress counts files.
            if (totalBytes == 0)
            {
                lock (gate)
                {
                    processed++;
                    progress?.Report("compress", processed, progressTotal, 0);
                }
            }
        });

        // Finish the bar only when the per-block updates did not reach the end (Python's final step rule).
        if (processed < progressTotal)
        {
            progress?.Report("compress", progressTotal, progressTotal, totalBytes > 0 ? totalBytes : 0);
        }
    }

    private static byte[] DirentBlob(DirNode d)
    {
        using MemoryStream blob = new();
        foreach ((WriteInode inode, int type, string name) in d.Dirents)
        {
            blob.Write(PFSWriter.Dirent(inode.Number, type, name));
        }

        return blob.ToArray();
    }

    private static void SetPayloadSize(PayloadNode node, int blockSize)
    {
        long size = node.PayloadSize;
        uint blocks = (uint)(size > 0 ? Math.Max(1, Sizes.CeilDiv(size, blockSize)) : 1);
        node.Inode.Blocks = blocks;
        if (node.IsDir)
        {
            node.Inode.Size = (long)blocks * blockSize;
            node.Inode.SizeCompressed = node.Inode.Size;
        }
        else
        {
            node.Inode.Size = size;
            if ((node.Inode.Flags & PFSConstants.InodeFlagCompressed) == 0)
            {
                node.Inode.SizeCompressed = size;
            }
        }
    }

    private static void SetContiguous(WriteInode inode, long firstBlock)
    {
        inode.Db[0] = firstBlock;
        for (int i = 1; i < PFSConstants.MaxDirectBlocks; i++)
        {
            inode.Db[i] = -1;
        }
    }

    // Python validate_d32_ranges (only the checks a C# layout can violate).
    private static void ValidateD32Ranges(List<WriteInode> inodes, long finalNdblock)
    {
        if (finalNdblock > PFSConstants.Int32Max)
        {
            throw new BuildException($"Image requires block index {finalNdblock}, exceeds D32 pointer limit {PFSConstants.Int32Max}");
        }

        foreach (WriteInode inode in inodes)
        {
            foreach (long pointer in inode.Db)
            {
                if (pointer is < -1 or > PFSConstants.Int32Max)
                {
                    throw new BuildException($"Direct block pointer {pointer} out of int32 range");
                }
            }

            foreach (long pointer in inode.Ib)
            {
                if (pointer is < -1 or > PFSConstants.Int32Max)
                {
                    throw new BuildException($"Indirect block pointer {pointer} out of int32 range");
                }
            }
        }
    }

    // Python signed_inode_capacity_bytes: 12 direct + one indirect level + one double-indirect level.
    private static long SignedCapacityBytes(int blockSize, int inodeBits)
    {
        long perBlock = blockSize / SignedInodeLayout.For(inodeBits).EntrySize;
        return perBlock <= 0 ? 0 : (12 + perBlock + (perBlock * perBlock)) * blockSize;
    }

    private static long SignedInodeSigOffset(long inodeNumber, int pointerIndex, int blockSize, int inodeBits)
    {
        SignedInodeLayout layout = SignedInodeLayout.For(inodeBits);
        long perBlock = blockSize / layout.InodeSize;
        if (perBlock <= 0)
        {
            throw new BuildException("block size too small for signed inode table");
        }

        long inodeOffset = blockSize + ((inodeNumber / perBlock) * blockSize) + ((inodeNumber % perBlock) * layout.InodeSize);
        return inodeOffset + layout.PointerTableOffset + (pointerIndex * layout.EntrySize);
    }

    // Python assign_signed_inode_layout: direct blocks, then ib[0] records, then ib[1] -> child record blocks.
    private static long AssignSigned(WriteInode inode, long blockCount, int blockSize, int inodeBits, long nextBlock, List<SignatureTarget> targets, Dictionary<long, List<long>> records)
    {
        SignedInodeLayout layout = SignedInodeLayout.For(inodeBits);
        long perBlock = blockSize / layout.EntrySize;
        if (perBlock <= 0)
        {
            throw new BuildException("Block size too small for signed pointer records");
        }

        if (blockCount > 12 + perBlock + (perBlock * perBlock))
        {
            throw new BuildException($"Signed inode {inode.Number} requires {blockCount} blocks, exceeds current signed layout capacity");
        }

        Array.Clear(inode.Db);
        Array.Clear(inode.Ib);
        long direct = Math.Min(blockCount, PFSConstants.MaxDirectBlocks);
        for (int i = 0; i < direct; i++)
        {
            inode.Db[i] = nextBlock;
            targets.Add(new SignatureTarget(nextBlock, SignedInodeSigOffset(inode.Number, i, blockSize, inodeBits), blockSize, 0));
            nextBlock++;
        }

        long remaining = blockCount - direct;
        if (remaining <= 0)
        {
            return nextBlock;
        }

        long ib0 = nextBlock++;
        inode.Ib[0] = ib0;
        targets.Add(new SignatureTarget(ib0, SignedInodeSigOffset(inode.Number, 12, blockSize, inodeBits), blockSize, 1));
        List<long> ib0Children = [];
        long simple = Math.Min(remaining, perBlock);
        for (long i = 0; i < simple; i++)
        {
            long child = nextBlock++;
            targets.Add(new SignatureTarget(child, (ib0 * blockSize) + (ib0Children.Count * layout.EntrySize), blockSize, 0));
            ib0Children.Add(child);
        }

        records[ib0] = ib0Children;
        remaining -= simple;
        if (remaining <= 0)
        {
            return nextBlock;
        }

        long ib1 = nextBlock++;
        inode.Ib[1] = ib1;
        targets.Add(new SignatureTarget(ib1, SignedInodeSigOffset(inode.Number, 13, blockSize, inodeBits), blockSize, 2));
        List<long> ib1Children = [];
        for (long idx = 0; idx < perBlock && remaining > 0; idx++)
        {
            long childIndirect = nextBlock++;
            ib1Children.Add(childIndirect);
            targets.Add(new SignatureTarget(childIndirect, (ib1 * blockSize) + (idx * layout.EntrySize), blockSize, 1));
            List<long> childRecords = [];
            long count = Math.Min(remaining, perBlock);
            for (long r = 0; r < count; r++)
            {
                long data = nextBlock++;
                childRecords.Add(data);
                targets.Add(new SignatureTarget(data, (childIndirect * blockSize) + (r * layout.EntrySize), blockSize, 0));
            }

            records[childIndirect] = childRecords;
            remaining -= count;
        }

        records[ib1] = ib1Children;
        return nextBlock;
    }

    // Python collect_signed_block_numbers: payload blocks in order.
    private static List<long> SignedBlocks(WriteInode inode, int blockSize, Dictionary<long, List<long>> records, int inodeBits)
    {
        long perBlock = blockSize / SignedInodeLayout.For(inodeBits).EntrySize;
        int direct = (int)Math.Min(inode.Blocks, PFSConstants.MaxDirectBlocks);
        List<long> blocks = [.. inode.Db.Take(direct)];
        long remaining = inode.Blocks - direct;
        if (remaining > 0)
        {
            List<long> children = records.GetValueOrDefault(inode.Ib[0], []);
            int take = (int)Math.Min(remaining, perBlock);
            blocks.AddRange(children.Take(take));
            remaining -= take;
        }

        if (remaining > 0)
        {
            foreach (long childIndirect in records.GetValueOrDefault(inode.Ib[1], []))
            {
                List<long> children = records.GetValueOrDefault(childIndirect, []);
                int take = (int)Math.Min(remaining, perBlock);
                blocks.AddRange(children.Take(take));
                remaining -= take;
                if (remaining <= 0)
                {
                    break;
                }
            }
        }

        return blocks;
    }

    // Python make_sig_records_blob: zero signatures, block pointers at each entry.
    private static byte[] SigRecordsBlob(List<long> blocks, int blockSize, int inodeBits)
    {
        SignedInodeLayout layout = SignedInodeLayout.For(inodeBits);
        byte[] blob = new byte[blockSize];
        int off = 0;
        foreach (long block in blocks)
        {
            if (layout.PointerSize == 4)
            {
                BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(off + PFSConstants.SigSize), (int)block);
            }
            else
            {
                BinaryPrimitives.WriteInt64LittleEndian(blob.AsSpan(off + PFSConstants.SigSize), block);
            }

            off += layout.EntrySize;
        }

        return blob;
    }

    // HMAC every target level by level (data, indirect, double indirect, inode table, header) so parents sign final children.
    private static void Sign(FileStream output, List<SignatureTarget> targets, byte[] signKey, int blockSize)
    {
        for (int level = 0; level < 5; level++)
        {
            foreach (SignatureTarget target in targets.Where(t => t.Level == level))
            {
                byte[] data = new byte[target.Size];
                output.Seek(target.Block * blockSize, SeekOrigin.Begin);
                output.ReadExactly(data);
                long slot = target.SigOffset - (target.Block * blockSize);
                if (slot >= 0 && slot <= data.Length - PFSConstants.SigSize)
                {
                    data.AsSpan((int)slot, PFSConstants.SigSize).Clear();
                }

                output.Seek(target.SigOffset, SeekOrigin.Begin);
                output.Write(HMACSHA256.HashData(signKey, data));
            }
        }
    }

    private static void WriteToBlocks(Stream output, byte[] payload, List<long> blocks, int blockSize)
    {
        for (int i = 0; i < blocks.Count && (long)i * blockSize < payload.Length; i++)
        {
            output.Seek(blocks[i] * blockSize, SeekOrigin.Begin);
            output.Write(payload, i * blockSize, Math.Min(blockSize, payload.Length - (i * blockSize)));
        }
    }

    // Copy a payload source into consecutive runs: one contiguous run (unsigned) or one block per entry (signed).
    private static void CopySource(Stream output, string sourcePath, long size, List<long> blocks, long runLength, int blockSize)
    {
        if (size <= 0)
        {
            return;
        }

        using FileStream source = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, SpoolCopyChunk, FileOptions.SequentialScan);
        byte[] buffer = new byte[SpoolCopyChunk];
        long remaining = size;
        foreach (long block in blocks)
        {
            if (remaining <= 0)
            {
                break;
            }

            output.Seek(block * blockSize, SeekOrigin.Begin);
            long run = Math.Min(runLength, remaining);
            for (long done = 0; done < run;)
            {
                int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, run - done));
                if (read == 0)
                {
                    throw new BuildException("Stored payload source ended before expected size");
                }

                output.Write(buffer, 0, read);
                done += read;
            }

            remaining -= run;
        }
    }

    private static void WriteAt(Stream output, long offset, byte[] data)
    {
        output.Seek(offset, SeekOrigin.Begin);
        output.Write(data);
    }

    private sealed record SignatureTarget(long Block, long SigOffset, int Size, int Level);

    private sealed record PayloadNode(WriteInode Inode, byte[]? Bytes, FileNode? File, bool IsDir)
    {
        public long PayloadSize => Bytes?.Length ?? File!.StoredSize;
    }

    private sealed class DirNode(string relDir, string name, string? parentRelDir)
    {
        public string RelDir { get; } = relDir;

        public string Name { get; } = name;

        public string? ParentRelDir { get; } = parentRelDir;

        public List<string> ChildrenDirs { get; } = [];

        public List<string> ChildrenFiles { get; } = [];

        public List<(WriteInode Inode, int Type, string Name)> Dirents { get; } = [];

        public WriteInode? Inode { get; set; }
    }

    private sealed class FileNode(string relPath, string absPath, string name, long rawSize)
    {
        public string RelPath { get; } = relPath;

        public string AbsPath { get; } = absPath;

        public string Name { get; } = name;

        public long RawSize { get; } = rawSize;

        public string? StoredSourcePath { get; set; }

        public long StoredSize { get; set; }

        public bool Compressed { get; set; }

        public double Gain { get; set; }

        public long Hypothetical { get; set; }

        public WriteInode? Inode { get; set; }

        public void StoreRaw(long hypothetical)
        {
            StoredSourcePath = AbsPath;
            StoredSize = RawSize;
            Compressed = false;
            Gain = 0.0;
            Hypothetical = hypothetical;
        }
    }
}
