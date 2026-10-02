using System.Buffers.Binary;
using System.Security.Cryptography;
using MkPFS.Core.Compression;
using MkPFS.Core.PFS;
using MkPFS.Core.PFSC;

namespace MkPFS.Tests.PFSC;

/// <summary>PFSC header, policy, encoder and reader (port of the PFSC parts of Python <c>test_pfs.py</c>).</summary>
public sealed class PFSCTests
{
    private const int Block = PFSConstants.PFSCLogicalBlockSize;

    private static byte[] Repeat(params (char Value, int Count)[] parts)
    {
        List<byte> bytes = [];
        foreach ((char value, int count) in parts)
        {
            bytes.AddRange(Enumerable.Repeat((byte)value, count));
        }

        return [.. bytes];
    }

    private static byte[] Mixed(int length, int seed)
    {
        byte[] data = new byte[length];
        Random random = new(seed);
        for (int i = 0; i < length; i++)
        {
            // Every fourth 64 KiB block is random (stays raw), the rest compress well.
            data[i] = (i / Block) % 4 == 3 ? (byte)random.Next(256) : (byte)((i * 7 / 13) % 251);
        }

        return data;
    }

    [Theory]
    [InlineData(0, 0x10000)]
    [InlineData(1, 0x10000)]
    [InlineData(8063, 0x10000)]
    [InlineData(8064, 0x20000)]
    [InlineData(16255, 0x20000)]
    [InlineData(16256, 0x30000)]
    public void HeaderSize_grows_by_whole_blocks_when_the_offset_table_overflows(long blocks, long expected) =>
        Assert.Equal(expected, PFSCHeader.HeaderSize(blocks));

    [Fact]
    public void Header_round_trips_and_matches_reference_layout()
    {
        PFSCHeader header = PFSCHeader.ForBlocks(3);
        byte[] bytes = new byte[0x30];
        header.Write(bytes);

        Assert.Equal("PFSC"u8.ToArray(), bytes[..4]);
        Assert.Equal(6, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)));
        Assert.Equal(Block, BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(0x10)));
        Assert.Equal(3L * Block, BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(0x28)));
        Assert.Equal(header, PFSCHeader.Parse(bytes));
    }

    [Theory]
    [InlineData(0x00, "magic")]
    [InlineData(0x04, "unk4")]
    [InlineData(0x08, "unk8")]
    [InlineData(0x0C, "logical block size")]
    [InlineData(0x10, "block_sz2")]
    [InlineData(0x18, "offset table")]
    [InlineData(0x28, "aligned")]
    public void Header_parse_rejects_bad_fields(int offset, string message)
    {
        byte[] bytes = new byte[0x30];
        PFSCHeader.ForBlocks(2).Write(bytes);
        bytes[offset] ^= 0x01;

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => PFSCHeader.Parse(bytes));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(65535, 0, true)]
    [InlineData(65536, 0, false)]
    [InlineData(62259, 5, true)]
    [InlineData(62260, 5, false)]
    [InlineData(70000, 0, false)]
    public void Policy_keeps_blocks_strictly_smaller_with_enough_gain(int compressed, int threshold, bool expected) =>
        Assert.Equal(expected, PFSCBlockPolicy.ShouldStoreCompressed(compressed, Block, threshold));

    [Fact]
    public void Encode_decode_round_trip_preserves_bytes()
    {
        byte[] raw = Repeat(('A', Block), ('B', Block), ('C', 1234));
        (byte[] encoded, double gain, long hypothetical) = PFSCEncoder.EncodePayload(raw, new PFSCEncodeOptions { ThresholdGain = 1, Level = 9 });

        Assert.NotEqual(raw, encoded);
        Assert.True(gain > 0.0);
        Assert.True(hypothetical >= encoded.Length);
        PFSCHeader header = PFSCHeader.Parse(encoded);
        Assert.Equal(PFSConstants.PFSCBlockOffsetsOffset, header.BlockOffsetsOffset);
        Assert.Equal(3L * Block, header.DataLength);
        Assert.Equal(raw, PFSCReader.DecodePayload(encoded, raw.Length));
    }

    [Fact]
    public void Encode_reports_raw_bytes_per_block_in_order()
    {
        List<int> deltas = [];
        PFSCEncoder.EncodePayload(Repeat(('A', Block), ('B', 1234)), new PFSCEncodeOptions { ThresholdGain = 1, Level = 9 }, deltas.Add);
        Assert.Equal([Block, 1234], deltas);
    }

    [Fact]
    public void Compressed_block_of_exactly_one_logical_block_is_stored_raw()
    {
        byte[] raw = Repeat(('A', Block), ('B', Block), ('C', Block));
        int calls = 0;
        IPFSCBlockCompressor Factory(int level) => new FakeCompressor(level, () => Interlocked.Increment(ref calls) == 1);

        (byte[] encoded, _, _) = PFSCEncoder.EncodePayload(raw, new PFSCEncodeOptions { Level = 9 }, Factory, null);

        PFSCReader reader = PFSCReader.Open(new MemoryStream(encoded), 0, encoded.Length);
        Assert.Equal(Block, reader.StoredLength(0));
        Assert.True(reader.StoredLength(1) < Block);
        Assert.True(reader.StoredLength(2) < Block);
        Assert.Equal(raw, PFSCReader.DecodePayload(encoded, raw.Length));
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(4, 8)]
    [InlineData(7, 3)]
    public void Parallel_encode_is_byte_identical_to_sequential(int workers, int windowFactor)
    {
        byte[] raw = Mixed((Block * 37) + 4321, seed: 9);
        PFSCEncodeOptions sequential = new() { ThresholdGain = 5, ComputeBlockHashes = true };
        PFSCEncodeOptions parallel = sequential with { Workers = workers, WindowFactor = windowFactor };

        (byte[] a, PFSCEncodeResult ra) = EncodeToBytes(raw, sequential);
        (byte[] b, PFSCEncodeResult rb) = EncodeToBytes(raw, parallel);

        Assert.Equal(a, b);
        Assert.Equal(ra with { BlockHashes = null }, rb with { BlockHashes = null });
        Assert.Equal(ra.BlockHashes, rb.BlockHashes);
    }

    [Fact]
    public void Block_hashes_cover_unpadded_raw_blocks()
    {
        byte[] raw = Mixed((Block * 2) + 100, seed: 3);
        (_, PFSCEncodeResult result) = EncodeToBytes(raw, new PFSCEncodeOptions { ComputeBlockHashes = true });

        Assert.Equal(3 * 32, result.BlockHashes!.Length);
        Assert.Equal(SHA256.HashData(raw.AsSpan(0, Block)), result.BlockHashes[..32]);
        Assert.Equal(SHA256.HashData(raw.AsSpan(2 * Block, 100)), result.BlockHashes[64..]);
    }

    [Fact]
    public void Incompressible_file_stays_raw()
    {
        byte[] raw = new byte[Block * 2];
        new Random(1).NextBytes(raw);
        (_, PFSCEncodeResult result) = EncodeToBytes(raw, new PFSCEncodeOptions());

        Assert.False(result.IsCompressed);
        Assert.Equal(raw.Length, result.StoredSize);
        Assert.Equal(0, result.CompressedBlocks);
    }

    [Fact]
    public void Min_file_gain_keeps_low_gain_files_raw()
    {
        byte[] raw = Mixed(Block * 4, seed: 2);
        (_, PFSCEncodeResult kept) = EncodeToBytes(raw, new PFSCEncodeOptions { MinFileGain = 0 });
        Assert.True(kept.IsCompressed);

        int tooHigh = (int)Math.Ceiling(kept.GainPercent) + 1;
        (_, PFSCEncodeResult raw2) = EncodeToBytes(raw, new PFSCEncodeOptions { MinFileGain = tooHigh });
        Assert.False(raw2.IsCompressed);
        Assert.Equal(raw.Length, raw2.StoredSize);
        Assert.Equal(kept.GainPercent, raw2.GainPercent);
    }

    [Fact]
    public void Empty_file_is_not_compressed()
    {
        (_, PFSCEncodeResult result) = EncodeToBytes([], new PFSCEncodeOptions());
        Assert.Equal(new PFSCEncodeResult(0, false, 0.0, 0, 0, 0, null), result);
    }

    [Fact]
    public void Stream_mode_always_writes_PFSC_and_checks_length()
    {
        byte[] raw = new byte[Block + 10];
        new Random(4).NextBytes(raw);
        using MemoryStream output = new();
        PFSCEncodeResult result = PFSCEncoder.EncodeStream(new MemoryStream(raw), raw.Length, output, 0, new PFSCEncodeOptions(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsCompressed);
        Assert.Equal(raw, PFSCReader.DecodePayload(output.ToArray(), raw.Length));

        Assert.Throws<InvalidDataException>(() =>
            PFSCEncoder.EncodeStream(new MemoryStream(raw), raw.Length - Block, new MemoryStream(), 0, new PFSCEncodeOptions(), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() =>
            PFSCEncoder.EncodeStream(new MemoryStream(raw), raw.Length + Block, new MemoryStream(), 0, new PFSCEncodeOptions(), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void File_mode_writes_at_base_offset()
    {
        byte[] raw = Mixed(Block * 3, seed: 5);
        using MemoryStream output = new();
        output.Write(new byte[1000]);
        PFSCEncodeResult result = PFSCEncoder.EncodeFile(new MemoryStream(raw), raw.Length, output, 1000, new PFSCEncodeOptions(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsCompressed);
        PFSCReader reader = PFSCReader.Open(output, 1000, result.StoredSize);
        using MemoryStream decoded = new();
        reader.DecodeTo(decoded, raw.Length);
        Assert.Equal(raw, decoded.ToArray());
    }

    [Fact]
    public void Decode_rejects_invalid_magic_and_legacy_whole_file_zlib()
    {
        byte[] raw = Repeat(('A', 70000));
        (byte[] encoded, _, _) = PFSCEncoder.EncodePayload(raw, new PFSCEncodeOptions { ThresholdGain = 1, Level = 9 });
        "BAD!"u8.CopyTo(encoded);
        Assert.Contains("magic", Assert.Throws<InvalidDataException>(() => PFSCReader.DecodePayload(encoded, raw.Length)).Message, StringComparison.Ordinal);

        byte[] legacy = Zlib.Compress(Repeat(('A', 100000)));
        Assert.Contains("magic", Assert.Throws<InvalidDataException>(() => PFSCReader.DecodePayload(legacy)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_rejects_bad_offset_tables_and_sizes()
    {
        byte[] raw = Mixed(Block * 3, seed: 6);
        (byte[] encoded, _, _) = PFSCEncoder.EncodePayload(raw, new PFSCEncodeOptions());

        byte[] nonMonotonic = (byte[])encoded.Clone();
        BinaryPrimitives.WriteInt64LittleEndian(nonMonotonic.AsSpan(0x400 + 16), 0x10000 + 1);
        BinaryPrimitives.WriteInt64LittleEndian(nonMonotonic.AsSpan(0x400 + 8), 0x10000 + 50);
        Assert.Contains("monotonic", Assert.Throws<InvalidDataException>(() => PFSCReader.DecodePayload(nonMonotonic)).Message, StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => PFSCReader.DecodePayload(encoded[..(encoded.Length - 1)]));
        Assert.Contains("inode size", Assert.Throws<InvalidDataException>(() => PFSCReader.DecodePayload(encoded, (Block * 3) + 1)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveWorkerCount_matches_python_policy()
    {
        Assert.Equal(Math.Min(16, Math.Max(1, Environment.ProcessorCount - 1)), PFSCEncoder.ResolveWorkerCount(0));
        Assert.Equal(3, PFSCEncoder.ResolveWorkerCount(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => PFSCEncoder.ResolveWorkerCount(-1));
    }

    private static (byte[] Bytes, PFSCEncodeResult Result) EncodeToBytes(byte[] raw, PFSCEncodeOptions options)
    {
        using MemoryStream output = new();
        PFSCEncodeResult result = PFSCEncoder.EncodeFile(new MemoryStream(raw), raw.Length, output, 0, options, cancellationToken: TestContext.Current.CancellationToken);
        return (output.ToArray(), result);
    }

    // Returns a full 64 KiB "compressed" block when asked to, otherwise real zlib.
    private sealed class FakeCompressor(int level, Func<bool> fullSize) : IPFSCBlockCompressor
    {
        private readonly ZlibBlockCompressor _real = new(level);

        public int Compress(ReadOnlySpan<byte> block, Span<byte> destination)
        {
            if (fullSize())
            {
                destination[..block.Length].Fill((byte)'Z');
                return block.Length;
            }

            return _real.Compress(block, destination);
        }

        public void Dispose() => _real.Dispose();
    }
}
