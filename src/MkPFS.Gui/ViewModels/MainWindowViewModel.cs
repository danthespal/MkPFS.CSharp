using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MkPFS.Cli;
using MkPFS.Gui.Localization;

namespace MkPFS.Gui.ViewModels;

/// <summary>A sidebar entry (Python <c>NavButton</c>).</summary>
public sealed partial class NavItem : ObservableObject
{
    private readonly string _labelKey;

    /// <summary>Create an entry.</summary>
    /// <param name="key">Stable page key (tests and selection).</param>
    /// <param name="labelKey">Label string key.</param>
    /// <param name="icon">Icon path data.</param>
    /// <param name="page">Page shown when selected.</param>
    public NavItem(string key, string labelKey, string icon, PanelViewModel page)
    {
        Key = key;
        _labelKey = labelKey;
        Icon = StreamGeometry.Parse(icon);
        Page = page;
        Localizer.Instance.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Label));
    }

    /// <summary>Stable page key.</summary>
    public string Key { get; }

    /// <summary>Localized label.</summary>
    public string Label => Localizer.Instance[_labelKey];

    /// <summary>Icon geometry.</summary>
    public Geometry Icon { get; }

    /// <summary>Page shown when selected.</summary>
    public PanelViewModel Page { get; }

    /// <summary>Accent brush of the page.</summary>
    public IBrush Accent => Page.Accent;

    /// <summary>Selected in the sidebar.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>A titled group of sidebar entries.</summary>
/// <param name="TitleKey">Section title string key.</param>
/// <param name="Items">Entries.</param>
public sealed record NavSection(string TitleKey, IReadOnlyList<NavItem> Items)
{
    /// <summary>Observable localized title.</summary>
    public IObservable<string> Title => TrExtension.Observe(TitleKey);
}

/// <summary>Main window state: sidebar sections, the current page, and the language picker.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    // Python theme.py neon palette; Repair is new and takes the error red, AMPR Packs a new lime.
    private static readonly Color Orange = Color.Parse("#FF6B3D");
    private static readonly Color Cyan = Color.Parse("#00FFD4");
    private static readonly Color Green = Color.Parse("#39FF8A");
    private static readonly Color Red = Color.Parse("#FF3B5C");
    private static readonly Color Purple = Color.Parse("#B560FF");
    private static readonly Color Amber = Color.Parse("#FFB800");
    private static readonly Color Pink = Color.Parse("#FF5CAA");
    private static readonly Color Lime = Color.Parse("#C6FF3D");

    /// <summary>Create the main window state with every page.</summary>
    public MainWindowViewModel()
    {
        Sections =
        [
            new("section_build",
            [
                Item("pack_exfat", "nav_pack_exfat", Icons.PackExfat, new PackExfatPanelViewModel(Orange)),
                Item("pack_file", "nav_pack_file", Icons.PackFile, new PackFilePanelViewModel(Cyan)),
                Item("ampr", "nav_ampr", Icons.AmprPack, new AmprPackPanelViewModel(Lime)),
            ]),
            new("section_check",
            [
                Item("verify", "nav_verify", Icons.Verify, new VerifyPanelViewModel(Green)),
                Item("repair", "nav_repair", Icons.Repair, new RepairPanelViewModel(Red)),
            ]),
            new("section_read",
            [
                Item("inspect", "nav_inspect", Icons.Inspect, new InspectPanelViewModel(Purple)),
                Item("tree", "nav_tree", Icons.Tree, new TreePanelViewModel(Amber)),
                Item("unpack", "nav_unpack", Icons.Unpack, new UnpackPanelViewModel(Pink)),
            ]),
        ];
        Localizer.Instance.PropertyChanged += (_, _) => OnPropertyChanged(nameof(VersionFooter));
        Select(Sections[0].Items[0]);
    }

    /// <summary>Sidebar sections.</summary>
    public IReadOnlyList<NavSection> Sections { get; }

    /// <summary>Every entry in sidebar order.</summary>
    public IEnumerable<NavItem> Items => Sections.SelectMany(s => s.Items);

    /// <summary>Page shown in the content area.</summary>
    [ObservableProperty]
    public partial PanelViewModel Current { get; private set; } = null!;

    /// <summary>Languages for the picker.</summary>
    public IReadOnlyList<Language> Languages => Localizer.Languages;

    /// <summary>Active language.</summary>
    public Language Language
    {
        get => Localizer.Instance.Language;
        set
        {
            Localizer.Instance.Language = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Version line at the bottom of the sidebar.</summary>
    public string VersionFooter => Localizer.Instance.Format("version_footer", MkPFSCli.Version);

    /// <summary>Show <paramref name="item"/>'s page (Python <c>_select</c>).</summary>
    /// <param name="item">Entry to select.</param>
    public void Select(NavItem item)
    {
        foreach (NavItem other in Items)
        {
            other.IsSelected = ReferenceEquals(other, item);
        }

        Current = item.Page;
    }

    /// <summary>Some page is running a job (closing the window asks first).</summary>
    public bool HasRunningJob => Items.Any(i => i.Page.Job.IsRunning);

    /// <summary>Cancel every running job and wait until each one has ended and cleaned up.</summary>
    /// <returns>Completes when no job is running.</returns>
    public Task StopJobsAsync()
    {
        foreach (NavItem item in Items)
        {
            item.Page.Job.Cancel();
        }

        return Task.WhenAll(Items.Select(i => i.Page.Job.WhenIdle()));
    }

    /// <summary>Select the entry with <paramref name="key"/>.</summary>
    /// <param name="key">Page key.</param>
    public void Select(string key) => Select(Items.First(i => i.Key == key));

    private NavItem Item(string key, string labelKey, string icon, PanelViewModel page)
    {
        NavItem item = new(key, labelKey, icon, page);
        item.PropertyChanged += (_, e) =>
        {
            // Radio buttons set IsSelected from the view; mirror it into Current.
            if (e.PropertyName == nameof(NavItem.IsSelected) && item.IsSelected && !ReferenceEquals(Current, item.Page))
            {
                Select(item);
            }
        };
        return item;
    }
}
