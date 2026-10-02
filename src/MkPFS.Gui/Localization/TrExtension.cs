using System.ComponentModel;
using Avalonia;
using Avalonia.Markup.Xaml;

namespace MkPFS.Gui.Localization;

/// <summary>
/// XAML <c>{l:Tr key}</c>: text from <see cref="Localizer"/> that follows language changes
/// (Python <c>refresh_labels</c>, without per-widget refresh code).
/// </summary>
/// <param name="key">String key.</param>
public sealed class TrExtension(string key) : MarkupExtension
{
    /// <summary>String key.</summary>
    public string Key { get; set; } = key;

    /// <inheritdoc />
    public override object ProvideValue(IServiceProvider serviceProvider) => Observe(Key).ToBinding();

    /// <summary>Text for <paramref name="key"/> now and after every language change.</summary>
    /// <param name="key">String key.</param>
    /// <returns>Observable text.</returns>
    public static IObservable<string> Observe(string key) => new LocalizedText(key);

    private sealed class LocalizedText(string key) : IObservable<string>
    {
        public IDisposable Subscribe(IObserver<string> observer)
        {
            Localizer localizer = Localizer.Instance;
            void OnChanged(object? sender, PropertyChangedEventArgs e) => observer.OnNext(localizer[key]);
            localizer.PropertyChanged += OnChanged;
            observer.OnNext(localizer[key]);
            return new Unsubscriber(() => localizer.PropertyChanged -= OnChanged);
        }
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
