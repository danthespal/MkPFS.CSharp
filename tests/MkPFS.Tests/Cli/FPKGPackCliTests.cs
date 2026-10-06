using System.Text.Json;
using MkPFS.Cli;
using MkPFS.Cli.Output;
using MkPFS.Core.Metadata;
using MkPFS.Tests.FPKG;

namespace MkPFS.Tests.Cli;

/// <summary>FPKG plan F10: <c>pack fpkg</c> and reading its packages back from the command line.</summary>
public sealed class FPKGPackCliTests : IDisposable
{
    private const string ContentId = "UP9000-PPSA99999_00-MKPFSCLITESTS000";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mkpfs-f10-" + Guid.NewGuid().ToString("N"));

    public FPKGPackCliTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "out"));
        Write("eboot.bin", FPKGSourceTests.Elf());
        Write("sce_sys/icon0.png", FPKGSourceTests.Png());
        Write("data/text.txt", System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("packed by mkpfs pack fpkg\n", 20000))));
    }

    private string Source => Path.Combine(_root, "src");

    private string Out => Path.Combine(_root, "out");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Content_id_and_default_name_come_from_param_json_and_the_package_verifies()
    {
        Write("sce_sys/param.json", PS5ParamJson.Create(ContentId, "CLI", "01.02", "free"));
        (int exit, string output) = Run("pack", "fpkg", Source, "--verify");
        Assert.True(exit == 0, output);
        string package = Path.Combine(Out, $"{ContentId}-A0102-V0100.pkg");
        Assert.True(File.Exists(package), output);
        Assert.Contains("Source compare", output, StringComparison.Ordinal);
        Assert.Contains("Errors:  0", output, StringComparison.Ordinal);
        Assert.False(File.Exists(package + ".tmp"));
    }

    [Fact]
    public void Without_param_json_a_content_id_is_required()
    {
        (int exit, string output) = Run("pack", "fpkg", Source);
        Assert.Equal(1, exit);
        Assert.Contains("--content-id", output, StringComparison.Ordinal);

        (exit, output) = Run("pack", "fpkg", Source, Path.Combine(Out, "gen.pkg"), "--content-id", ContentId, "--title", "Generated");
        Assert.True(exit == 0, output);
        Assert.Contains("generated sce_sys/param.json", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Temp_folder_holds_the_staged_image_and_verbose_lists_every_file()
    {
        Write("sce_sys/param.json", PS5ParamJson.Create(ContentId, null, "01.00", "free"));
        string temp = Path.Combine(_root, "scratch");
        (int exit, string output) = Run("pack", "fpkg", Source, Path.Combine(Out, "t.pkg"), "--temp-folder", temp, "--verbose", "--cpu-count", "2");
        Assert.True(exit == 0, output);
        Assert.Contains("CPU cores:     2", output, StringComparison.Ordinal);
        Assert.Contains($"staging it in {temp}", output, StringComparison.Ordinal);
        Assert.Contains($"Temp folder:   {temp}", output, StringComparison.Ordinal);
        Assert.Contains("eboot.bin [fake-signed]:", output, StringComparison.Ordinal);
        Assert.Contains("data/text.txt:", output, StringComparison.Ordinal);
        Assert.Contains("Kraken", output, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp));
        Assert.True(File.Exists(Path.Combine(Out, "t.pkg")));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("257")]
    public void Cpu_count_outside_0_to_256_is_refused(string count)
    {
        Write("sce_sys/param.json", PS5ParamJson.Create(ContentId, null, "01.00", "free"));
        (int exit, string output) = Run("pack", "fpkg", Source, Path.Combine(Out, "c.pkg"), "--cpu-count", count);
        Assert.Equal(1, exit);
        Assert.Contains("--cpu-count must be between 0 and 256", output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Out, "c.pkg")));
    }

    [Fact]
    public void Dry_run_writes_nothing()
    {
        Write("sce_sys/param.json", PS5ParamJson.Create(ContentId, null, "01.00", "free"));
        (int exit, string output) = Run("pack", "fpkg", Source, Path.Combine(Out, "dry.pkg"), "--dry-run");
        Assert.True(exit == 0, output);
        Assert.Contains("Dry run: nothing written.", output, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Out));
    }

    [Theory]
    [InlineData("stored")]
    [InlineData("auto")]
    [InlineData("fast")]
    public void Seed_and_timestamp_make_the_package_reproducible(string compression)
    {
        Write("sce_sys/param.json", PS5ParamJson.Create(ContentId, null, "01.00", "free"));
        string[] options = ["--compression", compression, "--seed", "000102030405060708090a0b0c0d0e0f", "--timestamp", "1700000000", "--json"];
        (int exit, string output) = Run(["pack", "fpkg", Source, Path.Combine(Out, "a.pkg"), .. options]);
        Assert.True(exit == 0, output);
        using (JsonDocument json = JsonDocument.Parse(output))
        {
            Assert.Equal(compression, json.RootElement.GetProperty("compression").GetString());
            Assert.Equal(ContentId, json.RootElement.GetProperty("content_id").GetString());
            Assert.Equal("FakeSELF", json.RootElement.GetProperty("modules")[0].GetProperty("packed").GetString());
        }

        (exit, output) = Run(["pack", "fpkg", Source, Path.Combine(Out, "b.pkg"), .. options]);
        Assert.True(exit == 0, output);
        Assert.Equal(File.ReadAllBytes(Path.Combine(Out, "a.pkg")), File.ReadAllBytes(Path.Combine(Out, "b.pkg")));

        (exit, output) = Run("inspect", Path.Combine(Out, "a.pkg"), "--format", "json");
        Assert.True(exit == 0, output);
        using JsonDocument inspect = JsonDocument.Parse(output);
        Assert.Equal("fih-debug", inspect.RootElement.GetProperty("type").GetString());
        Assert.Equal(5, inspect.RootElement.GetProperty("inner_files").GetInt32());
    }

    private void Write(string rel, byte[] data)
    {
        string path = Path.Combine(Source, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
    }

    private (int Exit, string Output) Run(params string[] args)
    {
        using StringWriter stdout = new();
        using StringWriter stderr = new();
        CliContext ctx = new(stdout, stderr, useColor: false, utf8: false, progress: false) { WorkingDirectory = Out };
        int exit = MkPFSCli.Run(args, ctx);
        return (exit, stdout.ToString() + stderr.ToString());
    }
}
