# MkPFS.C#

[![CI](https://github.com/danthespal/MkPFS.CSharp/actions/workflows/ci.yml/badge.svg)](https://github.com/danthespal/MkPFS.CSharp/actions/workflows/ci.yml)
[![License: GPL-3.0](https://img.shields.io/badge/license-GPL--3.0-blue.svg)](LICENSE.md)

Build, check, and repair PS4/PS5 PFS game images (`.ffpfs`, `.ffpfsc`, `.exfat`) from the command line
or a desktop app.

MkPFS.C# is a .NET 10 port of the Python [MkPFS](https://github.com/PSBrew/MkPFS) by PSBrew. It ships
as a native executable (no Python needed), writes the same images as the original, and adds an
offline PFSC block repair ported from PS5 Game Compressor.

## Features

- **Pack**: a game folder, an exFAT image, or any single file into a compressed `.ffpfsc` (PFSC, zlib),
  or a folder into a plain PFS image (`--raw`, with signed, encrypted, 64-bit inode, and PS4 options).
- **exFAT**: build exFAT images from game folders (deterministic, 64 KiB clusters by default).
- **Batch**: pack every game folder and image in a folder in one run.
- **Check**: `verify`, `inspect`, `tree`, and `unpack` for PFS, PFSC, and exFAT images, including
  encrypted ones (`--ekpfs-key`).
- **Repair**: find and fix compressed blocks the PS5 may decode wrongly (images made with ISA-L).
- **APR Emu**: `ampr_emu.index` is created or refreshed automatically for games that use it.
- **GUI**: `mkpfs-gui` with a page per command, cover and metadata preview, batch queue, and a PFSC
  block map; English, Português (BR), and Español.

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
- macOS: the desktop app is `MkPFS.C#.app`. It is not notarized, so open it the first time with
  right-click > Open.
- Linux: the desktop app needs X11 and fontconfig, which desktop distributions include.
- `SHA256SUMS.txt` on the release page lists the archive checksums.
- Check the command line with `mkpfs selftest`.

## Usage

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

- `--scan` only reports (exit code 3 when blocks need repair).
- Repaired blocks are stored raw (or re-encoded with zlib with `--recompress`); every block is then
  decoded again and compared with its content before the repair.
- `--bad-blocks bad_blocks.tsv` repairs the blocks PS5 Game Compressor measured on the console.
- `--mode auto` writes a copy and swaps it in when free space is at least 1.2× the image; otherwise
  it rewrites in place, which corrupts the image if interrupted.

Run `mkpfs <command> --help` for every option. Encrypted images take `--ekpfs-key <64 hex>` (and
`--new-crypt` for the alternate key derivation).

### GUI

`mkpfs-gui` runs the same commands from a window and shows their output and progress. From a
source checkout:

```bash
dotnet run --project src/MkPFS.Gui -c Release
```

- Pick a game folder or image to see its cover, title, IDs, version, region, and APR Emu marker.
- The Batch page lists every item it will pack before you run it.
- Pack File, Pack Folder, and Batch have compression presets (Fast, Balanced, Max, Low RAM) and
  settings for the zlib level, CPU cores, block size, and when to keep blocks uncompressed.
- The Repair page scans an image and draws a block map (zlib, raw, risky); click a cell for its
  offset, stored size, and largest back-reference distance.

## Differences from Python MkPFS

- Same inputs give the same images as Python MkPFS 1.0.0 run with its zlib backend. Set
  `SOURCE_DATE_EPOCH` for reproducible timestamps.
- Compression always uses zlib 1.3.1 at level 7. `--compression-backend` is accepted but ignored:
  ISA-L output uses back-references the PS5 decodes wrongly.
- `repair` is new.
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
| `native/` | zlib 1.3.1 and the `mkpfs_zlib` shim |
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
- Drakmor's [APR Emu](https://github.com/drakmor/ampr_emu): the `ampr_emu.index` format.

Third-party components:

- [zlib](https://zlib.net) 1.3.1 (zlib license)
- [Avalonia](https://avaloniaui.net), [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet),
  [System.CommandLine](https://github.com/dotnet/command-line-api), and
  [Spectre.Console](https://spectreconsole.net) (MIT)
- [Material Design Icons](https://pictogrammers.com/library/mdi/) (Apache-2.0)

## License

GPL-3.0-only, same as MkPFS. See [LICENSE.md](LICENSE.md).

This project is not affiliated with Sony Interactive Entertainment.
