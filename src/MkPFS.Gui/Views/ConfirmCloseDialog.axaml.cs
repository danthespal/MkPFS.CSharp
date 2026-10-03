using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MkPFS.Gui.Views;

/// <summary>Asks whether to stop the running job before the main window closes; the result is <see langword="true"/> to stop.</summary>
public sealed partial class ConfirmCloseDialog : Window
{
    /// <summary>Create the dialog.</summary>
    public ConfirmCloseDialog()
    {
        InitializeComponent();
    }

    private void OnKeep(object? sender, RoutedEventArgs e) => Close(false);

    private void OnStop(object? sender, RoutedEventArgs e) => Close(true);
}
