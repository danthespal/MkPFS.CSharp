using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using MkPFS.Gui.Jobs;

namespace MkPFS.Gui.Views;

/// <summary>Log line color per tone (Python log tags: success green, warning amber, error red).</summary>
public sealed class LogToneBrush : IValueConverter
{
    /// <summary>Shared instance.</summary>
    public static readonly LogToneBrush Instance = new();

    private static readonly IBrush Normal = new ImmutableSolidColorBrush(Color.Parse("#E8EDF5"));
    private static readonly IBrush Muted = new ImmutableSolidColorBrush(Color.Parse("#8892B0"));
    private static readonly IBrush Success = new ImmutableSolidColorBrush(Color.Parse("#39FF8A"));
    private static readonly IBrush Warning = new ImmutableSolidColorBrush(Color.Parse("#FFB800"));
    private static readonly IBrush Error = new ImmutableSolidColorBrush(Color.Parse("#FF3B5C"));

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        LogTone.Muted => Muted,
        LogTone.Success => Success,
        LogTone.Warning => Warning,
        LogTone.Error => Error,
        _ => Normal,
    };

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
