using MkPFS.Build;
using MkPFS.Build.PFS;
using MkPFS.Cli;
using MkPFS.Cli.Output;
using MkPFS.Core.Diagnostics;
using MkPFS.Core.Exfat;
using MkPFS.Core.PFS;

namespace MkPFS.Tests.Build;

public sealed class AmprLibsTests
{
    private sealed class ListLog : IMkPFSLog
    {
        public List<string> Lines { get; } = [];

        public void Log(LogLevel level, string message, LogIcon icon = LogIcon.None) => Lines.Add($"{level}: {message}");
    }

    // An APR game (PlayGo chunk file, no fakelib) and a libs folder with both libraries.
    private static (string Source, string Libs) AprTree(TempDir dir, bool playGo = true)
    {
        string source = dir.Dir("game");
        dir.File("game/eboot.bin", "eboot");
        dir.File("game/sce_sys/param.json", "{}");
        if (playGo)
        {
            dir.File("game/sce_sys/playgo-chunk.dat", "chunks");
        }

        string libs = dir.Dir("libs");
        dir.File("libs/libSceAmpr.sprx", "ampr");
        dir.File("libs/libScePlayGo.sprx", "playgo");
        return (source, libs);
    }

    private static (int Exit, string Output) Run(params string[] args)
    {
        StringWriter stdout = new() { NewLine = "\n" };
        StringWriter stderr = new() { NewLine = "\n" };
        int exit = MkPFSCli.Run(args, new CliContext(stdout, stderr, useColor: false, utf8: false, progress: false));
        return (exit, stdout.ToString() + stderr.ToString());
    }

    [Fact]
    public void Prepare_copies_the_libraries_into_fakelib_of_an_apr_title_and_indexes_them()
    {
        using TempDir dir = new();
        (string source, string libs) = AprTree(dir);
        ListLog log = new();

        AmprLibs.Prepare(source, new AmprOptions { LibsDir = libs }, log);

        Assert.Equal("ampr", File.ReadAllText(Path.Combine(source, "fakelib", "libSceAmpr.sprx")));
        Assert.Equal("playgo", File.ReadAllText(Path.Combine(source, "fakelib", "libScePlayGo.sprx")));
        Assert.Equal(
            ["Info: Copied libSceAmpr.sprx into fakelib/", "Info: Copied libScePlayGo.sprx into fakelib/",
             "Info: Detected fakelib/libSceAmpr.sprx; generating ampr_emu.index...", "Info: Generated ampr_emu.index with 5 entries"],
            log.Lines);
        Assert.Contains("/app0/fakelib/libScePlayGo.sprx", AmprIndex.ReadRows(File.ReadAllBytes(Path.Combine(source, AmprIndex.IndexName))).Select(r => r.Path));
    }

    [Fact]
    public void Prepare_keeps_identical_libraries_and_replaces_changed_ones()
    {
        using TempDir dir = new();
        (string source, string libs) = AprTree(dir);
        AmprLibs.Prepare(source, new AmprOptions { LibsDir = libs, Index = false }, new ListLog());
        File.WriteAllText(Path.Combine(libs, "libSceAmpr.sprx"), "ampr v2");
        ListLog log = new();

        AmprLibs.Prepare(source, new AmprOptions { LibsDir = libs, Index = false }, log);

        Assert.Equal(["Info: Copied libSceAmpr.sprx into fakelib/", "Info: fakelib/libScePlayGo.sprx is up to date"], log.Lines);
        Assert.Equal("ampr v2", File.ReadAllText(Path.Combine(source, "fakelib", "libSceAmpr.sprx")));
    }

    [Fact]
    public void Prepare_skips_titles_without_playgo_unless_forced()
    {
        using TempDir dir = new();
        (string source, string libs) = AprTree(dir, playGo: false);
        ListLog skipped = new();

        AmprLibs.Prepare(source, new AmprOptions { LibsDir = libs }, skipped);

        Assert.Equal(["Info: No sce_sys/playgo-chunk.dat; not an APR title, AMPR Emu libraries not added (use --ampr-title to force)"], skipped.Lines);
        Assert.False(Directory.Exists(Path.Combine(source, "fakelib")));

        AmprLibs.Prepare(source, new AmprOptions { LibsDir = libs, ForceAprTitle = true }, new ListLog());
        Assert.True(File.Exists(Path.Combine(source, AmprIndex.IndexName)));
    }

    [Fact]
    public void Prepare_warns_about_an_apr_title_without_libraries()
    {
        using TempDir dir = new();
        (string source, _) = AprTree(dir);
        ListLog log = new();

        AmprLibs.Prepare(source, new AmprOptions(), log);

        Assert.Equal(["Warning: APR title (sce_sys/playgo-chunk.dat) without fakelib/libSceAmpr.sprx; pass --ampr-libs <dir> to add AMPR Emu"], log.Lines);
        Assert.False(File.Exists(Path.Combine(source, AmprIndex.IndexName)));
    }

    [Fact]
    public void Inject_requires_libSceAmpr_in_the_libs_folder()
    {
        using TempDir dir = new();
        (string source, string libs) = AprTree(dir);
        File.Delete(Path.Combine(libs, "libSceAmpr.sprx"));

        BuildException missingLib = Assert.Throws<BuildException>(() => AmprLibs.Inject(source, libs, new ListLog()));
        BuildException missingDir = Assert.Throws<BuildException>(() => AmprLibs.Inject(source, Path.Combine(dir.Path, "nope"), new ListLog()));

        Assert.StartsWith("--ampr-libs folder has no libSceAmpr.sprx", missingLib.Message, StringComparison.Ordinal);
        Assert.StartsWith("--ampr-libs must be an existing directory", missingDir.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_exfat_adds_the_libraries_and_index_to_the_image()
    {
        using TempDir dir = new();
        (string source, string libs) = AprTree(dir);
        string image = Path.Combine(dir.Path, "out.exfat");

        (int exit, string output) = Run("pack", "exfat", source, image, "--ampr-libs", libs, "--no-progress");

        Assert.True(exit == 0, output);
        Assert.Contains("Generated ampr_emu.index with 5 entries", output, StringComparison.Ordinal);
        using FileStream stream = File.OpenRead(image);
        List<string> files = [.. new ExfatReader(stream).EnumerateFiles().Select(e => e.RelPath)];
        Assert.Contains(AmprIndex.IndexName, files);
        Assert.Contains("fakelib/libSceAmpr.sprx", files);
    }

    [Fact]
    public void Pack_exfat_indexes_an_existing_fakelib_unless_disabled()
    {
        using TempDir dir = new();
        (string source, _) = AprTree(dir, playGo: false);
        dir.File("game/fakelib/libSceAmpr.sprx", "ampr");

        (int disabled, _) = Run("pack", "exfat", source, Path.Combine(dir.Path, "a.exfat"), "--no-ampr-index", "--no-progress");
        Assert.Equal(0, disabled);
        Assert.False(File.Exists(Path.Combine(source, AmprIndex.IndexName)));

        (int exit, string output) = Run("pack", "exfat", source, Path.Combine(dir.Path, "b.exfat"), "--no-progress");
        Assert.True(exit == 0, output);
        Assert.True(File.Exists(Path.Combine(source, AmprIndex.IndexName)));
    }

    [Fact]
    public void Pack_exfat_fails_on_a_bad_libs_folder()
    {
        using TempDir dir = new();
        (string source, _) = AprTree(dir);
        string image = Path.Combine(dir.Path, "out.exfat");

        (int exit, string output) = Run("pack", "exfat", source, image, "--ampr-libs", Path.Combine(dir.Path, "nope"), "--no-progress");

        Assert.Equal(1, exit);
        Assert.Contains("--ampr-libs must be an existing directory", output, StringComparison.Ordinal);
        Assert.False(File.Exists(image));
    }

    [Fact]
    public void Batch_prepares_ampr_emu_for_folder_items()
    {
        using TempDir dir = new();
        string game = dir.Dir("batch/game");
        dir.File("batch/game/eboot.bin", "eboot");
        dir.File("batch/game/sce_sys/playgo-chunk.dat", "chunks");
        string libs = dir.Dir("libs");
        dir.File("libs/libSceAmpr.sprx", "ampr");
        string output = dir.Dir("out");

        (int exit, string log) = Run("batch", Path.Combine(dir.Path, "batch"), output, "--ampr-libs", libs, "--cpu-count", "1");

        Assert.True(exit == 0, log);
        Assert.Contains("Generated ampr_emu.index with 3 entries", log, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(game, "fakelib", "libSceAmpr.sprx")));
        Assert.True(File.Exists(Path.Combine(output, "game.ffpfsc")));
    }
}
