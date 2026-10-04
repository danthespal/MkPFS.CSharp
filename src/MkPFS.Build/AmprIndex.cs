using System.Buffers.Binary;
using System.Text;
using MkPFS.Core.AMPR;
using MkPFS.Core.Diagnostics;
using MkPFS.Core.Util;

namespace MkPFS.Build;

/// <summary>
/// AMPR emulation index <c>ampr_emu.index</c> (<c>AMPRIDX3</c>, port of Python <c>mkpfs/ampr.py</c>). Maps each game
/// file <c>/app0/&lt;rel&gt;</c> to its size and modification time, with an FNV-1a-64 open-addressed hash table for
/// path lookups. The layout is fixed by the PS5 AMPR/APR resolver.
/// </summary>
public static class AmprIndex
{
    /// <summary>Index file name in the source root.</summary>
    public const string IndexName = "ampr_emu.index";

    /// <summary>Marker that signals an emulation build (relative to the source root).</summary>
    public const string FakelibMarker = "fakelib/libSceAmpr.sprx";

    /// <summary>
    /// The AMPR Emu marker present in <paramref name="sourceRoot"/>: <see cref="FakelibMarker"/>, else
    /// <c>fakelib2/libSceAmpr.sprx</c> (ShadowMountPlus's exclusive overlay; Python MkPFS only checks <c>fakelib/</c>).
    /// </summary>
    /// <param name="sourceRoot">Game folder.</param>
    /// <returns>The marker, relative with <c>/</c>, or <see langword="null"/>.</returns>
    public static string? FindMarker(string sourceRoot)
    {
        foreach (string marker in (string[])[FakelibMarker, "fakelib2/libSceAmpr.sprx"])
        {
            string path = Path.Combine(sourceRoot, marker);
            if (File.Exists(path) || Directory.Exists(path))
            {
                return marker;
            }
        }

        return null;
    }

    /// <summary>Header size (<c>&lt;8sIIQQQII</c>).</summary>
    public const int HeaderSize = 48;

    /// <summary>Record size (<c>&lt;IIQq</c>: path offset, path length, size, mtime).</summary>
    public const int RecordSize = 24;

    /// <summary>Hash slot size (<c>&lt;QII</c>: hash, record index + 1, flags).</summary>
    public const int HashSlotSize = 16;

    private const uint Version = 3;
    private const uint DuplicateFlag = 1;
    private static readonly byte[] Magic = "AMPRIDX3"u8.ToArray();

    /// <summary>One indexed file.</summary>
    /// <param name="Size">File size.</param>
    /// <param name="MTime">Modification time, whole Unix seconds.</param>
    /// <param name="Path"><c>/app0/&lt;rel&gt;</c>.</param>
    public readonly record struct Row(long Size, long MTime, string Path);

    /// <summary>
    /// Sort and collision key: forward slashes, only ASCII <c>A..Z</c> folded (ampr_emu <c>build_ampr_index.py</c>
    /// <c>key_for</c>). Python MkPFS lower-cases every letter, which the console resolver does not do.
    /// </summary>
    /// <param name="path">Path.</param>
    /// <returns>Key.</returns>
    public static string KeyFor(string path) =>
        string.Create(path.Length, path, static (span, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];
                span[i] = c == '\\' ? '/' : c is >= 'A' and <= 'Z' ? (char)(c + 0x20) : c;
            }
        });

    /// <summary>
    /// FNV-1a 64 over the UTF-8 bytes of <see cref="KeyFor"/>; 0 becomes 1 so empty slots stay distinct. This is the
    /// hash the ampr_emu resolver computes for a lookup; Python MkPFS hashes lower-cased code points instead, so a
    /// non-ASCII path in its index is never found on the console. The offset basis is ampr_emu's
    /// <c>1469598103934665603</c>, not the standard FNV one that the pack manifest uses.
    /// </summary>
    /// <param name="path">Path.</param>
    /// <returns>Hash.</returns>
    public static ulong PathHash(string path)
    {
        ulong hash = 1469598103934665603UL;
        foreach (byte b in AMPRAssetPath.AsciiFoldPathBytes(path))
        {
            hash ^= b;
            hash = unchecked(hash * 1099511628211UL);
        }

        return hash == 0 ? 1 : hash;
    }

    /// <summary>Smallest power of two ≥ 2 × entries (minimum 2), 0 for no entries.</summary>
    /// <param name="entryCount">Entries.</param>
    /// <returns>Slot count.</returns>
    public static int HashSlotCount(int entryCount)
    {
        if (entryCount <= 0)
        {
            return 0;
        }

        int slots = 2;
        while (slots < entryCount * 2)
        {
            slots <<= 1;
        }

        return slots;
    }

    /// <summary>
    /// Generate the index when enabled and the marker exists (Python <c>ensure_ampr_index</c>). With
    /// <paramref name="createIfMissing"/>, a valid existing index is kept unless <paramref name="forceRegen"/>.
    /// </summary>
    /// <param name="sourceRoot">Source tree, which also receives the index.</param>
    /// <param name="log">Messages.</param>
    /// <param name="enabled">When false, nothing happens.</param>
    /// <param name="createIfMissing">Skip when a valid index exists.</param>
    /// <param name="forceRegen">Always rebuild.</param>
    /// <returns>The index path when generated, else <see langword="null"/>.</returns>
    public static string? Ensure(string sourceRoot, IMkPFSLog log, bool enabled = true, bool createIfMissing = false, bool forceRegen = false)
    {
        if (!enabled)
        {
            return null;
        }

        string indexPath = Path.Combine(sourceRoot, IndexName);
        if (FindMarker(sourceRoot) is not { } marker)
        {
            return null;
        }

        // An AMPR pack set addresses files by their row in this index; rebuilding renumbers the rows (the new
        // ampr_assets-*.pak files sort first) and the emulator then fails every packed read (not in Python MkPFS).
        bool packSet = File.Exists(indexPath) && File.Exists(Path.Combine(sourceRoot, AMPRPack.AMPRPackConfig.DefaultIndexName));
        if (packSet && !forceRegen)
        {
            log.Warning($"{IndexName} kept: the AMPR packs in this folder (ampr_assets.index) are bound to it; --ampr-force-regen rebuilds it and breaks them");
            return null;
        }

        if (packSet)
        {
            log.Warning($"Rebuilding {IndexName} although ampr_assets.index is present; the AMPR packs will stop working until they are rebuilt");
        }

        if (createIfMissing && !forceRegen && (File.Exists(indexPath) || Directory.Exists(indexPath)))
        {
            if (Validate(indexPath, sourceRoot))
            {
                log.Info($"{IndexName} valid and present; skipping generation (create_if_missing)");
                return null;
            }

            log.Warning($"{IndexName} failed validation; regenerating...");
        }

        log.Info($"Detected {marker}; generating {IndexName}...");
        int count;
        try
        {
            count = Build(sourceRoot, indexPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            log.Warning($"Failed to generate {IndexName}: {ex.Message}");
            return null;
        }

        log.Info($"Generated {IndexName} with {count} entries");
        return indexPath;
    }

    /// <summary>Write the index for <paramref name="root"/> (Python <c>build_ampr_index</c>).</summary>
    /// <param name="root">Source tree.</param>
    /// <param name="outputPath">Index path.</param>
    /// <returns>Records written (0 and no file when the tree has no files).</returns>
    public static int Build(string root, string outputPath)
    {
        outputPath = Path.GetFullPath(outputPath);
        return Write(ScanRows(root, outputPath), outputPath);
    }

    private static int Write(List<Row> rows, string outputPath)
    {
        string tmp = outputPath + ".tmp";
        if (rows.Count == 0)
        {
            return 0;
        }

        rows = [.. rows.OrderBy(r => KeyFor(r.Path), CodePointOrder)];
        byte[] index = Serialize(rows);
        try
        {
            File.WriteAllBytes(tmp, index);
            File.Move(tmp, outputPath, overwrite: true);
        }
        finally
        {
            TryDelete(tmp);
        }

        return rows.Count;
    }

    /// <summary>
    /// Write the index for the tree a game will have after <paramref name="overlayDirectory"/> (for example
    /// <c>fakelib</c>) is taken from <paramref name="overlayRoot"/> instead of <paramref name="root"/>: the files under
    /// that folder are listed with the overlay's sizes and times, everything else with the source's.
    /// </summary>
    /// <param name="root">Source tree.</param>
    /// <param name="overlayRoot">Tree holding the replacement folder.</param>
    /// <param name="overlayDirectory">Folder name relative to both roots.</param>
    /// <param name="outputPath">Index path.</param>
    /// <returns>Records written (0 and no file when there are no files).</returns>
    public static int BuildWithOverlay(string root, string overlayRoot, string overlayDirectory, string outputPath)
    {
        outputPath = Path.GetFullPath(outputPath);
        string prefix = KeyFor($"/app0/{overlayDirectory}/");
        List<Row> rows = [.. ScanRows(root, outputPath).Where(r => !KeyFor(r.Path).StartsWith(prefix, StringComparison.Ordinal))];
        string overlay = Path.Combine(Path.GetFullPath(overlayRoot), overlayDirectory);
        if (Directory.Exists(overlay))
        {
            HashSet<string> seen = [.. rows.Select(r => KeyFor(r.Path))];
            rows.AddRange(ScanRows(overlayRoot, outputPath, overlay).Where(r => seen.Add(KeyFor(r.Path))));
        }

        return Write(rows, outputPath);
    }

    /// <summary>Serialize rows already in record order.</summary>
    /// <param name="rows">Rows.</param>
    /// <returns>Index bytes.</returns>
    public static byte[] Serialize(IReadOnlyList<Row> rows)
    {
        using MemoryStream blob = new();
        byte[] records = new byte[rows.Count * RecordSize];
        for (int i = 0; i < rows.Count; i++)
        {
            byte[] encoded = Encoding.UTF8.GetBytes(rows[i].Path);
            Span<byte> record = records.AsSpan(i * RecordSize, RecordSize);
            BinaryPrimitives.WriteUInt32LittleEndian(record, checked((uint)blob.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], (uint)encoded.Length);
            BinaryPrimitives.WriteInt64LittleEndian(record[8..], rows[i].Size);
            BinaryPrimitives.WriteInt64LittleEndian(record[16..], rows[i].MTime);
            blob.Write(encoded);
            blob.WriteByte(0);
        }

        (ulong Hash, uint IndexPlusOne, uint Flags)[] slots = BuildHashSlots(rows);
        long pathEnd = HeaderSize + records.Length + blob.Length;
        long hashOffset = (pathEnd + (HashSlotSize - 1)) & ~(long)(HashSlotSize - 1);
        byte[] output = new byte[hashOffset + (slots.Length * HashSlotSize)];
        Span<byte> header = output;
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], Version);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], RecordSize);
        BinaryPrimitives.WriteInt64LittleEndian(header[16..], rows.Count);
        BinaryPrimitives.WriteInt64LittleEndian(header[24..], blob.Length);
        BinaryPrimitives.WriteInt64LittleEndian(header[32..], hashOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], HashSlotSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[44..], (uint)slots.Length);
        records.CopyTo(output, HeaderSize);
        blob.ToArray().CopyTo(output, HeaderSize + records.Length);
        for (int i = 0; i < slots.Length; i++)
        {
            Span<byte> slot = output.AsSpan((int)hashOffset + (i * HashSlotSize), HashSlotSize);
            BinaryPrimitives.WriteUInt64LittleEndian(slot, slots[i].Hash);
            BinaryPrimitives.WriteUInt32LittleEndian(slot[8..], slots[i].IndexPlusOne);
            BinaryPrimitives.WriteUInt32LittleEndian(slot[12..], slots[i].Flags);
        }

        return output;
    }

    /// <summary>Read the rows of an index (for tests and diagnostics).</summary>
    /// <param name="data">Index bytes.</param>
    /// <returns>Rows in record order.</returns>
    /// <exception cref="InvalidDataException">The header or a record is invalid.</exception>
    public static List<Row> ReadRows(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || !data[..8].SequenceEqual(Magic))
        {
            throw new InvalidDataException("not an AMPRIDX3 index");
        }

        long count = BinaryPrimitives.ReadInt64LittleEndian(data[16..]);
        long blobLength = BinaryPrimitives.ReadInt64LittleEndian(data[24..]);
        long blobStart = HeaderSize + (count * RecordSize);
        if (count < 0 || blobLength < 0 || blobStart + blobLength > data.Length)
        {
            throw new InvalidDataException("AMPR index is truncated");
        }

        List<Row> rows = [];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> record = data.Slice(HeaderSize + (i * RecordSize), RecordSize);
            long offset = BinaryPrimitives.ReadUInt32LittleEndian(record);
            long length = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
            if (offset + length > blobLength)
            {
                throw new InvalidDataException($"AMPR record {i} points outside the path blob");
            }

            string path = Encoding.UTF8.GetString(data.Slice((int)(blobStart + offset), (int)length));
            rows.Add(new Row(BinaryPrimitives.ReadInt64LittleEndian(record[8..]), BinaryPrimitives.ReadInt64LittleEndian(record[16..]), path));
        }

        return rows;
    }

    /// <summary>
    /// Header sanity checks plus a comparison of every indexed path (case-insensitive) and size with the live tree.
    /// Modification times are not compared: copying a game folder changes them. Python <c>validate_ampr_index</c>
    /// only compares the row count, so a swapped or resized file passes there (oracle finding 19).
    /// </summary>
    /// <param name="indexPath">Index path.</param>
    /// <param name="sourceRoot">Source tree.</param>
    /// <returns><see langword="true"/> when the index looks current.</returns>
    public static bool Validate(string indexPath, string sourceRoot)
    {
        byte[] data;
        try
        {
            if (!File.Exists(indexPath))
            {
                return false;
            }

            data = File.ReadAllBytes(indexPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (data.Length < HeaderSize || !data.AsSpan(0, 8).SequenceEqual(Magic) ||
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)) != Version ||
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12)) != RecordSize)
        {
            return false;
        }

        long rows = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(16));
        long blobLength = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(24));
        long hashOffset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(32));
        uint slotSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(40));
        uint slots = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(44));
        if (rows <= 0 || blobLength <= 0 || slotSize != HashSlotSize || slots == 0 ||
            rows > (long.MaxValue - HeaderSize) / RecordSize ||
            hashOffset < HeaderSize + (rows * RecordSize) + blobLength || hashOffset % slotSize != 0 ||
            data.Length < hashOffset + ((long)slots * slotSize))
        {
            return false;
        }

        Dictionary<string, long> indexed = new(StringComparer.Ordinal);
        List<Row> live;
        try
        {
            foreach (Row row in ReadRows(data))
            {
                if (!indexed.TryAdd(KeyFor(row.Path), row.Size))
                {
                    return false;
                }
            }

            live = ScanRows(sourceRoot, Path.GetFullPath(indexPath));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return live.Count == indexed.Count &&
            live.All(row => indexed.TryGetValue(KeyFor(row.Path), out long size) && size == row.Size);
    }

    // Files the index lists, in walk order: the index and its temp file, the emulator's trace and log, and paths with
    // tabs or line breaks skipped (as build_ampr_index.py and the runtime scan do); case-insensitive duplicates
    // dropped (first wins).
    private static List<Row> ScanRows(string root, string indexPath, string? walkFrom = null)
    {
        root = Path.GetFullPath(root);
        string tmp = indexPath + ".tmp";
        List<Row> rows = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string file in WalkFiles(walkFrom ?? root))
        {
            if (string.Equals(file, indexPath, PathComparison) || string.Equals(file, tmp, PathComparison))
            {
                continue;
            }

            string indexed = "/app0/" + Path.GetRelativePath(root, file).Replace('\\', '/');
            string key = KeyFor(indexed);
            if (key == $"/app0/{IndexName}" || key == $"/app0/{IndexName}.tmp" || SkippedRootFiles.Contains(key) ||
                indexed.AsSpan().IndexOfAny('\t', '\n', '\r') >= 0 || !seen.Add(key))
            {
                continue;
            }

            FileInfo info = new(file);
            rows.Add(new Row(info.Length, UnixSeconds(info.LastWriteTimeUtc), indexed));
        }

        return rows;
    }

    // Written into /app0 by debug emulator builds; never indexed.
    private static readonly HashSet<string> SkippedRootFiles = new(StringComparer.Ordinal) { "/app0/ampr_commands.bin", "/app0/apr_emu.log" };

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Preserve the original index-generation failure when cleanup also fails.
        }
    }

    private static IComparer<string> CodePointOrder { get; } = Comparer<string>.Create(static (a, b) =>
    {
        StringRuneEnumerator x = a.EnumerateRunes();
        StringRuneEnumerator y = b.EnumerateRunes();
        while (true)
        {
            bool hasX = x.MoveNext();
            bool hasY = y.MoveNext();
            if (!hasX || !hasY)
            {
                return hasX.CompareTo(hasY);
            }

            int diff = x.Current.Value.CompareTo(y.Current.Value);
            if (diff != 0)
            {
                return diff;
            }
        }
    });

    // Python os.walk top-down with directories and files sorted by key_for(name): ignored names skipped,
    // directory links listed but not descended, file links kept when their target is a file.
    private static IEnumerable<string> WalkFiles(string root)
    {
        EnumerationOptions options = new() { AttributesToSkip = 0, IgnoreInaccessible = true };
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string dir = pending.Pop();
            List<FileSystemInfo> entries = [.. new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options)
                .Where(e => !NameRules.IsIgnoredName(e.Name))];
            foreach (FileSystemInfo file in entries.Where(e => e is FileInfo).OrderBy(e => KeyFor(e.Name), CodePointOrder))
            {
                if (file.LinkTarget is null || file.ResolveLinkTarget(returnFinalTarget: true) is FileInfo { Exists: true })
                {
                    yield return file.FullName;
                }
            }

            // Push in reverse so subdirectories are visited in sorted order.
            foreach (FileSystemInfo sub in entries.Where(e => e is DirectoryInfo && e.LinkTarget is null)
                         .OrderByDescending(e => KeyFor(e.Name), CodePointOrder))
            {
                pending.Push(sub.FullName);
            }
        }
    }

    // Python int(st_mtime): truncate toward zero.
    private static long UnixSeconds(DateTime utc) => (utc.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerSecond;

    private static (ulong Hash, uint IndexPlusOne, uint Flags)[] BuildHashSlots(IReadOnlyList<Row> rows)
    {
        (ulong Hash, uint IndexPlusOne, uint Flags)[] slots = new (ulong, uint, uint)[HashSlotCount(rows.Count)];
        if (slots.Length == 0)
        {
            return slots;
        }

        int mask = slots.Length - 1;
        for (int i = 0; i < rows.Count; i++)
        {
            ulong hash = PathHash(rows[i].Path);
            int pos = (int)(hash & (ulong)mask);
            while (slots[pos].IndexPlusOne != 0)
            {
                if (slots[pos].Hash == hash)
                {
                    slots[pos].Flags |= DuplicateFlag;
                }

                pos = (pos + 1) & mask;
            }

            slots[pos] = (hash, (uint)(i + 1), 0);
        }

        return slots;
    }
}
