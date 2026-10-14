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

    /// <summary>
    /// Positional read that fills <paramref name="buffer"/> unless the file ends first; safe to call from several
    /// threads on one handle, since no file position is shared.
    /// </summary>
    /// <param name="handle">Readable file handle.</param>
    /// <param name="buffer">Destination.</param>
    /// <param name="offset">Absolute start offset.</param>
    /// <returns>Bytes read; fewer than the buffer length only at the end of the file.</returns>
    public static int ReadAt(Microsoft.Win32.SafeHandles.SafeFileHandle handle, Span<byte> buffer, long offset)
    {
        int got = 0;
        while (got < buffer.Length)
        {
            int read = RandomAccess.Read(handle, buffer[got..], offset + got);
            if (read == 0)
            {
                break;
            }

            got += read;
        }

        return got;
    }
}
