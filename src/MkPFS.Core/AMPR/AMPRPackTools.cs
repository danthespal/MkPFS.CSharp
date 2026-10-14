using System.Runtime.ExceptionServices;
using MkPFS.Core.Diagnostics;
using MkPFS.Core.Util;

namespace MkPFS.Core.AMPR;

/// <summary>Result of <see cref="AMPRPackTools.Verify"/>.</summary>
/// <param name="Files">Packed files checked.</param>
/// <param name="PhysicalChunks">Distinct physical chunks decoded.</param>
/// <param name="StoredBytes">Stored bytes of those chunks.</param>
/// <param name="RawBytes">Decoded bytes of those chunks.</param>
public sealed record AMPRVerifyResult(long Files, long PhysicalChunks, long StoredBytes, long RawBytes);

/// <summary>Result of <see cref="AMPRPackTools.VerifyAgainstRoot"/>.</summary>
/// <param name="Files">Files compared.</param>
/// <param name="Chunks">Chunks compared.</param>
/// <param name="Bytes">Bytes compared.</param>
public sealed record AMPRSourceCompareResult(long Files, long Chunks, long Bytes);

/// <summary>Result of <see cref="AMPRPackTools.Extract"/>.</summary>
/// <param name="Files">Files written.</param>
/// <param name="Bytes">Bytes written.</param>
public sealed record AMPRExtractResult(long Files, long Bytes);

/// <summary>Result of <see cref="AMPRPackTools.RestoreDiskFiles"/>.</summary>
/// <param name="Missing">Packed <see cref="AMPRDiskFiles"/> missing from disk or with the wrong size.</param>
/// <param name="Restored">Files written back.</param>
/// <param name="Paths">Relative paths of the missing files, in file-id order.</param>
public sealed record AMPRDiskFilesResult(long Missing, long Restored, IReadOnlyList<string> Paths);

/// <summary>One row of <see cref="AMPRPackTools.List"/>.</summary>
/// <param name="FileId">File id.</param>
/// <param name="Path">Canonical <c>/app0</c> path.</param>
/// <param name="Packed">Stored in volumes.</param>
/// <param name="LogicalSize">Decoded size.</param>
/// <param name="StoredSize">Sum of stored chunk sizes (shared chunks counted per reference).</param>
/// <param name="BlockSize">Block size, 0 for loose files.</param>
/// <param name="Chunks">Chunk records.</param>
/// <param name="Codecs"><c>lz4</c>/<c>raw</c>, sorted.</param>
/// <param name="Packs">Pack ids, sorted.</param>
/// <param name="IOPageSizes">I/O page sizes of those packs, sorted.</param>
/// <param name="Layout"><c>loose</c>, <c>streaming</c>, <c>random</c> or <c>mixed</c>.</param>
/// <param name="Streaming">Streaming flag.</param>
/// <param name="RandomAccess">Random-access flag.</param>
/// <param name="Hot">Hot flag.</param>
public sealed record AMPRListEntry(
    int FileId,
    string Path,
    bool Packed,
    ulong LogicalSize,
    long StoredSize,
    long BlockSize,
    uint Chunks,
    IReadOnlyList<string> Codecs,
    IReadOnlyList<int> Packs,
    IReadOnlyList<uint> IOPageSizes,
    string Layout,
    bool Streaming,
    bool RandomAccess,
    bool Hot);

/// <summary>One volume in <see cref="AMPRInspectResult"/>.</summary>
/// <param name="Id">Pack id.</param>
/// <param name="Name">Volume name.</param>
/// <param name="FileSize">Volume size.</param>
/// <param name="PayloadBytes">Payload bytes.</param>
/// <param name="IOPageSize">I/O page size.</param>
/// <param name="IOPages">Volume size in pages.</param>
/// <param name="Flags">Pack flags.</param>
public sealed record AMPRInspectPack(int Id, string Name, ulong FileSize, ulong PayloadBytes, uint IOPageSize, ulong IOPages, uint Flags);

/// <summary>Result of <see cref="AMPRPackTools.Inspect"/>.</summary>
/// <param name="BuildId">Build id, lower-case hex.</param>
/// <param name="Runtime">Runtime settings, if present.</param>
/// <param name="Files">Files.</param>
/// <param name="PackedFiles">Packed files.</param>
/// <param name="LooseFiles">Loose files.</param>
/// <param name="Chunks">Chunk records.</param>
/// <param name="Packs">Volumes.</param>
public sealed record AMPRInspectResult(
    string BuildId, AMPRRuntimeSettings? Runtime, long Files, long PackedFiles, long LooseFiles, long Chunks, IReadOnlyList<AMPRInspectPack> Packs);

/// <summary>Offline pack-set tools (ampr_pack <c>verify</c>, <c>unpack</c>, <c>list</c>, <c>inspect</c>).</summary>
public static class AMPRPackTools
{
    /// <summary>File ids whose relative path matches any pattern; all ids without patterns (Python <c>_selected_file_ids</c>).</summary>
    /// <param name="manifest">Manifest.</param>
    /// <param name="patterns">Globs over paths relative to <c>/app0</c>.</param>
    /// <returns>File ids in order.</returns>
    public static List<int> SelectedFileIds(AMPRPackManifest manifest, IReadOnlyList<string> patterns)
    {
        List<int> selected = [];
        for (int fileId = 1; fileId <= manifest.Files.Count; fileId++)
        {
            if (patterns.Count == 0 || AMPRGlob.Matches(AMPRAssetPath.Relative(manifest.FilePath(fileId)), patterns))
            {
                selected.Add(fileId);
            }
        }

        return selected;
    }

    /// <summary>
    /// Decode every selected packed file once per physical chunk and check sizes and CRCs; also validates the
    /// runtime settings (Python <c>verify_packs</c>). The metadata is checked in file order first, then the chunks
    /// are decoded on <see cref="CpuCores.Max"/> threads; a failure is the first one in file order.
    /// </summary>
    /// <param name="indexPath">Manifest path.</param>
    /// <param name="patterns">File globs.</param>
    /// <param name="progress">Phase <c>verify</c>, in decoded bytes of the distinct chunks.</param>
    /// <returns>Counters.</returns>
    public static AMPRVerifyResult Verify(string indexPath, IReadOnlyList<string>? patterns = null, IProgressSink? progress = null)
    {
        AMPRPackManifest manifest = AMPRPackManifest.Load(indexPath);
        AMPRRuntimeSettings.Read(AMPRPackFormat.RuntimePath(indexPath), manifest.BuildId);
        using AMPRPackReader reader = new(manifest);

        // 1. Metadata in file order: chunks shared by several files must agree, and every file's chunks add up to
        // its size (ReadChunk later checks each decoded size against its record).
        Dictionary<(int Pack, ulong Offset, int Stored), (int Codec, int Raw, uint Crc)> seen = [];
        List<int> distinct = [];
        long files = 0;
        long stored = 0;
        long rawBytes = 0;
        foreach (int fileId in SelectedFileIds(manifest, patterns ?? []))
        {
            AMPRFileRecord record = manifest.Files[fileId - 1];
            if (!record.IsPacked)
            {
                continue;
            }

            ulong total = 0;
            for (uint local = 0; local < record.ChunkCount; local++)
            {
                int chunkIndex = (int)(record.FirstChunk + local);
                AMPRChunkRecord chunk = manifest.Chunks[chunkIndex];
                (int, ulong, int) physical = (chunk.PackId, chunk.Offset, chunk.StoredSize);
                (int, int, uint) descriptor = (chunk.Codec, chunk.RawSize, reader.ChunkCrcs[chunkIndex]);
                total += (ulong)chunk.RawSize;
                if (seen.TryGetValue(physical, out (int, int, uint) prior))
                {
                    if (prior != descriptor)
                    {
                        throw new AMPRPackException("one physical chunk has conflicting metadata");
                    }

                    continue;
                }

                seen[physical] = descriptor;
                distinct.Add(chunkIndex);
                stored += chunk.StoredSize;
                rawBytes += chunk.RawSize;
            }

            if (total != record.LogicalSize)
            {
                throw new AMPRPackException($"logical size mismatch for file id {fileId}");
            }

            files++;
        }

        // 2. Decode and CRC-check every distinct chunk once, in parallel ranges.
        ParallelProgress report = new(progress, "verify", rawBytes);
        int ranges = (distinct.Count + ChunksPerRange - 1) / ChunksPerRange;
        RunInOrder(ranges, range =>
        {
            for (int i = range * ChunksPerRange; i < Math.Min(distinct.Count, (range + 1) * ChunksPerRange); i++)
            {
                report.Add(reader.ReadChunk(distinct[i]).Length);
            }
        });

        return new AMPRVerifyResult(files, distinct.Count, stored, rawBytes);
    }

    /// <summary>
    /// Rebuild every selected packed file and compare it byte for byte with the source tree (Python
    /// <c>verify_packs_against_root</c>). Sources and sizes are checked in file order first, then chunk ranges are
    /// decoded and compared on <see cref="CpuCores.Max"/> threads; a failure is the first one in file order.
    /// </summary>
    /// <param name="indexPath">Manifest path.</param>
    /// <param name="root"><c>/app0</c> source tree.</param>
    /// <param name="patterns">File globs.</param>
    /// <param name="progress">Phase <c>compare</c>, in logical bytes of the selected packed files.</param>
    /// <returns>Counters.</returns>
    public static AMPRSourceCompareResult VerifyAgainstRoot(
        string indexPath, string root, IReadOnlyList<string>? patterns = null, IProgressSink? progress = null)
    {
        AMPRPackManifest manifest = AMPRPackManifest.Load(indexPath);
        root = Path.GetFullPath(root);
        using AMPRPackReader reader = new(manifest);

        // 1. In file order: every source exists with the manifest size, and every file's chunks add up to it.
        List<(string Relative, string Path, AMPRFileRecord Record)> sources = [];
        List<(int Source, int FirstLocal, int Count, long Offset)> ranges = [];
        long chunks = 0;
        long bytes = 0;
        foreach (int fileId in SelectedFileIds(manifest, patterns ?? []))
        {
            AMPRFileRecord record = manifest.Files[fileId - 1];
            if (!record.IsPacked)
            {
                continue;
            }

            string relative = AMPRAssetPath.Relative(manifest.FilePath(fileId));
            string sourcePath = AMPRAssetPath.SafeOutputPath(root, relative);
            FileInfo info = new(sourcePath);
            if (!info.Exists)
            {
                throw new AMPRPackException($"source file missing while comparing packed data: {relative}");
            }

            if ((ulong)info.Length != record.LogicalSize)
            {
                throw new AMPRPackException($"source size mismatch for {relative}: manifest={record.LogicalSize}, disk={info.Length}");
            }

            long offset = 0;
            for (int local = 0; local < record.ChunkCount; local += ChunksPerRange)
            {
                int count = (int)Math.Min(ChunksPerRange, record.ChunkCount - (uint)local);
                ranges.Add((sources.Count, local, count, offset));
                for (int k = 0; k < count; k++)
                {
                    offset += manifest.Chunks[(int)record.FirstChunk + local + k].RawSize;
                }
            }

            if ((ulong)offset != record.LogicalSize)
            {
                throw new AMPRPackException($"reconstructed size mismatch for {relative}: expected={record.LogicalSize}, got={offset}");
            }

            sources.Add((relative, sourcePath, record));
            chunks += record.ChunkCount;
            bytes += offset;
        }

        // 2. Decode and compare in parallel; each range reads its part of the source by position.
        ParallelProgress report = new(progress, "compare", bytes);
        RunInOrder(ranges.Count, i =>
        {
            (int sourceIndex, int firstLocal, int count, long offset) = ranges[i];
            (string relative, string sourcePath, AMPRFileRecord record) = sources[sourceIndex];
            using Microsoft.Win32.SafeHandles.SafeFileHandle handle = File.OpenHandle(sourcePath);
            for (int local = firstLocal; local < firstLocal + count; local++)
            {
                AMPRChunkRecord chunk = manifest.Chunks[(int)record.FirstChunk + local];
                byte[] packed = reader.ReadChunk((int)record.FirstChunk + local);
                byte[] sourceRaw = new byte[packed.Length];
                if (StreamUtil.ReadAt(handle, sourceRaw, offset) != sourceRaw.Length)
                {
                    throw new AMPRPackException($"source truncated for {relative} at 0x{offset:x}");
                }

                int mismatch = sourceRaw.AsSpan().CommonPrefixLength(packed);
                if (mismatch < packed.Length)
                {
                    throw new AMPRPackException(
                        $"packed/source mismatch for {relative}: logical=0x{offset + mismatch:x}, chunk={local}, "
                        + $"pack={chunk.PackId}, physical=0x{chunk.Offset:x}, source=0x{sourceRaw[mismatch]:x2}, packed=0x{packed[mismatch]:x2}");
                }

                offset += packed.Length;
                report.Add(packed.Length);
            }
        });

        // 3. A source that changed size while it was compared.
        foreach ((string relative, string sourcePath, AMPRFileRecord record) in sources)
        {
            if ((ulong)new FileInfo(sourcePath).Length > record.LogicalSize)
            {
                throw new AMPRPackException($"source grew while comparing: {relative}");
            }
        }

        return new AMPRSourceCompareResult(sources.Count, chunks, bytes);
    }

    /// <summary>Write selected packed files under <paramref name="outputDir"/> (Python <c>extract_packs</c>).</summary>
    /// <param name="indexPath">Manifest path.</param>
    /// <param name="outputDir">Output directory.</param>
    /// <param name="patterns">File globs.</param>
    /// <param name="overwrite">Replace existing files.</param>
    /// <param name="preserveMTime">Set each file's modification time from the manifest.</param>
    /// <returns>Counters.</returns>
    public static AMPRExtractResult Extract(
        string indexPath, string outputDir, IReadOnlyList<string>? patterns = null, bool overwrite = false, bool preserveMTime = true)
    {
        AMPRPackManifest manifest = AMPRPackManifest.Load(indexPath);
        Directory.CreateDirectory(outputDir);
        long files = 0;
        long bytes = 0;
        using AMPRPackReader reader = new(manifest);
        foreach (int fileId in SelectedFileIds(manifest, patterns ?? []))
        {
            AMPRFileRecord record = manifest.Files[fileId - 1];
            if (!record.IsPacked)
            {
                continue;
            }

            string destination = AMPRAssetPath.SafeOutputPath(outputDir, AMPRAssetPath.Relative(manifest.FilePath(fileId)));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if ((File.Exists(destination) || Directory.Exists(destination)) && !overwrite)
            {
                throw new AMPRPackException($"destination exists: {destination}");
            }

            bytes += WriteFile(reader, fileId, record, destination, preserveMTime);
            files++;
        }

        return new AMPRExtractResult(files, bytes);
    }

    /// <summary>
    /// Find packed <see cref="AMPRDiskFiles"/> missing from the <c>/app0</c> folder that holds the packs, and
    /// optionally write them back from the packs with their manifest mtime (MkPFS addition, LZ4 Compression 1.4.3
    /// <c>ampr_pack_restore_disk_files</c>). The manifest is not changed, so the game still reads the packed copy.
    /// </summary>
    /// <param name="indexPath">Manifest path.</param>
    /// <param name="root"><c>/app0</c> folder of the packed game.</param>
    /// <param name="restore">Write the missing files; otherwise only count them.</param>
    /// <returns>Counters and paths.</returns>
    public static AMPRDiskFilesResult RestoreDiskFiles(string indexPath, string root, bool restore)
    {
        AMPRPackManifest manifest = AMPRPackManifest.Load(indexPath);
        List<string> missing = [];
        long restored = 0;
        using AMPRPackReader reader = new(manifest);
        for (int fileId = 1; fileId <= manifest.Files.Count; fileId++)
        {
            AMPRFileRecord record = manifest.Files[fileId - 1];
            string relative = AMPRAssetPath.Relative(manifest.FilePath(fileId));
            if (!record.IsPacked || !AMPRDiskFiles.MustStayOnDisk(relative, record.LogicalSize))
            {
                continue;
            }

            string destination = AMPRAssetPath.SafeOutputPath(root, relative);
            FileInfo info = new(destination);
            if (info.Exists && info.LinkTarget is null && (ulong)info.Length == record.LogicalSize)
            {
                continue;
            }

            missing.Add(relative);
            if (restore)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                WriteFile(reader, fileId, record, destination, preserveMTime: true);
                restored++;
            }
        }

        return new AMPRDiskFilesResult(missing.Count, restored, missing);
    }

    // Chunks one parallel work item decodes: 4 MiB at the default 64 KiB block size.
    private const int ChunksPerRange = 64;

    // Run work(0..count-1) on CpuCores.Max threads. When items fail, the one with the lowest index is rethrown, which is
    // the failure a loop in file order meets first; items after a failure are skipped.
    private static void RunInOrder(int count, Action<int> work)
    {
        int failedAt = int.MaxValue;
        ExceptionDispatchInfo? failure = null;
        object gate = new();
        Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = CpuCores.Max }, (i, state) =>
        {
            try
            {
                work(i);
            }
            catch (Exception exc) when (exc is AMPRPackException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                lock (gate)
                {
                    if (i < failedAt)
                    {
                        failedAt = i;
                        failure = ExceptionDispatchInfo.Capture(exc);
                    }
                }

                state.Break();
            }
        });
        failure?.Throw();
    }

    // Progress shared by parallel workers: bytes add up atomically, and the sink is called by one thread at a time.
    private sealed class ParallelProgress(IProgressSink? sink, string phase, long total)
    {
        private readonly object _gate = new();
        private long _done;

        public void Add(long bytes)
        {
            long done = Interlocked.Add(ref _done, bytes);
            if (sink is not null)
            {
                lock (_gate)
                {
                    sink.Report(phase, done, total, done);
                }
            }
        }
    }

    // Decode one packed file through a temporary file next to the destination, then move it into place.
    private static long WriteFile(AMPRPackReader reader, int fileId, AMPRFileRecord record, string destination, bool preserveMTime)
    {
        long bytes = 0;
        string temp = Path.Combine(Path.GetDirectoryName(destination)!, $".{Path.GetFileName(destination)}.tmp-{Environment.ProcessId}");
        try
        {
            using (FileStream handle = new(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                foreach (byte[] raw in reader.ReadFile(fileId))
                {
                    handle.Write(raw);
                    bytes += raw.Length;
                }

                handle.Flush(flushToDisk: true);
            }

            File.Move(temp, destination, overwrite: true);
            if (preserveMTime)
            {
                DateTime time = DateTime.UnixEpoch.AddSeconds(record.MTime);
                File.SetLastWriteTimeUtc(destination, time);
                File.SetLastAccessTimeUtc(destination, time);
            }
        }
        finally
        {
            File.Delete(temp);
        }

        return bytes;
    }

    /// <summary>Per-file placement summary (Python <c>list_manifest</c>).</summary>
    /// <param name="indexPath">Manifest path.</param>
    /// <param name="patterns">File globs.</param>
    /// <returns>Rows.</returns>
    public static List<AMPRListEntry> List(string indexPath, IReadOnlyList<string>? patterns = null)
    {
        AMPRPackManifest manifest = AMPRPackManifest.Load(indexPath);
        List<AMPRListEntry> rows = [];
        foreach (int fileId in SelectedFileIds(manifest, patterns ?? []))
        {
            AMPRFileRecord record = manifest.Files[fileId - 1];
            long stored = 0;
            SortedSet<string> codecs = new(StringComparer.Ordinal);
            SortedSet<int> packs = [];
            for (uint local = 0; local < record.ChunkCount; local++)
            {
                AMPRChunkRecord chunk = manifest.Chunks[(int)(record.FirstChunk + local)];
                stored += chunk.StoredSize;
                codecs.Add(chunk.Codec == AMPRPackFormat.ChunkCodecLZ4 ? "lz4" : "raw");
                packs.Add(chunk.PackId);
            }

            bool streaming = (record.Flags & AMPRPackFormat.FileFlagStreaming) != 0;
            bool randomAccess = (record.Flags & AMPRPackFormat.FileFlagRandomAccess) != 0;
            string layout = !record.IsPacked ? "loose" : streaming ? "streaming" : randomAccess ? "random" : "mixed";
            rows.Add(new AMPRListEntry(
                fileId,
                manifest.FilePath(fileId),
                record.IsPacked,
                record.LogicalSize,
                stored,
                record.BlockShift != 0 ? 1L << record.BlockShift : 0,
                record.ChunkCount,
                [.. codecs],
                [.. packs],
                [.. packs.Select(id => manifest.Packs[id].IOPageSize).Distinct().Order()],
                layout,
                streaming,
                randomAccess,
                (record.Flags & AMPRPackFormat.FileFlagHot) != 0));
        }

        return rows;
    }

    /// <summary>Manifest-level summary, validating the runtime settings (CLI <c>inspect</c>).</summary>
    /// <param name="indexPath">Manifest path.</param>
    /// <returns>Summary.</returns>
    public static AMPRInspectResult Inspect(string indexPath)
    {
        AMPRPackManifest manifest = AMPRPackManifest.Load(indexPath);
        AMPRRuntimeSettings? runtime = AMPRRuntimeSettings.Read(AMPRPackFormat.RuntimePath(indexPath), manifest.BuildId);
        long packed = manifest.Files.Count(record => record.IsPacked);
        List<AMPRInspectPack> packs = [];
        for (int packId = 0; packId < manifest.Packs.Count; packId++)
        {
            AMPRPackRecord record = manifest.Packs[packId];
            packs.Add(new AMPRInspectPack(
                packId, manifest.PackName(packId), record.FileSize, record.PayloadBytes, record.IOPageSize, record.FileSize / record.IOPageSize, record.Flags));
        }

        return new AMPRInspectResult(
            Convert.ToHexStringLower(manifest.BuildId), runtime, manifest.Files.Count, packed, manifest.Files.Count - packed, manifest.Chunks.Count, packs);
    }
}
