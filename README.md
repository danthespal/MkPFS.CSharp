# MkPFS.C#

.NET 10 port of [MkPFS](https://github.com/PSBrew/MkPFS): build, verify, inspect and unpack PS4/PS5
PFS images (`.ffpfs`, `.ffpfsc`, `.exfat`), plus PFSC block repair ported from PS5 Game Compressor.

Status: read side (`inspect`, `tree`, `unpack`, `verify`), `repair` and every `pack` mode (with the AMPR
emulation index) done; `batch` is next.

## Usage

Compress an exFAT image into a single-file `.ffpfsc` (zlib 1.3.1, structure check afterwards):

```bash
mkpfs pack file PPSA12345.exfat PPSA12345.ffpfsc
```

Pack a game folder into a `.ffpfsc` (wrapped in an exFAT and compressed in one pass, no temp image):

```bash
mkpfs pack folder PPSA12345-app PPSA12345.ffpfsc
```

Pack a folder directly as PFS (`--signed`, `--encrypted`, `--inode-bits 64` and `--version PS4` apply here):

```bash
mkpfs pack folder PPSA12345-app PPSA12345.ffpfs --raw
```

Build an exFAT image from a game folder (64 KiB clusters by default):

```bash
mkpfs pack exfat PPSA12345-app PPSA12345.exfat
```

Inspect an image (text or `--format json`):

```bash
mkpfs inspect PPSA12345.ffpfsc
```

List the files, including inside a wrapped exFAT:

```bash
mkpfs tree PPSA12345.ffpfsc --deep
```

Verify structure and payloads against the source:

```bash
mkpfs verify PPSA12345.ffpfsc --source-file PPSA12345.exfat
```

`verify` also warns when compressed blocks use back-references the PS5 may decode wrongly (ISA-L
output) and points to `mkpfs repair`.

Extract the files inside the wrapped exFAT:

```bash
mkpfs unpack PPSA12345.ffpfsc out --deep
```

Encrypted images take `--ekpfs-key <64 hex>` (and `--new-crypt` for the alternate key derivation).

Find and fix PFSC blocks the PS5 may decode wrongly in a single-file `.ffpfsc` (ISA-L output):

```bash
mkpfs repair PPSA12345.ffpfsc
```

- Marked blocks are stored raw (or re-encoded with zlib via `--recompress`), then every block is
  decoded again and compared with its content before the repair.
- `--scan` reports only (exit code 3 when blocks need repair).
- `--bad-blocks bad_blocks.tsv` repairs the blocks PS5 Game Compressor measured on the console.
- `--mode auto` writes a copy and replaces the image when free space is at least 1.2x the image;
  otherwise it rewrites in place, which corrupts the image if interrupted.

## Layout

| Path | Content |
|---|---|
| `src/MkPFS.Core` | Formats and codecs: PFS, PFSC, exFAT, AMPR, crypto, readers, validators |
| `src/MkPFS.Build` | Image builders, compression planner, batch |
| `src/MkPFS.Repair` | PFSC repair (Game Compressor port) |
| `src/MkPFS.Cli` | `mkpfs` command line (System.CommandLine, Native AOT) |
| `native/` | zlib 1.3.1 sources + `mkpfs_zlib` shim, built into `artifacts/native/<rid>/` |
| `tests/MkPFS.Tests` | Unit tests |
| `tests/MkPFS.Parity` | Byte-for-byte tests against the Python oracle corpus |
| `tests/fixtures/generated` | Oracle corpus (git-ignored, regenerate below) |
| `tools/oracle` | Python oracle scripts ([README](tools/oracle/README.md)) |
| `docs/PLAN.md` | Port plan and phases (local only, git-ignored) |
| `docs/FORMATS.md` | On-disk format reference (local only, git-ignored) |

## Prerequisites

- .NET SDK 10.0.401 or newer 10.0.4xx (`global.json`).
- Windows: Visual Studio 2026 with "Desktop development with C++" (native zlib and Native AOT).
- Linux/macOS: CMake and a C compiler; Linux AOT also needs `clang` and `zlib1g-dev`.
- For the oracle: Python MkPFS checked out at `../MkPFS` and `uv`.

## Build and test

```bash
dotnet build MkPFS.slnx
```

```bash
dotnet test --solution MkPFS.slnx
```

The first build compiles `native/` automatically (CMake via `native/build.ps1` or
`native/build.sh`). Parity tests skip when `tests/fixtures/generated` is missing.

```bash
dotnet run --project src/MkPFS.Cli -- selftest
```

## Publish (Native AOT)

```bash
dotnet publish src/MkPFS.Cli -r win-x64 -c Release
```

The output folder holds `mkpfs` plus the `mkpfs_zlib` native library.

On this machine AOT linking fails with `'vswhere.exe' is not recognized` unless the VS Installer
folder is on `PATH`:

```bash
setx PATH "%PATH%;C:\Program Files (x86)\Microsoft Visual Studio\Installer"
```

## Releases

Push a version tag to publish a GitHub release. The release workflow reruns the full CI, then
uploads Windows, Linux and macOS archives with `SHA256SUMS.txt`, a changelog since the previous
tag, and the contributor list. Tags with a suffix (`-alpha.1`, `-rc.1`) become pre-releases.

```bash
git tag v2.0.0-alpha.1
```

```bash
git push origin v2.0.0-alpha.1
```

## Regenerate the oracle corpus

```bash
uv run --project ../MkPFS python tools/oracle/build_goldens.py --check
```

## License

GPL-3.0, same as MkPFS.
