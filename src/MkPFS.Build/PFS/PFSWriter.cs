using System.Buffers.Binary;
using System.Text;
using MkPFS.Core.Crypto;
using MkPFS.Core.PFS;

namespace MkPFS.Build.PFS;

/// <summary>Invalid build input or failed post-write check (Python <c>BuildError</c>).</summary>
public sealed class BuildException(string message) : Exception(message);

/// <summary>Mutable inode used while laying out an image (Python <c>Inode</c>).</summary>
public sealed class WriteInode
{
    /// <summary>Inode number.</summary>
    public required long Number { get; set; }

    /// <summary>Mode bits.</summary>
    public required ushort Mode { get; set; }

    /// <summary>Link count.</summary>
    public required ushort Nlink { get; set; }

    /// <summary>Flags.</summary>
    public required uint Flags { get; set; }

    /// <summary>Size field (stored size for compressed files).</summary>
    public required long Size { get; set; }

    /// <summary>Size-compressed field (logical size for compressed files).</summary>
    public required long SizeCompressed { get; set; }

    /// <summary>Block count.</summary>
    public required uint Blocks { get; set; }

    /// <summary>Direct block pointers.</summary>
    public long[] Db { get; } = new long[PFSConstants.MaxDirectBlocks];

    /// <summary>Indirect block pointers.</summary>
    public long[] Ib { get; } = new long[PFSConstants.MaxIndirectBlocks];

    /// <summary>Direct block signatures (signed images).</summary>
    public byte[][] DbSig { get; } = [.. Enumerable.Range(0, PFSConstants.MaxDirectBlocks).Select(_ => new byte[PFSConstants.SigSize])];

    /// <summary>Indirect block signatures (signed images).</summary>
    public byte[][] IbSig { get; } = [.. Enumerable.Range(0, PFSConstants.MaxIndirectBlocks).Select(_ => new byte[PFSConstants.SigSize])];

    /// <summary>Timestamp for all four time fields.</summary>
    public long TimeSec { get; init; }

    /// <summary>Serialize in the unsigned D32 layout (0xA8 bytes).</summary>
    /// <param name="destination">At least <see cref="PFSConstants.InodeD32Size"/> bytes.</param>
    public void WriteD32(Span<byte> destination)
    {
        destination[..PFSConstants.InodeD32Size].Clear();
        WriteBase(destination);
        for (int i = 0; i < Db.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(destination[(0x64 + (i * 4))..], checked((int)Db[i]));
        }

        for (int i = 0; i < Ib.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(destination[(0x94 + (i * 4))..], checked((int)Ib[i]));
        }
    }

    /// <summary>Serialize in the signed S32 or S64 layout.</summary>
    /// <param name="destination">At least the layout's inode size.</param>
    /// <param name="inodeBits">32 or 64.</param>
    public void WriteSigned(Span<byte> destination, int inodeBits)
    {
        SignedInodeLayout layout = SignedInodeLayout.For(inodeBits);
        destination[..layout.InodeSize].Clear();
        WriteBase(destination);
        int off = layout.PointerTableOffset;
        foreach ((byte[] sig, long block) in DbSig.Zip(Db).Concat(IbSig.Zip(Ib)))
        {
            sig.CopyTo(destination[off..]);
            if (layout.PointerSize == 4)
            {
                BinaryPrimitives.WriteInt32LittleEndian(destination[(off + PFSConstants.SigSize)..], checked((int)block));
            }
            else
            {
                BinaryPrimitives.WriteInt64LittleEndian(destination[(off + PFSConstants.SigSize)..], block);
            }

            off += layout.EntrySize;
        }
    }

    private void WriteBase(Span<byte> d)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(d, Mode);
        BinaryPrimitives.WriteUInt16LittleEndian(d[0x02..], Nlink);
        BinaryPrimitives.WriteUInt32LittleEndian(d[0x04..], Flags);
        BinaryPrimitives.WriteInt64LittleEndian(d[0x08..], Size);
        BinaryPrimitives.WriteInt64LittleEndian(d[0x10..], SizeCompressed);
        for (int i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(d[(0x18 + (i * 8))..], TimeSec);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(d[0x60..], Blocks);
    }
}

/// <summary>Serialization helpers shared by the image builders (port of <c>pfs.py</c> writer helpers).</summary>
public static class PFSWriter
{
    /// <summary>Dirent bytes: inode, type, name length, entry size, ASCII name, zero padding to 8 (Python <c>Dirent.to_bytes</c>).</summary>
    /// <param name="inode">Inode number.</param>
    /// <param name="type">Dirent type.</param>
    /// <param name="name">ASCII name.</param>
    /// <returns>Entry bytes.</returns>
    /// <exception cref="BuildException">The name is not ASCII.</exception>
    public static byte[] Dirent(long inode, int type, string name)
    {
        if (!Ascii.IsValid(name))
        {
            throw new BuildException($"Filename {PythonRepr(name)} contains non-ASCII characters and cannot be stored in a PFS image");
        }

        int size = DirentSize(name.Length);
        byte[] entry = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(entry, (uint)inode);
        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(4), type);
        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(8), name.Length);
        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(12), size);
        Encoding.ASCII.GetBytes(name, entry.AsSpan(16));
        return entry;
    }

    /// <summary>Entry size for a name: <c>len + 17</c> rounded up to 8.</summary>
    /// <param name="nameLength">Name length.</param>
    /// <returns>Bytes.</returns>
    public static int DirentSize(int nameLength) => (nameLength + 17 + 7) & ~7;

    /// <summary>Compose header mode bits (Python <c>compose_pfs_mode_with_options</c>).</summary>
    /// <param name="inodeBits">32 or 64.</param>
    /// <param name="caseInsensitive">Case-insensitive flag.</param>
    /// <param name="signed">Signed flag.</param>
    /// <param name="encrypted">Encrypted flag.</param>
    /// <returns>Mode.</returns>
    public static ushort Mode(int inodeBits, bool caseInsensitive, bool signed, bool encrypted)
    {
        int mode = 0;
        if (inodeBits == 64)
        {
            mode |= PFSConstants.PFSMode64BitInodes;
        }

        if (caseInsensitive)
        {
            mode |= PFSConstants.PFSModeCaseInsensitive;
        }

        if (signed)
        {
            mode |= PFSConstants.PFSModeSigned;
        }

        if (encrypted)
        {
            mode |= PFSConstants.PFSModeEncrypted;
        }

        return (ushort)mode;
    }

    /// <summary>Plaintext header block (Python <c>_pack_pfs_header_block</c>).</summary>
    /// <returns><paramref name="blockSize"/> bytes.</returns>
    public static byte[] HeaderBlock(
        int blockSize, long version, ushort mode, long nblock, long inodeCount, long finalNdblock, long inodeBlockCount,
        long now, bool signed, bool encrypted, ReadOnlySpan<byte> seed)
    {
        byte[] hdr = new byte[blockSize];
        Span<byte> h = hdr;
        BinaryPrimitives.WriteInt64LittleEndian(h, version);
        BinaryPrimitives.WriteInt64LittleEndian(h[0x08..], PFSConstants.PFSMagic);
        h[0x1A] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(h[0x1C..], mode);
        BinaryPrimitives.WriteInt32LittleEndian(h[0x20..], blockSize);
        BinaryPrimitives.WriteInt64LittleEndian(h[0x28..], nblock);
        BinaryPrimitives.WriteInt64LittleEndian(h[0x30..], inodeCount);
        BinaryPrimitives.WriteInt64LittleEndian(h[0x38..], finalNdblock);
        BinaryPrimitives.WriteInt64LittleEndian(h[0x40..], inodeBlockCount);
        WriteInodeBlockSig(h.Slice(0x50, 0x310), inodeBlockCount, blockSize, now, signed);
        if (signed || encrypted)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(h[0x36C..], 1);
            seed.CopyTo(h[0x370..]);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(h[0x368..], 1);
        }

        return hdr;
    }

    /// <summary>
    /// Write inodes from the current position, skipping to the next block when the rest of a block cannot hold
    /// another inode (Python <c>_write_inode_table</c>).
    /// </summary>
    /// <param name="output">Stream positioned at the inode table.</param>
    /// <param name="inodes">Inodes in order.</param>
    /// <param name="signed">Signed layout.</param>
    /// <param name="inodeBits">Signed inode width.</param>
    /// <param name="blockSize">Block size.</param>
    public static void WriteInodeTable(Stream output, IReadOnlyList<WriteInode> inodes, bool signed, int inodeBits, int blockSize)
    {
        int inodeSize = InodeSize(signed, inodeBits);
        byte[] buffer = new byte[inodeSize];
        foreach (WriteInode inode in inodes)
        {
            if (signed)
            {
                inode.WriteSigned(buffer, inodeBits);
            }
            else
            {
                inode.WriteD32(buffer);
            }

            output.Write(buffer);
            long inBlock = output.Position % blockSize;
            if (inBlock > blockSize - inodeSize)
            {
                output.Seek(blockSize - inBlock, SeekOrigin.Current);
            }
        }
    }

    /// <summary>Serialized inode size for a layout.</summary>
    /// <param name="signed">Signed layout.</param>
    /// <param name="inodeBits">Signed width.</param>
    /// <returns>Bytes.</returns>
    public static int InodeSize(bool signed, int inodeBits) =>
        !signed ? PFSConstants.InodeD32Size : SignedInodeLayout.For(inodeBits).InodeSize;

    /// <summary>
    /// flat_path_table and collision resolver blobs (Python <c>make_fpt_and_collision_blob</c>). Entries keep their
    /// order inside a collision group; the table is sorted by hash.
    /// </summary>
    /// <param name="entries">Paths (<c>/dir</c>, <c>/dir/file</c>), inode numbers and directory flags.</param>
    /// <param name="caseInsensitive">Hash case folding.</param>
    /// <returns>Table, collision blob (or <see langword="null"/>), and whether any hash collided.</returns>
    public static (byte[] Table, byte[]? Collision, bool HasCollision) FlatPathTables(
        IEnumerable<(string Path, long Inode, bool IsDir)> entries, bool caseInsensitive)
    {
        SortedDictionary<uint, List<(string Path, long Inode, bool IsDir)>> byHash = [];
        foreach ((string Path, long Inode, bool IsDir) entry in entries)
        {
            uint hash = FlatPathTable.Hash(entry.Path, caseInsensitive);
            if (!byHash.TryGetValue(hash, out List<(string, long, bool)>? group))
            {
                byHash[hash] = group = [];
            }

            group.Add(entry);
        }

        bool hasCollision = byHash.Values.Any(g => g.Count > 1);
        using MemoryStream collision = new();
        Dictionary<uint, long> collisionOffsets = [];
        if (hasCollision)
        {
            foreach ((uint hash, List<(string Path, long Inode, bool IsDir)> group) in byHash)
            {
                if (group.Count <= 1)
                {
                    continue;
                }

                collisionOffsets[hash] = collision.Length;
                foreach ((string path, long inode, bool isDir) in group)
                {
                    collision.Write(Dirent(inode, isDir ? PFSConstants.DirentTypeDirectory : PFSConstants.DirentTypeFile, path));
                }

                collision.Write(new byte[0x18]);
            }
        }

        byte[] table = new byte[byHash.Count * 8];
        int off = 0;
        foreach ((uint hash, List<(string Path, long Inode, bool IsDir)> group) in byHash)
        {
            long value = group.Count == 1
                ? group[0].Inode | (group[0].IsDir ? FlatPathTable.DirectoryBit : 0)
                : FlatPathTable.CollisionBit | collisionOffsets[hash];
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(off), hash);
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(off + 4), (uint)value);
            off += 8;
        }

        return (table, hasCollision ? collision.ToArray() : null, hasCollision);
    }

    /// <summary>
    /// Encrypt every XTS sector after the plaintext header block, except sectors in <paramref name="skipBlocks"/>
    /// (Python <c>encrypt_image_filesystem</c>).
    /// </summary>
    /// <param name="image">Read/write image stream.</param>
    /// <param name="blockSize">Block size.</param>
    /// <param name="totalBlocks">Image blocks.</param>
    /// <param name="ekpfs">EKPFS key.</param>
    /// <param name="seed">Header seed.</param>
    /// <param name="newCrypt">Alternate key derivation.</param>
    /// <param name="skipBlocks">Blocks kept plaintext.</param>
    public static void EncryptFilesystem(Stream image, int blockSize, long totalBlocks, ReadOnlySpan<byte> ekpfs, ReadOnlySpan<byte> seed, bool newCrypt, IReadOnlySet<long> skipBlocks)
    {
        if (blockSize % PFSConstants.XtsSectorSize != 0)
        {
            throw new BuildException($"block size {blockSize} is not aligned to XTS sector size {PFSConstants.XtsSectorSize}");
        }

        using XtsAes xts = new(PFSKeys.XtsKey(ekpfs, seed, newCrypt));
        byte[] buffer = new byte[blockSize];
        for (long block = 1; block < totalBlocks; block++)
        {
            if (skipBlocks.Contains(block))
            {
                continue;
            }

            long offset = block * blockSize;
            image.Seek(offset, SeekOrigin.Begin);
            image.ReadExactly(buffer);
            for (int s = 0; s < blockSize; s += PFSConstants.XtsSectorSize)
            {
                xts.Encrypt(buffer.AsSpan(s, PFSConstants.XtsSectorSize), (ulong)((offset + s) / PFSConstants.XtsSectorSize));
            }

            image.Seek(offset, SeekOrigin.Begin);
            image.Write(buffer);
        }
    }

    /// <summary>Post-write sanity check (Python <c>validate_image_quick</c>).</summary>
    /// <exception cref="BuildException">A check fails.</exception>
    public static void ValidateQuick(string path, int blockSize, ushort mode, long version, byte[]? ekpfs, bool newCrypt)
    {
        using PFSImage image = PFSImage.Open(path, ekpfs, newCrypt);
        PFSHeader header = image.Header;
        List<PFSInode> inodes = image.ReadInodes();
        if (header.Version != version || header.Magic != PFSConstants.PFSMagic)
        {
            throw new BuildException("Post-write validation failed: invalid header magic/version");
        }

        if (header.BlockSize != blockSize)
        {
            throw new BuildException("Post-write validation failed: unexpected block size");
        }

        if (header.ReadOnly != 1)
        {
            throw new BuildException("Post-write validation failed: header readonly byte is not set");
        }

        if (header.Mode != mode)
        {
            throw new BuildException("Post-write validation failed: unexpected mode flags");
        }

        if (header.InodeCount < 3 || header.InodeBlockCount < 1)
        {
            throw new BuildException("Post-write validation failed: inode table looks invalid");
        }

        bool signed = (mode & PFSConstants.PFSModeSigned) != 0;
        foreach (PFSInode inode in inodes)
        {
            if ((inode.Mode & PFSConstants.InodeModeAnyWrite) != 0)
            {
                throw new BuildException($"Post-write validation failed: inode {inode.Number} has write bits set (mode=0x{inode.Mode:X4})");
            }

            if (!signed && (inode.Flags & PFSConstants.InodeFlagReadOnly) == 0)
            {
                throw new BuildException($"Post-write validation failed: inode {inode.Number} missing readonly flag (flags=0x{inode.Flags:X8})");
            }
        }
    }

    // Python build_inode_block_sig_s64: a DinodeS64 describing the inode table.
    private static void WriteInodeBlockSig(Span<byte> sig, long inodeBlockCount, int blockSize, long now, bool signed)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(sig[0x02..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(sig[0x04..], signed ? 0 : PFSConstants.InodeFlagReadOnly);
        long size = inodeBlockCount * blockSize;
        BinaryPrimitives.WriteInt64LittleEndian(sig[0x08..], size);
        BinaryPrimitives.WriteInt64LittleEndian(sig[0x10..], size);
        for (int i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(sig[(0x18 + (i * 8))..], now);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(sig[0x60..], (uint)inodeBlockCount);
        for (int i = 0; i < 12; i++)
        {
            long block = signed ? (i < inodeBlockCount ? 1 + i : 0) : (i == 0 ? 1 : 0);
            BinaryPrimitives.WriteInt64LittleEndian(sig[(0x68 + (i * 40) + 32)..], block);
        }
    }

    // Python repr() of a str: single quotes unless the text contains ' and no ".
    private static string PythonRepr(string value)
    {
        char quote = value.Contains('\'', StringComparison.Ordinal) && !value.Contains('"', StringComparison.Ordinal) ? '"' : '\'';
        StringBuilder text = new();
        text.Append(quote);
        foreach (char c in value)
        {
            text.Append(c switch
            {
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when c == quote => "\\" + c,
                _ when c < ' ' || c == '\x7f' => $"\\x{(int)c:x2}",
                _ => c.ToString(),
            });
        }

        return text.Append(quote).ToString();
    }
}
