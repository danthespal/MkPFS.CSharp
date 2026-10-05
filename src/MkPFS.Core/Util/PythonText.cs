using System.Globalization;
using System.Numerics;
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

    /// <summary>
    /// Python <c>repr(str)</c>: single quotes unless the text has <c>'</c> and no <c>"</c>; escapes
    /// backslash, the quote, <c>\t \n \r</c>, and non-printable code points as <c>\xNN</c>, <c>\uNNNN</c>
    /// or <c>\UNNNNNNNN</c>.
    /// </summary>
    /// <param name="value">Text.</param>
    /// <returns>The repr, including quotes.</returns>
    public static string Repr(string value)
    {
        char quote = value.Contains('\'', StringComparison.Ordinal) && !value.Contains('"', StringComparison.Ordinal) ? '"' : '\'';
        StringBuilder builder = new(value.Length + 2);
        builder.Append(quote);
        foreach (Rune rune in value.EnumerateRunes())
        {
            int code = rune.Value;
            if (code == '\\' || code == quote)
            {
                builder.Append('\\').Append((char)code);
            }
            else if (code == '\t')
            {
                builder.Append("\\t");
            }
            else if (code == '\n')
            {
                builder.Append("\\n");
            }
            else if (code == '\r')
            {
                builder.Append("\\r");
            }
            else if (IsPrintable(rune))
            {
                builder.Append(rune.ToString());
            }
            else
            {
                builder.Append(code switch
                {
                    <= 0xFF => $"\\x{code:x2}",
                    <= 0xFFFF => $"\\u{code:x4}",
                    _ => $"\\U{code:x8}",
                });
            }
        }

        return builder.Append(quote).ToString();
    }

    /// <summary>Python <c>str.splitlines()</c>: splits on LF, CR, CRLF, VT, FF, U+001C..U+001E, U+0085, U+2028 and U+2029.</summary>
    /// <param name="value">Text.</param>
    /// <returns>Lines without terminators; no trailing empty line.</returns>
    public static List<string> SplitLines(string value)
    {
        List<string> lines = [];
        int start = 0;
        int i = 0;
        while (i < value.Length)
        {
            char c = value[i];
            if (c is '\n' or '\r' or '\v' or '\f' or '\u001C' or '\u001D' or '\u001E' or '\u0085' or '\u2028' or '\u2029')
            {
                lines.Add(value[start..i]);
                i += c == '\r' && i + 1 < value.Length && value[i + 1] == '\n' ? 2 : 1;
                start = i;
            }
            else
            {
                i++;
            }
        }

        if (start < value.Length)
        {
            lines.Add(value[start..]);
        }

        return lines;
    }

    /// <summary>
    /// Python <c>repr(float)</c> (also <c>json.dumps</c>): shortest round-trip digits, fixed notation when the
    /// decimal exponent is in (-4, 16], otherwise <c>d.ddde+/-XX</c>; <c>inf</c>, <c>-inf</c>, <c>nan</c>.
    /// </summary>
    /// <param name="value">Value.</param>
    /// <returns>Text.</returns>
    public static string FloatRepr(double value)
    {
        if (double.IsNaN(value))
        {
            return "nan";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "inf" : "-inf";
        }

        // "R" gives the shortest round-trip digits, as mantissa[E+/-exp].
        string text = value.ToString("R", CultureInfo.InvariantCulture);
        bool negative = text.StartsWith('-');
        if (negative)
        {
            text = text[1..];
        }

        int exponent = 0;
        int e = text.IndexOf('E', StringComparison.Ordinal);
        if (e >= 0)
        {
            exponent = int.Parse(text.AsSpan(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            text = text[..e];
        }

        int dot = text.IndexOf('.', StringComparison.Ordinal);
        string digits = dot < 0 ? text : text[..dot] + text[(dot + 1)..];
        int pointPosition = (dot < 0 ? text.Length : dot) + exponent;
        int leadingZeros = digits.Length - digits.TrimStart('0').Length;
        digits = digits.Trim('0');
        if (digits.Length == 0)
        {
            return negative ? "-0.0" : "0.0";
        }

        // Value is 0.DIGITS x 10^decpt.
        int decpt = pointPosition - leadingZeros;
        string body;
        if (decpt > -4 && decpt <= 16)
        {
            body = decpt <= 0
                ? "0." + new string('0', -decpt) + digits
                : decpt >= digits.Length
                    ? digits + new string('0', decpt - digits.Length) + ".0"
                    : digits[..decpt] + "." + digits[decpt..];
        }
        else
        {
            int exp = decpt - 1;
            string mantissa = digits.Length == 1 ? digits : digits[0] + "." + digits[1..];
            body = mantissa + "e" + (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture);
        }

        return negative ? "-" + body : body;
    }

    /// <summary>
    /// Python <c>format(value, f".{decimals}f")</c>: the exact binary value rounded half to even, unlike .NET
    /// fixed-point formatting, which rounds ties away from zero.
    /// </summary>
    /// <param name="value">Finite value.</param>
    /// <param name="decimals">Digits after the point.</param>
    /// <returns>Text.</returns>
    public static string FormatFixed(double value, int decimals)
    {
        if (!double.IsFinite(value))
        {
            return FloatRepr(value);
        }

        long bits = BitConverter.DoubleToInt64Bits(value);
        bool negative = bits < 0;
        int exponentBits = (int)((bits >> 52) & 0x7FF);
        long fraction = bits & 0xFFFFFFFFFFFFFL;
        BigInteger mantissa = exponentBits == 0 ? fraction : fraction | (1L << 52);
        int exponent = (exponentBits == 0 ? 1 : exponentBits) - 1075;

        // value * 10^decimals = mantissa * 2^exponent * 10^decimals, rounded half to even.
        BigInteger numerator = mantissa * BigInteger.Pow(10, decimals);
        BigInteger scaled;
        if (exponent >= 0)
        {
            scaled = numerator << exponent;
        }
        else
        {
            BigInteger denominator = BigInteger.One << -exponent;
            scaled = BigInteger.DivRem(numerator, denominator, out BigInteger remainder);
            int compare = (remainder * 2).CompareTo(denominator);
            if (compare > 0 || (compare == 0 && !scaled.IsEven))
            {
                scaled += 1;
            }
        }

        string digits = scaled.ToString(CultureInfo.InvariantCulture).PadLeft(decimals + 1, '0');
        string text = decimals == 0 ? digits : digits[..^decimals] + "." + digits[^decimals..];
        return negative ? "-" + text : text;
    }

    // Python str.isprintable(): everything except categories Cc, Cf, Cs, Co, Cn, Zl, Zp and Zs other than space.
    private static bool IsPrintable(Rune rune) =>
        rune.Value == ' ' || Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.Control
            or UnicodeCategory.Format
            or UnicodeCategory.Surrogate
            or UnicodeCategory.PrivateUse
            or UnicodeCategory.OtherNotAssigned
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator
            or UnicodeCategory.SpaceSeparator);
}
