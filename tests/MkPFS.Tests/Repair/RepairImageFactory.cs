using System.Buffers.Binary;
using System.Text;
using MkPFS.Core.Compression;
using MkPFS.Core.PFSC;

namespace MkPFS.Tests.Repair;

/// <summary>
/// Builds single-file <c>.ffpfsc</c> images with the same fixed wrapper layout as Python <c>pack file</c>
/// (header, 4 inodes, superroot, flat_path_table, collision block, uroot, payload at block 6), with full control
/// over each stored PFSC block.
/// </summary>
internal static class RepairImageFactory
{
    public const int BlockSize = 65536;
    private const int InodeSize = 0xA8;

    /// <summary>Compressible block content (zlib keeps it well under 64 KiB).</summary>
    public static byte[] Text(int seed, int length = BlockSize)
    {
        byte[] data = new byte[length];
        Random random = new(seed);
        string[] words = ["alpha ", "beta ", "gamma ", "delta ", "pfs ", "block ", "\n"];
        for (int i = 0; i < length;)
        {
            foreach (byte b in Encoding.ASCII.GetBytes(words[random.Next(words.Length)]))
            {
                if (i < length)
                {
                    data[i++] = b;
                }
            }
        }

        return data;
    }

    /// <summary>Incompressible block content.</summary>
    public static byte[] Noise(int seed)
    {
        byte[] data = new byte[BlockSize];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>Normal zlib 1.3.1 block.</summary>
    public static byte[] ZlibBlock(byte[] raw) => Zlib.Compress(raw);

    /// <summary>
    /// ISA-L style block: a stored 32 KiB half, then a fixed-Huffman block that copies it with matches at
    /// distance 32768, beyond the 32506 that zlib emits. Decodes to <c>half ‖ half</c>.
    /// </summary>
    public static (byte[] Stored, byte[] Decoded) FarDistance(byte[] half)
    {
        if (half.Length != 32768)
        {
            throw new ArgumentException("half must be 32768 bytes", nameof(half));
        }

        List<byte> stream = [0x78, 0x01, 0x00, 0x00, 0x80, 0xFF, 0x7F];
        stream.AddRange(half);
        BitWriter bits = new();
        bits.Write(1, 1);
        bits.Write(1, 2);

        // 126 x 258 + 257 + 3 = 32768 bytes of matches.
        for (int i = 0; i < 126; i++)
        {
            bits.WriteCode(0b11000101, 8); // symbol 285: length 258
            Distance32768(bits);
        }

        bits.WriteCode(0b11000100, 8); // symbol 284: base 227, 5 extra bits
        bits.Write(257 - 227, 5);
        Distance32768(bits);
        bits.WriteCode(0b0000001, 7); // symbol 257: length 3
        Distance32768(bits);
        bits.WriteCode(0, 7); // end of block
        stream.AddRange(bits.Flush());

        byte[] decoded = [.. half, .. half];
        uint adler = Adler32(decoded);
        stream.AddRange([(byte)(adler >> 24), (byte)(adler >> 16), (byte)(adler >> 8), (byte)adler]);
        return ([.. stream], decoded);
    }

    /// <summary>Build an image from stored blocks (65536 bytes = raw, shorter = zlib stream).</summary>
    public static byte[] BuildImage(IReadOnlyList<byte[]> storedBlocks, long nestedSize, string nestedName = "in.exfat")
    {
        long count = storedBlocks.Count;
        long headerSize = PFSCHeader.HeaderSize(count);
        long[] offsets = new long[count + 1];
        offsets[0] = headerSize;
        for (int i = 0; i < count; i++)
        {
            offsets[i + 1] = offsets[i] + storedBlocks[i].Length;
        }

        long storedSize = offsets[^1];
        long fileBlocks = (storedSize + BlockSize - 1) / BlockSize;
        long finalBlocks = 6 + fileBlocks;
        byte[] image = new byte[finalBlocks * BlockSize];

        Span<byte> header = image;
        BinaryPrimitives.WriteInt64LittleEndian(header, 2);
        BinaryPrimitives.WriteInt64LittleEndian(header[0x08..], 20130315);
        header[0x1A] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x1C..], 0x8);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x20..], BlockSize);
        BinaryPrimitives.WriteInt64LittleEndian(header[0x28..], 1);
        BinaryPrimitives.WriteInt64LittleEndian(header[0x30..], 4);
        BinaryPrimitives.WriteInt64LittleEndian(header[0x38..], finalBlocks);
        BinaryPrimitives.WriteInt64LittleEndian(header[0x40..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x368..], 1);

        Inode(image, 0, 0x416D, 1, 0x20010, BlockSize, BlockSize, 1, 2);
        Inode(image, 1, 0x816D, 1, 0x20010, 8, 8, 1, 3);
        Inode(image, 2, 0x416D, 3, 0x10, BlockSize, BlockSize, 1, 5);
        Inode(image, 3, 0x816D, 1, 0x11, storedSize, nestedSize, (uint)fileBlocks, 6);

        int off = 2 * BlockSize;
        off = Dirent(image, off, 1, 2, "flat_path_table");
        Dirent(image, off, 2, 3, "uroot");
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(3 * BlockSize), HashPath("/" + nestedName));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan((3 * BlockSize) + 4), 3);
        off = 5 * BlockSize;
        off = Dirent(image, off, 2, 4, ".");
        off = Dirent(image, off, 2, 5, "..");
        Dirent(image, off, 3, 2, nestedName);

        int payload = 6 * BlockSize;
        PFSCHeader.ForBlocks(count).Write(image.AsSpan(payload));
        for (int i = 0; i <= count; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(payload + 0x400 + (i * 8)), offsets[i]);
        }

        for (int i = 0; i < count; i++)
        {
            storedBlocks[i].CopyTo(image.AsSpan(payload + (int)offsets[i]));
        }

        return image;
    }

    /// <summary>Decode the nested payload of an image file (block-rounded).</summary>
    public static byte[] DecodePayload(string path)
    {
        byte[] image = File.ReadAllBytes(path);
        long stored = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan((BlockSize + (3 * InodeSize)) + 8));
        return PFSCReader.DecodePayload(image.AsSpan(6 * BlockSize, (int)stored).ToArray());
    }

    private static void Inode(byte[] image, int index, ushort mode, ushort nlink, uint flags, long size, long sizeCompressed, uint blocks, int db0)
    {
        Span<byte> inode = image.AsSpan(BlockSize + (index * InodeSize), InodeSize);
        BinaryPrimitives.WriteUInt16LittleEndian(inode, mode);
        BinaryPrimitives.WriteUInt16LittleEndian(inode[0x02..], nlink);
        BinaryPrimitives.WriteUInt32LittleEndian(inode[0x04..], flags);
        BinaryPrimitives.WriteInt64LittleEndian(inode[0x08..], size);
        BinaryPrimitives.WriteInt64LittleEndian(inode[0x10..], sizeCompressed);
        BinaryPrimitives.WriteUInt32LittleEndian(inode[0x60..], blocks);
        BinaryPrimitives.WriteInt32LittleEndian(inode[0x64..], db0);
        for (int i = 1; i < 12; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(inode[(0x64 + (i * 4))..], -1);
        }
    }

    private static int Dirent(byte[] image, int off, uint inode, uint type, string name)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(name);
        int span = (bytes.Length + 17 + 7) & ~7;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off), inode);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 4), type);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 8), (uint)bytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 12), (uint)span);
        bytes.CopyTo(image, off + 16);
        return off + span;
    }

    private static uint HashPath(string path)
    {
        uint hash = 0;
        foreach (char c in path)
        {
            hash = unchecked(char.ToUpperInvariant(c) + (31 * hash));
        }

        return hash;
    }

    private static void Distance32768(BitWriter bits)
    {
        bits.WriteCode(29, 5);
        bits.Write(32768 - 24577, 13);
    }

    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        uint a = 1, b = 0;
        foreach (byte value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _buffer;
        private int _count;

        public void Write(int value, int bits)
        {
            for (int i = 0; i < bits; i++)
            {
                Push((value >> i) & 1);
            }
        }

        public void WriteCode(int code, int bits)
        {
            for (int i = bits - 1; i >= 0; i--)
            {
                Push((code >> i) & 1);
            }
        }

        public byte[] Flush()
        {
            if (_count > 0)
            {
                _bytes.Add((byte)_buffer);
            }

            return [.. _bytes];
        }

        private void Push(int bit)
        {
            _buffer |= bit << _count;
            if (++_count == 8)
            {
                _bytes.Add((byte)_buffer);
                _buffer = 0;
                _count = 0;
            }
        }
    }
}
