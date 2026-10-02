using System.Buffers.Binary;
using System.Text;

namespace MkPFS.Core.PFS;

/// <summary>Parsed PFS header (block 0, first 0x400 bytes; Python <c>ParsedHeader</c>).</summary>
/// <param name="Version">1 = PS4, 2 = PS5.</param>
/// <param name="Magic">Expected <see cref="PFSConstants.PFSMagic"/>.</param>
/// <param name="Mode">Mode bits (signed, 64-bit inodes, encrypted, case-insensitive).</param>
/// <param name="BlockSize">Filesystem block size.</param>
/// <param name="NBlock">Leading block count.</param>
/// <param name="InodeCount">Inode count.</param>
/// <param name="NDBlock">Total data blocks.</param>
/// <param name="InodeBlockCount">Inode table blocks.</param>
/// <param name="ReadOnly">Read-only byte at 0x1A.</param>
/// <param name="Seed">16-byte seed at 0x370.</param>
public sealed record PFSHeader(
    long Version,
    long Magic,
    ushort Mode,
    uint BlockSize,
    long NBlock,
    long InodeCount,
    long NDBlock,
    long InodeBlockCount,
    byte ReadOnly,
    byte[] Seed)
{
    /// <summary>Bytes read for the header.</summary>
    public const int Size = 0x400;

    /// <summary>Signed image.</summary>
    public bool IsSigned => (Mode & PFSConstants.PFSModeSigned) != 0;

    /// <summary>Encrypted image.</summary>
    public bool IsEncrypted => (Mode & PFSConstants.PFSModeEncrypted) != 0;

    /// <summary>Case-insensitive flat path table.</summary>
    public bool IsCaseInsensitive => (Mode & PFSConstants.PFSModeCaseInsensitive) != 0;

    /// <summary>64-bit inode mode bit.</summary>
    public bool Is64BitInodes => (Mode & PFSConstants.PFSMode64BitInodes) != 0;

    /// <summary>"PS5" for version 2, otherwise "PS4" (Python <c>version_label</c>).</summary>
    public string VersionLabel => Version == PFSConstants.PFSVersionPS5 ? "PS5" : "PS4";

    /// <summary>Parse the first 0x400 bytes of an image.</summary>
    /// <param name="data">At least <see cref="Size"/> bytes.</param>
    /// <returns>Header.</returns>
    public static PFSHeader Parse(ReadOnlySpan<byte> data) => new(
        BinaryPrimitives.ReadInt64LittleEndian(data),
        BinaryPrimitives.ReadInt64LittleEndian(data[0x08..]),
        BinaryPrimitives.ReadUInt16LittleEndian(data[0x1C..]),
        BinaryPrimitives.ReadUInt32LittleEndian(data[0x20..]),
        BinaryPrimitives.ReadInt64LittleEndian(data[0x28..]),
        BinaryPrimitives.ReadInt64LittleEndian(data[0x30..]),
        BinaryPrimitives.ReadInt64LittleEndian(data[0x38..]),
        BinaryPrimitives.ReadInt64LittleEndian(data[0x40..]),
        data[0x1A],
        data.Slice(0x370, 16).ToArray());
}

/// <summary>Signed inode layout (Python <c>SignedInodeLayout</c>).</summary>
/// <param name="InodeSize">Serialized inode size.</param>
/// <param name="EntrySize">Signature + block pointer size.</param>
/// <param name="PointerSize">Block pointer size (4 or 8).</param>
/// <param name="PointerTableOffset">Offset of the db/ib entry table.</param>
public readonly record struct SignedInodeLayout(int InodeSize, int EntrySize, int PointerSize, int PointerTableOffset)
{
    /// <summary>Layout for 32- or 64-bit signed inodes.</summary>
    /// <param name="inodeBits">32 or 64.</param>
    /// <returns>Layout.</returns>
    public static SignedInodeLayout For(int inodeBits) => inodeBits switch
    {
        32 => new SignedInodeLayout(PFSConstants.InodeS32Size, PFSConstants.SigEntryS32Size, 4, 0x64),
        64 => new SignedInodeLayout(PFSConstants.InodeS64Size, PFSConstants.SigEntryS64Size, 8, 0x68),
        _ => throw new ArgumentOutOfRangeException(nameof(inodeBits), $"Unsupported signed inode width: {inodeBits}"),
    };

    /// <summary>Signed inode width from header mode bits.</summary>
    /// <param name="mode">Header mode.</param>
    /// <returns>64 when the 64-bit bit is set, else 32.</returns>
    public static int BitsFromMode(ushort mode) => (mode & PFSConstants.PFSMode64BitInodes) != 0 ? 64 : 32;

    /// <summary>Read the block pointer of an entry.</summary>
    /// <param name="entry">Entry bytes starting at the signature.</param>
    /// <returns>Block number.</returns>
    public long ReadPointer(ReadOnlySpan<byte> entry) => PointerSize == 4
        ? BinaryPrimitives.ReadInt32LittleEndian(entry[PFSConstants.SigSize..])
        : BinaryPrimitives.ReadInt64LittleEndian(entry[PFSConstants.SigSize..]);
}

/// <summary>Parsed inode (Python <c>ParsedInode</c>).</summary>
public sealed class PFSInode
{
    /// <summary>Inode number.</summary>
    public required long Number { get; init; }

    /// <summary>Mode bits.</summary>
    public required ushort Mode { get; init; }

    /// <summary>Link count.</summary>
    public required ushort NLink { get; init; }

    /// <summary>Flags (compressed, readonly, internal).</summary>
    public required uint Flags { get; init; }

    /// <summary>Size field at 0x08 (stored size when compressed).</summary>
    public required long Size { get; init; }

    /// <summary>Size field at 0x10 (logical size when compressed).</summary>
    public required long SizeCompressed { get; init; }

    /// <summary>Allocated blocks.</summary>
    public required uint Blocks { get; init; }

    /// <summary>Direct block pointers.</summary>
    public required long[] Db { get; init; }

    /// <summary>Indirect block pointers.</summary>
    public required long[] Ib { get; init; }

    /// <summary>Direct block signatures (empty for unsigned inodes).</summary>
    public byte[][] DbSig { get; init; } = [];

    /// <summary>Indirect block signatures (empty for unsigned inodes).</summary>
    public byte[][] IbSig { get; init; } = [];

    /// <summary>Directory inode.</summary>
    public bool IsDir => (Mode & PFSConstants.InodeModeDir) != 0;

    /// <summary>File inode.</summary>
    public bool IsFile => (Mode & PFSConstants.InodeModeFile) != 0;

    /// <summary>PFSC-compressed payload.</summary>
    public bool IsCompressed => (Flags & PFSConstants.InodeFlagCompressed) != 0;

    /// <summary>Signed inode (has signature slots).</summary>
    public bool IsSigned => DbSig.Length > 0 || IbSig.Length > 0;

    /// <summary>Bytes stored on disk.</summary>
    public long StoredSize => IsCompressed ? Size : SizeCompressed;

    /// <summary>Logical (decoded) size.</summary>
    public long LogicalSize => IsCompressed ? SizeCompressed : Size;

    /// <summary>Parse one inode (Python <c>parse_image_inode</c>).</summary>
    /// <param name="blob">Inode bytes.</param>
    /// <param name="number">Inode number.</param>
    /// <param name="signed">Signed layout.</param>
    /// <param name="inodeBits">Signed width (32 or 64).</param>
    /// <returns>Parsed inode.</returns>
    public static PFSInode Parse(ReadOnlySpan<byte> blob, long number, bool signed, int inodeBits = 32)
    {
        int expected = signed ? SignedInodeLayout.For(inodeBits).InodeSize : PFSConstants.InodeD32Size;
        if (blob.Length != expected)
        {
            throw new InvalidDataException($"inode blob has invalid size {blob.Length}");
        }

        ushort mode = BinaryPrimitives.ReadUInt16LittleEndian(blob);
        ushort nlink = BinaryPrimitives.ReadUInt16LittleEndian(blob[0x02..]);
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(blob[0x04..]);
        long size = BinaryPrimitives.ReadInt64LittleEndian(blob[0x08..]);
        long sizeCompressed = BinaryPrimitives.ReadInt64LittleEndian(blob[0x10..]);
        uint blocks = BinaryPrimitives.ReadUInt32LittleEndian(blob[0x60..]);
        long[] db = new long[PFSConstants.MaxDirectBlocks];
        long[] ib = new long[PFSConstants.MaxIndirectBlocks];

        if (!signed)
        {
            for (int i = 0; i < db.Length; i++)
            {
                db[i] = BinaryPrimitives.ReadInt32LittleEndian(blob[(0x64 + (i * 4))..]);
            }

            for (int i = 0; i < ib.Length; i++)
            {
                ib[i] = BinaryPrimitives.ReadInt32LittleEndian(blob[(0x94 + (i * 4))..]);
            }

            return new PFSInode { Number = number, Mode = mode, NLink = nlink, Flags = flags, Size = size, SizeCompressed = sizeCompressed, Blocks = blocks, Db = db, Ib = ib };
        }

        SignedInodeLayout layout = SignedInodeLayout.For(inodeBits);
        byte[][] dbSig = new byte[db.Length][];
        byte[][] ibSig = new byte[ib.Length][];
        int offset = layout.PointerTableOffset;
        for (int i = 0; i < db.Length; i++, offset += layout.EntrySize)
        {
            dbSig[i] = blob.Slice(offset, PFSConstants.SigSize).ToArray();
            db[i] = layout.ReadPointer(blob[offset..]);
        }

        for (int i = 0; i < ib.Length; i++, offset += layout.EntrySize)
        {
            ibSig[i] = blob.Slice(offset, PFSConstants.SigSize).ToArray();
            ib[i] = layout.ReadPointer(blob[offset..]);
        }

        return new PFSInode { Number = number, Mode = mode, NLink = nlink, Flags = flags, Size = size, SizeCompressed = sizeCompressed, Blocks = blocks, Db = db, Ib = ib, DbSig = dbSig, IbSig = ibSig };
    }
}

/// <summary>Parsed directory entry (Python <c>ParsedDirent</c>).</summary>
/// <param name="InodeNumber">Target inode.</param>
/// <param name="TypeCode">2 file, 3 dir, 4 dot, 5 dotdot.</param>
/// <param name="Name">Entry name.</param>
public sealed record PFSDirent(long InodeNumber, int TypeCode, string Name)
{
    /// <summary>
    /// Parse a dirent blob (Python <c>parse_image_dirents</c>). Parsing stops at an all-zero entry or the
    /// first malformed one; in strict mode the problem is reported.
    /// </summary>
    /// <param name="blob">Directory payload.</param>
    /// <param name="strict">Report malformed entries.</param>
    /// <returns>Entries and errors.</returns>
    public static (List<PFSDirent> Entries, List<string> Errors) ParseAll(ReadOnlySpan<byte> blob, bool strict = false)
    {
        List<PFSDirent> entries = [];
        List<string> errors = [];
        int offset = 0;
        while (offset + 16 <= blob.Length)
        {
            uint inode = BinaryPrimitives.ReadUInt32LittleEndian(blob[offset..]);
            int type = BinaryPrimitives.ReadInt32LittleEndian(blob[(offset + 4)..]);
            int nameLength = BinaryPrimitives.ReadInt32LittleEndian(blob[(offset + 8)..]);
            int entrySize = BinaryPrimitives.ReadInt32LittleEndian(blob[(offset + 12)..]);
            if (inode == 0 && type == 0 && nameLength == 0 && entrySize == 0)
            {
                break;
            }

            string? problem = null;
            if (entrySize < 17 || entrySize % 8 != 0)
            {
                problem = $"invalid dirent size {entrySize} at offset {offset}";
            }
            else if (nameLength < 0 || nameLength > entrySize - 16)
            {
                problem = $"invalid dirent name length {nameLength} at offset {offset}";
            }
            else if ((long)offset + entrySize > blob.Length)
            {
                problem = $"dirent at offset {offset} exceeds payload boundary";
            }

            if (problem is not null)
            {
                if (strict)
                {
                    errors.Add(problem);
                }

                break;
            }

            ReadOnlySpan<byte> nameBytes = blob.Slice(offset + 16, nameLength);
            bool ascii = System.Text.Ascii.IsValid(nameBytes);
            if (!ascii && strict)
            {
                errors.Add($"non-ascii dirent name at offset {offset}");
            }

            // Python decodes with errors="replace": every non-ASCII byte becomes U+FFFD.
            string name = ascii ? Encoding.ASCII.GetString(nameBytes) : DecodeAsciiReplace(nameBytes);
            entries.Add(new PFSDirent(inode, type, name));
            offset += entrySize;
        }

        return (entries, errors);
    }

    private static string DecodeAsciiReplace(ReadOnlySpan<byte> bytes)
    {
        StringBuilder builder = new(bytes.Length);
        foreach (byte b in bytes)
        {
            builder.Append(b < 0x80 ? (char)b : '�');
        }

        return builder.ToString();
    }
}
