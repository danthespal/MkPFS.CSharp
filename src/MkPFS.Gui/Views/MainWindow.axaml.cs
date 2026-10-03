using Avalonia.Controls;
using MkPFS.Gui.ViewModels;

namespace MkPFS.Gui.Views;

/// <summary>Main window: sidebar plus the selected operation page.</summary>
public sealed partial class MainWindow : Window
{
    private bool _closeConfirmed;
    private bool _confirming;

    /// <summary>Create the window.</summary>
    public MainWindow()
    {
        InitializeComponent();
    }

    // A running job would be killed mid-write; ask first, then cancel it and wait for its cleanup before closing.
    /// <inheritdoc />
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || DataContext is not MainWindowViewModel { HasRunningJob: true } model)
        {
            return;
        }

        e.Cancel = true;
        if (_confirming)
        {
            return;
        }

        _confirming = true;
        try
        {
            if (!await new ConfirmCloseDialog().ShowDialog<bool>(this).ConfigureAwait(true))
            {
                return;
            }

            await model.StopJobsAsync().ConfigureAwait(true);
            _closeConfirmed = true;
            Close();
        }
        finally
        {
            _confirming = false;
        }
    }
}
