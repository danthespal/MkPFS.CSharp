using System.Buffers.Binary;
using System.Text;

namespace MkPFS.Core.PKG;

/// <summary>PS5 metadata container header (<c>\x7FCNT</c>, big-endian).</summary>
public sealed class CNTHeader
{
    /// <summary>Header region size.</summary>
    public const int Size = 0x1000;

    /// <summary>Size of one entry-table record.</summary>
    public const int EntrySize = 0x20;

    private static ReadOnlySpan<byte> Magic => [0x7F, (byte)'C', (byte)'N', (byte)'T'];

    /// <summary>Flags at 0x04.</summary>
    public required uint Flags { get; init; }

    /// <summary>Entry count (0x10).</summary>
    public required int EntryCount { get; init; }

    /// <summary>System-container entry count (0x14).</summary>
    public required ushort SCEntryCount { get; init; }

    /// <summary>Entry-table offset (0x18).</summary>
    public required uint EntryTableOffset { get; init; }

    /// <summary>Body offset (0x20).</summary>
    public required long BodyOffset { get; init; }

    /// <summary>Body size (0x28).</summary>
    public required long BodySize { get; init; }

    /// <summary>Content id (0x40, 36 characters).</summary>
    public required string ContentId { get; init; }

    /// <summary>DRM type (0x70).</summary>
    public required uint DrmType { get; init; }

    /// <summary>Content type (0x74): 0x20 application, 0x21/0x22 additional content.</summary>
    public required uint ContentType { get; init; }

    /// <summary>Content flags (0x78).</summary>
    public required uint ContentFlags { get; init; }

    /// <summary>Whether <paramref name="data"/> starts with the CNT magic.</summary>
    /// <param name="data">Container start.</param>
    /// <returns>True for a CNT header.</returns>
    public static bool IsCNT(ReadOnlySpan<byte> data) => data.Length >= 4 && data[..4].SequenceEqual(Magic);

    /// <summary>Parse the header.</summary>
    /// <param name="data">At least 0x80 bytes of the container.</param>
    /// <returns>Header.</returns>
    /// <exception cref="InvalidDataException">Not a CNT container.</exception>
    public static CNTHeader Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x80 || !IsCNT(data))
        {
            throw new InvalidDataException("not a PS5 metadata container (missing \\x7FCNT magic)");
        }

        ReadOnlySpan<byte> id = data.Slice(0x40, 0x30);
        int end = id.IndexOf((byte)0);
        return new CNTHeader
        {
            Flags = BinaryPrimitives.ReadUInt32BigEndian(data[0x04..]),
            EntryCount = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[0x10..])),
            SCEntryCount = BinaryPrimitives.ReadUInt16BigEndian(data[0x14..]),
            EntryTableOffset = BinaryPrimitives.ReadUInt32BigEndian(data[0x18..]),
            BodyOffset = checked((long)BinaryPrimitives.ReadUInt64BigEndian(data[0x20..])),
            BodySize = checked((long)BinaryPrimitives.ReadUInt64BigEndian(data[0x28..])),
            ContentId = Encoding.ASCII.GetString(end < 0 ? id : id[..end]),
            DrmType = BinaryPrimitives.ReadUInt32BigEndian(data[0x70..]),
            ContentType = BinaryPrimitives.ReadUInt32BigEndian(data[0x74..]),
            ContentFlags = BinaryPrimitives.ReadUInt32BigEndian(data[0x78..]),
        };
    }
}

/// <summary>One CNT entry-table record (big-endian, 0x20 bytes).</summary>
/// <param name="Id">Entry id (for example 0x2000 = param.json).</param>
/// <param name="NameOffset">Offset in the entry-name table (0 = unnamed).</param>
/// <param name="Flags1">Flags word 1; bit 31 marks an encrypted entry.</param>
/// <param name="Flags2">Flags word 2.</param>
/// <param name="DataOffset">Payload offset from the CNT start.</param>
/// <param name="DataSize">Payload size.</param>
public sealed record CNTEntry(uint Id, uint NameOffset, uint Flags1, uint Flags2, uint DataOffset, uint DataSize)
{
    /// <summary>Entry-name table id.</summary>
    public const uint EntryNamesId = 0x0200;

    /// <summary>Resolved name, or empty for an unnamed entry.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Encrypted payload.</summary>
    public bool IsEncrypted => (Flags1 & 0x8000_0000u) != 0;

    /// <summary>Parse one record.</summary>
    /// <param name="data">0x20 bytes.</param>
    /// <returns>Entry.</returns>
    public static CNTEntry Parse(ReadOnlySpan<byte> data) => new(
        BinaryPrimitives.ReadUInt32BigEndian(data),
        BinaryPrimitives.ReadUInt32BigEndian(data[0x04..]),
        BinaryPrimitives.ReadUInt32BigEndian(data[0x08..]),
        BinaryPrimitives.ReadUInt32BigEndian(data[0x0C..]),
        BinaryPrimitives.ReadUInt32BigEndian(data[0x10..]),
        BinaryPrimitives.ReadUInt32BigEndian(data[0x14..]));
}
