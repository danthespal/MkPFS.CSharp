using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using MkPFS.Build;
using MkPFS.Build.PFS;
using MkPFS.Core.Diagnostics;
using MkPFS.Core.Metadata;
using MkPFS.Gui.Localization;

namespace MkPFS.Gui.ViewModels;

/// <summary>One discovered batch item (Python <c>BatchItemRow</c>).</summary>
public sealed class BatchQueueRow
{
    internal BatchQueueRow(BatchItem item, GameMetadata metadata, IBrush accent)
    {
        Accent = accent;
        Localizer tr = Localizer.Instance;
        string Or(string value) => value.Length > 0 ? value : "-";
        string kind = item.Kind == BatchItemKind.Folder ? "FOLDER" : "FILE";
        Title = metadata.GameTitle.Length > 0 ? metadata.GameTitle : metadata.FileName.Length > 0 ? metadata.FileName : item.Name;
        Details = $"{tr["pkf_meta_title_id"]}: {Or(metadata.TitleId)}  |  {tr["pkf_meta_version"]}: {Or(metadata.Version)}"
            + $"  |  {(metadata.PackageType.Length > 0 ? metadata.PackageType : kind)}  |  {tr["pkf_meta_size"]}: {metadata.SizeDisplay}"
            + $"  |  {tr["pkf_meta_region"]}: {Or(metadata.Region)}";
        ContentId = $"{tr["pkf_meta_content_id"]}: {Or(metadata.ContentId)}";
        Kind = kind;
        AprEmu = metadata.HasAprEmu ? $"{tr["pkf_meta_apr_emu"]}: {tr["pkf_meta_apr_yes"]}" : null;
        Cover = MetadataPreviewViewModel.DecodeCover(metadata.IconBytes);
    }

    /// <summary>Game title, file name, or item name.</summary>
    public string Title { get; }

    /// <summary>Title ID, version, type, size, and region.</summary>
    public string Details { get; }

    /// <summary>Content ID line.</summary>
    public string ContentId { get; }

    /// <summary>FOLDER or FILE.</summary>
    public string Kind { get; }

    /// <summary>"APR-EMU: Yes" when the item carries the APR Emu marker.</summary>
    public string? AprEmu { get; }

    /// <summary>Cover image.</summary>
    public Bitmap? Cover { get; }

    /// <summary>A cover image is shown.</summary>
    public bool HasCover => Cover is not null;

    /// <summary>Panel accent for the details line.</summary>
    public IBrush Accent { get; }
}

/// <summary>Batch queue preview: every packable item of the source folder (Python <c>BatchQueuePreview</c>).</summary>
/// <param name="accent">Panel accent for the details lines.</param>
public sealed partial class BatchQueueViewModel(IBrush accent) : ObservableObject
{
    private int _token;

    /// <summary>Discovered items.</summary>
    public ObservableCollection<BatchQueueRow> Rows { get; } = [];

    /// <summary>Summary next to the section title.</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = Localizer.Instance["bt_preview_empty"];

    /// <summary>The list is empty and shows <see cref="Summary"/> instead.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; private set; } = true;

    /// <summary>Scan <paramref name="path"/> and read every item's metadata; a newer call supersedes it.</summary>
    /// <param name="path">Batch source folder.</param>
    /// <returns>Completes when the list is updated or superseded.</returns>
    public async Task LoadAsync(string? path)
    {
        int token = ++_token;
        Localizer tr = Localizer.Instance;
        Rows.Clear();
        IsEmpty = true;
        string trimmed = path?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            Summary = tr["bt_preview_empty"];
            return;
        }

        if (!Directory.Exists(trimmed))
        {
            Summary = tr["bt_preview_invalid"];
            return;
        }

        Summary = tr["bt_preview_scanning"];
        List<BatchQueueRow> rows;
        try
        {
            rows = await Task.Run(() => Batch.Discover(trimmed, NullLog.Instance)
                .Select(item => new BatchQueueRow(item, GameMetadataReader.Read(item.Source), accent))
                .ToList()).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is BuildException or IOException or UnauthorizedAccessException)
        {
            if (token == _token)
            {
                Summary = tr.Format("bt_preview_error", ex.Message);
            }

            return;
        }

        if (token != _token)
        {
            return;
        }

        if (rows.Count == 0)
        {
            Summary = tr["bt_preview_none"];
            return;
        }

        int folders = rows.Count(r => r.Kind == "FOLDER");
        Summary = tr.Format("bt_preview_count", rows.Count, folders, rows.Count - folders);
        foreach (BatchQueueRow row in rows)
        {
            Rows.Add(row);
        }

        IsEmpty = false;
    }
}
