using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace MkPFS.Gui.Localization;

/// <summary>A selectable GUI language.</summary>
/// <param name="Code">Table code (Python <c>_LANG_NAMES</c> key).</param>
/// <param name="DisplayName">Name shown in the language picker.</param>
public sealed record Language(string Code, string DisplayName)
{
    /// <inheritdoc />
    public override string ToString() => DisplayName;
}

/// <summary>
/// GUI string lookup (Python <c>gui/i18n.py</c> <c>tr</c>/<c>set_locale</c>). Each language is a
/// <c>.resx</c> table in the main assembly; a missing key falls back to English, then to the key.
/// </summary>
public sealed class Localizer : INotifyPropertyChanged
{
    /// <summary>Languages in picker order (Python <c>_LANG_NAMES</c>).</summary>
    public static readonly IReadOnlyList<Language> Languages =
    [
        new("en", "English"),
        new("pt_BR", "Português (BR)"),
        new("es", "Español"),
        new("ro", "Română"),
        new("de", "Deutsch"),
        new("fr", "Français"),
    ];

    private static readonly ResourceManager English = Table("Strings");
    private readonly Dictionary<string, ResourceManager> _tables = new(StringComparer.Ordinal)
    {
        ["en"] = English,
        ["pt_BR"] = Table("Strings_pt_BR"),
        ["es"] = Table("Strings_es"),
        ["ro"] = Table("Strings_ro"),
        ["de"] = Table("Strings_de"),
        ["fr"] = Table("Strings_fr"),
    };

    private ResourceManager _current = English;
    private Language _language = Languages[0];

    /// <summary>Shared instance used by XAML (<see cref="TrExtension"/>) and view models.</summary>
    public static Localizer Instance { get; } = new();

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Active language.</summary>
    public Language Language
    {
        get => _language;
        set
        {
            if (value == _language)
            {
                return;
            }

            _language = value;
            _current = _tables.GetValueOrDefault(value.Code, English);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        }
    }

    /// <summary>Translated text for <paramref name="key"/> in the active language.</summary>
    /// <param name="key">String key.</param>
    /// <returns>Text, the English text, or the key itself.</returns>
    public string this[string key] =>
        _current.GetString(key, CultureInfo.InvariantCulture) ?? English.GetString(key, CultureInfo.InvariantCulture) ?? key;

    /// <summary>Translated composite format filled with <paramref name="args"/> (Python <c>tr(key).format(...)</c>).</summary>
    /// <param name="key">String key.</param>
    /// <param name="args">Format arguments.</param>
    /// <returns>Formatted text.</returns>
    public string Format(string key, params object?[] args) => string.Format(CultureInfo.InvariantCulture, this[key], args);

    /// <summary>Keys defined in a language table (tests check that every table has the English keys).</summary>
    /// <param name="code">Language code.</param>
    /// <returns>Key to text.</returns>
    internal IReadOnlyDictionary<string, string> Entries(string code)
    {
        Dictionary<string, string> entries = new(StringComparer.Ordinal);
        // Not disposed: the ResourceManager caches and keeps using this set.
        ResourceSet set = _tables[code].GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false)!;
        foreach (System.Collections.DictionaryEntry entry in set)
        {
            entries[(string)entry.Key] = (string)entry.Value!;
        }

        return entries;
    }

    private static ResourceManager Table(string name) => new($"MkPFS.Gui.Localization.{name}", typeof(Localizer).Assembly);
}
