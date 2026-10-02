using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MkPFS.Gui.Localization;
using MkPFS.Gui.ViewModels;
using MkPFS.Gui.Views;

namespace MkPFS.Gui.Tests;

[Collection(GuiCollection.Name)]
public sealed class MainWindowTests
{
    private static (MainWindow Window, MainWindowViewModel Model) Open()
    {
        MainWindowViewModel model = new();
        MainWindow window = new() { DataContext = model };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, model);
    }

    private static IEnumerable<RadioButton> NavButtons(Window window) => window.GetVisualDescendants().OfType<RadioButton>();

    private static RadioButton Nav(Window window, string key) => NavButtons(window).Single(b => AutomationProperties.GetName(b) == key);

    private static string PageTitle(Window window) => window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "TitleText").Text!;

    private static bool Shows(Window window, string text) => window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text);

    [AvaloniaFact]
    public void Sidebar_lists_every_operation_grouped_by_section()
    {
        (MainWindow window, MainWindowViewModel model) = Open();

        Assert.Equal(["section_build", "section_check", "section_read"], model.Sections.Select(s => s.TitleKey));
        Assert.Equal(
            ["batch", "pack_folder", "pack_exfat", "pack_file", "verify", "repair", "inspect", "tree", "unpack"],
            NavButtons(window).Select(AutomationProperties.GetName));
        Assert.True(Nav(window, "batch").IsChecked);
        Assert.Equal("Batch Convert", PageTitle(window));
        Assert.True(Shows(window, "BUILD"));
        Assert.Equal("MkPFS.C#", window.Title);
        Assert.True(Shows(window, "MkPFS.C#"));
        window.Close();
    }

    [AvaloniaFact]
    public void Clicking_an_entry_shows_its_page()
    {
        (MainWindow window, MainWindowViewModel model) = Open();

        Nav(window, "repair").IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Repair", PageTitle(window));
        Assert.Same(model.Items.Single(i => i.Key == "repair").Page, model.Current);
        Assert.Equal(["repair"], model.Items.Where(i => i.IsSelected).Select(i => i.Key));
        Assert.False(Nav(window, "batch").IsChecked);
        window.Close();
    }

    [AvaloniaFact]
    public void Language_picker_relabels_the_window()
    {
        (MainWindow window, _) = Open();
        ComboBox picker = window.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Name == "LanguagePicker");
        try
        {
            picker.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("Converter em Lote", PageTitle(window));
            Assert.True(Shows(window, "CONSTRUIR"));
            Assert.True(Shows(window, "Empacotar Pasta"));
            Assert.True(Shows(window, "Executar"));
        }
        finally
        {
            Localizer.Instance.Language = Localizer.Languages[0];
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Every_page_renders()
    {
        using TempDir dir = new();
        string game = BuildPanelTests.Game(dir, "games/PPSA01234-app");
        (MainWindow window, MainWindowViewModel model) = Open();
        PackFolderPanelViewModel folder = (PackFolderPanelViewModel)model.Items.Single(i => i.Key == "pack_folder").Page;
        folder.Metadata.Debounce = TimeSpan.Zero;
        folder.Source = game;
        await folder.Metadata.LoadAsync(game);
        BatchPanelViewModel batch = (BatchPanelViewModel)model.Items.Single(i => i.Key == "batch").Page;
        batch.Source = Path.GetDirectoryName(game)!;
        await batch.Queue.LoadAsync(batch.Source);
        window.Height = 1500; // whole pages in the snapshots
        RepairPanelViewModel repair = (RepairPanelViewModel)model.Items.Single(i => i.Key == "repair").Page;
        repair.Image = RepairPanelTests.RiskyImage(dir);
        await repair.ScanCommand.ExecuteAsync(null);
        repair.Selection = new Controls.BlockRange(1, 1);
        string? snapshots = Environment.GetEnvironmentVariable("MKPFS_GUI_SNAPSHOT_DIR");

        foreach (NavItem item in model.Items)
        {
            model.Select(item);
            Dispatcher.UIThread.RunJobs();
            using WriteableBitmap? frame = window.CaptureRenderedFrame();

            Assert.NotNull(frame);
            Assert.Equal(item.Page.Title, PageTitle(window));
            if (snapshots is { Length: > 0 })
            {
                frame.Save(Path.Combine(snapshots, item.Key + ".png"), PngBitmapEncoderOptions.Default);
            }
        }

        window.Close();
    }
}
