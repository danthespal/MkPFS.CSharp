# FPKG oracle corpus

Reference PS5 debug packages (`\x7FFIH`, fake-signed, license-free) built by
[LibProsperoPKG](https://github.com/SvenGDK/LibProsperoPKG) for the `pack fpkg` port (`docs/FPKG_PLAN.md`).

| Oracle | Checked out at | Pinned commit |
|---|---|---|
| LibProsperoPkg 2.6.0 (.NET 10) | `../LibProsperoPKG` | `748eabf1b7d17819528cabf367d8e27109d8fce3` |

```bash
uv run --project ../MkPFS python tools/oracle-fpkg/build_fpkg_goldens.py --check
```

The script uses only the Python standard library, so plain `python` works too. It takes about 20 s
for the two builds that `--check` runs.

## How it works

1. Checks that `../LibProsperoPKG` is at the pinned commit (`--allow-commit` overrides).
2. Copies `src/LibProsperoPkg` to `tmp/oracle-fpkg/lib`, adds empty `Directory.Build.props/.targets`
   (so MkPFS's warnings-as-errors settings do not apply) and `OracleHooks.cs`, then applies the
   patches in `PATCHES`. Each anchor must match exactly once. The checkout itself is never modified.
3. Builds `runner/FPKGOracle.csproj` into `tmp/oracle-fpkg/bin`. The runner is not in `MkPFS.slnx`.
4. Generates the fixture trees, builds every case, extracts it with the oracle's extractor, and writes
   test vectors.

### Hooks (`OracleHooks.cs`)

| Patched site | Original behavior | Hooked behavior |
|---|---|---|
| `ProsperoPkgBuilder` outer seed | `RandomNumberGenerator.GetBytes(16)` | `--seed` |
| `ProsperoInode()` time | `DateTime.UtcNow` | `--timestamp` |
| `ProsperoPackageBuilder` build props | `TimeStamp` left at the Unix epoch | `--timestamp` |
| `Crypto.RsaPkcs1EncryptKey` (CNT entries 0x0010/0x0020) | random PKCS#1 v1.5 padding | deterministic padding (below) |
| `ProsperoPkgSigner.EncryptHeaderDigest` (CNT+0x1000) | random PKCS#1 v1.5 padding | deterministic padding (below) |
| `ProsperoPs5InnerImageAssembler` file policy | classifier result | `--raw`: `Compress` becomes `StoreVerbatim` |

Deterministic PKCS#1 v1.5 type-2 padding, which MkPFS must reproduce: `EM = 00 02 PS 00 M`, where `PS`
is the nonzero bytes of `SHA-256(seed || modulus || M || u32le(counter))` for counter = 0, 1, ...,
concatenated and cut to `k - 3 - len(M)` bytes. Then `C = EM^e mod n` (big-endian, `k` bytes).
The runner checks the padding: the CNT+0x1000 value decrypts with the oracle's private key in every case.

## Output (`tests/fixtures/generated/fpkg/`, git-ignored)

- `trees/<tree>/`: fixture sources.
- `goldens/<case>/out.pkg`: the package. `result.json` holds the FIH/PFS/CNT/SI segment offsets and
  SHA-256 values, the CNT entry table with per-entry SHA-256, the header-signature check, the extract result
  and the oracle warnings. `build.log` is the oracle log. `work/` is the source copy after the build.
  `extract/` is the extractor output (empty, see findings).
- `goldens/manifest.json`: inputs, tree hashes, per-case hashes and checks, vector hashes.
- `vectors/vectors.json`: EKPFS (SHA3 and SHA-256), XTS tweak/data and sign keys for a fixed seed,
  keystone, outer `\x7fFLT` name hash and inner path hash (non-ASCII paths included), outer-block XTS
  (plain and bit-47 signed sectors), deterministic RSA wraps for both moduli, SHA3-256 and CRC-32C on
  patterned input, plus `fself/**.self` (`MakeFself` of every fixture ELF) and `dds/**.dds`
  (`EncodePngToDds` of every fixture PNG). `tests/MkPFS.Parity/FPKGVectorTests.cs` checks them.
- `determinism.json` (with `--check`): differing manifest keys between two builds (expected `[]`).

Inputs: content id `UP9000-PPSA99999_00-MKPFSORACLE00000`, passcode 32 × `0`, timestamp 1700000000,
seed `000102…0f`.

## Cases

| Tree | Contents |
|---|---|
| `hb_min` | ELF `eboot.bin` (type 0xFE10), `param.json`, 512×512 `icon0.png` |
| `hb_noparam` | same without `param.json` (the oracle generates one) |
| `app_multi` | eboot, `.prx` with SCE module-data segment, plain ELF, `pic0.png` 1920×1080, `changeinfo.xml`, empty file, files at 64 KiB / 256 KiB / 256 KiB+1, 2 MiB mixed, deep dirs, upper-case names |
| `app_unicode` | non-ASCII directory and file names |

Each tree is built as `<tree>_kraken` (oracle default) and `<tree>_raw`. Two more cases:
`hb_noparam_freemium` (`--app-type FreemiumApp`) and `hb_min_passcode` (a non-zero passcode).

## Publishing Tools references (`build_sdk_refs.py`)

Sony's own nwonly output is the format reference; LibProsperoPkg is not (findings 6–9). The script runs the
local toolkit `../sdk-fpkg279-fix12` (`prospero-pub-cmd.exe img_create --oformat nwonly`, a patched
plaintext/no-auth build) on the fixture trees. It stages each tree with the passcode keystone and the fSELF
vectors under `fpkg/sdk/<tree>/src` and writes `fpkg/sdk/<tree>/out.pkg` plus build logs. The SDK is never
copied, committed or shipped; its outputs stay in the git-ignored corpus. Builds are not reproducible
(timestamps), so tests check structure and contents, not hashes.

```bash
python tools/oracle-fpkg/build_sdk_refs.py
```

`hb_min`, `app_multi`, `app_raw` and `app_sizes` build; `hb_noparam` (no param.json) and `app_unicode`
(non-ASCII paths) are rejected by the toolkit. Each built tree also gets two rebuilds of the wrapper's GP5
with one PlayGo chunk and no `playgo-languages/` payload (the `pack fpkg` profile), run through
`prospero-pub-cmd` directly: `single.pkg` (`applicationDrmType` standard) and `free.pkg` (free).
`tests/MkPFS.Parity/PS5PackageReadTests.cs` reads, verifies and unpacks all three;
`tests/MkPFS.Parity/FPKGPackageTests.cs` checks the FIH, CNT, generated entries and SI writers against them
and builds each tree with `FPKGBuilder`.

## Findings (2026-10-05)

1. **The oracle cannot extract its own packages.** In every case `ProsperoPackageExtractor` fails with
   "Inner mount metadata does not start with a PS5 PFS superblock". This holds at the pinned commit and at the previous
   one (`c28be59`). The reader reconstructs the inner mount only from raw blocks. The writer always
   Kraken-compresses the inner metadata region (`ProsperoPs5InnerImageAssembler`, `CompressPayload(metaPlain,
   storeRaw: false)`), even with `--raw`. The docs' build→extract round-trip claim does not hold, so the oracle's
   reader is not an independent check. MkPFS's own reader (FPKG plan F2) and a console test take its place.
2. **The RSA key reaches the image.** CNT+0x1000 is the header SHA3 digest RSA-encrypted with the
   PKG-metadata public key. CNT entries 0x0010 and 0x0020 wrap the passcode and EKPFS with RSA keys from
   `passcode.bin`. All three use random padding in the oracle. Only the detached `.metasig` file is
   unused.
3. **Raw mode still contains Kraken.** `--raw` stores file payloads verbatim, but the inner metadata
   region stays Kraken-compressed, so byte parity on raw packages still needs a Kraken encoder that
   matches the oracle for that region.
4. **Source restore works.** The oracle restores every fake-signed module. The only change it leaves
   behind is the generated `sce_sys/param.json` in the `hb_noparam*` cases, and that lands in `work/`
   only. MkPFS must not write it into the source.
5. Non-ASCII names build without error (`app_unicode`). Whether the console accepts them is unknown.
6. **Publishing Tools' naps format differs from LibProsperoPkg's.** Verified on SDK output: fidx and u2c
   sections are packed, the cblock section starts on an 8-byte boundary, u2c is a 24-bit base plus seven
   deltas per 8 ublocks (u2c[u] = first chunk starting at or after u × 256 KiB), and every file and the
   zero gap before the metadata are cut into ≤ 256 KiB chunks from their own start. LibProsperoPkg
   writes one cblock for the whole gap, a different u2c byte order, and throws past 255 cblocks.
   MkPFS's reader cannot map any LibProsperoPkg package (`naps layout covers 0x147040 of a 0x4A0000-byte
   inner mount`); a console using the same rules would fail the same way.
7. LibProsperoPkg does not write SHA3-256(FIH block) at CNT+0x460; Publishing Tools does. Every other
   digest checked by `PS5PackageVerifier` matches.
8. Publishing Tools stores compressed chunks as header-stripped Kraken newLZ (flags 0x22 in every block
   seen), decodable by the ported LibProsperoPkg decoder. It adds `sce_sys/pfs-version.dat` and
   `sce_sys/about/right.sprx` (identical to the one embedded in LibProsperoPkg), keeps changeinfo.xml as
   an inner file too, sets CNT DRM type 0x10 and adds license entries 0x0400/0x0401 and
   playgo-scenario.json (0x3000).
9. Publishing Tools rejects non-ASCII paths (`invalid attribute value dst_path`): `pack fpkg` must too.
10. Publishing Tools keeps `sce_sys/changeinfo/*` as inner files only (no CNT entry 0x1260), while
    LibProsperoPkg moves it to the CNT and out of the inner image.
11. LibProsperoPkg's generated param.json uses `contentVersion` `01.00` (Publishing Tools input uses
    `01.000.000`), CRLF line endings on Windows, and ignores `ApplicationType` (`hb_noparam_freemium`
    still says `free`).
12. Given the original fixture trees (plain ELF modules, no keystone), MkPFS's `FPKGSource` reproduces every
    inner file of the Publishing Tools packages byte for byte (`verify --source-dir`); only Publishing
    Tools' param.json rewrite and the GP5 wrapper's `playgo-languages/` payload differ.
13. LibProsperoPkg throws `IndexOutOfRangeException` on `app_sizes` with Kraken (the `app_sizes_kraken` case is
    expected to fail). Corrected 2026-10-06: the throw is not in the Kraken encoder but in
    `ProsperoNapsLayoutBuilder.BuildU2c`, which reads `first[8 * g]` for every u2c group while the layout has
    `(ublocks + 8) >> 3` groups; with 32 ublocks the last group starts past the end. MkPFS's port gives that group
    the terminator as base.
14. LibProsperoPkg's CblockInfo model splits fields one bit off: Publishing Tools' `uoff` is 19 bits and the
    length field 16 bits at bit 38 (docs/FORMATS.md §13), so the "clenEvenMinus1" low bit is really the
    top `uoff` bit. Its raw-chunk encodings (kde 4 / 0x1FFFE for full blocks) differ from the SDK's.

## Findings (2026-10-06)

15. Publishing Tools derives the CNT policy from param.json `applicationDrmType`: `standard` gives DRM type
    0x10, content flags 0x0A020000, CNT+0x9C = 1 and a debug RIF as `license.dat`/`license.info` (0x0400/0x0401);
    `free` gives 0, 0x02020000, 0 and no license entries (LibProsperoPkg's values). `freemium` is refused for an
    application (category 0). The GP5 wrapper forces `standard`.
16. The toolkit profile is plaintext: outer seed `PPRPLAIN-NOAUTH!`, no XTS, and an unencrypted
    `naps_meta_18.dat`. Its SI has no `pfsimage.xml` and carries records LibProsperoPkg does not write (`ftyp`,
    a second `ibcl`, `obcc`, `gitt`, `gith`); every record is reproduced by `PS5NapsMeta` (docs/FORMATS.md §13).
17. `playgo-hash-table.dat` is a `\x7FFLT` of the inner file path hashes; the entries LibProsperoPkg keeps as
    constants are the hashes of its fixed file set. Its single-chunk `playgo-chunk.dat` writes an all-languages
    mask where Publishing Tools sets the default language's bit.
18. CblockInfo bits 59–61 are the kind of the second Kraken sub-chunk (3 = sub literals), not a shuffle index.
    MkPFS's reader used the first sub-chunk's mode for both and decoded `app_sizes/single.pkg` metadata to wrong
    bytes without an error; fixed, and `verify` now compares every chunk with the SI's SHA3-256.
19. Publishing Tools encodes the DDS entries with its own BC7 encoder: same header and size as LibProsperoPkg's
    (and MkPFS's port), different blocks.
20. (F4b) `MkPFS.Build.FPKG.Prospero.ProsperoPackage` reproduces every successful case byte for byte
    (`tests/MkPFS.Parity/FPKGProsperoTests.cs`). LibProsperoPkg's naps differs from Publishing Tools' in layout
    too: its cblock section follows the u2c groups directly (Publishing Tools starts it on an 8-byte boundary).
