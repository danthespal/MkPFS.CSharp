using MkPFS.Core.AMPR;
using MkPFS.Core.Compression;
using MkPFS.Core.Util;

namespace MkPFS.Build.AMPRPack;

/// <summary>
/// Builds an AMPR asset-pack set (manifest, volumes, CRC sidecar, optional runtime settings) from an <c>/app0</c>
/// tree and its AMPRIDX3 index. Port of ampr_pack <c>build_packs</c> and <c>_publish_transaction</c>; output is
/// byte-identical to the oracle for the same inputs.
/// </summary>
public static class AMPRPackBuilder
{
    /// <summary>Build and publish a pack set.</summary>
    /// <param name="root"><c>/app0</c> source directory.</param>
    /// <param name="amprIndex">AMPRIDX3 index of that tree.</param>
    /// <param name="outputDir">Output directory.</param>
    /// <param name="config">Configuration.</param>
    /// <param name="includePatterns">CLI include globs.</param>
    /// <param name="excludePatterns">CLI exclude globs.</param>
    /// <param name="allowMissing">Leave missing sources loose.</param>
    /// <param name="progress">Progress callback.</param>
    /// <returns>Manifest path, counters and warnings.</returns>
    /// <exception cref="AMPRPackException">The build failed; temporary files are removed.</exception>
    public static AMPRBuildResult Build(
        string root,
        string amprIndex,
        string outputDir,
        AMPRPackConfig config,
        IReadOnlyList<string>? includePatterns = null,
        IReadOnlyList<string>? excludePatterns = null,
        bool allowMissing = false,
        Action<AMPRBuildProgress>? progress = null)
    {
        root = Path.GetFullPath(root);
        outputDir = Path.GetFullPath(outputDir);
        Directory.CreateDirectory(outputDir);
        progress?.Invoke(new AMPRBuildProgress("reading-index", 0, 0, 0, 0, amprIndex));
        List<AMPRIndexEntry> entries = AMPRIndexReader.Read(amprIndex);
        AMPRBuildStats stats = new() { FilesTotal = entries.Count };
        (List<AMPRSelectedFile> selected, List<string> warnings) = AMPRPackPlanner.Plan(
            entries, root, config, stats, includePatterns ?? [], excludePatterns ?? [], allowMissing, progress);

        List<AMPRSelectedFile> packedItems = [.. selected.Where(item => item.Rule.Action != "loose")];
        PackingProgress packing = new(progress, packedItems.Count, packedItems.Sum(item => item.Size));
        packing.Report("packing");
        AMPRPackLayout layout = new(config, outputDir);
        string? tempIndex = null;
        string? tempCrc = null;
        string? tempRuntime = null;
        try
        {
            PackedContent content = PackFiles(selected, config, layout, stats, packing);

            // Names go into the string table before the build id; sizes and placement are final now.
            packing.Report("finalizing");
            List<AMPRPackRecord> provisional = [];
            foreach (AMPRPackVolumeWriter writer in layout.Volumes)
            {
                (uint nameOffset, uint nameLength) = content.Strings.Add(writer.Name);
                provisional.Add(new AMPRPackRecord(
                    (ulong)(writer.ProjectedFinalSize() - writer.PayloadOffset),
                    (ulong)writer.ProjectedFinalSize(),
                    nameOffset,
                    nameLength,
                    writer.Flags,
                    (uint)writer.IOPageSize));
            }

            byte[] crcPayload = AMPRChunkCrcs.EncodePayload(content.ChunkCrcs);
            byte[] strings = content.Strings.ToArray();
            byte[] buildId = AMPRBuildId.Compute([
                config.CanonicalBytes(),
                Records(content.Files, AMPRPackFormat.FileRecordSize, (r, s) => r.Write(s)),
                Records(content.Chunks, AMPRPackFormat.ChunkRecordSize, (r, s) => r.Write(s)),
                crcPayload,
                Records(provisional, AMPRPackFormat.PackRecordSize, (r, s) => r.Write(s)),
                strings,
            ]);

            List<AMPRPackRecord> finalRecords = [];
            for (int i = 0; i < layout.Volumes.Count; i++)
            {
                AMPRPackVolumeWriter writer = layout.Volumes[i];
                AMPRPackRecord actual = writer.Finalize(buildId) with
                {
                    NameOffset = provisional[i].NameOffset,
                    NameLength = provisional[i].NameLength,
                };
                finalRecords.Add(actual);
                stats.PaddingBytes += writer.PaddingBytes;
                stats.IOPagesTouched += ((long)actual.PayloadBytes + writer.IOPageSize - 1) / writer.IOPageSize;
            }

            byte[] indexData = new AMPRPackManifest(buildId, 0, content.Files, content.Chunks, finalRecords, strings).Serialize();
            string indexPath = AMPRAssetPath.SafeOutputPath(outputDir, config.IndexName);
            Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
            tempIndex = Path.Combine(Path.GetDirectoryName(indexPath)!, $".{Path.GetFileName(indexPath)}.tmp-{Environment.ProcessId}");
            WriteDurable(tempIndex, indexData);
            tempCrc = tempIndex + ".crc";
            WriteDurable(tempCrc, AMPRChunkCrcs.Build(buildId, content.ChunkCrcs));
            if (config.Runtime is not null)
            {
                tempRuntime = tempIndex + ".runtime";
                WriteDurable(tempRuntime, config.Runtime.Encode(buildId));
            }

            packing.Report("publishing");
            PublishTransaction(layout, tempIndex, indexPath, tempCrc, tempRuntime);
            tempIndex = tempCrc = tempRuntime = null;
            packing.Report("complete");
            return new AMPRBuildResult(indexPath, stats, warnings, finalRecords.Count);
        }
        catch
        {
            layout.Abort();
            foreach (string? temp in (string?[])[tempRuntime, tempCrc, tempIndex])
            {
                if (temp is not null)
                {
                    File.Delete(temp);
                }
            }

            throw;
        }
    }

    private static PackedContent PackFiles(
        List<AMPRSelectedFile> selected, AMPRPackConfig config, AMPRPackLayout layout, AMPRBuildStats stats, PackingProgress packing)
    {
        PackedContent content = new();
        Dictionary<string, AMPRChunkRecord> dedupe = new(StringComparer.Ordinal);
        List<string> sortedGroups = config.SortedGroupNames();
        using EncoderPool encoders = new();
        TaskScheduler scheduler = new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, (int)config.Workers).ConcurrentScheduler;
        foreach (AMPRSelectedFile item in selected)
        {
            string path = AMPRAssetPath.Canonical(item.IndexEntry.Path);
            (uint pathOffset, uint pathLength) = content.Strings.Add(path);
            ulong pathHash = AMPRAssetPath.Hash(path);
            if (item.Rule.Action == "loose")
            {
                stats.LoosePaths.Add(item.Relative);
                content.Files.Add(new AMPRFileRecord(pathHash, item.IndexEntry.Size, item.IndexEntry.MTime, 0, 0, pathOffset, pathLength, 0, 0));
                stats.FilesLoose++;
                continue;
            }

            packing.Report("packing", item.Relative);
            AMPRGroupConfig group = config.Groups[item.Rule.Group];
            long ioPageSize = group.IOPageSize != 0 ? group.IOPageSize : config.IOPageSize;
            int firstChunk = content.Chunks.Count;
            uint fileFlags = AMPRPackFormat.FileFlagPacked;
            if (item.Rule.Action == "store")
            {
                fileFlags |= AMPRPackFormat.FileFlagStoreOnly;
            }

            string layoutMode = item.Rule.ResolvedLayout();
            fileFlags |= layoutMode switch
            {
                "streaming" => AMPRPackFormat.FileFlagStreaming,
                "random" => AMPRPackFormat.FileFlagRandomAccess,
                _ => 0U,
            };
            if (item.Rule.Hot)
            {
                fileFlags |= AMPRPackFormat.FileFlagHot;
            }

            bool striped = group.StripeLargeFiles && group.PackCount > 1 && item.Size >= group.StripeThreshold;
            bool allowDeduplicate = config.Deduplicate && (layoutMode != "streaming" || config.DeduplicateStreaming);
            string physicalLayout = layoutMode == "streaming" ? "dense" : "isolated";
            int chunkIndex = 0;
            using (FileStream source = File.OpenRead(item.Source))
            {
                (long Length, DateTime Written) before = AMPRPackPlanner.FileStamp(item.Source);
                foreach (AMPRCompressedBlock block in CompressBlocks(source, item, config, ioPageSize, encoders, scheduler))
                {
                    int lane = item.Lane;
                    if (striped)
                    {
                        lane = (int)((item.Lane + (chunkIndex / group.StripeGroupBlocks)) % group.PackCount);
                    }

                    string domain = config.DeduplicateScope == "group" ? item.Rule.Group : $"{item.Rule.Group}:{lane}";
                    string dedupeKey = $"{domain}\0{physicalLayout}\0{block.Codec}\0{Convert.ToHexString(block.Fingerprint)}";
                    AMPRChunkRecord record;
                    if (allowDeduplicate && dedupe.TryGetValue(dedupeKey, out AMPRChunkRecord prior))
                    {
                        int flags = AMPRPackFormat.ChunkFlagShared
                            | (prior.Flags & (AMPRPackFormat.ChunkFlagPageContained | AMPRPackFormat.ChunkFlagPageAligned))
                            | (layoutMode == "streaming" ? AMPRPackFormat.ChunkFlagStreaming : 0);
                        record = prior with { Flags = flags };
                        stats.ChunksShared++;
                    }
                    else
                    {
                        // Only dense streaming extents need a page-aligned start; random/mixed chunks share pages
                        // but never straddle one.
                        bool extentStart = layoutMode == "streaming"
                            && (chunkIndex == 0 || (striped && chunkIndex % group.StripeGroupBlocks == 0));
                        AMPRPackVolumeWriter writer = layout.WriterFor(group, lane, block.Stored.Length, striped, layoutMode, extentStart);
                        (long offset, int placement) = writer.Write(block.Stored, layoutMode, extentStart);
                        int flags = placement | (layoutMode == "streaming" ? AMPRPackFormat.ChunkFlagStreaming : 0);
                        if (layoutMode != "streaming" && !IsPageSafe(block.Stored.Length, placement, writer.IOPageSize))
                        {
                            throw new AMPRPackException("non-streaming chunk was not placed page-safely");
                        }

                        record = new AMPRChunkRecord((ulong)offset, block.Stored.Length, block.Raw.Length, writer.PackId, block.Codec, flags);
                        if (allowDeduplicate)
                        {
                            dedupe[dedupeKey] = record;
                        }

                        stats.StoredBytes += block.Stored.Length;
                        if (layoutMode == "streaming")
                        {
                            stats.DenseStreamingChunks++;
                        }
                    }

                    if (IsPageSafe(record.StoredSize, record.Flags, layout.Volumes[record.PackId].IOPageSize))
                    {
                        stats.IOPageSafeChunks++;
                    }

                    content.Chunks.Add(record);
                    content.ChunkCrcs.Add(block.RawCrc);
                    chunkIndex++;
                    stats.Chunks++;
                    stats.LogicalBytes += block.Raw.Length;
                    packing.AddBytes(block.Raw.Length, item.Relative);
                    if (record.Codec == AMPRPackFormat.ChunkCodecLZ4)
                    {
                        stats.ChunksLZ4++;
                    }
                    else
                    {
                        stats.ChunksRaw++;
                    }
                }

                if (AMPRPackPlanner.FileStamp(item.Source) != before)
                {
                    throw new AMPRPackException($"source changed while packing: {item.Relative}");
                }
            }

            content.Files.Add(new AMPRFileRecord(
                pathHash,
                (ulong)item.Size,
                config.PreserveMTime ? item.MTime : item.IndexEntry.MTime,
                (uint)firstChunk,
                (uint)chunkIndex,
                pathOffset,
                pathLength,
                fileFlags,
                (byte)item.Rule.BlockShift,
                (byte)sortedGroups.IndexOf(item.Rule.Group)));
            stats.FilesPacked++;
            packing.FileDone(item.Relative);
        }

        return content;
    }

    // Reads blocks in order and compresses up to max(2, 2 * workers) of them ahead on at most `workers` threads,
    // yielding results in file order (Python _iter_compressed_blocks).
    private static IEnumerable<AMPRCompressedBlock> CompressBlocks(
        FileStream source, AMPRSelectedFile item, AMPRPackConfig config, long ioPageSize, EncoderPool encoders, TaskScheduler scheduler)
    {
        long blockSize = 1L << item.Rule.BlockShift;
        long remaining = item.Size;
        int window = (int)Math.Max(2, config.Workers * 2);
        Queue<Task<AMPRCompressedBlock>> pending = new();
        while (remaining > 0 || pending.Count > 0)
        {
            while (remaining > 0 && pending.Count < window)
            {
                byte[] raw = new byte[Math.Min(blockSize, remaining)];
                int read = source.ReadAtLeast(raw, raw.Length, throwOnEndOfStream: false);
                if (read == 0)
                {
                    throw new AMPRPackException("source file was truncated while packing");
                }

                if (read < raw.Length)
                {
                    Array.Resize(ref raw, read);
                }

                remaining -= read;
                AMPRRule rule = item.Rule;
                pending.Enqueue(Task.Factory.StartNew(
                    () => encoders.Run(encoder => AMPRBlockCompressor.Compress(encoder, raw, rule, ioPageSize)),
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach,
                    scheduler));
            }

            yield return pending.Dequeue().GetAwaiter().GetResult();
        }

        if (source.ReadByte() != -1)
        {
            throw new AMPRPackException("source file grew while packing");
        }
    }

    private static bool IsPageSafe(long storedSize, int flags, long ioPageSize) =>
        storedSize <= ioPageSize
            ? (flags & AMPRPackFormat.ChunkFlagPageContained) != 0
            : (flags & AMPRPackFormat.ChunkFlagPageAligned) != 0;

    private static byte[] Records<T>(IReadOnlyList<T> records, int size, Action<T, Span<byte>> write)
    {
        byte[] data = new byte[records.Count * size];
        for (int i = 0; i < records.Count; i++)
        {
            write(records[i], data.AsSpan(i * size, size));
        }

        return data;
    }

    private static void WriteDurable(string path, byte[] data)
    {
        using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(data);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Publish volumes first, then the CRC sidecar and runtime settings (a stale <c>.runtime</c> is removed when the
    /// build has none), and the manifest last; on failure restore the previous files (Python <c>_publish_transaction</c>).
    /// The build id in every header makes partial combinations fail closed even after a crash.
    /// </summary>
    private static void PublishTransaction(AMPRPackLayout layout, string tempIndex, string indexPath, string tempCrc, string? tempRuntime)
    {
        List<(string Final, string? Backup)> replacements = [];
        int pid = Environment.ProcessId;
        try
        {
            foreach (AMPRPackVolumeWriter writer in layout.Volumes)
            {
                string? backup = MoveAside(writer.FinalPath, $".{Path.GetFileName(writer.FinalPath)}.backup-{pid}-{writer.PackId}");
                replacements.Add((writer.FinalPath, backup));
                writer.Publish();
            }

            string crcPath = AMPRPackFormat.ChunkCrcPath(indexPath);
            replacements.Add((crcPath, MoveAside(crcPath, $".{Path.GetFileName(crcPath)}.backup-{pid}")));
            File.Move(tempCrc, crcPath, overwrite: true);
            string runtimePath = AMPRPackFormat.RuntimePath(indexPath);
            replacements.Add((runtimePath, MoveAside(runtimePath, $".{Path.GetFileName(runtimePath)}.backup-{pid}")));
            if (tempRuntime is not null)
            {
                File.Move(tempRuntime, runtimePath, overwrite: true);
            }

            File.Move(tempIndex, indexPath, overwrite: true);
        }
        catch
        {
            for (int i = replacements.Count - 1; i >= 0; i--)
            {
                (string final, string? backup) = replacements[i];
                File.Delete(final);
                if (backup is not null && File.Exists(backup))
                {
                    File.Move(backup, final, overwrite: true);
                }
            }

            throw;
        }

        foreach ((_, string? backup) in replacements)
        {
            if (backup is not null)
            {
                File.Delete(backup);
            }
        }
    }

    private static string? MoveAside(string path, string backupName)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        string backup = Path.Combine(Path.GetDirectoryName(path)!, backupName);
        File.Delete(backup);
        File.Move(path, backup);
        return backup;
    }

    private sealed class PackedContent
    {
        public AMPRStringTable Strings { get; } = new();

        public List<AMPRFileRecord> Files { get; } = [];

        public List<AMPRChunkRecord> Chunks { get; } = [];

        public List<uint> ChunkCrcs { get; } = [];
    }

    // One LZ4 encoder per concurrently running task, reused across blocks and files.
    private sealed class EncoderPool : IDisposable
    {
        private readonly System.Collections.Concurrent.ConcurrentBag<LZ4Encoder> _idle = [];
        private readonly List<LZ4Encoder> _all = [];

        public AMPRCompressedBlock Run(Func<LZ4Encoder, AMPRCompressedBlock> work)
        {
            if (!_idle.TryTake(out LZ4Encoder? encoder))
            {
                encoder = new LZ4Encoder();
                lock (_all)
                {
                    _all.Add(encoder);
                }
            }

            try
            {
                return work(encoder);
            }
            finally
            {
                _idle.Add(encoder);
            }
        }

        public void Dispose()
        {
            foreach (LZ4Encoder encoder in _all)
            {
                encoder.Dispose();
            }
        }
    }

    // Progress bookkeeping for the packing, finalizing, publishing and complete phases.
    private sealed class PackingProgress(Action<AMPRBuildProgress>? callback, long filesTotal, long bytesTotal)
    {
        private long _filesDone;
        private long _bytesDone;

        public void Report(string phase, string currentPath = "") =>
            callback?.Invoke(new AMPRBuildProgress(phase, _filesDone, filesTotal, _bytesDone, bytesTotal, currentPath));

        public void AddBytes(long bytes, string currentPath)
        {
            _bytesDone += bytes;
            Report("packing", currentPath);
        }

        public void FileDone(string currentPath)
        {
            _filesDone++;
            Report("packing", currentPath);
        }
    }
}
