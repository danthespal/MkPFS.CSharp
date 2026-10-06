using System.Buffers.Binary;

namespace MkPFS.Core.PKG;

/// <summary>
/// PS5 finalized-image header (<c>\x7FFIH</c>, little-endian, first 0x10000 bytes of a package).
/// Field meanings follow Publishing Tools output (<c>tools/oracle-fpkg</c> SDK references) and
/// LibProsperoPkg <c>ProsperoFihBuilder</c>; fields with no confirmed meaning keep their offset name.
/// </summary>
public sealed class FIHHeader
{
    /// <summary>Header region size; the outer PFS image starts here.</summary>
    public const int Size = 0x10000;

    /// <summary>Signed byte of a debug (fake) image.</summary>
    public const byte SignedByteDebug = 0x00;

    /// <summary>Signed byte of a retail (submitted) image.</summary>
    public const byte SignedByteRetail = 0x80;

    /// <summary>Format version the mount path requires.</summary>
    public const ushort RequiredFormatVersion = 3;

    private static ReadOnlySpan<byte> Magic => [0x7F, (byte)'F', (byte)'I', (byte)'H'];

    /// <summary>Byte 0x05: 0x00 debug, 0x80 retail.</summary>
    public required byte SignedByte { get; init; }

    /// <summary>Format version at 0x06 (3 on PS5).</summary>
    public required ushort FormatVersion { get; init; }

    /// <summary>Outer PFS image offset (0x10, always 0x10000).</summary>
    public required long PFSImageOffset { get; init; }

    /// <summary>Outer PFS image size (0x18).</summary>
    public required long PFSImageSize { get; init; }

    /// <summary>Absolute offset of the plaintext outer superblock (0x20).</summary>
    public required long SuperblockOffset { get; init; }

    /// <summary>Size of the superblock block (0x28).</summary>
    public required long SuperblockSize { get; init; }

    /// <summary>Game digest: SHA3-256 of the superblock block (0x30; repeated at 0x70 and 0xD0).</summary>
    public required byte[] GameDigest { get; init; }

    /// <summary>Inner-mount block index of the inner superblock (0x50).</summary>
    public required long InnerSuperblockBlock { get; init; }

    /// <summary>Embedded CNT offset (0x58).</summary>
    public required long CNTOffset { get; init; }

    /// <summary>Block size (0x60).</summary>
    public required long BlockSize { get; init; }

    /// <summary>Block count of <c>pfs_image.dat</c> in the outer image (0x90).</summary>
    public required uint InnerImageBlocks { get; init; }

    /// <summary>Field 0x94 (inner inode count on SDK output).</summary>
    public required uint Field94 { get; init; }

    /// <summary>Field 0x98 (mirrors 0x94 on SDK output).</summary>
    public required uint Field98 { get; init; }

    /// <summary>Content-version echo (0x9C).</summary>
    public required uint ContentVersion { get; init; }

    /// <summary>Size field 0xA0 (block-aligned <c>pfs_image.dat</c> size on SDK output).</summary>
    public required long FieldA0 { get; init; }

    /// <summary>Length of <c>naps_pkg_layout.dat</c> (0xA8).</summary>
    public required long NapsSize { get; init; }

    /// <summary>SHA3-256 of <c>naps_pkg_layout.dat</c> (0xB0).</summary>
    public required byte[] NapsDigest { get; init; }

    /// <summary>Field 0xF0 (non-sce_sys file count on SDK output).</summary>
    public required uint FieldF0 { get; init; }

    /// <summary>True for a debug image.</summary>
    public bool IsDebug => SignedByte == SignedByteDebug;

    /// <summary>Whether <paramref name="data"/> starts with the FIH magic.</summary>
    /// <param name="data">File start.</param>
    /// <returns>True for a finalized image.</returns>
    public static bool IsFIH(ReadOnlySpan<byte> data) => data.Length >= 4 && data[..4].SequenceEqual(Magic);

    /// <summary>Parse the header.</summary>
    /// <param name="data">At least 0x100 bytes.</param>
    /// <returns>Header.</returns>
    /// <exception cref="InvalidDataException">Not a finalized image.</exception>
    public static FIHHeader Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x100 || !IsFIH(data))
        {
            throw new InvalidDataException("not a PS5 finalized image (missing \\x7FFIH magic)");
        }

        return new FIHHeader
        {
            SignedByte = data[0x05],
            FormatVersion = BinaryPrimitives.ReadUInt16LittleEndian(data[0x06..]),
            PFSImageOffset = BinaryPrimitives.ReadInt64LittleEndian(data[0x10..]),
            PFSImageSize = BinaryPrimitives.ReadInt64LittleEndian(data[0x18..]),
            SuperblockOffset = BinaryPrimitives.ReadInt64LittleEndian(data[0x20..]),
            SuperblockSize = BinaryPrimitives.ReadInt64LittleEndian(data[0x28..]),
            GameDigest = data.Slice(0x30, 32).ToArray(),
            InnerSuperblockBlock = BinaryPrimitives.ReadInt64LittleEndian(data[0x50..]),
            CNTOffset = BinaryPrimitives.ReadInt64LittleEndian(data[0x58..]),
            BlockSize = BinaryPrimitives.ReadInt64LittleEndian(data[0x60..]),
            InnerImageBlocks = BinaryPrimitives.ReadUInt32LittleEndian(data[0x90..]),
            Field94 = BinaryPrimitives.ReadUInt32LittleEndian(data[0x94..]),
            Field98 = BinaryPrimitives.ReadUInt32LittleEndian(data[0x98..]),
            ContentVersion = BinaryPrimitives.ReadUInt32LittleEndian(data[0x9C..]),
            FieldA0 = BinaryPrimitives.ReadInt64LittleEndian(data[0xA0..]),
            NapsSize = BinaryPrimitives.ReadInt64LittleEndian(data[0xA8..]),
            NapsDigest = data.Slice(0xB0, 32).ToArray(),
            FieldF0 = BinaryPrimitives.ReadUInt32LittleEndian(data[0xF0..]),
        };
    }
}
