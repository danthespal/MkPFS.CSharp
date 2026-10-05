using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MkPFS.Gui.Jobs;
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
            ["pack_exfat", "pack_file", "ampr", "verify", "repair", "inspect", "tree", "unpack"],
            NavButtons(window).Select(AutomationProperties.GetName));
        Assert.True(Nav(window, "pack_exfat").IsChecked);
        Assert.Equal("Pack exFAT", PageTitle(window));
        Assert.True(Shows(window, "Pack FFPFSC"));
        Assert.True(Shows(window, "BUILD"));
        Assert.Equal("MkPFS.CSharp", window.Title);
        Assert.True(Shows(window, "MkPFS.CSharp"));
        window.Close();
    }

    [AvaloniaFact]
    public void Packing_pages_link_to_the_apr_emu_downloads()
    {
        (MainWindow window, _) = Open();

        foreach (string page in new[] { "pack_exfat" })
        {
            Nav(window, page).IsChecked = true;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(
                ["https://github.com/drakmor/ampr_emu/releases", "https://github.com/drakmor/pgo_stub/releases"],
                window.GetVisualDescendants().OfType<HyperlinkButton>().Select(b => b.NavigateUri?.ToString()));
            Assert.True(Shows(window, "APR Emu"));
        }

        window.Close();
    }

    [AvaloniaFact]
    public void Run_sits_between_the_options_and_the_log_and_advanced_starts_collapsed()
    {
        (MainWindow window, _) = Open();

        foreach (string page in new[] { "pack_exfat", "pack_file" })
        {
            Nav(window, page).IsChecked = true;
            Dispatcher.UIThread.RunJobs();

            Control card = window.GetVisualDescendants().OfType<ContentControl>().Single(c => c.Name == "FormHost");
            Control run = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "RunButton");
            Control log = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "Log");
            double Top(Control c) => c.TranslatePoint(default, window)!.Value.Y;
            Assert.True(Top(card) < Top(run) && Top(run) < Top(log), page);
            Assert.False(window.GetVisualDescendants().OfType<Expander>().Single(e => e.Name == "AdvancedExpander").IsExpanded);
        }

        window.Close();
    }

    [AvaloniaFact]
    public void Closing_during_a_job_asks_then_keeps_running_or_stops_it()
    {
        (MainWindow window, MainWindowViewModel model) = Open();
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        JobRunner job = model.Items.First().Page.Job;
        Task<JobOutcome> running = job.RunAsync(context =>
        {
            while (true)
            {
                context.Progress.Step("work", 0, 1, 0); // throws once cancelled
                Thread.Sleep(5);
            }
        });

        ConfirmCloseDialog AskToClose()
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
            return window.OwnedWindows.OfType<ConfirmCloseDialog>().Single();
        }

        void Click(ConfirmCloseDialog dialog, string button)
        {
            dialog.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == button).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        }

        // Keep Running: the window and the job stay.
        Click(AskToClose(), "KeepButton");
        Assert.False(closed);
        Assert.True(job.IsRunning);

        // Stop and Close: the job is cancelled and has ended before the window closes.
        Click(AskToClose(), "StopButton");
        for (int i = 0; i < 500 && !closed; i++)
        {
            Thread.Sleep(10);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(closed);
        Assert.Equal(JobOutcome.Cancelled, running.Result);
        Assert.False(model.HasRunningJob);
    }

    [AvaloniaFact]
    public void Closing_without_a_job_does_not_ask()
    {
        (MainWindow window, _) = Open();
        bool closed = false;
        window.Closed += (_, _) => closed = true;

        window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.True(closed);
        Assert.Empty(window.OwnedWindows);
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
        Assert.False(Nav(window, "pack_exfat").IsChecked);
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

            Assert.Equal("Empacotar EXFAT", PageTitle(window));
            Assert.True(Shows(window, "CONSTRUIR"));
            Assert.True(Shows(window, "Empacotar FFPFSC"));
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
        PackExfatPanelViewModel exfat = (PackExfatPanelViewModel)model.Items.Single(i => i.Key == "pack_exfat").Page;
        exfat.Metadata.Debounce = TimeSpan.Zero;
        exfat.Source = game;
        await exfat.Metadata.LoadAsync(game);
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
