using MkPFS.Core.Compression;

namespace MkPFS.Tests.Compression;

public sealed class ZlibTests
{
    private static byte[] Block(int seed)
    {
        byte[] data = new byte[0x10000];
        Random random = new(seed);
        for (int i = 0; i < data.Length; i++)
        {
            // Half repetitive, half random: compressible but not trivial.
            data[i] = i % 2 == 0 ? (byte)(i % 97) : (byte)random.Next(256);
        }

        return data;
    }

    [Fact]
    public void Native_library_reports_zlib_1_3_1() => Assert.Equal("1.3.1", Zlib.NativeVersion);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(9)]
    public void Compress_then_decompress_round_trips(int level)
    {
        byte[] block = Block(level);
        byte[] compressed = Zlib.Compress(block, level);
        byte[] restored = new byte[block.Length];

        Assert.Equal(block.Length, Zlib.Decompress(compressed, restored));
        Assert.Equal(block, restored);
    }

    [Fact]
    public void Level_7_uses_the_78_DA_zlib_header()
    {
        byte[] compressed = Zlib.Compress(Block(1), 7);
        Assert.Equal(0x78, compressed[0]);
        Assert.Equal(0xDA, compressed[1]);
    }

    [Fact]
    public void Reused_deflater_gives_the_same_bytes_as_a_fresh_one()
    {
        using ZlibDeflater deflater = new(7);
        byte[] first = deflater.Compress(Block(1));
        byte[] other = deflater.Compress(Block(2));
        byte[] again = deflater.Compress(Block(1));

        Assert.Equal(first, again);
        Assert.Equal(first, Zlib.Compress(Block(1), 7));
        Assert.NotEqual(first, other);
    }

    [Fact]
    public void TryCompress_returns_false_when_output_does_not_fit()
    {
        using ZlibDeflater deflater = new(7);
        byte[] random = new byte[0x10000];
        new Random(5).NextBytes(random);

        Assert.False(deflater.TryCompress(random, new byte[0xFFFF], out int written));
        Assert.Equal(0, written);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10)]
    public void Invalid_level_is_rejected(int level) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZlibDeflater(level));

    [Fact]
    public void Decompress_rejects_corrupt_and_truncated_streams()
    {
        byte[] compressed = Zlib.Compress(Block(3));
        byte[] output = new byte[0x10000];

        Assert.Throws<InvalidDataException>(() => Zlib.Decompress(compressed.AsSpan(0, compressed.Length / 2), output));
        compressed[10] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => Zlib.Decompress(compressed, output));
    }

    [Fact]
    public void Decompress_rejects_too_small_destination()
    {
        byte[] compressed = Zlib.Compress(Block(4));
        Assert.Throws<InvalidDataException>(() => Zlib.Decompress(compressed, new byte[100]));
    }

    [Fact]
    public void Disposed_deflater_throws()
    {
        ZlibDeflater deflater = new(7);
        deflater.Dispose();
        Assert.Throws<ObjectDisposedException>(() => deflater.Compress(new byte[16]));
    }
}
