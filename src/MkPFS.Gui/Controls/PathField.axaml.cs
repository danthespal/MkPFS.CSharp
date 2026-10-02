using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace MkPFS.Gui.Controls;

/// <summary>What the Browse button picks (Python <c>PathRow</c> modes).</summary>
public enum PathMode
{
    /// <summary>Existing folder.</summary>
    Folder,

    /// <summary>Existing file.</summary>
    Open,

    /// <summary>New or existing file to write.</summary>
    Save,

    /// <summary>Existing file or folder, with one button each.</summary>
    Any,
}

/// <summary>Labelled path entry with a Browse button.</summary>
public sealed partial class PathField : UserControl
{
    /// <summary>Field label.</summary>
    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<PathField, string?>(nameof(Label));

    /// <summary>Path text.</summary>
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<PathField, string?>(nameof(Text), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Placeholder shown while empty.</summary>
    public static readonly StyledProperty<string?> PlaceholderProperty = AvaloniaProperty.Register<PathField, string?>(nameof(Placeholder));

    /// <summary>Browse mode.</summary>
    public static readonly StyledProperty<PathMode> ModeProperty = AvaloniaProperty.Register<PathField, PathMode>(nameof(Mode));

    /// <summary>
    /// File type filters for Open and Save: <c>Name:*.a *.b|Other:*.*</c> (Python <c>filetypes</c>).
    /// </summary>
    public static readonly StyledProperty<string?> FiltersProperty = AvaloniaProperty.Register<PathField, string?>(nameof(Filters));

    /// <summary>Create the field.</summary>
    public PathField()
    {
        InitializeComponent();
    }

    /// <summary>Field label.</summary>
    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>Path text.</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Placeholder shown while empty.</summary>
    public string? Placeholder
    {
        get => GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    /// <summary>Browse mode.</summary>
    public PathMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    /// <summary>File type filters for Open and Save.</summary>
    public string? Filters
    {
        get => GetValue(FiltersProperty);
        set => SetValue(FiltersProperty, value);
    }

    /// <summary>Parse <see cref="Filters"/> into picker file types.</summary>
    /// <param name="filters">Filter text.</param>
    /// <returns>File types, empty when none.</returns>
    internal static List<FilePickerFileType> ParseFilters(string? filters) =>
    [
        .. (filters ?? string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split(':', 2))
            .Where(pair => pair.Length == 2)
            .Select(pair => new FilePickerFileType(pair[0]) { Patterns = pair[1].Split(' ', StringSplitOptions.RemoveEmptyEntries) }),
    ];

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ModeProperty)
        {
            bool any = Mode == PathMode.Any;
            BrowseButton.IsVisible = !any;
            FileButton.IsVisible = any;
            FolderButton.IsVisible = any;
        }
    }

    private void OnBrowse(object? sender, RoutedEventArgs e) => Browse(Mode == PathMode.Any ? PathMode.Open : Mode);

    private void OnBrowseFolder(object? sender, RoutedEventArgs e) => Browse(PathMode.Folder);

    private async void Browse(PathMode mode)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        // Start in the folder of the current path, when it exists.
        string? current = Text?.Trim();
        string? startDir = string.IsNullOrEmpty(current) ? null : Directory.Exists(current) ? current : Path.GetDirectoryName(current);
        IStorageFolder? start = startDir is not null && Directory.Exists(startDir)
            ? await storage.TryGetFolderFromPathAsync(startDir).ConfigureAwait(true)
            : null;
        List<FilePickerFileType> types = ParseFilters(Filters);
        IStorageItem? picked = mode switch
        {
            PathMode.Folder => (await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Label, SuggestedStartLocation = start }).ConfigureAwait(true))
                .FirstOrDefault(),
            PathMode.Open => (await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = Label, SuggestedStartLocation = start, FileTypeFilter = types })
                .ConfigureAwait(true)).FirstOrDefault(),
            _ => await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Label,
                SuggestedStartLocation = start,
                SuggestedFileName = string.IsNullOrEmpty(current) ? null : Path.GetFileName(current),
                FileTypeChoices = types,
            }).ConfigureAwait(true),
        };
        if (picked?.TryGetLocalPath() is { } path)
        {
            Text = path;
        }
    }
}
