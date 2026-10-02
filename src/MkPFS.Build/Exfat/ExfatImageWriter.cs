using System.Buffers.Binary;
using MkPFS.Core.Diagnostics;
using MkPFS.Core.Exfat;
using System.Text;
using MkPFS.Core.Util;

namespace MkPFS.Build.Exfat;

/// <summary>
/// Forward-only exFAT image serializer (port of Python <c>mkpfs/exfat_writer.py</c>). The whole layout is computed
/// from the scanned tree first, then the image is produced strictly in offset order (boot regions, FAT, cluster
/// heap), so it can stream straight into a file or the PFSC encoder without a temporary image.
/// </summary>
public sealed class ExfatImageWriter
{
    /// <summary>Default cluster size: 64 KiB, required by the PS5 LVD/BFS fast path.</summary>
    public const int DefaultClusterSize = 64 * 1024;

    private const int BytesPerSector = 512;
    private const int BytesPerSectorShift = 9;
    private const int FatOffsetSectors = 128;
    private const int NumberOfFats = 1;
    private const uint FatEntryEndOfChain = 0xFFFFFFFF;
    private const uint FatEntryMedia = 0xFFFFFFF8;
    private const uint VolumeSerial = 0x4D6B5046; // "MkPF"; fixed for deterministic output
    private const uint FixedTimestamp = ((2024 - 1980) << 25) | (1 << 21) | (1 << 16); // 2024-01-01 00:00:00
    private const ushort AttrDirectory = 0x10;
    private const ushort AttrArchive = 0x20;
    private const byte FlagAllocationPossible = 0x01;
    private const int NameCharsPerEntry = 15;
    private const uint BitmapFirstCluster = 2;
    private const int FileReadChunk = 1024 * 1024;

    private readonly Node _root;
    private readonly long _bitmapClusters;
    private readonly long _upcaseClusters;
    private readonly long _clusterCount;
    private readonly long _fatLengthSectors;
    private readonly long _heapOffsetSectors;

    private ExfatImageWriter(Node root, int clusterSize)
    {
        _root = root;
        ClusterSize = clusterSize;
        long sectorsPerCluster = clusterSize / BytesPerSector;
        (_bitmapClusters, _upcaseClusters, _clusterCount) = AssignClusters(root, clusterSize);
        if (_clusterCount > uint.MaxValue - 16)
        {
            throw new InvalidOperationException("source tree is too large for an exFAT volume with this cluster size");
        }

        _fatLengthSectors = AlignUp(Sizes.CeilDiv((_clusterCount + 2) * 4, BytesPerSector), sectorsPerCluster);
        _heapOffsetSectors = AlignUp(FatOffsetSectors + (_fatLengthSectors * NumberOfFats), sectorsPerCluster);
        ImageSize = (_heapOffsetSectors + (_clusterCount * sectorsPerCluster)) * BytesPerSector;
    }

    /// <summary>Cluster size in bytes.</summary>
    public int ClusterSize { get; }

    /// <summary>Total image size in bytes.</summary>
    public long ImageSize { get; }

    /// <summary>Scan <paramref name="sourceRoot"/> and compute the layout.</summary>
    /// <param name="sourceRoot">Directory whose contents become the volume root.</param>
    /// <param name="clusterSize">Cluster size in bytes, or <see langword="null"/> for <see cref="DefaultClusterSize"/>.</param>
    /// <returns>Writer ready to emit the image.</returns>
    public static ExfatImageWriter Plan(string sourceRoot, int? clusterSize = null) =>
        new(ScanTree(sourceRoot), clusterSize ?? DefaultClusterSize);

    /// <summary>
    /// Write a complete image (Python <c>write_exfat_image</c>). An existing directory as <paramref name="outputPath"/>
    /// receives <c>&lt;titleId&gt;.exfat</c>.
    /// </summary>
    /// <param name="sourceRoot">Source directory.</param>
    /// <param name="outputPath">Image path or existing directory.</param>
    /// <param name="clusterSize">Cluster size, or <see langword="null"/> for the default.</param>
    /// <param name="progress">Optional progress, phase <c>exfat</c>.</param>
    /// <returns>The path written.</returns>
    public static string Write(string sourceRoot, string outputPath, int? clusterSize = null, IProgressSink? progress = null)
    {
        if (Directory.Exists(outputPath))
        {
            outputPath = Path.Combine(outputPath, GameParams.DefaultImageBasename(sourceRoot) + ".exfat");
        }

        ExfatImageWriter writer = Plan(sourceRoot, clusterSize);
        using FileStream output = new(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        writer.WriteTo(output, progress);
        return outputPath;
    }

    /// <summary>Write the image to <paramref name="output"/>, reporting progress every 8 MiB.</summary>
    /// <param name="output">Destination stream.</param>
    /// <param name="progress">Optional progress, phase <c>exfat</c>.</param>
    public void WriteTo(Stream output, IProgressSink? progress = null)
    {
        const long updateInterval = 8L * 1024 * 1024;
        long total = Math.Max(ImageSize, 1);
        progress?.Report("exfat", 0, total);
        long written = 0;
        long lastReported = 0;
        foreach (ReadOnlyMemory<byte> chunk in Chunks())
        {
            output.Write(chunk.Span);
            written += chunk.Length;
            if (progress is not null && written - lastReported >= updateInterval)
            {
                lastReported = written;
                progress.Report("exfat", Math.Min(written, total), total, written);
            }
        }

        progress?.Report("exfat", total, total, written);
    }

    /// <summary>Forward-only stream over <see cref="Chunks"/>, for consumers that read a <see cref="Stream"/>.</summary>
    /// <returns>Readable, non-seekable stream of <see cref="ImageSize"/> bytes.</returns>
    public Stream OpenRead() => new ChunkStream(Chunks().GetEnumerator(), ImageSize);

    /// <summary>
    /// The image in increasing offset order; concatenation is the volume. File chunks reuse one buffer, so each chunk
    /// is valid only until the next one is requested.
    /// </summary>
    /// <returns>Chunks.</returns>
    /// <exception cref="IOException">A source file changed size since the scan.</exception>
    public IEnumerable<ReadOnlyMemory<byte>> Chunks()
    {
        // 1. Boot region and its backup copy.
        byte[] boot = BuildBootRegion();
        yield return boot;
        yield return boot;

        // 2. Pad to the FAT, the FAT, then pad to the cluster heap.
        yield return new byte[(FatOffsetSectors - 24) * BytesPerSector];
        yield return BuildFat();
        long padToHeap = (_heapOffsetSectors - (FatOffsetSectors + (_fatLengthSectors * NumberOfFats))) * BytesPerSector;
        if (padToHeap > 0)
        {
            yield return new byte[padToHeap];
        }

        // 3. Cluster heap in cluster order: bitmap, up-case table, root directory, then the tree in pre-order.
        yield return BuildBitmap();
        byte[] upcase = new byte[_upcaseClusters * ClusterSize];
        ExfatUpcase.Table.CopyTo(upcase);
        yield return upcase;
        yield return BuildDirectory(_root, isRoot: true);

        byte[] buffer = new byte[FileReadChunk];
        foreach (ReadOnlyMemory<byte> chunk in EmitChildren(_root, buffer))
        {
            yield return chunk;
        }
    }

    private IEnumerable<ReadOnlyMemory<byte>> EmitChildren(Node node, byte[] buffer)
    {
        foreach (Node child in node.Children)
        {
            if (child.IsDir)
            {
                yield return BuildDirectory(child, isRoot: false);
                foreach (ReadOnlyMemory<byte> chunk in EmitChildren(child, buffer))
                {
                    yield return chunk;
                }
            }
            else if (child.Size > 0)
            {
                long written = 0;
                using (FileStream file = new(child.AbsPath!, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
                {
                    while (written < child.Size)
                    {
                        int read = file.Read(buffer, 0, (int)Math.Min(buffer.Length, child.Size - written));
                        if (read == 0)
                        {
                            break;
                        }

                        written += read;
                        yield return buffer.AsMemory(0, read);
                    }

                    if (written != child.Size || file.Length != child.Size)
                    {
                        throw new IOException($"source file changed size while packing: {child.AbsPath}");
                    }
                }

                long padding = (child.ClusterCount * ClusterSize) - written;
                if (padding > 0)
                {
                    yield return new byte[padding];
                }
            }
        }
    }

    private static Node ScanTree(string sourceRoot)
    {
        Node root = new(string.Empty, string.Empty, isDir: true);
        Walk(new DirectoryInfo(sourceRoot), root);
        return root;

        static void Walk(DirectoryInfo dir, Node node)
        {
            // Python sorts by name.lower(); links (and Windows junctions) are skipped like is_dir/is_file(follow_symlinks=False).
            IEnumerable<FileSystemInfo> entries = dir.EnumerateFileSystemInfos()
                .Where(e => !NameRules.IsIgnoredName(e.Name) && e.LinkTarget is null)
                .OrderBy(e => e.Name.ToLowerInvariant(), CodePointComparer.Instance);
            foreach (FileSystemInfo entry in entries)
            {
                string rel = node.RelPath.Length > 0 ? $"{node.RelPath}/{entry.Name}" : entry.Name;
                if (entry is DirectoryInfo childDir)
                {
                    Node child = new(rel, entry.Name, isDir: true);
                    node.Children.Add(child);
                    Walk(childDir, child);
                }
                else if (entry is FileInfo file)
                {
                    node.Children.Add(new Node(rel, entry.Name, isDir: false) { Size = file.Length, AbsPath = file.FullName });
                }
            }
        }
    }

    private static long DirectoryEntryCount(Node node, bool isRoot)
    {
        long count = isRoot ? 3 : 0; // root adds Volume Label, Allocation Bitmap and Up-case Table entries
        foreach (Node child in node.Children)
        {
            count += 2 + Sizes.CeilDiv(child.Name.Length, NameCharsPerEntry);
        }

        return count;
    }

    private static long NodeClusters(Node node, bool isRoot, int clusterSize) =>
        node.IsDir ? Math.Max(1, Sizes.CeilDiv(DirectoryEntryCount(node, isRoot) * 32, clusterSize)) : Sizes.CeilDiv(node.Size, clusterSize);

    private static (long Bitmap, long Upcase, long Total) AssignClusters(Node root, int clusterSize)
    {
        long upcaseClusters = Sizes.CeilDiv(ExfatUpcase.Table.Length, clusterSize);
        long contentClusters = NodeClusters(root, isRoot: true, clusterSize) + Accumulate(root) + upcaseClusters;

        // The bitmap must cover itself plus all content.
        long bitmapClusters = 1;
        while (true)
        {
            long need = Sizes.CeilDiv(Sizes.CeilDiv(bitmapClusters + contentClusters, 8), clusterSize);
            if (need == bitmapClusters)
            {
                break;
            }

            bitmapClusters = need;
        }

        long next = BitmapFirstCluster + bitmapClusters + upcaseClusters;
        root.FirstCluster = next;
        root.ClusterCount = NodeClusters(root, isRoot: true, clusterSize);
        next += root.ClusterCount;
        Assign(root);
        return (bitmapClusters, upcaseClusters, bitmapClusters + contentClusters);

        long Accumulate(Node node)
        {
            long sum = 0;
            foreach (Node child in node.Children)
            {
                sum += NodeClusters(child, isRoot: false, clusterSize);
                if (child.IsDir)
                {
                    sum += Accumulate(child);
                }
            }

            return sum;
        }

        void Assign(Node node)
        {
            foreach (Node child in node.Children)
            {
                child.ClusterCount = NodeClusters(child, isRoot: false, clusterSize);
                if (child.ClusterCount > 0)
                {
                    child.FirstCluster = next;
                    next += child.ClusterCount;
                }

                if (child.IsDir)
                {
                    Assign(child);
                }
            }
        }
    }

    // exFAT NameHash over the up-cased UTF-16LE name.
    private static ushort NameHash(string name)
    {
        ushort hash = 0;
        foreach (char c in name)
        {
            char upper = ExfatUpcase.ToUpper(c);
            hash = (ushort)(((hash << 15) | (hash >> 1)) + (byte)upper);
            hash = (ushort)(((hash << 15) | (hash >> 1)) + (byte)(upper >> 8));
        }

        return hash;
    }

    private static ushort EntrySetChecksum(ReadOnlySpan<byte> entries)
    {
        ushort checksum = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            if (i is 2 or 3)
            {
                continue; // SetChecksum field of the File entry
            }

            checksum = (ushort)(((checksum << 15) | (checksum >> 1)) + entries[i]);
        }

        return checksum;
    }

    private void WriteFileEntrySet(Node child, Span<byte> destination)
    {
        int nameEntries = (int)Sizes.CeilDiv(child.Name.Length, NameCharsPerEntry);
        long dataLength = child.IsDir ? child.ClusterCount * ClusterSize : child.Size;
        bool hasAllocation = child.FirstCluster >= 2;
        Span<byte> set = destination[..((2 + nameEntries) * 32)];

        Span<byte> file = set[..32];
        file[0] = 0x85;
        file[1] = (byte)(1 + nameEntries);
        BinaryPrimitives.WriteUInt16LittleEndian(file[0x04..], child.IsDir ? AttrDirectory : AttrArchive);
        BinaryPrimitives.WriteUInt32LittleEndian(file[0x08..], FixedTimestamp);
        BinaryPrimitives.WriteUInt32LittleEndian(file[0x0C..], FixedTimestamp);
        BinaryPrimitives.WriteUInt32LittleEndian(file[0x10..], FixedTimestamp);

        Span<byte> stream = set.Slice(32, 32);
        stream[0] = 0xC0;
        stream[1] = hasAllocation ? FlagAllocationPossible : (byte)0;
        stream[3] = (byte)child.Name.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(stream[0x04..], NameHash(child.Name));
        BinaryPrimitives.WriteInt64LittleEndian(stream[0x08..], dataLength);
        BinaryPrimitives.WriteUInt32LittleEndian(stream[0x14..], hasAllocation ? (uint)child.FirstCluster : 0);
        BinaryPrimitives.WriteInt64LittleEndian(stream[0x18..], dataLength);

        for (int i = 0; i < nameEntries; i++)
        {
            Span<byte> entry = set.Slice(64 + (i * 32), 32);
            entry[0] = 0xC1;
            ReadOnlySpan<char> chunk = child.Name.AsSpan(i * NameCharsPerEntry, Math.Min(NameCharsPerEntry, child.Name.Length - (i * NameCharsPerEntry)));
            for (int k = 0; k < chunk.Length; k++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(entry[(2 + (k * 2))..], chunk[k]);
            }
        }

        BinaryPrimitives.WriteUInt16LittleEndian(file[0x02..], EntrySetChecksum(set));
    }

    private byte[] BuildDirectory(Node node, bool isRoot)
    {
        // Unused bytes stay zero (end-of-directory markers).
        byte[] bytes = new byte[node.ClusterCount * ClusterSize];
        int off = 0;
        if (isRoot)
        {
            bytes[0] = 0x83; // empty Volume Label

            Span<byte> bitmap = bytes.AsSpan(32, 32);
            bitmap[0] = 0x81;
            BinaryPrimitives.WriteUInt32LittleEndian(bitmap[0x14..], BitmapFirstCluster);
            BinaryPrimitives.WriteInt64LittleEndian(bitmap[0x18..], Sizes.CeilDiv(_clusterCount, 8));

            Span<byte> upcase = bytes.AsSpan(64, 32);
            upcase[0] = 0x82;
            BinaryPrimitives.WriteUInt32LittleEndian(upcase[0x04..], ExfatUpcase.TableChecksum);
            BinaryPrimitives.WriteUInt32LittleEndian(upcase[0x14..], BitmapFirstCluster + (uint)_bitmapClusters);
            BinaryPrimitives.WriteInt64LittleEndian(upcase[0x18..], ExfatUpcase.Table.Length);
            off = 96;
        }

        foreach (Node child in node.Children)
        {
            WriteFileEntrySet(child, bytes.AsSpan(off));
            off += (int)(2 + Sizes.CeilDiv(child.Name.Length, NameCharsPerEntry)) * 32;
        }

        return bytes;
    }

    // 12-sector main boot region; the caller emits it twice (main and backup).
    private byte[] BuildBootRegion()
    {
        byte[] region = new byte[12 * BytesPerSector];
        Span<byte> vbr = region.AsSpan(0, BytesPerSector);
        vbr[0] = 0xEB;
        vbr[1] = 0x76;
        vbr[2] = 0x90;
        ExfatReader.Signature.CopyTo(vbr[3..]);
        BinaryPrimitives.WriteInt64LittleEndian(vbr[72..], _heapOffsetSectors + (_clusterCount * (ClusterSize / BytesPerSector)));
        BinaryPrimitives.WriteUInt32LittleEndian(vbr[80..], FatOffsetSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(vbr[84..], (uint)_fatLengthSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(vbr[88..], (uint)_heapOffsetSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(vbr[92..], (uint)_clusterCount);
        BinaryPrimitives.WriteUInt32LittleEndian(vbr[96..], (uint)_root.FirstCluster);
        BinaryPrimitives.WriteUInt32LittleEndian(vbr[100..], VolumeSerial);
        BinaryPrimitives.WriteUInt16LittleEndian(vbr[104..], 0x0100); // FileSystemRevision 1.00
        vbr[108] = BytesPerSectorShift;
        vbr[109] = (byte)(System.Numerics.BitOperations.Log2((uint)(ClusterSize / BytesPerSector)));
        vbr[110] = NumberOfFats;
        vbr[111] = 0x80; // DriveSelect
        vbr[112] = 0xFF; // PercentInUse not available
        BinaryPrimitives.WriteUInt16LittleEndian(vbr[510..], 0xAA55);

        // Extended boot sectors 1..8 end with the extended boot signature; sectors 9 and 10 stay zero.
        for (int sector = 1; sector <= 8; sector++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(region.AsSpan(((sector + 1) * BytesPerSector) - 4), 0xAA550000);
        }

        uint checksum = 0;
        for (int i = 0; i < 11 * BytesPerSector; i++)
        {
            if (i is 106 or 107 or 112)
            {
                continue; // VolumeFlags and PercentInUse are excluded
            }

            checksum = ((checksum << 31) | (checksum >> 1)) + region[i];
        }

        for (int i = 11 * BytesPerSector; i < region.Length; i += 4)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(region.AsSpan(i), checksum);
        }

        return region;
    }

    // Explicit FAT chain for every contiguous run.
    private byte[] BuildFat()
    {
        byte[] fat = new byte[_fatLengthSectors * BytesPerSector];
        BinaryPrimitives.WriteUInt32LittleEndian(fat, FatEntryMedia);
        BinaryPrimitives.WriteUInt32LittleEndian(fat.AsSpan(4), FatEntryEndOfChain);
        Chain(BitmapFirstCluster, _bitmapClusters);
        Chain(BitmapFirstCluster + _bitmapClusters, _upcaseClusters);
        Walk(_root);
        return fat;

        void Walk(Node node)
        {
            if (node.FirstCluster >= 2 && node.ClusterCount > 0)
            {
                Chain(node.FirstCluster, node.ClusterCount);
            }

            foreach (Node child in node.Children)
            {
                Walk(child);
            }
        }

        void Chain(long first, long count)
        {
            for (long i = 0; i < count - 1; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(fat.AsSpan((int)((first + i) * 4)), (uint)(first + i + 1));
            }

            BinaryPrimitives.WriteUInt32LittleEndian(fat.AsSpan((int)((first + count - 1) * 4)), FatEntryEndOfChain);
        }
    }

    // Every heap cluster is allocated (tight image).
    private byte[] BuildBitmap()
    {
        byte[] bitmap = new byte[AlignUp(Sizes.CeilDiv(_clusterCount, 8), ClusterSize)];
        bitmap.AsSpan(0, (int)(_clusterCount / 8)).Fill(0xFF);
        if (_clusterCount % 8 != 0)
        {
            bitmap[_clusterCount / 8] = (byte)((1 << (int)(_clusterCount % 8)) - 1);
        }

        return bitmap;
    }

    private static long AlignUp(long value, long alignment) => Sizes.CeilDiv(value, alignment) * alignment;

    private sealed class ChunkStream(IEnumerator<ReadOnlyMemory<byte>> chunks, long length) : Stream
    {
        private ReadOnlyMemory<byte> _current;
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            while (_current.IsEmpty)
            {
                if (!chunks.MoveNext())
                {
                    return 0;
                }

                _current = chunks.Current;
            }

            int take = Math.Min(buffer.Length, _current.Length);
            _current.Span[..take].CopyTo(buffer);
            _current = _current[take..];
            _position += take;
            return take;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                chunks.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class Node(string relPath, string name, bool isDir)
    {
        public string RelPath { get; } = relPath;

        public string Name { get; } = name;

        public bool IsDir { get; } = isDir;

        public long Size { get; init; }

        public string? AbsPath { get; init; }

        public List<Node> Children { get; } = [];

        public long FirstCluster { get; set; }

        public long ClusterCount { get; set; }
    }

    // Python compares str by code point; UTF-16 ordinal order differs only for supplementary characters.
    private sealed class CodePointComparer : IComparer<string>
    {
        public static readonly CodePointComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            StringRuneEnumerator a = (x ?? string.Empty).EnumerateRunes();
            StringRuneEnumerator b = (y ?? string.Empty).EnumerateRunes();
            while (true)
            {
                bool hasA = a.MoveNext();
                bool hasB = b.MoveNext();
                if (!hasA || !hasB)
                {
                    return hasA.CompareTo(hasB);
                }

                int diff = a.Current.Value.CompareTo(b.Current.Value);
                if (diff != 0)
                {
                    return diff;
                }
            }
        }
    }
}
