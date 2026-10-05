using System.Security.Cryptography;
using MkPFS.Build;
using MkPFS.Build.AMPRPack;
using MkPFS.Build.Exfat;
using MkPFS.Build.PFS;
using MkPFS.Cli;
using MkPFS.Cli.Output;
using MkPFS.Core.AMPR;
using MkPFS.Core.Diagnostics;
using MkPFS.Core.Exfat;

namespace MkPFS.Tests.AMPR;

/// <summary><c>ampr game</c>: a folder that runs from packs as is (an extension over ampr_pack.py).</summary>
public sealed class AMPRGameBuilderTests
{
    private sealed class ListLog : IMkPFSLog
    {
        public List<string> Lines { get; } = [];

        public void Log(LogLevel level, string message, LogIcon icon = LogIcon.None) => Lines.Add(message);
    }

    // A stand-in for AMPR Emu 0.4.2.1+: what matters is the embedded manifest magic.
    private const string PackCapable = "ELF...AMPRPAK4.../app0/ampr_assets.index";

    private static readonly string Level = string.Concat(Enumerable.Repeat("terrain mesh texture vertex ", 20_000));

    private static string Game(TempDir dir, string library = "fakelib")
    {
        dir.File("game/sce_sys/param.json", """{"titleId":"PPSA01234","contentVersion":"01.000.000"}""");
        dir.File("game/eboot.bin", "eboot");
        dir.File("game/sce_module/libc.prx", "module");
        dir.File("game/data/level0.dat", Level);
        dir.File("game/data/Niveau_é.dat", Level + "é");
        dir.File($"game/{library}/libSceAmpr.sprx", "old emulator without packs");
        dir.File($"game/{library}/libkernel.sprx", "kernel");
        dir.Dir("game/empty/dir");
        dir.File("libs/libSceAmpr.sprx", PackCapable);
        dir.File("libs/libScePlayGo.sprx", "playgo");
        dir.File("libs/libkernel.sprx", "kernel");
        dir.File("libs/.DS_Store", "ignored");
        return Path.Combine(dir.Path, "game");
    }

    private static AMPRGameOptions Options(TempDir dir, string? image = null) => new()
    {
        Config = CompressAll(),
        LibsDir = Path.Combine(dir.Path, "libs"),
        ExfatImage = image,
    };

    // Compress every file but executables, modules, system files and the libraries.
    private static AMPRPackConfig CompressAll() => AMPRProfiler.ToConfig("""
        [pack]
        default_action = "compress"

        [[rule]]
        action = "loose"
        include = ["eboot.bin", "*/eboot.bin", "*.prx", "*.sprx", "sce_sys/*", "sce_module/*", "fakelib/*", "fakelib2/*", "ampr_emu.index"]
        """);

    private static Dictionary<string, string> Hashes(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(root, f), f => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))));

    [Fact]
    public void Output_holds_libraries_index_packs_and_loose_files_and_the_source_is_untouched()
    {
        using TempDir dir = new();
        string game = Game(dir);
        Dictionary<string, string> before = Hashes(game);
        string output = Path.Combine(dir.Path, "out");
        ListLog log = new();

        AMPRGameResult result = AMPRGameBuilder.Build(game, output, Options(dir), log);

        Assert.Equal(before, Hashes(game));
        Assert.Equal("fakelib", result.LibraryDir);
        Assert.Equal(2, result.Pack.Stats.FilesPacked);
        Assert.Contains("  libSceAmpr.sprx: replaced", log.Lines);
        Assert.Contains("  libScePlayGo.sprx: added", log.Lines);
        Assert.Contains("  libkernel.sprx: already up to date", log.Lines);
        Assert.Equal(PackCapable, File.ReadAllText(Path.Combine(output, "fakelib", "libSceAmpr.sprx")));
        Assert.False(File.Exists(Path.Combine(output, "fakelib", ".DS_Store")));

        // Packed files are only in the packs; everything else is copied; empty folders are kept.
        Assert.False(File.Exists(Path.Combine(output, "data", "level0.dat")));
        Assert.False(File.Exists(Path.Combine(output, "data", "Niveau_é.dat")));
        foreach (string loose in (string[])["eboot.bin", "sce_module/libc.prx", "sce_sys/param.json"])
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(game, loose)), File.ReadAllBytes(Path.Combine(output, loose)));
        }

        Assert.True(Directory.Exists(Path.Combine(output, "empty", "dir")));
        Assert.Contains(log.Lines, l => l.StartsWith("  Loose files: ", StringComparison.Ordinal));

        // The index is exactly the index of the final tree: unpack the packs over a copy and index that.
        string final = dir.Dir("final");
        foreach (string file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(output, file);
            if (!relative.StartsWith("ampr_", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(final, relative))!);
                File.Copy(file, Path.Combine(final, relative));
                File.SetLastWriteTimeUtc(Path.Combine(final, relative), File.GetLastWriteTimeUtc(file));
            }
        }

        AMPRPackTools.Extract(result.Pack.IndexPath, final, null, overwrite: false, preserveMTime: true);
        AmprIndex.Build(final, Path.Combine(dir.Path, "final.index"));
        Assert.Equal(File.ReadAllBytes(Path.Combine(dir.Path, "final.index")), File.ReadAllBytes(Path.Combine(output, AmprIndex.IndexName)));
    }

    [Fact]
    public void A_game_with_fakelib2_gets_its_libraries_there()
    {
        using TempDir dir = new();
        string game = Game(dir, "fakelib2");
        string output = Path.Combine(dir.Path, "out");

        AMPRGameResult result = AMPRGameBuilder.Build(game, output, Options(dir), new ListLog());

        Assert.Equal("fakelib2", result.LibraryDir);
        Assert.Equal(PackCapable, File.ReadAllText(Path.Combine(output, "fakelib2", "libSceAmpr.sprx")));
        Assert.False(Directory.Exists(Path.Combine(output, "fakelib")));
    }

    [Fact]
    public void Inputs_that_cannot_give_a_playable_folder_are_rejected_before_anything_is_written()
    {
        using TempDir dir = new();
        string game = Game(dir);
        string output = Path.Combine(dir.Path, "out");
        string Error(AMPRGameOptions options, string target) =>
            Assert.Throws<BuildException>(() => AMPRGameBuilder.Build(game, target, options, new ListLog())).Message;

        // The game's own emulator cannot read packs.
        Assert.StartsWith("fakelib/libSceAmpr.sprx cannot read asset packs", Error(Options(dir) with { LibsDir = null }, output), StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));

        Assert.StartsWith("library folder not found", Error(Options(dir) with { LibsDir = Path.Combine(dir.Path, "nope") }, output), StringComparison.Ordinal);
        Assert.Equal("the game folder and the output folder must not contain each other", Error(Options(dir), Path.Combine(game, "out")));

        dir.File("busy/file.txt");
        Assert.StartsWith("the output folder must be new or empty", Error(Options(dir), Path.Combine(dir.Path, "busy")), StringComparison.Ordinal);

        Assert.Equal("the exFAT image must be outside the game and output folders", Error(Options(dir, Path.Combine(output, "x.exfat")), output));

        dir.File("game/ampr_assets.index", "old manifest");
        Assert.StartsWith("the game folder already has ampr_assets.index", Error(Options(dir), output), StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_build_leaves_the_output_folder_empty()
    {
        using TempDir dir = new();
        string game = Game(dir);
        string output = dir.Dir("out");
        AMPRGameOptions options = Options(dir) with { Config = CompressAll() };
        options.Config.RequiredPacked = ["eboot.bin"]; // the rules keep it loose, so the packer fails

        Assert.ThrowsAny<Exception>(() => AMPRGameBuilder.Build(game, output, options, new ListLog()));

        Assert.True(Directory.Exists(output));
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
    }

    [Fact]
    public void The_exfat_image_holds_the_playable_folder()
    {
        using TempDir dir = new();
        string game = Game(dir);
        string output = Path.Combine(dir.Path, "out");

        AMPRGameResult result = AMPRGameBuilder.Build(game, output, Options(dir, dir.Path), new ListLog());

        Assert.Equal(Path.Combine(dir.Path, "PPSA01234.exfat"), result.ExfatImage);
        using FileStream stream = File.OpenRead(result.ExfatImage!);
        ExfatReader reader = new(stream);
        Assert.Equal(64 * 1024, reader.Geometry.ClusterSize);
        List<string> names = [.. reader.RootEntries().Select(e => e.Name).Order(StringComparer.Ordinal)];
        Assert.Equal(["ampr_assets-000.pak", "ampr_assets.index", "ampr_assets.index.crc", "ampr_emu.index", "data", "eboot.bin", "empty", "fakelib", "sce_module", "sce_sys"], names);
    }

    [Fact]
    public void The_exfat_image_can_keep_free_space_for_writes()
    {
        using TempDir dir = new();
        string game = Game(dir);
        string output = Path.Combine(dir.Path, "out");

        AMPRGameResult result = AMPRGameBuilder.Build(game, output, Options(dir, dir.Path) with { ExfatFreeBytes = 3 * 1024 * 1024 + 1 }, new ListLog());

        using FileStream stream = File.OpenRead(result.ExfatImage!);
        ExfatReader reader = new(stream);
        Assert.Contains(reader.RootEntries(), e => e.Name == "eboot.bin");
        ExfatImageWriter tight = ExfatImageWriter.Plan(output);
        ExfatImageWriter roomy = ExfatImageWriter.Plan(output, null, 3 * 1024 * 1024 + 1);
        Assert.Equal(0, tight.FreeBytes);
        Assert.Equal(49L * 64 * 1024, roomy.FreeBytes); // 3 MiB + 1 byte, rounded up to 64 KiB clusters
        Assert.Equal(stream.Length, roomy.ImageSize);
        Assert.True(roomy.ImageSize >= tight.ImageSize + roomy.FreeBytes);
    }

    [Fact]
    public void Cli_game_builds_the_folder_and_reports_each_step()
    {
        using TempDir dir = new();
        string game = Game(dir);
        StringWriter stdout = new() { NewLine = "\n" };
        StringWriter stderr = new() { NewLine = "\n" };
        CliContext ctx = new(stdout, stderr, useColor: false, utf8: false, progress: false);

        string toml = dir.File("rules.toml", "[pack]\ndefault_action = \"compress\"\n[[rule]]\naction = \"loose\"\ninclude = [\"eboot.bin\", \"*.prx\", \"*.sprx\", \"sce_sys/*\"]\n");
        int exit = MkPFSCli.Run(["ampr", "game", "--root", game, "--output", Path.Combine(dir.Path, "out"), "--fakelib", Path.Combine(dir.Path, "libs"), "--config", toml], ctx);

        Assert.True(exit == 0, stdout.ToString() + stderr.ToString());
        Assert.StartsWith(global::MkPFS.Cli.Commands.AmprCommand.ExperimentalWarning + $"\nRules: TOML profile {toml}\n", stdout.ToString(), StringComparison.Ordinal);
        foreach (string step in (string[])["[1/5] Libraries: fakelib/", "[2/5] Writing ampr_emu.index", "[3/5] Packing", "[4/5] Copying loose files", "[5/5] Verifying"])
        {
            Assert.Contains(step + "\n", stdout.ToString(), StringComparison.Ordinal);
        }

        exit = MkPFSCli.Run(["ampr", "game", "--root", game, "--output", Path.Combine(dir.Path, "out2"), "--config", toml], ctx);
        Assert.Equal(2, exit);
        Assert.EndsWith("error: fakelib/libSceAmpr.sprx cannot read asset packs; use AMPR Emu 0.4.2.1 or newer from https://github.com/drakmor/ampr_emu/releases\n", stderr.ToString(), StringComparison.Ordinal);
    }
}
