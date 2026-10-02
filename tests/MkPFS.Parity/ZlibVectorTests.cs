using System.Buffers.Binary;
using System.Text.Json;
using MkPFS.Core.Compression;

namespace MkPFS.Parity;

/// <summary>
/// Byte-for-byte check of the native zlib against Python <c>zlib.compress</c> output recorded by
/// the oracle (<c>vectors/zlib_vectors.bin</c>, format <c>ZVEC0001</c>).
/// </summary>
public sealed class ZlibVectorTests
{
    private sealed record Vector(int Index, int Level, byte[] Raw, byte[] Compressed);

    private static List<Vector> LoadVectors()
    {
        byte[] data = File.ReadAllBytes(Fixtures.PathOrSkip("vectors", "zlib_vectors.bin"));
        Assert.Equal("ZVEC0001"u8.ToArray(), data[..8]);
        int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8));
        List<Vector> vectors = new(count);
        int offset = 12;
        for (int i = 0; i < count; i++)
        {
            int level = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
            int rawLength = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 4));
            int compressedLength = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 8));
            offset += 12;
            byte[] raw = data.AsSpan(offset, rawLength).ToArray();
            offset += rawLength;
            byte[] compressed = data.AsSpan(offset, compressedLength).ToArray();
            offset += compressedLength;
            vectors.Add(new Vector(i, level, raw, compressed));
        }

        Assert.Equal(data.Length, offset);
        return vectors;
    }

    [Fact]
    public void Native_zlib_output_matches_python_for_every_vector()
    {
        List<Vector> vectors = LoadVectors();
        Assert.NotEmpty(vectors);
        Dictionary<int, ZlibDeflater> deflaters = [];
        try
        {
            List<int> mismatches = [];
            foreach (Vector vector in vectors)
            {
                if (!deflaters.TryGetValue(vector.Level, out ZlibDeflater? deflater))
                {
                    deflater = new ZlibDeflater(vector.Level);
                    deflaters[vector.Level] = deflater;
                }

                if (!deflater.Compress(vector.Raw).AsSpan().SequenceEqual(vector.Compressed))
                {
                    mismatches.Add(vector.Index);
                }
            }

            Assert.True(mismatches.Count == 0, $"vectors differing from Python: {string.Join(", ", mismatches)}");
        }
        finally
        {
            foreach (ZlibDeflater deflater in deflaters.Values)
            {
                deflater.Dispose();
            }
        }
    }

    [Fact]
    public void Native_zlib_decodes_every_python_vector()
    {
        foreach (Vector vector in LoadVectors())
        {
            byte[] output = new byte[vector.Raw.Length];
            Assert.Equal(vector.Raw.Length, Zlib.Decompress(vector.Compressed, output));
            Assert.Equal(vector.Raw, output);
        }
    }

    [Fact]
    public void Oracle_used_the_same_zlib_release()
    {
        using JsonDocument manifest = Fixtures.Manifest();
        Assert.Equal(Zlib.NativeVersion, manifest.RootElement.GetProperty("zlib_runtime").GetString());
    }
}
