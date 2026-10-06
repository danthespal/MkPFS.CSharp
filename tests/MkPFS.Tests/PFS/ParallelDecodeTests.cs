using MkPFS.Cli;
using MkPFS.Cli.Output;
using MkPFS.Core.PFS;
using MkPFS.Core.PFSC;

namespace MkPFS.Tests.PFS;

/// <summary>
/// The PFSC readers decode blocks in parallel batches (logical chunks) and with read-ahead (logical stream); both
/// must return the same bytes, and a corrupt block must fail exactly where a block-by-block decode would.
/// </summary>
public sealed class ParallelDecodeTests
{
    private const int BlockSize = 0x10000;
    private const int Blocks = 40;

    // A compressed single-file image of 40 distinct, compressible blocks; returns the source bytes.
    private static byte[] Build(TempDir dir, out string image)
    {
        byte[] data = new byte[(Blocks * BlockSize) - 1234];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)((i % 251) ^ (i / BlockSize * 7) ^ (i / 4096));
        }

        string source = Path.Combine(dir.Path, "PPSA01234.exfat");
        File.WriteAllBytes(source, data);
        image = Path.Combine(dir.Path, "out.ffpfsc");
        StringWriter output = new();
        int exit = MkPFSCli.Run(["pack", "file", source, image], new CliContext(output, output, useColor: false, utf8: false, progress: false));
        Assert.True(exit == 0, output.ToString());
        return data;
    }

    private static PFSInode FileInode(string image)
    {
        PFSInspection inspection = PFSInspector.Inspect(image, new PFSInspectOptions { Checklist = ChecklistMode.Never });
        return inspection.Inodes[(int)inspection.FileInodes.Single().Value];
    }

    // Overwrite the middle of stored block `block` so zlib rejects it.
    private static void Corrupt(string image, PFSInode inode, int block)
    {
        long payload;
        long blockStart;
        int length;
        using (PFSImage pfs = PFSImage.Open(image))
        using (FileStream read = File.OpenRead(image))
        {
            payload = pfs.BlockOffset(inode.Db[0]);
            PFSCReader reader = PFSCReader.Open(read, payload, inode.StoredSize);
            Assert.True(reader.IsBlockCompressed(block));
            blockStart = payload + reader.Offsets[block];
            length = reader.StoredLength(block);
        }

        using FileStream file = new(image, FileMode.Open, FileAccess.Write);
        file.Position = blockStart + (length / 2);
        file.Write(new byte[16].Select(_ => (byte)0xFF).ToArray());
    }

    [Fact]
    public void Logical_chunks_and_the_logical_stream_return_the_source_bytes()
    {
        using TempDir dir = new();
        byte[] data = Build(dir, out string image);
        PFSInode inode = FileInode(image);
        using PFSImage pfs = PFSImage.Open(image);

        // Batches smaller than the file, so several parallel batches run.
        using MemoryStream chunks = new();
        foreach (ReadOnlyMemory<byte> chunk in pfs.ReadLogicalChunks(inode, chunkSize: 4 * BlockSize))
        {
            chunks.Write(chunk.Span);
        }

        Assert.Equal(data, chunks.ToArray());

        // Sequential reads, then seeks backwards and forwards across the read-ahead window.
        using Stream logical = pfs.OpenLogical(inode);
        using MemoryStream copy = new();
        logical.CopyTo(copy, 5000);
        Assert.Equal(data, copy.ToArray());
        foreach (long at in new long[] { 30L * BlockSize + 17, 2L * BlockSize, 39L * BlockSize - 100, 0 })
        {
            byte[] part = new byte[300];
            logical.Position = at;
            logical.ReadExactly(part);
            Assert.Equal(data.AsSpan((int)at, 300).ToArray(), part);
        }
    }

    [Fact]
    public void A_corrupt_block_fails_after_the_blocks_before_it()
    {
        using TempDir dir = new();
        byte[] data = Build(dir, out string image);
        PFSInode inode = FileInode(image);
        Corrupt(image, inode, 9);
        using PFSImage pfs = PFSImage.Open(image);

        // Block 9 sits in the third batch of four: the first nine blocks still come out, then the error.
        using MemoryStream chunks = new();
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
        {
            foreach (ReadOnlyMemory<byte> chunk in pfs.ReadLogicalChunks(inode, chunkSize: 4 * BlockSize))
            {
                chunks.Write(chunk.Span);
            }
        });
        Assert.Contains("PFSC block 9", error.Message, StringComparison.Ordinal);
        Assert.Equal(data.AsSpan(0, 9 * BlockSize).ToArray(), chunks.ToArray());

        // The logical stream reads block 5 although its read-ahead runs into block 9, and fails on block 9 itself.
        using Stream logical = pfs.OpenLogical(inode);
        byte[] part = new byte[100];
        logical.Position = 5L * BlockSize;
        logical.ReadExactly(part);
        Assert.Equal(data.AsSpan(5 * BlockSize, 100).ToArray(), part);
        logical.Position = 9L * BlockSize;
        Assert.Contains("PFSC block 9", Assert.Throws<InvalidDataException>(() => logical.ReadExactly(part)).Message, StringComparison.Ordinal);
    }
}
