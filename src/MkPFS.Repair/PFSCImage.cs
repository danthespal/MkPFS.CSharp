using System.Buffers.Binary;
using System.Text;
using MkPFS.Core.PFS;
using MkPFS.Core.PFSC;

namespace MkPFS.Repair;

/// <summary>
/// Single-file <c>.ffpfsc</c> opened for repair: an unsigned, unencrypted PS5 PFS whose inode 3 holds one
/// PFSC-compressed nested image (port of GC <c>pfsc_open</c> and <c>read_outer_nested_name</c>).
/// </summary>
public sealed class PFSCImage : IDisposable
{
    /// <summary>Outer PFS and PFSC block size (64 KiB).</summary>
    public const int BlockSize = 65536;

    /// <summary>Unsigned 32-bit inode size.</summary>
    public const int InodeSize = 0xA8;

    /// <summary>PFSC offset table position inside the payload.</summary>
    public const long OffsetTableOffset = 0x400;

    /// <summary>Offset of inode 3 (the nested image) from the start of the image.</summary>
    public const long NestedInodeOffset = BlockSize + (3L * InodeSize);

    private const long VersionPS5 = 2;
    private const long PFSMagic = 20130315;
    private const uint PFSCMagic = 0x43534650;
    private const uint DirentTypeFile = 2;

    private PFSCImage(FileStream stream)
    {
        Stream = stream;
    }

    /// <summary>Open image file.</summary>
    public FileStream Stream { get; }

    /// <summary>Image size at open time.</summary>
    public long OuterSize { get; private set; }

    /// <summary>Absolute offset of the PFSC payload (<c>db[0]</c> of inode 3).</summary>
    public long FileStart { get; private set; }

    /// <summary>Stored payload size (inode 3 <c>size</c>).</summary>
    public long StoredSize { get; private set; }

    /// <summary>Block-rounded logical size from the PFSC header.</summary>
    public long LogicalSize { get; private set; }

    /// <summary>Real nested file size (inode 3 <c>size_compressed</c>).</summary>
    public long NestedSize { get; private set; }

    /// <summary>PFSC header span, which is also the data start.</summary>
    public long HeaderSize { get; private set; }

    /// <summary>Number of PFSC blocks.</summary>
    public long BlockCount { get; private set; }

    /// <summary>Nested file name from the root directory.</summary>
    public string NestedName { get; private set; } = "pfs_image.dat";

    /// <summary>Nested image kind derived from <see cref="NestedName"/>.</summary>
    public PFSCNestedType NestedType => PFSCVHashIdentity.TypeFromName(NestedName);

    /// <summary>FNV-1a 64 of the 0x30-byte PFSC header (GC journal identity).</summary>
    public ulong PFSCHeaderHash { get; private set; }

    /// <summary>FNV-1a 64 of the offset table (GC journal identity).</summary>
    public ulong OffsetTableHash { get; private set; }

    /// <summary>Block start offsets relative to <see cref="FileStart"/>; <c>BlockCount + 1</c> entries.</summary>
    public long[] Offsets { get; private set; } = [];

    /// <summary>Identity used to look up the <c>.vhash</c> sidecar.</summary>
    public PFSCVHashIdentity VHashIdentity => new(LogicalSize, NestedSize, BlockCount, NestedName, NestedType);

    /// <summary>Open and validate an image.</summary>
    /// <param name="path">Image path.</param>
    /// <param name="writable">Open for writing (in-place repair, slack cleanup).</param>
    /// <returns>Opened image.</returns>
    /// <exception cref="InvalidDataException">The image is not a supported single-file <c>.ffpfsc</c>.</exception>
    public static PFSCImage Open(string path, bool writable = false)
    {
        FileStream stream = new(path, FileMode.Open, writable ? FileAccess.ReadWrite : FileAccess.Read, writable ? FileShare.None : FileShare.Read);
        PFSCImage image = new(stream);
        try
        {
            image.Load();
            return image;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Stored length of block <paramref name="index"/> (65536 = raw).</summary>
    /// <param name="index">Block index.</param>
    /// <returns>Span length.</returns>
    public int StoredLength(long index) => (int)(Offsets[index + 1] - Offsets[index]);

    /// <summary>Bytes of block <paramref name="index"/> that belong to the nested file (GC <c>repair_block_compare_size</c>).</summary>
    /// <param name="index">Block index.</param>
    /// <returns>65536, or less for the last block.</returns>
    public int CompareLength(long index) => (int)Math.Clamp(NestedSize - (index * BlockSize), 0, BlockSize);

    /// <summary>Read the stored bytes of a block (GC <c>pfsc_read_stored</c>).</summary>
    /// <param name="index">Block index.</param>
    /// <param name="destination">At least 65536 bytes.</param>
    /// <returns>Stored length.</returns>
    public int ReadStored(long index, Span<byte> destination)
    {
        int length = StoredLength(index);
        if (length == 0)
        {
            throw new InvalidDataException($"empty PFSC block span at block {index}");
        }

        ReadAt(FileStart + Offsets[index], destination[..length]);
        return length;
    }

    /// <summary>Read and decode a block (GC <c>pfsc_decode_block</c>).</summary>
    /// <param name="index">Block index.</param>
    /// <param name="stored">Scratch buffer, at least 65536 bytes.</param>
    /// <param name="decoded">Output, exactly 65536 bytes.</param>
    /// <returns>Stored length.</returns>
    public int DecodeBlock(long index, Span<byte> stored, Span<byte> decoded)
    {
        int length = ReadStored(index, stored);
        PFSCReader.DecodeStoredBlock(stored[..length], decoded[..BlockSize], index);
        return length;
    }

    /// <summary>Read <paramref name="buffer"/>.Length bytes at <paramref name="offset"/>.</summary>
    /// <param name="offset">Absolute offset.</param>
    /// <param name="buffer">Destination.</param>
    public void ReadAt(long offset, Span<byte> buffer) => ReadAt(Stream, offset, buffer);

    /// <inheritdoc />
    public void Dispose() => Stream.Dispose();

    internal static void ReadAt(FileStream stream, long offset, Span<byte> buffer)
    {
        int got = RandomAccess.Read(stream.SafeFileHandle, buffer, offset);
        while (got < buffer.Length)
        {
            int more = RandomAccess.Read(stream.SafeFileHandle, buffer[got..], offset + got);
            if (more == 0)
            {
                throw new InvalidDataException($"image truncated at offset {offset + got}");
            }

            got += more;
        }
    }

    /// <summary>FNV-1a 64 (GC <c>fnv1a64</c>).</summary>
    /// <param name="data">Bytes.</param>
    /// <returns>Hash.</returns>
    public static ulong Fnv1a64(ReadOnlySpan<byte> data)
    {
        ulong hash = 1469598103934665603UL;
        foreach (byte b in data)
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }

        return hash;
    }

    /// <summary>PFSC header span for a block count (GC <c>pfsc_header_span</c>).</summary>
    /// <param name="blockCount">Block count.</param>
    /// <returns>Data start offset.</returns>
    public static long HeaderSpan(long blockCount) => PFSCHeader.HeaderSize(blockCount);

    private void Load()
    {
        OuterSize = Stream.Length;
        byte[] header = new byte[BlockSize];
        ReadAt(0, header);
        if (BinaryPrimitives.ReadInt64LittleEndian(header) != VersionPS5 ||
            BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(0x08)) != PFSMagic ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x20)) != BlockSize)
        {
            throw new InvalidDataException("not a supported outer PFS container");
        }

        // GC reads inodes at fixed 0xA8 strides; signed, encrypted or 64-bit layouts would be misread.
        ushort mode = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x1C));
        if ((mode & (PFSConstants.PFSModeSigned | PFSConstants.PFSModeEncrypted | PFSConstants.PFSMode64BitInodes)) != 0)
        {
            throw new InvalidDataException("repair supports unsigned, unencrypted images with 32-bit inodes only");
        }

        byte[] inode = new byte[InodeSize];
        ReadAt(NestedInodeOffset, inode);
        InodeInfo nested = InodeInfo.Parse(inode);
        if ((nested.Flags & PFSConstants.InodeFlagCompressed) == 0 || nested.Db0 < 0 || nested.Size == 0 || nested.SizeCompressed == 0)
        {
            throw new InvalidDataException("outer PFS does not contain a compressed nested image");
        }

        FileStart = (long)nested.Db0 * BlockSize;
        StoredSize = ToLong(nested.Size, "nested stored size");
        NestedSize = ToLong(nested.SizeCompressed, "nested image size");
        NestedName = ReadNestedName();

        byte[] pfsc = new byte[PFSConstants.PFSCHeaderSize];
        ReadAt(FileStart, pfsc);
        PFSCHeaderHash = Fnv1a64(pfsc);
        if (BinaryPrimitives.ReadUInt32LittleEndian(pfsc) != PFSCMagic ||
            BinaryPrimitives.ReadUInt32LittleEndian(pfsc.AsSpan(0x04)) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(pfsc.AsSpan(0x08)) != 6 ||
            BinaryPrimitives.ReadUInt32LittleEndian(pfsc.AsSpan(0x0C)) != BlockSize ||
            BinaryPrimitives.ReadUInt64LittleEndian(pfsc.AsSpan(0x10)) != BlockSize)
        {
            throw new InvalidDataException("invalid PFSC header");
        }

        ulong tableOffset = BinaryPrimitives.ReadUInt64LittleEndian(pfsc.AsSpan(0x18));
        ulong dataOffset = BinaryPrimitives.ReadUInt64LittleEndian(pfsc.AsSpan(0x20));
        ulong logical = BinaryPrimitives.ReadUInt64LittleEndian(pfsc.AsSpan(0x28));
        if (tableOffset != OffsetTableOffset || dataOffset < 0x10000 || logical == 0 || logical % BlockSize != 0 || logical > long.MaxValue)
        {
            throw new InvalidDataException("unsupported PFSC layout");
        }

        LogicalSize = (long)logical;
        BlockCount = LogicalSize / BlockSize;
        if (NestedSize > LogicalSize)
        {
            throw new InvalidDataException("invalid nested image logical size");
        }

        if ((ulong)HeaderSpan(BlockCount) != dataOffset)
        {
            throw new InvalidDataException("unsupported PFSC data offset");
        }

        long tableSize = (BlockCount + 1) * PFSConstants.PFSCOffsetEntrySize;
        if (OffsetTableOffset + tableSize > (long)dataOffset || (long)dataOffset > StoredSize || tableSize > int.MaxValue ||
            FileStart + StoredSize > OuterSize)
        {
            throw new InvalidDataException("invalid PFSC offset table");
        }

        HeaderSize = (long)dataOffset;
        byte[] table = new byte[tableSize];
        ReadAt(FileStart + OffsetTableOffset, table);
        OffsetTableHash = Fnv1a64(table);
        long[] offsets = new long[BlockCount + 1];
        for (long i = 0; i <= BlockCount; i++)
        {
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(table.AsSpan((int)(i * 8)));
            offsets[i] = value > long.MaxValue ? long.MaxValue : (long)value;
            if (i > 0 && offsets[i] < offsets[i - 1])
            {
                throw new InvalidDataException("PFSC offsets are not monotonic");
            }

            if (i > 0 && offsets[i] - offsets[i - 1] > BlockSize)
            {
                throw new InvalidDataException($"invalid PFSC block span at block {i - 1}");
            }
        }

        if (offsets[0] != HeaderSize || offsets[BlockCount] > StoredSize)
        {
            throw new InvalidDataException("PFSC offsets exceed stored size");
        }

        Offsets = offsets;
    }

    private string ReadNestedName()
    {
        byte[] inode = new byte[InodeSize];
        ReadAt(BlockSize + (2L * InodeSize), inode);
        InodeInfo root = InodeInfo.Parse(inode);
        if ((root.Mode & PFSConstants.InodeModeDir) == 0 || root.Db0 < 0 || root.Size == 0 || root.Size > BlockSize)
        {
            throw new InvalidDataException("outer PFS root directory is invalid");
        }

        byte[] dir = new byte[(int)root.Size];
        ReadAt((long)root.Db0 * BlockSize, dir);
        for (int off = 0; off + 16 <= dir.Length;)
        {
            uint child = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(off));
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(off + 4));
            uint nameLength = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(off + 8));
            uint entrySize = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(off + 12));
            if (entrySize == 0)
            {
                break;
            }

            if (entrySize < 16 || entrySize > BlockSize || nameLength == 0 || nameLength >= 256 ||
                16 + nameLength > entrySize || off + entrySize > dir.Length)
            {
                throw new InvalidDataException("outer PFS root directory entry is invalid");
            }

            if (child == 3 && type == DirentTypeFile)
            {
                string name = Encoding.UTF8.GetString(dir, off + 16, (int)nameLength);
                return IsSupportedSegment(name) ? name : throw new InvalidDataException("nested image name is unsupported");
            }

            off += (int)entrySize;
        }

        throw new InvalidDataException("outer PFS nested image entry was not found");
    }

    // GC path_segment_supported: no empty, ".", "..", or names with '/' or NUL.
    internal static bool IsSupportedSegment(string name) =>
        name.Length > 0 && name != "." && name != ".." && name.IndexOfAny(['/', '\0']) < 0;

    private static long ToLong(ulong value, string what) =>
        value <= long.MaxValue ? (long)value : throw new InvalidDataException($"{what} is out of range");

    internal readonly record struct InodeInfo(ushort Mode, ushort Nlink, uint Flags, ulong Size, ulong SizeCompressed, uint Blocks, int Db0)
    {
        public static InodeInfo Parse(ReadOnlySpan<byte> data) => new(
            BinaryPrimitives.ReadUInt16LittleEndian(data),
            BinaryPrimitives.ReadUInt16LittleEndian(data[0x02..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[0x04..]),
            BinaryPrimitives.ReadUInt64LittleEndian(data[0x08..]),
            BinaryPrimitives.ReadUInt64LittleEndian(data[0x10..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[0x60..]),
            BinaryPrimitives.ReadInt32LittleEndian(data[0x64..]));
    }
}
