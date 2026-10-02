using Avalonia;
using Avalonia.Headless;
using MkPFS.Gui;
using MkPFS.Gui.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace MkPFS.Gui.Tests;

/// <summary>Headless Avalonia with Skia rendering, so tests can capture frames.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>The localizer and the Avalonia dispatcher are process-wide: every class joins this collection to run alone.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GuiCollection
{
    public const string Name = "Gui";
}

/// <summary>Temporary directory deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mkpfs-gui-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Create a file (and parent folders) relative to the temp root.</summary>
    public string File(string relative, string content = "x")
    {
        string full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; a locked file must not fail the test run.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
