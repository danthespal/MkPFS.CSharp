using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MkPFS.Core.Util;
using MkPFS.Gui.Localization;

namespace MkPFS.Gui.ViewModels;

/// <summary>
/// CPU core picker: Auto, then 1 to <see cref="CpuCores.Max"/> (every logical processor but two, which stay free for
/// Windows and other programs).
/// </summary>
public sealed partial class CpuPicker : ObservableObject
{
    /// <summary>Create the picker with Auto selected.</summary>
    public CpuPicker()
    {
        Choices = [AutoChoice(), .. Enumerable.Range(1, CpuCores.Max).Select(n => new Choice(n.ToString(CultureInfo.InvariantCulture), null, n.ToString(CultureInfo.InvariantCulture)))];
        Selected = Choices[0];
        Localizer.Instance.PropertyChanged += (_, _) =>
        {
            // The Auto label carries the core count, so it is rebuilt for the new language.
            bool auto = Selected.Value == "0";
            Choices[0] = AutoChoice();
            if (auto)
            {
                Selected = Choices[0];
            }
        };
    }

    /// <summary>Auto first, then every count.</summary>
    public ObservableCollection<Choice> Choices { get; }

    /// <summary>Selected entry.</summary>
    [ObservableProperty]
    public partial Choice Selected { get; set; }

    /// <summary>The count to pass: <see cref="CpuCores.Max"/> for Auto.</summary>
    public int Count => int.Parse(Selected.Value, CultureInfo.InvariantCulture) is > 0 and int count ? count : CpuCores.Max;

    /// <summary>Select a count; 0 is Auto, and larger counts select the most.</summary>
    /// <param name="count">Count.</param>
    public void Select(int count) => Selected = Choices[Math.Clamp(count, 0, CpuCores.Max)];

    private static Choice AutoChoice() => new("0", null, Localizer.Instance.Format("ct_cpu_auto", CpuCores.Max));
}
