using System.Globalization;
using System.Text;
using MkPFS.Core.AMPR;
using MkPFS.Core.Util;

namespace MkPFS.Build.AMPRPack;

/// <summary>
/// Volume name pattern such as <c>ampr_assets-{id:03d}.pak</c>: a Python <c>str.format</c> string over the fields
/// <c>group</c> (text) and <c>lane</c>, <c>volume</c>, <c>id</c> (integers). Port of ampr_pack
/// <c>_pack_output_glob</c> plus the parts of <c>string.Formatter</c> and the format-spec mini-language it relies on.
/// Float presentation types <c>g</c>/<c>G</c> on integers are not supported (rejected as invalid).
/// </summary>
public sealed class AMPRPackPattern
{
    private static readonly string[] AllowedFields = ["group", "lane", "volume", "id"];

    private readonly List<Segment> _segments;

    private AMPRPackPattern(string pattern, List<Segment> segments, string outputGlob)
    {
        Pattern = pattern;
        _segments = segments;
        OutputGlob = outputGlob;
    }

    /// <summary>The pattern text.</summary>
    public string Pattern { get; }

    /// <summary>Glob matching every volume this pattern can produce (fields become <c>*</c>).</summary>
    public string OutputGlob { get; }

    /// <summary>Parse and check a pattern like ampr_pack <c>_pack_output_glob</c>.</summary>
    /// <param name="pattern">Pattern.</param>
    /// <returns>The pattern.</returns>
    /// <exception cref="AMPRPackException">The pattern is malformed, uses another field, is empty, or renders an unsafe path.</exception>
    public static AMPRPackPattern Parse(string pattern)
    {
        List<Segment> segments;
        try
        {
            segments = ParseFormat(pattern);
        }
        catch (FormatException exc)
        {
            throw new AMPRPackException($"invalid pack_pattern: {PythonText.Repr(pattern)}", exc);
        }

        StringBuilder glob = new();
        foreach (Segment segment in segments)
        {
            if (segment.Field is null)
            {
                glob.Append(segment.Literal);
                continue;
            }

            if (!AllowedFields.Contains(segment.Field, StringComparer.Ordinal))
            {
                throw new AMPRPackException(
                    $"pack_pattern uses unsupported field {PythonText.Repr(segment.Field)}; allowed fields are group, lane, volume and id");
            }

            glob.Append('*');
        }

        string outputGlob = glob.ToString().Replace('\\', '/').TrimStart('/');
        if (outputGlob.Length == 0)
        {
            throw new AMPRPackException("pack_pattern cannot be empty");
        }

        AMPRPackPattern result = new(pattern, segments, outputGlob);
        // A concrete rendering catches absolute paths and traversal before any data is written.
        try
        {
            AMPRAssetPath.SafeOutputPath(".", result.Render("g", 0, 0, 0).Replace('\\', '/'));
        }
        catch (Exception exc) when (exc is ArgumentException or FormatException)
        {
            throw new AMPRPackException($"unsafe or invalid pack_pattern: {PythonText.Repr(pattern)}", exc);
        }

        return result;
    }

    /// <summary>Python <c>pattern.format(group=..., lane=..., volume=..., id=...)</c>.</summary>
    /// <param name="group">Group name.</param>
    /// <param name="lane">Lane.</param>
    /// <param name="volume">Volume within the lane.</param>
    /// <param name="id">Pack id.</param>
    /// <returns>Volume file name.</returns>
    /// <exception cref="FormatException">A conversion or format spec is invalid for its value.</exception>
    public string Render(string group, long lane, long volume, long id)
    {
        StringBuilder output = new();
        foreach (Segment segment in _segments)
        {
            if (segment.Field is null)
            {
                output.Append(segment.Literal);
                continue;
            }

            object value = segment.Field switch
            {
                "group" => group,
                "lane" => lane,
                "volume" => volume,
                _ => id,
            };
            output.Append(FormatField(value, segment.Conversion, segment.Spec));
        }

        return output.ToString();
    }

    private static string FormatField(object value, char? conversion, string spec)
    {
        if (conversion is { } c)
        {
            value = c switch
            {
                's' => value is string s ? s : ((long)value).ToString(CultureInfo.InvariantCulture),
                'r' or 'a' => value is string s ? PythonText.Repr(s) : ((long)value).ToString(CultureInfo.InvariantCulture),
                _ => throw new FormatException($"Unknown conversion specifier {c}"),
            };
        }

        if (spec.Contains('{', StringComparison.Ordinal) || spec.Contains('}', StringComparison.Ordinal))
        {
            // Nested replacement fields would need arguments this pattern does not have (Python KeyError).
            throw new FormatException("nested replacement field in format spec");
        }

        FormatSpec parsed = FormatSpec.Parse(spec);
        return value is string text ? parsed.FormatString(text) : parsed.FormatInteger((long)value);
    }

    // string.Formatter().parse: literal text with {{ and }} escapes, then fields "{name[!conv][:spec]}".
    private static List<Segment> ParseFormat(string pattern)
    {
        List<Segment> segments = [];
        StringBuilder literal = new();
        int i = 0;
        while (i < pattern.Length)
        {
            char c = pattern[i];
            if (c == '}')
            {
                if (i + 1 < pattern.Length && pattern[i + 1] == '}')
                {
                    literal.Append('}');
                    i += 2;
                    continue;
                }

                throw new FormatException("Single '}' encountered in format string");
            }

            if (c != '{')
            {
                literal.Append(c);
                i++;
                continue;
            }

            if (i + 1 < pattern.Length && pattern[i + 1] == '{')
            {
                literal.Append('{');
                i += 2;
                continue;
            }

            if (i + 1 >= pattern.Length)
            {
                throw new FormatException("Single '{' encountered in format string");
            }

            // Find the matching '}' (format specs may nest braces).
            int depth = 1;
            int j = i + 1;
            while (j < pattern.Length)
            {
                if (pattern[j] == '{')
                {
                    depth++;
                }
                else if (pattern[j] == '}' && --depth == 0)
                {
                    break;
                }

                j++;
            }

            if (j >= pattern.Length)
            {
                throw new FormatException("expected '}' before end of string");
            }

            segments.Add(new Segment(literal.ToString(), null, null, string.Empty));
            literal.Clear();
            segments.Add(ParseField(pattern[(i + 1)..j]));
            i = j + 1;
        }

        segments.Add(new Segment(literal.ToString(), null, null, string.Empty));
        return segments;
    }

    private static Segment ParseField(string field)
    {
        int k = 0;
        while (k < field.Length)
        {
            char c = field[k];
            if (c == '{')
            {
                throw new FormatException("unexpected '{' in field name");
            }

            if (c == '[')
            {
                while (k < field.Length && field[k] != ']')
                {
                    k++;
                }

                continue;
            }

            if (c is ':' or '!')
            {
                break;
            }

            k++;
        }

        string name = field[..k];
        char? conversion = null;
        if (k < field.Length && field[k] == '!')
        {
            if (k + 1 >= field.Length)
            {
                throw new FormatException("end of string while looking for conversion specifier");
            }

            conversion = field[k + 1];
            k += 2;
            if (k < field.Length && field[k] != ':')
            {
                throw new FormatException("expected ':' after conversion specifier");
            }
        }

        string spec = k < field.Length ? field[(k + 1)..] : string.Empty;
        return new Segment(string.Empty, name, conversion, spec);
    }

    private sealed record Segment(string Literal, string? Field, char? Conversion, string Spec);

    // Python format-spec mini-language: [[fill]align][sign][z][#][0][width][grouping][.precision][type].
    private sealed record FormatSpec(
        char? Fill, char? Align, char Sign, bool Alternate, bool ZeroPad, int Width, char? Grouping, int? Precision, char? Type)
    {
        public static FormatSpec Parse(string spec)
        {
            int i = 0;
            char? fill = null;
            char? align = null;
            if (spec.Length >= 2 && spec[1] is '<' or '>' or '=' or '^')
            {
                fill = spec[0];
                align = spec[1];
                i = 2;
            }
            else if (spec.Length >= 1 && spec[0] is '<' or '>' or '=' or '^')
            {
                align = spec[0];
                i = 1;
            }

            char sign = '-';
            if (i < spec.Length && spec[i] is '+' or '-' or ' ')
            {
                sign = spec[i++];
            }

            if (i < spec.Length && spec[i] == 'z')
            {
                throw new FormatException("Negative zero coercion (z) not allowed in format specifier");
            }

            bool alternate = false;
            if (i < spec.Length && spec[i] == '#')
            {
                alternate = true;
                i++;
            }

            bool zero = false;
            if (i < spec.Length && spec[i] == '0')
            {
                zero = true;
                i++;
            }

            int width = ReadNumber(spec, ref i) ?? 0;
            char? grouping = null;
            if (i < spec.Length && spec[i] is ',' or '_')
            {
                grouping = spec[i++];
            }

            int? precision = null;
            if (i < spec.Length && spec[i] == '.')
            {
                i++;
                precision = ReadNumber(spec, ref i) ?? throw new FormatException("Format specifier missing precision");
            }

            char? type = null;
            if (i < spec.Length)
            {
                type = spec[i++];
            }

            return i == spec.Length
                ? new FormatSpec(fill, align, sign, alternate, zero, width, grouping, precision, type)
                : throw new FormatException("Invalid format specifier");
        }

        public string FormatString(string value)
        {
            if (Type is not (null or 's'))
            {
                throw new FormatException($"Unknown format code '{Type}' for object of type 'str'");
            }

            if (Sign != '-' || Alternate || Align == '=' || Grouping is not null)
            {
                throw new FormatException("invalid format specifier for a string");
            }

            if (Precision is { } p && p < value.Length)
            {
                value = value[..p];
            }

            // Since Python 3.10 a leading '0' only sets the fill for strings; the default alignment stays left.
            char fill = Fill ?? (ZeroPad ? '0' : ' ');
            return Pad(string.Empty, value, fill, Align ?? '<');
        }

        public string FormatInteger(long value)
        {
            string digits;
            string prefix = string.Empty;
            ulong magnitude = value < 0 ? (ulong)(-(value + 1)) + 1 : (ulong)value;
            switch (Type)
            {
                case null or 'd' or 'n':
                    if (Type == 'n' && Grouping is not null)
                    {
                        throw new FormatException("Cannot specify ',' with 'n'.");
                    }

                    digits = magnitude.ToString(CultureInfo.InvariantCulture);
                    break;
                case 'b' or 'o' or 'x' or 'X':
                    if (Grouping == ',')
                    {
                        throw new FormatException($"Cannot specify ',' with '{Type}'.");
                    }

                    digits = Type switch
                    {
                        'b' => Convert.ToString((long)magnitude, 2),
                        'o' => Convert.ToString((long)magnitude, 8),
                        'x' => magnitude.ToString("x", CultureInfo.InvariantCulture),
                        _ => magnitude.ToString("X", CultureInfo.InvariantCulture),
                    };
                    if (Alternate)
                    {
                        prefix = Type switch { 'b' => "0b", 'o' => "0o", 'x' => "0x", _ => "0X" };
                    }

                    break;
                case 'c':
                    if (Sign != '-' || Alternate || Grouping is not null || value is < 0 or > 0x10FFFF)
                    {
                        throw new FormatException("invalid format specifier for 'c'");
                    }

                    return FormatChar((int)value);
                case 'f' or 'F' or 'e' or 'E' or '%':
                    return FormatFloat(value);
                default:
                    throw new FormatException($"Unknown format code '{Type}' for object of type 'int'");
            }

            if (Precision is not null)
            {
                throw new FormatException("Precision not allowed in integer format specifier");
            }

            string signText = value < 0 ? "-" : Sign == '+' ? "+" : Sign == ' ' ? " " : string.Empty;
            return PadNumber(signText + prefix, digits, Grouping is null ? 0 : Type is 'b' or 'o' or 'x' or 'X' ? 4 : 3);
        }

        private string FormatChar(int value)
        {
            string text = char.ConvertFromUtf32(value);
            return Pad(string.Empty, text, Fill ?? (ZeroPad ? '0' : ' '), Align ?? (ZeroPad ? '=' : '>'));
        }

        private string FormatFloat(long value)
        {
            int precision = Precision ?? 6;
            double number = Type == '%' ? value * 100.0 : value;
            string digits = Type switch
            {
                'e' or 'E' => FormatExponent(Math.Abs(number), precision, Type == 'E'),
                _ => Math.Abs(number).ToString("F" + precision.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
            };
            if (Alternate && precision == 0)
            {
                digits = Type is 'e' or 'E' ? digits.Insert(1, ".") : digits + ".";
            }

            string suffix = Type == '%' ? "%" : string.Empty;
            string signText = number < 0 ? "-" : Sign == '+' ? "+" : Sign == ' ' ? " " : string.Empty;
            if (Grouping is not null && Type is 'e' or 'E')
            {
                return PadNumber(signText, digits + suffix, 0);
            }

            int dot = digits.IndexOf('.', StringComparison.Ordinal);
            string whole = dot < 0 ? digits : digits[..dot];
            string fraction = dot < 0 ? string.Empty : digits[dot..];
            return PadNumber(signText, whole, Grouping is null ? 0 : 3, fraction + suffix);
        }

        private static string FormatExponent(double value, int precision, bool upper)
        {
            string text = value.ToString((upper ? "E" : "e") + precision.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            // .NET writes e+006; Python writes e+06.
            int e = text.IndexOfAny(['e', 'E']);
            int exponent = int.Parse(text.AsSpan(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            return text[..(e + 1)] + (exponent < 0 ? "-" : "+") + Math.Abs(exponent).ToString("00", CultureInfo.InvariantCulture);
        }

        // Numbers align right by default; '=' (or a leading '0' without an explicit alignment) pads between the
        // sign/prefix and the digits, and zero padding is grouped like the digits.
        private string PadNumber(string head, string digits, int groupSize, string tail = "")
        {
            char align = Align ?? (ZeroPad ? '=' : '>');
            char fill = Fill ?? (ZeroPad ? '0' : ' ');
            if (align == '=' && fill == '0' && Fill is null && groupSize > 0)
            {
                string grouped = Group(digits, groupSize);
                while (head.Length + grouped.Length + tail.Length < Width)
                {
                    digits = "0" + digits;
                    grouped = Group(digits, groupSize);
                }

                return head + grouped + tail;
            }

            string body = (groupSize > 0 ? Group(digits, groupSize) : digits) + tail;
            return Pad(head, body, fill, align);
        }

        private string Group(string digits, int size)
        {
            StringBuilder builder = new();
            for (int k = 0; k < digits.Length; k++)
            {
                if (k > 0 && (digits.Length - k) % size == 0)
                {
                    builder.Append(Grouping);
                }

                builder.Append(digits[k]);
            }

            return builder.ToString();
        }

        private string Pad(string head, string body, char fill, char align)
        {
            int padding = Width - head.Length - body.Length;
            if (padding <= 0)
            {
                return head + body;
            }

            return align switch
            {
                '<' => head + body + new string(fill, padding),
                '^' => new string(fill, padding / 2) + head + body + new string(fill, padding - (padding / 2)),
                '=' => head + new string(fill, padding) + body,
                _ => new string(fill, padding) + head + body,
            };
        }

        private static int? ReadNumber(string spec, ref int i)
        {
            int start = i;
            while (i < spec.Length && char.IsAsciiDigit(spec[i]))
            {
                i++;
            }

            return i == start ? null : int.Parse(spec.AsSpan(start, i - start), CultureInfo.InvariantCulture);
        }
    }
}
