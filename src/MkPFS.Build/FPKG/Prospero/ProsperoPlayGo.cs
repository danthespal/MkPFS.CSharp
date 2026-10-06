// Ported from LibProsperoPkg (SvenGDK, GPL-3.0-or-later) PlayGo/ProsperoPlayGo.cs at commit 748eabf
// for byte parity with its packages (FPKG plan F4b). See NOTICE.
using MkPFS.Core.Compression.Kraken;
// LibProsperoPkg - A library for building and inspecting PS5 packages.
// Copyright (C) 2026 SvenGDK
//
// Generators for the PS5 PlayGo / about helper files added during PKG building. These generators
// ensure the produced inner PFS carries the full expected file set.
//
// The PS5 PlayGo "chunk" file uses the 'plgx' container (version 0x1000). For the single-image / single-chunk /
// single-scenario system-application profile that these system packages use, every byte is
// constant except the 36-char content id (at 0x40) and the two manifest-chunk size words
// (at 0x148 / 0x158), which describe the chunk data layout and are supplied by the builder.

#nullable enable
using System;
using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Text;

using MkPFS.Core.PFS.PS5;

namespace MkPFS.Build.FPKG.Prospero;

/// <summary>
/// Generators for the PS5 PlayGo / "about" files added during PKG building. See the file header
/// for the layouts and the boundary.
/// </summary>
public static class ProsperoPlayGo
{
    /// <summary>The fixed size of a PS5 <c>playgo-chunk.dat</c> for the single-chunk profile.</summary>
    public const int ChunkDatSize = 0x1A0; // 416

    /// <summary>The fixed size of a PS5 <c>playgo-ficm.dat</c> header (per-file array follows).</summary>
    public const int FicmHeaderSize = 0x10; // 16

    // MkPFS: the same right.sprx is embedded once for both package paths.
    private const string RightSprxResource = FPKGSource.RightSprxResource;

    /// <summary>The PlayGo CRC block size: the finalized mount image is reduced in 64KiB blocks.</summary>
    public const int ChunkCrcBlockSize = 0x10000;

    /// <summary>
    /// Builds the PS5 <c>sce_suppl/config/&lt;content-id&gt;/playgo-chunk.crc</c> by reducing the
    /// finalized mount image with CRC-32C (Castagnoli) in 64KiB blocks and serialising each block's
    /// checksum as a little-endian uint32, in block order. The <paramref name="finalizedMountImage"/>
    /// is the FIH+PFS+SC region that precedes the SI segment (i.e. everything from offset 0 up to the
    /// SI archive); a finalized
    /// mount image is always a whole number of 64KiB blocks, but a trailing partial block (if any)
    /// is reduced over its actual length.
    /// </summary>
    /// <param name="finalizedMountImage">The finalized mount image bytes (FIH header + PFS image + embedded CNT).</param>
    /// <returns>The <c>playgo-chunk.crc</c> payload: 4 bytes per 64KiB block.</returns>
    public static byte[] BuildChunkCrc(ReadOnlySpan<byte> finalizedMountImage)
    {
        if (finalizedMountImage.Length == 0)
            return [];

        int blockCount = (finalizedMountImage.Length + ChunkCrcBlockSize - 1) / ChunkCrcBlockSize;
        byte[] crc = new byte[blockCount * 4];
        for (int i = 0; i < blockCount; i++)
        {
            int start = i * ChunkCrcBlockSize;
            int len = Math.Min(ChunkCrcBlockSize, finalizedMountImage.Length - start);
            uint value = ProsperoCrc32C.Compute(finalizedMountImage.Slice(start, len));
            BinaryPrimitives.WriteUInt32LittleEndian(crc.AsSpan(i * 4), value);
        }
        return crc;
    }

    /// <summary>
    /// Builds the PS5 <c>sce_sys/playgo-chunk.dat</c> (<c>plgx</c> container, version 0x1000) for the
    /// single-image / single-chunk / single-scenario profile used by PS5 system applications.
    /// </summary>
    /// <param name="contentId">The 36-character content id stamped at offset 0x40.</param>
    /// <param name="mchunk0Size">
    /// Size of the first mchunk region (word at 0x148), covering the block-aligned inner image.
    /// </param>
    /// <param name="mchunk1Size">
    /// Size of the second mchunk region (word at 0x158), covering the remainder of the mount image.
    /// </param>
    /// <returns>The 416-byte <c>playgo-chunk.dat</c> payload.</returns>
    public static byte[] BuildChunkDat(string contentId, ulong mchunk0Size = 0, ulong mchunk1Size = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(contentId);
        if (contentId.Length != 36)
            throw new ArgumentException("Content id must be exactly 36 characters.", nameof(contentId));

        byte[] d = new byte[ChunkDatSize];
        var s = d.AsSpan();

        // ---- Header (0x00 .. 0x40). ----
        Encoding.ASCII.GetBytes("plgx").CopyTo(s);                  // 0x00 magic
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x04..], 0x1000); // version_major (PS5)
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x06..], 0x0000); // version_minor
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x08..], 1);      // image_count
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x0A..], 1);      // chunk_count
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x0C..], 0);      // mchunk_count (in this profile)
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x0E..], 1);      // scenario_count
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x10..], ChunkDatSize); // file_size
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x14..], 0);      // default_scenario_id
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x16..], 1);      // attrib
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x18..], 0);      // sdk_ver
        // 0x1C .. 0x40: fixed preamble.
        s[0x1E] = 0x85;                                             // layer/flags constant
        s[0x20] = 0x02;
        s[0x24] = 0x01;
        s[0x30] = 0x11;
        s[0x38] = 0xFF; s[0x39] = 0xFF; s[0x3A] = 0xFF; s[0x3B] = 0xFF;
        s[0x3C] = 0xFF; s[0x3D] = 0xFF; s[0x3E] = 0xFF; s[0x3F] = 0xFF;

        // ---- Content id (0x40, 36 bytes ASCII). ----
        Encoding.ASCII.GetBytes(contentId).CopyTo(s[0x40..]);

        // ---- Section pointer table (0xC0): (offset, size) pairs. ----
        WritePtr(s, 0xC0, 0x100, 0x20); // chunk_attrs
        WritePtr(s, 0xC8, 0x120, 0x08); // chunk_mchunks
        WritePtr(s, 0xD0, 0x130, 0x09); // chunk_labels ("Chunk #0\0")
        WritePtr(s, 0xD8, 0x140, 0x20); // mchunk_attrs
        WritePtr(s, 0xE0, 0x160, 0x20); // inner mchunk_attrs
        WritePtr(s, 0xE8, 0x180, 0x02); // scenario_attrs
        WritePtr(s, 0xF0, 0x190, 0x0C); // scenario_labels ("Scenario #0\0")

        // ---- Chunk attribute (0x100). ----
        s[0x100] = 0x80; // flag
        s[0x102] = 0x03; // req_locus
        s[0x104] = 0x02; // mchunk_count for the chunk
        s[0x108] = 0x11; // language/attr constant
        // language_mask: all-languages (0xFFFFFFFFFFFFFFFF) at 0x110.
        BinaryPrimitives.WriteUInt64LittleEndian(s[0x110..], ulong.MaxValue);

        // ---- chunk_mchunks (0x120): chunk #0 references mchunk index 1. ----
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x124..], 1);

        // ---- chunk_labels (0x130). ----
        Encoding.ASCII.GetBytes("Chunk #0").CopyTo(s[0x130..]);

        // ---- mchunk_attrs (0x140): two 16-byte {offset, size} entries that tile the mount
        // image [0, cnt_offset) = FIH header block + PFS image. entry0 = {0, mchunk0Size};
        // entry1 = {mchunk0Size, mchunk1Size}. Both sizes are non-zero and sum to the mount
        // image length; the chunk at 0x100 references mchunk index 1, so it must be present.
        BinaryPrimitives.WriteUInt64LittleEndian(s[0x140..], 0);
        BinaryPrimitives.WriteUInt64LittleEndian(s[0x148..], mchunk0Size);
        BinaryPrimitives.WriteUInt64LittleEndian(s[0x150..], mchunk0Size);
        BinaryPrimitives.WriteUInt64LittleEndian(s[0x158..], mchunk1Size);

        // ---- inner mchunk_attrs (0x160): {0x21, 0} then a constant {1,1} marker at 0x174. ----
        BinaryPrimitives.WriteUInt64LittleEndian(s[0x160..], 0x21);
        s[0x174] = 0x01;
        s[0x176] = 0x01;

        // ---- scenario_labels (0x190). ----
        Encoding.ASCII.GetBytes("Scenario #0").CopyTo(s[0x190..]);

        return d;
    }

    /// <summary>
    /// Builds the PS5 <c>sce_sys/playgo-ficm.dat</c>. The file is a 16-byte header followed by a
    /// <paramref name="fileCount"/>-byte per-file array (zero-filled), so its
    /// total length is <c>16 + fileCount</c>.
    /// </summary>
    /// <param name="fileCount">The PlayGo file/inode count stamped at 0x0C.</param>
    public static byte[] BuildFicm(uint fileCount)
    {
        // Defensive bound: the per-file array is one byte per file; a package has at most a few
        // thousand inodes, so cap well below int.MaxValue to keep the (int) cast and allocation safe.
        if (fileCount > 0x100000)
            throw new ArgumentOutOfRangeException(nameof(fileCount), fileCount, "PlayGo file count is implausibly large.");
        byte[] d = new byte[FicmHeaderSize + (int)fileCount];
        var s = d.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x00..], 1);              // version
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x08..], FicmHeaderSize); // per-file array offset
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x0C..], fileCount);      // file/inode count
        return d;
    }

    /// <summary>The fixed size of the <c>playgo-hash-table.dat</c> header + 16-byte prefix
    /// (the per-chunk constant table follows at this offset).</summary>
    public const int HashTableTableOffset = 0x38;

    /// <summary>
    /// Builds the PS5 <c>sce_sys/playgo-hash-table.dat</c> (CNT entry id <c>0x2010</c>): a 0x28-byte header with a
    /// <c>FLT</c> magic, the two path-hash seeds, then the <see cref="PS5PathHash.HashPath"/> of every inner
    /// file in ascending order, 8 bytes each (PS5PkgTool; LibProsperoPkg took the hashes for constants).
    /// </summary>
    /// <param name="innerPaths">Inner file paths from the user root.</param>
    public static byte[] BuildHashTable(IReadOnlyList<string> innerPaths)
    {
        List<ulong> hashes = [.. innerPaths.Select(p => PS5PathHash.HashPath("/" + p)).Order()];
        int tableSize = hashes.Count * HashTableEntrySize;
        byte[] d = new byte[HashTableTableOffset + tableSize];
        var s = d.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x00..], 1);                          // version
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x04..], 0x08000000);                 // const flags
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x08..], HashTableTableOffset);       // table offset
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x0C..], (uint)tableSize);            // table size
        new byte[] { 0x7F, (byte)'F', (byte)'L', (byte)'T' }.CopyTo(s[0x18..]);          // "FLT" magic
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x24..], (uint)hashes.Count);         // entry count
        BinaryPrimitives.WriteUInt64LittleEndian(s[0x28..], PS5PathHash.Seed0);
        BinaryPrimitives.WriteUInt64LittleEndian(s[0x30..], PS5PathHash.Seed1);
        for (int i = 0; i < hashes.Count; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(s[(HashTableTableOffset + (i * HashTableEntrySize))..], hashes[i]);
        }

        return d;
    }

    /// <summary>The size of one <c>playgo-hash-table.dat</c> per-chunk table entry.</summary>
    public const int HashTableEntrySize = 8;

    /// <summary>
    /// The fixed PS5 debug rights module embedded in every debug package, or <c>null</c> when the
    /// embedded resource is unavailable.
    /// </summary>
    public static byte[]? GetRightSprx()
    {
        using Stream? stream = typeof(ProsperoPlayGo).GetTypeInfo().Assembly
            .GetManifestResourceStream(RightSprxResource);
        if (stream is null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static void WritePtr(Span<byte> s, int at, uint offset, uint size)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(s[at..], offset);
        BinaryPrimitives.WriteUInt32LittleEndian(s[(at + 4)..], size);
    }
}
