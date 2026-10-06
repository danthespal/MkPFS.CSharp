using System.Buffers.Binary;
using MkPFS.Core.PFS.PS5;
using MkPFS.Core.PKG;

namespace MkPFS.Tests.PKG;

/// <summary>FPKG plan F2: naps layout parsing and the inner-mount chunk walk on a synthetic layout.</summary>
public sealed class PS5InnerImageTests
{
    // Mount: A (0x10 bytes) at 0, B (0x50000 bytes) at 0x10, a zero gap to the metadata at 0x80000, and
    // 0x10000 bytes of metadata. Stored: A and B in one run from 0, the gap's two fill blocks (16 bytes) at
    // 0x50010, the metadata in a second run at 0x60000. Records carry uoff = 2 × (logical mod 256 KiB) and the
    // Publishing Tools length fields (docs/FORMATS.md §13).
    private const long FileBSize = 0x50000;
    private const long MetaBase = 0x80000;
    private const long MountEnd = 0x90000;

    private static ulong Std(long coff, int kde, bool even = false, bool odd = true, uint uoff = 0, uint clen = 0) =>
        ((ulong)coff & 0x3FFFF) | ((ulong)uoff << 19) | ((ulong)clen << 37) | ((even ? 1UL : 0) << 54) | ((odd ? 1UL : 0) << 55) | ((ulong)kde << 56);

    private static ulong Run(long coffEnd, uint tweak) => ((ulong)coffEnd & 0x3FFFF) | (1UL << 18) | ((ulong)tweak << 19);

    private static byte[] BuildNaps(byte[]? u2cOverride = null)
    {
        long[] fidx = [0, 0x10, 0x10 + FileBSize, MetaBase, MountEnd];
        ulong[] records =
        [
            Run(0, 0),
            Std(0, kde: 0, clen: 2 * 0xF),                                  // A, raw 0x10
            Std(0x10, kde: 4, even: true, uoff: 0x20, clen: 2 * 0xFFFF),     // B, first 256 KiB raw
            Std(0x10 + 0x40000, kde: 0, uoff: 0x20, clen: 2 * 0xFFFF),     // B, rest (64 KiB raw)
            Std(0x50010, kde: 4, uoff: 0x20020, clen: 2 * 7),             // zero gap, two fill blocks
            Run(0x50020, 0x60000 >> 15),
            Std(0x60000, kde: 0, clen: 2 * 0xFFFF),                         // metadata, 64 KiB raw
            Run(0x70000, 0),
            Std(0x70000, kde: 0, odd: false, uoff: 1, clen: 1), // terminator
        ];
        int ublocks = (int)((MountEnd + 0x3FFFF) / 0x40000);
        List<byte> blob = [];
        Span<byte> word = stackalloc byte[8];
        ulong w0 = (ulong)(fidx.Length - 1) | (2UL << 24) | ((ulong)ublocks << 32);
        ulong w1 = 1 | ((ulong)(records.Length - 2) << 24);
        BinaryPrimitives.WriteUInt64LittleEndian(word, w0);
        blob.AddRange(word.ToArray());
        BinaryPrimitives.WriteUInt64LittleEndian(word, w1);
        blob.AddRange(word.ToArray());
        blob.AddRange(new byte[8]); // one outer-block record
        foreach (long f in fidx)
        {
            for (int k = 0; k < 5; k++)
            {
                blob.Add((byte)(f >> (8 * k)));
            }

            blob.Add(f == MountEnd ? (byte)0x40 : (byte)0);
        }

        // u2c: ublock 0 -> record 1 (A), ublock 1 -> record 3 (B rest), ublock 2 -> record 6 (metadata).
        blob.AddRange(u2cOverride ?? [1, 0, 0, 2, 5, 0, 0, 0, 0, 0]);
        while (blob.Count % 8 != 0)
        {
            blob.Add(0);
        }

        foreach (ulong r in records)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(word, r);
            blob.AddRange(word.ToArray());
            blob.Add(0);
        }

        return [.. blob];
    }

    private static (byte[] Stored, byte[] A, byte[] B, byte[] Meta) BuildStored()
    {
        byte[] a = [.. Enumerable.Range(0, 0x10).Select(i => (byte)(i + 1))];
        byte[] b = [.. Enumerable.Range(0, (int)FileBSize).Select(i => (byte)(i * 7))];
        byte[] meta = [.. Enumerable.Range(0, 0x10000).Select(i => (byte)(i ^ 0x5A))];
        byte[] stored = new byte[0x70000];
        a.CopyTo(stored, 0);
        b.CopyTo(stored, 0x10);
        Fill(0x20000, 0).CopyTo(stored, 0x50010);
        Fill(0x2FFF0 - 0x20000, 0).CopyTo(stored, 0x50018);
        meta.CopyTo(stored, 0x60000);
        return (stored, a, b, meta);
    }

    // Kraken fill block: (4 << 19) | (length − 1) << 2, 00 03 00, 0x4000 | value << 6.
    private static byte[] Fill(int length, byte value)
    {
        int header = (4 << 19) | ((length - 1) << 2);
        int v = 0x4000 | (value << 6);
        return [(byte)(header >> 16), (byte)(header >> 8), (byte)header, 0, 3, 0, (byte)(v >> 8), (byte)v];
    }

    [Fact]
    public void Parse_reads_counts_fidx_and_u2c()
    {
        NAPSLayout layout = NAPSLayout.Parse(BuildNaps());
        Assert.Equal(5, layout.FileOffsetCount);
        Assert.Equal(2, layout.CompressionType);
        Assert.Equal(3, layout.UBlockCount);
        Assert.Equal(MountEnd, layout.MountSize);
        Assert.Equal(MetaBase, layout.MetadataBase);
        Assert.Equal([1, 3, 6], layout.FirstCblockByUBlock);
        Assert.Equal(9, layout.Cblocks.Count);
        Assert.True(layout.Cblocks[5].IsRunBase);
        Assert.Equal(0x60000u >> 15, layout.Cblocks[5].TweakIndex);
        Assert.Equal(4, layout.Cblocks[4].Kde);
    }

    [Fact]
    public void Parse_rejects_a_truncated_layout() =>
        Assert.Throws<InvalidDataException>(() => NAPSLayout.Parse(BuildNaps().AsSpan(0, 80)));

    [Fact]
    public void Chunks_follow_file_starts_and_256K_steps()
    {
        (byte[] stored, _, _, _) = BuildStored();
        PS5InnerImage inner = PS5InnerImage.Open(new MemoryStream(stored), NAPSLayout.Parse(BuildNaps()));
        Assert.Empty(inner.Problems);
        Assert.Equal(
            [(0L, 0x10), (0x10L, 0x40000), (0x40010L, 0x10000), (0x50010L, 0x2FFF0), (MetaBase, 0x10000)],
            inner.Chunks.Select(c => (c.LogicalOffset, c.Length)));
        Assert.Equal(PS5ChunkKind.Fill, inner.Chunks[3].Kind);
        Assert.Equal(0x60000, inner.Chunks[4].StoredOffset);
    }

    [Fact]
    public void Read_returns_files_zero_gap_and_metadata()
    {
        (byte[] stored, byte[] a, byte[] b, byte[] meta) = BuildStored();
        PS5InnerImage inner = PS5InnerImage.Open(new MemoryStream(stored), NAPSLayout.Parse(BuildNaps()));
        byte[] buffer = new byte[MountEnd];
        inner.Read(0, buffer);
        Assert.Equal(a, buffer[..0x10]);
        Assert.Equal(b, buffer[0x10..(int)(0x10 + FileBSize)]);
        Assert.All(buffer[(int)(0x10 + FileBSize)..(int)MetaBase], x => Assert.Equal(0, x));
        Assert.Equal(meta, buffer[(int)MetaBase..]);
    }

    [Fact]
    public void Short_kind_0_record_is_a_constant_fill()
    {
        // B's 64 KiB tail stored as one 8-byte fill block of 0x83 (how Publishing Tools stores a constant file).
        (byte[] stored, _, _, _) = BuildStored();
        byte[] naps = BuildNaps();
        int cblocks = naps.Length - (9 * 9);
        BinaryPrimitives.WriteUInt64LittleEndian(naps.AsSpan(cblocks + (3 * 9)), Std(0x10 + 0x40000, kde: 0, uoff: 0x20, clen: 2 * 7));
        BinaryPrimitives.WriteUInt64LittleEndian(naps.AsSpan(cblocks + (4 * 9)), Std(0x10 + 0x40008, kde: 4, uoff: 0x20020, clen: 2 * 7));
        Fill(0x10000, 0x83).CopyTo(stored, 0x40010);
        Fill(0x20000, 0).CopyTo(stored, 0x40018);
        Fill(0x2FFF0 - 0x20000, 0).CopyTo(stored, 0x40020);
        PS5InnerImage inner = PS5InnerImage.Open(new MemoryStream(stored), NAPSLayout.Parse(naps));
        Assert.Equal(PS5ChunkKind.Fill, inner.Chunks[2].Kind);
        Assert.Equal(8, inner.Chunks[2].StoredLength);
        byte[] tail = new byte[0x10000];
        inner.Read(0x40010, tail);
        Assert.All(tail, x => Assert.Equal(0x83, x));
    }

    [Fact]
    public void Fill_block_longer_than_its_chunk_is_rejected()
    {
        // A fill block that declares a whole 128 KiB for the gap's 0xFFF0-byte second half.
        (byte[] stored, _, _, _) = BuildStored();
        Fill(0x20000, 0).CopyTo(stored, 0x50018);
        PS5InnerImage inner = PS5InnerImage.Open(new MemoryStream(stored), NAPSLayout.Parse(BuildNaps()));
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => inner.Read(0x50010, new byte[16]));
        Assert.Contains("not a Kraken fill stream", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Wrong_u2c_is_reported()
    {
        (byte[] stored, _, _, _) = BuildStored();
        PS5InnerImage inner = PS5InnerImage.Open(new MemoryStream(stored), NAPSLayout.Parse(BuildNaps([1, 0, 0, 1, 5, 0, 0, 0, 0, 0])));
        Assert.Contains(inner.Problems, p => p.StartsWith("u2c ublock 1:", StringComparison.Ordinal));
    }
}
