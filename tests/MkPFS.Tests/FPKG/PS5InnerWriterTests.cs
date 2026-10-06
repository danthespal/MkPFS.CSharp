using System.Buffers.Binary;
using MkPFS.Build.FPKG;
using MkPFS.Core.PFS.PS5;
using MkPFS.Core.PKG;
using MkPFS.Core.SELF;

namespace MkPFS.Tests.FPKG;

/// <summary>FPKG plan F6: PS5PkgTool's stored inner-image rules on synthetic inputs.</summary>
public sealed class PS5InnerWriterTests
{
    private static FPKGInput Input(string path, int size, byte fill = 0x5A)
    {
        byte[] data = new byte[size];
        Array.Fill(data, fill);
        return new FPKGInput { Path = path, Origin = FPKGInputOrigin.Generated, Data = data };
    }

    private static FPKGInput Executable(string path, int size)
    {
        FPKGInput input = Input(path, size);
        BinaryPrimitives.WriteUInt32LittleEndian(input.Data, SELFFile.Magic);
        return input;
    }

    private static (PS5InnerWriter Writer, PS5InnerResult Result, byte[] Image) Write(params FPKGInput[] inputs) => Write(false, inputs);

    private static (PS5InnerWriter Writer, PS5InnerResult Result, byte[] Image) Write(bool compress, params FPKGInput[] inputs)
    {
        PS5InnerWriter writer = PS5InnerWriter.Plan(inputs, 1_700_000_000, compress: compress);
        using MemoryStream image = new();
        PS5InnerResult result = writer.Write(image);
        return (writer, result, image.ToArray());
    }

    private static long StoredOffset(PS5InnerResult result, PS5InnerWriter writer, string path)
    {
        int index = writer.Files.Select((f, i) => (f, i)).Single(t => t.f.Path == path).i;
        return result.Chunks.First(c => c.FileIndex == index).StoredOffset;
    }

    [Fact]
    public void A_file_joins_the_run_unless_it_would_cross_a_64_KiB_block()
    {
        (PS5InnerWriter writer, PS5InnerResult result, _) = Write(
            Input("data/a.bin", 0x100), Input("data/b.bin", 0x9000), Input("data/c.bin", 0x7000), Input("data/d.bin", 0x50));
        Assert.Equal(0, StoredOffset(result, writer, "data/a.bin"));
        Assert.Equal(0x100, StoredOffset(result, writer, "data/b.bin"));
        Assert.Equal(0x10000, StoredOffset(result, writer, "data/c.bin"));
        Assert.Equal(0x17000, StoredOffset(result, writer, "data/d.bin"));
    }

    [Fact]
    public void Executables_and_the_file_after_one_start_a_new_run()
    {
        (PS5InnerWriter writer, PS5InnerResult result, _) = Write(
            Input("a.bin", 0x10), Executable("b.prx", 0x100), Input("c.bin", 0x10));
        Assert.Equal(0, StoredOffset(result, writer, "a.bin"));
        Assert.Equal(0x10000, StoredOffset(result, writer, "b.prx"));
        Assert.Equal(0x20000, StoredOffset(result, writer, "c.bin"));
    }

    [Fact]
    public void Files_are_cut_into_128_KiB_chunks_from_their_own_start()
    {
        (_, PS5InnerResult result, _) = Write(Input("a.bin", 0x10), Input("big.bin", 0x50001));
        int[] lengths = [.. result.Chunks.Where(c => c.Role == PS5InnerChunkRole.File && c.FileIndex == 1).Select(c => c.Length)];
        Assert.Equal([0x20000, 0x20000, 0x10001], lengths);
    }

    [Fact]
    public void Whole_gap_chunks_share_one_stored_block()
    {
        (_, PS5InnerResult result, byte[] image) = Write(Input("a.bin", 0x10));
        List<PS5InnerChunk> gap = [.. result.Chunks.Where(c => c.Role == PS5InnerChunkRole.Gap)];
        Assert.All(gap.Where(c => c.Length == 0x40000), c => Assert.Equal(result.GapStoredOffset, c.StoredOffset));
        Assert.Equal(result.GapStoredOffset + 16, gap[^1].StoredOffset);

        // The metadata follows a 0x400-byte PFSC header at the next 64 KiB boundary.
        PS5InnerChunk meta = result.Chunks.First(c => c.Role == PS5InnerChunkRole.Metadata);
        Assert.Equal(0x400, meta.StoredOffset % 0x10000);
        Assert.Equal("PFSC"u8.ToArray(), image.AsSpan((int)meta.StoredOffset - 0x400, 4).ToArray());
    }

    [Fact]
    public void Empty_files_have_no_afid_table_slot()
    {
        (PS5InnerWriter writer, _, _) = Write(Input("a.bin", 0x10), Input("empty.bin", 0), Input("b.bin", 0x10));
        Assert.Equal(writer.DataEnd, writer.Files.Single(f => f.Path == "empty.bin").LogicalOffset);

        // afid table (metadata block 5): count, then the inodes of the non-empty files and two −1 slots.
        Span<byte> table = writer.Metadata.AsSpan(5 * PS5InnerWriter.BlockSize);
        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(table));
        Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(table[12..]));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(table[20..]));
    }

    [Fact]
    public void Reader_maps_the_written_image_back_to_its_files()
    {
        FPKGInput[] inputs = [Input("a.bin", 0x10, 1), Input("big.bin", 0x50001, 2), Executable("eboot.bin", 0x3000), Input("empty.bin", 0)];
        (PS5InnerWriter writer, PS5InnerResult result, byte[] image) = Write(inputs);
        using MemoryStream stored = new(image);
        PS5InnerImage inner = PS5InnerImage.Open(stored, NAPSLayout.Parse(result.Naps));
        Assert.Equal(writer.MountSize, inner.Layout.MountSize);
        foreach (PS5InnerFile file in inner.ReadTree().Where(f => (f.Mode & 0xF000) == 0x8000))
        {
            using MemoryStream copy = new();
            inner.CopyFile(file, copy);
            Assert.Equal(inputs.Single(i => i.Path == file.Path).Data, copy.ToArray());
        }
    }

    private static FPKGInput Text(string path, int size)
    {
        byte[] line = "Kraken mode probe line, words words words.\n"u8.ToArray();
        byte[] data = new byte[size];
        for (int i = 0; i < size; i++)
        {
            data[i] = line[i % line.Length];
        }

        return new FPKGInput { Path = path, Origin = FPKGInputOrigin.Generated, Data = data };
    }

    private static PS5ChunkKind[] Kinds(PS5InnerImage inner, PS5InnerWriter writer, string path)
    {
        PS5InnerPlacement file = writer.Files.Single(f => f.Path == path);
        return [.. inner.Chunks.Where(c => c.LogicalOffset >= file.LogicalOffset && c.LogicalOffset < file.LogicalOffset + file.Size).Select(c => c.Kind)];
    }

    [Fact]
    public void Kraken_mode_compresses_every_chunk_but_a_files_last_and_never_executables()
    {
        FPKGInput[] inputs = [Text("a.txt", 0x50000), Input("const.bin", 0x40001, 7), Executable("b.prx", 0x40000), Text("small.txt", 0x8000)];
        (PS5InnerWriter writer, PS5InnerResult result, byte[] image) = Write(true, inputs);
        using MemoryStream stored = new(image);
        PS5InnerImage inner = PS5InnerImage.Open(stored, NAPSLayout.Parse(result.Naps));

        Assert.Equal([PS5ChunkKind.Kraken, PS5ChunkKind.Kraken, PS5ChunkKind.Raw], Kinds(inner, writer, "a.txt"));
        Assert.Equal([PS5ChunkKind.Fill, PS5ChunkKind.Fill, PS5ChunkKind.Raw], Kinds(inner, writer, "const.bin"));
        Assert.Equal([PS5ChunkKind.Raw, PS5ChunkKind.Raw], Kinds(inner, writer, "b.prx"));
        Assert.Equal([PS5ChunkKind.Raw], Kinds(inner, writer, "small.txt"));
        Assert.All(inner.Chunks.Where(c => c.LogicalOffset >= writer.MetadataBase), c => Assert.Equal(PS5ChunkKind.Kraken, c.Kind));
        Assert.Equal(2, result.Naps[3]);

        foreach (PS5InnerFile file in inner.ReadTree().Where(f => (f.Mode & 0xF000) == 0x8000))
        {
            using MemoryStream copy = new();
            inner.CopyFile(file, copy);
            Assert.Equal(inputs.Single(i => i.Path == file.Path).Data, copy.ToArray());
        }
    }

    [Fact]
    public void Kraken_batches_span_files_and_the_image_does_not_depend_on_the_worker_count()
    {
        // Many two-chunk files (one Kraken candidate each), empty files and an executable between them.
        List<FPKGInput> inputs = [Executable("eboot.bin", 0x30000)];
        for (int i = 0; i < 40; i++)
        {
            inputs.Add(i % 9 == 4 ? Input($"data/e{i:00}.bin", 0) : Text($"data/t{i:00}.txt", 0x30000 + (i * 0x100)));
        }

        byte[]? first = null;
        foreach (int workers in new[] { 1, 3, 16 })
        {
            PS5InnerWriter writer = PS5InnerWriter.Plan(inputs, 1_700_000_000, compress: true);
            writer.Workers = workers;
            using MemoryStream image = new();
            PS5InnerResult result = writer.Write(image);
            Assert.Equal(36, result.Chunks.Count(c => c.Role == PS5InnerChunkRole.File && c.StoredLength < c.Length));
            first ??= image.ToArray();
            Assert.Equal(first, image.ToArray());
        }
    }

    [Fact]
    public void Every_file_is_reported_as_it_is_written_in_afid_order()
    {
        FPKGInput[] inputs = [Text("b.txt", 0x30000), Input("empty.bin", 0), Input("a.bin", 0x100)];
        PS5InnerWriter writer = PS5InnerWriter.Plan(inputs, 1_700_000_000, compress: true);
        List<(string Path, int Chunks)> seen = [];
        writer.FileWritten = (file, chunks) => seen.Add((file.Path, chunks.Count));
        using MemoryStream image = new();
        writer.Write(image);

        Assert.Equal([.. writer.Files.Select(f => f.Path)], seen.Select(s => s.Path));
        Assert.Equal([("a.bin", 1), ("b.txt", 2), ("empty.bin", 0)], seen);
    }

    [Fact]
    public void Fast_mode_compresses_the_same_chunks_and_round_trips()
    {
        FPKGInput[] inputs = [Text("a.txt", 0x50000), Input("const.bin", 0x40001, 7), Executable("b.prx", 0x40000)];
        PS5InnerWriter writer = PS5InnerWriter.Plan(inputs, 1_700_000_000, compress: true);
        writer.Fast = true;
        using MemoryStream image = new();
        PS5InnerResult result = writer.Write(image);
        image.Position = 0;
        PS5InnerImage inner = PS5InnerImage.Open(image, NAPSLayout.Parse(result.Naps));

        Assert.Equal([PS5ChunkKind.Kraken, PS5ChunkKind.Kraken, PS5ChunkKind.Raw], Kinds(inner, writer, "a.txt"));
        Assert.Equal([PS5ChunkKind.Fill, PS5ChunkKind.Fill, PS5ChunkKind.Raw], Kinds(inner, writer, "const.bin"));
        foreach (PS5InnerFile file in inner.ReadTree().Where(f => (f.Mode & 0xF000) == 0x8000))
        {
            using MemoryStream copy = new();
            inner.CopyFile(file, copy);
            Assert.Equal(inputs.Single(i => i.Path == file.Path).Data, copy.ToArray());
        }
    }

    [Fact]
    public void Kraken_mode_packs_data_files_across_64_KiB_blocks()
    {
        (PS5InnerWriter writer, PS5InnerResult result, _) = Write(true, Input("data/a.bin", 0x100), Input("data/c.bin", 0x10000));
        Assert.Equal(0x100, StoredOffset(result, writer, "data/c.bin"));
    }
}
