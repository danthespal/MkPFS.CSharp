using System.Buffers.Binary;
using MkPFS.Core.Compression;
using MkPFS.Core.PFS;

namespace MkPFS.Core.PFSC;

/// <summary>
/// Random-access reader for a PFSC payload stored at <c>baseOffset</c> in a stream
/// (port of Python <c>decode_pfsc_payload</c> validation and block decoding).
/// </summary>
public sealed class PFSCReader
{
    private readonly Stream _stream;
    private readonly long _baseOffset;
    private readonly long[] _offsets;

    private PFSCReader(Stream stream, long baseOffset, PFSCHeader header, long[] offsets)
    {
        _stream = stream;
        _baseOffset = baseOffset;
        Header = header;
        _offsets = offsets;
    }

    /// <summary>Validated header.</summary>
    public PFSCHeader Header { get; }

    /// <summary>Number of logical blocks.</summary>
    public long BlockCount => Header.BlockCount;

    /// <summary>Block-rounded logical size.</summary>
    public long LogicalSize => Header.DataLength;

    /// <summary>Block start offsets relative to the payload start; <c>BlockCount + 1</c> entries.</summary>
    public IReadOnlyList<long> Offsets => _offsets;

    /// <summary>End of the last block relative to the payload start.</summary>
    public long PayloadEnd => _offsets[^1];

    /// <summary>Stored length of block <paramref name="index"/> (65536 = raw).</summary>
    /// <param name="index">Block index.</param>
    /// <returns>Stored span length.</returns>
    public int StoredLength(long index) => checked((int)(_offsets[index + 1] - _offsets[index]));

    /// <summary>Whether block <paramref name="index"/> is stored compressed.</summary>
    /// <param name="index">Block index.</param>
    /// <returns><see langword="true"/> when its span is shorter than the logical block.</returns>
    public bool IsBlockCompressed(long index) => StoredLength(index) < Header.LogicalBlockSize;

    /// <summary>Open and validate a PFSC payload.</summary>
    /// <param name="stream">Readable, seekable stream.</param>
    /// <param name="baseOffset">Absolute offset of the payload.</param>
    /// <param name="payloadLength">Stored payload length (inode size).</param>
    /// <returns>Reader over the payload.</returns>
    /// <exception cref="InvalidDataException">The header or offset table is invalid.</exception>
    public static PFSCReader Open(Stream stream, long baseOffset, long payloadLength)
    {
        byte[] head = new byte[PFSConstants.PFSCHeaderSize];
        ReadAt(stream, baseOffset, head, payloadLength >= head.Length ? head.Length : (int)Math.Max(0, payloadLength));
        PFSCHeader header = PFSCHeader.Parse(payloadLength >= head.Length ? head : head.AsSpan(0, (int)Math.Max(0, payloadLength)));
        if (header.DataOffset > payloadLength)
        {
            throw new InvalidDataException("PFSC data offset exceeds payload length");
        }

        long count = header.BlockCount;
        long tableSize = checked((count + 1) * PFSConstants.PFSCOffsetEntrySize);
        long tableEnd = header.BlockOffsetsOffset + tableSize;
        if (tableEnd > header.DataOffset || tableEnd > payloadLength)
        {
            throw new InvalidDataException("PFSC payload is truncated before block offset table");
        }

        byte[] table = new byte[checked((int)tableSize)];
        ReadAt(stream, baseOffset + header.BlockOffsetsOffset, table, table.Length);
        long[] offsets = new long[count + 1];
        for (long i = 0; i <= count; i++)
        {
            // Python reads these as unsigned Q; values above long.MaxValue cannot be valid offsets anyway.
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(table.AsSpan(checked((int)(i * 8))));
            offsets[i] = value > long.MaxValue ? long.MaxValue : (long)value;
        }

        if (offsets[0] != header.DataOffset)
        {
            throw new InvalidDataException("PFSC block offsets must start at data_start");
        }

        if (offsets[^1] > payloadLength)
        {
            throw new InvalidDataException("PFSC block offsets exceed payload size");
        }

        for (long i = 1; i < offsets.Length; i++)
        {
            if (offsets[i] < offsets[i - 1])
            {
                throw new InvalidDataException("PFSC block offsets are not monotonic");
            }
        }

        return new PFSCReader(stream, baseOffset, header, offsets);
    }

    /// <summary>Read the stored bytes of block <paramref name="index"/>.</summary>
    /// <param name="index">Block index.</param>
    /// <param name="destination">At least <see cref="StoredLength"/> bytes.</param>
    /// <returns>Stored length.</returns>
    public int ReadStoredBlock(long index, Span<byte> destination)
    {
        int length = StoredLength(index);
        ReadAt(_stream, _baseOffset + _offsets[index], destination[..length], length);
        return length;
    }

    /// <summary>Decode block <paramref name="index"/> into exactly one logical block.</summary>
    /// <param name="index">Block index.</param>
    /// <param name="destination">At least <see cref="PFSCHeader.LogicalBlockSize"/> bytes.</param>
    /// <exception cref="InvalidDataException">The block fails to decode or has the wrong size.</exception>
    public void DecodeBlock(long index, Span<byte> destination)
    {
        int blockSize = Header.LogicalBlockSize;
        int stored = StoredLength(index);
        if (stored > blockSize)
        {
            throw new InvalidDataException($"PFSC block {index} stored size {stored} exceeds logical size {blockSize}");
        }

        if (stored == blockSize)
        {
            ReadStoredBlock(index, destination);
            return;
        }

        byte[] buffer = new byte[stored];
        ReadStoredBlock(index, buffer);
        DecodeStoredBlock(buffer, destination[..blockSize], index);
    }

    /// <summary>Decode a stored block (raw or zlib) to one logical block.</summary>
    /// <param name="stored">Stored bytes.</param>
    /// <param name="destination">Exactly one logical block.</param>
    /// <param name="index">Block index for error messages.</param>
    public static void DecodeStoredBlock(ReadOnlySpan<byte> stored, Span<byte> destination, long index)
    {
        int blockSize = destination.Length;
        if (stored.Length == blockSize)
        {
            stored.CopyTo(destination);
            return;
        }

        if (stored.Length > blockSize)
        {
            throw new InvalidDataException($"PFSC block {index} stored size {stored.Length} exceeds logical size {blockSize}");
        }

        int written;
        try
        {
            written = Zlib.Decompress(stored, destination);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"PFSC block {index} failed to decompress: {ex.Message}", ex);
        }

        if (written != blockSize)
        {
            throw new InvalidDataException($"PFSC block {index} decompressed to {written} bytes, expected {blockSize}");
        }
    }

    /// <summary>Decode every block to <paramref name="destination"/>, truncated to <paramref name="expectedLogicalSize"/> when given.</summary>
    /// <param name="destination">Output stream.</param>
    /// <param name="expectedLogicalSize">Inode logical size; must not exceed <see cref="LogicalSize"/>.</param>
    /// <returns>Bytes written.</returns>
    public long DecodeTo(Stream destination, long? expectedLogicalSize = null)
    {
        long limit = ResolveLimit(expectedLogicalSize);
        byte[] block = new byte[Header.LogicalBlockSize];
        long written = 0;
        // Every block is decoded, even past the inode size, so corrupt tail blocks still fail like Python.
        for (long i = 0; i < BlockCount; i++)
        {
            DecodeBlock(i, block);
            int take = (int)Math.Min(block.Length, limit - written);
            if (take > 0)
            {
                destination.Write(block, 0, take);
                written += take;
            }
        }

        return written;
    }

    /// <summary>Decode a whole in-memory payload (Python <c>decode_pfsc_payload</c>).</summary>
    /// <param name="payload">Stored payload.</param>
    /// <param name="expectedLogicalSize">Optional inode logical size.</param>
    /// <returns>Logical bytes.</returns>
    public static byte[] DecodePayload(byte[] payload, long? expectedLogicalSize = null)
    {
        using MemoryStream source = new(payload, writable: false);
        PFSCReader reader = Open(source, 0, payload.Length);
        using MemoryStream output = new();
        reader.DecodeTo(output, expectedLogicalSize);
        return output.ToArray();
    }

    private long ResolveLimit(long? expectedLogicalSize)
    {
        if (expectedLogicalSize is null)
        {
            return LogicalSize;
        }

        if (expectedLogicalSize < 0)
        {
            throw new InvalidDataException("expected inode logical size is negative");
        }

        if (expectedLogicalSize > LogicalSize)
        {
            throw new InvalidDataException($"PFSC logical size {LogicalSize} is smaller than inode size {expectedLogicalSize}");
        }

        return expectedLogicalSize.Value;
    }

    private static void ReadAt(Stream stream, long offset, Span<byte> buffer, int length)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        int got = stream.ReadAtLeast(buffer[..length], length, throwOnEndOfStream: false);
        if (got != length)
        {
            throw new InvalidDataException($"PFSC payload truncated at offset {offset} (wanted {length}, got {got})");
        }
    }
}
