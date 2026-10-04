using System.Runtime.InteropServices;

namespace MkPFS.Core.Compression;

/// <summary>P/Invoke surface of the native <c>mkpfs_lz4_*</c> shim (see <c>native/mkpfs_lz4.c</c>).</summary>
internal static unsafe partial class LZ4Native
{
    private const string Library = "mkpfs_zlib";

    [LibraryImport(Library, EntryPoint = "mkpfs_lz4_version")]
    internal static partial byte* Version();

    [LibraryImport(Library, EntryPoint = "mkpfs_lz4_bound")]
    internal static partial int Bound(int sourceLength);

    [LibraryImport(Library, EntryPoint = "mkpfs_lz4_encoder_create")]
    internal static partial nint EncoderCreate();

    [LibraryImport(Library, EntryPoint = "mkpfs_lz4_encoder_free")]
    internal static partial void EncoderFree(nint handle);

    [LibraryImport(Library, EntryPoint = "mkpfs_lz4_compress_fast")]
    internal static partial int CompressFast(
        LZ4EncoderHandle handle,
        byte* source,
        int sourceLength,
        byte* destination,
        int destinationCapacity,
        int acceleration);

    [LibraryImport(Library, EntryPoint = "mkpfs_lz4_compress_hc")]
    internal static partial int CompressHC(
        LZ4EncoderHandle handle,
        byte* source,
        int sourceLength,
        byte* destination,
        int destinationCapacity,
        int level);

    [LibraryImport(Library, EntryPoint = "mkpfs_lz4_decompress_safe")]
    internal static partial int DecompressSafe(byte* source, int sourceLength, byte* destination, int destinationCapacity);
}

/// <summary>Owns a native encoder created by <c>mkpfs_lz4_encoder_create</c>.</summary>
internal sealed class LZ4EncoderHandle : SafeHandle
{
    public LZ4EncoderHandle()
        : base(invalidHandleValue: 0, ownsHandle: true)
    {
    }

    public override bool IsInvalid => handle == 0;

    protected override bool ReleaseHandle()
    {
        LZ4Native.EncoderFree(handle);
        return true;
    }
}
