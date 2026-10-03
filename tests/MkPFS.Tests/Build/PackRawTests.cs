using MkPFS.Cli;
using MkPFS.Cli.Output;
using MkPFS.Core.PFS;

namespace MkPFS.Tests.Build;

public sealed class PackRawTests
{
    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        StringWriter stdout = new() { NewLine = "\n" };
        StringWriter stderr = new() { NewLine = "\n" };
        int exit = MkPFSCli.Run(args, new CliContext(stdout, stderr, useColor: false, utf8: false, progress: false));
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static string Tree(TempDir dir, bool game = true)
    {
        string source = dir.Dir("src");
        if (game)
        {
            dir.File("src/sce_sys/param.json", """{"titleId": "PPSA07777"}""");
            dir.File("src/eboot.bin", new string('e', 1000));
        }

        dir.File("src/data/big.dat", string.Concat(Enumerable.Repeat("compressible block of text ", 40_000)));
        dir.File("src/data/empty.bin", string.Empty);
        dir.File("src/data/sub/deep.txt", "deep");
        dir.Dir("src/empty_dir");
        return source;
    }

    private static void AssertRoundTrip(TempDir dir, string image, string source, params string[] keyArgs)
    {
        string unpacked = Path.Combine(dir.Path, "unpacked-" + Guid.NewGuid().ToString("N"));
        (int exit, string output, string err) = Run(["unpack", image, unpacked, .. keyArgs]);
        Assert.True(exit == 0, output + err);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(source, file);
            Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(unpacked, rel)));
        }
    }

    [Theory]
    [InlineData()]
    [InlineData("--signed")]
    [InlineData("--signed", "--inode-bits", "64")]
    [InlineData("--inode-bits", "64")]
    [InlineData("--case-sensitive")]
    [InlineData("--no-compress")]
    [InlineData("--version", "PS4")]
    [InlineData("--block-size", "16384")]
    public void Raw_images_verify_and_round_trip(params string[] options)
    {
        using TempDir dir = new();
        string source = Tree(dir);
        string image = Path.Combine(dir.Path, "out.img");

        (int exit, string output, string err) = Run(["pack", "folder", source, image, "--raw", "--verify", .. options]);

        string adjusted = Path.Combine(dir.Path, "out.ffpfs");
        Assert.True(exit == 0, output + err);
        Assert.StartsWith("Raw game files detected inside the source folder, adjusting output file extension to .ffpfs\n", output, StringComparison.Ordinal);
        Assert.Contains("PFS Full Verify Report", output, StringComparison.Ordinal);
        Assert.Contains("Image created successfully!", output, StringComparison.Ordinal);
        AssertRoundTrip(dir, adjusted, source);
    }

    [Fact]
    public void Encrypted_raw_image_needs_its_key()
    {
        using TempDir dir = new();
        string source = Tree(dir, game: false);
        string image = Path.Combine(dir.Path, "enc.ffpfsc");
        string key = string.Concat(Enumerable.Repeat("fedcba9876543210", 4));

        (int exit, string output, string err) = Run("pack", "folder", source, image, "--raw", "--encrypted", "--ekpfs-key", key, "--verify");

        Assert.True(exit == 0, output + err);
        AssertRoundTrip(dir, image, source, "--ekpfs-key", key);
        Assert.NotEqual(0, Run("verify", image).Exit);
    }

    [Fact]
    public void Colliding_paths_get_a_collision_resolver()
    {
        using TempDir dir = new();
        string source = dir.Dir("src");

        // "/Aa" and "/BB" share a flat_path_table hash (31 * 'A' + 'a' == 31 * 'B' + 'B') when case-sensitive.
        dir.File("src/Aa", "first");
        dir.File("src/BB", "second");
        string image = Path.Combine(dir.Path, "c.ffpfsc");

        (int exit, string output, string err) = Run("pack", "folder", source, image, "--raw", "--case-sensitive", "--verify");

        Assert.True(exit == 0, output + err);
        PFSInspection inspection = PFSInspector.Inspect(image, new PFSInspectOptions { Checklist = ChecklistMode.Never });
        Assert.Empty(inspection.Errors);
        Assert.Single(inspection.CollisionMap);
        AssertRoundTrip(dir, image, source);
    }

    [Fact]
    public void Non_ascii_names_are_rejected()
    {
        using TempDir dir = new();
        string source = dir.Dir("src");
        dir.File("src/données.txt", "x");

        (int exit, _, string err) = Run("pack", "folder", source, Path.Combine(dir.Path, "o.ffpfsc"), "--raw");

        Assert.Equal(1, exit);
        Assert.Contains("Source tree contains 1 file(s) with non-ASCII names. PFS images only support ASCII filenames:\n  données.txt", err, StringComparison.Ordinal);
    }

    [Fact]
    public void Require_game_files_checks_param_json_and_eboot()
    {
        using TempDir dir = new();
        string source = Tree(dir, game: false);

        (int exit, _, string err) = Run("pack", "folder", source, Path.Combine(dir.Path, "o.ffpfsc"), "--raw", "--require-game-files");

        Assert.Equal(1, exit);
        Assert.Contains($"Missing required file: {Path.Combine(source, "sce_sys", "param.json")}", err, StringComparison.Ordinal);
    }

    [Fact]
    public void Auto_fit_picks_the_smallest_footprint_and_dry_run_writes_nothing()
    {
        using TempDir dir = new();
        string source = dir.Dir("src");
        for (int i = 0; i < 20; i++)
        {
            dir.File($"src/small{i}.txt", new string('s', 3000));
        }

        string image = Path.Combine(dir.Path, "o.ffpfsc");
        (int exit, string output, _) = Run("pack", "folder", source, image, "--raw", "--block-size", "auto-fit", "--dry-run", "--verbose");

        Assert.Equal(0, exit);
        Assert.Contains("Auto-fit block size selected: 4,096 bytes (4 KiB), estimated file-data saving vs 64 KiB: ", output, StringComparison.Ordinal);
        Assert.Contains("[file] small0.txt: raw=3000 stored=3000", output, StringComparison.Ordinal);
        Assert.False(File.Exists(image));
    }

    [Theory]
    [InlineData("--signed")]
    [InlineData("--inode-bits", "64")]
    [InlineData("--use-spool")]
    public void Pack_file_options_that_need_the_folder_builder_work(params string[] options)
    {
        using TempDir dir = new();
        string source = dir.File("payload.bin", string.Concat(Enumerable.Repeat("payload ", 200_000)));
        string image = Path.Combine(dir.Path, "p.ffpfsc");

        (int exit, string output, string err) = Run(["pack", "file", source, image, "--verify", .. options]);

        Assert.True(exit == 0, output + err);
        if (options[0] != "--use-spool")
        {
            Assert.Contains("Direct-to-image streaming unavailable (", output, StringComparison.Ordinal);
        }

        PFSInspection inspection = PFSInspector.Inspect(image, new PFSInspectOptions
        {
            Source = SourceTree.FromSingleFile(source, "payload.bin"),
            Checklist = ChecklistMode.Never,
        });
        Assert.Empty(inspection.Errors);
        Assert.Equal(1, inspection.CheckedFiles);
    }

    [Theory]
    [InlineData(1024, true)]
    [InlineData(1025, false)]
    public void Deeply_nested_directories_are_walked_up_to_the_limit(int depth, bool accepted)
    {
        if (OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS limits paths to 1024 bytes, so the source tree cannot be created");
        }

        using TempDir dir = new();
        string source = dir.Dir("src");
        string deepest = Path.Combine([source, .. Enumerable.Repeat("d", depth)]);
        Directory.CreateDirectory(deepest);
        File.WriteAllText(Path.Combine(deepest, "a.txt"), "hi");
        string image = Path.Combine(dir.Path, "deep.ffpfs");
        Assert.Equal(0, Run("pack", "folder", source, image, "--raw", "--no-compress", "--skip-verification", "--no-adjust-output-file-extension").Exit);

        // Before the fix the tree walk recursed once per level and a ~600-level image ended the process.
        PFSInspection inspection = PFSInspector.Inspect(image, new PFSInspectOptions { VerifyPayloads = false });

        if (accepted)
        {
            Assert.Empty(inspection.Errors);
            (int exit, string output, _) = Run("tree", image);
            Assert.Equal(0, exit);
            Assert.EndsWith("`-- a.txt\n", output, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains(inspection.Errors, e => e.EndsWith("is nested deeper than 1024 levels", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Cancelling_parallel_compression_surfaces_as_cancellation()
    {
        using TempDir dir = new();
        string source = dir.Dir("src");
        dir.File("src/a.dat", string.Concat(Enumerable.Repeat("compressible block of text ", 10_000)));
        dir.File("src/b.dat", string.Concat(Enumerable.Repeat("another compressible block ", 10_000)));
        StringWriter output = new() { NewLine = "\n" };
        CliContext ctx = new(output, output, useColor: false, utf8: false, progress: true, progressSink: new CancelOnCompress());

        // The sink throws inside Parallel.ForEach, which wraps it; before the fix the GUI log showed the stack trace.
        Assert.ThrowsAny<OperationCanceledException>(() => MkPFSCli.Run(["pack", "folder", source, Path.Combine(dir.Path, "out.ffpfs"), "--raw", "--cpu-count", "2"], ctx));
        Assert.DoesNotContain("Unhandled exception", output.ToString(), StringComparison.Ordinal);
    }

    // Like the GUI job sink after Cancel: throws on the first block reported from the compression workers.
    private sealed class CancelOnCompress : MkPFS.Core.Diagnostics.IProgressSink
    {
        public void Step(string phase, long done, long total, long bytesProcessed)
        {
            if (phase == "compress" && done > 0)
            {
                throw new OperationCanceledException();
            }
        }

        public void Status(string message)
        {
        }
    }
}
