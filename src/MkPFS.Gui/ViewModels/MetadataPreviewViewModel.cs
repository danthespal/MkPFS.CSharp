using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using MkPFS.Core.Metadata;
using MkPFS.Gui.Localization;

namespace MkPFS.Gui.ViewModels;

/// <summary>Cover and game details for the selected source (Python <c>MetadataPreview</c>).</summary>
public sealed partial class MetadataPreviewViewModel : ObservableObject
{
    private const string Dash = "-";
    private CancellationTokenSource? _pending;
    private GameMetadata? _metadata;

    /// <summary>Create the preview.</summary>
    public MetadataPreviewViewModel()
    {
        Localizer.Instance.PropertyChanged += (_, _) => OnPropertyChanged(nameof(AprEmu));
    }

    /// <summary>Wait after the last path change before reading (Python: 250 ms debounce).</summary>
    public TimeSpan Debounce { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Game title, file name, or a dash.</summary>
    [ObservableProperty]
    public partial string Title { get; private set; } = Dash;

    /// <summary>Title ID.</summary>
    [ObservableProperty]
    public partial string TitleId { get; private set; } = Dash;

    /// <summary>Content ID.</summary>
    [ObservableProperty]
    public partial string ContentId { get; private set; } = Dash;

    /// <summary>Human-readable size.</summary>
    [ObservableProperty]
    public partial string Size { get; private set; } = Dash;

    /// <summary>Application version.</summary>
    [ObservableProperty]
    public partial string Version { get; private set; } = Dash;

    /// <summary>Region from the content ID.</summary>
    [ObservableProperty]
    public partial string Region { get; private set; } = Dash;

    /// <summary>Cover image, or <see langword="null"/> for the "Cover" placeholder.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCover))]
    public partial Bitmap? Cover { get; private set; }

    /// <summary>A cover image is shown.</summary>
    public bool HasCover => Cover is not null;

    /// <summary>APR Emu marker: Yes, No, or a dash before anything is read.</summary>
    public string AprEmu => _metadata is null ? Dash : Localizer.Instance[_metadata.HasAprEmu ? "pkf_meta_apr_yes" : "pkf_meta_apr_no"];

    /// <summary>
    /// Read metadata for <paramref name="path"/> after the debounce delay; a newer call supersedes it.
    /// </summary>
    /// <param name="path">File or folder, or empty to clear.</param>
    /// <returns>Completes when the preview is updated or superseded.</returns>
    public async Task LoadAsync(string? path)
    {
        _pending?.Cancel();
        _pending = null;
        string trimmed = path?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            Reset();
            return;
        }

        using CancellationTokenSource pending = new();
        _pending = pending;
        Title = Path.GetFileName(Path.TrimEndingDirectorySeparator(trimmed)) is { Length: > 0 } name ? name : Dash;
        try
        {
            if (Debounce > TimeSpan.Zero)
            {
                await Task.Delay(Debounce, pending.Token).ConfigureAwait(true);
            }

            if (!File.Exists(trimmed) && !Directory.Exists(trimmed))
            {
                Reset();
                return;
            }

            GameMetadata metadata = await Task.Run(() => GameMetadataReader.Read(trimmed), pending.Token).ConfigureAwait(true);
            if (!pending.IsCancellationRequested)
            {
                Apply(metadata);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer path.
        }
        finally
        {
            if (ReferenceEquals(_pending, pending))
            {
                _pending = null;
            }
        }
    }

    /// <summary>Decode cover bytes; <see langword="null"/> when missing or not an image.</summary>
    /// <param name="icon">PNG or JPEG bytes.</param>
    /// <returns>Bitmap or <see langword="null"/>.</returns>
    internal static Bitmap? DecodeCover(byte[]? icon)
    {
        if (icon is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            using MemoryStream stream = new(icon);
            return new Bitmap(stream);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    private void Reset()
    {
        _metadata = null;
        Title = TitleId = ContentId = Size = Version = Region = Dash;
        Cover = null;
        OnPropertyChanged(nameof(AprEmu));
    }

    // Python _apply_metadata.
    private void Apply(GameMetadata metadata)
    {
        _metadata = metadata;
        Title = OrDash(metadata.GameTitle.Length > 0 ? metadata.GameTitle : metadata.FileName);
        TitleId = OrDash(metadata.TitleId);
        ContentId = OrDash(metadata.ContentId);
        Size = metadata.SizeDisplay;
        Version = OrDash(metadata.Version);
        Region = OrDash(metadata.Region);
        Cover = DecodeCover(metadata.IconBytes);
        OnPropertyChanged(nameof(AprEmu));
    }

    private static string OrDash(string value) => value.Length > 0 ? value : Dash;
}
