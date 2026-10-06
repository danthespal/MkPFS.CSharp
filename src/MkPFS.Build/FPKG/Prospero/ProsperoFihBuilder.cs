// Ported from LibProsperoPkg (SvenGDK, GPL-3.0-or-later) PKG/ProsperoFihBuilder.cs at commit 748eabf
// for byte parity with its packages (FPKG plan F4b). See NOTICE.
using MkPFS.Core.Compression.Kraken;
// LibProsperoPkg - A library for building and inspecting PS5 packages.
// Copyright (C) 2026 SvenGDK
//
// Produces the PS5 finalized image (\x7FFIH), in the "debug" variant
// (signed byte 0x00) used by consoles whose debug mode relaxes finalized-image verification.
//
// A finalized image is built from four consecutive segments:
// FIH / PFS / SC / SI:
//
//   FIH  [0x00000 .. 0x10000)                     header (LITTLE-endian fields) + finalization
//                                                  digest table. FIH offset (0) and FIH size
//                                                  (0x10000) are ALWAYS constant, as is the PFS
//                                                  offset (0x10000); only the sizes below vary.
//   PFS  [0x10000 .. 0x10000+pfs_image_size)      the shared, AES-XTS-encrypted outer PFS image.
//   SC   [pfs_end .. pfs_end+sc_size)             the embedded \x7FCNT metadata container; its own
//                                                  pfs_image_offset points back to the shared image
//                                                  at 0x10000.
//   SI   [sc_end .. EOF)                           a ZIP archive of install-time metadata
//                                                  (common/etc/*_meta_*.dat, pfsimage.xml,
//                                                  playgo-chunk.dat, config/<cid>/playgo-chunk.crc).
//
// The signed byte at offset 0x05 distinguishes the two finalized variants: 0x00 = debug,
// 0x80 = retail / submitted. This is THE single byte that separates a retail-submitted package
// from a debug one in a complete FIH .pkg file.
//
// The FIH header's structural fields are magic, signed byte, PFS image offset/size, and
// embedded-CNT/SC offset and size. The embedded CNT and shared PFS image are the output of
// ProsperoPkgBuilder, so the produced file is parsed and validated by ProsperoPkgReader
// (Type=FullDebug, embedded CNT round-trips). The FIH game-digest at 0x30/0x70/0xD0 is
// SHA3-256 of the plaintext outer superblock. The CNT package-digest self-seal at CNT+0xFE0
// is SHA3-256 of CNT[0:0xFE0]. The CNT GeneralDigests block and per-entry digest table are
// SHA3-256 of plaintext CNT regions and entries. The distinct FIH slot at 0xB0 is the
// nested-image-content digest: SHA3-256 of the uncompressed inner PFS image at its logical
// size. The CNT build path threads that exact preimage in. The standalone-finalize path has
// only a finished encrypted CNT, so it falls back to SHA3-256 of the outer image. The trailing
// SI ZIP is generated only when the caller passes one through the siArchive parameter; its
// container is deterministic and its keyed members are caller-supplied. A console in debug mode
// that does not enforce those keyed members accepts the image.

using System;
using System.Buffers.Binary;
using System.IO;

namespace MkPFS.Build.FPKG.Prospero;

/// <summary>The finalized-image variant to emit.</summary>
public enum ProsperoFihVariant
{
    /// <summary>Debug finalized image (signed byte 0x00) for debug-mode consoles.</summary>
    Debug,

    /// <summary>Official finalized image (signed byte 0x80). The finalization digest table is
    /// debug/retail-key gated and not reproduced; emitting this is for structural tooling only.</summary>
    Official,
}

/// <summary>
/// Wraps a PS5 CNT metadata package into a finalized (FIH) image. See the file
/// header for the exact format and the reproduced fields.
/// </summary>
public static class ProsperoFihBuilder
{
    // CNT header field offsets (big-endian) reused to locate the shared PFS image.
    private const int CntPfsImageOffsetField = 0x410;
    private const int CntPfsImageSizeField = 0x418;

    /// <summary>
    /// Builds the 0x10000-byte finalized-image (FIH) header block. This is a SHARED, cycle-free helper used
    /// by both the standalone FIH writer (<see cref="BuildFromCnt"/>) and the PS5 CNT builder so the
    /// fixed-info-digest (SHA3-256 of this block) is self-consistent. The image-content slot 0xB0 is the
    /// nested-image-content digest: when <paramref name="nestedImageDigest"/> is supplied (the CNT build path,
    /// which has the uncompressed inner image in hand) it is written verbatim as SHA3-256 of the
    /// UNCOMPRESSED inner PFS image at its plain size; when it
    /// is null (the standalone finalize path, which only has the finished encrypted CNT) it falls back to the
    /// best-effort SHA3-256(outer image). Cycle-free either way: both inputs are final before the CNT digest
    /// table is computed (using the embedded CNT metadata here would create a digest cycle).
    /// </summary>
    internal static byte[] BuildFihHeaderBlock(
        ProsperoFihVariant variant, ulong pfsImageSize, ulong embeddedCntOffset,
        long sbOffsetInImage, byte[] gameDigest,
        byte[]? nestedImageDigest = null, long nestedImageSize = 0, long nestedMetaBaseBlocks = 0,
        uint nwonlyContentVersionHi = 0, int nwonlyInnerContentInodes = 0, int nwonlyAppFileCount = 0,
        long nwonlyNdblock = 0, int nwonlyEmptyFiles = 0, long nwonlyInnerImageBlocks = 0)
    {
        byte[] h = new byte[ProsperoPkgLayout.FihHeaderRegionSize];

        // ---- Structural fields (little-endian). ----
        h[0] = ProsperoPkgLayout.FihMagic[0];
        h[1] = ProsperoPkgLayout.FihMagic[1];
        h[2] = ProsperoPkgLayout.FihMagic[2];
        h[3] = ProsperoPkgLayout.FihMagic[3];
        h[4] = 0x01;
        h[ProsperoPkgLayout.FihSignedByteOffset] = (byte)(variant == ProsperoFihVariant.Official ? 0x80 : 0x00);
        h[6] = 0x03;
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(0x08), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(ProsperoPkgLayout.FihPfsImageOffsetField), (ulong)ProsperoPkgLayout.FihHeaderRegionSize);
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(ProsperoPkgLayout.FihPfsImageSizeField), pfsImageSize);
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(0x28), (ulong)ProsperoPkgLayout.FihHeaderRegionSize);
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(ProsperoPkgLayout.FihEmbeddedCntOffsetField), embeddedCntOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(0x60), (ulong)ProsperoPkgLayout.FihHeaderRegionSize);
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(0x68), 0x800000000000UL);

        // 0x50 = the inner mount's data-region block count (= metaBase block index). SceShellCore
        // computes FIH[0x50] * FIH[0x60] and stores it as ppkg_opt[+0x30] = inner_sblock_offset, which
        // the kernel passes to read_sblock_wo_icv as the byte offset where the inner PFS superblock is
        // read. This MUST be metaBase (= nestedMetaBaseBlocks * blockSize), NOT Ndblock — the sblock
        // sits at metaBase, not at the logical mount end. The lvd3 mediasize comes independently from
        // sceKernelStat (the file's st_size), not from any FIH field.
        if (nestedMetaBaseBlocks > 0)
            BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(ProsperoPkgLayout.FihDataRegionBlockCountField), (ulong)nestedMetaBaseBlocks);

        // ---- Finalized-image digest table. ----
        // game-digest == sblock-digest == SHA3-256(plaintext outer superblock block, 0x10000 bytes),
        // stored three times at 0x30/0x70/0xD0.
        // The FIH also records the
        // superblock's absolute offset (0x20) and size (0x28) so the loader can locate the hashed
        // block. See ProsperoImageDigests for the full digest construction.
        {
            ulong sbAbsoluteOffset = (ulong)ProsperoPkgLayout.FihHeaderRegionSize + (ulong)sbOffsetInImage;
            BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(0x20), sbAbsoluteOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(0x28), (ulong)ProsperoImageDigests.BlockSize);
            CopyDigest(h, 0x30, gameDigest);
            CopyDigest(h, 0x70, gameDigest);
            CopyDigest(h, 0xD0, gameDigest);

            // ---- Outer-PFS / nested-image accounting. ----
            // The nwonly outer PFS uses the "data-first" layout
            //   [pfs_image.dat blocks][naps_pkg_layout.dat block][superblock][structural metadata...],
            // so the plaintext superblock sits exactly one block (the naps file) after the inner image.
            //   0x90 inner-image (pfs_image.dat) block count = sbBlockIndex - 1
            //   0x94 = 0x98 inner content-inode count         = dirs+files below uroot (nwonly), threaded in
            //   0x9C content-version echo                     = contentVersion packed 2-3-3 BCD
            //   0xA0 block-aligned inner-image size           = 0x90 * blockSize
            //   0xA8 naps_pkg_layout.dat (map[0xD]) length    = nestedImageSize (the 0xB0 digest preimage length)
            //   0xB0 nested-image-content digest              = SHA3-256(naps_pkg_layout.dat) [written below]
            //   0xF0 app-payload (non-sce_sys) file count / 0xF8 flat-path-table accounting (=2)
            int blockSize = ProsperoPkgLayout.FihHeaderRegionSize;
            long sbBlockIndex = (long)sbOffsetInImage / blockSize;
            long totalBlocks = (long)pfsImageSize / blockSize;
            if (sbBlockIndex >= 1 && (long)sbOffsetInImage % blockSize == 0 &&
                (long)pfsImageSize % blockSize == 0 && totalBlocks > sbBlockIndex)
            {
                bool nwonly = nwonlyInnerContentInodes > 0;
                // pfs_image.dat's block count; the naps file between it and the superblock can span several blocks.
                uint innerBlocks = nwonlyInnerImageBlocks > 0 ? (uint)nwonlyInnerImageBlocks : (uint)(sbBlockIndex - 1);
                // 0x94/0x98: the inner content-inode count for the data-first nwonly image: dirs and files
                // below uroot. The legacy non-nwonly path keeps its outer meta-block count.
                //
                // Reference packages also satisfy 0x90 + 0x98 == outer block count, and every reference
                // measured so far gives the same number for both readings, so the corpus cannot tell them
                // apart. The consumer of the field has not been identified either, so the reading that has
                // produced installable packages is kept until something decides it.
                uint metaOrInodes = nwonly ? (uint)nwonlyInnerContentInodes : (uint)(totalBlocks - innerBlocks);
                BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(ProsperoPkgLayout.FihInnerImageBlockCountField), innerBlocks);
                // PS5PkgTool: 0x98 = inner files + 2, 0x94 the same without empty files, 0xFC = empty files.
                BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(ProsperoPkgLayout.FihMetaBlockCountField), metaOrInodes - (uint)nwonlyEmptyFiles);
                BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(0xFC), (uint)nwonlyEmptyFiles);
                BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(ProsperoPkgLayout.FihMetaBlockCountMirrorField), metaOrInodes);
                ulong innerImageFieldValue = nwonlyNdblock > 0
                    ? (ulong)nwonlyNdblock * (ulong)blockSize
                    : (ulong)innerBlocks * (ulong)blockSize;
                BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(ProsperoPkgLayout.FihInnerImageSizeField), innerImageFieldValue);

                // 0x9C: content-version echo (high 32 bits of the param/content_ver u64), the "MM.mmm.ppp"
                // content version packed as 2-3-3 BCD digits.
                if (nwonlyContentVersionHi != 0)
                    BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(ProsperoPkgLayout.FihContentVersionField), nwonlyContentVersionHi);

                // 0xA8: naps_pkg_layout.dat length (= ctx.0x14e0 = size of map[0xD], the 0xB0 digest preimage).
                if (nestedImageSize > 0)
                    BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(ProsperoPkgLayout.FihInnerImageLogicalSizeField), (ulong)nestedImageSize);

                // 0xF0/0xF8: outer-PFS inode accounting. 0xF0 = app-payload (non-sce_sys) file count;
                // 0xF8 = flat-path-table accounting value.
                uint outerFileCount = nwonly && nwonlyAppFileCount > 0 ? (uint)nwonlyAppFileCount : ProsperoPkgLayout.FihOuterFileCount;
                BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(ProsperoPkgLayout.FihOuterFileCountField), outerFileCount);
                BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(ProsperoPkgLayout.FihFlatPathTableBlockCountField), ProsperoPkgLayout.FihFlatPathTableBlockCount);
            }

        }

        // The distinct 0xB0 slot is the nested-image-content digest:
        // 0xB0 = SHA3-256(map[0xD]) where map[0xD] is the naps_pkg_layout.dat content. FIH 0xA8
        // is its length. The CNT build path threads that digest in via nestedImageDigest; the standalone
        // finalize path, which only has the finished encrypted CNT, falls back to SHA3-256(outer image).
        // Cycle-free either way: the naps is final before the CNT digest table is computed.
        if (nestedImageDigest is { Length: 32 })
            CopyDigest(h, 0xB0, nestedImageDigest);

        return h;
    }

    private static void CopyDigest(byte[] dst, int offset, byte[] digest32)
    {
        Array.Copy(digest32, 0, dst, offset, Math.Min(32, digest32.Length));
    }

}
