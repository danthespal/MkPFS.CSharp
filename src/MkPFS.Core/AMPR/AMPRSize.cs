using System.Globalization;
using MkPFS.Core.Util;

namespace MkPFS.Core.AMPR;

/// <summary>Size strings such as <c>64KiB</c> or <c>1.5MB</c> (Python <c>parse_size</c>, <c>block_shift</c>).</summary>
public static class AMPRSize
{
    private static readonly Dictionary<string, long> Units = new(StringComparer.Ordinal)
    {
        ["b"] = 1,
        ["k"] = 1000,
        ["kb"] = 1000,
        ["kib"] = 1024,
        ["m"] = 1000L * 1000,
        ["mb"] = 1000L * 1000,
        ["mib"] = 1024L * 1024,
        ["g"] = 1000L * 1000 * 1000,
        ["gb"] = 1000L * 1000 * 1000,
        ["gib"] = 1024L * 1024 * 1024,
        ["t"] = 1000L * 1000 * 1000 * 1000,
        ["tb"] = 1000L * 1000 * 1000 * 1000,
        ["tib"] = 1024L * 1024 * 1024 * 1024,
    };

    /// <summary>Return a byte count; integers pass through.</summary>
    /// <param name="value">Non-negative byte count.</param>
    /// <returns><paramref name="value"/>.</returns>
    /// <exception cref="ArgumentException">The value is negative.</exception>
    public static long Parse(long value) =>
        value >= 0 ? value : throw new ArgumentException("size must be non-negative");

    /// <summary>
    /// Parse a number with an optional unit (<c>b</c>, <c>k</c>/<c>kb</c>/<c>kib</c> ... <c>tib</c>, case-insensitive;
    /// underscores ignored). Decimal units are powers of 1000; the product is truncated like Python <c>int()</c>.
    /// </summary>
    /// <param name="value">Size text.</param>
    /// <returns>Byte count.</returns>
    /// <exception cref="ArgumentException">The text is empty, malformed, negative or has an unknown unit.</exception>
    public static long Parse(string value)
    {
        string text = PythonText.Strip(value).Replace("_", string.Empty, StringComparison.Ordinal);
        if (text.Length == 0)
        {
            throw new ArgumentException("empty size");
        }

        int split = text.Length;
        while (split > 0 && char.IsLetter(text[split - 1]))
        {
            split--;
        }

        string number = text[..split];
        string suffix = split == text.Length ? "b" : text[split..].ToLowerInvariant();
        if (!Units.TryGetValue(suffix, out long unit))
        {
            throw new ArgumentException($"unknown size suffix: {suffix}");
        }

        if (!double.TryParse(PythonText.Strip(number), NumberStyles.Float, CultureInfo.InvariantCulture, out double numeric)
            || !double.IsFinite(numeric))
        {
            throw new ArgumentException($"invalid size: {value}");
        }

        double product = Math.Truncate(numeric * unit);
        if (product < 0)
        {
            throw new ArgumentException("size must be non-negative");
        }

        return product < 9.2233720368547758E18 ? (long)product : throw new ArgumentException($"invalid size: {value}");
    }

    /// <summary>Return log2 of a power-of-two block size between 16 KiB and 1 MiB.</summary>
    /// <param name="size">Block size in bytes.</param>
    /// <returns>Shift 14..20.</returns>
    /// <exception cref="ArgumentException">The size is not a power of two or is out of range.</exception>
    public static int BlockShift(long size)
    {
        size = Parse(size);
        if (size == 0 || (size & (size - 1)) != 0)
        {
            throw new ArgumentException($"block size must be a power of two, got {size}");
        }

        int shift = 63 - (int)long.LeadingZeroCount(size);
        if (shift < AMPRPackFormat.MinBlockShift || shift > AMPRPackFormat.MaxBlockShift)
        {
            throw new ArgumentException(
                $"block size must be between {1 << AMPRPackFormat.MinBlockShift} and {1 << AMPRPackFormat.MaxBlockShift} bytes");
        }

        return shift;
    }

    /// <summary>Parse a size string and return its block shift.</summary>
    /// <param name="size">Block size text.</param>
    /// <returns>Shift 14..20.</returns>
    public static int BlockShift(string size) => BlockShift(Parse(size));
}
