using System.Buffers.Binary;
using MkPFS.Build.Exfat;
using MkPFS.Cli.Commands;
using MkPFS.Core.Exfat;
using MkPFS.Core.PFS;

namespace MkPFS.Tests.Build;

public sealed class ExfatImageWriterTests
{
    private static string Pack(TempDir dir, string source, int? clusterSize = null)
    {
        string image = Path.Combine(dir.Path, "out.exfat");
        ExfatImageWriter.Write(source, image, clusterSize);
        return image;
    }

    private static Dictionary<string, ExfatEntry> Flatten(IEnumerable<ExfatEntry> entries)
    {
        Dictionary<string, ExfatEntry> all = new(StringComparer.Ordinal);
        foreach (ExfatEntry entry in entries)
        {
            all[entry.RelPath] = entry;
            foreach ((string key, ExfatEntry child) in Flatten(entry.Children))
            {
                all[key] = child;
            }
        }

        return all;
    }

    [Theory]
    [InlineData(null)]
    [InlineData(4096)]
    [InlineData(512)]
    public void Image_round_trips_through_the_reader_and_verify(int? clusterSize)
    {
        using TempDir dir = new();
        string source = dir.Dir("src");
        byte[] big = new byte[200_000];
        new Random(3).NextBytes(big);
        File.WriteAllBytes(Path.Combine(source, "big.bin"), big);
        File.WriteAllText(Path.Combine(source, "a name longer than fifteen characters.txt"), "long");
        File.WriteAllBytes(Path.Combine(source, "empty.dat"), []);
        Directory.CreateDirectory(Path.Combine(source, "empty dir"));
        Directory.CreateDirectory(Path.Combine(source, "sce_sys", "deep"));
        File.WriteAllText(Path.Combine(source, "sce_sys", "deep", "straße.txt"), "ß");
        File.WriteAllText(Path.Combine(source, ".DS_Store"), "ignored");

        string image = Pack(dir, source, clusterSize);

        using FileStream stream = File.OpenRead(image);
        ExfatReader reader = new(stream);
        Assert.Equal(clusterSize ?? ExfatImageWriter.DefaultClusterSize, reader.Geometry.ClusterSize);
        Dictionary<string, ExfatEntry> entries = Flatten(reader.RootEntries());
        Assert.Equal(
            ["a name longer than fifteen characters.txt", "big.bin", "empty dir", "empty.dat", "sce_sys", "sce_sys/deep", "sce_sys/deep/straße.txt"],
            entries.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(0u, entries["empty.dat"].FirstCluster);
        Assert.Equal(big, reader.ReadFile(entries["big.bin"]).SelectMany(m => m.ToArray()).ToArray());
        stream.Dispose();

        (List<string> errors, List<string> warnings) = PFSExtractor.VerifyExfat(image, source);
        Assert.Empty(errors);
        Assert.Empty(warnings);
        Assert.Equal(new FileInfo(image).Length, ExfatImageWriter.Plan(source, clusterSize).ImageSize);
    }

    [Fact]
    public void Entries_are_sorted_case_insensitively_like_python()
    {
        using TempDir dir = new();
        string source = dir.Dir("src");
        foreach (string name in new[] { "b.txt", "A.txt", "c.txt", "_x.txt" })
        {
            File.WriteAllText(Path.Combine(source, name), name);
        }

        using FileStream stream = File.OpenRead(Pack(dir, source));
        Assert.Equal(["_x.txt", "A.txt", "b.txt", "c.txt"], new ExfatReader(stream).RootEntries().Select(e => e.Name));
    }

    [Fact]
    public void Boot_region_checksum_and_backup_are_valid()
    {
        using TempDir dir = new();
        string source = dir.Dir("src");
        File.WriteAllText(Path.Combine(source, "f.txt"), "x");

        byte[] image = File.ReadAllBytes(Pack(dir, source));

        uint checksum = 0;
        for (int i = 0; i < 11 * 512; i++)
        {
            if (i is not (106 or 107 or 112))
            {
                checksum = ((checksum << 31) | (checksum >> 1)) + image[i];
            }
        }

        Assert.Equal(checksum, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(11 * 512)));
        Assert.Equal(image.AsSpan(0, 12 * 512).ToArray(), image.AsSpan(12 * 512, 12 * 512).ToArray());
        Assert.Equal(0xAA55, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(510)));
    }

    [Fact]
    public void Name_hash_uses_the_upcase_table()
    {
        // The exFAT table maps ß to itself (Python str.upper() gives "SS"); é maps to É.
        Assert.Equal('ß', ExfatUpcase.ToUpper('ß'));
        Assert.Equal('É', ExfatUpcase.ToUpper('é'));
        Assert.Equal('A', ExfatUpcase.ToUpper('a'));
        Assert.Equal('1', ExfatUpcase.ToUpper('1'));
        Assert.Equal(5836, ExfatUpcase.Table.Length);
    }

    [Fact]
    public void Output_directory_gets_the_title_id_name()
    {
        using TempDir dir = new();
        string source = dir.Dir("src");
        dir.File("src/sce_sys/param.json", """{"titleId": "PPSA01234"}""");
        string outDir = dir.Dir("out");

        string written = ExfatImageWriter.Write(source, outDir);

        Assert.Equal(Path.Combine(outDir, "PPSA01234.exfat"), written);
    }

    [Theory]
    [InlineData("auto", null, null)]
    [InlineData(" AUTO ", null, null)]
    [InlineData("", null, null)]
    [InlineData("32768", 32768, null)]
    [InlineData("65_536", 65536, null)]
    [InlineData("x", null, "--cluster-size must be an integer or 'auto'")]
    [InlineData("1000", null, "--cluster-size must be a power of two between 512 and 33554432")]
    [InlineData("256", null, "--cluster-size must be a power of two between 512 and 33554432")]
    [InlineData("67108864", null, "--cluster-size must be a power of two between 512 and 33554432")]
    public void Cluster_size_parses_like_python(string text, int? expected, string? error)
    {
        bool ok = PackCommands.TryParseClusterSize(text, out int? value, out string? message);

        Assert.Equal(error is null, ok);
        Assert.Equal(expected, value);
        Assert.Equal(error, message);
    }

    [Fact]
    public void Pack_exfat_refuses_to_overwrite_without_the_flag()
    {
        using TempDir dir = new();
        string source = dir.Dir("src");
        string existing = dir.File("out.exfat", "keep");
        StringWriter stdout = new() { NewLine = "\n" };
        StringWriter stderr = new() { NewLine = "\n" };

        int exit = MkPFS.Cli.MkPFSCli.Run(["pack", "exfat", source, existing], new MkPFS.Cli.Output.CliContext(stdout, stderr, false, false, false));

        Assert.Equal(1, exit);
        Assert.Equal($"output already exists (use --overwrite): {existing}\n", stderr.ToString());
        Assert.Equal("keep", File.ReadAllText(existing));
    }
}
