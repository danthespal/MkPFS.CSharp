using System.Globalization;
using System.Text;

namespace MkPFS.Cli.Output;

/// <summary>
/// Minimal writer reproducing Python <c>json.dumps(value, indent=2, sort_keys=True)</c> for the shapes MkPFS
/// prints (objects of strings, numbers, booleans, null and string lists), so output matches byte for byte.
/// </summary>
public static class PythonJson
{
    /// <summary>Serialize an object whose values are <see langword="null"/>, bool, long, string or a list of strings.</summary>
    /// <param name="values">Key/value pairs (sorted by key on output).</param>
    /// <returns>JSON text without a trailing newline.</returns>
    public static string Dump(IReadOnlyDictionary<string, object?> values)
    {
        StringBuilder builder = new();
        builder.Append('{');
        bool first = true;
        foreach (KeyValuePair<string, object?> pair in values.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            builder.Append(first ? "\n  " : ",\n  ");
            first = false;
            WriteString(builder, pair.Key);
            builder.Append(": ");
            WriteValue(builder, pair.Value);
        }

        builder.Append(first ? "}" : "\n}");
        return builder.ToString();
    }

    private static void WriteValue(StringBuilder builder, object? value)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                break;
            case bool b:
                builder.Append(b ? "true" : "false");
                break;
            case string s:
                WriteString(builder, s);
                break;
            case IConvertible number when value is long or int or uint:
                builder.Append(number.ToString(CultureInfo.InvariantCulture));
                break;
            case IReadOnlyList<string> list:
                if (list.Count == 0)
                {
                    builder.Append("[]");
                    break;
                }

                builder.Append('[');
                for (int i = 0; i < list.Count; i++)
                {
                    builder.Append(i == 0 ? "\n    " : ",\n    ");
                    WriteString(builder, list[i]);
                }

                builder.Append("\n  ]");
                break;
            default:
                throw new NotSupportedException($"unsupported JSON value {value.GetType().Name}");
        }
    }

    // ensure_ascii=True: non-ASCII as \uXXXX (UTF-16 units), control characters escaped.
    private static void WriteString(StringBuilder builder, string value)
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
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                default:
                    if (c < 0x20 || c > 0x7E)
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
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
