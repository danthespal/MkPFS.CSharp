# MkPFS.CSharp

[![CI](https://github.com/danthespal/MkPFS.CSharp/actions/workflows/ci.yml/badge.svg)](https://github.com/danthespal/MkPFS.CSharp/actions/workflows/ci.yml)
[![License: GPL-3.0](https://img.shields.io/badge/license-GPL--3.0-blue.svg)](LICENSE.md)

Build, check, and repair PS4/PS5 PFS game images (`.ffpfs`, `.ffpfsc`, `.exfat`) from the command line
or a desktop app.

MkPFS.CSharp is a .NET 10 port of the Python [MkPFS](https://github.com/PSBrew/MkPFS) by PSBrew. It ships
as a native executable (no Python needed), writes the same images as the original, and adds an
offline PFSC block repair ported from PS5 Game Compressor.

> [!WARNING]
> **AMPR packing (`ampr`, the AMPR Packs page) is experimental.** It can shrink a game a lot, but whether
> the packed game runs depends on how that game reads its files, and only a test on the PS5 shows that.
> Keep the original game until the packed one has been played. See
> [AMPR packing: what it is and why it is experimental](#ampr-packing-what-it-is-and-why-it-is-experimental).

## Features

- **Pack**: a game folder, an exFAT image, or any single file into a compressed `.ffpfsc` (PFSC, zlib),
  or a folder into a plain PFS image (`--raw`, with signed, encrypted, 64-bit inode, and PS4 options).
- **exFAT**: build exFAT images from game folders (deterministic, 64 KiB clusters by default).
- **Batch**: pack every game folder and image in a folder in one run.
- **Check**: `verify`, `inspect`, `tree`, and `unpack` for PFS, PFSC, and exFAT images, including
  encrypted ones (`--ekpfs-key`).
- **Repair**: find and fix compressed blocks the PS5 may decode wrongly (images made with ISA-L).
- **APR Emu**: copies your AMPR Emu libraries into `fakelib/` of APR titles (`--ampr-libs`) and builds
  `ampr_emu.index` when `pack folder`, `pack exfat`, or `batch` packs a game that has them.
- **AMPR packs (experimental)**: `ampr` builds, checks, and extracts AMPR Emu seekable LZ4 asset packs, byte for byte
  like ampr_emu's `ampr_pack.py`, and `ampr game` turns a game folder into a smaller one that runs from
  them ([what it is and its limits](#ampr-packing-what-it-is-and-why-it-is-experimental)).
- **GUI**: `mkpfs-gui` with a page per command, cover and metadata preview, batch queue, and a PFSC
  block map; English, Português (BR), Español, Română, Deutsch, and Français.

## Download

Get the archive for your system from the
[releases page](https://github.com/danthespal/MkPFS.CSharp/releases) and unpack it anywhere:

| System | Command line | Desktop app |
|---|---|---|
| Windows x64 | `mkpfs-<version>-win-x64.zip` | `mkpfs-gui-<version>-win-x64.zip` |
| Linux x64 | `mkpfs-<version>-linux-x64.tar.gz` | `mkpfs-gui-<version>-linux-x64.tar.gz` |
| macOS Apple silicon | `mkpfs-<version>-osx-arm64.tar.gz` | `mkpfs-gui-<version>-osx-arm64.tar.gz` |

- Keep each program in its folder with the libraries next to it (`mkpfs_zlib`, plus Skia and
  HarfBuzz for the desktop app).
- macOS: the desktop app is `MkPFS.CSharp.app`. It is not notarized, so open it the first time with
  right-click > Open.
- Linux: the desktop app needs X11 and fontconfig, which desktop distributions include.
- `SHA256SUMS.txt` on the release page lists the archive checksums.
- Check the command line with `mkpfs selftest`.

## Usage

All paths may be absolute or relative to the current directory. Replace values in angle brackets
with your own paths; square brackets indicate an optional argument. Run `mkpfs <command> --help`
for the parser's built-in help.

### Commands at a glance

| Command | Arguments | Default result | Purpose |
|---|---|---|---|
| `pack folder <source_dir> <image_file>` | game/homebrew folder, output path | exFAT wrapped in a compressed `.ffpfsc` | Package a game folder. |
| `pack file <source_file> <image_file>` | input file, output path | compressed `.ffpfsc` | Package one file in a PFS container. |
| `pack exfat <source_dir> [output]` | game/homebrew folder, optional output path | `<titleId>.exfat` beside the source | Build an uncompressed exFAT image. |
| `batch <source_dir> <output_dir>` | directory of folders/images, destination directory | one `.ffpfsc` per discovered item | Package many inputs; existing outputs are skipped. |
| `verify <image_file>` | image path | — | Validate an image, optionally against its source. |
| `inspect <image_file>` | image path | text report | Show image metadata and integrity information. |
| `tree <image_file>` | folder or image path | outer tree | List files and directories. |
| `unpack <image_file> <output_dir>` | image path, destination directory | — | Extract an image. |
| `repair <image_file>` | single-file `.ffpfsc` path | repairs risky blocks | Repair PFSC blocks that a PS5 may decode incorrectly. |
| `ampr <subcommand>` | see [`ampr`](#ampr-asset-packs) | JSON on standard output | Build and manage AMPR Emu LZ4 asset packs. |

### Common examples

Pack a game folder into a `.ffpfsc` (wrapped in exFAT and compressed in one pass):

```bash
mkpfs pack folder PPSA12345-app PPSA12345.ffpfsc
```

Compress an existing exFAT image:

```bash
mkpfs pack file PPSA12345.exfat PPSA12345.ffpfsc
```

Pack a folder directly as PFS (`--signed`, `--encrypted`, `--inode-bits 64`, and `--version PS4` apply
here):

```bash
mkpfs pack folder PPSA12345-app PPSA12345.ffpfs --raw
```

Build an exFAT image from a game folder:

```bash
mkpfs pack exfat PPSA12345-app PPSA12345.exfat
```

Pack every game folder and image file in a folder (existing outputs are skipped):

```bash
mkpfs batch ./games ./output
```

Verify an image against its source:

```bash
mkpfs verify PPSA12345.ffpfsc --source-file PPSA12345.exfat
```

Show image details (`--format json` for scripts):

```bash
mkpfs inspect PPSA12345.ffpfsc
```

List the files, including inside a wrapped exFAT:

```bash
mkpfs tree PPSA12345.ffpfsc --deep
```

Extract the game files:

```bash
mkpfs unpack PPSA12345.ffpfsc out --deep
```

Repair blocks the PS5 may decode wrongly in a single-file `.ffpfsc`:

```bash
mkpfs repair PPSA12345.ffpfsc
```

### `pack folder` and `pack file`

Both commands require a source path and an output image path. By default, the output extension is
changed to `.ffpfsc` when necessary. The default build is PS5, 32-bit inodes, case-insensitive,
zlib level 7, 64 KiB blocks, and compression enabled. Direct-PFS folder builds and single-file
builds also run a quick structure verification by default. The default exFAT-wrapped folder flow
runs a post-pack check only when `--verify` is supplied.

For the default exFAT-wrapped `pack folder` flow, compression remains enabled and the PFS block
size remains 64 KiB. Use `--raw` to make options that control direct PFS layout or compression
(`--no-compress`, `--block-size`, `--inode-bits`, `--max-compressed-ratio`,
`--min-compress-size`, and `--skip-executable-compression`) take effect.

| Option | Default | Meaning |
|---|---|---|
| `--adjust-output-file-extension` | on | Change the requested output extension to match the selected pack mode. |
| `--no-adjust-output-file-extension` | off | Keep the output filename exactly as supplied. Cannot be combined with `--adjust-output-file-extension`. |
| `--compress` / `--no-compress` | compression on | Enable or disable PFSC block compression. The two flags are mutually exclusive. |
| `--threshold-gain <0-100>` | `0` | Keep a compressed block only when it saves at least this percentage. |
| `--block-size <bytes\|auto\|auto-fit>` | `auto` (`65536`) | PFS block size; it must be a power of two from 4096 through 2097152. `auto-fit` is supported by folder packing and the spool path to reduce file-data padding. |
| `--temp-folder <dir>` | system temporary directory | Where staged pack artifacts are written. |
| `--version <PS4\|PS5>` | `PS5` | PFS profile version. |
| `--inode-bits <32\|64>` | `32` | PFS inode-width mode. |
| `--case-sensitive` / `--case-insensitive` | case-insensitive | Select the PFS name-comparison mode. The two flags are mutually exclusive. |
| `--cpu-count <n>` | `0` (automatic) | PFSC compression workers. Automatic mode uses up to 16 workers and leaves one logical CPU free; a nonzero value is clamped to at least one. |
| `--compression-level <0-9>` | `7` | zlib compression level. |
| `--compression-backend <auto\|zlib-ng\|zlib\|isal>` | `auto` | Compatibility option. This port always uses zlib 1.3.1; unsupported values produce a warning. |
| `--max-compressed-ratio <0-100>` | `100` | Do not use PFSC when its stored size exceeds this percentage of the raw file size. |
| `--min-compress-size <bytes>` | resolved block size | Store smaller files raw without attempting PFSC compression. |
| `--skip-executable-compression` | off | Do not compress important executable files. |
| `--signed` | off | Build a signed PFS using a zero EKPFS/seed. It is not supported by the default exFAT-wrapped folder mode. |
| `--encrypted` | off | Encrypt filesystem blocks with AES-XTS. |
| `--ekpfs-key <64-hex>` | all-zero key | EKPFS key for an encrypted image; it requires `--encrypted`. |
| `--verbose` | off | Print per-file decisions. |
| `--dry-run` | off | Scan and report the planned layout without writing an image. |
| `--verify` | off | Run full post-pack verification instead of the default structure-only check. |
| `--verify-structure` / `--no-verify-structure` | structure check on | Explicitly enable or disable the default quick post-pack check. The two flags are mutually exclusive. |
| `--skip-verification` | off | Skip all post-pack verification. It cannot be combined with `--verify`. |

`pack folder` also accepts the [APR Emu options](#apr-emu) and the following:

| Option | Default | Meaning |
|---|---|---|
| `--raw` | off | Package the source directly as a PFS `.ffpfs`, rather than making the default exFAT-wrapped `.ffpfsc`. Use this mode for `--signed`, `--inode-bits 64`, and other direct-PFS settings. |
| `--require-game-files` | off | Refuse to pack unless `sce_sys/param.json` and `eboot.bin` are present. |

`pack file` also accepts:

| Option | Default | Meaning |
|---|---|---|
| `--use-spool` | off | Force the legacy staged/spool builder instead of direct-to-image streaming. |
| `--rename-inner-image` / `--no-rename-inner-image` | rename on | Normalize the filename stored inside the image (the first flag explicitly selects the default), or preserve the source filename. |

### `pack exfat`

`mkpfs pack exfat <source_dir> [output]` creates an uncompressed exFAT image. If `output` is omitted,
the program derives `<titleId>.exfat` beside the source directory; an output directory is also
accepted and receives that derived filename.

| Option | Default | Meaning |
|---|---|---|
| `--cluster-size <bytes\|auto>` | `auto` (`65536`) | exFAT cluster size. The automatic 64 KiB value is optimized for SMP/LVD. |
| `--overwrite` | off | Replace an existing output image. |
| `--verbose` | off | Print detailed packing output. |
| `--no-progress` | off | Hide the progress bar written to standard error. |

It also accepts the [APR Emu options](#apr-emu).

### `batch`

`mkpfs batch <source_dir> <output_dir>` discovers packable folders and image files in `source_dir`
and writes `.ffpfsc` images into `output_dir`. It skips existing outputs by default. Its compression,
PFS-profile, naming, and encryption options have the same meanings and defaults as the corresponding
`pack` options: `--compress`/`--no-compress`, `--threshold-gain`, `--block-size` (`auto` = 65536;
`auto-fit` is not accepted), `--version`, `--inode-bits`, `--case-sensitive`/`--case-insensitive`,
`--cpu-count`, `--compression-level`, `--compression-backend`, `--max-compressed-ratio`,
`--min-compress-size`, `--skip-executable-compression`, `--encrypted`, `--ekpfs-key`, and `--verbose`.

| Option | Default | Meaning |
|---|---|---|
| `--overwrite` | off | Replace images that already exist in the output directory. |
| `--dry-run` | off | Report the conversions without writing images. |
| `--verify` | off | Run full verification for each successful image. |
| `--compress` / `--no-compress` | compression on | Enable or disable compression; these flags are mutually exclusive. |

Folder items get the [APR Emu options](#apr-emu) too.

### APR Emu

Some PS5 titles use PlayGo/APR and need Drakmor's APR Emu to run from a mounted image: the emulator
libraries in the game's `fakelib/` folder plus an `ampr_emu.index` that lists every file. MkPFS does
not ship or download the libraries. Download them into one folder and pass it with `--ampr-libs`:

| Library | Download | Required |
|---|---|---|
| `libSceAmpr.sprx` | [drakmor/ampr_emu releases](https://github.com/drakmor/ampr_emu/releases) | yes |
| `libScePlayGo.sprx` | [drakmor/pgo_stub releases](https://github.com/drakmor/pgo_stub/releases) | copied when present |

Before packing a game folder, `pack folder`, `pack exfat`, and `batch` (folder items):

1. With `--ampr-libs`, copy the libraries into `<game>/fakelib/` when the game is an APR title
   (`sce_sys/playgo-chunk.dat` exists) or `--ampr-title` is given. Identical files are left alone;
   changed ones are replaced.
2. When `fakelib/libSceAmpr.sprx` or `fakelib2/libSceAmpr.sprx` exists, write `ampr_emu.index` into the
   game folder. Paths are hashed and sorted the way the emulator looks them up (only ASCII letters fold
   case), so names with accents resolve on the console.

ShadowMountPlus mounts `fakelib2/` instead of `fakelib/` when both exist, so `--ampr-libs` warns and leaves
a `fakelib2/` folder for you to update.

Both steps change the source folder, so the image includes them. An APR title without
`fakelib/libSceAmpr.sprx` and without `--ampr-libs` gets a warning and no index.

A folder that already has `ampr_emu.index` gets it rebuilt by default. To keep an index made by other
tools, pass `--ampr-skip-regen-if-exists`: the index is kept while it lists exactly the folder's files
and sizes (paths compared case-insensitively, modification times ignored) and rebuilt otherwise. The GUI
turns this on when the chosen folder already has an index. `--no-ampr-index` packs the existing index
untouched, even when it no longer matches.

A folder that also holds AMPR packs (`ampr_assets.index`, see [`ampr`](#ampr-asset-packs)) always keeps its
`ampr_emu.index`, with a warning: the packs address files by their row in that index, and a rebuilt index
renumbers the rows so the emulator fails every packed read. `--ampr-force-regen` still rebuilds it. Keep
ShadowMountPlus's `mount_read_only=1` (its default) for such images: on a writable mount the emulator can
rebuild a missing index from the remaining files, with the same result.

| Option | Default | Meaning |
|---|---|---|
| `--ampr-libs <dir>` | none | Folder holding `libSceAmpr.sprx` (required) and `libScePlayGo.sprx` (optional) to copy into `fakelib/`. |
| `--ampr-title` | off | With `--ampr-libs`, add the libraries even without `sce_sys/playgo-chunk.dat`. |
| `--no-ampr-index` | off | Do not create `ampr_emu.index` when `fakelib/libSceAmpr.sprx` is present. |
| `--ampr-skip-regen-if-exists` | off | Keep an existing index while it lists exactly the folder's files and sizes; rebuild it otherwise. |
| `--ampr-force-regen` | off | Regenerate an existing AMPR index. |

### `ampr` (asset packs)

#### AMPR packing: what it is and why it is experimental

> [!WARNING]
> Experimental. Only a few games have been tested. Keep the original, unpacked game until the packed one
> has been played on the PS5, and report the games you try (see [Reporting a game](#reporting-a-game)).

**What it does.** Most of a game's size is data files: levels, textures, audio, video. `ampr` compresses
selected data files with LZ4 into a few large pack files (`ampr_assets-*.pak`) plus a manifest
(`ampr_assets.index`). On the PS5, Drakmor's [AMPR Emu](https://github.com/drakmor/ampr_emu) (the
`libSceAmpr.sprx` in the game's `fakelib/`) sits between the game and the file system: when the game
opens a packed file, AMPR Emu reads the matching blocks from the packs and decompresses them, so the game
sees the original bytes. Nothing else changes: `eboot.bin`, modules and `sce_sys` stay as they are, and
the PS5 kernel never sees LZ4. Packed files are not stored a second time, which is where the space goes.

**What a game needs.**

- A jailbroken PS5 running [ShadowMountPlus](https://github.com/drakmor/ShadowMountPlus), which mounts
  the game's `fakelib/` (or `fakelib2/`) into the running game.
- A game that loads `libSceAmpr` (AMPR/APR titles; these carry `fakelib/libSceAmpr.sprx` once set up for
  AMPR Emu). AMPR Emu is only loaded by such games, so packs in any other game are never read.
- AMPR Emu **0.4.2.1 or newer**: the first release that reads packs. `ampr game` checks the library and
  refuses older ones.
- The image or folder mounted read-only (ShadowMountPlus `mount_read_only=1`, the default), so AMPR Emu
  never rebuilds `ampr_emu.index`.

**Why it is experimental.** AMPR Emu serves packed files only to the ways of reading files it intercepts:
AMPR reads, `open`/`sceKernelOpen`, `read`/`pread`, `stat`/`fstat`, directory listing, and asynchronous
reads. A game that reads a file any other way, most likely by memory-mapping it (`mmap`), gets nothing
back and usually stops at startup. Which files a game reads which way cannot be seen from the files on a
PC, so the only real check is to start the packed game on the console. `ampr game` verifies on the PC
that every packed byte decodes to the original, but that proves the packs are correct, not that the game
reads them in a supported way.

The built-in rules come from these tests, and `ampr game` picks them from the game itself (`--preset
auto`, the default; the log line `Rules: ...` says which and why):

- **Unity games** (a `globalgamemanagers`, `data.unity3d` or `global-metadata.dat` in the game): only
  `StreamingAssets/` is packed (asset bundles, audio banks, videos). Unity's own data files
  (`level*`, `sharedassets*`, `globalgamemanagers`, `.resS`) stay loose, because packing them made a
  Unity game abort at startup.
- **Other games**: every file except executables, modules and system files is packed. No game of this
  kind has been confirmed on the console yet.

**Tested games.** Results on a PS5 with ShadowMountPlus 1.7 beta 4 and AMPR Emu 0.4.2.1:

| Game | Version | Engine | Rules | Result |
|---|---|---|---|---|
| God of War Sons of Sparta (PPSA28997) | 01.008.001 | Unity (IL2CPP) | `unity` (auto) | Runs: menu, saves, gameplay. |
| God of War Sons of Sparta (PPSA28997) | 01.008.001 | Unity (IL2CPP) | `default` | Aborts at startup (`SYSTEM_ABNORMAL_TERMINATION_REQUEST`). |

**Recommended steps.**

1. Make sure the unpacked game runs with AMPR Emu 0.4.2.1 in its `fakelib/`.
2. On the AMPR Packs page, choose **Build playable game** (the default), pick the game folder, a new
   output folder and the folder with AMPR Emu's `libSceAmpr.sprx`, and leave the TOML field empty.
   Optionally tick the exFAT image box. On the command line:
   `mkpfs ampr game --root <game> --output <new folder> --fakelib <AMPR Emu folder> --exfat .`
3. Copy the output folder or the `.exfat` image to the PS5 and start the game. Play past the menu and load
   a save or a level: some files are only read later.
4. Delete the original only after that.

##### Reporting a game

Whether it works or not, please report: the game title, ID and version, the `Rules:` line from the build
log, what happened on the console, and for a game that fails, the ShadowMountPlus log
(`/data/shadowmount/debug.log`) and the console log around the crash. A failing game can often still be
packed with a TOML file that leaves more files loose (see the TOML format below); a report with the result
of such a test lets the built-in rules learn that game.

#### How the packs are made

`mkpfs ampr` is a port of ampr_emu's `tools/ampr_pack.py` (tool version 4.0): the same options, the same
JSON on standard output, and byte-identical packs. A pack set is the manifest `ampr_assets.index`, data
volumes `ampr_assets-*.pak`, optional runtime settings `ampr_assets.index.runtime`, and an offline CRC
sidecar `ampr_assets.index.crc`.

`pack` writes only the pack set (`ampr_assets.index`, `.pak` volumes, `.crc`) into `--output`, exactly
like `ampr_pack.py`. For a folder to copy to the PS5, use [`ampr game`](#ampr-game-a-folder-to-copy-to-the-ps5).

The default emulator build loads at most 2,000,000 files, 16,000,000 chunks, and 1,024 volumes, and
rejects the whole set beyond that; `pack` warns on standard error when a set exceeds a limit (larger
blocks or more loose files bring it down). Deploy `ampr_emu.index`, the manifest, its `.runtime`, and every
volume from one build together; the `.crc` sidecar is only for `verify` and `unpack`.

#### Command reference

| Subcommand | Required options | Purpose |
|---|---|---|
| `game` | `--root <app0> --output <dir>` | Build a folder that runs from packs as is (see below; not in `ampr_pack.py`). |
| `pack` | `--root <app0> --ampr-index <ampr_emu.index> --output <dir>` | Build the manifest, volumes, CRC sidecar, and optional runtime settings. |
| `verify` | `--index <manifest>` | Decode every chunk and check its CRC; `--root <app0>` also compares every byte with the source. |
| `unpack` | `--index <manifest> --output <dir>` | Extract packed files (`--file <glob>`, `--overwrite`, `--no-preserve-mtime`). |
| `list` | `--index <manifest>` | One line per file (`--json` for details). |
| `inspect` | `--index <manifest>` | Manifest summary, volumes, and runtime settings. |
| `runtime-config` | `--index <manifest> --config <toml>` | Replace `<manifest>.runtime` from a `[runtime]` section without repacking. |
| `remove-sources` | `--index <manifest> --root <app0>` | Show which sources the packs replace; with `--confirm`, verify everything and delete them. Also `remove-packed-sources`. |

`pack` options:

| Option | Default | Meaning |
|---|---|---|
| `--config <toml>` | none | Pack rules. Without a config or preset every file stays loose, and `pack` prints a warning on standard error. |
| `--preset default` | none | Built-in rules instead of `--config` (not in `ampr_pack.py`): compress every file but keep loose everything `remove-sources` protects (`eboot.bin`, `*.prx`/`*.sprx`/`*.elf`/`*.self`, `sce_sys/`, `sce_module/`, `fakelib/`, `fakelib2/`, `mods/`, `save/`, `system/`, `param.sfo`, `nptitle.dat`, `ampr_emu.index`) and Unity IL2CPP `global-metadata.dat`, which is memory-mapped. |
| `--preset unity` | none | For Unity games: compress only `StreamingAssets/` (Addressables bundles, FMOD banks, videos) and keep Unity's own data files (`level*`, `sharedassets*`, `globalgamemanagers`, `.resS`) loose. Packing those made a Unity title abort at startup on the console, while packing only `StreamingAssets` ran normally. |
| `--preset auto` | none | `unity` for Unity games (a `globalgamemanagers`, `data.unity3d` or `global-metadata.dat` within four folder levels), else `default`; prints the choice on standard error. The default for `ampr game` and the AMPR Packs page. |
| `--include <glob>`, `--exclude <glob>` | none; repeatable | Narrow the rule selection; `--exclude` forces files loose. |
| `--include-from <file>`, `--exclude-from <file>` | none | Glob lists, one per line, `#` comments. |
| `--workers <n>` | config, else min(8, cores) | Compression threads (1 to 256). |
| `--self-contained` | off | Never auto-loose selected files; incompressible blocks are stored uncompressed. |
| `--require-packed <glob>` | none; repeatable | Fail if a matching file would stay loose. |
| `--allow-missing` | off | Leave selected files that are missing from `--root` loose. |
| `--no-progress` | off | Hide progress on standard error. |

#### `ampr game`: a folder to copy to the PS5

`ampr game` turns an unpacked game folder into a new folder that runs from the packs as is. The game
folder is only read, and the output folder must be new or empty:

```bash
mkpfs ampr game --root PPSA12345-app --output PPSA12345-packed --fakelib ampr-emu-libs --exfat .
```

1. **Libraries.** Copies the game's `fakelib/` into the output, then every file of `--fakelib` over it.
   Identical files are left alone, and the log says which ones were added or replaced. If the game has
   `fakelib2/`, ShadowMountPlus mounts that one instead of `fakelib/`, so the libraries go there. The
   resulting `libSceAmpr.sprx` must be AMPR Emu 0.4.2.1 or newer (the first release that reads packs; its
   [release](https://github.com/drakmor/ampr_emu/releases) `libSceAmpr.sprx` does). Older builds are
   rejected before anything is written.
2. **Index.** Writes `ampr_emu.index` for the final tree: the game's files plus the new libraries with
   their real sizes. This must come before packing, because the manifest addresses files by their row
   in this index.
3. **Packs.** Packs from the game folder with `--config`, `--preset`, or by default `--preset auto`, which
   picks the rules from the game: `unity` when a Unity file (`globalgamemanagers`, `data.unity3d`,
   `global-metadata.dat`) is within four folder levels, else `default`. The log names the rules and the
   file that decided them.
4. **Loose files.** Copies every file that stays loose, keeping its modification time, and every folder,
   including empty ones. This runs after packing because the packer only then decides which large,
   incompressible files to leave loose.
5. **Checks.** Decodes every chunk, compares every packed file with the game folder, and checks that every
   loose file is in the output with its indexed size (`--skip-verify` skips this).

If a step fails, the output folder is emptied so a retry starts clean. `--exfat <file or folder>` then
builds an exFAT image of the output (64 KiB clusters, `<titleId>.exfat` in a folder). Use an exFAT
image or the plain folder, and keep ShadowMountPlus's `mount_read_only=1` (the default) so AMPR Emu
never rebuilds the index. A `.ffpfsc` would put zlib (about 150–250 MB/s on the PS5) on top of the LZ4
packs. The output also keeps `ampr_assets.index.crc`: the game never reads it, and `verify` and
`unpack` need it.

The TOML format is ampr_emu's (see its `tools/ampr_pack.example.toml`). A minimal config:

```toml
[pack]
default_action = "compress"   # LZ4 HC level 12, 64 KiB blocks

[[rule]]                       # last match wins: keep modules and system files loose
action = "loose"
include = ["eboot.bin", "**/*.prx", "**/*.sprx", "sce_sys/**", "sce_module/**", "fakelib/**"]
```

`ampr_pack.py` has no built-in exclusions, so a config without that last rule packs `eboot.bin` and
modules too; `remove-sources` then refuses to run. Errors print `error: <message>` and exit with code 2.

### Reading and extracting images

Encrypted read commands accept `--ekpfs-key <64-hex>` (default: all-zero key) and `--new-crypt`
(default: off) to select the alternate EKPFS derivation. `verify`, `tree`, and `unpack` also take
`--format <auto|pfs|exfat>`; the default `auto` detects the format from the input.

| Command | Options | Default | Meaning |
|---|---|---|---|
| `inspect <image_file>` | `--format <text\|json>` | `text` | Select a human-readable or JSON metadata report. |
| `tree <image_file>` | `--deep` | off | For a PFS that wraps one exFAT image, list the files inside that exFAT. |
| `unpack <image_file> <output_dir>` | `--overwrite` | off | Replace an existing output path. |
|  | `--deep` | off | Extract files from an inner exFAT image instead of only the outer PFS contents. |
|  | `--only <inner-path>` | none; repeatable | With `--deep`, extract only the named inner exFAT file or directory. |
|  | `--no-progress` | off | Hide extraction progress on standard error. |
| `verify <image_file>` | `--source-dir <dir>` | none | Compare hierarchy and payloads against a source folder. Cannot be combined with `--source-file`. |
|  | `--source-file <file>` | none | Compare a single-file image to the source file; not supported for exFAT input. |
|  | `--expect-crc32 <hex>` | none | Require this cumulative payload CRC32. |
|  | `--expect-manifest-sha256 <64-hex>` | none | Require this manifest SHA256 digest. |
|  | `--require-game-files` | off | Warn when `sce_sys/param.json`, `eboot.bin`, or `pfs-version.dat` is missing. |

### `repair`

`repair` operates on an unsigned, unencrypted, single-file `.ffpfsc`. By default it scans for risky
compressed blocks, stores replacements raw, verifies their decoded content, and cleans unused bytes
in the outer PFS wrapper.

| Option | Default | Meaning |
|---|---|---|
| `--scan` | off | Report only; do not modify the image. Returns exit code 3 if blocks need repair. |
| `--bad-blocks <file>` | none | Repair the block numbers in a PS5 Game Compressor `bad_blocks.tsv` instead of the scan's risky-block selection. |
| `--recompress` | off | Re-encode repaired blocks with zlib level 7 instead of storing them raw. |
| `--mode <auto\|in-place\|copy>` | `auto` | `auto` copy-replaces when free space is at least 1.2× the image, otherwise rewrites in place. `in-place` can leave an interrupted image corrupt; `copy` always uses a replacement copy. |
| `--report-dir <dir>` | none | Write `summary.json` and `bad_blocks.tsv` to this directory. |
| `--no-slack-cleanup` | off | Leave unused bytes in the outer PFS wrapper unchanged. |
| `--cpu-count <n>` | `0` (all cores) | Worker count for scanning and repair. |
| `--no-progress` | off | Hide progress output. |

Exit code `0` means success, `1` means an operation failed, `2` means invalid command-line usage,
and `repair --scan` uses `3` when it finds blocks that need repair.

### GUI

`mkpfs-gui` runs the same commands from a window and shows their output and progress. From a
source checkout:

```bash
dotnet run --project src/MkPFS.Gui -c Release
```

- Pick a game folder or image to see its cover, title, IDs, version, region, and APR Emu marker.
- The Batch page lists every item it will pack before you run it.
- Pack Folder, Pack exFAT, and Batch have an APR Emu section: the libraries folder, download links,
  and every [APR Emu option](#apr-emu).
- The packing pages have a collapsed Advanced section with the remaining CLI options: raw PFS, PFS
  version, inode size, case sensitivity, encryption and EKPFS key, verification, cluster size, and
  verbose output.
- The check pages cover the CLI options too: Verify takes a source file, the image format, and the
  game-file checklist; Unpack extracts inside a wrapped exFAT (`--deep`, `--only`); Tree and Unpack
  take the image format; Inspect, Tree, Verify, and Unpack take newCrypt keys.
- Closing the window while a job runs asks first; Stop and Close cancels the job and waits for its
  cleanup (an in-place repair finishes its rewrite) before the window closes.
- Pack File, Pack Folder, and Batch have compression presets (Fast, Balanced, Max, Low RAM) and
  settings for the zlib level, CPU cores, block size, and when to keep blocks uncompressed.
- The Repair page scans an image and draws a block map (zlib, raw, risky); click a cell for its
  offset, stored size, and largest back-reference distance.
- The AMPR Packs page runs every `ampr` subcommand and shows only the fields the chosen action needs. Its
  default action, Build playable game, runs `ampr game` with a library folder and an optional exFAT image.
  Without a TOML file, the rules are picked from the game (Unity or not), so there is nothing to choose.
  Packing without a TOML uses `--preset default`.

## Differences from Python MkPFS

- Same inputs give the same images as Python MkPFS 1.0.0 run with its zlib backend. Set
  `SOURCE_DATE_EPOCH` for reproducible timestamps.
- Compression always uses zlib 1.3.1 at level 7. `--compression-backend` is accepted but ignored:
  ISA-L output uses back-references the PS5 decodes wrongly.
- `repair` is new.
- `pack exfat` and `batch` also build `ampr_emu.index` (Python only does it in `pack folder`), and
  `--ampr-libs`/`--ampr-title` are new. `--ampr-skip-regen-if-exists` checks every path and size, not
  just the file count.
- `ampr_emu.index` matches ampr_emu's `build_ampr_index.py` and the console lookup: Python MkPFS folds
  every letter and hashes code points, so on the console it cannot find files with non-ASCII names. The
  index also skips the emulator's `ampr_commands.bin` and `apr_emu.log`, and `fakelib2/libSceAmpr.sprx`
  also triggers it. ASCII-only folders give the same index as before.
- Bugs found in the Python version while porting are listed in
  [tools/oracle/README.md](tools/oracle/README.md); some are fixed here.
- Switching from Python MkPFS: see [MIGRATION.md](MIGRATION.md).

## Build from source

Requirements:

- .NET SDK 10.0.401 or a newer 10.0.4xx (see `global.json`).
- Windows: Visual Studio 2026 with "Desktop development with C++" (native zlib and Native AOT).
- Linux and macOS: CMake and a C compiler; Linux Native AOT also needs `clang` and `zlib1g-dev`.

Build and test:

```bash
dotnet build MkPFS.slnx
```

```bash
dotnet test --solution MkPFS.slnx
```

The first build compiles the bundled zlib in `native/`. Parity tests skip when the oracle corpus
(`tests/fixtures/generated`) is missing.

Publish native executables:

```bash
dotnet publish src/MkPFS.Cli -r win-x64 -c Release
```

```bash
dotnet publish src/MkPFS.Gui -r win-x64 -c Release
```

Use `linux-x64` or `osx-arm64` on those systems. On Windows, Native AOT linking needs the Visual
Studio Installer folder (`C:\Program Files (x86)\Microsoft Visual Studio\Installer`) on `PATH`.

### Project layout

| Path | Content |
|---|---|
| `src/MkPFS.Core` | Formats and codecs: PFS, PFSC, exFAT, AMPR, crypto, readers, validators |
| `src/MkPFS.Build` | Image builders, compression planner, batch |
| `src/MkPFS.Repair` | PFSC repair (Game Compressor port) |
| `src/MkPFS.Cli` | `mkpfs` command line |
| `src/MkPFS.Gui` | `mkpfs-gui` desktop app (Avalonia) |
| `native/` | zlib 1.3.1, lz4 1.9.4, and the `mkpfs_zlib` shim |
| `tests/MkPFS.Tests` | Unit tests |
| `tests/MkPFS.Parity` | Byte-for-byte tests against the Python oracle corpus |
| `tests/MkPFS.Gui.Tests` | GUI view model and headless UI tests |
| `tools/oracle` | Python oracle scripts ([README](tools/oracle/README.md)) |

### Oracle corpus

The parity tests compare against images and logs made by Python MkPFS. With Python MkPFS checked
out at `../MkPFS` and `uv` installed:

```bash
uv run --project ../MkPFS python tools/oracle/build_goldens.py --check
```

The `ampr` tests use a second corpus made by ampr_emu's `ampr_pack.py` (checked out at `../ampr_emu`;
needs python-lz4 4.4.5):

```bash
python tools/oracle/build_ampr_goldens.py --check
```

### Releases

Push a version tag to publish a release. The workflow reruns CI, then uploads the archives,
`SHA256SUMS.txt`, and release notes, and marks the release as latest. With a `VT_API_KEY` repository
secret (a VirusTotal API key), it also scans every archive and links the reports in the notes.

```bash
git tag v2.0.0
```

```bash
git push origin v2.0.0
```

## Credits

- [MkPFS](https://github.com/PSBrew/MkPFS) by PSBrew: the Python original this port follows.
- PS5 Game Compressor by Juma Sayeh: the PFSC repair logic.
- Drakmor's [APR Emu](https://github.com/drakmor/ampr_emu): the `ampr_emu.index` format and the asset-pack
  format and tools that `ampr` ports.
- Drakmor's [PlayGo stub](https://github.com/drakmor/pgo_stub): `libScePlayGo.sprx`.

Third-party components:

- [zlib](https://zlib.net) 1.3.1 (zlib license)
- [LZ4](https://github.com/lz4/lz4) 1.9.4 (BSD-2-Clause)
- [Tomlyn](https://github.com/xoofx/Tomlyn) (BSD-2-Clause)
- [Avalonia](https://avaloniaui.net), [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet),
  [System.CommandLine](https://github.com/dotnet/command-line-api), and
  [Spectre.Console](https://spectreconsole.net) (MIT)
- [Material Design Icons](https://pictogrammers.com/library/mdi/) (Apache-2.0)

## License

GPL-3.0-only, same as MkPFS. See [LICENSE.md](LICENSE.md).

This project is not affiliated with Sony Interactive Entertainment.
