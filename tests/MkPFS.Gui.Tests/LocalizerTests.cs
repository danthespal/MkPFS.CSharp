using System.ComponentModel;
using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using MkPFS.Gui.Localization;

namespace MkPFS.Gui.Tests;

[Collection(GuiCollection.Name)]
public sealed partial class LocalizerTests
{
    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex Placeholder();

    public static TheoryData<string> Codes() => [.. Localizer.Languages.Select(l => l.Code)];

    [Theory]
    [MemberData(nameof(Codes))]
    public void Every_language_has_every_english_key_with_the_same_placeholders(string code)
    {
        IReadOnlyDictionary<string, string> english = Localizer.Instance.Entries("en");
        IReadOnlyDictionary<string, string> table = Localizer.Instance.Entries(code);

        Assert.Equal(english.Keys.Order(StringComparer.Ordinal), table.Keys.Order(StringComparer.Ordinal));
        foreach ((string key, string text) in english)
        {
            Assert.True(
                Placeholder().Matches(text).Select(m => m.Value).Order().SequenceEqual(Placeholder().Matches(table[key]).Select(m => m.Value).Order()),
                $"{code}:{key} placeholders differ");
        }
    }

    [Fact]
    public void Lookup_falls_back_to_the_key_and_formats_like_python()
    {
        Assert.Equal("no_such_key", Localizer.Instance["no_such_key"]);
        Assert.Equal("✗ Process exited with code 3.", Localizer.Instance.Format("err_process", 3));
        Assert.StartsWith("✓ Rules from traces: 2 recorded run(s) found.", Localizer.Instance.Format("ap_rules_traces", 2), StringComparison.Ordinal);
    }

    [AvaloniaTheory]
    [InlineData("ro", "Română", "Despachetare")]
    [InlineData("de", "Deutsch", "Entpacken")]
    [InlineData("fr", "Français", "Extraire")]
    public void Romanian_german_and_french_are_offered_and_translated(string code, string name, string unpack)
    {
        Language language = Localizer.Languages.Single(l => l.Code == code);
        Assert.Equal(name, language.DisplayName);
        try
        {
            Localizer.Instance.Language = language;
            Assert.Equal(unpack, Localizer.Instance["nav_unpack"]);
            Assert.Contains("MkPFS.CSharp", Localizer.Instance["close_message"], StringComparison.Ordinal);
        }
        finally
        {
            Localizer.Instance.Language = Localizer.Languages[0];
        }
    }

    // Windows from other tests still listen to the localizer, so switch on the UI thread.
    [AvaloniaFact]
    public void Switching_language_notifies_and_translates()
    {
        List<string?> changes = [];
        void OnChanged(object? sender, PropertyChangedEventArgs e) => changes.Add(e.PropertyName);
        Localizer.Instance.PropertyChanged += OnChanged;
        try
        {
            Localizer.Instance.Language = Localizer.Languages[1];
            Assert.Equal("Verificar", Localizer.Instance["v_title"]);
            Localizer.Instance.Language = Localizer.Languages[2];
            Assert.Equal("Desempaquetar", Localizer.Instance["nav_unpack"]);
        }
        finally
        {
            Localizer.Instance.Language = Localizer.Languages[0];
            Localizer.Instance.PropertyChanged -= OnChanged;
        }

        Assert.Equal(3, changes.Count);
        Assert.Equal("Verify", Localizer.Instance["v_title"]);
    }
}
