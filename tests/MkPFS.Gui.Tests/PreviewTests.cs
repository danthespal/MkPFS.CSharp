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

    [AvaloniaFact]
    public async Task Batch_queue_lists_folders_and_images_with_metadata()
    {
        using TempDir dir = new();
        string games = Path.Combine(dir.Path, "games");
        string game = BuildPanelTests.Game(dir, "games/PPSA01234-app");
        Assert.Equal(0, MkPFSCli.Run(["pack", "exfat", game, Path.Combine(games, "Data.exfat")], new CliContext(TextWriter.Null, TextWriter.Null, false, false, false)));
        dir.File("games/readme.txt");
        BatchQueueViewModel queue = new(Brushes.Teal);

        await queue.LoadAsync(games);

        Assert.Equal("2 item(s): 1 folder(s), 1 file(s)", queue.Summary);
        Assert.False(queue.IsEmpty);
        Assert.Equal(["Astro Test", "Astro Test"], queue.Rows.Select(r => r.Title)); // the exFAT holds the same game
        Assert.Equal(["FILE", "FOLDER"], queue.Rows.Select(r => r.Kind));
        BatchQueueRow folder = queue.Rows[1];
        Assert.StartsWith("Title ID: PPSA01234  |  Version: 01.004.000  |  ", folder.Details, StringComparison.Ordinal);
        Assert.Equal("Content ID: UP0001-PPSA01234_00-ASTROBOT00000000", folder.ContentId);
        Assert.True(folder.HasCover);
        Assert.Null(folder.AprEmu);
    }

    [AvaloniaFact]
    public async Task Batch_queue_explains_empty_and_invalid_sources()
    {
        using TempDir dir = new();
        BatchQueueViewModel queue = new(Brushes.Teal);

        await queue.LoadAsync(string.Empty);
        Assert.Equal("Select a source folder to preview the batch queue.", queue.Summary);
        await queue.LoadAsync(Path.Combine(dir.Path, "missing"));
        Assert.Equal("Select an existing folder to preview the batch queue.", queue.Summary);
        await queue.LoadAsync(dir.Path);
        Assert.Equal("No packable items found.", queue.Summary);
        Assert.True(queue.IsEmpty);
    }
}
