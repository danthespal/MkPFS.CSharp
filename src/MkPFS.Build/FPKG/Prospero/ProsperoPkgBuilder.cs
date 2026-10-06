// Ported from LibProsperoPkg (SvenGDK, GPL-3.0-or-later) PKG/ProsperoPkgBuilder.cs at commit 748eabf
// for byte parity with its packages (FPKG plan F4b). See NOTICE.
using MkPFS.Core.Compression.Kraken;
// LibProsperoPkg - A library for building and inspecting PS5 packages.
// Copyright (C) 2026 SvenGDK
//
// End-to-end PS5 PKG/CNT writer: turns a prepared folder into a complete
// \x7FCNT package fully in-process. It assembles the outer container header, the system-container
// entries, the param.json + media entries and the inner+outer PFS image, then computes
// every digest and the header signature.
//
// Boundary: on-console acceptance is gated by the target console's configuration and is not
// validated here. The in-process validation covers the full
// structural correctness of the produced package: it round-trips through ProsperoPkgReader, its
// outer PFS decrypts back to the inner image, and every internal digest is self-consistent.

#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MkPFS.Build.FPKG.Prospero;

/// <summary>
/// The nwonly (data-first) FIH accounting fields the finalizer stamps into the FIH header, computed during the
/// CNT build where the inner assembler result, naps layout, and param.json are available.
/// </summary>
internal sealed class ProsperoFihNwonlyFields
{
    /// <summary>FIH 0x9C: high 32 bits of the param/content_ver u64 (contentVersion major BCD in the top byte).</summary>
    public uint ContentVersionHi { get; init; }

    /// <summary>FIH 0x94/0x98: inner content-inode count (dirs + files below uroot).</summary>
    public int InnerContentInodes { get; init; }

    /// <summary>FIH 0xF0: app-payload (non-sce_sys) regular file count.</summary>
    public int AppFileCount { get; init; }

    /// <summary>Inner PFS total block count (Ndblock) for the logical mount size at FIH 0xA0.</summary>
    public long Ndblock { get; init; }

    /// <summary>Empty inner files.</summary>
    public int EmptyFiles { get; init; }

    /// <summary>FIH 0x90: blocks of pfs_image.dat.</summary>
    public long InnerImageBlocks { get; init; }
}

/// <summary>The PS5 volume kind, which selects the content-type code stamped into the header.</summary>
public enum ProsperoVolumeType
{
    /// <summary>A PS5 application / game (gd, content_type 0x20).</summary>
    Application,

    /// <summary>Additional content that ships data (ac, content_type 0x21).</summary>
    AdditionalContentData,

    /// <summary>Additional content, entitlement only / no data (al, content_type 0x22).</summary>
    AdditionalContentNoData,
}

/// <summary>
/// The CNT part of a package: header, system entries, param.json, PlayGo and media entries, digests and the
/// header seal. <see cref="FPKGBuilder"/> supplies the outer image geometry and writes the result.
/// </summary>
internal static class ProsperoPkgBuilder
{
    // PS5 header constants.
    // CNT header @0x70. Content whose info resolves without an entitlement lookup.
    private const uint DrmTypeNone = 0x0;
    private const uint ContentTypeGd = 0x20;       // CNT header @0x74 (game data).
    private const uint ContentTypeAc = 0x21;       // additional content, with data.
    private const uint ContentTypeAl = 0x22;       // additional content, no data.
    private const uint Unk0CPs5 = 0xC;             // CNT header @0x0C.
    // CNT header @0x04 (BE u32). The validator reads bytes 0x04..0x05 as a little-endian u16 selector
    // and bytes 0x06..0x07 as a big-endian u16 version. This value yields selector 0x0200 (bit 9) and
    // version 1, which routes header validation through the RSA-3072 metadata-signature path.
    private const uint FlagsPs5 = 0x00020001;
    // CNT header @0x08 (BE u32). The validator reads byte 0x08 as a signed value and requires it to be
    // negative on the bit-9 path, so byte 0x08 must have its high bit set.
    private const uint Unk08Ps5 = 0x80000000;
    // CNT header @0x408 (BE u64): the finalized-outer-PFS flag word for an installable outer image.
    // bit 63 (present) | bit 61 (0x2000000000000000 = newCrypt/finalized outer
    // image) | 0x30c. The two low bits 0x0c0 that formerly (0x3cc) flagged a pfs cache are cleared, since
    // pfs_cache_size is 0. Only written to CNT+0x408; it does not drive any crypto path (the outer-PFS
    // reader takes the newCrypt bit from the superblock, not from here).
    private const ulong PfsFlags = 0xA00000000000030C;

    private const ulong BodyOffset = 0x2000;
    private const ulong PfsImageOffset = 0x80000;  // Canonical PFS image offset.
    private const int BlockSize = 0x10000;

    // Inner-image regular-file mode for a NON-sce_sys file (app payload); sce_sys files use 0x8168. Set by
    // ProsperoPs5InnerImageAssembler.BuildNodes; used to count the FIH 0xF0 app-payload file field.
    private const ushort NonSceSysFileMode = 0x816d;

    // imagedigs.dat is the unnamed CNT entry id 0x040A (one after PSRESERVED_DAT 0x409). It is a CNT
    // body entry — NOT an inner-PFS file — so it does not digest its own storage: there is no fixpoint
    // and no multi-pass build. Its size (= outer block count x 32) is known up front from the image.
    private const uint ImagedigsEntryId = 0x040A;

    // playgo-chunk.dat is CNT entry id 0x1001. Its bytes are copied verbatim into the trailing debug SI
    // segment (common/etc/playgo-chunk.dat), so the SI capture reads them straight off the built entry.
    private const uint PlayGoChunkDatEntryId = 0x1001;

    /// <summary>The content-type code for a PS5 volume kind.</summary>
    public static uint ContentTypeFor(ProsperoVolumeType type) => type switch
    {
        ProsperoVolumeType.AdditionalContentData => ContentTypeAc,
        ProsperoVolumeType.AdditionalContentNoData => ContentTypeAl,
        _ => ContentTypeGd,
    };

    /// <summary>True for additional-content (DLC) volume kinds.</summary>
    public static bool IsAdditionalContent(ProsperoVolumeType type) =>
        type is ProsperoVolumeType.AdditionalContentData or ProsperoVolumeType.AdditionalContentNoData;

    private static ProsperoCntContentFlags ContentFlagsFor(ProsperoVolumeType type) => type switch
    {
        ProsperoVolumeType.AdditionalContentNoData => 0,
        _ => ProsperoCntContentFlags.GD_AC | ProsperoCntContentFlags.GD_BASE,
    };

    /// <summary>Lay out the CNT of a package.</summary>
    /// <param name="contentId">Content id.</param>
    /// <param name="passcode">Passcode.</param>
    /// <param name="ekpfs">Image key.</param>
    /// <param name="paramJson">param.json, packed verbatim.</param>
    /// <param name="media">sce_sys CNT entries (name, bytes) other than param.json.</param>
    /// <param name="pfsSize">Outer PFS image size.</param>
    /// <param name="imagedigsSize">imagedigs size (32 bytes per outer block).</param>
    /// <param name="playgoFileCount">playgo-ficm.dat count.</param>
    /// <param name="innerPaths">Inner file paths (playgo-hash-table.dat).</param>
    /// <param name="mchunk0Size">First PlayGo mchunk.</param>
    /// <param name="mchunk1Size">Second PlayGo mchunk.</param>
    /// <returns>The container, digests not yet computed.</returns>
    internal static ProsperoCnt BuildContainer(string contentId, string passcode, byte[] ekpfs, byte[] paramJson,
        IReadOnlyList<(string Name, byte[] Data)> media, ulong pfsSize, int imagedigsSize, uint playgoFileCount,
        IReadOnlyList<string> innerPaths, ulong mchunk0Size, ulong mchunk1Size)
    {
        const ProsperoVolumeType volumeType = ProsperoVolumeType.Application;
        uint contentType = ContentTypeFor(volumeType);
        var pkg = new ProsperoCnt
        {
            Header = new ProsperoCntHeader
            {
                CNTMagic = "\u007fCNT",
                flags = (ProsperoCntFlags)FlagsPs5,
                unk_0x08 = Unk08Ps5,
                unk_0x0C = Unk0CPs5,
                entry_count = 0,
                sc_entry_count = 6,
                entry_count_2 = 0,
                entry_table_offset = 0,
                main_ent_data_size = 0,
                body_offset = BodyOffset,
                body_size = 0,
                content_id = contentId,
                drm_type = DrmTypeNone,
                content_type = contentType,
                content_flags = ContentFlagsFor(volumeType),
                promote_size = 0,
                // version_date / version_hash are FIXED PS5 package-format constants (NOT a real date/hash):
                // 0x20200722 / 0x01fe52e9. version_hash must be nonzero: the installer reads CNT+0x84 and
                // rejects a zero value. Matches ProsperoSiArchive.VersionDate/VersionHash used for the
                // pfsimage.xml <version-date>/<version-hash>.
                version_date = 0x20200722,
                version_hash = 0x01fe52e9,
                iro_tag = ProsperoCntIroTag.None,
                ekc_version = 0,  // license-free; drm-type none uses EKC v0
                sc_entries1_hash = new byte[32],
                sc_entries2_hash = new byte[32],
                digest_table_hash = new byte[32],
                body_digest = new byte[32],
                unk_0x400 = 1,
                pfs_image_count = 1,
                pfs_flags = PfsFlags,
                pfs_image_offset = PfsImageOffset,
                pfs_image_size = pfsSize,
                mount_image_offset = 0,
                mount_image_size = 0,
                package_size = PfsImageOffset + pfsSize,
                pfs_signed_size = BlockSize,
                pfs_cache_size = 0, // No pfs cache region, so this field is 0.
                pfs_image_digest = new byte[32],
                pfs_signed_digest = new byte[32],
                pfs_split_size_nth_0 = 0,
                pfs_split_size_nth_1 = 0,
                image_seed = new byte[16],   // 0x4A0: filled from the built outer superblock in FinishContainer.
                cnt_region_offset = 0,       // 0x4B0/0x4B8: set to the finalized FIH-relative locator in LayOutEntries.
                cnt_region_size = 0,
                desc_digest = new byte[64],  // 0x520: two SHA3-256 region digests, computed after the body is written.
            },
            HeaderDigest = new byte[32],
            HeaderSignature = new byte[ProsperoPkgSigner.SignatureSize],
        };

        // System-container entries (the 6 SC entries), ids 0x1/0x10/0x20/0x80/0x100/0x200.
        pkg.EntryKeys = new ProsperoCntKeysEntry(contentId, passcode);
        pkg.ImageKey = new ProsperoCntGenericEntry(ProsperoCntEntryId.IMAGE_KEY)
        {
            FileData = BuildImageKeyEntry(ekpfs),
        };
        pkg.GeneralDigests = new ProsperoCntGeneralDigestsEntry { type = ProsperoImageDigests.GeneralDigestsTypeFull };
        pkg.Metas = new ProsperoCntMetasEntry();
        pkg.Digests = new ProsperoCntGenericEntry(ProsperoCntEntryId.DIGESTS);
        pkg.EntryNames = new ProsperoCntNameTableEntry();

        // param.json (PS5 entry id 0x2000).
        var paramEntry = new ProsperoCntGenericEntry((ProsperoCntEntryId)0x2000, "param.json") { FileData = paramJson };

        pkg.Entries = new List<ProsperoCntEntry>
        {
            pkg.EntryKeys,
            pkg.ImageKey,
            pkg.GeneralDigests,
            pkg.Metas,
            pkg.Digests,
            pkg.EntryNames,
            paramEntry,
        };

        // PS5 image-digest + PlayGo descriptor CNT entries. The package layout has
        // these as OUTER CNT entries — imagedigs.dat
        // (id 0x040A, UNNAMED), playgo-chunk.dat (0x1001), playgo-hash-table.dat (0x2010) and
        // playgo-ficm.dat (0x2011) — NOT inner-PFS files. imagedigs is laid out as a placeholder sized
        // to the outer block count and filled with the captured per-block digests after the image is
        // written. The PlayGo file/inode count drives playgo-ficm.dat (count) and playgo-hash-table.dat
        // (count / 2), self-consistent. Any entry the source folder already
        // supplied (e.g. a hand-authored playgo-chunk.dat) is respected and not regenerated.
        //
        // Entry (container data) order:
        //   param.json, imagedigs.dat, playgo-chunk.dat, <media...>, playgo-hash-table.dat, playgo-ficm.dat.
        // imagedigs.dat carries the per-block image digests the installer reads to validate the
        // supplemental/mandatory region, so it (and playgo-chunk.dat) must precede the large media
        // entries (icon0.png/icon0.dds). Placing media first inflates <mandatory-size> (= imagedigs
        // offset) and pushes imagedigs past the mandatory prefix, which the FW10.01 installer rejects.
        void AddDescriptorEntries((uint Id, string? Name, byte[] Data)[] descriptors)
        {
            foreach (var (id, name, data) in descriptors)
                if (!pkg.Entries.Any(e => (uint)e.Id == id))
                    pkg.Entries.Add(new ProsperoCntGenericEntry((ProsperoCntEntryId)id, name) { FileData = data });
        }

        // imagedigs.dat + playgo-chunk.dat come BEFORE the media entries.
        AddDescriptorEntries(
        [
            (ImagedigsEntryId, null, new byte[imagedigsSize]),
            (0x1001u, "playgo-chunk.dat", ProsperoPlayGo.BuildChunkDat(contentId, mchunk0Size, mchunk1Size)),
        ]);

        // sce_sys media entries (icon0.png, pic0.png, pic1.png, snd0.at9, ...) present in the folder.
        // Skip any id already staged by the descriptor pass: playgo-chunk.dat (0x1001) is generated
        // from the rebuilt image's chunk sizes above, so a source copy of it is not added a second time.
        foreach (ProsperoCntEntry entry in OrderMedia(media))
            if (!pkg.Entries.Any(e => (uint)e.Id == (uint)entry.Id))
                pkg.Entries.Add(entry);

        // playgo-hash-table.dat + playgo-ficm.dat come AFTER the media entries.
        AddDescriptorEntries(
        [
            (0x2010u, "playgo-hash-table.dat", ProsperoPlayGo.BuildHashTable(innerPaths)),
            (0x2011u, "playgo-ficm.dat", ProsperoPlayGo.BuildFicm(playgoFileCount)),
        ]);

        pkg.Digests.FileData = new byte[pkg.Entries.Count * ProsperoCnt.HASH_SIZE];

        LayOutEntries(pkg, paramJson);
        return pkg;
    }

    // The PS5 Flags1 word for each entry id.
    private static uint Flags1For(uint id) => id switch
    {
        (uint)ProsperoCntEntryId.DIGESTS => 0x40000000,
        (uint)ProsperoCntEntryId.ENTRY_KEYS => 0x60000000,
        (uint)ProsperoCntEntryId.IMAGE_KEY => 0x60000000,        // image key is not entry-encrypted.
        (uint)ProsperoCntEntryId.GENERAL_DIGESTS => 0x60000000,
        (uint)ProsperoCntEntryId.METAS => 0x60000000,
        (uint)ProsperoCntEntryId.ENTRY_NAMES => 0x40000000,
        0x2000 => 0x00000000,                          // param.json
        _ => 0x08000000,                               // media / data entries
    };

    // No CNT entries in this package class are entry-encrypted, so Flags2 is always zero.
    private static uint Flags2For(uint id) => 0u;

    private static void LayOutEntries(ProsperoCnt pkg, byte[] paramJson)
    {
        // 1st pass: register every entry name so the name-table offsets are stable.
        foreach (var entry in pkg.Entries.OrderBy(e => e.Name, StringComparer.Ordinal))
            pkg.EntryNames.GetOffset(entry.Name);

        // 2nd pass: assign 16-byte-aligned data offsets and build the meta table.
        ulong dataOffset = pkg.Header.body_offset;
        foreach (var entry in pkg.Entries)
        {
            var meta = new ProsperoCntMetaEntry
            {
                id = entry.Id,
                NameTableOffset = pkg.EntryNames.GetOffset(entry.Name),
                DataOffset = (uint)dataOffset,
                DataSize = entry.Length,
                Flags1 = Flags1For((uint)entry.Id),
                Flags2 = Flags2For((uint)entry.Id),
            };
            pkg.Metas.Metas.Add(meta);
            if (entry == pkg.Metas)
                meta.DataSize = (uint)pkg.Entries.Count * 32;

            dataOffset = Align(dataOffset + meta.DataSize, 16);
            entry.meta = meta;
        }

        ulong bodySize = dataOffset - pkg.Header.body_offset;
        pkg.Metas.Metas.Sort((a, b) => a.id.CompareTo(b.id));
        pkg.Header.entry_count = (uint)pkg.Entries.Count;
        pkg.Header.entry_count_2 = (ushort)pkg.Entries.Count;
        pkg.Header.entry_table_offset = pkg.Metas.meta.DataOffset;
        pkg.Header.body_size = Align(pkg.Header.body_offset + bodySize, 0x10000) - pkg.Header.body_offset;
        pkg.Header.main_ent_data_size = (uint)(new ProsperoCntEntry[]
        {
            pkg.EntryKeys, pkg.ImageKey, pkg.GeneralDigests, pkg.Metas, pkg.Digests,
        }).Sum(x => x.Length);

        pkg.Header.pfs_image_offset = pkg.Header.body_offset + pkg.Header.body_size;

        // Finalized mount geometry = FIH block (0x10000) + shared PFS image + CNT container. pfs_image_offset
        // above is the CNT-INTERNAL container size (= body_offset + body_size); the FIH finalizer rewrites
        // CNT+0x410 to the FIH-relative 0x10000. mount_image_size / package_size must therefore be computed
        // from the FINALIZED geometry (adding the leading FIH block), NOT from the standalone pfs_image_offset
        // which omits it — the same value BuildSiXmlOptions derives for pfsimage.xml <package-size>.
        ulong containerSize = pkg.Header.pfs_image_offset;   // CNT body end = CNT container size (0x50000).
        pkg.Header.package_size = pkg.Header.mount_image_size =
            ProsperoImageDigests.FihRelativeImageOffset + pkg.Header.pfs_image_size + containerSize; // 0x100000

        // The CNT container's own locator in the finalized mount image (FIH-relative): its file offset is
        // the FIH block plus the shared PFS image, and its size is the container size. offset + size ==
        // package_size. The on-console 0x80b21185 install gate enumerates the content region these
        // descriptors point to; if they are zero the enumeration totals 0 and the geometry check fails.
        pkg.Header.cnt_region_offset = ProsperoImageDigests.FihRelativeImageOffset + pkg.Header.pfs_image_size; // 0xb0000
        pkg.Header.cnt_region_size = containerSize;          // 0x50000

        // Content-region descriptor (0x510): the IMAGE_KEY entry (id 0x0020) and the mandatory entry — the
        // entry at mandatory_size (CNT+0x30), i.e. the imagedigs entry. Use the entries' actual container
        // (DataOffset, DataSize).
        var mandatoryMeta = pkg.Metas.Metas.First(m => (uint)m.id == ImagedigsEntryId);
        pkg.Header.desc_image_key_offset = pkg.ImageKey.meta.DataOffset;
        pkg.Header.desc_image_key_size = pkg.ImageKey.meta.DataSize;
        pkg.Header.desc_mandatory_offset = mandatoryMeta.DataOffset;
        pkg.Header.desc_mandatory_size = mandatoryMeta.DataSize;

        // promote_size (CNT 0x7C) = the CNT container size (body_offset + body_size = CNT-internal
        // pfs_image_offset). mandatory_size (CNT 0x30) = the imagedigs entry offset (size of the mandatory
        // install region). Both are read by the installer's pre-allocation transfer and must be nonzero;
        // promote_size is the CNT size and mandatory_size is the imagedigs entry offset.
        pkg.Header.promote_size = (uint)pkg.Header.pfs_image_offset;
        pkg.Header.mandatory_size = pkg.Metas.Metas.First(m => (uint)m.id == ImagedigsEntryId).DataOffset;
    }

    // PS5 CNT IMAGE_KEY (0x20, 2048 bytes) — the EEKPFS entry. The outer-PFS EKPFS is wrapped with
    // RSA-3072 (EME-PKCS#1 v1.5) under the mount-image modulus (Keys/Data/mount_image.bin, one 384-byte
    // modulus). The fixed 0x800 field is filled with back-to-back independent wraps of the same EKPFS
    // (each 384 bytes), the last wrap truncated to fit — 5 whole wraps + the first 128 bytes of a 6th
    // (2048 % 384 = 128). Every 384-byte slot is a self-contained EEKPFS ciphertext, so a keyed mount
    // recovers the EKPFS from any whole wrap; a debug console derives the EKPFS from the content id +
    // passcode and does not read this entry. Each PKCS#1 wrap uses fresh random padding, so the entry
    // is not byte-reproducible, matching the per-build variation of a genuine package.
    private static byte[] BuildImageKeyEntry(byte[] ekpfs)
    {
        const int ImageKeySize = 0x800;
        const int Rsa3072Size = 384;
        var modulus = ProsperoKeys.MountImageKey.ToArray();
        var img = new byte[ImageKeySize];
        for (int off = 0; off < ImageKeySize; off += Rsa3072Size)
        {
            byte[] wrap = Crypto.RsaPkcs1EncryptKey(modulus, ekpfs);
            wrap.AsSpan(0, Math.Min(Rsa3072Size, ImageKeySize - off)).CopyTo(img.AsSpan(off));
        }
        return img;
    }

    // Computes the CNT+0x520 content-region descriptor digest table: two consecutive 32-byte SHA3-256
    // digests, one per region the 0x510 descriptor locates.
    //   0x520 = SHA3-256(IMAGE_KEY entry payload)   [region 1, (desc_image_key_offset, desc_image_key_size)]
    //   0x540 = SHA3-256(imagedigs entry payload)   [region 2, (desc_mandatory_offset, desc_mandatory_size)]
    // Both regions are read as on-disk bytes from the CNT stream (CNT base = stream offset 0 during the
    // container build; neither entry is entry-encrypted, so the on-disk bytes are the plaintext payloads).
    // This table stores exactly SHA3-256 of these two regions (the same per-entry digests the CNT digest
    // table already carries).
    private static byte[] ComputeDescriptorDigest(Stream s, in ProsperoCntHeader hdr)
    {
        byte[] r1 = new byte[hdr.desc_image_key_size];
        s.Position = hdr.desc_image_key_offset;
        s.ReadExactly(r1);
        byte[] r2 = new byte[hdr.desc_mandatory_size];
        s.Position = hdr.desc_mandatory_offset;
        s.ReadExactly(r2);
        byte[] table = new byte[2 * ProsperoImageDigests.DigestSize];
        ProsperoImageDigests.Sha3_256(r1).CopyTo(table, 0);
        ProsperoImageDigests.Sha3_256(r2).CopyTo(table, ProsperoImageDigests.DigestSize);
        return table;
    }

    /// <summary>
    /// Compute every digest, write the CNT (header and body, CNT-relative offsets) to <paramref name="s"/> and seal
    /// it, and return the FIH header block whose digest the CNT carries.
    /// </summary>
    /// <param name="pkg">Container from <see cref="BuildContainer"/>.</param>
    /// <param name="s">CNT stream, at least body_offset + body_size long.</param>
    /// <param name="contentId">Content id.</param>
    /// <param name="passcode">Passcode.</param>
    /// <param name="superblock">Plaintext outer superblock block.</param>
    /// <param name="superblockOffset">Superblock offset inside the outer image.</param>
    /// <param name="nestedImageDigest">SHA3-256 of the naps (FIH 0xB0).</param>
    /// <param name="nestedImageSize">naps length (FIH 0xA8).</param>
    /// <param name="nestedMetaBaseBlocks">Inner metadata base in blocks (FIH 0x50).</param>
    /// <param name="nwonlyFih">FIH accounting fields.</param>
    /// <returns>FIH header block.</returns>
    internal static byte[] FinishContainer(ProsperoCnt pkg, Stream s, string contentId, string passcode, byte[] superblock, long superblockOffset,
        byte[] nestedImageDigest, long nestedImageSize, long nestedMetaBaseBlocks, ProsperoFihNwonlyFields nwonlyFih)
    {
        // Read the outer PFS image (encrypted blocks + plaintext superblock) so the PS5 mount digests can be
        // computed for the mount image — both are SHA3-256, NOT SHA-256:
        //   game-digest  (pfs_image_digest @0x440) = SHA3-256(plaintext outer superblock block)
        //   fixed-info   (pfs_signed_digest @0x460) = SHA3-256(the FIH header block that wraps this CNT)
        // The FIH block is cycle-free here (it depends only on the image + sizes, never on the CNT digest
        // table) so it is identical to the one ProsperoFihBuilder.BuildFromCnt writes when finalizing.
        byte[] sblockDigest = ProsperoImageDigests.Sha3_256(superblock);
        pkg.Header.pfs_image_digest = sblockDigest;

        // CNT+0x4A0 image_seed: the 16-byte AES-XTS seed baked into the outer superblock (superblock+0x370).
        pkg.Header.image_seed = superblock.AsSpan(PfsSeedOffset, 16).ToArray();

        byte[] fihBlock = ProsperoFihBuilder.BuildFihHeaderBlock(
            ProsperoFihVariant.Debug, pkg.Header.pfs_image_size,
            ProsperoImageDigests.FihRelativeImageOffset + pkg.Header.pfs_image_size, superblockOffset, sblockDigest,
            nestedImageDigest: nestedImageDigest, nestedImageSize: nestedImageSize,
            nestedMetaBaseBlocks: nestedMetaBaseBlocks,
            nwonlyContentVersionHi: nwonlyFih.ContentVersionHi,
            nwonlyInnerContentInodes: nwonlyFih.InnerContentInodes,
            nwonlyAppFileCount: nwonlyFih.AppFileCount,
            nwonlyEmptyFiles: nwonlyFih.EmptyFiles,
            nwonlyInnerImageBlocks: nwonlyFih.InnerImageBlocks);
        pkg.Header.pfs_signed_digest = ProsperoImageDigests.ComputeFixedInfoDigest(fihBlock);

        // General digests (PS5 nwonly scheme: type 0x102 [set at creation so the layout reserves 0x1E0],
        // set_digests 0x10DE = content|game|header|system|param|playgo|target, all SHA3-256). game/fixed-info
        // above must already be set: the header-digest preimage (CNT[0x400:0x480]) includes both.
        foreach (var kv in ComputeGeneralDigests(pkg))
            pkg.GeneralDigests.Set(kv.Key, kv.Value);

        // Write the body (entries) now so the per-entry hashes can be computed from the stream.
        var writer = new ProsperoCntWriter(s);
        writer.WriteBody(pkg, contentId, passcode);
        CalcBodyDigests(pkg, s);

        // CNT+0x520/+0x540 descriptor digests: SHA3-256 over each of the two CNT regions the 0x510 descriptor
        // pair locates (the IMAGE_KEY entry and the mandatory/imagedigs entry), read as on-disk bytes now that
        // the body is written. This relationship holds exactly for reference packages, so the values are
        // reproduced rather than approximated.
        pkg.Header.desc_digest = ComputeDescriptorDigest(s, pkg.Header);

        // Header, header digest and the header signature.
        s.Position = 0;
        writer.WriteHeader(pkg.Header);
        // Package-digest (the CNT self-seal at +0xFE0): SHA3-256(CNT[0:0xFE0]). The preimage spans 0x410
        // (pfs_image_offset); BuildFromCnt rewrites that field to the FIH-relative 0x10000 when it finalizes
        // the image, so force 0x10000 here too — otherwise the stored seal would be over the physical offset
        // and would not match a verifier reading the finalized package. The full 0x1000-byte header region is
        // held so the header signature can be taken over the same finalized bytes.
        s.Position = 0;
        byte[] cntHead = new byte[0x1000];
        s.ReadExactly(cntHead);
        BinaryPrimitives.WriteUInt64BigEndian(
            cntHead.AsSpan(ProsperoImageDigests.CntPfsImageOffsetField, 8), ProsperoImageDigests.FihRelativeImageOffset);
        pkg.HeaderDigest = ProsperoImageDigests.ComputePackageDigest(cntHead);
        pkg.HeaderDigest.CopyTo(cntHead.AsSpan(ProsperoImageDigests.PackageDigestStoredOffset));
        s.Position = ProsperoImageDigests.PackageDigestStoredOffset;
        s.Write(pkg.HeaderDigest, 0, pkg.HeaderDigest.Length);

        // Header signature (+0x1000): SHA3-256 over the finalized 0x1000-byte header region (digest included),
        // sealed with the metadata RSA-3072 key. 384 bytes.
        byte[] headerDigest = ProsperoImageDigests.Sha3_256(cntHead);
        pkg.HeaderSignature = ProsperoPkgSigner.EncryptHeaderDigest(headerDigest);
        s.Position = 0x1000;
        s.Write(pkg.HeaderSignature, 0, pkg.HeaderSignature.Length);

        // The finalized CNT points at the outer image right after the FIH block.
        s.Position = ProsperoImageDigests.CntPfsImageOffsetField;
        Span<byte> imageOffset = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(imageOffset, ProsperoImageDigests.FihRelativeImageOffset);
        s.Write(imageOffset);
        return fihBlock;
    }

    /// <summary>
    /// FIH 0x9C value: the high 32 bits of the param/content_ver u64 stored in the FIH header.
    /// <paramref name="contentVersion"/> is the param.json "MM.mmm.ppp" string (e.g. "01.001.000"). All three
    /// fields are BCD-encoded and packed 2-3-3 hex digits: major in the top byte, minor in bits 12-23 and
    /// patch in bits 0-11, so "01.001.000" gives 0x01001000 and "01.000.000" gives 0x01000000.
    /// </summary>
    internal static uint ContentVersionHigh(string contentVersion)
    {
        if (string.IsNullOrWhiteSpace(contentVersion)) return 0;
        string[] parts = contentVersion.Split('.');
        if (parts.Length < 1) return 0;

        if (!TryBcd(parts[0], 2, out uint major)) return 0;
        uint minor = parts.Length > 1 && TryBcd(parts[1], 3, out uint m) ? m : 0;
        uint patch = parts.Length > 2 && TryBcd(parts[2], 3, out uint p) ? p : 0;
        return (major << 24) | (minor << 12) | patch;

        // Packs up to "digits" decimal characters as one BCD nibble each ("001" -> 0x001).
        static bool TryBcd(string field, int digits, out uint value)
        {
            value = 0;
            string s = field.Trim();
            if (s.Length == 0 || s.Length > digits) return false;
            foreach (char c in s)
            {
                if (c is < '0' or > '9') return false;
                value = (value << 4) | (uint)(c - '0');
            }
            return true;
        }
    }

    /// <summary>Plaintext superblock offset of the 16-byte outer PFS seed (superblock+0x370).</summary>
    private const int PfsSeedOffset = 0x370;

    // Per-entry CNT ids that contribute to the system-digest (the sce_sys visual/audio media + their *.dds
    // re-encodes) and the playgo-digest (the PlayGo stream files):
    // system = SHA3-256( ed(icon0.png 0x1200) ‖ ed(icon0.dds 0x1280) );
    // playgo = SHA3-256( ed(playgo-chunk.dat 0x1001) ‖ ed(playgo-hash-table.dat 0x2010) ‖ ed(playgo-ficm.dat 0x2011) ).
    private static readonly uint[] SystemMediaIds =
        [0x1006, 0x100D, 0x1200, 0x1220, 0x1240, 0x1280, 0x12A0, 0x12C0, 0x2040, 0x2060];
    private static readonly uint[] PlaygoIds = [0x1001, 0x2010, 0x2011];

    private static Dictionary<ProsperoCntGeneralDigest, byte[]> ComputeGeneralDigests(ProsperoCnt pkg)
    {
        byte[] game = pkg.Header.pfs_image_digest;
        bool includeGame = pkg.Header.content_type != ContentTypeAl;

        var digests = new Dictionary<ProsperoCntGeneralDigest, byte[]>
        {
            { ProsperoCntGeneralDigest.HeaderDigest, ComputeHeaderDigest(pkg) },
            { ProsperoCntGeneralDigest.ContentDigest, ComputeContentDigest(pkg, game, includeGame) },
        };
        if (includeGame)
        {
            // game-digest (= pfs_image_digest) and its copy in the target slot (target == game for nwonly).
            digests[ProsperoCntGeneralDigest.GameDigest] = game;
            digests[ProsperoCntGeneralDigest.TargetDigest] = game;
        }

        // system-digest / playgo-digest = SHA3-256 over the concatenated per-entry SHA3 digests of the
        // relevant entries, in ascending id order. Computed over whatever such entries the package carries
        // (self-consistent).
        byte[]? system = ComputeConcatOverEntries(pkg, SystemMediaIds);
        if (system is not null) digests[ProsperoCntGeneralDigest.SystemDigest] = system;
        byte[]? playgo = ComputeConcatOverEntries(pkg, PlaygoIds);
        if (playgo is not null) digests[ProsperoCntGeneralDigest.PlaygoDigest] = playgo;

        // param.json drives the param-digest (SHA3-256 of the entry payload) on PS5.
        var paramEntry = pkg.Entries.FirstOrDefault(e => (uint)e.Id == 0x2000);
        if (paramEntry is ProsperoCntGenericEntry { FileData: { } pj })
            digests[ProsperoCntGeneralDigest.ParamDigest] = ProsperoImageDigests.ComputeEntryDigest(pj);

        return digests;
    }

    private static byte[]? ComputeConcatOverEntries(ProsperoCnt pkg, uint[] ids)
    {
        var set = new HashSet<uint>(ids);
        var perEntry = pkg.Entries
            .Where(e => set.Contains((uint)e.Id) && e is ProsperoCntGenericEntry { FileData: not null })
            .OrderBy(e => (uint)e.Id)
            .Select(e => ProsperoImageDigests.ComputeEntryDigest(((ProsperoCntGenericEntry)e).FileData!))
            .ToList();
        return perEntry.Count == 0 ? null : ProsperoImageDigests.ComputeConcatDigest(perEntry);
    }

    private static byte[] ComputeHeaderDigest(ProsperoCnt pkg)
    {
        // header-digest = SHA3-256( CNT[0x00:0x40] ‖ CNT[0x400:0x480] ). The mount descriptor must carry the
        // finalized FIH-relative pfs_image_offset (0x10000) at CNT+0x410 — BuildFromCnt rewrites it on disk
        // after this runs, so force it in the preimage so the stored digest matches the finalized image.
        using var ms = new MemoryStream();
        new ProsperoCntWriter(ms).WriteHeader(pkg.Header);
        byte[] prefix = new byte[ProsperoImageDigests.HeaderDigestPrefixSize];
        ms.Position = 0;
        ms.ReadExactly(prefix);
        byte[] mount = new byte[ProsperoImageDigests.HeaderDigestMountDescriptorSize];
        ms.Position = 0x400;
        ms.ReadExactly(mount);
        return ProsperoImageDigests.ComputeHeaderDigest(prefix, ProsperoImageDigests.ForceFihRelativeImageOffset(mount));
    }

    private static byte[] ComputeContentDigest(ProsperoCnt pkg, byte[] game, bool includeGame)
    {
        // content-digest = SHA3-256( CNT[0x40:0x78] ‖ game-digest(32, when present) ‖ major-param-digest(32) ).
        // CNT[0x40:0x78] = content_id(36) + 12 reserved + drm_type(BE32 @0x30) + content_type(BE32 @0x34).
        // The major-param-digest is all-zero for the nwonly package class.
        byte[] descriptor = new byte[ProsperoImageDigests.ContentDescriptorSize];
        byte[] cid = Encoding.ASCII.GetBytes(pkg.Header.content_id);
        Array.Copy(cid, 0, descriptor, 0, Math.Min(cid.Length, 36));
        BinaryPrimitives.WriteUInt32BigEndian(descriptor.AsSpan(0x30, 4), pkg.Header.drm_type);
        BinaryPrimitives.WriteUInt32BigEndian(descriptor.AsSpan(0x34, 4), pkg.Header.content_type);
        return ProsperoImageDigests.ComputeContentDigest(
            descriptor, includeGame ? game : default, new byte[ProsperoImageDigests.DigestSize], includeGame);
    }

    private static void CalcBodyDigests(ProsperoCnt pkg, Stream s)
    {
        // All CNT body digests are SHA3-256 on PS5 (the per-entry table, body-digest, digest-table hash and
        // the two sc-entry rollups). This is the same primitive the digest layer above uses.
        var digests = pkg.Digests;
        var digestsOffset = pkg.Metas.Metas.First(m => m.id == ProsperoCntEntryId.DIGESTS).DataOffset;
        for (int i = 1; i < pkg.Metas.Metas.Count; i++)
        {
            var meta = pkg.Metas.Metas[i];
            var hash = Crypto.Sha3_256(s, meta.DataOffset, meta.DataSize);
            Buffer.BlockCopy(hash, 0, digests.FileData, 32 * i, 32);
            s.Position = digestsOffset + 32 * i;
            s.Write(hash, 0, 32);
        }

        pkg.Header.body_digest = Crypto.Sha3_256(s, (long)pkg.Header.body_offset, (long)pkg.Header.body_size);
        pkg.Header.digest_table_hash = Crypto.Sha3_256(pkg.Digests.FileData);

        using var ms = new MemoryStream();
        foreach (var entry in new ProsperoCntEntry[] { pkg.EntryKeys, pkg.ImageKey, pkg.GeneralDigests, pkg.Metas, pkg.Digests })
            new SubStream(s, entry.meta.DataOffset, entry.meta.DataSize).CopyTo(ms);
        pkg.Header.sc_entries1_hash = Crypto.Sha3_256(ms);

        ms.SetLength(0);
        foreach (var entry in new ProsperoCntEntry[] { pkg.EntryKeys, pkg.ImageKey, pkg.GeneralDigests, pkg.Metas })
        {
            long size = entry.Id == ProsperoCntEntryId.METAS ? pkg.Header.sc_entry_count * 0x20 : entry.meta.DataSize;
            new SubStream(s, entry.meta.DataOffset, size).CopyTo(ms);
        }
        pkg.Header.sc_entries2_hash = Crypto.Sha3_256(ms);
    }

    // Known sce_sys media files and their PS5 entry ids (the inspection-relevant subset).
    private static readonly (string Name, uint Id)[] MediaFiles =
    [
        ("icon0.png", 0x1200),
        ("pic0.png", 0x1220),
        ("pic1.png", 0x1006),
        ("pic2.png", 0x2040),
        ("snd0.at9", 0x1240),
        ("save_data.png", 0x100D),
        ("playgo-chunk.dat", 0x1001),
    ];

    // sce_sys images that are re-encoded as a same-named *.dds (BC7) sibling,
    // with the PS5 entry id of the generated *.dds. The mapping is
    // icon0.png->icon0.dds (0x1280), pic0.png->pic0.dds (0x12A0), pic1.png->pic1.dds
    // (0x12C0), pic2.png->pic2.dds (0x2060).
    private static readonly (string Png, string Dds, uint Id)[] DdsMedia =
    [
        ("icon0.png", "icon0.dds", 0x1280),
        ("pic0.png", "pic0.dds", 0x12A0),
        ("pic1.png", "pic1.dds", 0x12C0),
        ("pic2.png", "pic2.dds", 0x2060),
    ];

    // Entry ids produced by dedicated builders that must not be re-emitted from a supplied sce_sys
    // file. Id 0x1000 is not part of a PS5 package and is skipped if a source file maps to it.
    private static readonly HashSet<uint> GeneratedEntryIds = [0x1000];

    // The CNT body order LibProsperoPkg (and PS5PkgTool) use for supplied sce_sys entries: the media files in
    // MediaFiles order, their DDS re-encodes in DdsMedia order, then every other entry with a CNT id by name.
    private static IEnumerable<ProsperoCntEntry> OrderMedia(IReadOnlyList<(string Name, byte[] Data)> media)
    {
        Dictionary<string, byte[]> byName = media.ToDictionary(m => m.Name, m => m.Data, StringComparer.Ordinal);
        HashSet<uint> emitted = [];
        foreach ((string name, uint id) in MediaFiles)
        {
            if (byName.TryGetValue(name, out byte[]? data) && emitted.Add(id))
                yield return new ProsperoCntGenericEntry((ProsperoCntEntryId)id, name) { FileData = data };
        }

        foreach ((string _, string dds, uint id) in DdsMedia)
        {
            if (byName.TryGetValue(dds, out byte[]? data) && emitted.Add(id))
                yield return new ProsperoCntGenericEntry((ProsperoCntEntryId)id, dds) { FileData = data };
        }

        foreach ((string rel, byte[] data) in media.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            if (!ProsperoCntEntryNames.NameToId.TryGetValue(rel, out var id) || GeneratedEntryIds.Contains((uint)id) || !emitted.Add((uint)id))
                continue;
            if (!ProsperoSystemFiles.Validate(rel, data, out var error))
                throw new InvalidDataException($"sce_sys/{rel}: {error}");
            yield return new ProsperoCntGenericEntry(id, rel) { FileData = data };
        }
    }

    private static ulong Align(ulong value, ulong align)
    {
        var rem = value % align;
        return rem == 0 ? value : value + (align - rem);
    }

    private static long ToUnixSeconds(DateTime time) =>
        (long)time.ToUniversalTime().Subtract(DateTime.UnixEpoch).TotalSeconds;
}
