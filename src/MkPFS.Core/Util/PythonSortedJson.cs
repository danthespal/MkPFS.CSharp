using System.Globalization;
using System.Text;

namespace MkPFS.Core.Util;

/// <summary>
/// Python <c>json.dumps(value, sort_keys=True, ...)</c> for byte-exact output: keys sorted by code point, floats as
/// Python repr. Values: <see langword="null"/>, <see cref="bool"/>, integers, <see cref="double"/>,
/// <see cref="string"/>, <see cref="IReadOnlyDictionary{TKey, TValue}"/> with string keys, and other
/// <see cref="System.Collections.IEnumerable"/> as arrays.
/// </summary>
public static class PythonSortedJson
{
    /// <summary><c>json.dumps(value, sort_keys=True, separators=(",", ":"))</c> (ASCII escapes).</summary>
    /// <param name="value">Value tree.</param>
    /// <returns>JSON text.</returns>
    public static string Compact(object? value)
    {
        StringBuilder builder = new();
        Write(builder, value, indent: null, level: 0, ensureAscii: true);
        return builder.ToString();
    }

    /// <summary><c>json.dumps(value, indent=2, sort_keys=True, ensure_ascii=...)</c>.</summary>
    /// <param name="value">Value tree.</param>
    /// <param name="ensureAscii">Escape non-ASCII characters as <c>\uXXXX</c>.</param>
    /// <returns>JSON text without a trailing newline.</returns>
    public static string Indented(object? value, bool ensureAscii = false)
    {
        StringBuilder builder = new();
        Write(builder, value, indent: 2, level: 0, ensureAscii);
        return builder.ToString();
    }

    private static void Write(StringBuilder builder, object? value, int? indent, int level, bool ensureAscii)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                break;
            case bool b:
                builder.Append(b ? "true" : "false");
                break;
            case int or long or uint or ulong or byte or ushort:
                builder.Append(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture));
                break;
            case double d:
                builder.Append(double.IsNaN(d) ? "NaN" : double.IsInfinity(d) ? (d > 0 ? "Infinity" : "-Infinity") : PythonText.FloatRepr(d));
                break;
            case string s:
                WriteString(builder, s, ensureAscii);
                break;
            case IReadOnlyDictionary<string, object?> map:
                List<string> keys = [.. map.Keys.OrderBy(k => k, PythonCodePointComparer.Instance)];
                WriteContainer(builder, '{', '}', keys.Count, indent, level, i =>
                {
                    WriteString(builder, keys[i], ensureAscii);
                    builder.Append(indent is null ? ":" : ": ");
                    Write(builder, map[keys[i]], indent, level + 1, ensureAscii);
                });
                break;
            case System.Collections.IEnumerable items:
                List<object?> list = [.. items.Cast<object?>()];
                WriteContainer(builder, '[', ']', list.Count, indent, level, i => Write(builder, list[i], indent, level + 1, ensureAscii));
                break;
            default:
                throw new ArgumentException($"unsupported JSON value type {value.GetType().Name}");
        }
    }

    // Python: empty containers stay "[]"/"{}"; with indent each item goes on its own line.
    private static void WriteContainer(StringBuilder builder, char open, char close, int count, int? indent, int level, Action<int> item)
    {
        builder.Append(open);
        if (count == 0)
        {
            builder.Append(close);
            return;
        }

        for (int i = 0; i < count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            if (indent is { } width)
            {
                builder.Append('\n').Append(' ', width * (level + 1));
            }

            item(i);
        }

        if (indent is { } closing)
        {
            builder.Append('\n').Append(' ', closing * level);
        }

        builder.Append(close);
    }

    private static void WriteString(StringBuilder builder, string value, bool ensureAscii)
    {
        builder.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append(@"\\");
                    break;
                case '\n':
                    builder.Append(@"\n");
                    break;
                case '\r':
                    builder.Append(@"\r");
                    break;
                case '\t':
                    builder.Append(@"\t");
                    break;
                case '\b':
                    builder.Append(@"\b");
                    break;
                case '\f':
                    builder.Append(@"\f");
                    break;
                default:
                    // ensure_ascii escapes everything outside ' '..'~'; otherwise only control characters.
                    if (c < 0x20 || (ensureAscii && c > 0x7E))
                    {
                        builder.Append(@"\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
    }
}
