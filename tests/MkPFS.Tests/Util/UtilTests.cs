using System.Text;
using MkPFS.Core.Util;

namespace MkPFS.Tests.Util;

/// <summary>Port of Python <c>tests/mkpfs/test_utils.py</c> plus Python-semantics checks.</summary>
public sealed class UtilTests
{
    [Fact]
    public void HumanReadable_formats_common_units()
    {
        Assert.Equal("0.00 B", Sizes.HumanReadable(0));
        Assert.Equal("1.00 KB", Sizes.HumanReadable(1024));
        Assert.Equal("1.00 MB", Sizes.HumanReadable(1024 * 1024));
        Assert.Equal("3.62 MB", Sizes.HumanReadable(3_801_088));
        Assert.Equal("1.00 PB", Sizes.HumanReadable(1L << 50));
    }

    [Fact]
    public void CeilDiv_returns_the_ceiling()
    {
        Assert.Equal(1, Sizes.CeilDiv(1, 1));
        Assert.Equal(2, Sizes.CeilDiv(3, 2));
        Assert.Equal(0, Sizes.CeilDiv(0, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => Sizes.CeilDiv(1, 0));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(65536, true)]
    [InlineData(0, false)]
    [InlineData(3, false)]
    [InlineData(-2, false)]
    public void IsPowerOfTwo_accepts_only_positive_powers(long value, bool expected) =>
        Assert.Equal(expected, Sizes.IsPowerOfTwo(value));

    [Theory]
    [InlineData("out.FFPFS", ".ffpfs", "out.FFPFS", false)]
    [InlineData("image.ffpfs", ".ffpfs", "image.ffpfs", false)]
    [InlineData("out.bin", ".ffpfsc", "out.ffpfsc", true)]
    [InlineData("out", ".ffpfsc", "out.ffpfsc", true)]
    [InlineData(".hidden", ".ffpfs", ".hidden.ffpfs", true)]
    [InlineData("a.tar.gz", ".ffpfs", "a.tar.ffpfs", true)]
    public void NormalizeOutputPath_follows_pathlib_suffix_rules(string input, string suffix, string expected, bool changed)
    {
        (string path, bool didChange) = PathRules.NormalizeOutputPath(input, suffix);
        Assert.Equal(expected, path);
        Assert.Equal(changed, didChange);
    }

    [Fact]
    public void NormalizeOutputPath_keeps_path_when_adjust_disabled()
    {
        string input = Path.Combine("dir", "out.bin");
        Assert.Equal((input, false), PathRules.NormalizeOutputPath(input, ".ffpfsc", adjust: false));
        Assert.Equal((Path.Combine("dir", "out.ffpfsc"), true), PathRules.NormalizeOutputPath(input, ".ffpfsc"));
    }

    [Fact]
    public void ReadParamJson_returns_data_and_rejects_invalid_json()
    {
        using TempDir temp = new();
        string valid = temp.File("params.json", """{"a": 1}""");
        string invalid = temp.File("bad.json", "notjson");

        using (var document = GameParams.ReadParamJson(valid))
        {
            Assert.Equal(1, document.RootElement.GetProperty("a").GetInt32());
        }

        Assert.Throws<InvalidDataException>(() => GameParams.ReadParamJson(invalid));
    }

    [Fact]
    public void ReadExact_returns_bytes_and_rejects_truncation()
    {
        using MemoryStream buffer = new("0123456789"u8.ToArray());
        Assert.Equal("2345"u8.ToArray(), StreamUtil.ReadExact(buffer, 2, 4));

        using MemoryStream truncated = new("abc"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => StreamUtil.ReadExact(truncated, 0, 10));
    }

    [Theory]
    [InlineData(".DS_Store")]
    [InlineData("._payload.bin")]
    [InlineData(".Spotlight-V100")]
    [InlineData(".Trashes")]
    [InlineData(".fseventsd")]
    [InlineData("__MACOSX")]
    [InlineData("Thumbs.db")]
    [InlineData("thumbs.db")]
    [InlineData("desktop.ini")]
    [InlineData("Desktop.ini")]
    [InlineData("$RECYCLE.BIN")]
    [InlineData("System Volume Information")]
    [InlineData("._foo")]
    public void OS_metadata_names_are_ignored(string name) => Assert.True(NameRules.IsIgnoredName(name));

    [Theory]
    [InlineData("eboot.bin")]
    [InlineData("data.pkg")]
    [InlineData("sce_sys")]
    [InlineData("notjunk_but_real")]
    [InlineData("ds_store.txt")]
    [InlineData("_foo")]
    [InlineData(".foo")]
    public void Regular_names_are_kept(string name) => Assert.False(NameRules.IsIgnoredName(name));

    private static string Source(TempDir temp, string? json)
    {
        if (json is not null)
        {
            temp.File(Path.Combine("sce_sys", "param.json"), json);
        }

        return temp.Path;
    }

    [Fact]
    public void TitleId_is_read_from_param_json()
    {
        using TempDir temp = new();
        Assert.Equal("PPSA25872", GameParams.TitleIdFromSource(Source(temp, """{"titleId": " PPSA25872 "}""")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{not json")]
    [InlineData("""{"titleId": 123}""")]
    [InlineData("""{"titleId": "   "}""")]
    [InlineData("""[1, 2]""")]
    public void TitleId_is_null_when_missing_or_unusable(string? json)
    {
        using TempDir temp = new();
        Assert.Null(GameParams.TitleIdFromSource(Source(temp, json)));
    }

    [Theory]
    [InlineData("""{"titleId": "", "title_id": "PPSA00001"}""", "PPSA00001")]
    [InlineData("""{"titleId": null, "title_id": "PPSA00002"}""", "PPSA00002")]
    [InlineData("""{"title_id": "PPSA00003"}""", "PPSA00003")]
    [InlineData("""{"titleId": "PPSA00004", "title_id": "PPSA99999"}""", "PPSA00004")]
    public void TitleId_falls_back_to_title_id_only_when_titleId_is_falsy(string json, string expected)
    {
        using TempDir temp = new();
        Assert.Equal(expected, GameParams.TitleIdFromSource(Source(temp, json)));
    }

    [Fact]
    public void Basename_prefers_title_id_and_sanitizes()
    {
        using TempDir a = new();
        Assert.Equal("PPSA25872", GameParams.DefaultImageBasename(Source(a, """{"titleId": "PPSA25872"}""")));

        using TempDir b = new();
        Assert.Equal("PP_SA_25872", GameParams.DefaultImageBasename(Source(b, """{"titleId": "PP/SA 25872"}""")));
    }

    [Fact]
    public void Basename_falls_back_to_folder_name()
    {
        using TempDir temp = new();
        string folder = temp.Dir("My Game (EU)");
        Assert.Equal("My_Game__EU_", GameParams.DefaultImageBasename(folder));
        Assert.Equal("My_Game__EU_", GameParams.DefaultImageBasename(folder + Path.DirectorySeparatorChar));
    }

    [Theory]
    [InlineData("abc-1.2_x", "abc-1.2_x")]
    [InlineData("a b/c", "a_b_c")]
    [InlineData("café", "café")]
    [InlineData("x½y", "x½y")] // U+00BD is No: Python isalnum() is True
    [InlineData("Ⅷ", "Ⅷ")] // U+2167 ROMAN NUMERAL EIGHT is Nl
    [InlineData("a\U0001F600b", "a_b")] // emoji is one code point, so one underscore
    public void SanitizeNameComponent_uses_python_isalnum_per_code_point(string input, string expected) =>
        Assert.Equal(expected, NameRules.SanitizeNameComponent(input));

    [Theory]
    [InlineData("My Game: (Deluxe) [EU]", "My Game Deluxe EU")]
    [InlineData("  a\t\u001Cb  ", "a b")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void UiSanitizeBasename_replaces_problem_chars_and_collapses_whitespace(string? input, string expected) =>
        Assert.Equal(expected, NameRules.UiSanitizeBasename(input));

    [Fact]
    public void PythonText_matches_python_whitespace_rules()
    {
        Assert.Equal("x", PythonText.Strip("\u001F x  "));
        Assert.Equal(["a", "b"], PythonText.SplitWhitespace(" a\u001Db "));
        Assert.True(PythonText.IsAlnum(new Rune('7')));
        Assert.False(PythonText.IsAlnum(new Rune('-')));
    }

    [Fact]
    public void ResolveTempRoot_creates_requested_folder()
    {
        using TempDir temp = new();
        string target = Path.Combine(temp.Path, "nested", "tmp");

        Assert.Equal(Path.GetFullPath(target), PathRules.ResolveTempRoot(target));
        Assert.True(Directory.Exists(target));
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetTempPath()), PathRules.ResolveTempRoot());
    }
}
