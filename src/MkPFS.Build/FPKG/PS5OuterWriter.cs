using System.Buffers.Binary;
using System.Text;
using MkPFS.Core.Crypto;
using MkPFS.Core.PFS.PS5;

namespace MkPFS.Build.FPKG;

/// <summary>A file of the outer PFS.</summary>
/// <param name="Name">Name under <c>uroot</c> (<c>pfs_image.dat</c>, <c>naps_pkg_layout.dat</c>).</param>
/// <param name="Length">Stored length.</param>
/// <param name="SizeCompressed">Inode <c>SizeCompressed</c> (the inner mount size for pfs_image.dat).</param>
/// <param name="Signed">Encrypted as a signed block (bit-47 sector); pfs_image.dat is plain data.</param>
/// <param name="BlockDigests">SHA3-256 of each 64 KiB block of the file (the last zero-padded), 32 bytes each.</param>
public sealed record PS5OuterFile(string Name, long Length, long SizeCompressed, bool Signed, byte[] BlockDigests);

/// <summary>The planned outer PFS: everything but the encrypted file data.</summary>
public sealed class PS5OuterLayout
{
    internal PS5OuterLayout(IReadOnlyList<PS5OuterFile> files, int superblockIndex, int total, byte[] digests, byte[] metadata)
    {
        Files = files;
        SuperblockIndex = superblockIndex;
        BlockCount = total;
        BlockDigests = digests;
        Metadata = metadata;
    }

    /// <summary>Files in order.</summary>
    public IReadOnlyList<PS5OuterFile> Files { get; }

    /// <summary>Superblock block index (= the number of file data blocks).</summary>
    public int SuperblockIndex { get; }

    /// <summary>Block count.</summary>
    public int BlockCount { get; }

    /// <summary>Image size in bytes.</summary>
    public long Size => (long)BlockCount * PS5OuterWriter.BlockSize;

    /// <summary>SHA3-256 of every plaintext block, 32 bytes each in block order (the imagedigs source).</summary>
    public byte[] BlockDigests { get; }

    /// <summary>Plaintext superblock block.</summary>
    public ReadOnlySpan<byte> Superblock => Metadata.AsSpan(0, PS5OuterWriter.BlockSize);

    /// <summary>Plaintext blocks from the superblock to the end.</summary>
    internal byte[] Metadata { get; }
}

public static class PS5OuterWriter
{
    /// <summary>Block size.</summary>
    public const int BlockSize = PS5OuterImage.BlockSize;

    private const int DigestSize = 32;
    private const int SignedInodeSize = 0x2C8;
    private const int DirectSlots = 12;
    private const int IndirectSlots = 5;
    private const int SigRecordSize = 36;
    private const int RecordsPerIndirect = BlockSize / SigRecordSize;
    private const int MetadataInodes = 3;
    private const int BatchBlocks = 64;

    /// <summary>Plan the image.</summary>
    /// <param name="files">Outer files in order, with their block digests.</param>
    /// <param name="seconds">Inode and superblock time.</param>
    /// <param name="nanoseconds">Nanosecond part.</param>
    /// <param name="seed">16-byte superblock seed (key derivation input).</param>
    /// <returns>Layout.</returns>
    /// <exception cref="InvalidDataException">A file needs more blocks than one inode addresses.</exception>
    public static PS5OuterLayout Plan(IReadOnlyList<PS5OuterFile> files, long seconds, uint nanoseconds, byte[] seed)
    {
        // ---- Block plan: data first, then sb, inode table, super-root dirents, FLT, indirect trees, uroot. ----
        int[] first = new int[files.Count];
        int[] count = new int[files.Count];
        int dataBlocks = 0;
        for (int i = 0; i < files.Count; i++)
        {
            first[i] = dataBlocks;
            count[i] = BlockCount(files[i].Length);
            if (files[i].BlockDigests.Length != count[i] * DigestSize)
            {
                throw new ArgumentException($"{files[i].Name}: {count[i]} block digests expected", nameof(files));
            }

            dataBlocks = checked(dataBlocks + count[i]);
        }

        int sb = dataBlocks;
        int inodeTable = sb + 1;
        int superRoot = sb + 2;
        int flt = sb + 3;
        int next = sb + 4;
        int[] indirectFirst = new int[files.Count];
        for (int i = 0; i < files.Count; i++)
        {
            indirectFirst[i] = next;
            next += IndirectBlocks(count[i] - DirectSlots, files[i].Name);
        }

        int uroot = next;
        int total = uroot + 1;
        byte[] digests = new byte[(long)total * DigestSize];
        for (int i = 0; i < files.Count; i++)
        {
            files[i].BlockDigests.CopyTo(digests, (long)first[i] * DigestSize);
        }

        // ---- Structural blocks, in memory from the superblock on. ----
        byte[] meta = new byte[(long)(total - sb) * BlockSize];
        Span<byte> Block(int index) => meta.AsSpan((index - sb) * BlockSize, BlockSize);
        ReadOnlySpan<byte> Digest(int index) => digests.AsSpan(index * DigestSize, DigestSize);

        WriteDirents(Block(superRoot), [(1, 2, "inode_flat_path_table"), (2, 3, "uroot")]);
        WriteDirents(Block(uroot), [(2, 4, "."), (2, 5, ".."), .. files.Select((f, i) => (MetadataInodes + i, 2, f.Name))]);
        Span<byte> fltBlock = Block(flt);
        BinaryPrimitives.WriteUInt32LittleEndian(fltBlock, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(fltBlock[0x04..], 0x10);
        BinaryPrimitives.WriteUInt32LittleEndian(fltBlock[0x08..], 0x40);
        fltBlock[0x20] = 0x7F;
        "FLT"u8.CopyTo(fltBlock[0x21..]);
        BinaryPrimitives.WriteInt32LittleEndian(fltBlock[0x2C..], files.Count);
        BinaryPrimitives.WriteUInt64LittleEndian(fltBlock[0x30..], PS5PathHash.Seed0);
        BinaryPrimitives.WriteUInt64LittleEndian(fltBlock[0x38..], PS5PathHash.Seed1);
        for (int i = 0; i < files.Count; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(fltBlock[(0x40 + (16 * i))..], PS5PathHash.HashName(files[i].Name));
            BinaryPrimitives.WriteUInt64LittleEndian(fltBlock[(0x48 + (16 * i))..], (uint)(MetadataInodes + i) | ((ulong)(uint)i << 40));
        }

        // Indirect trees: a node's records hash finished children, so each subtree is written before its parent
        // record (pre-order allocation). Metadata-block digests are filled in as blocks complete.
        void Seal(int index) => SHA3256.HashData(Block(index), digests.AsSpan(index * DigestSize, DigestSize));
        int WriteTree(ref int cursor, int firstData, long blocks, int depth)
        {
            int node = cursor++;
            long span = Span(depth - 1);
            int slot = 0;
            for (long done = 0; done < blocks; done += span, slot++)
            {
                int child = depth == 0 ? firstData + (int)done : WriteTree(ref cursor, firstData + (int)done, Math.Min(span, blocks - done), depth - 1);
                Span<byte> record = Block(node).Slice(slot * SigRecordSize, SigRecordSize);
                Digest(child).CopyTo(record);
                BinaryPrimitives.WriteInt32LittleEndian(record[32..], child);
            }

            Seal(node);
            return node;
        }

        List<int>[] roots = new List<int>[files.Count];
        for (int i = 0; i < files.Count; i++)
        {
            roots[i] = [];
            int cursor = indirectFirst[i];
            long done = DirectSlots;
            for (int slot = 0; slot < IndirectSlots && done < count[i]; slot++)
            {
                long take = Math.Min(count[i] - done, Span(slot));
                roots[i].Add(WriteTree(ref cursor, first[i] + (int)done, take, slot));
                done += take;
            }
        }

        Seal(superRoot);
        Seal(flt);
        Seal(uroot);

        Span<byte> table = Block(inodeTable);
        long fltSize = 0x40 + (16L * files.Count);
        WriteInode(table, 0, 0x416D, 1, 0x2000C, BlockSize, BlockSize, [superRoot], [], Digest, seconds, nanoseconds);
        WriteInode(table, 1, 0x816D, 1, 0x2000C, fltSize, fltSize, [flt], [], Digest, seconds, nanoseconds);
        WriteInode(table, 2, 0x416D, 3, 0xC, BlockSize, BlockSize, [uroot], [], Digest, seconds, nanoseconds);
        for (int i = 0; i < files.Count; i++)
        {
            int[] blocks = [.. Enumerable.Range(first[i], Math.Min(count[i], DirectSlots))];
            WriteInode(table, MetadataInodes + i, 0x816D, 1, 0xD, files[i].Length, files[i].SizeCompressed, blocks, [.. roots[i]], Digest, seconds, nanoseconds, count[i]);
        }

        Seal(inodeTable);

        // ---- Superblock: the super-root inode records SHA3-256 of the inode table; ICV over 0x5A0 bytes. ----
        Span<byte> super = Block(sb);
        BinaryPrimitives.WriteInt64LittleEndian(super, 2);
        BinaryPrimitives.WriteInt64LittleEndian(super[0x08..], 20130315);
        super[0x1A] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(super[0x1C..], 0x0D);
        BinaryPrimitives.WriteUInt32LittleEndian(super[0x20..], BlockSize);
        BinaryPrimitives.WriteInt64LittleEndian(super[0x28..], 1);
        BinaryPrimitives.WriteInt64LittleEndian(super[0x30..], MetadataInodes + files.Count);
        BinaryPrimitives.WriteInt64LittleEndian(super[0x38..], total);
        BinaryPrimitives.WriteInt64LittleEndian(super[0x40..], 1);
        Span<byte> root = super[0x50..];
        BinaryPrimitives.WriteUInt16LittleEndian(root[0x02..], 1);
        BinaryPrimitives.WriteInt64LittleEndian(root[0x08..], BlockSize);
        BinaryPrimitives.WriteInt64LittleEndian(root[0x10..], BlockSize);
        WriteTimes(root[0x18..], seconds, nanoseconds);
        BinaryPrimitives.WriteUInt32LittleEndian(root[0x60..], 1);
        Digest(inodeTable).CopyTo(root[0x68..]);
        BinaryPrimitives.WriteInt64LittleEndian(root[0x88..], inodeTable);
        BinaryPrimitives.WriteInt32LittleEndian(super[0x36C..], 1);
        seed.CopyTo(super[0x370..]);
        SHA3256.HashData(super[..PS5OuterImage.SuperblockIcvLength], super[PS5OuterImage.SuperblockIcvOffset..]);
        Seal(sb);

        return new PS5OuterLayout(files, sb, total, digests, meta);
    }

    /// <summary>
    /// Write the encrypted image at the current position of <paramref name="output"/>: each file's blocks read from
    /// its stream (encrypted in parallel batches, written in order), then the structural blocks.
    /// </summary>
    /// <param name="layout">Plan.</param>
    /// <param name="output">Destination.</param>
    /// <param name="data">One stream per file, read from its current position.</param>
    /// <param name="seed">The seed the plan used.</param>
    /// <param name="ekpfs">32-byte image key.</param>
    /// <param name="workers">Blocks encrypted at once (−1: no limit).</param>
    public static void Write(PS5OuterLayout layout, Stream output, IReadOnlyList<Stream> data, byte[] seed, byte[] ekpfs, int workers = -1)
    {
        byte[] key = PS5Keys.XtsKey(ekpfs, seed);

        // Batches of up to BatchBlocks blocks of one file; the next batch is read on a background task while the
        // current one is encrypted and written.
        List<(int File, int Blocks, int FirstIndex)> plan = [];
        int index = 0;
        for (int i = 0; i < layout.Files.Count; i++)
        {
            int blocks = BlockCount(layout.Files[i].Length);
            for (int k = 0; k < blocks; k += BatchBlocks)
            {
                int n = Math.Min(BatchBlocks, blocks - k);
                plan.Add((i, n, index));
                index += n;
            }
        }

        long[] left = [.. layout.Files.Select(f => f.Length)];
        byte[][][] buffers = [new byte[BatchBlocks][], new byte[BatchBlocks][]];
        byte[][] Read(int b)
        {
            (int file, int n, _) = plan[b];
            byte[][] batch = buffers[b & 1];
            for (int j = 0; j < n; j++)
            {
                batch[j] ??= new byte[BlockSize];
                int length = (int)Math.Min(BlockSize, left[file]);
                data[file].ReadExactly(batch[j].AsSpan(0, length));
                batch[j].AsSpan(length).Clear();
                left[file] -= length;
            }

            return batch;
        }

        Task<byte[][]>? pending = plan.Count > 0 ? Task.Run(() => Read(0)) : null;
        try
        {
            for (int b = 0; b < plan.Count; b++)
            {
                byte[][] batch = pending!.GetAwaiter().GetResult();
                int next = b + 1;
                pending = next < plan.Count ? Task.Run(() => Read(next)) : null;
                (int file, int n, int firstIndex) = plan[b];
                bool signed = layout.Files[file].Signed;
                Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = workers }, () => new XtsAes(key), (j, _, worker) =>
                {
                    worker.Encrypt(batch[j], PS5Keys.OuterBlockSector(firstIndex + j, signed));
                    return worker;
                }, worker => worker.Dispose());
                for (int j = 0; j < n; j++)
                {
                    output.Write(batch[j]);
                }
            }
        }
        finally
        {
            // A failed write leaves the read-ahead to finish before its streams go away.
            try
            {
                pending?.Wait();
            }
            catch (AggregateException)
            {
            }
        }

        // Structural blocks: the superblock stays plaintext, the rest are signed blocks.
        byte[] meta = (byte[])layout.Metadata.Clone();
        using XtsAes xts = new(key);
        for (int b = layout.SuperblockIndex + 1; b < layout.BlockCount; b++)
        {
            xts.Encrypt(meta.AsSpan((b - layout.SuperblockIndex) * BlockSize, BlockSize), PS5Keys.OuterBlockSector(b, signed: true));
        }

        output.Write(meta);
    }

    /// <summary>Blocks a file of <paramref name="length"/> bytes occupies (at least one).</summary>
    /// <param name="length">Length.</param>
    /// <returns>Block count.</returns>
    public static int BlockCount(long length) => checked((int)Math.Max(1, (length + BlockSize - 1) / BlockSize));

    /// <summary>SHA3-256 of each block of a small in-memory file, zero-padded.</summary>
    /// <param name="data">File bytes.</param>
    /// <returns>32 bytes per block.</returns>
    public static byte[] BlockDigestsOf(ReadOnlySpan<byte> data)
    {
        int blocks = BlockCount(data.Length);
        byte[] digests = new byte[blocks * DigestSize];
        byte[] block = new byte[BlockSize];
        for (int b = 0; b < blocks; b++)
        {
            ReadOnlySpan<byte> part = data[Math.Min(data.Length, b * BlockSize)..Math.Min(data.Length, (b + 1) * BlockSize)];
            part.CopyTo(block);
            block.AsSpan(part.Length).Clear();
            SHA3256.HashData(block, digests.AsSpan(b * DigestSize, DigestSize));
        }

        return digests;
    }

    // Data blocks one indirect tree of the given depth addresses (depth −1: a single data block).
    private static long Span(int depth)
    {
        long span = 1;
        for (int d = 0; d <= depth; d++)
        {
            span = checked(span * RecordsPerIndirect);
        }

        return span;
    }

    // Indirect blocks for `extra` data blocks past the direct slots, over the five slots.
    private static int IndirectBlocks(long extra, string name)
    {
        int total = 0;
        for (int slot = 0; slot < IndirectSlots && extra > 0; slot++)
        {
            long take = Math.Min(extra, Span(slot));
            total += TreeBlocks(take, slot);
            extra -= take;
        }

        if (extra > 0)
        {
            throw new InvalidDataException($"{name} is too large for one outer inode");
        }

        return total;
    }

    private static int TreeBlocks(long blocks, int depth)
    {
        if (depth == 0)
        {
            return 1;
        }

        long span = Span(depth - 1);
        int total = 1;
        for (long done = 0; done < blocks; done += span)
        {
            total += TreeBlocks(Math.Min(span, blocks - done), depth - 1);
        }

        return total;
    }

    private delegate ReadOnlySpan<byte> DigestOf(int block);

    private static void WriteTimes(Span<byte> at, long seconds, uint nanoseconds)
    {
        for (int t = 0; t < 4; t++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(at[(8 * t)..], seconds);
            BinaryPrimitives.WriteUInt32LittleEndian(at[(0x20 + (4 * t))..], nanoseconds);
        }
    }

    private static void WriteInode(Span<byte> table, int inode, ushort mode, ushort nlink, uint flags, long size, long sizeCompressed,
        int[] direct, int[] indirect, DigestOf digest, long seconds, uint nanoseconds, int blockCount = -1)
    {
        Span<byte> e = table.Slice(inode * SignedInodeSize, SignedInodeSize);
        BinaryPrimitives.WriteUInt16LittleEndian(e, mode);
        BinaryPrimitives.WriteUInt16LittleEndian(e[0x02..], nlink);
        BinaryPrimitives.WriteUInt32LittleEndian(e[0x04..], flags);
        BinaryPrimitives.WriteInt64LittleEndian(e[0x08..], size);
        BinaryPrimitives.WriteInt64LittleEndian(e[0x10..], sizeCompressed);
        WriteTimes(e[0x18..], seconds, nanoseconds);
        BinaryPrimitives.WriteUInt32LittleEndian(e[0x60..], (uint)(blockCount < 0 ? direct.Length : blockCount));
        for (int k = 0; k < direct.Length; k++)
        {
            Span<byte> record = e.Slice(0x64 + (k * SigRecordSize), SigRecordSize);
            digest(direct[k]).CopyTo(record);
            BinaryPrimitives.WriteInt32LittleEndian(record[32..], direct[k]);
        }

        for (int k = 0; k < indirect.Length; k++)
        {
            Span<byte> record = e.Slice(0x64 + ((DirectSlots + k) * SigRecordSize), SigRecordSize);
            digest(indirect[k]).CopyTo(record);
            BinaryPrimitives.WriteInt32LittleEndian(record[32..], indirect[k]);
        }
    }

    private static void WriteDirents(Span<byte> block, List<(int Inode, int Type, string Name)> entries)
    {
        int offset = 0;
        foreach ((int inode, int type, string name) in entries)
        {
            int size = (16 + name.Length + 1 + 7) & ~7;
            BinaryPrimitives.WriteInt32LittleEndian(block[offset..], inode);
            BinaryPrimitives.WriteInt32LittleEndian(block[(offset + 4)..], type);
            BinaryPrimitives.WriteInt32LittleEndian(block[(offset + 8)..], name.Length);
            BinaryPrimitives.WriteInt32LittleEndian(block[(offset + 12)..], size);
            Encoding.ASCII.GetBytes(name, block[(offset + 16)..]);
            offset += size;
        }
    }
}
