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
    /// runtime settings (Python <c>verify_packs</c>).
    /// </summary>
    /// <param name="indexPath">Manifest path.</param>
    /// <param name="patterns">File globs.</param>
    /// <returns>Counters.</returns>
    public static AMPRVerifyResult Verify(string indexPath, IReadOnlyList<string>? patterns = null)
    {
        AMPRPackManifest manifest = AMPRPackManifest.Load(indexPath);
        AMPRRuntimeSettings.Read(AMPRPackFormat.RuntimePath(indexPath), manifest.BuildId);
        Dictionary<(int Pack, ulong Offset, int Stored), (int Codec, int Raw, uint Crc)> seen = [];
        long files = 0;
        long chunks = 0;
        long stored = 0;
        long raw = 0;
        using AMPRPackReader reader = new(manifest);
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
                if (seen.TryGetValue(physical, out (int, int, uint) prior))
                {
                    if (prior != descriptor)
                    {
                        throw new AMPRPackException("one physical chunk has conflicting metadata");
                    }

                    total += (ulong)chunk.RawSize;
                    continue;
                }

                total += (ulong)reader.ReadChunk(chunkIndex).Length;
                seen[physical] = descriptor;
                chunks++;
                stored += chunk.StoredSize;
                raw += chunk.RawSize;
            }

            if (total != record.LogicalSize)
            {
                throw new AMPRPackException($"logical size mismatch for file id {fileId}");
            }

            files++;
        }

        return new AMPRVerifyResult(files, chunks, stored, raw);
    }

    /// <summary>Rebuild every selected packed file and compare it byte for byte with the source tree (Python <c>verify_packs_against_root</c>).</summary>
    /// <param name="indexPath">Manifest path.</param>
    /// <param name="root"><c>/app0</c> source tree.</param>
    /// <param name="patterns">File globs.</param>
    /// <returns>Counters.</returns>
    public static AMPRSourceCompareResult VerifyAgainstRoot(string indexPath, string root, IReadOnlyList<string>? patterns = null)
    {
        AMPRPackManifest manifest = AMPRPackManifest.Load(indexPath);
        root = Path.GetFullPath(root);
        long files = 0;
        long chunks = 0;
        long bytes = 0;
        using AMPRPackReader reader = new(manifest);
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

            long logicalOffset = 0;
            using (FileStream source = File.OpenRead(sourcePath))
            {
                int local = 0;
                foreach (byte[] packed in reader.ReadFile(fileId))
                {
                    byte[] sourceRaw = new byte[packed.Length];
                    if (source.ReadAtLeast(sourceRaw, sourceRaw.Length, throwOnEndOfStream: false) != sourceRaw.Length)
                    {
                        throw new AMPRPackException($"source truncated for {relative} at 0x{logicalOffset:x}");
                    }

                    int mismatch = sourceRaw.AsSpan().CommonPrefixLength(packed);
                    if (mismatch < packed.Length)
                    {
                        AMPRChunkRecord chunk = manifest.Chunks[(int)record.FirstChunk + local];
                        throw new AMPRPackException(
                            $"packed/source mismatch for {relative}: logical=0x{logicalOffset + mismatch:x}, chunk={local}, "
                            + $"pack={chunk.PackId}, physical=0x{chunk.Offset:x}, source=0x{sourceRaw[mismatch]:x2}, packed=0x{packed[mismatch]:x2}");
                    }

                    logicalOffset += packed.Length;
                    bytes += packed.Length;
                    chunks++;
                    local++;
                }

                if (source.ReadByte() != -1)
                {
                    throw new AMPRPackException($"source grew while comparing: {relative}");
                }
            }

            if ((ulong)logicalOffset != record.LogicalSize)
            {
                throw new AMPRPackException($"reconstructed size mismatch for {relative}: expected={record.LogicalSize}, got={logicalOffset}");
            }

            files++;
        }

        return new AMPRSourceCompareResult(files, chunks, bytes);
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

                files++;
            }
            finally
            {
                File.Delete(temp);
            }
        }

        return new AMPRExtractResult(files, bytes);
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
