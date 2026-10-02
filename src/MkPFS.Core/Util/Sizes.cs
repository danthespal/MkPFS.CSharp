using System.Globalization;

namespace MkPFS.Core.Util;

/// <summary>Integer and size helpers (port of Python <c>mkpfs/utils.py</c>).</summary>
public static class Sizes
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>Format a byte count with binary prefixes, e.g. <c>3.62 MB</c> (Python <c>human_readable_size</c>).</summary>
    /// <param name="size">Number of bytes.</param>
    /// <returns>Formatted size with two decimals.</returns>
    public static string HumanReadable(long size)
    {
        double value = size;
        foreach (string unit in Units)
        {
            if (value < 1024.0)
            {
                return string.Create(CultureInfo.InvariantCulture, $"{value:F2} {unit}");
            }

            value /= 1024.0;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:F2} PB");
    }

    /// <summary>Ceiling of <paramref name="a"/> / <paramref name="b"/> for non-negative <paramref name="a"/>.</summary>
    /// <param name="a">Numerator.</param>
    /// <param name="b">Positive denominator.</param>
    /// <returns>Smallest integer ≥ a / b.</returns>
    public static long CeilDiv(long a, long b)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(b);
        return checked((a + b - 1) / b);
    }

    /// <summary>Return whether <paramref name="value"/> is a positive power of two.</summary>
    /// <param name="value">Value to test.</param>
    /// <returns><see langword="true"/> for 1, 2, 4, 8, ...</returns>
    public static bool IsPowerOfTwo(long value) => value > 0 && (value & (value - 1)) == 0;
}
