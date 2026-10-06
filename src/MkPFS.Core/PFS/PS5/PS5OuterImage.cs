using System.Buffers.Binary;
using System.Collections.Concurrent;
using MkPFS.Core.Crypto;
using MkPFS.Core.PKG;

namespace MkPFS.Core.PFS.PS5;

/// <summary>
/// The outer PFS of a PS5 finalized image: data blocks first, then the plaintext superblock, the signed
/// inode table, super-root dirents, <c>\x7fFLT</c> and <c>uroot</c> dirents (LibProsperoPkg
/// <c>ProsperoOuterPfsBuilder</c>; Publishing Tools output has the same layout). Every block is AES-XTS
/// encrypted as one 0x10000-byte data unit, except the superblock, unless the image is plaintext
/// (Publishing Tools' no-auth profile). Every block read is checked against the SHA3-256 recorded by
/// its owner, which also tells which of the three encodings (plain, data sector, signed sector) applies.
/// </summary>
public sealed class PS5OuterImage
{
    /// <summary>Outer block size.</summary>
    public const int BlockSize = 0x10000;

    /// <summary>Superblock region covered by the ICV.</summary>
    public const int SuperblockIcvLength = 0x5A0;

    /// <summary>ICV offset in the superblock.</summary>
    public const int SuperblockIcvOffset = 0x380;

    private const int SuperRootSigOffset = 0xB8;
    private const int SuperRootBlockOffset = 0xD8;
    private const int SignedInodeSize = 0x2C8;

    private readonly PKGFile _pkg;
    private readonly byte[]? _xtsKey;
    private readonly ConcurrentBag<XtsAes> _ciphers = [];
    private readonly long _base;

    private PS5OuterImage(PKGFile pkg, PFSHeader header, byte[] superblock, byte[]? xtsKey, bool plaintext)
    {
        _pkg = pkg;
        _base = pkg.FIH!.PFSImageOffset;
        _xtsKey = xtsKey;
        Header = header;
        Superblock = superblock;
        IsPlaintext = plaintext;
    }

    /// <summary>Parsed outer superblock.</summary>
    public PFSHeader Header { get; }

    /// <summary>Raw superblock block.</summary>
    public byte[] Superblock { get; }

    /// <summary>True when blocks are stored without encryption.</summary>
    public bool IsPlaintext { get; }

    /// <summary>Superblock block index within the outer image.</summary>
    public long SuperblockIndex => (_pkg.FIH!.SuperblockOffset - _base) / BlockSize;

    /// <summary>Inodes (signed, 32-bit layout).</summary>
    public IReadOnlyList<PFSInode> Inodes { get; private set; } = [];

    /// <summary>Files directly under <c>uroot</c>, by name.</summary>
    public IReadOnlyDictionary<string, PFSInode> Files { get; private set; } = new Dictionary<string, PFSInode>();

    /// <summary>Whether the stored ICV equals SHA3-256 of the superblock with the ICV zeroed.</summary>
    public bool IcvValid
    {
        get
        {
            byte[] region = Superblock.AsSpan(0, SuperblockIcvLength).ToArray();
            region.AsSpan(SuperblockIcvOffset, 32).Clear();
            return SHA3256.HashData(region).AsSpan().SequenceEqual(Superblock.AsSpan(SuperblockIcvOffset, 32));
        }
    }

    /// <summary>
    /// Open the outer image of a finalized package. <paramref name="ekpfs"/> is only needed for an
    /// encrypted image (debug images: <see cref="PS5Keys.DeriveEkpfs"/>).
    /// </summary>
    /// <param name="pkg">Package with a FIH header.</param>
    /// <param name="ekpfs">32-byte image key, or null.</param>
    /// <returns>Outer image.</returns>
    /// <exception cref="InvalidDataException">Malformed image, or the key does not open it.</exception>
    public static PS5OuterImage Open(PKGFile pkg, byte[]? ekpfs)
    {
        FIHHeader fih = pkg.FIH ?? throw new InvalidDataException("a bare CNT container has no outer PFS image");
        byte[] superblock = new byte[BlockSize];
        pkg.Read(fih.SuperblockOffset, superblock);
        PFSHeader header = PFSHeader.Parse(superblock);
        if (header.Magic != PFSConstants.PFSMagic || header.Version != PFSConstants.PFSVersionPS5 || header.BlockSize != BlockSize)
        {
            throw new InvalidDataException("outer superblock is not a PS5 PFS superblock");
        }

        // The super-root inode inside the superblock records SHA3-256(inode table) and its block index.
        byte[] tableSig = superblock.AsSpan(SuperRootSigOffset, 32).ToArray();
        long tableBlock = BinaryPrimitives.ReadInt64LittleEndian(superblock.AsSpan(SuperRootBlockOffset));

        byte[] raw = new byte[BlockSize];
        pkg.Read(fih.PFSImageOffset + (tableBlock * BlockSize), raw);
        byte[]? xtsKey = null;
        bool plaintext = SHA3256.HashData(raw).AsSpan().SequenceEqual(tableSig);
        if (!plaintext)
        {
            if (ekpfs is null)
            {
                throw new InvalidDataException("the outer PFS image is encrypted; a passcode or image key is required");
            }

            xtsKey = PS5Keys.XtsKey(ekpfs, header.Seed);
        }

        PS5OuterImage image = new(pkg, header, superblock, xtsKey, plaintext);
        byte[] table = image.ReadBlock(tableBlock, tableSig)
            ?? throw new InvalidDataException("the image key does not open the outer PFS image (inode table hash mismatch)");
        image.LoadTree(table);
        return image;
    }

    /// <summary>Read and verify one block, or null when no encoding matches <paramref name="expectedSig"/>.</summary>
    /// <param name="block">Block index.</param>
    /// <param name="expectedSig">SHA3-256 of the plaintext block.</param>
    /// <returns>Plaintext block or null.</returns>
    public byte[]? ReadBlock(long block, ReadOnlySpan<byte> expectedSig)
    {
        if (block < 0 || (block + 1) * BlockSize > _pkg.FIH!.PFSImageSize)
        {
            return null;
        }

        // Thread-safe: a buffer per call and a cipher borrowed from a pool, so checks and readers run in parallel.
        byte[] stored = new byte[BlockSize];
        _pkg.Read(_base + (block * BlockSize), stored);
        if (IsPlaintext)
        {
            return SHA3256.HashData(stored).AsSpan().SequenceEqual(expectedSig) ? stored : null;
        }

        // Signed (metadata) blocks use bit 47 in the sector number, data blocks the plain index. Data blocks are
        // nearly all of an image, so they are tried first; only the one whose hash matches is returned either way.
        XtsAes xts = _ciphers.TryTake(out XtsAes? pooled) ? pooled : new XtsAes(_xtsKey);
        try
        {
            foreach (bool signed in (ReadOnlySpan<bool>)[false, true])
            {
                byte[] candidate = (byte[])stored.Clone();
                xts.Decrypt(candidate, PS5Keys.OuterBlockSector(block, signed));
                if (SHA3256.HashData(candidate).AsSpan().SequenceEqual(expectedSig))
                {
                    return candidate;
                }
            }
        }
        finally
        {
            _ciphers.Add(xts);
        }

        return null;
    }

    /// <summary>Resolve the {signature, block} list of a file inode, following indirect blocks.</summary>
    /// <param name="inode">Inode.</param>
    /// <returns>Block references in file order.</returns>
    /// <exception cref="InvalidDataException">An indirect block fails its hash.</exception>
    public List<(byte[] Sig, long Block)> BlockList(PFSInode inode)
    {
        List<(byte[] Sig, long Block)> blocks = [];
        int direct = (int)Math.Min(inode.Blocks, PFSConstants.MaxDirectBlocks);
        for (int i = 0; i < direct; i++)
        {
            blocks.Add((inode.DbSig[i], inode.Db[i]));
        }

        // Indirect blocks hold consecutive 36-byte {SHA3-256, i32 block} records. Slot n of the inode is an
        // (n + 1)-level tree: slot 0 points at data blocks, slot 1 at indirect blocks (Publishing Tools output
        // past 12 + 1820 blocks), and so on.
        for (int i = 0; i < inode.Ib.Length && blocks.Count < inode.Blocks; i++)
        {
            Collect(inode, inode.Ib[i], inode.IbSig[i], i, blocks);
        }

        return blocks;
    }

    private void Collect(PFSInode inode, long block, byte[] sig, int depth, List<(byte[] Sig, long Block)> blocks)
    {
        const int RecordSize = 36;
        byte[] indirect = ReadBlock(block, sig)
            ?? throw new InvalidDataException($"indirect block {block} of inode {inode.Number} fails its hash");
        for (int off = 0; off + RecordSize <= BlockSize && blocks.Count < inode.Blocks; off += RecordSize)
        {
            byte[] childSig = indirect.AsSpan(off, 32).ToArray();
            long child = BinaryPrimitives.ReadInt32LittleEndian(indirect.AsSpan(off + 32));
            if (depth == 0)
            {
                blocks.Add((childSig, child));
            }
            else
            {
                Collect(inode, child, childSig, depth - 1, blocks);
            }
        }
    }

    /// <summary>Read a whole small file (for example <c>naps_pkg_layout.dat</c>).</summary>
    /// <param name="inode">File inode.</param>
    /// <returns>File bytes.</returns>
    public byte[] ReadFile(PFSInode inode)
    {
        byte[] data = new byte[checked((int)inode.Size)];
        using Stream stream = OpenFile(inode);
        stream.ReadExactly(data);
        return data;
    }

    /// <summary>Open a seekable, verified view of a file's stored bytes.</summary>
    /// <param name="inode">File inode.</param>
    /// <returns>Read-only stream of <see cref="PFSInode.Size"/> bytes.</returns>
    public Stream OpenFile(PFSInode inode) => new OuterFileStream(this, inode, BlockList(inode));

    private void LoadTree(byte[] table)
    {
        List<PFSInode> inodes = [];
        for (int i = 0; i < Header.InodeCount; i++)
        {
            inodes.Add(PFSInode.Parse(table.AsSpan(i * SignedInodeSize, SignedInodeSize), i, signed: true));
        }

        Inodes = inodes;
        PFSInode root = inodes[0];
        byte[] rootDirents = ReadBlock(root.Db[0], root.DbSig[0])
            ?? throw new InvalidDataException("super-root dirent block fails its hash");
        PFSDirent uroot = PFSDirent.ParseAll(rootDirents).Entries.Find(d => d.Name == "uroot")
            ?? throw new InvalidDataException("outer image has no uroot directory");
        PFSInode urootInode = inodes[(int)uroot.InodeNumber];
        byte[] urootDirents = ReadBlock(urootInode.Db[0], urootInode.DbSig[0])
            ?? throw new InvalidDataException("uroot dirent block fails its hash");
        Dictionary<string, PFSInode> files = new(StringComparer.Ordinal);
        foreach (PFSDirent entry in PFSDirent.ParseAll(urootDirents).Entries)
        {
            if (entry.TypeCode == PFSConstants.DirentTypeFile && entry.InodeNumber < inodes.Count)
            {
                files[entry.Name] = inodes[(int)entry.InodeNumber];
            }
        }

        Files = files;
    }

    private sealed class OuterFileStream(PS5OuterImage image, PFSInode inode, List<(byte[] Sig, long Block)> blocks) : Stream
    {
        private long _position;
        private int _cachedIndex = -1;
        private byte[] _cached = [];

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => inode.Size;

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int total = 0;
            while (buffer.Length > 0 && _position < inode.Size)
            {
                int index = (int)(_position / BlockSize);
                if (index != _cachedIndex)
                {
                    if (index >= blocks.Count)
                    {
                        throw new InvalidDataException($"inode {inode.Number} has fewer blocks than its size");
                    }

                    _cached = image.ReadBlock(blocks[index].Block, blocks[index].Sig)
                        ?? throw new InvalidDataException($"block {blocks[index].Block} of inode {inode.Number} fails its hash");
                    _cachedIndex = index;
                }

                int inBlock = (int)(_position % BlockSize);
                int n = (int)Math.Min(Math.Min(buffer.Length, BlockSize - inBlock), inode.Size - _position);
                _cached.AsSpan(inBlock, n).CopyTo(buffer);
                buffer = buffer[n..];
                _position += n;
                total += n;
            }

            return total;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => inode.Size + offset,
            };
            return _position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
