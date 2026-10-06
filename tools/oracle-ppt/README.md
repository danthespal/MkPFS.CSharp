# PS5PkgTool oracle

`pack fpkg` reproduces the debug packages that [PS5PkgTool](https://github.com/pearlxcore/PS5PKGTool)
(pearlxcore, GPL-3.0) builds (FPKG plan F5, user decision 2026-10-06). PS5PkgTool's package engine,
`ThirdParty/ProsperoPkgTool/ProsperoPkgTool.dll` (MIT, "clean-room"), ships without source, so it is used as a
black box: MkPFS is checked against its output, not ported from its code.

| Oracle | Checked out at | Pinned commit | Engine DLL SHA-256 |
|---|---|---|---|
| PS5PkgTool | `../PS5PkgTool` | `61874dfe0f4bbf23c61d421010c9fd86bb3a68d2` | `f6ca7f56…6aa6b8` |

```bash
python tools/oracle-ppt/build_ppt_goldens.py --check
```

The script builds `runner/PPTOracle.csproj` (not in `MkPFS.slnx`) against the checkout and runs the engine
through `PS5PKGTool.Core.Builders.SonyDebugPackageBuilder` on every fixture tree (`fpkg/trees`, built by
`tools/oracle-fpkg/build_fpkg_goldens.py`, and `fpkg/sdk-trees`, built by `tools/oracle-fpkg/build_sdk_refs.py`)
and `fpkg/ppt-trees` (made by this script: `app_pack`, hb_min plus files that pin the stored packing rule and a
short last gap chunk) in `stored` (no Kraken) and `auto` (Kraken where it helps) mode. Content id
`UP9000-PPSA99999_00-MKPFSORACLE00000`, passcode 32 × `0`, seed `000102…0f`. Output:
`tests/fixtures/generated/fpkg/ppt/<tree>_<mode>/out.pkg` and `manifest.json` (git-ignored). With a seed the engine
is byte-reproducible; its clock is then fixed (inner and outer superblock time 1781638585, 350 000 000 ns).

## Findings (2026-10-06)

1. Inner image and naps follow the Publishing Tools record encoding with their own chunking (128 KiB Kraken
   chunks, a run re-anchor per zero chunk); MkPFS's reader verifies them. CNT, FIH and SI follow LibProsperoPkg
   2.x (version stamp 0x20200722/0x01FE52E9, DRM 0 even for `standard`, no license or PlayGo scenario entries,
   param.json verbatim, all-languages PlayGo mask, XTS-encrypted naps_meta_18, ZIP times 1980-01-01) without
   pfsimage.xml, and add Publishing Tools' `sce_sys/pfs-version.dat`.
2. Keystone: header `"keystone"`, u16 3, zeros (LibProsperoPkg writes u16 1 at offset 10). `PS5Keys.Keystone`
   follows the engine.
3. Fake SELF: version 0x10, key type 0x10000101, flags 0x32, authority 0x3100000000000001 for executables and
   0x…02 for dynamic libraries, one zero 0x20-byte digest per 16 KiB page of each segment, metadata
   0x50 × entries + 0x30 + 0x240 bytes with the `00 00 01 00` marker at 0x50 × entries + 0x30.
   `SELFFile.MakeFake` follows the engine (`tests/MkPFS.Parity/FPKGContentTests.cs`).
4. `sce_sys/changeinfo/*` becomes CNT entries (0x1260…), not inner files.
5. Non-ASCII path characters are written as `?` (`日本語/テスト.bin` → `???/???.bin`), so names are lost and can
   collide. MkPFS refuses non-ASCII paths instead (as Publishing Tools does).
6. The last zero-gap chunk is always written as two fill blocks, 128 KiB and 64 KiB + (rest − 1) mod 64 KiB + 1,
   which is the exact second half only when the chunk is above 192 KiB (`app_sizes` and `app_raw` declare 64 KiB
   too much). A chunk of at most 128 KiB is recorded as one 8-byte block, so the decoder sees 128 KiB of zeros
   (`app_pack`). A strict decoder rejects both; MkPFS writes exact lengths (tests patch the engine's value back
   through `PS5InnerWriter.EngineGapFill`).
7. Packages without `sce_sys/param.json` are refused (`hb_noparam`).
8. Scale: 1 GiB builds in 15 s with 1.3 GB peak memory (file-backed above some size); 300 MiB peaked at 4.3 GB.

## Findings (2026-10-06, F6/F7)

9. Inner metadata is Publishing Tools' layout with three changes: afid order starts `sce_sys/keystone`,
   `sce_sys/about/right.sprx`, `sce_sys/pfs-version.dat`; empty files get no afid-table slot; the inode FLT has no
   empty-file bit. Stored image: 128 KiB raw chunks from each file's start; a file joins the current run unless the
   cursor is inside a 64 KiB block and the file is an executable, follows one, or would cross the block; the gap is
   256 KiB fill chunks sharing one 16-byte block (a run re-anchor each); the metadata sits at the next 64 KiB
   boundary behind LibProsperoPkg's stored PFSC container header (0x400 bytes, id=5 signatures zeroed). The image is
   not padded at the end. naps: no `2 << 24` in the header word, unused u2c slots point at the terminator, raw chunk
   records carry 0x08 in their ninth byte, the terminator's 128 KiB base is the image end and its uoff
   2 × (mount mod 256 KiB) + 1.
10. CNT/FIH: playgo-ficm counts 2 × inner files; playgo-hash-table is a `FLT` table of the path-hash seeds and the
    sorted `PS5PathHash.HashPath` of every inner file (LibProsperoPkg took the hashes for constants); FIH 0x94 =
    non-empty files + 2, 0x98 = files + 2, 0xFC = empty files; CNT+0x460 = SHA3-256 of the whole FIH block.
11. SI: naps_meta_18 (LibProsperoPkg's XTS TLV blob) has one block per file (whole file extent), one per gap chunk
    (16 stored bytes, 8 + 8, also for a short last chunk) and one per metadata chunk.
12. Outer PFS: inode slot n is an (n + 1)-level indirect tree laid out in pre-order (`app_large`: slot 0 block, slot 1
    root, its leaf, then the uroot dirents); LibProsperoPkg used flat single-indirect blocks.
13. RSA ciphertexts (CNT entries 0x0010, 0x0020 and the CNT+0x1000 seal) use padding derived inside the engine and
    cannot be reproduced without its private keys or its code; everything else in a stored package is byte-identical
    once they are swapped in (`tests/MkPFS.Parity/FPKGPackageTests.cs`).

## Findings (2026-10-06, F8: `auto` mode)

14. `auto` differs from `stored` in placement and compression only (`app_kraken` probe tree): no "would cross the
    64 KiB block" realignment; every 128 KiB chunk of a file except its last is compressed when the Kraken stream is
    smaller (no other threshold), except in executables (SELF magic; a `.prx` name alone does not count); every
    metadata chunk is Kraken (256 KiB, two sub-chunks). Kraken records: kind 2 (3 for sub literals) per sub-chunk,
    odd, field = first sub-chunk length − 1, no raw flag; the naps header word gains `2 << 24`. The metadata's PFSC
    header lists the blocks with the naps kinds as flags (no restart bit) and the first sub-chunk length − 1 as hint.
    naps_meta_18 lists a compressed file as one block (stored and plain totals) and a metadata chunk with both
    sub-chunk lengths (flag 0x40450000 for two).
15. The engine's Kraken encoder is not LibProsperoPkg's: none of its chunks equal MkPFS's encoding (MkPFS's are
    smaller with Huffman arrays). A constant 128 KiB chunk is a 0x26-byte LZ stream where MkPFS writes an 8-byte fill
    block (Publishing Tools' form). `auto` packages therefore match in structure only: chunk boundaries, every
    compress-or-store decision and the naps header (`FPKGPackageTests.Auto_package_matches_PS5PkgTool_in_structure`).

## Findings (2026-10-06, F9: scale)

16. A 256 KiB ublock holds at most 30 naps records (runs included). A file whose records would exceed that moves one
    ublock on, keeping its offset inside the ublock, which leaves a logical hole; the u2c table marks where the next
    ublock starts and the afid table gets a −1 slot in front of the moved file (`app_many`, 4,514 files).
17. Metadata spans blocks as needed: 390 inodes per inode-table block (none crossing a block, superblock 0x40 =
    the table's block count), and the FLTs, afid table and each directory's dirents packed across block boundaries
    (a directory inode's size is its whole blocks). FIH 0x90/0xA0 are the blocks and size of pfs_image.dat, also when
    naps_pkg_layout.dat spans several blocks.
18. naps_meta_18 keeps 32-bit sizes: a file extent or inner image of 4 GiB or more is written truncated (phdr image
    size, i2ob cs/ps/c0) and its ihsh digest covers only the truncated length. A 5 GiB game (one 5 GiB file) is
    byte-identical to PS5PkgTool's with that rule (manual check: `tmp/f9/scale`, 97 MiB peak memory, 65 s against
    PS5PkgTool's 97 s).
