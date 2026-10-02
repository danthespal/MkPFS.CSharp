using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MkPFS.Gui.Jobs;
using MkPFS.Gui.Localization;
using MkPFS.Gui.ViewModels;

namespace MkPFS.Gui.Views;

/// <summary>Shared page layout for every operation (Python <c>BasePanel</c>).</summary>
public sealed partial class PanelView : UserControl
{
    private PanelViewModel? _model;

    /// <summary>Create the view.</summary>
    public PanelView()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
        {
            _model.Job.Lines.CollectionChanged -= OnLinesChanged;
        }

        _model = DataContext as PanelViewModel;
        if (_model is not null)
        {
            _model.Job.Lines.CollectionChanged += OnLinesChanged;
        }
    }

    // Keep the newest line in view, like the Python log textbox.
    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && _model is { Job.Lines.Count: > 0 } model)
        {
            LogLine last = model.Job.Lines[^1];
            Dispatcher.UIThread.Post(() => Log.ScrollIntoView(last), DispatcherPriority.Background);
        }
    }

    private async void OnExportLog(object? sender, RoutedEventArgs e)
    {
        if (_model is not { Job.Lines.Count: > 0 } model || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        IStorageFile? file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Localizer.Instance["export_log"],
            DefaultExtension = ".txt",
            FileTypeChoices =
            [
                new FilePickerFileType("Text file") { Patterns = ["*.txt"] },
                new FilePickerFileType("JSON file") { Patterns = ["*.json"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        }).ConfigureAwait(true);
        if (file?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        try
        {
            model.ExportLog(path);
        }
        catch (IOException ex)
        {
            model.Job.Append(new LogLine($"Export failed: {ex.Message}", LogTone.Error));
        }
        catch (UnauthorizedAccessException ex)
        {
            model.Job.Append(new LogLine($"Export failed: {ex.Message}", LogTone.Error));
        }
    }
}
