using System.Runtime.InteropServices;

namespace MkPFS.Core.Compression;

/// <summary>P/Invoke surface of the native <c>mkpfs_zlib</c> shim (see <c>native/mkpfs_zlib.c</c>).</summary>
internal static unsafe partial class ZlibNative
{
    private const string Library = "mkpfs_zlib";

    internal const int ZOk = 0;
    internal const int ZStreamError = -2;
    internal const int ZDataError = -3;
    internal const int ZMemError = -4;
    internal const int ZBufError = -5;

    [LibraryImport(Library, EntryPoint = "mkpfs_zlib_version")]
    internal static partial byte* Version();

    [LibraryImport(Library, EntryPoint = "mkpfs_compress_bound")]
    internal static partial uint CompressBound(uint sourceLength);

    [LibraryImport(Library, EntryPoint = "mkpfs_deflater_create")]
    internal static partial nint DeflaterCreate(int level);

    [LibraryImport(Library, EntryPoint = "mkpfs_deflater_compress")]
    internal static partial int DeflaterCompress(
        DeflaterHandle handle,
        byte* source,
        uint sourceLength,
        byte* destination,
        uint destinationCapacity,
        out uint written);

    [LibraryImport(Library, EntryPoint = "mkpfs_deflater_free")]
    internal static partial void DeflaterFree(nint handle);

    [LibraryImport(Library, EntryPoint = "mkpfs_inflate")]
    internal static partial int Inflate(
        byte* source,
        uint sourceLength,
        byte* destination,
        uint destinationCapacity,
        out uint written,
        out uint consumed);
}

/// <summary>Owns a native deflate stream created by <c>mkpfs_deflater_create</c>.</summary>
internal sealed class DeflaterHandle : SafeHandle
{
    public DeflaterHandle()
        : base(invalidHandleValue: 0, ownsHandle: true)
    {
    }

    public override bool IsInvalid => handle == 0;

    protected override bool ReleaseHandle()
    {
        ZlibNative.DeflaterFree(handle);
        return true;
    }
}
