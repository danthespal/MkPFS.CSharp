// Ported from LibProsperoPkg (SvenGDK, GPL-3.0-or-later) PKG/ProsperoSiArchive.cs at commit 748eabf, reduced to
// the members PS5PkgTool writes (FPKG plan F7). See NOTICE.
// LibProsperoPkg - A library for building and inspecting PS5 packages.
// Copyright (C) 2026 SvenGDK
using System.Text;

namespace MkPFS.Build.FPKG.Prospero;

/// <summary>A single member (one stored file) of the SI ZIP.</summary>
/// <param name="Path">ZIP entry path with forward slashes.</param>
/// <param name="Content">Raw, already-final bytes of the member.</param>
public readonly record struct ProsperoSiMember(string Path, byte[] Content);

/// <summary>Values the SI segment is built from.</summary>
internal sealed class ProsperoSiBuildInputs
{
    /// <summary>Content id (names the playgo-chunk.crc folder).</summary>
    public required string ContentId { get; init; }

    /// <summary>Outer PFS image size (the naps_meta_18 <c>*PFSmetadata</c> pseudo-file).</summary>
    public required long PfsImageSize { get; init; }

    /// <summary>Block-aligned stored inner-image size (FIH 0xA0; naps_meta_300 = this − 0x10000).</summary>
    public required long InnerImageSize { get; init; }

    /// <summary>Inner files (path, size) in afid order.</summary>
    public required IReadOnlyList<(string Path, long Size)> InnerFiles { get; init; }

    /// <summary>Stored chunks of the inner image.</summary>
    public required IReadOnlyList<PS5InnerChunk> InnerChunks { get; init; }

    /// <summary>SHA3-256 of a stored range of the inner image (offset, length).</summary>
    public required Func<long, long, byte[]> InnerDigest { get; init; }

    /// <summary>SHA3-256 of the plaintext outer superblock.</summary>
    public required byte[] SuperblockDigest { get; init; }

    /// <summary>SHA3-256 of the finalized mount image (FIH, outer PFS, CNT).</summary>
    public required byte[] MountDigest { get; init; }

    /// <summary><c>playgo-chunk.crc</c>: CRC-32C of every 64 KiB of the mount image, little-endian.</summary>
    public required byte[] ChunkCrc { get; init; }

    /// <summary>PlayGo chunk descriptor (CNT entry 0x1001).</summary>
    public byte[]? PlayGoChunkDat { get; init; }
}

/// <summary>
/// The trailing SI segment of a debug package: a STORED ZIP with <c>common/etc/naps_meta_18.dat</c> (encrypted
/// metric blob), <c>naps_meta_300/301/302/308.dat</c> (48-byte descriptor), <c>common/etc/playgo-chunk.dat</c>
/// (CNT entry 0x1001) and <c>config/&lt;content-id&gt;/playgo-chunk.crc</c> (CRC-32C per 64 KiB of the mount
/// image), in that order; PS5PkgTool writes no pfsimage.xml.
/// </summary>
internal static class ProsperoSiArchive
{
    /// <summary>Path of the encrypted metric blob.</summary>
    public const string NapsMeta18Path = "common/etc/naps_meta_18.dat";

    /// <summary>Path of the PlayGo chunk descriptor.</summary>
    public const string PlayGoChunkDatPath = "common/etc/playgo-chunk.dat";

    /// <summary>Build the SI segment.</summary>
    /// <param name="inputs">Values captured during the build.</param>
    /// <returns>ZIP bytes.</returns>
    public static byte[] BuildDebugSiSegment(ProsperoSiBuildInputs inputs)
    {
        ulong innerSize = (ulong)inputs.InnerImageSize;
        byte[] napsMeta300 = ProsperoNapsMeta.BuildMeta300FromInnerImageSize(innerSize);

        // File table: the inner files in afid order, then a "*PFSmetadata" pseudo-entry sized to the outer PFS
        // image region (the installer reads the sum as package_size).
        List<(string Path, long Size)> contentFiles = [.. inputs.InnerFiles, ("*PFSmetadata", inputs.PfsImageSize)];
        byte[] napsMeta18 = ProsperoNapsMeta.BuildMeta18(innerSize, contentFiles, inputs.InnerChunks, inputs.InnerDigest, inputs.SuperblockDigest, inputs.MountDigest);

        List<ProsperoSiMember> members = [new(NapsMeta18Path, napsMeta18)];
        foreach (int id in ProsperoNapsMeta.Meta300Ids)
        {
            members.Add(new($"common/etc/naps_meta_{id}.dat", napsMeta300));
        }

        if (inputs.PlayGoChunkDat is not null)
        {
            members.Add(new(PlayGoChunkDatPath, inputs.PlayGoChunkDat));
        }

        members.Add(new($"config/{inputs.ContentId}/playgo-chunk.crc", inputs.ChunkCrc));
        return WriteZip(members);
    }

    /// <summary>
    /// Serialises <paramref name="members"/> into a ZIP using <see cref="CompressionLevel.NoCompression"/>
    /// (the SI uses STORED entries) and returns the raw segment bytes.
    /// </summary>
    public static byte[] WriteZip(IReadOnlyList<ProsperoSiMember> members)
    {
        ArgumentNullException.ThrowIfNull(members);

        // Custom minimal ZIP writer that produces the exact SI framing the SI walker requires (all members
        // STORED, no extra fields, central-directory "version made by" = 0, deterministic DOS date/time).
        // .NET's ZipArchive emits `version made by` = 0x0014 and its own timestamps, which diverge from
        // the required SI ZIP layout; the console's SI walker depends on these exact fields, so this writer
        // emits the layout field-for-field. Paths use forward slashes and the exact member set/order the caller gives.
        const ushort VersionNeeded = 20;   // 2.0 (STORED)
        const ushort DosTime = 0;          // deterministic; the walker does not inspect the timestamp.
        const ushort DosDate = 0x0021;     // 1980-01-01 (minimal valid DOS date).

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        var local = new (uint Crc, int Size, int NameLen, long Offset)[members.Count];

        for (int i = 0; i < members.Count; i++)
        {
            ProsperoSiMember m = members[i];
            byte[] name = Encoding.ASCII.GetBytes(m.Path);
            uint crc = ZipCrc32(m.Content);
            long off = ms.Position;
            local[i] = (crc, m.Content.Length, name.Length, off);

            w.Write((uint)0x04034b50);          // local file header signature
            w.Write(VersionNeeded);
            w.Write((ushort)0);                 // general purpose flag
            w.Write((ushort)0);                 // method = stored
            w.Write(DosTime);
            w.Write(DosDate);
            w.Write(crc);
            w.Write((uint)m.Content.Length);    // compressed size (== uncompressed for STORED)
            w.Write((uint)m.Content.Length);    // uncompressed size
            w.Write((ushort)name.Length);
            w.Write((ushort)0);                 // extra field length
            w.Write(name);
            w.Write(m.Content);
        }

        long cdStart = ms.Position;
        for (int i = 0; i < members.Count; i++)
        {
            byte[] name = Encoding.ASCII.GetBytes(members[i].Path);
            w.Write((uint)0x02014b50);          // central directory header signature
            w.Write((ushort)0);                 // version made by = 0 (required layout; .NET writes 0x0014)
            w.Write(VersionNeeded);
            w.Write((ushort)0);                 // general purpose flag
            w.Write((ushort)0);                 // method = stored
            w.Write(DosTime);
            w.Write(DosDate);
            w.Write(local[i].Crc);
            w.Write((uint)local[i].Size);       // compressed size
            w.Write((uint)local[i].Size);       // uncompressed size
            w.Write((ushort)name.Length);
            w.Write((ushort)0);                 // extra field length
            w.Write((ushort)0);                 // comment length
            w.Write((ushort)0);                 // disk number start
            w.Write((ushort)0);                 // internal attributes
            w.Write((uint)0);                   // external attributes
            w.Write((uint)local[i].Offset);     // relative offset of local header
            w.Write(name);
        }
        long cdSize = ms.Position - cdStart;

        w.Write((uint)0x06054b50);              // end of central directory signature
        w.Write((ushort)0);                     // disk number
        w.Write((ushort)0);                     // disk with central directory
        w.Write((ushort)members.Count);         // entries on this disk
        w.Write((ushort)members.Count);         // total entries
        w.Write((uint)cdSize);
        w.Write((uint)cdStart);
        w.Write((ushort)0);                     // comment length
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Standard ZIP CRC-32 (reflected polynomial 0xEDB88320) over <paramref name="data"/>.</summary>
    private static uint ZipCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)(-(int)(crc & 1)));
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
