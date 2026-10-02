using System.Buffers.Binary;
using MkPFS.Core.PFSC;

namespace MkPFS.Tests.PFSC;

public sealed class PFSCVHashTests
{
    private static readonly PFSCVHashIdentity Identity = new(3L * 65536, (2L * 65536) + 100, 3, "PPSA00001.exfat", PFSCNestedType.Exfat);

    private static byte[] Hashes(long count)
    {
        byte[] hashes = new byte[count * 32];
        new Random(1).NextBytes(hashes);
        return hashes;
    }

    [Fact]
    public void Header_matches_game_compressor_layout()
    {
        byte[] header = PFSCVHash.BuildHeader(Identity);

        Assert.Equal(4096, header.Length);
        Assert.Equal("PFSCVHS1"u8.ToArray(), header[..8]);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)));
        Assert.Equal(4096u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)));
        Assert.Equal(65536L, BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16)));
        Assert.Equal(Identity.LogicalSize, BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(24)));
        Assert.Equal(Identity.NestedSize, BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(32)));
        Assert.Equal(3L, BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(40)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(48)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(52)));
        Assert.Equal("PPSA00001.exfat"u8.ToArray(), header[128..143]);
        Assert.Equal(0, header[143]);
    }

    [Fact]
    public void Written_sidecar_probes_as_used_and_reads_back_hashes()
    {
        using TempDir temp = new();
        string path = PFSCVHash.SidecarPath(Path.Combine(temp.Path, "game.ffpfsc"));
        byte[] hashes = Hashes(3);
        PFSCVHash.Write(path, Identity, hashes);

        Assert.EndsWith("game.ffpfsc.vhash", path, StringComparison.Ordinal);
        Assert.Equal(PFSCVHashMode.Used, PFSCVHash.Probe(path, Identity));
        Assert.Equal(hashes[64..96], PFSCVHash.ReadHash(path, 2));
    }

    [Fact]
    public void Probe_reports_missing_stale_and_invalid()
    {
        using TempDir temp = new();
        string path = Path.Combine(temp.Path, "x.vhash");
        Assert.Equal(PFSCVHashMode.Missing, PFSCVHash.Probe(path, Identity));

        PFSCVHash.Write(path, Identity, Hashes(3));
        Assert.Equal(PFSCVHashMode.Stale, PFSCVHash.Probe(path, Identity with { NestedSize = Identity.NestedSize + 1 }));
        Assert.Equal(PFSCVHashMode.Stale, PFSCVHash.Probe(path, Identity with { NestedName = "other.exfat" }));
        Assert.Equal(PFSCVHashMode.Invalid, PFSCVHash.Probe(path, Identity with { BlockCount = 4 }));

        byte[] bytes = File.ReadAllBytes(path);
        bytes[0] = (byte)'X';
        File.WriteAllBytes(path, bytes);
        Assert.Equal(PFSCVHashMode.Invalid, PFSCVHash.Probe(path, Identity));
    }

    [Theory]
    [InlineData("pfs_image.dat", PFSCNestedType.PFS)]
    [InlineData("GAME.FFPFS", PFSCNestedType.PFS)]
    [InlineData("PPSA00001.exfat", PFSCNestedType.Exfat)]
    [InlineData("data.bin", PFSCNestedType.Unknown)]
    public void Nested_type_follows_file_name(string name, PFSCNestedType expected) =>
        Assert.Equal(expected, PFSCVHashIdentity.TypeFromName(name));

    [Fact]
    public void Long_nested_names_are_truncated_like_snprintf()
    {
        PFSCVHashIdentity identity = Identity with { NestedName = new string('n', 300) };
        byte[] header = PFSCVHash.BuildHeader(identity);
        Assert.Equal((byte)'n', header[128 + 254]);
        Assert.Equal(0, header[128 + 255]);
    }
}
