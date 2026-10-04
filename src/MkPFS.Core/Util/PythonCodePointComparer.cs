using System.Text;

namespace MkPFS.Core.Util;

/// <summary>Orders strings by Unicode code point, like Python string comparison.</summary>
public sealed class PythonCodePointComparer : IComparer<string>
{
    /// <summary>Shared instance.</summary>
    public static readonly PythonCodePointComparer Instance = new();

    /// <inheritdoc />
    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
        {
            return x is null ? (y is null ? 0 : -1) : 1;
        }

        StringRuneEnumerator a = x.EnumerateRunes();
        StringRuneEnumerator b = y.EnumerateRunes();
        while (true)
        {
            bool hasA = a.MoveNext();
            bool hasB = b.MoveNext();
            if (!hasA || !hasB)
            {
                return hasA ? 1 : hasB ? -1 : 0;
            }

            int diff = a.Current.Value - b.Current.Value;
            if (diff != 0)
            {
                return diff;
            }
        }
    }
}
