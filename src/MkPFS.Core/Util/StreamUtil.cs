namespace MkPFS.Core.Util;

/// <summary>Stream helpers (port of Python <c>utils._read_exact</c>).</summary>
public static class StreamUtil
{
    /// <summary>Read exactly <paramref name="size"/> bytes starting at <paramref name="offset"/>.</summary>
    /// <param name="stream">Seekable readable stream.</param>
    /// <param name="offset">Absolute start offset.</param>
    /// <param name="size">Number of bytes to read.</param>
    /// <returns>The requested bytes.</returns>
    /// <exception cref="InvalidDataException">Fewer than <paramref name="size"/> bytes are available.</exception>
    public static byte[] ReadExact(Stream stream, long offset, int size)
    {
        byte[] buffer = new byte[size];
        stream.Seek(offset, SeekOrigin.Begin);
        int got = stream.ReadAtLeast(buffer, size, throwOnEndOfStream: false);
        if (got != size)
        {
            throw new InvalidDataException($"truncated read at offset {offset} (wanted {size}, got {got})");
        }

        return buffer;
    }
}
