using System.Globalization;
using System.Text;

namespace MkPFS.Core.Util;

/// <summary>
/// Python <c>str</c> semantics that .NET does not match out of the box. Used wherever output
/// must stay identical to Python MkPFS (names, sorting, hashing).
/// </summary>
public static class PythonText
{
    /// <summary>Python <c>str.isspace()</c> for one character (adds U+001C..U+001F to .NET's set).</summary>
    /// <param name="c">Character to test.</param>
    /// <returns><see langword="true"/> when Python treats the character as whitespace.</returns>
    public static bool IsSpace(char c) => char.IsWhiteSpace(c) || (c >= '\u001C' && c <= '\u001F');

    /// <summary>Python <c>str.strip()</c> with no arguments.</summary>
    /// <param name="value">Text to strip.</param>
    /// <returns>Text without leading or trailing Python whitespace.</returns>
    public static string Strip(string value)
    {
        int start = 0;
        int end = value.Length;
        while (start < end && IsSpace(value[start]))
        {
            start++;
        }

        while (end > start && IsSpace(value[end - 1]))
        {
            end--;
        }

        return value[start..end];
    }

    /// <summary>Python <c>str.split()</c> with no arguments (runs of whitespace, no empty parts).</summary>
    /// <param name="value">Text to split.</param>
    /// <returns>Non-empty parts.</returns>
    public static List<string> SplitWhitespace(string value)
    {
        List<string> parts = [];
        int i = 0;
        while (i < value.Length)
        {
            while (i < value.Length && IsSpace(value[i]))
            {
                i++;
            }

            int start = i;
            while (i < value.Length && !IsSpace(value[i]))
            {
                i++;
            }

            if (i > start)
            {
                parts.Add(value[start..i]);
            }
        }

        return parts;
    }

    /// <summary>
    /// Python <c>str.isalnum()</c> for one code point: letters (L*) plus numeric categories
    /// Nd, Nl and No. .NET <see cref="Rune.IsLetterOrDigit(Rune)"/> misses Nl and No.
    /// </summary>
    /// <param name="rune">Code point to test.</param>
    /// <returns><see langword="true"/> when Python treats the code point as alphanumeric.</returns>
    public static bool IsAlnum(Rune rune)
    {
        UnicodeCategory category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.UppercaseLetter
            or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter
            or UnicodeCategory.DecimalDigitNumber
            or UnicodeCategory.LetterNumber
            or UnicodeCategory.OtherNumber;
    }
}
