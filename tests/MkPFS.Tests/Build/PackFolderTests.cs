using MkPFS.Cli;
using MkPFS.Cli.Output;
using MkPFS.Core.PFS;

namespace MkPFS.Tests.Build;

public sealed class PackFolderTests
{
    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        StringWriter stdout = new() { NewLine = "\n" };
        StringWriter stderr = new() { NewLine = "\n" };
        int exit = MkPFSCli.Run(args, new CliContext(stdout, stderr, useColor: false, utf8: false, progress: false));
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static string Tree(TempDir dir)
    {
        string source = dir.Dir("PPSA05678-app");
        dir.File("PPSA05678-app/sce_sys/param.json", """{"titleId": "PPSA05678"}""");
        dir.File("PPSA05678-app/eboot.bin", new string('e', 70_000));
        dir.File("PPSA05678-app/data/level.dat", string.Concat(Enumerable.Repeat("level data ", 20_000)));
        return source;
    }

    [Fact]
    public void Folder_is_wrapped_in_an_exfat_named_after_the_title_and_verifies()
    {
        using TempDir dir = new();
        string source = Tree(dir);
        string image = Path.Combine(dir.Path, "game.img");

        (int exit, string output, string err) = Run("pack", "folder", source, image, "--verify");

        string adjusted = Path.Combine(dir.Path, "game.ffpfsc");
        Assert.True(exit == 0, output + err);
        Assert.StartsWith("exFAT wrapping mode enabled, adjusting output file extension to .ffpfsc\n", output, StringComparison.Ordinal);
        Assert.Contains("Running post-create check...", output, StringComparison.Ordinal);
        Assert.Contains("PFS Check Report", output, StringComparison.Ordinal);

        // The inner exFAT holds exactly the source tree.
        string unpacked = Path.Combine(dir.Path, "unpacked");
        Assert.Equal(0, Run("unpack", adjusted, unpacked, "--deep").Exit);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(source, file);
            Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(unpacked, rel)));
        }

        Assert.Single(PFSInspector.Inspect(adjusted).FileInodes, f => f.Key == "PPSA05678.exfat");
    }

    [Fact]
    public void Verify_source_dir_compares_a_wrapped_image_with_its_inner_exfat()
    {
        // Python compares with the outer image and reports every game file missing (oracle finding 17).
        using TempDir dir = new();
        string source = Tree(dir);
        string image = Path.Combine(dir.Path, "game.ffpfsc");
        Assert.Equal(0, Run("pack", "folder", source, image, "--cpu-count", "1").Exit);

        (int exit, string output, string err) = Run("verify", image, "--source-dir", source);
        Assert.True(exit == 0, output + err);
        Assert.Contains($"Comparing {source} with the files inside PPSA05678.exfat", output, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(source, "eboot.bin"), new string('x', 70_000));
        dir.File("PPSA05678-app/extra.txt");
        (exit, output, err) = Run("verify", image, "--source-dir", source);
        Assert.Equal(1, exit);
        Assert.Contains("content mismatch for eboot.bin", err, StringComparison.Ordinal);
        Assert.Contains("missing files in exFAT image: extra.txt", err, StringComparison.Ordinal);

        // A folder that holds the .exfat itself is still compared with the outer image.
        string outer = dir.Dir("outer");
        Assert.Equal(0, Run("unpack", image, Path.Combine(dir.Path, "outer-unpacked")).Exit);
        File.Copy(Path.Combine(dir.Path, "outer-unpacked", "PPSA05678.exfat"), Path.Combine(outer, "PPSA05678.exfat"));
        (exit, output, err) = Run("verify", image, "--source-dir", outer);
        Assert.True(exit == 0, output + err);
        Assert.DoesNotContain("Comparing", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Dry_run_reports_the_inner_size_and_writes_nothing()
    {
        using TempDir dir = new();
        string source = Tree(dir);
        string image = Path.Combine(dir.Path, "out.ffpfsc");

        (int exit, string output, _) = Run("pack", "folder", source, image, "--dry-run");

        Assert.Equal(0, exit);
        Assert.Contains($"Dry run: would wrap {source} into a ", output, StringComparison.Ordinal);
        Assert.Contains($" exFAT and compress it to {image}; nothing written.", output, StringComparison.Ordinal);
        Assert.False(File.Exists(image));
    }

    [Theory]
    [InlineData("--exfat wrapping does not support --signed images", "--signed")]
    public void Unsupported_options_fail(string message, string option)
    {
        using TempDir dir = new();

        (int exit, _, string err) = Run("pack", "folder", Tree(dir), Path.Combine(dir.Path, "o.ffpfsc"), option);

        Assert.Equal(1, exit);
        Assert.Contains(message, err, StringComparison.Ordinal);
    }
}
