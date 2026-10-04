using System.Globalization;
using MkPFS.Core.AMPR;
using MkPFS.Core.Util;
using Tomlyn;
using Tomlyn.Model;

namespace MkPFS.Build.AMPRPack;

/// <summary>
/// Python built-in conversions (<c>str</c>, <c>int</c>, <c>float</c>, <c>bool</c>, <c>parse_size</c>) applied to TOML
/// values, as ampr_pack's <c>load_config</c> does. TOML values are <see cref="string"/>, <see cref="long"/>,
/// <see cref="double"/>, <see cref="bool"/>, <see cref="TomlDateTime"/>, <see cref="TomlArray"/>,
/// <see cref="TomlTableArray"/> and <see cref="TomlTable"/>. Where Python raises <c>TypeError</c> or
/// <c>AttributeError</c> (a traceback, exit 1) this raises <see cref="ArgumentException"/> with the same text.
/// </summary>
internal static class PythonValue
{
    /// <summary>Python type name for messages.</summary>
    public static string TypeName(object? value) => value switch
    {
        null => "NoneType",
        string => "str",
        bool => "bool",
        long => "int",
        double => "float",
        TomlDateTime => "datetime",
        TomlTable => "dict",
        TomlArray or TomlTableArray => "list",
        _ => value.GetType().Name,
    };

    /// <summary>Python <c>str(value)</c>.</summary>
    public static string Str(object? value) => value switch
    {
        string s => s,
        _ => Repr(value),
    };

    /// <summary>Python <c>repr(value)</c>.</summary>
    public static string Repr(object? value) => value switch
    {
        null => "None",
        string s => PythonText.Repr(s),
        bool b => b ? "True" : "False",
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => PythonText.FloatRepr(d),
        TomlTable t => "{" + string.Join(", ", t.Select(kv => PythonText.Repr(kv.Key) + ": " + Repr(kv.Value))) + "}",
        TomlTableArray a => "[" + string.Join(", ", a.Select(Repr)) + "]",
        TomlArray a => "[" + string.Join(", ", a.Select(Repr)) + "]",
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>Python <c>int(value)</c>.</summary>
    public static long Int(object? value)
    {
        switch (value)
        {
            case bool b:
                return b ? 1 : 0;
            case long l:
                return l;
            case double d when double.IsNaN(d):
                throw new ArgumentException("cannot convert float NaN to integer");
            case double d when double.IsInfinity(d):
                throw new ArgumentException("cannot convert float infinity to integer");
            case double d:
                return (long)Math.Truncate(d);
            case string s:
                string text = PythonText.Strip(s);
                if (IsPythonNumber(text, allowFraction: false)
                    && long.TryParse(text.Replace("_", string.Empty, StringComparison.Ordinal), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long parsed))
                {
                    return parsed;
                }

                throw new ArgumentException($"invalid literal for int() with base 10: {PythonText.Repr(s)}");
            default:
                throw new ArgumentException($"int() argument must be a string, a bytes-like object or a real number, not '{TypeName(value)}'");
        }
    }

    /// <summary>Python <c>float(value)</c>.</summary>
    public static double Float(object? value)
    {
        switch (value)
        {
            case bool b:
                return b ? 1.0 : 0.0;
            case long l:
                return l;
            case double d:
                return d;
            case string s:
                string text = PythonText.Strip(s);
                string lower = text.TrimStart('+', '-').ToLowerInvariant();
                if (lower is "inf" or "infinity" or "nan")
                {
                    double special = lower == "nan" ? double.NaN : double.PositiveInfinity;
                    return text.StartsWith('-') ? -special : special;
                }

                if (IsPythonNumber(text, allowFraction: true)
                    && double.TryParse(text.Replace("_", string.Empty, StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                {
                    return parsed;
                }

                throw new ArgumentException($"could not convert string to float: {PythonText.Repr(s)}");
            default:
                throw new ArgumentException($"float() argument must be a string or a real number, not '{TypeName(value)}'");
        }
    }

    /// <summary>Python <c>bool(value)</c> (truthiness).</summary>
    public static bool Bool(object? value) => value switch
    {
        null => false,
        bool b => b,
        long l => l != 0,
        double d => d != 0.0,
        string s => s.Length > 0,
        TomlTable t => t.Count > 0,
        TomlArray a => a.Count > 0,
        TomlTableArray a => a.Count > 0,
        _ => true,
    };

    /// <summary>
    /// ampr_pack <c>parse_size(value)</c>: integers pass through, strings are parsed. A TOML boolean is treated as
    /// 0 or 1 (Python keeps the <c>bool</c>; only the canonical JSON would differ).
    /// </summary>
    public static long Size(object? value) => value switch
    {
        bool b => b ? 1 : 0,
        long l => AMPRSize.Parse(l),
        string s => AMPRSize.Parse(s),
        _ => throw new ArgumentException($"'{TypeName(value)}' object has no attribute 'strip'"),
    };

    /// <summary>ampr_pack <c>block_shift(value)</c>.</summary>
    public static int BlockShift(object? value) => AMPRSize.BlockShift(Size(value));

    // Python int()/float() literals: digits with single underscores between digits, optional sign,
    // and for floats an optional fraction and exponent.
    private static bool IsPythonNumber(string text, bool allowFraction)
    {
        int i = 0;
        if (i < text.Length && text[i] is '+' or '-')
        {
            i++;
        }

        bool digits = false;
        bool lastDigit = false;
        bool seenDot = false;
        bool seenExp = false;
        for (; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsAsciiDigit(c))
            {
                digits = true;
                lastDigit = true;
            }
            else if (c == '_' && lastDigit && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1]))
            {
                lastDigit = false;
            }
            else if (allowFraction && c == '.' && !seenDot && !seenExp)
            {
                seenDot = true;
                lastDigit = false;
            }
            else if (allowFraction && c is 'e' or 'E' && digits && !seenExp)
            {
                seenExp = true;
                lastDigit = false;
                digits = false;
                if (i + 1 < text.Length && text[i + 1] is '+' or '-')
                {
                    i++;
                }
            }
            else
            {
                return false;
            }
        }

        return digits;
    }
}
