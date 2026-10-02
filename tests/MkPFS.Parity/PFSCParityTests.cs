using System.Buffers.Binary;
using MkPFS.Core.Compression;
using MkPFS.Core.PFS;
using MkPFS.Core.PFSC;

namespace MkPFS.Parity;

/// <summary>
/// PFSC encoder/decoder against Python-built single-file images (<c>pack file</c> and <c>pack folder</c>
/// goldens). In those images inode 3 holds the nested file and its payload starts at block 6.
/// </summary>
public sealed class PFSCParityTests
{
    private const int Block = PFSConstants.PFSCLogicalBlockSize;

    private readonly record struct NestedFile(long PayloadOffset, long StoredSize, long LogicalSize, bool Compressed);

    private static NestedFile ReadInode3(string image)
    {
        using FileStream stream = File.OpenRead(image);
        byte[] inode = new byte[PFSConstants.InodeD32Size];
        stream.Seek(Block + (3 * PFSConstants.InodeD32Size), SeekOrigin.Begin);
        stream.ReadExactly(inode);
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(0x04));
        long size = BinaryPrimitives.ReadInt64LittleEndian(inode.AsSpan(0x08));
        long sizeCompressed = BinaryPrimitives.ReadInt64LittleEndian(inode.AsSpan(0x10));
        int db0 = BinaryPrimitives.ReadInt32LittleEndian(inode.AsSpan(0x64));
        return new NestedFile((long)db0 * Block, size, sizeCompressed, (flags & PFSConstants.InodeFlagCompressed) != 0);
    }

    public static TheoryData<string, int, int> PackFileCases => new()
    {
        // case, threshold-gain, min-file-gain (100 - max-compressed-ratio); level 7 everywhere.
        { "file_app_cpu1", 0, 0 },
        { "file_app_cpu4", 0, 0 },
        { "file_app_user_flags", 5, 0 },
        { "file_many_files", 0, 0 },
    };

    [Theory]
    [MemberData(nameof(PackFileCases))]
    public void Pack_file_PFSC_payload_is_byte_identical_to_python(string caseName, int thresholdGain, int minFileGain)
    {
        string input = Fixtures.PathOrSkip("goldens", caseName, "in.exfat");
        string image = Fixtures.PathOrSkip("goldens", caseName, "out.ffpfsc");
        NestedFile nested = ReadInode3(image);
        Assert.True(nested.Compressed);

        byte[] expected = new byte[nested.StoredSize];
        using (FileStream golden = File.OpenRead(image))
        {
            golden.Seek(nested.PayloadOffset, SeekOrigin.Begin);
            golden.ReadExactly(expected);
        }

        foreach (int workers in new[] { 1, 4 })
        {
            using FileStream source = File.OpenRead(input);
            using MemoryStream output = new();
            PFSCEncodeOptions options = new() { ThresholdGain = thresholdGain, MinFileGain = minFileGain, Workers = workers };
            PFSCEncodeResult result = PFSCEncoder.EncodeFile(source, source.Length, output, 0, options, cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(result.IsCompressed);
            Assert.Equal(nested.StoredSize, result.StoredSize);
            Assert.True(output.GetBuffer().AsSpan(0, (int)result.StoredSize).SequenceEqual(expected), $"{caseName} workers={workers}: payload differs from Python");
        }
    }

    public static TheoryData<string> CompressedImages => new()
    {
        "file_app_cpu1",
        "file_app_user_flags",
        "file_many_files",
        "file_app_isal",
        "folder_app_basic",
        "folder_many_files",
        "folder_ampr",
    };

    [Theory]
    [MemberData(nameof(CompressedImages))]
    public void Every_python_PFSC_payload_decodes(string caseName)
    {
        string image = Fixtures.PathOrSkip("goldens", caseName, "out.ffpfsc");
        NestedFile nested = ReadInode3(image);
        Assert.True(nested.Compressed);

        using FileStream stream = File.OpenRead(image);
        PFSCReader reader = PFSCReader.Open(stream, nested.PayloadOffset, nested.StoredSize);
        using MemoryStream decoded = new();
        Assert.Equal(nested.LogicalSize, reader.DecodeTo(decoded, nested.LogicalSize));

        string input = Path.Combine(Path.GetDirectoryName(image)!, "in.exfat");
        if (File.Exists(input))
        {
            Assert.True(decoded.GetBuffer().AsSpan(0, (int)decoded.Length).SequenceEqual(File.ReadAllBytes(input)), $"{caseName}: decoded payload differs from the source exFAT");
        }
        else
        {
            Assert.Equal("EXFAT   "u8.ToArray(), decoded.GetBuffer().AsSpan(3, 8).ToArray());
        }
    }

    [Fact]
    public void ISAL_image_has_far_distance_blocks_and_zlib_reencode_has_none()
    {
        string image = Fixtures.PathOrSkip("goldens", "file_app_isal", "out.ffpfsc");
        string input = Fixtures.PathOrSkip("goldens", "file_app_isal", "in.exfat");
        NestedFile nested = ReadInode3(image);

        using FileStream stream = File.OpenRead(image);
        int isalFar = CountFarBlocks(PFSCReader.Open(stream, nested.PayloadOffset, nested.StoredSize), out int isalCompressed);

        using FileStream source = File.OpenRead(input);
        using MemoryStream output = new();
        PFSCEncodeResult result = PFSCEncoder.EncodeFile(source, source.Length, output, 0, new PFSCEncodeOptions(), cancellationToken: TestContext.Current.CancellationToken);
        int zlibFar = CountFarBlocks(PFSCReader.Open(output, 0, result.StoredSize), out int zlibCompressed);

        Assert.True(isalCompressed > 0 && zlibCompressed > 0);
        Assert.True(isalFar > 0, "expected ISA-L blocks with back-references beyond 32506 bytes");
        Assert.Equal(0, zlibFar);
    }

    private static int CountFarBlocks(PFSCReader reader, out int compressedBlocks)
    {
        int far = 0;
        compressedBlocks = 0;
        byte[] buffer = new byte[Block];
        for (long i = 0; i < reader.BlockCount; i++)
        {
            if (!reader.IsBlockCompressed(i))
            {
                continue;
            }

            compressedBlocks++;
            int length = reader.ReadStoredBlock(i, buffer);
            DeflateStreamReport report = DeflateInspector.InspectZlib(buffer.AsSpan(0, length));
            Assert.True(report.Valid, $"block {i}: {report.Error}");
            if (report.HasFarDistance)
            {
                far++;
            }
        }

        return far;
    }
}
