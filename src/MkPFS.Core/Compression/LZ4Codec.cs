using System.Runtime.InteropServices;

namespace MkPFS.Core.Compression;

/// <summary>
/// Raw LZ4 block helpers (no frame, no size prefix) backed by the bundled lz4 1.9.4. Output is
/// byte-identical to ampr_emu <c>ampr_pack_format.Lz4Codec</c> with python-lz4 4.4.5.
/// </summary>
public static class LZ4Codec
{
    /// <summary>Highest HC level; ampr_pack's default.</summary>
    public const int MaxHCLevel = 12;

    /// <summary>lz4 version string reported by the native library (expected <c>1.9.4</c>).</summary>
    public static unsafe string NativeVersion =>
        Marshal.PtrToStringAnsi((nint)LZ4Native.Version()) ?? string.Empty;

    /// <summary>Return the worst-case block size for <paramref name="sourceLength"/> bytes.</summary>
    /// <param name="sourceLength">Uncompressed length.</param>
    /// <returns>Upper bound of the raw block length.</returns>
    public static int CompressBound(int sourceLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceLength);
        int bound = LZ4Native.Bound(sourceLength);
        return bound > 0 ? bound : throw new ArgumentOutOfRangeException(nameof(sourceLength), "input exceeds LZ4_MAX_INPUT_SIZE");
    }

    /// <summary>Decode one raw block, like the ampr_emu runtime (<c>LZ4_decompress_safe</c>).</summary>
    /// <param name="source">Raw LZ4 block.</param>
    /// <param name="destination">Output buffer.</param>
    /// <returns>Number of bytes written to <paramref name="destination"/>.</returns>
    /// <exception cref="InvalidDataException">The block is malformed or does not fit.</exception>
    public static unsafe int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int written;
        fixed (byte* src = source)
        fixed (byte* dst = destination)
        {
            written = LZ4Native.DecompressSafe(src, source.Length, dst, destination.Length);
        }

        return written >= 0
            ? written
            : throw new InvalidDataException($"LZ4 block is malformed or does not fit in {destination.Length} bytes");
    }
}

/// <summary>
/// Reusable LZ4 encoder; every call produces an independent raw block. Calls lz4 the way python-lz4
/// does (stream reset + <c>*_continue</c>), which differs from <c>LZ4_compress_fast</c>. Not
/// thread-safe; use one instance per worker.
/// </summary>
public sealed class LZ4Encoder : IDisposable
{
    private readonly LZ4EncoderHandle _handle = new();

    /// <summary>Create an encoder (about 280 KB of native state).</summary>
    public LZ4Encoder()
    {
        nint raw = LZ4Native.EncoderCreate();
        if (raw == 0)
        {
            throw new OutOfMemoryException("LZ4 encoder allocation failed");
        }

        Marshal.InitHandle(_handle, raw);
    }

    /// <summary>Compress with the fast encoder.</summary>
    /// <param name="source">Bytes to compress.</param>
    /// <param name="acceleration">Acceleration; values below 1 become 1, like ampr_pack.</param>
    /// <returns>The raw block; empty for empty input, like ampr_pack.</returns>
    public byte[] CompressFast(ReadOnlySpan<byte> source, int acceleration = 1) =>
        Compress(source, highCompression: false, Math.Max(1, acceleration));

    /// <summary>Compress with the HC encoder.</summary>
    /// <param name="source">Bytes to compress.</param>
    /// <param name="level">HC level; clamped to 1..12, like ampr_pack.</param>
    /// <returns>The raw block; empty for empty input, like ampr_pack.</returns>
    public byte[] CompressHC(ReadOnlySpan<byte> source, int level = LZ4Codec.MaxHCLevel) =>
        Compress(source, highCompression: true, Math.Clamp(level, 1, LZ4Codec.MaxHCLevel));

    private unsafe byte[] Compress(ReadOnlySpan<byte> source, bool highCompression, int parameter)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        if (source.IsEmpty)
        {
            return [];
        }

        byte[] buffer = new byte[LZ4Codec.CompressBound(source.Length)];
        int written;
        fixed (byte* src = source)
        fixed (byte* dst = buffer)
        {
            written = highCompression
                ? LZ4Native.CompressHC(_handle, src, source.Length, dst, buffer.Length, parameter)
                : LZ4Native.CompressFast(_handle, src, source.Length, dst, buffer.Length, parameter);
        }

        return written > 0
            ? buffer.AsSpan(0, written).ToArray()
            : throw new InvalidOperationException("LZ4 output exceeded LZ4_compressBound");
    }

    /// <inheritdoc />
    public void Dispose() => _handle.Dispose();
}
