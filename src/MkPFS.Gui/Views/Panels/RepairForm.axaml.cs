using Avalonia;
using Avalonia.Controls;
using MkPFS.Gui.Controls;
using MkPFS.Gui.ViewModels;

namespace MkPFS.Gui.Views.Panels;

/// <summary>Repair options, Scan, and the block map.</summary>
public sealed partial class RepairForm : UserControl
{
    /// <summary>Create the form.</summary>
    public RepairForm()
    {
        InitializeComponent();
        Map.CellSelected += (_, range) =>
        {
            if (DataContext is RepairPanelViewModel model)
            {
                model.Selection = range;
            }
        };
        Map.PropertyChanged += OnMapPropertyChanged;
    }

    private void OnMapPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == BlockMap.BlocksPerCellProperty && DataContext is RepairPanelViewModel model)
        {
            model.BlocksPerCell = Map.BlocksPerCell;
        }
    }
}
