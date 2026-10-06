using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MkPFS.Core.SELF;

/// <summary>A SELF segment-table entry.</summary>
/// <param name="Flags">Flags word.</param>
/// <param name="FileOffset">Data offset in the SELF.</param>
/// <param name="FileSize">Stored size.</param>
/// <param name="MemSize">Memory size.</param>
public sealed record SELFSegment(ulong Flags, ulong FileOffset, ulong FileSize, ulong MemSize)
{
    /// <summary>Segment id (program-header index for data segments).</summary>
    public int Id => (int)((Flags >> 20) & 0xFFFF);

    /// <summary>Encrypted data.</summary>
    public bool Encrypted => (Flags & 0x2) != 0;
}

/// <summary>A parsed SELF container.</summary>
/// <param name="HeaderSize">Header region size.</param>
/// <param name="MetaSize">Metadata footer size.</param>
/// <param name="FileSize">File size recorded in the header.</param>
/// <param name="Segments">Segment table.</param>
/// <param name="AuthorityId">Program authority id from the extended info, or null.</param>
/// <param name="ElfDigest">SHA-256 of the embedded ELF from the extended info, or null.</param>
public sealed record SELFImage(int HeaderSize, int MetaSize, ulong FileSize, IReadOnlyList<SELFSegment> Segments, ulong? AuthorityId, byte[]? ElfDigest);

/// <summary>Options for <see cref="SELFFile.MakeFake"/>.</summary>
public sealed class FakeSELFOptions
{
    /// <summary>Application version in the extended info.</summary>
    public ulong AppVersion { get; init; }

    /// <summary>Firmware version in the extended info.</summary>
    public ulong FirmwareVersion { get; init; }

    /// <summary>Authority id; null writes <see cref="SELFFile.FakeExecutableAuthorityId"/> or
    /// <see cref="SELFFile.FakeLibraryAuthorityId"/> by ELF type.</summary>
    public ulong? AuthorityId { get; init; }

    /// <summary>Apply <see cref="ELFHeader.NormalizeForModule"/> to a copy first (default true).</summary>
    public bool NormalizeHeader { get; init; } = true;
}

/// <summary>
/// SELF (signed ELF) container: reader and debug fake-SELF writer. The writer produces PS5PkgTool's fake SELF
/// byte for byte (<c>tools/oracle-ppt</c>); it started as a port of LibProsperoPkg <c>ProsperoFself</c> (748eabf),
/// which differs only in the header words, the authority and the metadata size.
/// </summary>
/// <remarks>
/// Layout: 0x20-byte container header (magic <c>0xEEF51454</c>, version 0x10, mode 1, endian 1, attributes 0x12,
/// key type 0x10000101, flags 0x32), 0x20-byte segment entries (a zero digest segment then the data segment for
/// each selected program header), the ELF header and program headers, 0x40 bytes of extended info (authority
/// id, program type 1, versions, SHA-256 of the ELF), a 0x30-byte control region, the metadata (0x50 per segment
/// entry, then 0x30 bytes, a <c>00 00 01 00</c> marker and 0x23C zero bytes), then the segment data padded to 16.
/// </remarks>
public static class SELFFile
{
    /// <summary>Container magic.</summary>
    public const uint Magic = 0xEEF51454;

    /// <summary>Authority id of a fake executable.</summary>
    public const ulong FakeExecutableAuthorityId = 0x3100000000000001;

    /// <summary>Authority id of a fake dynamic library (ELF type 0xFE18).</summary>
    public const ulong FakeLibraryAuthorityId = 0x3100000000000002;

    /// <summary>Authority prefix of a fake SELF.</summary>
    public const ulong FakeAuthorityPrefix = 0x3100000000000000;

    /// <summary>Authority prefix of a genuine (Sony-signed) SELF.</summary>
    public const ulong GenuineAuthorityPrefix = 0x4500000000000000;

    private const int ContainerHeaderSize = 0x20;
    private const int SegmentEntrySize = 0x20;
    private const int ExtInfoSize = 0x40;
    private const int ControlRegionSize = 0x30;
    private const int MetaEntrySize = 0x50;
    private const int MetaMarkerOffset = 0x30;
    private const int MetaTailSize = 0x240;
    private const int DigestSize = 0x20;
    private const int DigestPageSize = 0x4000;
    private const uint ContainerKeyType = 0x10000101;
    private const ushort ElfTypeSceDynamic = 0xFE18;

    // Program headers that become content segments.
    private const uint PtLoad = 0x00000001;
    private const uint PtModuleData = 0x61000000;
    private const uint PtRelro = 0x61000010;
    private const uint PtComment = 0x6FFFFF00;

    /// <summary>Whether <paramref name="data"/> starts with a SELF header.</summary>
    /// <param name="data">File start.</param>
    /// <returns>True for a SELF.</returns>
    public static bool IsSELF(ReadOnlySpan<byte> data) =>
        data.Length >= ContainerHeaderSize && BinaryPrimitives.ReadUInt32LittleEndian(data) == Magic;

    /// <summary>Parse a SELF container.</summary>
    /// <param name="data">SELF bytes (at least the header region).</param>
    /// <returns>Parsed image.</returns>
    /// <exception cref="InvalidDataException">Not a structurally valid SELF.</exception>
    public static SELFImage Parse(ReadOnlySpan<byte> data)
    {
        if (!IsSELF(data))
        {
            throw new InvalidDataException("not a SELF container");
        }

        int headerSize = BinaryPrimitives.ReadUInt16LittleEndian(data[0x0C..]);
        int metaSize = BinaryPrimitives.ReadUInt16LittleEndian(data[0x0E..]);
        ulong fileSize = BinaryPrimitives.ReadUInt64LittleEndian(data[0x10..]);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(data[0x18..]);
        if (ContainerHeaderSize + ((long)count * SegmentEntrySize) > data.Length || headerSize > data.Length)
        {
            throw new InvalidDataException("SELF segment table or header overruns the data");
        }

        List<SELFSegment> segments = [];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> e = data[(ContainerHeaderSize + (i * SegmentEntrySize))..];
            segments.Add(new SELFSegment(
                BinaryPrimitives.ReadUInt64LittleEndian(e),
                BinaryPrimitives.ReadUInt64LittleEndian(e[0x08..]),
                BinaryPrimitives.ReadUInt64LittleEndian(e[0x10..]),
                BinaryPrimitives.ReadUInt64LittleEndian(e[0x18..])));
        }

        // The extended info follows the embedded ELF header and program headers, 16-byte aligned.
        int elfStart = ContainerHeaderSize + (count * SegmentEntrySize);
        ulong? authority = null;
        byte[]? digest = null;
        if (ELFHeader.IsELF(data[elfStart..]))
        {
            int phnum = BinaryPrimitives.ReadUInt16LittleEndian(data[(elfStart + 0x38)..]);
            int ext = AlignUp(elfStart + ELFHeader.Size + (phnum * ELFHeader.ProgramHeaderSize), 0x10);
            if (ext + ExtInfoSize <= headerSize)
            {
                authority = BinaryPrimitives.ReadUInt64LittleEndian(data[ext..]);
                digest = data.Slice(ext + 0x20, 0x20).ToArray();
            }
        }

        return new SELFImage(headerSize, metaSize, fileSize, segments, authority, digest);
    }

    // One zero 0x20-byte digest per 16 KiB page of the data segment (PS5PkgTool output).
    private static int DigestSegmentSize(int dataSize) => DigestSize * Math.Max(1, (dataSize + DigestPageSize - 1) / DigestPageSize);

    /// <summary>Build a debug fake SELF from a 64-bit ELF module.</summary>
    /// <param name="elf">ELF bytes (not modified).</param>
    /// <param name="options">Options, or null for defaults.</param>
    /// <returns>Fake SELF bytes.</returns>
    /// <exception cref="ArgumentException">The ELF cannot be wrapped.</exception>
    public static byte[] MakeFake(ReadOnlySpan<byte> elf, FakeSELFOptions? options = null)
    {
        options ??= new FakeSELFOptions();
        if (!ELFHeader.Is64LittleEndian(elf))
        {
            throw new ArgumentException("only 64-bit little-endian ELF modules can be fake-signed", nameof(elf));
        }

        // The container embeds and digests the normalized copy; the caller's bytes stay unchanged.
        byte[] module = elf.ToArray();
        if (options.NormalizeHeader)
        {
            ELFHeader.NormalizeForModule(module);
        }

        int phoff = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(module.AsSpan(0x20)));
        int phentsize = BinaryPrimitives.ReadUInt16LittleEndian(module.AsSpan(0x36));
        int phnum = BinaryPrimitives.ReadUInt16LittleEndian(module.AsSpan(0x38));
        if (phentsize != ELFHeader.ProgramHeaderSize)
        {
            throw new ArgumentException($"unexpected ELF program-header size {phentsize}", nameof(elf));
        }

        if (phoff != ELFHeader.Size)
        {
            throw new ArgumentException($"the program-header table must follow the ELF header (e_phoff is 0x{phoff:X})", nameof(elf));
        }

        if (phoff + ((long)phnum * ELFHeader.ProgramHeaderSize) > module.Length)
        {
            throw new ArgumentException("ELF program headers overrun the file", nameof(elf));
        }

        List<(int Index, int Offset, int Size)> selected = SelectSegments(module, phnum);
        if (selected.Count == 0)
        {
            throw new ArgumentException("the ELF has no loadable segment content", nameof(elf));
        }

        int segCount = selected.Count * 2;
        int afterSegments = ContainerHeaderSize + (segCount * SegmentEntrySize);
        int elfHeaderLength = ELFHeader.Size + (phnum * ELFHeader.ProgramHeaderSize);
        int extInfo = AlignUp(afterSegments + elfHeaderLength, 0x10);
        int headerSize = extInfo + ExtInfoSize + ControlRegionSize;
        int metaSize = (segCount * MetaEntrySize) + MetaMarkerOffset + MetaTailSize;
        if (headerSize > ushort.MaxValue || metaSize > ushort.MaxValue)
        {
            throw new ArgumentException($"too many segments to fake-sign (header 0x{headerSize:X}, meta 0x{metaSize:X})", nameof(elf));
        }

        // Each pair: a 0x20-byte digest segment, then the data padded to 16 bytes.
        int[] offsets = new int[segCount];
        int cursor = headerSize + metaSize;
        for (int k = 0; k < selected.Count; k++)
        {
            offsets[k * 2] = cursor;
            cursor += DigestSegmentSize(selected[k].Size);
            offsets[(k * 2) + 1] = cursor;
            cursor = AlignUp(checked(cursor + selected[k].Size), 0x10);
        }

        byte[] output = new byte[cursor];
        Span<byte> span = output;
        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        span[0x04] = 0x10; // version
        span[0x05] = 1;    // mode
        span[0x06] = 1;    // endian
        span[0x07] = 0x12; // attributes
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x08..], ContainerKeyType);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x0C..], (ushort)headerSize);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x0E..], (ushort)metaSize);
        BinaryPrimitives.WriteUInt64LittleEndian(span[0x10..], (ulong)cursor);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x18..], (ushort)segCount);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x1A..], 0x0032);

        for (int k = 0; k < selected.Count; k++)
        {
            int digestEntry = ContainerHeaderSize + (k * 2 * SegmentEntrySize);
            ulong digestFlags = ((ulong)((k * 2) + 1) << 20) | 0x10004;
            WriteSegment(span[digestEntry..], digestFlags, (ulong)offsets[k * 2], (ulong)DigestSegmentSize(selected[k].Size));
            ulong dataFlags = ((ulong)selected[k].Index << 20) | 0x2804;
            WriteSegment(span[(digestEntry + SegmentEntrySize)..], dataFlags, (ulong)offsets[(k * 2) + 1], (ulong)selected[k].Size);
        }

        module.AsSpan(0, elfHeaderLength).CopyTo(span[afterSegments..]);
        bool library = BinaryPrimitives.ReadUInt16LittleEndian(module.AsSpan(0x10)) == ElfTypeSceDynamic;
        BinaryPrimitives.WriteUInt64LittleEndian(span[extInfo..], options.AuthorityId ?? (library ? FakeLibraryAuthorityId : FakeExecutableAuthorityId));
        BinaryPrimitives.WriteUInt64LittleEndian(span[(extInfo + 0x08)..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(span[(extInfo + 0x10)..], options.AppVersion);
        BinaryPrimitives.WriteUInt64LittleEndian(span[(extInfo + 0x18)..], options.FirmwareVersion);
        SHA256.HashData(module, span[(extInfo + 0x20)..]);
        BinaryPrimitives.WriteUInt64LittleEndian(span[(extInfo + ExtInfoSize)..], 3); // control block type
        BinaryPrimitives.WriteUInt32LittleEndian(span[(headerSize + (segCount * MetaEntrySize) + MetaMarkerOffset)..], 0x00010000);

        for (int k = 0; k < selected.Count; k++)
        {
            module.AsSpan(selected[k].Offset, selected[k].Size).CopyTo(span[offsets[(k * 2) + 1]..]);
        }

        return output;
    }

    private static List<(int Index, int Offset, int Size)> SelectSegments(byte[] elf, int phnum)
    {
        List<(int Index, int Offset, int Size)> result = [];
        for (int i = 0; i < phnum; i++)
        {
            ReadOnlySpan<byte> p = elf.AsSpan(ELFHeader.Size + (i * ELFHeader.ProgramHeaderSize));
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(p);
            long offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(p[0x08..]);
            long size = (long)BinaryPrimitives.ReadUInt64LittleEndian(p[0x20..]);
            if (size > 0 && offset + size <= elf.Length && type is PtLoad or PtModuleData or PtRelro or PtComment)
            {
                result.Add((i, (int)offset, (int)size));
            }
        }

        return result;
    }

    private static void WriteSegment(Span<byte> entry, ulong flags, ulong offset, ulong size)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(entry, flags);
        BinaryPrimitives.WriteUInt64LittleEndian(entry[0x08..], offset);
        BinaryPrimitives.WriteUInt64LittleEndian(entry[0x10..], size);
        BinaryPrimitives.WriteUInt64LittleEndian(entry[0x18..], size);
    }

    private static int AlignUp(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);
}
