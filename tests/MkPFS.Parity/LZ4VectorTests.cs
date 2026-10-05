using System.Buffers.Binary;
using System.Text.Json;
using MkPFS.Core.Compression;

namespace MkPFS.Parity;

/// <summary>
/// Byte-for-byte check of the native lz4 against the AMPR pack oracle codec (ampr_emu
/// <c>ampr_pack_format.Lz4Codec</c>, python-lz4 4.4.5), recorded by <c>tools/oracle/build_ampr_goldens.py</c>
/// in <c>ampr/vectors/lz4_vectors.bin</c> (format <c>L4VEC001</c>).
/// </summary>
public sealed class LZ4VectorTests
{
    private sealed record Vector(int Index, bool HighCompression, int Parameter, byte[] Raw, byte[] Compressed)
    {
        public override string ToString() => $"#{Index} {(HighCompression ? "hc" : "fast")}{Parameter}/{Raw.Length}";
    }

    private static List<Vector> LoadVectors()
    {
        byte[] data = File.ReadAllBytes(Fixtures.PathOrSkip("ampr", "vectors", "lz4_vectors.bin"));
        Assert.Equal("L4VEC001"u8.ToArray(), data[..8]);
        int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8));
        List<Vector> vectors = new(count);
        int offset = 12;
        for (int i = 0; i < count; i++)
        {
            byte mode = data[offset];
            int parameter = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 4));
            int rawLength = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 8));
            int compressedLength = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 12));
            offset += 16;
            byte[] raw = data.AsSpan(offset, rawLength).ToArray();
            offset += rawLength;
            byte[] compressed = data.AsSpan(offset, compressedLength).ToArray();
            offset += compressedLength;
            vectors.Add(new Vector(i, mode == 1, parameter, raw, compressed));
        }

        Assert.Equal(data.Length, offset);
        return vectors;
    }

    [Fact]
    public void Native_lz4_output_matches_ampr_pack_for_every_vector()
    {
        List<Vector> vectors = LoadVectors();
        Assert.NotEmpty(vectors);
        using LZ4Encoder encoder = new();
        List<Vector> mismatches = [];
        foreach (Vector vector in vectors)
        {
            byte[] produced = vector.HighCompression
                ? encoder.CompressHC(vector.Raw, vector.Parameter)
                : encoder.CompressFast(vector.Raw, vector.Parameter);
            if (!produced.AsSpan().SequenceEqual(vector.Compressed))
            {
                mismatches.Add(vector);
            }
        }

        Assert.True(mismatches.Count == 0, $"vectors differing from ampr_pack: {string.Join(", ", mismatches)}");
    }

    [Fact]
    public void Native_lz4_decodes_every_ampr_pack_vector()
    {
        foreach (Vector vector in LoadVectors())
        {
            byte[] output = new byte[vector.Raw.Length];
            Assert.Equal(vector.Raw.Length, LZ4Codec.Decompress(vector.Compressed, output));
            Assert.Equal(vector.Raw, output);
        }
    }

    [Fact]
    public void Oracle_used_the_same_lz4_release()
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(Fixtures.PathOrSkip("ampr", "goldens", "manifest.json")));
        Assert.Equal(LZ4Codec.NativeVersion, manifest.RootElement.GetProperty("versions").GetProperty("liblz4").GetString());
    }
}
