using System.Buffers.Binary;
using System.Text;
using MkPFS.Core.Compression.Kraken;
using MkPFS.Core.Crypto;
using MkPFS.Core.PFS.PS5;
using MkPFS.Core.PKG;
using MkPFS.Core.SELF;

namespace MkPFS.Build.FPKG;

/// <summary>A file placed in the inner image.</summary>
/// <param name="Input">Content.</param>
/// <param name="Inode">Inode number.</param>
/// <param name="Afid">File id.</param>
/// <param name="LogicalOffset">Start in the inner mount (data end for an empty file).</param>
/// <param name="Size">Size.</param>
/// <param name="Executable">SELF or ELF module (inode flag 0x40, own 64 KiB-aligned stored region).</param>
public sealed record PS5InnerPlacement(FPKGInput Input, int Inode, int Afid, long LogicalOffset, long Size, bool Executable)
{
    /// <summary>Path from the user root.</summary>
    public string Path => Input.Path;
}

/// <summary>What a stored chunk of the inner image holds.</summary>
public enum PS5InnerChunkRole
{
    /// <summary>File data.</summary>
    File,

    /// <summary>Zero gap between the data and the metadata.</summary>
    Gap,

    /// <summary>Inner PFS metadata.</summary>
    Metadata,
}

/// <summary>One chunk of the stored inner image, in naps order.</summary>
/// <param name="Role">Content.</param>
/// <param name="LogicalOffset">Start in the inner mount.</param>
/// <param name="Length">Logical length.</param>
/// <param name="StoredOffset">Start in <c>pfs_image.dat</c>.</param>
/// <param name="StoredLength">Stored length.</param>
/// <param name="FirstHalf">Stored length of the first 128 KiB half (the whole chunk when it has one part).</param>
/// <param name="SecondHalf">Stored length of the second half, 0 when the chunk has one part.</param>
/// <param name="FileIndex">Index in <see cref="PS5InnerWriter.Files"/> for a file chunk, else −1.</param>
public sealed record PS5InnerChunk(PS5InnerChunkRole Role, long LogicalOffset, int Length, long StoredOffset, int StoredLength, int FirstHalf, int SecondHalf, int FileIndex);

/// <summary>Result of <see cref="PS5InnerWriter.Write"/>.</summary>
/// <param name="Naps"><c>naps_pkg_layout.dat</c>.</param>
/// <param name="GapStoredOffset">Stored offset of the zero gap (the end of the file data region).</param>
/// <param name="StoredSize">Stored size (not padded).</param>
/// <param name="Chunks">Stored chunks in naps order.</param>
/// <param name="ExtentDigests">
/// SHA3-256 of the stored bytes of each file (its chunks are contiguous) and of each metadata chunk, keyed by
/// (stored offset, stored length); naps_meta_18 lists them.
/// </param>
public sealed record PS5InnerResult(byte[] Naps, long GapStoredOffset, long StoredSize, IReadOnlyList<PS5InnerChunk> Chunks,
    IReadOnlyDictionary<(long Offset, long Length), byte[]> ExtentDigests);

/// <summary>
/// Writes the inner <c>pfs_image.dat</c> and its <c>naps_pkg_layout.dat</c> the way PS5PkgTool does: Publishing
/// Tools' record encoding and metadata (docs/FORMATS.md §13) with PS5PkgTool's chunking and placement
/// (<c>tools/oracle-ppt/README.md</c> findings 6, 9 and 14):
/// <list type="bullet">
/// <item>afid order: <c>sce_sys/keystone</c>, the other sce_sys files, then every other file, each group ordinal
/// by path; empty files are listed last and have no afid-table slot.</item>
/// <item>Logical mount: files packed in afid order, except that a 256 KiB ublock holds at most 30 naps records,
/// so a file that would exceed that moves one ublock on (same offset within it), leaving a logical hole; metadata
/// at (⌈data end / 256 KiB⌉ + 15) × 256 KiB: superblock, inode table (390 inodes per block), super-root dirents,
/// the two FLTs, the afid table and each directory's dirents in pre-order, each table packed across as many
/// blocks as it needs, padded to an even block count.</item>
/// <item>Inodes: 0 super-root, 1–3 the internal tables, 4 uroot, directories in pre-order, then files by
/// directory in post-order, ordinal names.</item>
/// <item>Stored image: 128 KiB chunks from each file's start, runs re-anchored at 64 KiB for executables and the
/// file after one (and, stored mode only, for a file that would cross a 64 KiB block); the zero gap as 256 KiB
/// fill chunks sharing one 16-byte block; the metadata behind a 0x400-byte PFSC container header.</item>
/// <item>Kraken mode: every chunk of a non-executable file but its last, and every metadata chunk, is compressed
/// where that pays (a constant file chunk as a fill block).</item>
/// </list>
/// </summary>
public sealed class PS5InnerWriter
{
    /// <summary>Block size.</summary>
    public const int BlockSize = 0x10000;

    private const int UBlock = NAPSLayout.UBlockSize;
    private const int InodeSize = 0xA8;
    private const int InodesPerBlock = BlockSize / InodeSize;
    private const int MaxRecordsPerUBlock = 30;

    // One Kraken fill block covers at most 128 KiB.
    private const int Half = 0x20000;

    // Kraken fill block of `length` bytes of `value`: 3-byte (4 << 19) | (length − 1) << 2, 00 03 00, then
    // 0x4000 | value << 6 (docs/FORMATS.md §13); 128 KiB of zeros is 27 FF FC 00 03 00 40 00.
    private static byte[] ZeroFill(int length, byte value = 0)
    {
        int header = (4 << 19) | ((length - 1) << 2);
        int tail = 0x4000 | (value << 6);
        return [(byte)(header >> 16), (byte)(header >> 8), (byte)header, 0x00, 0x03, 0x00, (byte)(tail >> 8), (byte)tail];
    }

    private readonly long _seconds;
    private readonly uint _nanoseconds;
    private readonly List<PS5InnerPlacement> _files;
    private readonly HashSet<int> _movedFiles = [];

    private PS5InnerWriter(List<PS5InnerPlacement> files, List<string> directories, long seconds, uint nanoseconds, bool compress)
    {
        Compress = compress;
        _files = files;
        Directories = directories;
        _seconds = seconds;
        _nanoseconds = nanoseconds;
    }

    /// <summary>Kraken mode (PS5PkgTool <c>auto</c>): compress file chunks and the metadata where it pays.</summary>
    public bool Compress { get; }

    /// <summary>
    /// Called on the writing thread as each file is done, with its placement and its chunks (none for an empty file),
    /// so a caller can report files live.
    /// </summary>
    public Action<PS5InnerPlacement, IReadOnlyList<PS5InnerChunk>>? FileWritten { get; set; }

    /// <summary>Kraken mode only: parse greedily (about twice as fast, a few percent larger).</summary>
    public bool Fast { get; set; }

    /// <summary>Chunks encoded at once in Kraken mode (default: <see cref="PS5KrakenChunk.MaxParallelism"/>).</summary>
    public int Workers
    {
        get;
        set => field = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "at least one worker");
    } = PS5KrakenChunk.MaxParallelism;

    /// <summary>Files in afid order (empty files last); logical offsets are final after <see cref="Write"/>.</summary>
    public IReadOnlyList<PS5InnerPlacement> Files => _files;

    /// <summary>Directories in pre-order, uroot ("") first.</summary>
    public IReadOnlyList<string> Directories { get; }

    /// <summary>End of the file data (after <see cref="Write"/>).</summary>
    public long DataEnd { get; private set; }

    /// <summary>Logical offset of the inner superblock (after <see cref="Write"/>).</summary>
    public long MetadataBase { get; private set; }

    /// <summary>Logical mount size (after <see cref="Write"/>).</summary>
    public long MountSize { get; private set; }

    /// <summary>Metadata plaintext, <see cref="MountSize"/> − <see cref="MetadataBase"/> bytes (after <see cref="Write"/>).</summary>
    public byte[] Metadata { get; private set; } = [];

    /// <summary>
    /// Test hook: write PS5PkgTool's last gap fill block (a second half of 64 KiB plus the rest modulo 64 KiB, and
    /// 128 KiB for a single block) instead of the exact one, so parity tests can compare whole packages.
    /// </summary>
    internal bool EngineGapFill { get; set; }

    /// <summary>Inode count.</summary>
    public int InodeCount => 4 + Directories.Count + Files.Count;

    /// <summary>Plan the inner image of <paramref name="inputs"/>.</summary>
    /// <param name="inputs">Inner files (paths from the user root, ASCII).</param>
    /// <param name="seconds">Timestamp for every inode and the superblock.</param>
    /// <param name="nanoseconds">Nanosecond part of the timestamp.</param>
    /// <param name="compress">Kraken mode (see <see cref="Compress"/>).</param>
    /// <returns>Writer.</returns>
    public static PS5InnerWriter Plan(IReadOnlyList<FPKGInput> inputs, long seconds, uint nanoseconds = 0, bool compress = false)
    {
        // ---- afid order. ----
        static int Group(string path) => path switch
        {
            "sce_sys/keystone" => 0,
            _ when path.StartsWith("sce_sys/", StringComparison.Ordinal) => 1,
            _ => 2,
        };

        // Sizes and module magics take one file-system call or open per file; they run in parallel (open latency,
        // not bandwidth, is the cost with many small files), and the results do not depend on the order.
        bool[] executable = new bool[inputs.Count];
        Parallel.For(0, inputs.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 32) }, i =>
        {
            _ = inputs[i].Size;
            executable[i] = IsExecutable(inputs[i]);
        });
        Dictionary<FPKGInput, bool> isExecutable = new(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < inputs.Count; i++)
        {
            isExecutable[inputs[i]] = executable[i];
        }

        List<FPKGInput> ordered = [.. inputs.OrderBy(i => i.Size == 0).ThenBy(i => Group(i.Path)).ThenBy(i => i.Path, StringComparer.Ordinal)];

        // ---- Directories in pre-order (children ordinal), uroot first. ----
        HashSet<string> dirSet = [string.Empty];
        foreach (FPKGInput input in ordered)
        {
            for (int slash = input.Path.IndexOf('/'); slash >= 0; slash = input.Path.IndexOf('/', slash + 1))
            {
                dirSet.Add(input.Path[..slash]);
            }
        }

        // Each directory's children, ordinal, built once (a scan of every directory per directory is quadratic).
        Dictionary<string, List<string>> children = dirSet.Where(d => d.Length > 0).GroupBy(Parent, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(Name, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        IEnumerable<string> ChildDirs(string dir) => children.GetValueOrDefault(dir) ?? [];

        List<string> directories = [];
        void PreOrder(string dir)
        {
            directories.Add(dir);
            foreach (string child in ChildDirs(dir))
            {
                PreOrder(child);
            }
        }

        PreOrder(string.Empty);

        // ---- Inodes: files by directory in post-order. ----
        Dictionary<string, List<FPKGInput>> byDir = ordered.GroupBy(f => Parent(f.Path), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(f => Name(f.Path), StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        Dictionary<string, int> inodes = new(StringComparer.Ordinal);
        int next = 4 + directories.Count;
        void PostOrder(string dir)
        {
            foreach (string child in ChildDirs(dir))
            {
                PostOrder(child);
            }

            foreach (FPKGInput file in byDir.GetValueOrDefault(dir) ?? [])
            {
                inodes[file.Path] = next++;
            }
        }

        PostOrder(string.Empty);

        // ---- afids. Empty files follow the two −1 slots; logical offsets are assigned by Write. ----
        List<PS5InnerPlacement> files = [];
        int nonEmpty = ordered.Count(f => f.Size > 0);
        int empties = 0;
        foreach (FPKGInput input in ordered)
        {
            int afid = input.Size == 0 ? nonEmpty + 2 + empties++ : files.Count;
            files.Add(new PS5InnerPlacement(input, inodes[input.Path], afid, 0, input.Size, isExecutable[input]));
        }

        return new PS5InnerWriter(files, directories, seconds, nanoseconds, compress);
    }

    /// <summary>
    /// Write the stored <c>pfs_image.dat</c> (padded to 64 KiB) and return the matching
    /// <c>naps_pkg_layout.dat</c>.
    /// </summary>
    /// <param name="output">Destination, positioned at the start of the image.</param>
    /// <returns>naps and stored geometry.</returns>
    public PS5InnerResult Write(Stream output)
    {
        NapsBuilder naps = new();
        List<PS5InnerChunk> chunks = [];
        Dictionary<(long, long), byte[]> extents = [];
        SHA3256 extent = new();

        // A file's extent digest is taken on a background thread, behind the writes; reused buffers are copied.
        using SerialWorker hasher = new();
        void HashExtent(byte[] bytes) => hasher.Post(() => extent.Append(bytes));
        void HashExtentCopy(ReadOnlySpan<byte> bytes)
        {
            byte[] copy = System.Buffers.ArrayPool<byte>.Shared.Rent(bytes.Length);
            bytes.CopyTo(copy);
            int length = bytes.Length;
            hasher.Post(() =>
            {
                extent.Append(copy.AsSpan(0, length));
                System.Buffers.ArrayPool<byte>.Shared.Return(copy);
            });
        }

        long start = output.Position;
        long Stored() => output.Position - start;
        void AlignStored()
        {
            long pad = (BlockSize - (Stored() % BlockSize)) % BlockSize;
            output.Write(new byte[pad]);
        }

        // ---- File data: 128 KiB chunks from each file's start. A file joins the current run unless the cursor
        // is inside a 64 KiB block and the file is an executable or follows one, or (stored mode) would cross
        // the block; then it starts a new 64 KiB-aligned run. In Kraken mode every chunk of a non-executable
        // file but its last is compressed where that pays: a constant chunk as a fill block, else Kraken. Chunks
        // are read and encoded in batches that span files, so many small files keep every worker busy. ----
        using ChunkReader reader = new([.. _files], Compress, Fast, Workers);
        bool previousExecutable = false;
        bool first = true;
        long cursor = 0;
        for (int index = 0; index < Files.Count; index++)
        {
            PS5InnerPlacement file = Files[index];
            if (file.Size == 0)
            {
                FileWritten?.Invoke(file, []);
                continue;
            }

            long within = Stored() % BlockSize;
            bool run = first || (within != 0 && (file.Executable || previousExecutable || (!Compress && within + file.Size > BlockSize)));

            // At most 30 records per ublock (runs included): a file that would exceed it moves one ublock on.
            long ublock = cursor / UBlock;
            long startsHere = Math.Min(CeilDiv(file.Size, Half), ((((ublock + 1) * UBlock) - cursor - 1) / Half) + 1);
            if (naps.RecordsInUBlock(ublock) + (run ? 1 : 0) + startsHere > MaxRecordsPerUBlock)
            {
                cursor += UBlock;
                _movedFiles.Add(index);
            }

            file = file with { LogicalOffset = cursor };
            _files[index] = file;
            cursor += file.Size;
            if (run)
            {
                AlignStored();
                naps.Run(Stored());
                first = false;
            }

            previousExecutable = file.Executable;
            long extentStart = Stored();
            int firstChunk = chunks.Count;
            long chunkCount = CeilDiv(file.Size, Half);
            for (long c = 0; c < chunkCount; c++)
            {
                (ReadOnlyMemory<byte> memory, PS5KrakenChunk? packed) = reader.Next();
                ReadOnlySpan<byte> data = memory.Span;
                long logical = file.LogicalOffset + (c * Half);
                int length = data.Length;
                if (Compress && !file.Executable && c < chunkCount - 1 && !data.ContainsAnyExcept(data[0]))
                {
                    naps.FillChunk(logical);
                    chunks.Add(new PS5InnerChunk(PS5InnerChunkRole.File, logical, length, Stored(), 8, 8, 0, index));
                    byte[] fill = ZeroFill(length, data[0]);
                    HashExtent(fill);
                    output.Write(fill);
                }
                else if (packed is { } kraken)
                {
                    naps.KrakenChunk(logical, kraken);
                    chunks.Add(new PS5InnerChunk(PS5InnerChunkRole.File, logical, length, Stored(), kraken.Payload.Length, kraken.FirstLength, kraken.SecondLength, index));
                    HashExtent(kraken.Payload);
                    output.Write(kraken.Payload);
                }
                else
                {
                    naps.RawChunk(logical, length);
                    chunks.Add(new PS5InnerChunk(PS5InnerChunkRole.File, logical, length, Stored(), length, length, 0, index));
                    HashExtentCopy(data);
                    output.Write(data);
                }
            }

            (long, long) key = (extentStart, Stored() - extentStart);
            hasher.Post(() => extents[key] = extent.GetHashAndReset());
            FileWritten?.Invoke(file, chunks.GetRange(firstChunk, chunks.Count - firstChunk));
        }

        hasher.Drain();

        // ---- Geometry: empty files sit at the data end; the metadata follows 15 ublocks after it. ----
        DataEnd = cursor;
        for (int i = 0; i < _files.Count; i++)
        {
            if (_files[i].Size == 0)
            {
                _files[i] = _files[i] with { LogicalOffset = DataEnd };
            }
        }

        // Each hole takes an afid-table slot (−1) in front of the moved file; empty files follow the two pseudo-files.
        int slot = 0;
        for (int i = 0; i < _files.Count; i++)
        {
            if (_files[i].Size > 0)
            {
                slot += _movedFiles.Contains(i) ? 1 : 0;
                _files[i] = _files[i] with { Afid = slot++ };
            }
        }

        int emptySlot = slot + 2;
        for (int i = 0; i < _files.Count; i++)
        {
            if (_files[i].Size == 0)
            {
                _files[i] = _files[i] with { Afid = emptySlot++ };
            }
        }

        MetadataBase = (CeilDiv(DataEnd, UBlock) + 15) * UBlock;
        Metadata = BuildMetadata();

        // ---- Zero gap: 256 KiB chunks from the data end. Every whole chunk re-anchors on one shared 16-byte
        // pair of 128 KiB fill blocks; the last partial chunk follows in its own fill bytes. ----
        AlignStored();
        long gap = Stored();
        long whole = (MetadataBase - DataEnd) / UBlock;
        for (long i = 0; i < whole; i++)
        {
            naps.Run(gap);
            naps.ZeroChunk(DataEnd + (i * UBlock));
            chunks.Add(new PS5InnerChunk(PS5InnerChunkRole.Gap, DataEnd + (i * UBlock), UBlock, gap, 16, 8, 8, -1));
        }

        output.Write(ZeroFill(Half));
        output.Write(ZeroFill(Half));
        int rest = (int)((MetadataBase - DataEnd) % UBlock);
        if (rest > Half)
        {
            naps.ZeroChunk(DataEnd + (whole * UBlock));
            chunks.Add(new PS5InnerChunk(PS5InnerChunkRole.Gap, DataEnd + (whole * UBlock), rest, Stored(), 16, 8, 8, -1));
            output.Write(ZeroFill(Half));
            output.Write(ZeroFill(EngineGapFill ? BlockSize + ((rest - 1) % BlockSize) + 1 : rest - Half));
        }
        else if (rest > 0)
        {
            // PS5PkgTool writes 128 KiB and the rest here but records one 8-byte block, so a decoder sees
            // 128 KiB of zeros for a shorter chunk; MkPFS's first block has the exact length.
            naps.FillChunk(DataEnd + (whole * UBlock));
            // Both 8-byte blocks are stored (the second unused), and naps_meta_18 lists them like a two-part chunk.
            chunks.Add(new PS5InnerChunk(PS5InnerChunkRole.Gap, DataEnd + (whole * UBlock), rest, Stored(), 16, 8, 8, -1));
            output.Write(ZeroFill(EngineGapFill ? Half : rest));
            output.Write(ZeroFill(rest));
        }

        // ---- Metadata: a PFSC container header, then 256 KiB chunks (Kraken where it pays in Kraken mode). ----
        AlignStored();
        List<PS5KrakenChunk?> metaChunks = [];
        for (long l = MetadataBase; l < MountSize; l += UBlock)
        {
            ReadOnlySpan<byte> data = Metadata.AsSpan((int)(l - MetadataBase), (int)Math.Min(UBlock, MountSize - l));
            metaChunks.Add(Compress && PS5KrakenChunk.TryEncode(data, Fast, out PS5KrakenChunk? kraken) ? kraken : null);
        }

        output.Write(MetadataHeader(Metadata, metaChunks));
        naps.Run(Stored());
        for (int i = 0; i < metaChunks.Count; i++)
        {
            long l = MetadataBase + ((long)i * UBlock);
            int length = (int)Math.Min(UBlock, MountSize - l);
            if (metaChunks[i] is { } kraken)
            {
                naps.KrakenChunk(l, kraken);
                chunks.Add(new PS5InnerChunk(PS5InnerChunkRole.Metadata, l, length, Stored(), kraken.Payload.Length, kraken.FirstLength, kraken.SecondLength, -1));
                extents[(Stored(), kraken.Payload.Length)] = SHA3256.HashData(kraken.Payload);
                output.Write(kraken.Payload);
            }
            else
            {
                naps.RawChunk(l, length);
                chunks.Add(new PS5InnerChunk(PS5InnerChunkRole.Metadata, l, length, Stored(), length, length, 0, -1));
                extents[(Stored(), length)] = SHA3256.HashData(Metadata.AsSpan((int)(l - MetadataBase), length));
                output.Write(Metadata, (int)(l - MetadataBase), length);
            }
        }

        naps.Terminate(MountSize);
        List<long> fidx = [.. Files.Where(f => f.Size > 0).Select(f => f.LogicalOffset), DataEnd, MetadataBase, MountSize];
        return new PS5InnerResult(naps.Build(fidx, MountSize, CeilDiv(Stored(), BlockSize)), gap, Stored(), chunks, extents);
    }

    // PS5PkgTool puts LibProsperoPkg's PFSC container header of the metadata in front of it, with the id=5
    // signature section left zero. The header is not part of the mount; the naps skips it. A Kraken block's
    // boundary entry carries its compressed offset, the two sub-chunk kinds as flags (no restart bit, unlike
    // LibProsperoPkg) and the first sub-chunk length − 1; the file digest at 0x28 covers the boundary table.
    private static byte[] MetadataHeader(byte[] metadata, List<PS5KrakenChunk?> blocks)
    {
        byte[] container = ProsperoCompressedPfsFileWriter.WriteStored(metadata);
        int Section(int index, int field) => (int)BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(PFSCDirectory + (index * 16) + field));
        int dataOffset = Section(6, 2);
        byte[] header = container[..dataOffset];
        header.AsSpan(Section(4, 2), Section(4, 10)).Clear();
        if (blocks.All(b => b is null))
        {
            return header;
        }

        Span<byte> boundaries = header.AsSpan(Section(2, 2), Section(2, 10));
        ulong compressed = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            Span<byte> entry = boundaries[(16 * i)..];
            ulong uncompressed = BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]) & ((1UL << 44) - 1);
            if (blocks[i] is { } kraken)
            {
                ulong flags = (ulong)(kraken.Kind | (kraken.SecondKind << 4));
                BinaryPrimitives.WriteUInt64LittleEndian(entry, compressed | (flags << 48));
                BinaryPrimitives.WriteUInt64LittleEndian(entry[8..], uncompressed | ((ulong)(kraken.FirstLength - 1) << 44));
                compressed += (ulong)kraken.Payload.Length;
            }
            else
            {
                ulong flagged = BinaryPrimitives.ReadUInt64LittleEndian(entry) & ~((1UL << 44) - 1);
                BinaryPrimitives.WriteUInt64LittleEndian(entry, compressed | flagged);
                compressed += (ulong)Math.Min(UBlock, metadata.Length - ((long)i * UBlock));
            }
        }

        BinaryPrimitives.WriteUInt64LittleEndian(boundaries[(16 * blocks.Count)..], compressed);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(PFSCDirectory + (6 * 16) + 10), (uint)compressed);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x20), (ulong)dataOffset + compressed);
        ProsperoPfsDigest.ComputeFileDigest(
            header.AsSpan(0x08, ProsperoPfsDigest.FileDigestHeaderParamsLength),
            header.AsSpan(Section(1, 2), Section(1, 10)),
            header.AsSpan(Section(2, 2), Section(2, 10)),
            header.AsSpan(Section(3, 2), Section(3, 10))).CopyTo(header.AsSpan(0x28));
        return header;
    }

    private const int PFSCDirectory = 0x48;

    private static string Parent(string path) => path.LastIndexOf('/') is var i and >= 0 ? path[..i] : string.Empty;

    private static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static long CeilDiv(long value, long divisor) => (value + divisor - 1) / divisor;

    private static bool IsExecutable(FPKGInput input)
    {
        if (input.Size < 4)
        {
            return false;
        }

        Span<byte> head = stackalloc byte[4];
        using Stream stream = input.Open();
        stream.ReadExactly(head);
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(head);
        return magic is SELFFile.Magic or 0x1D3D154F or 0x464C457F;
    }

    // ---- Metadata ----------------------------------------------------------------------------------

    private byte[] BuildMetadata()
    {
        Dictionary<string, int> dirInodes = Directories.Select((d, i) => (d, i)).ToDictionary(t => t.d, t => 4 + t.i, StringComparer.Ordinal);
        Dictionary<string, List<string>> subdirs = Directories.Where(d => d.Length > 0).GroupBy(Parent, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(Name, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        Dictionary<string, List<PS5InnerPlacement>> dirFiles = Files.GroupBy(f => Parent(f.Path), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(f => Name(f.Path), StringComparer.Ordinal).ToList(), StringComparer.Ordinal);

        // ---- Table contents, then the block plan. ----
        List<List<(int Inode, int Type, string Name)>> dirents = [];
        foreach (string dir in Directories)
        {
            List<(int Inode, int Type, string Name)> entries =
            [
                (dirInodes[dir], PFSDirentDot, "."),
                (dir.Length == 0 ? dirInodes[dir] : dirInodes[Parent(dir)], PFSDirentDotDot, ".."),
            ];
            entries.AddRange((subdirs.GetValueOrDefault(dir) ?? []).Select(d => (dirInodes[d], PFSDirentDir, Name(d))));
            entries.AddRange((dirFiles.GetValueOrDefault(dir) ?? []).Select(f => (f.Inode, PFSDirentFile, Name(f.Path))));
            dirents.Add(entries);
        }

        // Flat-path tables: every directory below uroot and every file; apr = files outside sce_sys. Bit 30 marks a
        // directory, bit 31 the sce_sys subtree.
        List<(ulong Hash, ulong Packed)> inodeFlt = [];
        foreach (string dir in Directories.Where(d => d.Length > 0))
        {
            bool sys = IsSceSys(dir);
            inodeFlt.Add((PS5PathHash.HashPath("/" + dir), (uint)dirInodes[dir] | (1UL << 30) | (sys ? 1UL << 31 : 0) | (0xFFFFFFUL << 40)));
        }

        List<(ulong Hash, ulong Packed)> aprFlt = [];
        foreach (PS5InnerPlacement file in Files)
        {
            bool sys = IsSceSys(file.Path);
            inodeFlt.Add((PS5PathHash.HashPath("/" + file.Path), (uint)file.Inode | (sys ? 1UL << 31 : 0) | ((ulong)(uint)file.Afid << 40)));
            if (!sys)
            {
                aprFlt.Add((PS5PathHash.HashPath("/" + file.Path), ((ulong)file.Size & 0xFF_FFFF_FFFF) | ((ulong)(uint)file.Afid << 40)));
            }
        }

        // afid table: slot count, inode per afid (−1 for a hole), −1 for the data-end and metadata pseudo-files.
        List<int> afids = [];
        for (int i = 0; i < _files.Count; i++)
        {
            if (_files[i].Size > 0)
            {
                if (_movedFiles.Contains(i))
                {
                    afids.Add(-1);
                }

                afids.Add(_files[i].Inode);
            }
        }

        afids.AddRange([-1, -1]);
        int inodeFltSize = FltSize(inodeFlt.Count);
        int aprFltSize = FltSize(aprFlt.Count);
        int afidSize = 4 * (afids.Count + 1);
        int[] direntSizes = [.. dirents.Select(e => e.Sum(x => DirentSize(x.Name)))];

        int Blocks(long bytes) => (int)Math.Max(1, CeilDiv(bytes, BlockSize));
        int inodeBlocks = Blocks((long)InodeCount * BlockSize / InodesPerBlock);
        int superRootBlock = 1 + inodeBlocks;
        int inodeFltBlock = superRootBlock + 1;
        int aprFltBlock = inodeFltBlock + Blocks(inodeFltSize);
        int afidBlock = aprFltBlock + Blocks(aprFltSize);
        int[] dirBlock = new int[Directories.Count];
        int nextBlock = afidBlock + Blocks(afidSize);
        for (int i = 0; i < Directories.Count; i++)
        {
            dirBlock[i] = nextBlock;
            nextBlock += Blocks(direntSizes[i]);
        }

        MountSize = MetadataBase + ((long)(nextBlock + (nextBlock & 1)) * BlockSize);
        byte[] meta = new byte[MountSize - MetadataBase];
        Span<byte> Region(int block, int blocks) => meta.AsSpan(block * BlockSize, blocks * BlockSize);
        long Logical(int block) => MetadataBase + ((long)block * BlockSize);

        // ---- Write the tables. Dirent offsets are recorded in the child's inode. ----
        Dictionary<string, int> direntOffset = new(StringComparer.Ordinal);
        for (int i = 0; i < Directories.Count; i++)
        {
            string dir = Directories[i];
            int offset = WriteDirents(Region(dirBlock[i], Blocks(direntSizes[i])), dirents[i]);
            foreach ((string name, int at) in DirentOffsets(dirents[i]))
            {
                direntOffset[dir.Length == 0 ? name : dir + "/" + name] = at;
            }

            _ = offset;
        }

        WriteDirents(Region(superRootBlock, 1),
        [
            (1, PFSDirentFile, "inode_flat_path_table"),
            (2, PFSDirentFile, "apr_flat_path_table"),
            (3, PFSDirentFile, "afid_to_ino_table"),
            (4, PFSDirentDir, "uroot"),
        ]);
        WriteFlt(Region(inodeFltBlock, Blocks(inodeFltSize)), inodeFlt);
        WriteFlt(Region(aprFltBlock, Blocks(aprFltSize)), aprFlt);
        Span<byte> afidTable = Region(afidBlock, Blocks(afidSize));
        BinaryPrimitives.WriteInt32LittleEndian(afidTable, afids.Count);
        for (int i = 0; i < afids.Count; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(afidTable[(4 * (i + 1))..], afids[i]);
        }

        // Inode table: 390 inodes per block, none crossing a block.
        Span<byte> table = Region(1, inodeBlocks);
        WriteInode(table, 0, 0x416D, 1, 0x20010, BlockSize, Logical(superRootBlock), -1, -1, -1);
        WriteInode(table, 1, 0x816D, 1, 0x20010, inodeFltSize, Logical(inodeFltBlock), -1, -1, -1);
        WriteInode(table, 2, 0x816D, 1, 0x20010, aprFltSize, Logical(aprFltBlock), -1, -1, -1);
        WriteInode(table, 3, 0x816D, 1, 0x20010, afidSize, Logical(afidBlock), -1, -1, -1);
        for (int i = 0; i < Directories.Count; i++)
        {
            string dir = Directories[i];
            int children = subdirs.GetValueOrDefault(dir)?.Count ?? 0;
            long size = (long)Blocks(direntSizes[i]) * BlockSize;
            bool sys = IsSceSys(dir);
            if (dir.Length == 0)
            {
                WriteInode(table, 4, 0x416D, (ushort)(3 + children), 0x10, size, Logical(dirBlock[i]), -1, -1, -1);
            }
            else
            {
                WriteInode(table, dirInodes[dir], sys ? (ushort)0x4168 : (ushort)0x416D, (ushort)(2 + children), sys ? 0x20010u : 0x10u,
                    size, Logical(dirBlock[i]), -1, dirInodes[Parent(dir)], direntOffset[dir]);
            }
        }

        foreach (PS5InnerPlacement file in Files)
        {
            bool sys = IsSceSys(file.Path);
            uint flags = 0x10u | (file.Executable ? 0x40u : 0x20u) | (sys ? 0x20000u : 0u);
            WriteInode(table, file.Inode, sys ? (ushort)0x8168 : (ushort)0x816D, 1, flags, file.Size, file.LogicalOffset, file.Afid, dirInodes[Parent(file.Path)], direntOffset[file.Path]);
        }

        WriteSuperblock(meta, InodeCount, MountSize / BlockSize, inodeBlocks, (MetadataBase / BlockSize) + 1);
        return meta;
    }

    private const int PFSDirentFile = 2;
    private const int PFSDirentDir = 3;
    private const int PFSDirentDot = 4;
    private const int PFSDirentDotDot = 5;

    private static bool IsSceSys(string path) => path == "sce_sys" || path.StartsWith("sce_sys/", StringComparison.Ordinal);

    private static int DirentSize(string name) => (16 + Encoding.ASCII.GetByteCount(name) + 1 + 7) & ~7;

    private static IEnumerable<(string Name, int Offset)> DirentOffsets(List<(int Inode, int Type, string Name)> entries)
    {
        int offset = 0;
        foreach ((int _, int _, string name) in entries)
        {
            yield return (name, offset);
            offset += DirentSize(name);
        }
    }

    private static int WriteDirents(Span<byte> block, List<(int Inode, int Type, string Name)> entries)
    {
        int offset = 0;
        foreach ((int inode, int type, string name) in entries)
        {
            int size = DirentSize(name);

            BinaryPrimitives.WriteInt32LittleEndian(block[offset..], inode);
            BinaryPrimitives.WriteInt32LittleEndian(block[(offset + 4)..], type);
            BinaryPrimitives.WriteInt32LittleEndian(block[(offset + 8)..], name.Length);
            BinaryPrimitives.WriteInt32LittleEndian(block[(offset + 12)..], size);
            Encoding.ASCII.GetBytes(name, block[(offset + 16)..]);
            offset += size;
        }

        return offset;
    }

    private static int FltSize(int entries) => 0x40 + (16 * entries);

    private static int WriteFlt(Span<byte> block, List<(ulong Hash, ulong Packed)> entries)
    {
        entries.Sort((a, b) => a.Hash.CompareTo(b.Hash));
        int size = FltSize(entries.Count);

        BinaryPrimitives.WriteUInt32LittleEndian(block, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(block[0x04..], 0x10);
        BinaryPrimitives.WriteUInt32LittleEndian(block[0x08..], 0x40);
        block[0x20] = 0x7F;
        "FLT"u8.CopyTo(block[0x21..]);
        BinaryPrimitives.WriteInt32LittleEndian(block[0x2C..], entries.Count);
        BinaryPrimitives.WriteUInt64LittleEndian(block[0x30..], PS5PathHash.Seed0);
        BinaryPrimitives.WriteUInt64LittleEndian(block[0x38..], PS5PathHash.Seed1);
        for (int i = 0; i < entries.Count; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(block[(0x40 + (16 * i))..], entries[i].Hash);
            BinaryPrimitives.WriteUInt64LittleEndian(block[(0x48 + (16 * i))..], entries[i].Packed);
        }

        return size;
    }

    private void WriteInode(Span<byte> table, int inode, ushort mode, ushort nlink, uint flags, long size, long logical, int afid, int parent, int direntOffset)
    {
        Span<byte> e = table.Slice(((inode / InodesPerBlock) * BlockSize) + ((inode % InodesPerBlock) * InodeSize), InodeSize);
        BinaryPrimitives.WriteUInt16LittleEndian(e, mode);
        BinaryPrimitives.WriteUInt16LittleEndian(e[0x02..], nlink);
        BinaryPrimitives.WriteUInt32LittleEndian(e[0x04..], flags);
        BinaryPrimitives.WriteInt64LittleEndian(e[0x08..], size);
        BinaryPrimitives.WriteInt64LittleEndian(e[0x10..], size);
        WriteTimes(e[0x18..]);
        BinaryPrimitives.WriteInt64LittleEndian(e[0x60..], logical);
        BinaryPrimitives.WriteInt32LittleEndian(e[0x68..], afid);
        BinaryPrimitives.WriteInt32LittleEndian(e[0x6C..], parent);
        BinaryPrimitives.WriteInt32LittleEndian(e[0x70..], direntOffset);
    }

    // Four seconds fields then four nanosecond fields.
    private void WriteTimes(Span<byte> at)
    {
        for (int t = 0; t < 4; t++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(at[(8 * t)..], _seconds);
            BinaryPrimitives.WriteUInt32LittleEndian(at[(0x20 + (4 * t))..], _nanoseconds);
        }
    }

    private void WriteSuperblock(Span<byte> sb, int inodes, long ndblock, int inodeBlocks, long inodeTableBlock)
    {
        BinaryPrimitives.WriteInt64LittleEndian(sb, 2);
        BinaryPrimitives.WriteInt64LittleEndian(sb[0x08..], 20130315);
        sb[0x1A] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(sb[0x1C..], 0x18);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x20..], BlockSize);
        BinaryPrimitives.WriteInt64LittleEndian(sb[0x28..], 1);
        BinaryPrimitives.WriteInt64LittleEndian(sb[0x30..], inodes);
        BinaryPrimitives.WriteInt64LittleEndian(sb[0x38..], ndblock);
        BinaryPrimitives.WriteInt64LittleEndian(sb[0x40..], inodeBlocks);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x50..], BlockSize);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x54..], 0x10);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x58..], BlockSize);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x60..], BlockSize);
        WriteTimes(sb[0x68..]);
        BinaryPrimitives.WriteInt64LittleEndian(sb[0xB0..], 1);
        BinaryPrimitives.WriteInt64LittleEndian(sb[0xD8..], inodeTableBlock);
        sb[0x368] = 1;
    }

    // ---- naps ----------------------------------------------------------------------------------------

    /// <summary>Accumulates CblockInfo records while the stored image is written.</summary>
    private sealed class NapsBuilder
    {
        private readonly List<(ulong Lo, byte Hi)> _records = [];
        private readonly List<(long Logical, int Index)> _starts = [];
        private long _cursor;
        private long _runC256K;
        private bool _compressed;
        private long _lastUBlock = -1;
        private int _ublockFirst;

        // Records (runs included) from the first record of `ublock` on, 0 when it has none yet.
        public int RecordsInUBlock(long ublock) => ublock == _lastUBlock ? _records.Count - _ublockFirst : 0;

        public void Run(long stored)
        {
            _runC256K = 2 * (stored / UBlock);
            ulong lo = ((ulong)_cursor & 0x3FFFF) | (1UL << 18) | ((ulong)(stored >> 15) << 19) | ((ulong)(_runC256K & 0x7FFF) << 49);
            _records.Add((lo, (byte)(_runC256K >> 15)));
            _cursor = stored;
        }

        // Raw: ≤ 64 KiB kind 0 / even 0 / len−1; ≤ 128 KiB kind 0 / even 1 / len−0x10001; above, kind 4 / even 1 / 0xFFFF.
        public void RawChunk(long logical, int length)
        {
            (int kde, bool even, uint field) = length switch
            {
                <= 0x10000 => (0, false, (uint)(length - 1)),
                <= 0x20000 => (0, true, (uint)(length - 0x10001)),
                _ => (4, true, 0xFFFFu),
            };
            Std(logical, kde, even, odd: true, field, length, high: RawFlag);
        }

        // Kraken: kind 2/3 per sub-chunk, odd, the first sub-chunk's stored length − 1.
        public void KrakenChunk(long logical, PS5KrakenChunk chunk)
        {
            _compressed = true;
            Std(logical, chunk.Kind, even: false, odd: true, (uint)(chunk.FirstLength - 1), chunk.Payload.Length, secondKde: chunk.SecondKind);
        }

        public void ZeroChunk(long logical) => Std(logical, kde: 4, even: false, odd: true, field: 7, streamLength: 16);

        // One fill block (a chunk of at most 128 KiB): kind 0 with the stored length, like a constant-filled file.
        public void FillChunk(long logical) => Std(logical, kde: 0, even: false, odd: true, field: 7, streamLength: 8);

        public void Terminate(long mount)
        {
            // Run base with a zero tweak at the end cursor, then a record whose odd uoff marks the mount end.
            long end = 2 * (_cursor / UBlock);
            ulong lo = ((ulong)_cursor & 0x3FFFF) | (1UL << 18) | ((ulong)(end & 0x7FFF) << 49);
            _records.Add((lo, (byte)(end >> 15)));
            ulong uoff = ((ulong)(2 * (mount % UBlock)) | 1) & 0x7FFFF;
            _records.Add((((ulong)_cursor & 0x3FFFF) | (uoff << 19), 0));
        }

        public byte[] Build(List<long> fidx, long mount, long storedBlocks)
        {
            int ublocks = (int)CeilDiv(mount, UBlock);
            int groups = (ublocks + 8) >> 3;
            int terminator = _records.Count - 1;
            int[] first = new int[ublocks];
            int s = 0;
            for (int u = 0; u < ublocks; u++)
            {
                while (s < _starts.Count && _starts[s].Logical < (long)u * UBlock)
                {
                    s++;
                }

                first[u] = s < _starts.Count ? _starts[s].Index : terminator;
            }

            using MemoryStream blob = new();
            Span<byte> word = stackalloc byte[8];
            // 2 << 24 in the first word marks an image with Kraken chunks.
            ulong w0 = (uint)(fidx.Count - 1) | (_compressed ? 2UL << 24 : 0) | ((ulong)(uint)ublocks << 32);
            ulong w1 = (ulong)storedBlocks | ((ulong)(_records.Count - 2) << 24);
            BinaryPrimitives.WriteUInt64LittleEndian(word, w0);
            blob.Write(word);
            BinaryPrimitives.WriteUInt64LittleEndian(word, w1);
            blob.Write(word);
            blob.Write(new byte[8 * storedBlocks]);
            for (int i = 0; i < fidx.Count; i++)
            {
                for (int k = 0; k < 5; k++)
                {
                    blob.WriteByte((byte)(fidx[i] >> (8 * k)));
                }

                blob.WriteByte(i == fidx.Count - 1 ? (byte)0x40 : (byte)0);
            }

            for (int g = 0; g < groups; g++)
            {
                int baseIndex = 8 * g < ublocks ? first[8 * g] : terminator;
                blob.WriteByte((byte)baseIndex);
                blob.WriteByte((byte)(baseIndex >> 8));
                blob.WriteByte((byte)(baseIndex >> 16));
                for (int k = 1; k < 8; k++)
                {
                    int u = (8 * g) + k;
                    int delta = (u < ublocks ? first[u] : terminator) - baseIndex;
                    if (delta is < 0 or > 255)
                    {
                        throw new InvalidDataException($"naps u2c delta {delta} does not fit a byte");
                    }

                    blob.WriteByte((byte)delta);
                }
            }

            while (blob.Length % 8 != 0)
            {
                blob.WriteByte(0);
            }

            foreach ((ulong lo, byte hi) in _records)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(word, lo);
                blob.Write(word);
                blob.WriteByte(hi);
            }

            while (blob.Length % 16 != 0)
            {
                blob.WriteByte(0);
            }

            return blob.ToArray();
        }

        // PS5PkgTool sets bit 3 of the ninth record byte on raw chunks (fill chunks leave it clear).
        private const byte RawFlag = 0x08;

        private void Std(long logical, int kde, bool even, bool odd, uint field, long streamLength, byte high = 0, int secondKde = 0)
        {
            if (logical / UBlock != _lastUBlock)
            {
                _lastUBlock = logical / UBlock;
                _ublockFirst = _records.Count;
            }

            ulong uoff = (ulong)(2 * (logical % UBlock)) & 0x7FFFF;
            ulong lo = ((ulong)_cursor & 0x3FFFF) | (uoff << 19) | ((ulong)field << 38) | ((even ? 1UL : 0) << 54) | ((odd ? 1UL : 0) << 55)
                | ((ulong)kde << 56) | ((ulong)secondKde << 59);
            _starts.Add((logical, _records.Count));
            _records.Add((lo, high));
            _cursor += streamLength;
        }
    }

    // Yields every 128 KiB chunk of the non-empty files in order, with its Kraken encoding when it is a candidate
    // (Kraken mode, non-executable, not the file's last chunk, not constant). Batches are read across file
    // boundaries and encoded in parallel, one batch ahead of the consumer on a background task; a chunk's memory
    // stays valid until the consumer moves past its batch, and only then is that batch refilled.
    private sealed class ChunkReader : IDisposable
    {
        private readonly List<PS5InnerPlacement> _files;
        private readonly bool _compress;
        private readonly bool _fast;
        private readonly int _workers;
        private readonly Batch[] _batches;
        private Batch? _current;
        private int _taken;
        private int _next;
        private Task<Batch> _pending;
        private int _file = -1;
        private long _chunk;
        private long _chunkCount;
        private Stream? _source;

        public ChunkReader(List<PS5InnerPlacement> files, bool compress, bool fast, int workers)
        {
            _files = files;
            _compress = compress;
            _fast = fast;
            _workers = workers;

            // Four chunks per worker balance the parallel encode; stored mode reads 8 MiB at a time.
            int size = compress ? 4 * workers : 64;
            _batches = [new Batch(size), new Batch(size)];
            _pending = Task.Run(() => Fill(_batches[0]));
        }

        public (ReadOnlyMemory<byte> Data, PS5KrakenChunk? Packed) Next()
        {
            if (_current is null || _taken == _current.Count)
            {
                _current = _pending.GetAwaiter().GetResult();
                if (_current.Count == 0)
                {
                    throw new InvalidOperationException("No chunk left to read.");
                }

                _taken = 0;
                _next ^= 1;
                Batch free = _batches[_next];
                _pending = Task.Run(() => Fill(free));
            }

            int k = _taken++;
            return (_current.Buffers[k].AsMemory(0, _current.Lengths[k]), _current.Packed[k]);
        }

        public void Dispose()
        {
            try
            {
                _pending.Wait();
            }
            catch (AggregateException)
            {
                // The consumer already failed or stopped; the read-ahead's own error does not matter.
            }

            _source?.Dispose();
        }

        private Batch Fill(Batch batch)
        {
            batch.Count = 0;
            while (batch.Count < batch.Buffers.Length)
            {
                if (_chunk == _chunkCount && !OpenNextFile())
                {
                    break;
                }

                PS5InnerPlacement file = _files[_file];
                int k = batch.Count;
                int length = (int)Math.Min(Half, file.Size - (_chunk * Half));
                byte[] buffer = batch.Buffers[k] ??= new byte[Half];
                _source!.ReadExactly(buffer.AsSpan(0, length));
                batch.Lengths[k] = length;
                batch.Candidates[k] = _compress && !file.Executable && _chunk < _chunkCount - 1;
                batch.Packed[k] = null;
                batch.Count++;
                _chunk++;
            }

            if (_compress && batch.Count > 0)
            {
                bool fast = _fast;
                Parallel.For(0, batch.Count, new ParallelOptions { MaxDegreeOfParallelism = _workers }, k =>
                {
                    ReadOnlySpan<byte> data = batch.Buffers[k].AsSpan(0, batch.Lengths[k]);
                    if (batch.Candidates[k] && data.ContainsAnyExcept(data[0]) && PS5KrakenChunk.TryEncode(data, fast, out PS5KrakenChunk? kraken))
                    {
                        batch.Packed[k] = kraken;
                    }
                });
            }

            return batch;
        }

        private bool OpenNextFile()
        {
            _source?.Dispose();
            _source = null;
            do
            {
                _file++;
            }
            while (_file < _files.Count && _files[_file].Size == 0);
            if (_file >= _files.Count)
            {
                return false;
            }

            _source = _files[_file].Input.Open();
            _chunk = 0;
            _chunkCount = CeilDiv(_files[_file].Size, Half);
            return true;
        }

        private sealed class Batch(int size)
        {
            public byte[][] Buffers { get; } = new byte[size][];

            public int[] Lengths { get; } = new int[size];

            public bool[] Candidates { get; } = new bool[size];

            public PS5KrakenChunk?[] Packed { get; } = new PS5KrakenChunk?[size];

            public int Count { get; set; }
        }
    }
}
