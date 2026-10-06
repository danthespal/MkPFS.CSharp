// Ported from LibProsperoPkg (SvenGDK, GPL-3.0-or-later) PFS/Compression/ProsperoCompressedPfsFileWriter.cs at commit 748eabf
// for byte parity with its packages (FPKG plan F4b). See NOTICE.
// LibProsperoPkg - A library for building and inspecting PS5 packages.
// Copyright (C) 2026 SvenGDK
//
// Writer for the PS5 PFSv3 (and PFSv2) compression file format — the "PFSC" container.
// Each block is Kraken-compressed with OodleKrakenEncoder; blocks that do not compress — or cannot be
// expressed without the encoder's length escapes — fall back to stored (uncompressed) blocks
// (isBlockCompressed = 0). Both paths round-trip exactly through the decoder.
//
// The writer emits the 32-byte header, the 7-entry section directory (8-byte aligned, data padded to
// 0x400), the id=1/id=2 constant sections, the bit-packed block-boundary table (id=3) with its
// stored-block flags (0xCC/0x0C), single-chunk compressed flag (0x06), two-chunk compressed flag
// (0x26) and saturating size hint, the per-block SHA3-256 hash table (id=4), and the SHA3-256 file
// digest at 0x28. Compressed output is validated by decompressing it and comparing the result
// against the original input.
#nullable enable
using MkPFS.Core.Compression.Kraken;
using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading.Tasks;

namespace MkPFS.Core.Compression.Kraken;

/// <summary>
/// Produces a valid PS5 PFSv3/PFSv2 compression container ("PFSC"). Each block is
/// Kraken-compressed with <see cref="Oodle.OodleKrakenEncoder"/>; incompressible blocks are
/// stored uncompressed. Both paths round-trip exactly through the decoder.
/// </summary>
public static class ProsperoCompressedPfsFileWriter
{
    /// <summary>The default logical block size for PS5 v3 containers (256 KiB).</summary>
    public const int DefaultBlockSize = 0x40000;

    private const int HeaderSize = 0x48;
    private const int DirectoryEntrySize = 16;
    private const int SectionCount = 7;
    private const int SectionAlignment = 8;
    private const int DataAlignment = 0x400;

    private const uint Magic = 0x43534650;     // 'PFSC'
    private const uint EncodeParam0C = 0x0802;  // constant for v3 / Kraken / 256 KiB / window 18

    // Boundary-table flag byte (id=3, bits 48..55 of the first u64). Two independent 4-bit groups,
    // one per sub-chunk: bit 0 literal model (set = sub/delta), bit 1 LZ enable (clear = one entropy
    // array), bit 2 restart (decode at output position 0), bit 3 verbatim copy. Bits 4..7 are the same
    // four for sub-chunk 1, and the whole high group is zero when the block has one sub-chunk.
    private const ulong ChunkRawCopy = 0x0C;         // bits 2,3 — verbatim sub-chunk 0 (restart + copy)
    private const ulong ChunkRawCopyHigh = 0xC0;     // bits 6,7 — verbatim sub-chunk 1

    /// <summary>
    /// The flag nibble a sub-chunk contributes. Sub-chunk 0 always restarts the output window, so its
    /// nibble carries bit 2; an LZ sub-chunk 1 continues the same window and does not. A verbatim or
    /// entropy-array sub-chunk ignores the window origin, so it restarts in either position.
    /// </summary>
    private static ulong FormNibble(KrakenSubChunkForm form, int literalMode, bool first) => form switch
    {
        KrakenSubChunkForm.Verbatim => 0xC,
        KrakenSubChunkForm.BareEntropy => 0x4,
        _ => (first ? 0x6UL : 0x2UL) | (literalMode == 0 ? 1UL : 0UL),
    };
    private const ulong BoundaryShuffleShift = 44;
    private const ulong BoundaryFlagShift = 48;
    private const ulong SizeHintShift = 44;
    private const ulong SizeHintMask = 0x1FFFF;      // 17-bit (sub-chunk 0 compressed length - 1) hint

    /// <summary>The uncompressed size of one Kraken sub-chunk; a larger block is split into two.</summary>
    private const int SubChunkSize = 0x20000;

    /// <summary>Blocks of at most this many bytes are never compressed.</summary>
    private const int MinCompressibleBlock = 256;

    /// <summary>
    /// The compress-vs-store decision. The size compared is the codec stream's own size, which carries
    /// framing the container payload does not: five bytes per block plus three bytes per sub-chunk.
    /// A block is kept compressed only when that size is at most <c>uncompressedSize * 15 / 16</c>.
    /// </summary>
    private static bool KeepCompressed(int payloadSize, int uncompressedSize)
    {
        if (uncompressedSize <= MinCompressibleBlock)
            return false;
        int subChunks = uncompressedSize > SubChunkSize ? 2 : 1;
        long codecStreamSize = (long)payloadSize + 5 + 3 * subChunks;
        return codecStreamSize <= ((long)uncompressedSize * 15) >> 4;
    }

    // id=5 signature window: the first field covers at most this many bytes of a block.
    private const int SignatureWindow = 0x10000;

    // The whole-block field stops after this many bytes. No block the format allows reaches it.
    private const int SignatureLengthCap = 0x1000000;

    /// <summary>
    /// Writes the block's 16-byte id=5 signature: two running-sum pairs over the block's uncompressed
    /// bytes. The first pair covers the leading <see cref="SignatureWindow"/> bytes with the window
    /// zero-extended to its full length, packed as <c>sum | (weighted &lt;&lt; 25)</c> in wrapping 64-bit
    /// arithmetic (the windowed sum never exceeds 25 bits, so the fields do not overlap). The second pair
    /// covers the whole block and packs the weighted sum in the high 32 bits and the plain sum in the low
    /// 32 bits. Both are independent of the block index, offsets, flags and compression outcome.
    /// </summary>
    private static void WriteBlockSignature(Span<byte> destination, ReadOnlySpan<byte> block)
    {
        int whole = block.Length < SignatureLengthCap ? block.Length : SignatureLengthCap;
        ulong wholeSum = 0;
        ulong wholeWeighted = (ulong)whole;
        for (int i = 0; i < whole; i++)
        {
            wholeSum += block[i];
            wholeWeighted += wholeSum;
        }

        int window = block.Length < SignatureWindow ? block.Length : SignatureWindow;
        ulong windowSum = 0;
        ulong windowWeighted = 0;
        for (int i = 0; i < window; i++)
        {
            windowSum += block[i];
            windowWeighted += windowSum;
        }
        if (block.Length < SignatureWindow)
            windowWeighted += windowSum * (ulong)(SignatureWindow - block.Length);

        BinaryPrimitives.WriteUInt64LittleEndian(destination, (windowWeighted << 25) ^ windowSum);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..],
            ((wholeWeighted & 0xFFFFFFFFUL) << 32) | (wholeSum & 0xFFFFFFFFUL));
    }

    // id=1: 20-byte git hash (a fixed constant — SHA-1 of the encoder source revision).
    private static readonly byte[] GitHash =
    [
        0x23, 0x98, 0x7d, 0x16, 0xc9, 0x20, 0x9a, 0xc7, 0x28, 0x37,
        0x19, 0x32, 0x7e, 0x0f, 0x50, 0x6b, 0xbc, 0xf4, 0x59, 0xf4,
    ];

    // id=2: 64-byte shuffle-pattern field-width table (constant; eight 8-byte field-width rows). The
    // default format uses shuffle NONE.
    private static readonly byte[] ShuffleTable =
    [
        0x04, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x02, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x01, 0x01, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01,
        0x08, 0x02, 0x02, 0x04, 0x00, 0x00, 0x00, 0x00, 0x01, 0x01, 0x06, 0x02, 0x02, 0x04, 0x00, 0x00,
        0x01, 0x01, 0x06, 0x01, 0x01, 0x06, 0x00, 0x00, 0x04, 0x04, 0x04, 0x04, 0x00, 0x00, 0x00, 0x00,
    ];

    /// <summary>
    /// Builds a stored (uncompressed) PFSv3 compression container for <paramref name="payload"/> and
    /// returns the complete container bytes.
    /// </summary>
    /// <param name="payload">The uncompressed data to wrap (the logical file the container expands to).</param>
    /// <param name="level">The Kraken level recorded in the header (default 7). It has no effect
    /// on stored data but is preserved because it contributes to the file digest.</param>
    /// <param name="blockSize">The logical block size (default 256 KiB). Must be positive.</param>
    /// <returns>The serialized PFSv3 container.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="payload"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="blockSize"/> is not positive.</exception>
    /// <exception cref="PlatformNotSupportedException">SHA3-256 is unavailable on this host.</exception>
    public static byte[] WriteStored(ReadOnlySpan<byte> payload, int level = 7, int blockSize = DefaultBlockSize)
    {
        using var source = new MemoryStream(payload.ToArray(), writable: false);
        using var destination = new MemoryStream();
        Write(destination, source, payload.Length, level, blockSize, useHuffmanArrays: false,
            compress: false, maxDegreeOfParallelism: 1);
        return destination.ToArray();
    }

    /// <summary>
    /// Builds a stored PFSv3 container for <paramref name="payload"/> and writes it to
    /// <paramref name="destination"/>.
    /// </summary>
    public static void WriteStored(Stream destination, ReadOnlySpan<byte> payload, int level = 7, int blockSize = DefaultBlockSize)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(WriteStored(payload, level, blockSize));
    }

    /// <summary>
    /// Streams a stored PFSv3 container for <paramref name="sourceLength"/> bytes read from
    /// <paramref name="source"/> into <paramref name="destination"/>, which must be seekable and
    /// writable. Memory use is independent of the input length.
    /// </summary>
    public static void WriteStored(Stream destination, Stream source, long sourceLength,
        int level = 7, int blockSize = DefaultBlockSize)
        => Write(destination, source, sourceLength, level, blockSize, useHuffmanArrays: false,
            compress: false, maxDegreeOfParallelism: 1);

    /// <summary>
    /// Builds a Kraken-compressed PFSv3 container for <paramref name="payload"/>. Each logical block is
    /// compressed; blocks that do not compress far enough are written stored. The result round-trips
    /// exactly through the decoder.
    /// </summary>
    /// <param name="payload">The uncompressed data to wrap.</param>
    /// <param name="level">The Kraken level recorded in the header (default 7).</param>
    /// <param name="blockSize">The logical block size (default 256 KiB). Must be positive.</param>
    /// <param name="useHuffmanArrays">When true (the default) the streams inside a coded sub-chunk are
    /// entropy coded wherever that wins the array score.</param>
    /// <returns>The serialized PFSv3 container.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="blockSize"/> is not positive.</exception>
    /// <exception cref="NotSupportedException">The container would exceed the largest array this runtime
    /// can allocate. Use the <see cref="Stream"/> overload.</exception>
    /// <exception cref="PlatformNotSupportedException">SHA3-256 is unavailable on this host.</exception>
    public static byte[] WriteCompressed(ReadOnlySpan<byte> payload, int level = 7, int blockSize = DefaultBlockSize, bool useHuffmanArrays = true)
    {
        using var source = new MemoryStream(payload.ToArray(), writable: false);
        using var destination = new MemoryStream();
        Write(destination, source, payload.Length, level, blockSize, useHuffmanArrays,
            compress: true, maxDegreeOfParallelism: -1);
        return destination.ToArray();
    }

    /// <summary>
    /// Streams a Kraken-compressed PFSv3 container for <paramref name="sourceLength"/> bytes read from
    /// <paramref name="source"/> into <paramref name="destination"/>. The output is byte for byte what
    /// the array overload produces, and memory use depends on the block size and the degree of
    /// parallelism rather than on the input length, so an input of any size the format allows can be
    /// packaged.
    /// </summary>
    /// <param name="destination">Seekable, writable. The container is written at its current position.</param>
    /// <param name="source">Readable; read exactly once, front to back. Need not be seekable.</param>
    /// <param name="sourceLength">The logical length to read from <paramref name="source"/>.</param>
    /// <param name="level">The Kraken level recorded in the header (default 7).</param>
    /// <param name="blockSize">The logical block size (default 256 KiB). Must be positive.</param>
    /// <param name="useHuffmanArrays">As the array overload.</param>
    /// <param name="maxDegreeOfParallelism">Blocks encoded at once; -1 uses the processor count.
    /// The output does not depend on this value.</param>
    public static void WriteCompressed(Stream destination, Stream source, long sourceLength,
        int level = 7, int blockSize = DefaultBlockSize, bool useHuffmanArrays = true,
        int maxDegreeOfParallelism = -1)
        => Write(destination, source, sourceLength, level, blockSize, useHuffmanArrays,
            compress: true, maxDegreeOfParallelism);

    /// <summary>
    /// The one writer. Everything the layout needs is known from the logical length, so the metadata is
    /// reserved first, the blocks stream past in index order, and only the sentinel, the id=7 size, the
    /// total size and the file digest are written afterwards.
    /// </summary>
    private static void Write(Stream destination, Stream source, long sourceLength, int level,
        int blockSize, bool useHuffmanArrays, bool compress, int maxDegreeOfParallelism)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceLength);
        if (!destination.CanSeek || !destination.CanWrite)
            throw new ArgumentException("Destination must be seekable and writable.", nameof(destination));
        if (!ProsperoPfsDigest.IsSupported)
            throw new PlatformNotSupportedException(
                "SHA3-256 is required for the PS5 PFSv3 compression format but is not available on this host.");

        Layout layout = ComputeLayout(sourceLength, blockSize);
        if (sourceLength > MaxContainerExtent || layout.Off7 + sourceLength > MaxContainerExtent)
            throw new ArgumentOutOfRangeException(nameof(sourceLength), sourceLength,
                "The container would exceed the largest extent the block table can address.");
        int blockCount = checked((int)layout.BlockCount);

        var boundaries = new byte[checked((int)layout.Sec3Size)];
        var digests = new byte[checked((int)layout.Sec4Size)];
        var signatures = new byte[checked((int)layout.Sec5Size)];

        long basePosition = destination.Position;
        destination.Seek(basePosition + layout.Off7, SeekOrigin.Begin);

        long cumulativeComp = 0;
        long cumulativeUncomp = 0;
        int dop = maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : Environment.ProcessorCount;
        if (!compress) dop = 1;

        // Blocks are read in order and encoded in batches; each batch is written back in index order, so
        // the container never depends on how the work was scheduled.
        var batch = new byte[dop][];
        var results = new ContainerBlock[dop];
        int index = 0;
        while (index < blockCount)
        {
            int count = 0;
            while (count < dop && index + count < blockCount)
            {
                int size = (int)Math.Min(blockSize, sourceLength - (long)(index + count) * blockSize);
                if (size < 0) size = 0;
                byte[] buffer = new byte[size];
                source.ReadExactly(buffer, 0, size);
                batch[count] = buffer;
                count++;
            }

            int batchStart = index;
            int batchCount = count;
            if (batchCount == 1)
            {
                results[0] = EncodeContainerBlock(batch[0], compress, useHuffmanArrays,
                    digests.AsSpan((batchStart) * ProsperoPfsDigest.DigestLength, ProsperoPfsDigest.DigestLength),
                    signatures.AsSpan((batchStart) * DirectoryEntrySize, DirectoryEntrySize));
            }
            else
            {
                Parallel.For(0, batchCount, new ParallelOptions { MaxDegreeOfParallelism = dop }, k =>
                {
                    results[k] = EncodeContainerBlock(batch[k], compress, useHuffmanArrays,
                        digests.AsSpan((batchStart + k) * ProsperoPfsDigest.DigestLength, ProsperoPfsDigest.DigestLength),
                        signatures.AsSpan((batchStart + k) * DirectoryEntrySize, DirectoryEntrySize));
                });
            }

            for (int k = 0; k < batchCount; k++)
            {
                ContainerBlock b = results[k];
                int e = (batchStart + k) * DirectoryEntrySize;
                BinaryPrimitives.WriteUInt64LittleEndian(boundaries.AsSpan(e),
                    (ulong)cumulativeComp | (b.Flags << (int)BoundaryFlagShift));
                BinaryPrimitives.WriteUInt64LittleEndian(boundaries.AsSpan(e + 8),
                    (ulong)cumulativeUncomp | (b.SizeHint << (int)SizeHintShift));
                if (b.Payload.Length > 0)
                    destination.Write(b.Payload, 0, b.Payload.Length);
                cumulativeComp += b.Payload.Length;
                cumulativeUncomp += b.UncompressedSize;
                batch[k] = Array.Empty<byte>();
            }
            index += batchCount;
        }

        // Sentinel boundary entry: the totals, with no flags and no hint.
        int sentinel = blockCount * DirectoryEntrySize;
        BinaryPrimitives.WriteUInt64LittleEndian(boundaries.AsSpan(sentinel), (ulong)cumulativeComp);
        BinaryPrimitives.WriteUInt64LittleEndian(boundaries.AsSpan(sentinel + 8), (ulong)sourceLength);

        long totalSize = layout.Off7 + cumulativeComp;
        var prefix = new byte[checked((int)layout.Off7)];
        Span<byte> span = prefix;

        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x04..], 3);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x06..], SectionCount);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x08..], (uint)blockSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x0C..], EncodeParam0C);
        ulong encodeParam10 = (ulong)ProsperoCompressionAlgorithm.Kraken
                              | ((ulong)(byte)(sbyte)level << 8)
                              | (ProsperoPfsCompressionConstants.KrakenWindowBits << 16);
        BinaryPrimitives.WriteUInt64LittleEndian(span[0x10..], encodeParam10);
        BinaryPrimitives.WriteUInt64LittleEndian(span[0x18..], (ulong)sourceLength);
        BinaryPrimitives.WriteUInt64LittleEndian(span[0x20..], (ulong)totalSize);

        WriteDirectoryEntry(span, 0, 1, layout.Off1, layout.Sec1Size);
        WriteDirectoryEntry(span, 1, 2, layout.Off2, layout.Sec2Size);
        WriteDirectoryEntry(span, 2, 3, layout.Off3, layout.Sec3Size);
        WriteDirectoryEntry(span, 3, 4, layout.Off4, layout.Sec4Size);
        WriteDirectoryEntry(span, 4, 5, layout.Off5, layout.Sec5Size);
        WriteDirectoryEntry(span, 5, 6, layout.Off6, 0);
        WriteDirectoryEntry(span, 6, 7, layout.Off7, cumulativeComp);

        GitHash.CopyTo(span[(int)layout.Off1..]);
        ShuffleTable.CopyTo(span[(int)layout.Off2..]);
        boundaries.CopyTo(span[(int)layout.Off3..]);
        digests.CopyTo(span[(int)layout.Off4..]);
        signatures.CopyTo(span[(int)layout.Off5..]);

        byte[] digest = ProsperoPfsDigest.ComputeFileDigest(
            span.Slice(0x08, ProsperoPfsDigest.FileDigestHeaderParamsLength),
            span.Slice((int)layout.Off2, (int)layout.Sec2Size),
            span.Slice((int)layout.Off3, (int)layout.Sec3Size),
            span.Slice((int)layout.Off4, (int)layout.Sec4Size));
        digest.CopyTo(span[0x28..]);

        long endPosition = destination.Position;
        destination.Seek(basePosition, SeekOrigin.Begin);
        destination.Write(prefix, 0, prefix.Length);
        destination.Seek(endPosition, SeekOrigin.Begin);
    }

    /// <summary>
    /// Writes one section-directory entry. The offset and the size are each 48 bits: a low 32-bit word
    /// followed by a 16-bit high word, so a container may exceed 4 GiB.
    /// </summary>
    private static void WriteDirectoryEntry(Span<byte> span, int index, ushort id, long offset, long size)
    {
        int p = HeaderSize + index * DirectoryEntrySize;
        BinaryPrimitives.WriteUInt16LittleEndian(span[p..], id);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(p + 2)..], (uint)offset);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(p + 6)..], (ushort)(offset >> 32));
        // span[p+8 .. p+10] reserved (0)
        BinaryPrimitives.WriteUInt32LittleEndian(span[(p + 10)..], (uint)size);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(p + 14)..], (ushort)(size >> 32));
    }

    private static long Align(long value, long alignment) => (value + alignment - 1) & ~(alignment - 1);

    /// <summary>Largest value the 44-bit offset fields of the boundary table can hold.</summary>
    private const long MaxContainerExtent = 0xFFFFFFFFFFFL;

    /// <summary>The fixed part of the layout: every section size and offset follows from the block count.</summary>
    private readonly record struct Layout(
        long BlockCount, long Sec1Size, long Sec2Size, long Sec3Size, long Sec4Size, long Sec5Size,
        long Off1, long Off2, long Off3, long Off4, long Off5, long Off6, long Off7);

    private static Layout ComputeLayout(long uncompressedSize, int blockSize)
    {
        long blockCount = uncompressedSize == 0 ? 1 : (uncompressedSize + blockSize - 1) / blockSize;
        long sec1 = GitHash.Length;
        long sec2 = ShuffleTable.Length;
        long sec3 = (blockCount + 1) * DirectoryEntrySize;
        long sec4 = blockCount * ProsperoPfsDigest.DigestLength;
        long sec5 = blockCount * DirectoryEntrySize;
        long off1 = HeaderSize + SectionCount * DirectoryEntrySize;   // 0xB8
        long off2 = Align(off1 + sec1, SectionAlignment);
        long off3 = Align(off2 + sec2, SectionAlignment);
        long off4 = Align(off3 + sec3, SectionAlignment);
        long off5 = Align(off4 + sec4, SectionAlignment);
        long off6 = Align(off5 + sec5, SectionAlignment);             // id=6 is empty
        long off7 = Align(off6, DataAlignment);                       // block data, padded to 0x400
        return new Layout(blockCount, sec1, sec2, sec3, sec4, sec5, off1, off2, off3, off4, off5, off6, off7);
    }

    /// <summary>
    /// One block as it lands in the container: the bytes for section id=7, the boundary flags and the
    /// size hint. The digest and the signature are written straight into the caller's tables.
    /// </summary>
    private readonly struct ContainerBlock
    {
        public readonly byte[] Payload;
        public readonly int UncompressedSize;
        public readonly ulong Flags;
        public readonly ulong SizeHint;

        public ContainerBlock(byte[] payload, int uncompressedSize, ulong flags, ulong sizeHint)
        {
            Payload = payload;
            UncompressedSize = uncompressedSize;
            Flags = flags;
            SizeHint = sizeHint;
        }
    }

    /// <summary>
    /// The whole per-block decision, in one place so the in-memory and streaming writers cannot drift:
    /// encode the block, decide whether the coded form is kept, derive the flags and the size hint, and
    /// fill in the block's digest and signature.
    /// </summary>
    private static ContainerBlock EncodeContainerBlock(ReadOnlySpan<byte> block, bool compress,
        bool useHuffmanArrays, Span<byte> digest, Span<byte> signature)
    {
        ProsperoPfsDigest.ComputeBlockDigest(block, digest);
        WriteBlockSignature(signature, block);

        int size = block.Length;
        if (compress && size > 0)
        {
            EncodedBlock? encoded = OodleKrakenEncoder.EncodeBlock(block, useHuffmanArrays);
            if (encoded is EncodedBlock eb && KeepCompressed(eb.Payload.Length, size))
            {
                ulong flags = FormNibble(eb.Chunk0Form, eb.Chunk0LitMode, first: true);
                if (eb.MultiChunk)
                    flags |= FormNibble(eb.Chunk1Form, eb.Chunk1LitMode, first: false) << 4;
                int firstSubChunk = eb.MultiChunk ? eb.FirstChunkCompSize : eb.Payload.Length;
                return new ContainerBlock(eb.Payload, size, flags,
                    (ulong)(Math.Max(firstSubChunk - 1, 0) & (long)SizeHintMask));
            }
        }

        ulong storedFlags = ChunkRawCopy | (size > SubChunkSize ? ChunkRawCopyHigh : 0);
        int storedFirst = Math.Min(size, SubChunkSize);
        return new ContainerBlock(block.ToArray(), size, storedFlags,
            (ulong)(Math.Max(storedFirst - 1, 0) & (long)SizeHintMask));
    }
}
