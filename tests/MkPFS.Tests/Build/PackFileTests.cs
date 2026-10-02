using MkPFS.Cli;
using MkPFS.Cli.Output;
using MkPFS.Core.PFS;

namespace MkPFS.Tests.Build;

public sealed class PackFileTests
{
    private static (int Exit, string Out, string Err) Run(string? stdin, params string[] args)
    {
        StringWriter stdout = new() { NewLine = "\n" };
        StringWriter stderr = new() { NewLine = "\n" };
        TextReader? input = stdin is null ? null : new StringReader(stdin);
        int exit = MkPFSCli.Run(args, new CliContext(stdout, stderr, useColor: false, utf8: false, progress: false, input));
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static string Source(TempDir dir, string name = "PPSA01234-app.exfat", int size = 300_000)
    {
        byte[] data = new byte[size];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)((i % 97) ^ (i / 4096)); // compressible
        }

        string path = Path.Combine(dir.Path, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    [Fact]
    public void Default_pack_runs_structure_verify_and_round_trips()
    {
        using TempDir dir = new();
        string source = Source(dir);
        string image = Path.Combine(dir.Path, "out.ffpfsc");

        (int exit, string output, string err) = Run(null, "pack", "file", source, image);

        Assert.True(exit == 0, output + err);
        Assert.Contains("WARNING: The inner file was renamed to a safer file name to improve compatibility.\r\n\"PPSA01234-app.exfat\" -> \"PPSA01234.exfat\"", output, StringComparison.Ordinal);
        Assert.Contains("Running post-pack structure verification...", output, StringComparison.Ordinal);
        Assert.Contains("PFS Structure Verify Report", output, StringComparison.Ordinal);
        Assert.Contains("Image created successfully!", output, StringComparison.Ordinal);

        PFSInspection inspection = PFSInspector.Inspect(image, new PFSInspectOptions
        {
            Source = SourceTree.FromSingleFile(source, "PPSA01234.exfat"),
            Checklist = ChecklistMode.Never,
        });
        Assert.Empty(inspection.Errors);
        Assert.Equal(1, inspection.CompressedFiles);
        Assert.Equal(1, inspection.CheckedFiles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Output_equal_to_source_is_refused_and_source_is_kept(bool useSpool)
    {
        using TempDir dir = new();
        string source = Source(dir, "game.ffpfsc");
        byte[] before = File.ReadAllBytes(source);
        string[] args = useSpool ? ["pack", "file", source, source, "--use-spool"] : ["pack", "file", source, source];

        // "y" would have confirmed the overwrite prompt, which deleted the source before the build.
        (int exit, string output, string err) = Run("y\n", args);

        Assert.Equal(1, exit);
        Assert.Contains("output image must not be the source itself", output + err, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(source));
    }

    [Fact]
    public void Full_verify_and_encryption_with_a_key()
    {
        using TempDir dir = new();
        string source = Source(dir, "data.bin");
        string image = Path.Combine(dir.Path, "enc.ffpfsc");
        string key = string.Concat(Enumerable.Repeat("0123456789abcdef", 4));

        (int exit, string output, string err) = Run(null, "pack", "file", source, image, "--encrypted", "--ekpfs-key", key, "--verify");

        Assert.True(exit == 0, output + err);
        Assert.Contains("PFS Full Verify Report", output, StringComparison.Ordinal);
        Assert.Contains("Files hash-checked:    1", output, StringComparison.Ordinal);
        Assert.NotEmpty(PFSInspector.Inspect(image).Errors); // wrong (zero) key
    }

    [Fact]
    public void Output_extension_is_adjusted_and_dry_run_writes_nothing()
    {
        using TempDir dir = new();
        string source = Source(dir, "data.bin");

        (int exit, string output, _) = Run(null, "pack", "file", source, Path.Combine(dir.Path, "out.img"), "--dry-run");

        Assert.Equal(0, exit);
        Assert.StartsWith("Single file streaming mode enabled, adjusting output file extension to .ffpfsc\n", output, StringComparison.Ordinal);
        Assert.Contains("Dry run:           yes", output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(dir.Path, "out.ffpfsc")));
    }

    [Fact]
    public void Existing_output_is_replaced_only_after_yes()
    {
        using TempDir dir = new();
        string source = Source(dir, "data.bin");
        string image = dir.File("out.ffpfsc", "old");

        (int noExit, string noOut, _) = Run("n\n", "pack", "file", source, image, "--skip-verification");
        Assert.Equal(0, noExit);
        Assert.Contains("Overwrite? [Y/n] Operation cancelled.", noOut, StringComparison.Ordinal);
        Assert.Equal("old", File.ReadAllText(image));

        (int eofExit, _, _) = Run(null, "pack", "file", source, image, "--skip-verification");
        Assert.Equal(0, eofExit);
        Assert.Equal("old", File.ReadAllText(image));

        (int yesExit, _, _) = Run("maybe\ny\n", "pack", "file", source, image, "--skip-verification");
        Assert.Equal(0, yesExit);
        Assert.Empty(PFSInspector.Inspect(image).Errors);
    }

    [Theory]
    [InlineData("--ekpfs-key requires --encrypted", "--ekpfs-key", "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff")]
    [InlineData("--threshold-gain must be within 0..100", "--threshold-gain", "101")]
    [InlineData("--block-size must be a power of two", "--block-size", "70000")]
    [InlineData("--block-size must be between 4096 and 2097152", "--block-size", "2048")]
    [InlineData("--compression-level must be within 0..9", "--compression-level", "10")]
    [InlineData("--verify and --skip-verification cannot be used together", "--verify", "--skip-verification")]
    public void Invalid_options_fail_with_python_messages(string message, params string[] extra)
    {
        using TempDir dir = new();
        string source = Source(dir, "data.bin");

        (int exit, _, string err) = Run(null, ["pack", "file", source, Path.Combine(dir.Path, "o.ffpfsc"), .. extra]);

        Assert.Equal(1, exit);
        Assert.Contains(message, err, StringComparison.Ordinal);
    }

    [Fact]
    public void Exclusive_options_are_rejected()
    {
        using TempDir dir = new();
        string source = Source(dir, "data.bin");

        (int exit, _, string err) = Run(null, "pack", "file", source, Path.Combine(dir.Path, "o.ffpfsc"), "--compress", "--no-compress");

        Assert.NotEqual(0, exit);
        Assert.Contains("argument --no-compress: not allowed with argument --compress", err, StringComparison.Ordinal);
    }
}
