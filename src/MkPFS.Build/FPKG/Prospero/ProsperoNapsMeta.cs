// Ported from LibProsperoPkg (SvenGDK, GPL-3.0-or-later) PKG/ProsperoNapsMeta.cs at commit 748eabf
// for byte parity with its packages (FPKG plan F4b). See NOTICE.
using MkPFS.Core.Compression.Kraken;
// LibProsperoPkg - A library for building and inspecting PS5 packages.
// Copyright (C) 2026 SvenGDK
//
// Builder for the NAPS metadata records (`common/etc/naps_meta_*.dat`) that are
// streamed into the SI (install-metadata) segment of a finalized image for the streaming output
// formats (`nwonly`). The NAPS record dispatcher routes each record id to a stored
// member by the `naps_meta_%d.dat` naming.
//
// Records and inputs:
// * naps_meta_300/301/302/308.dat -> all four ids carry the same 48-byte
// plaintext descriptor record. The record is six little-endian u64 fields and is fully derived from
// the finalized inner-image geometry (no key, no console secret), so it is produced exactly here.
// * naps_meta_18.dat -> produced by BuildMeta18. The plaintext is a back-to-back TLV record stream
// (per-record 16-byte header: 4-byte tag, 1-byte version, 3 zero, u64 payload length) carrying the
// inner-image geometry (phdr), the content-file table (file/fstr), the per-block info tables
// (ibcl/i2ob/i2op/ihsh/rhsh) with real block digests over the finalized image, the outer digest
// (obdg), a fixed tweak marker (twek) and the four 48-byte descriptor records (pgpl/pgil/pgpi/pgpu,
// identical to naps_meta_300). The stream is padded to a 16-byte multiple with a trailing zero record
// and encrypted with AES-128-XTS under a fixed embedded key set. The whole file is one XTS data unit.
//
// naps_meta_300 RECORD (48 bytes, all values little-endian):
// 0x00 u64 = 0 reserved (record start offset)
// 0x08 u64 = 0 reserved
// 0x10 u64 = R inner-image data-region size (= innerImageSize - 0x10000)
// 0x18 u64 = 0x3E9 (1001) constant NAPS-meta kind/version id
// 0x20 u64 = R inner-image data-region size (repeated)
// 0x28 u64 = 0x10000 PFS block size (64 KiB)
// R = innerImageSize - 0x10000. R equals the nested-image <metadata offset> reported
// by the package's own pfsimage.xml, i.e. the size of the compressed inner-image content that precedes
// the inner image's own metadata block.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MkPFS.Build.FPKG.Prospero;

/// <summary>
/// Builder for the PS5 <c>naps_meta_*.dat</c> records emitted into the SI segment of a
/// <c>nwonly</c> finalized image. The 48-byte <c>naps_meta_300/301/302/308</c> descriptor is
/// derived from the inner-image geometry; <c>naps_meta_18.dat</c> is the
/// AES-128-XTS TLV metric blob built by <see cref="BuildMeta18"/> from the finalized image and its
/// content-file set. See <see cref="ProsperoSiArchive"/>.
/// </summary>
public static class ProsperoNapsMeta
{
    /// <summary>On-disk size of the <c>naps_meta_300/301/302/308</c> descriptor record, in bytes.</summary>
    public const int Meta300Length = 48;

    /// <summary>
    /// Constant NAPS-meta kind/version id stored at offset 0x18 of every <c>naps_meta_300</c> record
    /// (<c>0x3E9</c> = 1001). Identical across all debug packages.
    /// </summary>
    public const ulong Meta300KindId = 0x3E9;

    /// <summary>PFS block size (64 KiB) stored at offset 0x28 of the <c>naps_meta_300</c> record.</summary>
    public const ulong PfsBlockSize = 0x10000;

    /// <summary>The four <c>naps_meta_*</c> ids that share the 48-byte descriptor.</summary>
    public static ReadOnlySpan<int> Meta300Ids => [300, 301, 302, 308];

    /// <summary>
    /// Builds the 48-byte <c>naps_meta_300</c> descriptor (also used verbatim for ids 301,
    /// 302 and 308) from the inner-image data-region size.
    /// </summary>
    /// <param name="innerImageDataRegionSize">
    /// The inner-image data-region size <c>R</c> (offsets 0x10 and 0x20): the size of the compressed
    /// inner-image content that precedes the inner image's own metadata block. Equals
    /// <c>innerImageSize - 0x10000</c> and the nested-image metadata offset reported in pfsimage.xml.
    /// </param>
    /// <returns>A fresh 48-byte array containing the descriptor.</returns>
    public static byte[] BuildMeta300(ulong innerImageDataRegionSize)
    {
        byte[] record = new byte[Meta300Length];
        Span<byte> s = record;
        // 0x00, 0x08 already zero.
        BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(0x10, 8), innerImageDataRegionSize);
        BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(0x18, 8), Meta300KindId);
        BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(0x20, 8), innerImageDataRegionSize);
        BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(0x28, 8), PfsBlockSize);
        return record;
    }

    /// <summary>
    /// Builds the <c>naps_meta_300</c> descriptor from the full block-aligned inner-image size (the
    /// value the finalized-image header carries at offset 0xA0). Equivalent to
    /// <see cref="BuildMeta300(ulong)"/> with <c>innerImageSize - 0x10000</c>.
    /// </summary>
    /// <param name="innerImageSize">Block-aligned inner-image size; must be at least one block.</param>
    public static byte[] BuildMeta300FromInnerImageSize(ulong innerImageSize)
    {
        if (innerImageSize < PfsBlockSize)
            throw new ArgumentOutOfRangeException(nameof(innerImageSize),
                $"inner-image size 0x{innerImageSize:X} is smaller than one 0x{PfsBlockSize:X} block");
        return BuildMeta300(innerImageSize - PfsBlockSize);
    }

    // ---- naps_meta_18 (AES-128-XTS TLV metric blob) ----

    /// <summary>Image block size used for the per-block info tables (64 KiB).</summary>
    private const int Meta18BlockSize = 0x10000;

    // Fixed AES-128-XTS key set for the naps_meta_18 data unit. Constant across all packages.
    private static readonly byte[] Meta18DataKey =
        [0x02, 0x2D, 0xCA, 0xF6, 0xD1, 0x11, 0xE5, 0x8F, 0x25, 0x93, 0x6E, 0xF5, 0x46, 0x93, 0x45, 0xAB];
    private static readonly byte[] Meta18TweakKey =
        [0xAD, 0xAC, 0x16, 0x37, 0x60, 0xDA, 0x51, 0x46, 0x98, 0xC2, 0x45, 0xAB, 0x4C, 0x9C, 0x42, 0x6C];
    private static readonly byte[] Meta18Tweak =
        [0x3C, 0xBA, 0x10, 0x7D, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>
    /// Builds the encrypted <c>naps_meta_18.dat</c> metric blob: the inner-image geometry (phdr), the content-file
    /// table (file/fstr), one block per file, gap chunk and metadata chunk of the stored inner image
    /// (ibcl/i2ob/i2op/ihsh), the superblock digest (rhsh), a fixed marker (twek), the mount-image digest (obdg) and
    /// the four 48-byte descriptor records (pgpl/pgil/pgpi/pgpu), padded to 16 bytes and AES-128-XTS encrypted.
    /// </summary>
    /// <param name="innerImageSize">Block-aligned inner-image size (FIH 0xA0).</param>
    /// <param name="contentFiles">Inner files in afid order, then the <c>*PFSmetadata</c> pseudo-file.</param>
    /// <param name="chunks">Stored chunks of the inner image.</param>
    /// <param name="innerDigest">SHA3-256 of a stored range of the inner image (offset, length).</param>
    /// <param name="superblockDigest">SHA3-256 of the plaintext outer superblock.</param>
    /// <param name="mountDigest">SHA3-256 of the finalized mount image (FIH, outer PFS, CNT).</param>
    /// <returns>The encrypted blob.</returns>
    public static byte[] BuildMeta18(
        ulong innerImageSize, IReadOnlyList<(string Path, long Size)> contentFiles, IReadOnlyList<PS5InnerChunk> chunks,
        Func<long, long, byte[]> innerDigest, byte[] superblockDigest, byte[] mountDigest)
    {
        uint innerBlocks = (uint)(innerImageSize / PfsBlockSize);
        List<Meta18Block> blocks = BuildInnerBlocks(chunks);
        int metaFirstBlockIndex = blocks.FindIndex(b => b.Tail == Meta300KindId);
        var plain = new List<byte>(4096);

        // phdr: [ver=1, 0x30, innerBlocks, innerImageSize, 1, blockSize] (six u32).
        {
            Span<byte> h = stackalloc byte[0x18];
            WriteU32(h, 0x00, 1);
            WriteU32(h, 0x04, 0x30);
            WriteU32(h, 0x08, innerBlocks);
            WriteU32(h, 0x0C, (uint)innerImageSize);
            WriteU32(h, 0x10, 1);
            WriteU32(h, 0x14, (uint)PfsBlockSize);
            WriteRecord(plain, "phdr", 1, h);
        }

        // file: one 0x18 entry per content file [u64 size, u32 index, u32 type, u32 field10, u32 flag]; a file's index
        // is its position, flag 0 for the first file (keystone) else 1; "*PFSmetadata" has the first metadata
        // block's index, type 3, field10 0x3E9, flag 0.
        {
            var body = new byte[contentFiles.Count * 0x18];
            for (int i = 0; i < contentFiles.Count; i++)
            {
                Span<byte> e = body.AsSpan(i * 0x18, 0x18);
                bool isMeta = contentFiles[i].Path == PfsMetadataFileName;
                BinaryPrimitives.WriteUInt64LittleEndian(e[..8], (ulong)contentFiles[i].Size);
                WriteU32(e, 0x08, isMeta ? (uint)metaFirstBlockIndex : (uint)i);
                WriteU32(e, 0x0C, isMeta ? 3u : 1u);
                WriteU32(e, 0x10, isMeta ? (uint)Meta300KindId : 0u);
                WriteU32(e, 0x14, isMeta || i == 0 ? 0u : 1u);
            }

            WriteRecord(plain, "file", 2, body);
        }

        // ibcl: one class byte per block, 0x01 for blocks of files after the first, else 0x0F.
        {
            var body = new byte[blocks.Count];
            for (int i = 0; i < blocks.Count; i++)
                body[i] = blocks[i].OwnerFlag == 1 ? (byte)0x01 : (byte)0x0F;
            WriteRecord(plain, "ibcl", 1, body);
        }

        // i2ob: 0x28 per block [u64 co, u32 cs, u32 ps, u32 c0, u32 c1, u32 co >> 16, u32 0, u32 1, u32 flag].
        {
            var body = new byte[blocks.Count * 0x28];
            for (int i = 0; i < blocks.Count; i++)
            {
                Meta18Block b = blocks[i];
                Span<byte> e = body.AsSpan(i * 0x28, 0x28);
                BinaryPrimitives.WriteUInt64LittleEndian(e[..8], b.Co);
                WriteU32(e, 0x08, b.Cs);
                WriteU32(e, 0x0C, b.Ps);
                WriteU32(e, 0x10, b.C0);
                WriteU32(e, 0x14, b.C1);
                WriteU32(e, 0x18, (uint)(b.Co >> 16));
                WriteU32(e, 0x20, 1);
                WriteU32(e, 0x24, b.Flag);
            }

            WriteRecord(plain, "i2ob", 1, body);
        }

        // i2op: 0x10 per block [u64 co, u64 co >> 16].
        {
            var body = new byte[blocks.Count * 0x10];
            for (int i = 0; i < blocks.Count; i++)
            {
                Span<byte> e = body.AsSpan(i * 0x10, 0x10);
                BinaryPrimitives.WriteUInt64LittleEndian(e[..8], blocks[i].Co);
                BinaryPrimitives.WriteUInt64LittleEndian(e.Slice(8, 8), blocks[i].Co >> 16);
            }

            WriteRecord(plain, "i2op", 1, body);
        }

        // ihsh: 0x30 per block [u32 idx, u32 size, 32-byte digest, u64 tail]. Gap blocks: idx 0, size = ps, SHA3-256 of
        // ps zero bytes. File and metadata blocks: SHA3-256 of their stored bytes, tail 0 / 0x3E9.
        {
            var body = new byte[blocks.Count * 0x30];
            Dictionary<uint, byte[]> zeroDigests = [];
            for (int i = 0; i < blocks.Count; i++)
            {
                Meta18Block b = blocks[i];
                Span<byte> e = body.AsSpan(i * 0x30, 0x30);
                if (b.IsHole)
                {
                    WriteU32(e, 0x04, b.Ps);
                    if (!zeroDigests.TryGetValue(b.Ps, out byte[]? zero))
                        zeroDigests[b.Ps] = zero = ProsperoImageDigests.Sha3_256(new byte[b.Ps]);
                    zero.AsSpan(0, 32).CopyTo(e.Slice(0x08, 32));
                }
                else
                {
                    innerDigest(b.OnDiskOffset, b.OnDiskLen).AsSpan(0, 32).CopyTo(e.Slice(0x08, 32));
                    BinaryPrimitives.WriteUInt64LittleEndian(e.Slice(0x28, 8), b.Tail);
                }
            }

            WriteRecord(plain, "ihsh", 1, body);
        }

        // rhsh: the superblock digest, remainder zero (176 bytes).
        {
            var body = new byte[0xB0];
            superblockDigest.AsSpan(0, 32).CopyTo(body);
            WriteRecord(plain, "rhsh", 1, body);
        }

        // fstr: content-file relative paths, each (including the last) NUL-terminated.
        {
            var sb = new StringBuilder();
            foreach (var (path, _) in contentFiles)
            {
                sb.Append(path.Replace('\\', '/'));
                sb.Append('\0');
            }

            WriteRecord(plain, "fstr", 1, Encoding.ASCII.GetBytes(sb.ToString()));
        }

        // twek: fixed marker [0, 4, 0, 0, 0].
        {
            Span<byte> t = stackalloc byte[0x14];
            WriteU32(t, 0x04, 4);
            WriteRecord(plain, "twek", 1, t);
        }

        // obdg: the mount-image digest, remainder zero (128 bytes).
        {
            var body = new byte[0x80];
            mountDigest.AsSpan(0, 32).CopyTo(body);
            WriteRecord(plain, "obdg", 1, body);
        }

        // pgpl/pgil/pgpi/pgpu: the 48-byte descriptor, identical to naps_meta_300.
        {
            byte[] desc = BuildMeta300FromInnerImageSize(innerImageSize);
            WriteRecord(plain, "pgpl", 1, desc);
            WriteRecord(plain, "pgil", 1, desc);
            WriteRecord(plain, "pgpi", 1, desc);
            WriteRecord(plain, "pgpu", 1, desc);
        }

        // zero: trailing pad record sized so the plaintext ends on a 16-byte boundary.
        {
            int pad = (16 - (plain.Count % 16)) % 16;
            WriteRecord(plain, "zero", 1, new byte[pad]);
        }

        return AesXtsEncrypt(plain.ToArray());
    }

    private static void WriteU32(Span<byte> dst, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(offset, 4), value);

    // Emits one TLV record: 4-byte tag (stored in reverse byte order), 1-byte version, 3 zero, u64 length, payload.
    private static void WriteRecord(List<byte> dst, string tag, byte version, ReadOnlySpan<byte> payload)
    {
        dst.Add((byte)tag[3]);
        dst.Add((byte)tag[2]);
        dst.Add((byte)tag[1]);
        dst.Add((byte)tag[0]);
        dst.Add(version);
        dst.Add(0);
        dst.Add(0);
        dst.Add(0);
        Span<byte> len = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(len, (ulong)payload.Length);
        for (int i = 0; i < 8; i++) dst.Add(len[i]);
        for (int i = 0; i < payload.Length; i++) dst.Add(payload[i]);
    }

    // AES-128-XTS over the whole buffer as a single data unit (length must be a multiple of 16).
    private static byte[] AesXtsEncrypt(byte[] plain)
    {
        using var aesData = Aes.Create();
        aesData.Mode = CipherMode.ECB;
        aesData.Padding = PaddingMode.None;
        aesData.Key = Meta18DataKey;
        using var aesTweak = Aes.Create();
        aesTweak.Mode = CipherMode.ECB;
        aesTweak.Padding = PaddingMode.None;
        aesTweak.Key = Meta18TweakKey;

        using ICryptoTransform dataEnc = aesData.CreateEncryptor();
        using ICryptoTransform tweakEnc = aesTweak.CreateEncryptor();

        byte[] t = tweakEnc.TransformFinalBlock(Meta18Tweak, 0, 16);
        var outp = new byte[plain.Length];
        var pp = new byte[16];
        for (int i = 0; i < plain.Length; i += 16)
        {
            for (int j = 0; j < 16; j++) pp[j] = (byte)(plain[i + j] ^ t[j]);
            byte[] cc = dataEnc.TransformFinalBlock(pp, 0, 16);
            for (int j = 0; j < 16; j++) outp[i + j] = (byte)(cc[j] ^ t[j]);
            t = GfMulAlpha(t);
        }
        return outp;
    }

    // Multiply a 128-bit little-endian tweak by the element x in GF(2^128), reduction polynomial 0x87.
    private static byte[] GfMulAlpha(byte[] t)
    {
        var r = new byte[16];
        int carry = 0;
        for (int i = 0; i < 16; i++)
        {
            int b = t[i];
            r[i] = (byte)(((b << 1) | carry) & 0xFF);
            carry = (b >> 7) & 1;
        }
        if (carry != 0) r[0] ^= 0x87;
        return r;
    }

    // ---- nwonly inner-image NAPS block map (for ibcl/i2ob/i2op/ihsh/file) --------------------------

    /// <summary>256 KiB uncompressed NAPS block used by the inner-image geometry.</summary>
    private const long Meta18UBlock = 0x40000;

    /// <summary>The pseudo content-file name the nwonly SI appends for the inner PFS metadata region.</summary>
    private const string PfsMetadataFileName = "*PFSmetadata";

    /// <summary>
    /// One entry of the compressed inner-image NAPS block map. <see cref="Co"/> is the block's byte offset
    /// inside the packed <c>pfs_image.dat</c>; <see cref="Cs"/> its stored/compressed size (== C0+C1);
    /// <see cref="Ps"/> the plaintext byte span it covers; <see cref="C0"/>/<see cref="C1"/> the two
    /// 0x20000 sub-chunk compressed sizes; <see cref="Flag"/> the block-class word. <see cref="OwnerFlag"/>
    /// drives ibcl; <see cref="Tail"/> is the ihsh trailer (0x3E9 for metadata). For a real (non-hole)
    /// block, <see cref="OnDiskOffset"/>/<see cref="OnDiskLen"/> locate its compressed bytes in the image.
    /// </summary>
    private readonly record struct Meta18Block(
        ulong Co, uint Cs, uint Ps, uint C0, uint C1, uint Flag,
        bool IsHole, uint OwnerFlag, ulong Tail, long OnDiskOffset, uint OnDiskLen);

    /// <summary>
    /// Derives the compressed inner-image NAPS block map for <paramref name="inner"/>: the 3-way afid raw
    /// extents (<see cref="ProsperoPs5InnerPlacement"/>), the data-region hole ublocks tiling
    /// <c>[DataEndLogical, MetaBaseLogical)</c>, and the metadata ublocks (<c>MetadataBlocks</c>). This is
    /// the same geometry <see cref="ProsperoNwonlyNapsGenerator"/> emits for <c>naps_pkg_layout.dat</c>.
    /// </summary>
    private static List<Meta18Block> BuildInnerBlocks(IReadOnlyList<PS5InnerChunk> chunks)
    {
        List<Meta18Block> blocks = [];

        // One raw block per file (its chunks are contiguous); owner flag 0 for the first file (keystone).
        foreach (IGrouping<int, PS5InnerChunk> file in chunks.Where(c => c.Role == PS5InnerChunkRole.File).GroupBy(c => c.FileIndex))
        {
            long co = file.First().StoredOffset;
            uint size = (uint)file.Sum(c => (long)c.StoredLength);
            blocks.Add(new Meta18Block(
                Co: (ulong)co, Cs: size, Ps: (uint)file.Sum(c => (long)c.Length), C0: size, C1: 0,
                Flag: 0x40090000u, IsHole: false, OwnerFlag: file.Key == 0 ? 0u : 1u, Tail: 0,
                OnDiskOffset: co, OnDiskLen: size));
        }

        foreach (PS5InnerChunk c in chunks.Where(c => c.Role != PS5InnerChunkRole.File))
        {
            blocks.Add(c.Role == PS5InnerChunkRole.Gap
                // Zero-gap chunks: fill blocks; whole chunks share one stored offset.
                ? new Meta18Block(
                    Co: (ulong)c.StoredOffset, Cs: (uint)c.StoredLength, Ps: (uint)c.Length, C0: (uint)c.FirstHalf, C1: (uint)c.SecondHalf,
                    Flag: 0x40110000u, IsHole: true, OwnerFlag: 0, Tail: 0, OnDiskOffset: 0, OnDiskLen: 0)
                // Metadata chunks: raw, ihsh tail 0x3E9.
                : new Meta18Block(
                    Co: (ulong)c.StoredOffset, Cs: (uint)c.StoredLength, Ps: (uint)c.Length, C0: (uint)c.FirstHalf, C1: (uint)c.SecondHalf,
                    Flag: 0x40050000u, IsHole: false, OwnerFlag: 0, Tail: Meta300KindId,
                    OnDiskOffset: c.StoredOffset, OnDiskLen: (uint)c.StoredLength));
        }

        return blocks;
    }


}
