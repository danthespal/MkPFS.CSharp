using System.Runtime.InteropServices;

namespace MkPFS.Core.Compression;

/// <summary>
/// zlib 1.3.1 helpers backed by the bundled native library. Output is byte-identical to
/// Python <c>zlib.compress(data, level)</c> and PS5 Game Compressor for the same input.
/// </summary>
public static class Zlib
{
    /// <summary>Default PFSC compression level (same as MkPFS and Game Compressor).</summary>
    public const int DefaultLevel = 7;

    /// <summary>zlib version string reported by the native library (expected <c>1.3.1</c>).</summary>
    public static unsafe string NativeVersion =>
        Marshal.PtrToStringAnsi((nint)ZlibNative.Version()) ?? string.Empty;

    /// <summary>Return the worst-case compressed size for <paramref name="sourceLength"/> bytes.</summary>
    /// <param name="sourceLength">Uncompressed length.</param>
    /// <returns>Upper bound of the zlib stream length.</returns>
    public static int CompressBound(int sourceLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceLength);
        return checked((int)ZlibNative.CompressBound((uint)sourceLength));
    }

    /// <summary>Compress <paramref name="source"/> into a new zlib stream.</summary>
    /// <param name="source">Bytes to compress.</param>
    /// <param name="level">zlib level 0..9.</param>
    /// <returns>The complete zlib stream.</returns>
    public static byte[] Compress(ReadOnlySpan<byte> source, int level = DefaultLevel)
    {
        using ZlibDeflater deflater = new(level);
        return deflater.Compress(source);
    }

    /// <summary>Decompress one complete zlib stream.</summary>
    /// <param name="source">zlib stream.</param>
    /// <param name="destination">Output buffer; must hold the whole decompressed payload.</param>
    /// <returns>Number of bytes written to <paramref name="destination"/>.</returns>
    /// <exception cref="InvalidDataException">The stream is corrupt, truncated, or does not fit.</exception>
    public static unsafe int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int rc;
        uint written;
        fixed (byte* src = source)
        fixed (byte* dst = destination)
        {
            rc = ZlibNative.Inflate(src, (uint)source.Length, dst, (uint)destination.Length, out written, out _);
        }

        return rc switch
        {
            ZlibNative.ZOk => (int)written,
            // uncompress2 reports Z_BUF_ERROR only for a full output buffer; truncation is Z_DATA_ERROR.
            ZlibNative.ZBufError => throw new InvalidDataException($"zlib stream does not fit in {destination.Length} bytes"),
            ZlibNative.ZMemError => throw new OutOfMemoryException("zlib inflate ran out of memory"),
            _ => throw new InvalidDataException($"zlib stream is corrupt or truncated (code {rc})"),
        };
    }
}

/// <summary>
/// Reusable deflate stream: each call produces an independent zlib stream (deflateReset per
/// call, like Game Compressor's workers). Not thread-safe; use one instance per worker.
/// </summary>
public sealed class ZlibDeflater : IDisposable
{
    private readonly DeflaterHandle _handle;

    /// <summary>Create a deflater with window 15, memLevel 8 and the default strategy.</summary>
    /// <param name="level">zlib level 0..9.</param>
    public ZlibDeflater(int level = Zlib.DefaultLevel)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 9);
        Level = level;
        _handle = new DeflaterHandle();
        nint raw = ZlibNative.DeflaterCreate(level);
        if (raw == 0)
        {
            throw new OutOfMemoryException("zlib deflateInit failed");
        }

        Marshal.InitHandle(_handle, raw);
    }

    /// <summary>Compression level this deflater was created with.</summary>
    public int Level { get; }

    /// <summary>Compress into <paramref name="destination"/> when the result fits.</summary>
    /// <param name="source">Bytes to compress.</param>
    /// <param name="destination">Output buffer.</param>
    /// <param name="written">Compressed length on success, otherwise 0.</param>
    /// <returns><see langword="false"/> when the stream does not fit in <paramref name="destination"/>.</returns>
    public unsafe bool TryCompress(ReadOnlySpan<byte> source, Span<byte> destination, out int written)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        int rc;
        uint length;
        fixed (byte* src = source)
        fixed (byte* dst = destination)
        {
            rc = ZlibNative.DeflaterCompress(_handle, src, (uint)source.Length, dst, (uint)destination.Length, out length);
        }

        switch (rc)
        {
            case ZlibNative.ZOk:
                written = (int)length;
                return true;
            case ZlibNative.ZBufError:
                written = 0;
                return false;
            default:
                throw new InvalidOperationException($"zlib deflate failed (code {rc})");
        }
    }

    /// <summary>Compress <paramref name="source"/> into a new array.</summary>
    /// <param name="source">Bytes to compress.</param>
    /// <returns>The complete zlib stream.</returns>
    public byte[] Compress(ReadOnlySpan<byte> source)
    {
        byte[] buffer = new byte[Zlib.CompressBound(source.Length)];
        if (!TryCompress(source, buffer, out int written))
        {
            throw new InvalidOperationException("zlib output exceeded compressBound");
        }

        return buffer.AsSpan(0, written).ToArray();
    }

    /// <inheritdoc />
    public void Dispose() => _handle.Dispose();
}
