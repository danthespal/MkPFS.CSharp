using System.Buffers.Binary;
using System.Text;
using MkPFS.Core.PFS;

namespace MkPFS.Repair;

/// <summary>Outcome of <see cref="OuterSlack.Clean"/>.</summary>
/// <param name="Applicable">The image has the fixed single-file wrapper layout.</param>
/// <param name="Reason">Why cleanup was skipped, when not applicable.</param>
/// <param name="FixedBytes">Non-zero slack bytes that were cleared.</param>
public readonly record struct OuterSlackResult(bool Applicable, string? Reason, long FixedBytes);

/// <summary>
/// Zeroes unused bytes in the fixed single-file wrapper: superroot, flat_path_table, collision and uroot
/// block tails, and the image tail after the payload (port of GC <c>pfs_repair_ffpfsc_outer_slack</c>).
/// </summary>
public static class OuterSlack
{
    private const int BlockSize = PFSCImage.BlockSize;
    private const ushort RxOnly = 0x16D;
    private const ushort RwxAll = 0x1FF;
    private const uint DirentFile = 2;
    private const uint DirentDirectory = 3;
    private const uint DirentDot = 4;
    private const uint DirentDotDot = 5;

    /// <summary>Clean the slack of an image, or report why its layout is not the fixed wrapper.</summary>
    /// <param name="path">Image path.</param>
    /// <param name="dryRun">Only count the bytes that would be cleared.</param>
    /// <returns>Result.</returns>
    public static OuterSlackResult Clean(string path, bool dryRun = false)
    {
        PFSCImage image;
        try
        {
            image = PFSCImage.Open(path, writable: !dryRun);
        }
        catch (InvalidDataException ex)
        {
            return new OuterSlackResult(false, ex.Message, 0);
        }

        using (image)
        {
            if (image.FileStart != 6L * BlockSize)
            {
                return new OuterSlackResult(false, "outer PFS nested image offset is not the fixed wrapper layout", 0);
            }

            if (image.OuterSize % BlockSize != 0)
            {
                return new OuterSlackResult(false, "outer PFS image size is not fixed-wrapper compatible", 0);
            }

            if (VerifyWrapper(image, out int superrootUsed, out int rootUsed) is { } reason)
            {
                return new OuterSlackResult(false, reason, 0);
            }

            long tail = image.FileStart + image.StoredSize;
            (long Offset, long Size)[] ranges =
            [
                ((2L * BlockSize) + superrootUsed, BlockSize - superrootUsed),
                ((3L * BlockSize) + 8, BlockSize - 8),
                (4L * BlockSize, BlockSize),
                ((5L * BlockSize) + rootUsed, BlockSize - rootUsed),
                (tail, image.OuterSize - tail),
            ];

            long fixedBytes = 0;
            byte[] buffer = new byte[BlockSize];
            foreach ((long offset, long size) in ranges)
            {
                for (long done = 0; done < size;)
                {
                    int chunk = (int)Math.Min(BlockSize, size - done);
                    Span<byte> span = buffer.AsSpan(0, chunk);
                    image.ReadAt(offset + done, span);
                    int nonZero = chunk - span.Count((byte)0);
                    if (nonZero > 0)
                    {
                        fixedBytes += nonZero;
                        if (!dryRun)
                        {
                            span.Clear();
                            RandomAccess.Write(image.Stream.SafeFileHandle, span, offset + done);
                        }
                    }

                    done += chunk;
                }
            }

            if (!dryRun && fixedBytes > 0)
            {
                image.Stream.Flush(flushToDisk: true);
            }

            return new OuterSlackResult(true, null, fixedBytes);
        }
    }

    // GC outer_verify_gamecompressor_wrapper; also accepts the r-x inode modes MkPFS writes.
    private static string? VerifyWrapper(PFSCImage image, out int superrootUsed, out int rootUsed)
    {
        superrootUsed = 0;
        rootUsed = 0;
        byte[] header = new byte[BlockSize];
        byte[] inodes = new byte[BlockSize];
        image.ReadAt(0, header);
        image.ReadAt(BlockSize, inodes);
        long finalBlocks = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(0x38));
        if (header[0x1A] != 1 ||
            BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x1C)) != PFSConstants.PFSModeCaseInsensitive ||
            BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(0x28)) != 1 ||
            BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(0x30)) != 4 ||
            BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(0x40)) != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x368)) != 1 ||
            finalBlocks <= 0 || finalBlocks > long.MaxValue / BlockSize || finalBlocks * BlockSize != image.OuterSize)
        {
            return "not a fixed single-file .ffpfsc wrapper";
        }

        long fileBlocks = (image.StoredSize + BlockSize - 1) / BlockSize;
        if (fileBlocks == 0 || fileBlocks > uint.MaxValue)
        {
            return "outer PFS nested file block count is not fixed-wrapper compatible";
        }

        PFSCImage.InodeInfo[] ino = new PFSCImage.InodeInfo[4];
        for (int i = 0; i < 4; i++)
        {
            ino[i] = PFSCImage.InodeInfo.Parse(inodes.AsSpan(i * PFSCImage.InodeSize));
        }

        const uint internalReadOnly = PFSConstants.InodeFlagInternal | PFSConstants.InodeFlagReadOnly;
        if (!InodeMatches(ino[0], PFSConstants.InodeModeDir, 1, internalReadOnly, BlockSize, BlockSize, 1, 2) ||
            !InodeMatches(ino[1], PFSConstants.InodeModeFile, 1, internalReadOnly, 8, 8, 1, 3) ||
            !InodeMatches(ino[2], PFSConstants.InodeModeDir, 3, PFSConstants.InodeFlagReadOnly, BlockSize, BlockSize, 1, 5) ||
            !InodeMatches(ino[3], PFSConstants.InodeModeFile, 1, PFSConstants.InodeFlagReadOnly | PFSConstants.InodeFlagCompressed,
                (ulong)image.StoredSize, (ulong)image.NestedSize, (uint)fileBlocks, 6))
        {
            return "outer PFS inodes do not match the fixed wrapper";
        }

        byte[] fpt = new byte[8];
        image.ReadAt(3L * BlockSize, fpt);
        if (BinaryPrimitives.ReadUInt32LittleEndian(fpt) != HashPath("/" + image.NestedName) ||
            BinaryPrimitives.ReadUInt32LittleEndian(fpt.AsSpan(4)) != 3)
        {
            return "outer PFS flat path table does not match the fixed wrapper";
        }

        byte[] block = new byte[BlockSize];
        image.ReadAt(2L * BlockSize, block);
        int off = 0;
        if ((MatchDirent(block, ref off, 1, DirentFile, "flat_path_table") ??
             MatchDirent(block, ref off, 2, DirentDirectory, "uroot") ??
             NoExtraDirent(block, off)) is { } superrootError)
        {
            return superrootError;
        }

        superrootUsed = off;
        image.ReadAt(5L * BlockSize, block);
        off = 0;
        if ((MatchDirent(block, ref off, 2, DirentDot, ".") ??
             MatchDirent(block, ref off, 2, DirentDotDot, "..") ??
             MatchDirent(block, ref off, 3, DirentFile, image.NestedName) ??
             NoExtraDirent(block, off)) is { } rootError)
        {
            return rootError;
        }

        rootUsed = off;
        return null;
    }

    private static bool InodeMatches(PFSCImage.InodeInfo inode, ushort type, ushort nlink, uint flags, ulong size, ulong sizeCompressed, uint blocks, int db0) =>
        (inode.Mode == (type | RwxAll) || inode.Mode == (type | RxOnly)) &&
        inode.Nlink == nlink && inode.Flags == flags && inode.Size == size &&
        inode.SizeCompressed == sizeCompressed && inode.Blocks == blocks && inode.Db0 == db0;

    // GC outer_pfs_hash_path: ASCII-uppercase then h = c + 31 * h.
    private static uint HashPath(string path)
    {
        uint hash = 0;
        foreach (byte b in Encoding.UTF8.GetBytes(path))
        {
            uint c = b is >= (byte)'a' and <= (byte)'z' ? (uint)(b - 32) : b;
            hash = unchecked(c + (31 * hash));
        }

        return hash;
    }

    private static int DirentSpan(int nameLength) => (nameLength + 17 + 7) & ~7;

    private static string? MatchDirent(byte[] block, ref int off, uint inode, uint type, string name)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        int span = DirentSpan(nameBytes.Length);
        if (off > BlockSize || span > BlockSize - off)
        {
            return "outer PFS directory entry is outside the fixed wrapper";
        }

        ReadOnlySpan<byte> entry = block.AsSpan(off, span);
        if (BinaryPrimitives.ReadUInt32LittleEndian(entry) != inode ||
            BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]) != type ||
            BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]) != (uint)nameBytes.Length ||
            BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]) != (uint)span ||
            !entry.Slice(16, nameBytes.Length).SequenceEqual(nameBytes))
        {
            return "outer PFS directory does not match the fixed wrapper";
        }

        if (entry[(16 + nameBytes.Length)..].ContainsAnyExcept((byte)0))
        {
            return "outer PFS directory entry padding is not zero";
        }

        off += span;
        return null;
    }

    // GC outer_tail_has_valid_dirent: a plausible entry right after the expected ones means extra files.
    private static string? NoExtraDirent(byte[] block, int off)
    {
        if (off + 16 > BlockSize)
        {
            return null;
        }

        uint child = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(off));
        uint type = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(off + 4));
        uint nameLength = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(off + 8));
        uint entrySize = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(off + 12));
        if (entrySize < 16 || entrySize > BlockSize - off || entrySize % 8 != 0 || nameLength == 0 || nameLength >= 256 ||
            16 + nameLength > entrySize || child > 3)
        {
            return null;
        }

        string name = Encoding.UTF8.GetString(block, off + 16, (int)nameLength);
        bool valid = type switch
        {
            DirentDot => child == 2 && name == ".",
            DirentDotDot => child == 2 && name == "..",
            DirentFile or DirentDirectory => PFSCImage.IsSupportedSegment(name),
            _ => false,
        };
        return valid ? "outer PFS directory has entries outside the fixed wrapper" : null;
    }
}
