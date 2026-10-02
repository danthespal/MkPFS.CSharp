using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using MkPFS.Core.Crypto;
using MkPFS.Core.Exfat;
using MkPFS.Core.IO;
using MkPFS.Core.PFS;

namespace MkPFS.Parity;

/// <summary>Unpack round trips and corrupted-image checks on the Python-built corpus.</summary>
public sealed class ReadSideParityTests
{
    private static readonly byte[] TestKey = Convert.FromHexString("00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff");

    private static string Tree(string caseName)
    {
        using JsonDocument manifest = Fixtures.Manifest();
        string tree = manifest.RootElement.GetProperty("cases").GetProperty(caseName).GetProperty("tree").GetString()!;
        string caseSource = Fixtures.PathOrSkip("goldens", caseName);
        return Directory.Exists(Path.Combine(caseSource, "src")) ? Path.Combine(caseSource, "src") : Fixtures.PathOrSkip("trees", tree);
    }

    private static Dictionary<string, string> HashFiles(string root) =>
        SourceScanner.GatherFiles(root).ToDictionary(
            p => Path.GetRelativePath(root, p).Replace('\\', '/'),
            p => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p))),
            StringComparer.Ordinal);

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "mkpfs-parity-" + Guid.NewGuid().ToString("N"));

    public static TheoryData<string, bool> RawCases => new()
    {
        { "raw_app", false },
        { "raw_app_ps4", false },
        { "raw_app_inode64", false },
        { "raw_app_case_sensitive", false },
        { "raw_app_nc", false },
        { "raw_app_signed", false },
        { "raw_app_signed64", false },
        { "raw_app_enc", false },
        { "raw_app_enc_key", true },
        { "raw_fpt_collision", false },
        { "raw_many_files", false },
        { "raw_ampr", false },
    };

    [Theory]
    [MemberData(nameof(RawCases))]
    public void Unpacking_raw_images_reproduces_the_source_files(string caseName, bool useKey)
    {
        string image = Fixtures.PathOrSkip("goldens", caseName, "out.ffpfs");
        string output = TempDir();
        try
        {
            ExtractionResult result = PFSExtractor.ExtractPFS(image, output, new PFSExtractOptions { Ekpfs = useKey ? TestKey : null });

            Assert.Empty(result.Errors);
            Assert.Equal(HashFiles(Tree(caseName)), HashFiles(output));
            Assert.Equal(result.FilesWritten, Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Count());
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [Theory]
    [InlineData("file_app_cpu1")]
    [InlineData("file_app_enc")]
    [InlineData("folder_app_basic")]
    [InlineData("folder_many_files")]
    public void Deep_unpack_of_exfat_wrapped_images_reproduces_the_tree(string caseName)
    {
        string image = Fixtures.PathOrSkip("goldens", caseName, "out.ffpfsc");
        string output = TempDir();
        try
        {
            ExtractionResult result = PFSExtractor.ExtractPFS(image, output, new PFSExtractOptions { Deep = true });

            Assert.Empty(result.Errors);
            Assert.Empty(result.Warnings);
            Assert.Equal(HashFiles(Tree(caseName)), HashFiles(output));
            Assert.True(Directory.Exists(Path.Combine(output, "empty_dir")) || caseName.Contains("many", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public void Plain_unpack_of_a_single_file_image_returns_the_inner_exfat()
    {
        string caseDir = Fixtures.PathOrSkip("goldens", "file_app_cpu1");
        string output = TempDir();
        try
        {
            ExtractionResult result = PFSExtractor.ExtractPFS(Path.Combine(caseDir, "out.ffpfsc"), output);

            Assert.Empty(result.Errors);
            Assert.Equal(File.ReadAllBytes(Path.Combine(caseDir, "in.exfat")), File.ReadAllBytes(Path.Combine(output, "in.exfat")));
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public void Deep_unpack_with_only_extracts_selected_paths_and_warns_on_misses()
    {
        string image = Fixtures.PathOrSkip("goldens", "folder_app_basic", "out.ffpfsc");
        string output = TempDir();
        try
        {
            ExtractionResult result = PFSExtractor.ExtractPFS(image, output, new PFSExtractOptions { Deep = true, Selectors = ["data/", "\\sce_sys\\param.json", "nope"] });

            Assert.Empty(result.Errors);
            Assert.Equal(["--only: no inner exFAT entry matched 'nope'"], result.Warnings);
            List<string> files = [.. HashFiles(output).Keys.Order(StringComparer.Ordinal)];
            Assert.Contains("sce_sys/param.json", files);
            Assert.All(files, f => Assert.True(f.StartsWith("data/", StringComparison.Ordinal) || f == "sce_sys/param.json", f));
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public void Raw_exfat_unpack_and_reader_match_the_tree()
    {
        string image = Fixtures.PathOrSkip("goldens", "exfat_app_basic", "out.exfat");
        string output = TempDir();
        try
        {
            ExtractionResult result = PFSExtractor.ExtractExfat(image, output);
            Assert.Empty(result.Errors);
            Assert.Equal(HashFiles(Tree("exfat_app_basic")), HashFiles(output));

            using FileStream stream = File.OpenRead(image);
            ExfatReader reader = new(stream);
            Assert.Equal(65536, reader.Geometry.ClusterSize);
            Assert.Contains(reader.RootEntries(), e => e.IsDir && e.Name == "empty_dir");
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public void Exfat_verify_reports_content_and_set_differences()
    {
        string image = Fixtures.PathOrSkip("goldens", "exfat_app_basic", "out.exfat");
        string source = TempDir();
        try
        {
            CopyTree(Tree("exfat_app_basic"), source);
            Assert.Empty(PFSExtractor.VerifyExfat(image, source).Errors);

            File.WriteAllText(Path.Combine(source, "eboot.bin"), "changed");
            File.WriteAllText(Path.Combine(source, "added.txt"), "x");
            File.Delete(Path.Combine(source, "data", "zeros.bin"));
            (List<string> errors, _) = PFSExtractor.VerifyExfat(image, source);

            Assert.Equal(
                ["missing files in exFAT image: added.txt", "extra files in exFAT image: data/zeros.bin", "content mismatch for eboot.bin"],
                errors);
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }

    [Fact]
    public void Corrupted_images_report_python_errors()
    {
        string golden = Fixtures.PathOrSkip("goldens", "raw_app", "out.ffpfs");
        string work = TempDir();
        Directory.CreateDirectory(work);
        try
        {
            List<string> Errors(Action<byte[]> corrupt, SourceTree? source = null)
            {
                byte[] bytes = File.ReadAllBytes(golden);
                corrupt(bytes);
                string path = Path.Combine(work, Guid.NewGuid().ToString("N") + ".ffpfs");
                File.WriteAllBytes(path, bytes);
                return PFSInspector.Inspect(path, new PFSInspectOptions { Source = source, Checklist = ChecklistMode.Never }).Errors;
            }

            Assert.Contains($"header magic mismatch: 0x{20130316:X16} != 0x{20130315:X16}", Errors(b => BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(8), 20130316)));
            Assert.Contains(Errors(b => BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(0x30), 500)), e => e == "inode count mismatch: header=500 parsed=390");

            // flat_path_table lives in block 3 (inode 1 db[0]); flipping its first hash breaks both directions.
            int fpt = 3 * 65536;
            List<string> fptErrors = Errors(b => b[fpt] ^= 0xFF);
            Assert.Contains(fptErrors, e => e.StartsWith("flat_path_table missing hash", StringComparison.Ordinal));
            Assert.Contains(fptErrors, e => e.StartsWith("flat_path_table has unexpected hash", StringComparison.Ordinal));

            // A raw (uncompressed) file's data change is caught by the source comparison only.
            PFSInspection clean = PFSInspector.Inspect(golden);
            PFSInode random = clean.Inodes[(int)clean.FileInodes["data/random.bin"]];
            Assert.False(random.IsCompressed);
            List<string> contentErrors = Errors(b => b[(random.Db[0] * 65536) + 5] ^= 1, SourceTree.FromDirectory(Tree("raw_app")));
            Assert.Equal(["content mismatch for file: data/random.bin"], contentErrors);

            string empty = Path.Combine(work, "empty.ffpfs");
            File.WriteAllBytes(empty, []);
            Assert.Equal(["failed to inspect image: truncated read at offset 0 (wanted 1024, got 0)"], PFSInspector.Inspect(empty).Errors);
            Assert.Equal([$"image path does not exist or is not a file: {Path.Combine(work, "missing")}"], PFSInspector.Inspect(Path.Combine(work, "missing")).Errors);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void Tampered_signed_image_reports_signature_mismatches()
    {
        string golden = Fixtures.PathOrSkip("goldens", "raw_app_signed", "out.ffpfs");
        Assert.Empty(PFSInspector.Inspect(golden, new PFSInspectOptions { Checklist = ChecklistMode.Never }).Errors);

        PFSInspection clean = PFSInspector.Inspect(golden);
        PFSInode eboot = clean.Inodes[(int)clean.FileInodes["eboot.bin"]];
        byte[] bytes = File.ReadAllBytes(golden);
        bytes[(eboot.Db[0] * 65536) + 100] ^= 1;
        string path = Path.Combine(Path.GetTempPath(), $"mkpfs-signed-{Guid.NewGuid():N}.ffpfs");
        try
        {
            File.WriteAllBytes(path, bytes);
            List<string> errors = PFSInspector.Inspect(path, new PFSInspectOptions { Checklist = ChecklistMode.Never }).Errors;
            Assert.Contains($"inode {eboot.Number} direct signature mismatch at db[0] -> block {eboot.Db[0]}", errors);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Encrypted_image_needs_the_right_key()
    {
        string image = Fixtures.PathOrSkip("goldens", "raw_app_enc_key", "out.ffpfs");
        Assert.Empty(PFSInspector.Inspect(image, new PFSInspectOptions { Ekpfs = TestKey, Checklist = ChecklistMode.Never }).Errors);
        Assert.NotEmpty(PFSInspector.Inspect(image, new PFSInspectOptions { Checklist = ChecklistMode.Never }).Errors);
        Assert.NotEmpty(PFSInspector.Inspect(image, new PFSInspectOptions { Ekpfs = TestKey, NewCrypt = true, Checklist = ChecklistMode.Never }).Errors);
    }

    private static void CopyTree(string from, string to)
    {
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
