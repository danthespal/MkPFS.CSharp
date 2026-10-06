using System.Buffers.Binary;
using MkPFS.Core.Compression.Kraken;
using MkPFS.Core.PKG;

namespace MkPFS.Core.PFS.PS5;

/// <summary>How a chunk of the inner mount is stored.</summary>
public enum PS5ChunkKind
{
    /// <summary>Stored bytes.</summary>
    Raw,

    /// <summary>
    /// One repeated byte: 8-byte Kraken fill blocks (3-byte header (4 &lt;&lt; 19) | (length − 1) &lt;&lt; 2, <c>00 03 00</c>,
    /// big-endian 0x4000 | value &lt;&lt; 6). Kind 4 with even 0: two blocks of 128 KiB (the zero gap, shared between
    /// chunks); kind 0 with a stored length below the chunk length: one block (a constant-filled file).
    /// </summary>
    Fill,

    /// <summary>Header-stripped Kraken newLZ block (one or two 128 KiB sub-chunks).</summary>
    Kraken,
}

/// <summary>One chunk of the inner mount and where its bytes are stored.</summary>
/// <param name="CblockIndex">Index of the standard CblockInfo record.</param>
/// <param name="LogicalOffset">Start in the inner mount.</param>
/// <param name="Length">Uncompressed length (at most 256 KiB).</param>
/// <param name="StoredOffset">Offset in <c>pfs_image.dat</c>.</param>
/// <param name="StoredLength">Stored length.</param>
/// <param name="FirstSubChunkLength">Kraken: stored length of the first 128 KiB sub-chunk.</param>
/// <param name="Kind">Storage kind.</param>
/// <param name="Kde">The record's kind field (Kraken: bit 0 = sub literals in the first sub-chunk).</param>
/// <param name="SecondKde">Kraken: kind of the second sub-chunk (bit 0 = sub literals), 0 when there is none.</param>
public readonly record struct PS5Chunk(int CblockIndex, long LogicalOffset, int Length, long StoredOffset, int StoredLength, int FirstSubChunkLength, PS5ChunkKind Kind, int Kde = 0, int SecondKde = 0);

/// <summary>A file of the inner image.</summary>
/// <param name="Path">Path from the user root (for example <c>sce_sys/keystone</c>).</param>
/// <param name="Inode">Inode number.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="LogicalOffset">Start in the inner mount.</param>
/// <param name="Mode">Inode mode.</param>
/// <param name="Flags">Inode flags.</param>
/// <param name="Afid">File id.</param>
public sealed record PS5InnerFile(string Path, long Inode, long Size, long LogicalOffset, ushort Mode, uint Flags, int Afid);

/// <summary>
/// The inner <c>pfs_image.dat</c> of a PS5 package, read through <c>naps_pkg_layout.dat</c>. The layout
/// splits every file (and the zero gap before the metadata) into chunks of at most 256 KiB, counted from
/// the file's own start; the CblockInfo walk gives each chunk's stored position and kind. The metadata
/// (superblock, 0xA8-byte inodes with the logical offset at 0x60, dirents) sits at the second-to-last
/// fidx offset. Rules verified against Publishing Tools output (<c>tools/oracle-fpkg/build_sdk_refs.py</c>).
/// </summary>
public sealed class PS5InnerImage
{
    private const int InodeSize = 0xA8;
    private const int SubChunkSize = 0x20000;
    private const int KrakenFlagsNewLzBoth = 0x22;
    private const int FillBlockSize = 8;

    private readonly Stream _stored;
    private readonly long[] _starts;
    private int _cachedChunk = -1;
    private byte[] _cached = [];

    private PS5InnerImage(Stream stored, NAPSLayout layout, List<PS5Chunk> chunks, List<string> problems)
    {
        _stored = stored;
        Layout = layout;
        Chunks = chunks;
        Problems = problems;
        _starts = chunks.ConvertAll(c => c.LogicalOffset).ToArray();
    }

    /// <summary>The layout.</summary>
    public NAPSLayout Layout { get; }

    /// <summary>Chunks in logical order.</summary>
    public IReadOnlyList<PS5Chunk> Chunks { get; }

    /// <summary>Layout inconsistencies found while mapping (empty for a conforming image).</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>Inner superblock, once <see cref="ReadTree"/> has run.</summary>
    public PFSHeader? Superblock { get; private set; }

    /// <summary>Map the inner image.</summary>
    /// <param name="stored">Seekable <c>pfs_image.dat</c> stream (kept open, not owned).</param>
    /// <param name="layout">Its naps layout.</param>
    /// <returns>Inner image.</returns>
    /// <exception cref="InvalidDataException">The layout does not describe a readable mount.</exception>
    public static PS5InnerImage Open(Stream stored, NAPSLayout layout)
    {
        List<string> problems = [];
        List<PS5Chunk> chunks = BuildChunks(layout, stored.Length, problems);
        CheckU2c(layout, chunks, problems);
        return new PS5InnerImage(stored, layout, chunks, problems);
    }

    /// <summary>Read <paramref name="buffer"/>.Length bytes of the inner mount at <paramref name="offset"/>.</summary>
    /// <param name="offset">Logical offset.</param>
    /// <param name="buffer">Destination.</param>
    public void Read(long offset, Span<byte> buffer)
    {
        while (buffer.Length > 0)
        {
            int index = Array.BinarySearch(_starts, offset);
            if (index < 0)
            {
                index = ~index - 1;
            }

            if (index < 0 || offset >= Chunks[index].LogicalOffset + Chunks[index].Length)
            {
                throw new InvalidDataException($"inner mount offset 0x{offset:X} is not covered by the layout");
            }

            byte[] data = DecodeChunk(index);
            int inChunk = (int)(offset - Chunks[index].LogicalOffset);
            int n = Math.Min(buffer.Length, data.Length - inChunk);
            data.AsSpan(inChunk, n).CopyTo(buffer);
            buffer = buffer[n..];
            offset += n;
        }
    }

    /// <summary>Copy a file's bytes to <paramref name="destination"/>.</summary>
    /// <param name="file">File.</param>
    /// <param name="destination">Output stream.</param>
    public void CopyFile(PS5InnerFile file, Stream destination)
    {
        byte[] buffer = new byte[NAPSLayout.UBlockSize];
        for (long done = 0; done < file.Size;)
        {
            int n = (int)Math.Min(buffer.Length, file.Size - done);
            Read(file.LogicalOffset + done, buffer.AsSpan(0, n));
            destination.Write(buffer, 0, n);
            done += n;
        }
    }

    /// <summary>
    /// Copy a file's bytes to <paramref name="destination"/>, decoding through <paramref name="stored"/> (a stream of
    /// the caller's own, from <c>PS5Package.OpenInnerStream</c>), so several files can be copied at once.
    /// </summary>
    /// <param name="file">File.</param>
    /// <param name="destination">Output stream.</param>
    /// <param name="stored">Stored-image stream used by this caller only.</param>
    /// <exception cref="InvalidDataException">The layout does not cover the file, or a chunk does not decode.</exception>
    public void CopyFile(PS5InnerFile file, Stream destination, Stream stored)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(destination);
        long offset = file.LogicalOffset;
        long end = file.LogicalOffset + Math.Max(0, file.Size);
        while (offset < end)
        {
            int index = Array.BinarySearch(_starts, offset);
            if (index < 0)
            {
                index = ~index - 1;
            }

            if (index < 0 || offset >= Chunks[index].LogicalOffset + Chunks[index].Length)
            {
                throw new InvalidDataException($"inner mount offset 0x{offset:X} is not covered by the layout");
            }

            // Each chunk is decoded once and the part inside the file written.
            byte[] data = DecodeChunk(index, stored);
            int inChunk = (int)(offset - Chunks[index].LogicalOffset);
            int n = (int)Math.Min(end - offset, data.Length - inChunk);
            destination.Write(data, inChunk, n);
            offset += n;
        }
    }

    /// <summary>Read the metadata and list every file and directory under <c>uroot</c>.</summary>
    /// <returns>Files (directories have <see cref="PS5InnerFile.Size"/> −1), in directory walk order.</returns>
    /// <exception cref="InvalidDataException">Malformed metadata.</exception>
    public List<PS5InnerFile> ReadTree()
    {
        long metaBase = Layout.MetadataBase;
        byte[] sb = new byte[PFSHeader.Size];
        Read(metaBase, sb);
        PFSHeader header = PFSHeader.Parse(sb);
        if (header.Magic != PFSConstants.PFSMagic || header.Version != PFSConstants.PFSVersionPS5)
        {
            throw new InvalidDataException("inner metadata does not start with a PS5 PFS superblock");
        }

        Superblock = header;
        // The inode table holds BlockSize / 0xA8 inodes per block; an inode never crosses a block.
        int count = checked((int)header.InodeCount);
        int perBlock = (int)(header.BlockSize / InodeSize);
        byte[] table = new byte[checked((((count + perBlock - 1) / perBlock) * (int)header.BlockSize))];
        Read(metaBase + header.BlockSize, table);

        List<PS5InnerFile> files = [];
        ReadOnlySpan<byte> Entry(long ino) => table.AsSpan(checked((int)((ino / perBlock) * header.BlockSize) + (int)(ino % perBlock * InodeSize)), InodeSize);
        PS5InnerFile Node(long ino, string path)
        {
            ReadOnlySpan<byte> e = Entry(ino);
            ushort mode = BinaryPrimitives.ReadUInt16LittleEndian(e);
            bool dir = (mode & PFSConstants.InodeModeDir) != 0;
            return new PS5InnerFile(
                path,
                ino,
                dir ? -1 : BinaryPrimitives.ReadInt64LittleEndian(e[0x08..]),
                BinaryPrimitives.ReadInt64LittleEndian(e[0x60..]),
                mode,
                BinaryPrimitives.ReadUInt32LittleEndian(e[0x04..]),
                BinaryPrimitives.ReadInt32LittleEndian(e[0x68..]));
        }

        // A directory's dirents run across as many blocks as its inode size says.
        List<PFSDirent> Dirents(PS5InnerFile dir)
        {
            long size = BinaryPrimitives.ReadInt64LittleEndian(Entry(dir.Inode)[0x08..]);
            byte[] block = new byte[checked((int)Math.Max(header.BlockSize, size))];
            Read(dir.LogicalOffset, block);
            return PFSDirent.ParseAll(block).Entries;
        }

        PFSDirent uroot = Dirents(Node(0, string.Empty)).Find(d => d.Name == "uroot")
            ?? throw new InvalidDataException("inner image has no uroot directory");
        HashSet<long> seen = [];
        void Walk(long ino, string prefix)
        {
            if (!seen.Add(ino) || ino >= count)
            {
                throw new InvalidDataException($"inner directory tree loops or references inode {ino}");
            }

            foreach (PFSDirent entry in Dirents(Node(ino, prefix)))
            {
                if (entry.TypeCode is PFSConstants.DirentTypeDot or PFSConstants.DirentTypeDotDot)
                {
                    continue;
                }

                if (entry.InodeNumber < 0 || entry.InodeNumber >= count)
                {
                    throw new InvalidDataException($"inner directory entry {entry.Name} references inode {entry.InodeNumber} of {count}");
                }

                string path = prefix.Length == 0 ? entry.Name : prefix + "/" + entry.Name;
                files.Add(Node(entry.InodeNumber, path));
                if (entry.TypeCode == PFSConstants.DirentTypeDirectory)
                {
                    Walk(entry.InodeNumber, path);
                }
            }
        }

        Walk(uroot.InodeNumber, string.Empty);
        return files;
    }

    // Walk the CblockInfo records. A run base re-anchors the stored cursor at (tweak << 15) plus the low
    // 15 bits of the next chunk's cursor; inside a run chunks follow each other. Each standard record is
    // one chunk of at most 256 KiB that ends where the next standard record starts: uoff holds 2 × (logical
    // start mod 256 KiB), so the length is the difference mod 256 KiB (0 = a full 256 KiB). The terminator
    // (odd uoff) ends the last chunk at the mount end. Publishing Tools cuts 256 KiB chunks from each fidx
    // segment start; other builders (PS5PkgTool) cut 128 KiB Kraken chunks, which this reads the same way.
    private static List<PS5Chunk> BuildChunks(NAPSLayout layout, long imageLength, List<string> problems)
    {
        IReadOnlyList<NAPSCblock> records = layout.Cblocks;
        long mount = layout.MountSize;
        List<PS5Chunk> chunks = [];

        // The u2c table names the first record of each 256 KiB ublock. PS5PkgTool moves a file to the next ublock
        // (same offset within it) once a ublock holds 30 records, leaving a logical hole, so a record that starts a
        // ublock moves the logical cursor there when the ublock before holds those 30 records, the move is a whole number
        // of ublocks and a file starts there.
        HashSet<long> fileStarts = [.. layout.FileOffsets];
        int[] anchor = new int[records.Count];
        anchor.AsSpan().Fill(-1);
        for (int u = 0; u < layout.FirstCblockByUBlock.Count; u++)
        {
            int first = layout.FirstCblockByUBlock[u];
            if (first >= 0 && first < records.Count)
            {
                anchor[first] = u;
            }
        }

        long logical = 0;
        long stored = 0;
        for (int i = 0; i < records.Count && logical < mount; i++)
        {
            NAPSCblock r = records[i];
            if (r.IsRunBase)
            {
                uint next = i + 1 < records.Count ? records[i + 1].CoffsetMod256K : 0;
                stored = ((long)r.TweakIndex << 15) + (next & 0x7FFF);
                continue;
            }

            if (anchor[i] >= 0 && (r.UoffsetStart & 1) == 0)
            {
                long target = ((long)anchor[i] * NAPSLayout.UBlockSize) + (r.UoffsetStart >> 1);
                bool previousFull = anchor[i] > 0 && layout.FirstCblockByUBlock[anchor[i] - 1] == i - 30;
                if (previousFull && target > logical && (target - logical) % NAPSLayout.UBlockSize == 0 && fileStarts.Contains(target))
                {
                    logical = target;
                }
            }

            int nextStd = i + 1;
            while (nextStd < records.Count && records[nextStd].IsRunBase)
            {
                nextStd++;
            }

            long chunkEnd;
            if (nextStd >= records.Count || (records[nextStd].UoffsetStart & 1) != 0)
            {
                chunkEnd = mount;
            }
            else
            {
                long delta = ((records[nextStd].UoffsetStart >> 1) - (r.UoffsetStart >> 1)) & (NAPSLayout.UBlockSize - 1);
                chunkEnd = logical + (delta == 0 ? NAPSLayout.UBlockSize : delta);
            }

            if (chunkEnd > mount || chunkEnd - logical > NAPSLayout.UBlockSize)
            {
                throw new InvalidDataException($"cblock {i}: chunk 0x{logical:X}..0x{chunkEnd:X} is outside the inner mount or longer than 256 KiB");
            }

            int length = checked((int)(chunkEnd - logical));
            uint nextCursor = i + 1 < records.Count ? records[i + 1].CoffsetMod256K : r.CoffsetMod256K;
            int span = (int)((nextCursor - r.CoffsetMod256K) & 0x3FFFF);

            PS5ChunkKind kind;
            int storedLength = span;
            if (r.Kde is 2 or 3)
            {
                kind = PS5ChunkKind.Kraken;
            }
            else if (r.Kde == 0 && !r.Even && r.EvenLengthMinus1 + 1 < (ulong)length)
            {
                kind = PS5ChunkKind.Fill;
                storedLength = (int)r.EvenLengthMinus1 + 1;
            }
            else if (r.Kde == 0 || (r.Kde == 4 && r.Even && r.Odd))
            {
                kind = PS5ChunkKind.Raw;
                storedLength = length;
            }
            else if (r.Kde == 4)
            {
                // Two 128 KiB halves: the length field holds the first half's stored length, the second half
                // takes the rest of the span.
                kind = PS5ChunkKind.Fill;
            }
            else
            {
                throw new InvalidDataException($"cblock {i} uses unsupported kind {r.Kde}");
            }

            if (stored + storedLength > imageLength)
            {
                problems.Add($"cblock {i}: stored range 0x{stored:X}+0x{storedLength:X} is outside pfs_image.dat");
            }

            chunks.Add(new PS5Chunk(i, logical, length, stored, storedLength, (int)r.EvenLengthMinus1 + 1, kind, r.Kde, r.SecondKde));
            stored += storedLength;
            logical = chunkEnd;
        }

        if (logical != mount)
        {
            throw new InvalidDataException($"naps layout covers 0x{logical:X} of a 0x{mount:X}-byte inner mount");
        }

        return chunks;
    }

    private static void CheckU2c(NAPSLayout layout, List<PS5Chunk> chunks, List<string> problems)
    {
        int terminator = layout.Cblocks.Count - 1;
        int c = 0;
        for (int u = 0; u < layout.FirstCblockByUBlock.Count; u++)
        {
            long start = (long)u * NAPSLayout.UBlockSize;
            while (c < chunks.Count && chunks[c].LogicalOffset < start)
            {
                c++;
            }

            int expected = c < chunks.Count ? chunks[c].CblockIndex : terminator;
            if (layout.FirstCblockByUBlock[u] != expected)
            {
                problems.Add($"u2c ublock {u}: cblock {layout.FirstCblockByUBlock[u]}, expected {expected}");
            }
        }
    }

    private byte[] DecodeChunk(int index)
    {
        if (index == _cachedChunk)
        {
            return _cached;
        }

        byte[] output = DecodeChunk(index, _stored);
        _cachedChunk = index;
        _cached = output;
        return output;
    }

    /// <summary>
    /// Decode one chunk through <paramref name="stored"/>, a stream over the stored image of the caller's own, so
    /// several threads can decode at once (each with its stream, from <c>PS5Package.OpenInnerStream</c>).
    /// </summary>
    /// <param name="index">Chunk index.</param>
    /// <param name="stored">Stored-image stream used by this caller only.</param>
    /// <returns>Plaintext of the chunk.</returns>
    /// <exception cref="InvalidDataException">The chunk does not decode.</exception>
    public byte[] DecodeChunk(int index, Stream stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        PS5Chunk chunk = Chunks[index];
        byte[] output = new byte[chunk.Length];
        byte[] storedBytes = new byte[chunk.StoredLength];
        stored.Position = chunk.StoredOffset;
        stored.ReadExactly(storedBytes);
        if (chunk.Kind == PS5ChunkKind.Raw)
        {
            storedBytes.AsSpan(0, chunk.Length).CopyTo(output);
        }
        else if (chunk.Kind == PS5ChunkKind.Fill)
        {
            DecodeFill(storedBytes, chunk, output);
        }
        else if (!DecodeKraken(storedBytes, chunk, output))
        {
            throw new InvalidDataException($"Kraken chunk at inner offset 0x{chunk.LogicalOffset:X} (cblock {chunk.CblockIndex}) does not decode");
        }

        return output;
    }

    // Kind 0: fill blocks for the whole chunk. Kind 4: each 128 KiB half is either stored raw (stored length
    // equal to its length) or fill blocks.
    private static void DecodeFill(byte[] stored, PS5Chunk chunk, byte[] output)
    {
        if (chunk.Kde == 0)
        {
            ExpandFill(stored, chunk, output);
            return;
        }

        int first = Math.Min(chunk.FirstSubChunkLength, stored.Length);
        int half = Math.Min(SubChunkSize, output.Length);
        DecodeHalf(stored.AsSpan(0, first), chunk, output.AsSpan(0, half));
        DecodeHalf(stored.AsSpan(first), chunk, output.AsSpan(half));
    }

    private static void DecodeHalf(ReadOnlySpan<byte> stored, PS5Chunk chunk, Span<byte> output)
    {
        if (stored.Length == output.Length)
        {
            stored.CopyTo(output);
        }
        else
        {
            ExpandFill(stored, chunk, output);
        }
    }

    // Expand consecutive 8-byte fill blocks until the output is full; anything else is reported.
    private static void ExpandFill(ReadOnlySpan<byte> stored, PS5Chunk chunk, Span<byte> output)
    {
        int done = 0;
        for (int at = 0; done < output.Length; at += FillBlockSize)
        {
            ReadOnlySpan<byte> b = at + FillBlockSize <= stored.Length ? stored.Slice(at, FillBlockSize) : [];
            int header = b.IsEmpty ? 0 : (b[0] << 16) | (b[1] << 8) | b[2];
            ushort value = b.IsEmpty ? (ushort)0 : BinaryPrimitives.ReadUInt16BigEndian(b[6..]);
            int length = ((header & 0x7FFFF) >> 2) + 1;
            if (b.IsEmpty || header >> 19 != 4 || b[3] != 0 || b[4] != 3 || b[5] != 0 || (value & 0xC03F) != 0x4000 || length > output.Length - done)
            {
                throw new InvalidDataException($"fill chunk at inner offset 0x{chunk.LogicalOffset:X} (cblock {chunk.CblockIndex}) is not a Kraken fill stream");
            }

            output.Slice(done, length).Fill((byte)(value >> 6));
            done += length;
        }
    }

    private static bool DecodeKraken(byte[] stored, PS5Chunk chunk, byte[] output)
    {
        // Both sub-chunks are newLZ; kind 3 marks sub (delta) literals, in the record's first kind field for
        // sub-chunk 0 and its second one for sub-chunk 1 (Publishing Tools output). The literal mode is not
        // checked by the decoder, so it is never guessed: a wrong mode decodes to wrong bytes without an error.
        int first = chunk.Length > SubChunkSize ? chunk.FirstSubChunkLength : 0;
        int flags = KrakenFlagsNewLzBoth | (chunk.Kde & 1) | ((chunk.SecondKde & 1) << 4);
        try
        {
            return KrakenDecoder.DecodeBlock(stored, flags, first, output) == KrakenDecodeStatus.Success;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
