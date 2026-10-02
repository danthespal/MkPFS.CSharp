using MkPFS.Core.IO;

namespace MkPFS.Tests.IO;

/// <summary>Port of Python <c>tests/mkpfs/test_gather.py</c>.</summary>
public sealed class SourceScannerTests
{
    private static List<string> Relative(TempDir temp) =>
        [.. SourceScanner.GatherFiles(temp.Path)
            .Select(p => Path.GetRelativePath(temp.Path, p).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)];

    [Fact]
    public void Small_tree_returns_every_file()
    {
        using TempDir temp = new();
        temp.File("a.txt");
        temp.File("sub/b.bin");
        temp.File("sub/deeper/c.dat");
        temp.Dir("empty");

        Assert.Equal(["a.txt", "sub/b.bin", "sub/deeper/c.dat"], Relative(temp));
    }

    [Fact]
    public void Empty_directory_returns_empty()
    {
        using TempDir temp = new();
        Assert.Empty(SourceScanner.GatherFiles(temp.Path));
    }

    [Fact]
    public void Ignored_names_are_excluded_including_nested_content()
    {
        using TempDir temp = new();
        temp.File(".DS_Store");
        temp.File("Thumbs.db");
        temp.File("._resource");
        temp.File("__MACOSX/inner.bin");
        temp.File("$RECYCLE.BIN/deleted.bin");
        temp.File("keep/real.bin");
        temp.File("keep/desktop.ini");

        Assert.Equal(["keep/real.bin"], Relative(temp));
    }

    [Fact]
    public void Hidden_files_are_included()
    {
        using TempDir temp = new();
        string hidden = temp.File("hidden.bin");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);

        Assert.Equal(["hidden.bin"], Relative(temp));
    }

    [Fact]
    public void Non_directory_root_returns_empty()
    {
        using TempDir temp = new();
        string file = temp.File("file.txt");
        Assert.Empty(SourceScanner.GatherFiles(file));
    }

    [Fact]
    public void Nonexistent_root_returns_empty()
    {
        using TempDir temp = new();
        Assert.Empty(SourceScanner.GatherFiles(Path.Combine(temp.Path, "missing")));
    }

    [Fact]
    public void Directory_symlink_is_not_followed()
    {
        using TempDir temp = new();
        string target = temp.Dir("real");
        temp.File("real/inside.bin");
        CreateLinkOrSkip(() => Directory.CreateSymbolicLink(Path.Combine(temp.Path, "link"), target));

        Assert.Equal(["real/inside.bin"], Relative(temp));
    }

    [Fact]
    public void File_symlink_is_included_and_broken_symlink_is_excluded()
    {
        using TempDir temp = new();
        string target = temp.File("target.bin");
        CreateLinkOrSkip(() => File.CreateSymbolicLink(Path.Combine(temp.Path, "good.lnk"), target));
        CreateLinkOrSkip(() => File.CreateSymbolicLink(Path.Combine(temp.Path, "broken.lnk"), Path.Combine(temp.Path, "nope.bin")));

        Assert.Equal(["good.lnk", "target.bin"], Relative(temp));
    }

    [Fact]
    public void Unreadable_subdirectory_is_skipped()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("POSIX permissions test");
            return;
        }

        using TempDir temp = new();
        temp.File("ok.bin");
        string locked = temp.Dir("locked");
        temp.File("locked/secret.bin");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            Assert.Equal(["ok.bin"], Relative(temp));
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // Windows needs Developer Mode or admin rights to create symlinks.
    private static void CreateLinkOrSkip(Action create)
    {
        try
        {
            create();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"cannot create symlinks here: {ex.Message}");
        }
    }
}
