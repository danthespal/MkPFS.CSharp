using System.Buffers.Binary;
using System.Text;
using MkPFS.Build;
using MkPFS.Cli;
using MkPFS.Cli.Output;
using MkPFS.Core.Metadata;

namespace MkPFS.Tests.Build;

public sealed class BatchTests
{
    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        StringWriter stdout = new() { NewLine = "\n" };
        StringWriter stderr = new() { NewLine = "\n" };
        int exit = MkPFSCli.Run(args, new CliContext(stdout, stderr, useColor: false, utf8: false, progress: false));
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static string Source(TempDir dir)
    {
        string source = dir.Dir("games");
        dir.File("games/PPSA05555-app/sce_sys/param.json", """{"titleId": "PPSA05555"}""");
        dir.File("games/PPSA05555-app/eboot.bin", string.Concat(Enumerable.Repeat("eboot ", 20_000)));
        dir.File("games/notes.txt", "ignored: not an image");
        dir.File("games/.hidden.exfat", "ignored: dot name");
        Assert.Equal(0, Run("pack", "exfat", Path.Combine(source, "PPSA05555-app"), Path.Combine(source, "Data.exfat")).Exit);
        return source;
    }

    [Fact]
    public void Discovery_takes_folders_and_image_files_sorted_case_insensitively()
    {
        using TempDir dir = new();
        string source = Source(dir);

        List<BatchItem> items = Batch.Discover(source, MkPFS.Core.Diagnostics.NullLog.Instance);

        Assert.Equal(["Data.exfat", "PPSA05555-app"], items.Select(i => i.Name));
        Assert.Equal([BatchItemKind.File, BatchItemKind.Folder], items.Select(i => i.Kind));
    }

    [Fact]
    public void Verify_checks_folder_items_against_their_source()
    {
        using TempDir dir = new();
        string source = Source(dir);
        string output = Path.Combine(dir.Path, "out");

        (int exit, string stdout, string err) = Run("batch", source, output, "--verify", "--cpu-count", "1");

        Assert.True(exit == 0, stdout + err);
        Assert.Contains("[1/2] ✅ Data.exfat", stdout, StringComparison.Ordinal);
        Assert.Contains("[2/2] ✅ PPSA05555-app", stdout, StringComparison.Ordinal);
        Assert.Contains("  Version : PS5", stdout, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(output, "PPSA05555-app.ffpfsc")));

        (int again, string rerun, _) = Run("batch", source, output);
        Assert.Equal(0, again);
        Assert.Contains("  0 done, 2 skipped", rerun, StringComparison.Ordinal);
    }

    [Fact]
    public void Output_inside_the_source_is_refused()
    {
        using TempDir dir = new();
        string source = Source(dir);

        (int exit, _, string err) = Run("batch", source, Path.Combine(source, "out"));

        Assert.Equal(1, exit);
        Assert.Contains("output directory cannot be inside the source directory", err, StringComparison.Ordinal);
    }

    [Fact]
    public void Any_file_content_and_non_ascii_folder_names_pack()
    {
        using TempDir dir = new();
        string source = dir.Dir("games");
        dir.File("games/broken.ffpfs", "not an image but packable as a file");
        dir.File("games/bad/données.txt", "x");

        (int exit, string stdout, _) = Run("batch", source, Path.Combine(dir.Path, "out"), "--cpu-count", "1");

        Assert.Equal(0, exit); // unlike --raw, single files and exFAT-wrapped folders accept any names and content
        Assert.Contains("2 done", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Unexpected_item_errors_are_recorded_and_the_batch_continues()
    {
        using TempDir dir = new();
        string source = Source(dir);
        List<BatchItem> items = Batch.Discover(source, MkPFS.Core.Diagnostics.NullLog.Instance);
        BatchOptions options = new()
        {
            SourceDir = source,
            OutputDir = Path.Combine(dir.Path, "out"),

            // An invalid level makes the encoder throw ArgumentOutOfRangeException, which used to abort the batch.
            Build = new MkPFS.Build.PFS.SingleFileBuildOptions { SourceFile = string.Empty, OutputPath = string.Empty, ZlibLevel = 99 },
        };

        BatchSummary summary = Batch.Run(options, items, MkPFS.Core.Diagnostics.NullLog.Instance, progress: null);

        Assert.Equal(2, summary.Errors);
        Assert.All(summary.Results, r => Assert.False(string.IsNullOrEmpty(r.ErrorMessage)));
    }

    [Fact]
    public void Empty_source_reports_no_items()
    {
        using TempDir dir = new();
        string source = dir.Dir("empty");

        (int exit, string stdout, _) = Run("batch", source, Path.Combine(dir.Path, "out"));

        Assert.Equal(0, exit);
        Assert.Contains($"No packable items found in {source}", stdout, StringComparison.Ordinal);
    }
}

public sealed class GameMetadataTests
{
    private static byte[] Sfo(params (string Key, string Value)[] entries)
    {
        using MemoryStream keys = new();
        using MemoryStream values = new();
        List<(int KeyOffset, int Length, int DataOffset)> index = [];
        foreach ((string key, string value) in entries)
        {
            index.Add(((int)keys.Length, Encoding.UTF8.GetByteCount(value) + 1, (int)values.Length));
            keys.Write(Encoding.ASCII.GetBytes(key + "\0"));
            byte[] data = Encoding.UTF8.GetBytes(value + "\0");
            values.Write(data);
            values.Write(new byte[(4 - (data.Length % 4)) % 4]);
        }

        int keyTable = 0x14 + (entries.Length * 16);
        int dataTable = keyTable + (int)keys.Length;
        byte[] sfo = new byte[dataTable + values.Length];
        "\0PSF"u8.CopyTo(sfo);
        BinaryPrimitives.WriteUInt32LittleEndian(sfo.AsSpan(8), (uint)keyTable);
        BinaryPrimitives.WriteUInt32LittleEndian(sfo.AsSpan(12), (uint)dataTable);
        BinaryPrimitives.WriteUInt32LittleEndian(sfo.AsSpan(16), (uint)entries.Length);
        for (int i = 0; i < index.Count; i++)
        {
            Span<byte> entry = sfo.AsSpan(0x14 + (i * 16), 16);
            BinaryPrimitives.WriteUInt16LittleEndian(entry, (ushort)index[i].KeyOffset);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[2..], 0x0204);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], (uint)index[i].Length);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)index[i].DataOffset);
        }

        keys.ToArray().CopyTo(sfo, keyTable);
        values.ToArray().CopyTo(sfo, dataTable);
        return sfo;
    }

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82];

    [Fact]
    public void Pkg_reads_content_id_sfo_fields_and_icon()
    {
        using TempDir dir = new();
        byte[] sfo = Sfo(("CATEGORY", "gd"), ("CONTENT_ID", "EP1234-CUSA01234_00-GAME0000000000000"), ("TITLE", "Test Game"), ("APP_VER", "01.02"));
        byte[] pkg = new byte[0x2000];
        "\u007fCNT"u8.CopyTo(pkg);
        BinaryPrimitives.WriteUInt32BigEndian(pkg.AsSpan(0x04), 0x01);
        Encoding.ASCII.GetBytes("EP1234-CUSA01234_00-GAME0000000000000").CopyTo(pkg, 0x40);
        BinaryPrimitives.WriteUInt32BigEndian(pkg.AsSpan(0x10), 2);
        BinaryPrimitives.WriteUInt32BigEndian(pkg.AsSpan(0x18), 0x100);
        (uint Id, int Offset, byte[] Data)[] entries = [(0x1000, 0x400, sfo), (0x1200, 0x1000, Png)];
        for (int i = 0; i < entries.Length; i++)
        {
            Span<byte> entry = pkg.AsSpan(0x100 + (i * 0x20), 0x20);
            BinaryPrimitives.WriteUInt32BigEndian(entry, entries[i].Id);
            BinaryPrimitives.WriteUInt32BigEndian(entry[0x10..], (uint)entries[i].Offset);
            BinaryPrimitives.WriteUInt32BigEndian(entry[0x14..], (uint)entries[i].Data.Length);
            entries[i].Data.CopyTo(pkg, entries[i].Offset);
        }

        string path = Path.Combine(dir.Path, "game.pkg");
        File.WriteAllBytes(path, pkg);

        GameMetadata meta = GameMetadataReader.Read(path);

        Assert.Equal("PS4GD", meta.PackageType);
        Assert.Equal("EP1234-CUSA01234_00-GAME0000000000000", meta.ContentId);
        Assert.Equal("CUSA01234", meta.TitleId);
        Assert.Equal("EUR", meta.Region);
        Assert.Equal("Test Game", meta.GameTitle);
        Assert.Equal("01.02", meta.Version);
        Assert.Equal(Png, meta.IconBytes);
        Assert.Empty(meta.Error);
    }

    [Fact]
    public void Ffpkg_scans_directory_names_png_and_apr_marker()
    {
        using TempDir dir = new();
        byte[] ffpkg = new byte[0x40000];
        new byte[] { 0x19, 0x01, 0x54, 0x19 }.CopyTo(ffpkg, 0xFFEC);
        byte[] name = Encoding.ASCII.GetBytes("PPSA09876-app");
        BinaryPrimitives.WriteUInt16LittleEndian(ffpkg.AsSpan(0x38000 + 4), (ushort)(8 + name.Length));
        ffpkg[0x38000 + 7] = (byte)name.Length;
        name.CopyTo(ffpkg, 0x38000 + 8);
        Png.CopyTo(ffpkg, 0x39000);
        "fakelib/libSceAmpr.sprx"u8.ToArray().CopyTo(ffpkg, 0x20000);
        string path = Path.Combine(dir.Path, "game.ffpkg");
        File.WriteAllBytes(path, ffpkg);

        GameMetadata meta = GameMetadataReader.Read(path);

        Assert.Equal("FFPKG", meta.PackageType);
        Assert.Equal("PPSA09876", meta.TitleId);
        Assert.Equal("PPSA09876", meta.ContentId);
        Assert.True(meta.HasAprEmu);
        Assert.Equal(Png, meta.IconBytes);
    }

    [Fact]
    public void Folder_falls_back_to_param_sfo_and_the_name()
    {
        using TempDir dir = new();
        string folder = dir.Dir("CUSA07777-backup");
        File.WriteAllBytes(Path.Combine(dir.Dir("CUSA07777-backup/sce_sys"), "param.sfo"), Sfo(("TITLE", "Old Game"), ("TITLE_ID", "CUSA07777")));

        GameMetadata meta = GameMetadataReader.Read(folder);

        Assert.Equal("FOLDER", meta.PackageType);
        Assert.Equal("Old Game", meta.GameTitle);
        Assert.Equal("CUSA07777", meta.TitleId);
        Assert.Equal("CUSA07777", meta.ContentId); // from the folder name
    }

    [Theory]
    [InlineData("en-US", "Batman: Legacy")]
    [InlineData("fr-FR", "Batman : L'héritage")]
    [InlineData("", "Batman: Legacy")] // no default: en-US
    public void Localized_title_uses_the_default_language_not_the_first_locale(string defaultLanguage, string expected)
    {
        // Retail param.json lists locales alphabetically, so ar-AE comes first (oracle finding 16).
        using TempDir dir = new();
        string folder = dir.Dir("PPSA16833-app");
        string defaultEntry = defaultLanguage.Length > 0 ? $"\"defaultLanguage\": \"{defaultLanguage}\", " : "";
        dir.File(
            "PPSA16833-app/sce_sys/param.json",
            "{\"titleId\": \"PPSA16833\", \"localizedParameters\": {\"ar-AE\": {\"titleName\": \"ليغو باتمان\"}, " + defaultEntry
            + "\"en-US\": {\"titleName\": \"Batman: Legacy\"}, \"fr-FR\": {\"titleName\": \"Batman : L'héritage\"}}}");

        Assert.Equal(expected, GameMetadataReader.Read(folder).GameTitle);
    }

    [Theory]
    [InlineData(0, "-")]
    [InlineData(512, "512 B")]
    [InlineData(1536, "2 KB")]
    [InlineData(5 * 1024 * 1024, "5.0 MB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3.00 GB")]
    public void Sizes_format_like_spectrum(long size, string expected) => Assert.Equal(expected, GameMetadataReader.FormatBytes(size));

    [Theory]
    [InlineData("UP0001-PPSA00001_00-X", "USA")]
    [InlineData("JP0001-PPSA00001_00-X", "JPN")]
    [InlineData("HP0001-PPSA00001_00-X", "ASIA")]
    [InlineData("-", "")]
    [InlineData("ZZ", "")]
    public void Regions_follow_the_content_id_prefix(string contentId, string region) =>
        Assert.Equal(region, GameMetadataReader.RegionFromContentId(contentId));

    [Fact]
    public void Missing_and_unknown_paths_do_not_throw()
    {
        using TempDir dir = new();
        Assert.StartsWith("Path not found:", GameMetadataReader.Read(Path.Combine(dir.Path, "nope.pkg")).Error, StringComparison.Ordinal);
        Assert.Equal("ISO", GameMetadataReader.Read(dir.File("disc.iso", "x")).PackageType);
        Assert.NotEmpty(GameMetadataReader.Read(dir.File("bad.ffpfs", "garbage")).PackageType);
    }
}
