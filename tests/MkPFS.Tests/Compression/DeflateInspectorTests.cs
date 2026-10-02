using MkPFS.Core.Compression;

namespace MkPFS.Tests.Compression;

public sealed class DeflateInspectorTests
{
    private static byte[] Block(int seed)
    {
        byte[] data = new byte[65536];
        Random random = new(seed);
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = i % 3 == 0 ? (byte)random.Next(256) : (byte)(i % 211);
        }

        return data;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(9)]
    public void Zlib_output_is_valid_and_never_uses_far_distances(int level)
    {
        DeflateStreamReport report = DeflateInspector.InspectZlib(Zlib.Compress(Block(level), level));

        Assert.True(report.Valid, report.Error);
        Assert.Equal(65536, report.OutputLength);
        Assert.False(report.HasFarDistance);
        Assert.InRange(report.MaxDistance, 1, DeflateInspector.ZlibMaxDistance);
        Assert.True(report.DynamicBlocks + report.FixedBlocks > 0);
    }

    [Fact]
    public void Level_0_produces_stored_blocks_only()
    {
        DeflateStreamReport report = DeflateInspector.InspectZlib(Zlib.Compress(Block(1), 0));

        Assert.True(report.Valid, report.Error);
        Assert.Equal(report.Blocks, report.StoredBlocks);
        Assert.Equal(65536, report.OutputLength);
    }

    [Fact]
    public void Corrupt_or_truncated_streams_are_invalid()
    {
        byte[] good = Zlib.Compress(Block(2));

        Assert.False(DeflateInspector.InspectZlib(good.AsSpan(0, good.Length / 2)).Valid);
        Assert.False(DeflateInspector.InspectZlib([0x78]).Valid);
        Assert.False(DeflateInspector.InspectZlib([0x79, 0xDA, 0, 0]).Valid);
    }

    [Fact]
    public void Output_limit_is_enforced()
    {
        DeflateStreamReport report = DeflateInspector.InspectZlib(Zlib.Compress(Block(3)), maxOutput: 1000);
        Assert.False(report.Valid);
        Assert.Contains("more than 1000", report.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Far_distance_is_detected_in_a_hand_built_fixed_block()
    {
        // 32768 literal zeros via a stored block, then a fixed block with one match of
        // length 3 at distance 32768 (beyond zlib's 32506 limit), then end-of-block.
        List<byte> stream = [0x78, 0x01];
        stream.Add(0x00); // BFINAL=0, BTYPE=00, padding
        stream.AddRange([0x00, 0x80, 0xFF, 0x7F]); // LEN=32768, NLEN
        stream.AddRange(new byte[32768]);

        BitWriter bits = new();
        bits.Write(1, 1); // BFINAL
        bits.Write(1, 2); // BTYPE=01 (fixed)
        bits.WriteCode(0b0000001, 7); // symbol 257 (length 3)
        bits.WriteCode(29, 5); // distance symbol 29: base 24577, 13 extra bits
        bits.Write(32768 - 24577, 13);
        bits.WriteCode(0b0000000, 7); // symbol 256 end-of-block
        stream.AddRange(bits.Flush());
        stream.AddRange([0, 0, 0, 0]); // Adler-32 (not checked)

        DeflateStreamReport report = DeflateInspector.InspectZlib([.. stream]);

        Assert.True(report.Valid, report.Error);
        Assert.Equal(32768 + 3, report.OutputLength);
        Assert.Equal(32768, report.MaxDistance);
        Assert.True(report.HasFarDistance);
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

        // Huffman codes are packed most-significant bit first.
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
