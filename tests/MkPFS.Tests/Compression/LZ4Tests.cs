using MkPFS.Core.Compression;

namespace MkPFS.Tests.Compression;

public sealed class LZ4Tests
{
    private static readonly string[] Words = ["pfs ", "block ", "inode ", "asset ", "texture ", "mesh ", "\n"];

    private static byte[] Block(int seed)
    {
        // Random word sequence: LZ4 needs 4-byte repeats, so byte-level noise would not shrink.
        Random random = new(seed);
        System.Text.StringBuilder text = new();
        while (text.Length < 0x10000)
        {
            text.Append(Words[random.Next(Words.Length)]);
        }

        return System.Text.Encoding.ASCII.GetBytes(text.ToString(0, 0x10000));
    }

    [Fact]
    public void Native_library_reports_lz4_1_9_4() => Assert.Equal("1.9.4", LZ4Codec.NativeVersion);

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 8)]
    [InlineData(true, 1)]
    [InlineData(true, 9)]
    [InlineData(true, 12)]
    public void Compress_then_decompress_round_trips(bool highCompression, int parameter)
    {
        byte[] block = Block(parameter);
        using LZ4Encoder encoder = new();
        byte[] compressed = highCompression ? encoder.CompressHC(block, parameter) : encoder.CompressFast(block, parameter);
        byte[] restored = new byte[block.Length];

        Assert.True(compressed.Length < block.Length);
        Assert.Equal(block.Length, LZ4Codec.Decompress(compressed, restored));
        Assert.Equal(block, restored);
    }

    [Fact]
    public void Reused_encoder_gives_the_same_bytes_as_a_fresh_one()
    {
        using LZ4Encoder encoder = new();
        byte[] fast = encoder.CompressFast(Block(1));
        byte[] hc = encoder.CompressHC(Block(1));
        encoder.CompressFast(Block(2));
        encoder.CompressHC(Block(2), 3);

        using LZ4Encoder fresh = new();
        Assert.Equal(fast, encoder.CompressFast(Block(1)));
        Assert.Equal(hc, encoder.CompressHC(Block(1)));
        Assert.Equal(fast, fresh.CompressFast(Block(1)));
        Assert.Equal(hc, fresh.CompressHC(Block(1)));
    }

    [Fact]
    public void Out_of_range_parameters_are_clamped_like_ampr_pack()
    {
        using LZ4Encoder encoder = new();
        byte[] block = Block(3);

        Assert.Equal(encoder.CompressFast(block, 1), encoder.CompressFast(block, 0));
        Assert.Equal(encoder.CompressHC(block, 1), encoder.CompressHC(block, 0));
        Assert.Equal(encoder.CompressHC(block, 12), encoder.CompressHC(block, 13));
    }

    [Fact]
    public void Empty_input_gives_an_empty_block_like_ampr_pack()
    {
        using LZ4Encoder encoder = new();
        Assert.Empty(encoder.CompressFast([]));
        Assert.Empty(encoder.CompressHC([]));
    }

    [Fact]
    public void Decompress_rejects_corrupt_and_truncated_blocks()
    {
        using LZ4Encoder encoder = new();
        byte[] compressed = encoder.CompressHC(Block(4));
        byte[] output = new byte[0x10000];

        Assert.Throws<InvalidDataException>(() => LZ4Codec.Decompress(compressed.AsSpan(0, compressed.Length / 2), output));
        Assert.Throws<InvalidDataException>(() => LZ4Codec.Decompress([0xF0, 0xFF, 0xFF, 0xFF], output));
    }

    [Fact]
    public void Decompress_rejects_too_small_destination()
    {
        using LZ4Encoder encoder = new();
        byte[] compressed = encoder.CompressFast(Block(5));
        Assert.Throws<InvalidDataException>(() => LZ4Codec.Decompress(compressed, new byte[100]));
    }

    [Fact]
    public void CompressBound_rejects_negative_length() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => LZ4Codec.CompressBound(-1));

    [Fact]
    public void Disposed_encoder_throws()
    {
        LZ4Encoder encoder = new();
        encoder.Dispose();
        Assert.Throws<ObjectDisposedException>(() => encoder.CompressFast(new byte[16]));
    }
}
