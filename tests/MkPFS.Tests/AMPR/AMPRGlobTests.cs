using MkPFS.Core.AMPR;
using MkPFS.Core.Util;

namespace MkPFS.Tests.AMPR;

/// <summary>fnmatch, float repr, splitlines and JSON helpers; expected values come from CPython 3.11.</summary>
public sealed class AMPRGlobTests
{
    [Theory]
    [InlineData("a/b.txt", "*", true)]
    [InlineData("a/b.txt", "*.txt", true)]
    [InlineData("a/b.txt", "a/*", true)]
    [InlineData("a/b/c", "a/**", true)]
    [InlineData("a/b/c", "**/c", true)]
    [InlineData("c", "**/c", false)]
    [InlineData("x.BIN", "*.bin", false)]
    [InlineData("a[1]", "a[[]1]", true)]
    [InlineData("a1", "a[0-9]", true)]
    [InlineData("a-", "a[a-]", true)]
    [InlineData("ab", "a[!b]", false)]
    [InlineData("ac", "a[!b]", true)]
    [InlineData("a]", "a[]]", true)]
    [InlineData("a!", "a[!]", false)]
    [InlineData("a[!]", "a[!]", true)]
    [InlineData("a", "a[]", false)]
    [InlineData("ab", "a?", true)]
    [InlineData("a.b", "a.b", true)]
    [InlineData("axb", "a.b", false)]
    [InlineData("a+b", "a+b", true)]
    [InlineData("a\\b", "a\\b", true)]
    [InlineData("a^", "a[^]", true)]
    [InlineData("a^", "a[\\^]", true)]
    [InlineData("a\\", "a[\\^]", true)]
    [InlineData("ab", "a[z-a]", false)]
    [InlineData("a-", "a[z-a-]", true)]
    [InlineData("a&", "a[&&]", true)]
    [InlineData("a~", "a[~~]", true)]
    [InlineData("a\nb", "a*b", true)]
    [InlineData("a/b", "a?b", true)]
    [InlineData("a", "[a-c-e]", true)]
    [InlineData("d", "[a-c-e]", false)]
    [InlineData("-", "[a-c-e]", true)]
    [InlineData("\u00e9", "?", true)]
    [InlineData("a", "a**", true)]
    [InlineData("", "*", true)]
    [InlineData("a[", "a[", true)]
    [InlineData("a[b", "a[b", true)]
    [InlineData("a]x", "a[]x]", false)]
    [InlineData("a]", "a[!]x]", false)]
    [InlineData("ay", "a[!]x]", true)]
    public void FnMatchCase_matches_python(string name, string pattern, bool expected) =>
        Assert.Equal(expected, AMPRGlob.FnMatchCase(name, pattern));

    [Fact]
    public void Matches_normalizes_separators_and_leading_slashes()
    {
        Assert.True(AMPRGlob.Matches("\\assets\\a.bin", ["/assets/*.bin"]));
        Assert.True(AMPRGlob.Matches("assets/a.bin", ["x", "assets\\*"]));
        Assert.False(AMPRGlob.Matches("assets/a.bin", []));
    }

    [Fact]
    public void Matches_large_list_agrees_with_per_pattern_fnmatch()
    {
        // Large lists take the compiled PatternSet path (exact paths in a hash set, globs as regexes).
        List<string> patterns = [.. Enumerable.Range(0, 64).Select(i => $"contents/{i:X2}/file{i}"),
            "/abs/lead.bin", "win\\sep.bin", "a.b", "a+b", "glob/*.dat", "set/a[0-9]", "q/?x"];
        string[] paths =
        [
            "contents/00/file0", "contents/3F/file63", "contents/3F/file62", "CONTENTS/00/file0", "abs/lead.bin",
            "/abs/lead.bin", "win/sep.bin", "a.b", "axb", "a+b", "aab", "glob/x.dat", "glob/sub/x.dat", "glob/x.da",
            "set/a5", "set/ab", "q/ax", "q/axx", "",
        ];

        foreach (string path in paths)
        {
            string normalized = path.Replace('\\', '/').TrimStart('/');
            bool expected = patterns.Any(p => AMPRGlob.FnMatchCase(normalized, p.Replace('\\', '/').TrimStart('/')));
            Assert.Equal(expected, AMPRGlob.Matches(path, patterns));
        }
    }

    [Fact]
    public void Matches_large_list_recompiles_after_the_list_grows()
    {
        List<string> patterns = [.. Enumerable.Range(0, 20).Select(i => $"p{i}")];
        Assert.False(AMPRGlob.Matches("late/added.bin", patterns));

        patterns.Add("late/*.bin");
        Assert.True(AMPRGlob.Matches("late/added.bin", patterns));
    }

    [Theory]
    [InlineData(0.01, "0.01")]
    [InlineData(0.125, "0.125")]
    [InlineData(1e-05, "1e-05")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(1e16, "1e+16")]
    [InlineData(1e15, "1000000000000000.0")]
    [InlineData(123456789012345678.0, "1.2345678901234568e+17")]
    [InlineData(0.0, "0.0")]
    [InlineData(-0.0, "-0.0")]
    [InlineData(1.0, "1.0")]
    [InlineData(0.30000000000000004, "0.30000000000000004")]
    [InlineData(5e-324, "5e-324")]
    [InlineData(1.7976931348623157e308, "1.7976931348623157e+308")]
    [InlineData(2.5, "2.5")]
    [InlineData(100.0, "100.0")]
    [InlineData(1e22, "1e+22")]
    [InlineData(-12.5, "-12.5")]
    public void FloatRepr_matches_python(double value, string expected) => Assert.Equal(expected, PythonText.FloatRepr(value));

    [Fact]
    public void SplitLines_matches_python()
    {
        Assert.Equal(["a", "b", "", "c", "d", "e"], PythonText.SplitLines("a\nb\r\n\rc\u2028d\u0085e\n"));
        Assert.Empty(PythonText.SplitLines(string.Empty));
    }

    [Fact]
    public void Json_matches_python_dumps_sorted_compact()
    {
        Dictionary<string, object?> value = new()
        {
            ["b"] = new List<object?> { 1L, 0.5, true, null, "\u00e9\"\\\n\u007f" },
            ["a"] = new Dictionary<string, object?> { ["y"] = 1e-05, ["x"] = 0.0 },
        };

        Assert.Equal("{\"a\":{\"x\":0.0,\"y\":1e-05},\"b\":[1,0.5,true,null,\"\\u00e9\\\"\\\\\\n\\u007f\"]}", PythonSortedJson.Compact(value));
    }
}
