using Avalonia.Headless.XUnit;
using Avalonia.Media;
using MkPFS.Cli;
using MkPFS.Cli.Output;
using MkPFS.Gui.ViewModels;

namespace MkPFS.Gui.Tests;

[Collection(GuiCollection.Name)]
public sealed class PreviewTests
{
    [AvaloniaFact]
    public async Task Metadata_preview_reads_a_source_folder()
    {
        using TempDir dir = new();
        string game = BuildPanelTests.Game(dir);
        MetadataPreviewViewModel preview = new() { Debounce = TimeSpan.Zero };

        await preview.LoadAsync(game);

        Assert.Equal("Astro Test", preview.Title);
        Assert.Equal("PPSA01234", preview.TitleId);
        Assert.Equal("UP0001-PPSA01234_00-ASTROBOT00000000", preview.ContentId);
        Assert.Equal("USA", preview.Region);
        Assert.Equal("No", preview.AprEmu);
        Assert.True(preview.HasCover);

        await preview.LoadAsync("  ");
        Assert.Equal(["-", "-", "-", "-"], new[] { preview.Title, preview.TitleId, preview.AprEmu, preview.Size });
        Assert.False(preview.HasCover);
    }

    [AvaloniaFact]
    public async Task A_newer_path_supersedes_a_pending_one()
    {
        using TempDir dir = new();
        string game = BuildPanelTests.Game(dir);
        MetadataPreviewViewModel preview = new() { Debounce = TimeSpan.FromMilliseconds(200) };

        Task first = preview.LoadAsync(game);
        Task second = preview.LoadAsync(Path.Combine(dir.Path, "missing"));
        await Task.WhenAll(first, second);

        Assert.Equal("-", preview.TitleId);
        Assert.Equal("-", preview.Title);
    }
}
