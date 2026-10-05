using System.Text;

namespace MkPFS.Core.AMPR;

/// <summary>Deduplicating UTF-8 string table; each entry ends with NUL (Python <c>StringTable</c>).</summary>
public sealed class AMPRStringTable
{
    private readonly List<byte> _data = [];
    private readonly Dictionary<string, (uint Offset, uint Length)> _offsets = new(StringComparer.Ordinal);

    /// <summary>Add a string, or return the location of an identical earlier one.</summary>
    /// <param name="value">String without NUL.</param>
    /// <returns>Offset and byte length (without NUL).</returns>
    /// <exception cref="ArgumentException">The value contains NUL or the table exceeds 4 GiB.</exception>
    public (uint Offset, uint Length) Add(string value)
    {
        if (_offsets.TryGetValue(value, out (uint Offset, uint Length) prior))
        {
            return prior;
        }

        byte[] encoded = Encoding.UTF8.GetBytes(value);
        if (encoded.Contains((byte)0))
        {
            throw new ArgumentException("NUL in string table value");
        }

        long offset = _data.Count;
        if (offset + encoded.Length + 1 > 0x100000000L)
        {
            throw new ArgumentException("string table exceeds uint32 format limits");
        }

        _data.AddRange(encoded);
        _data.Add(0);
        (uint Offset, uint Length) result = ((uint)offset, (uint)encoded.Length);
        _offsets[value] = result;
        return result;
    }

    /// <summary>Return the table bytes.</summary>
    /// <returns>Table bytes.</returns>
    public byte[] ToArray() => [.. _data];
}
