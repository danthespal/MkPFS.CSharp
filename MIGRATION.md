# Moving from Python MkPFS

MkPFS.CSharp is a **drop-in replacement** for the Python [MkPFS](https://github.com/PSBrew/MkPFS) 1.0.0
command line. Scripts keep working: the program is still `mkpfs`, every command and flag has the same name
and default, and the console output has the same layout.

## At a glance

| | Python MkPFS | MkPFS.CSharp |
|---|---|---|
| **Install** | `pip install mkpfs` or `uv tool install mkpfs` | Unpack `mkpfs-<version>-<system>` from the [releases page](https://github.com/danthespal/MkPFS.CSharp/releases) |
| **Needs** | Python 3.9+ with `cryptography`, and `isal` on x64 and ARM | Nothing: one native executable plus `mkpfs_zlib` |
| **Desktop app** | `mkpfs-gui` (customtkinter) | `mkpfs-gui-<version>-<system>`; on macOS `MkPFS.CSharp.app` |
| **Compression** | ISA-L by default | Always zlib 1.3.1 |
| **Images** | — | Byte-identical to Python with `--compression-backend zlib` |

## Commands and flags

| Command | Change |
|---|---|
| `pack folder`, `pack file`, `pack exfat` | Same flags. `--compression-backend` is accepted but ignored ([why](#compression-backend)). |
| `batch` | Same flags. |
| `verify`, `inspect`, `tree`, `unpack` | Same flags. |
| `repair` | **New:** finds and fixes PFSC blocks the PS5 may decode wrongly. |
| `ampr` | **New (experimental):** AMPR Emu LZ4 asset packs. |
| `selftest` | **New:** checks that the bundled native libraries load. |

**Exit codes** match Python: `0` success, `1` error, `2` usage error (missing arguments, unknown or
conflicting options). `repair --scan` adds `3` for "blocks need repair". Usage error messages are worded
differently from Python's argparse.

## Behavior that differs

### Compression backend

Python's default `auto` backend picks ISA-L, which Python MkPFS installs on x64 and ARM. ISA-L streams use
back-references the PS5 decodes wrongly, which corrupts game files after mounting. MkPFS.CSharp always
compresses with zlib 1.3.1 at the same level (7 by default): `auto` and `zlib` are accepted silently, `isal`
and `zlib-ng` print a warning and use zlib.

### Same images

With the same source and flags, MkPFS.CSharp writes the same bytes as Python MkPFS compressing with zlib,
except for the build time stored in the image. In Python that takes `--compression-backend zlib`, plus
`--cpu-count 1` for large inputs, because its worker processes ignore the backend option. Set
`SOURCE_DATE_EPOCH` (seconds since 1970) to pin the build time for reproducible builds.

### Fixed Python bugs

These Python results change (details in [tools/oracle/README.md](tools/oracle/README.md), "Findings"):

| Area | Fix |
|---|---|
| `--skip-verification` | Works on its own; Python always rejected it. |
| `verify` | Signed images larger than 12 blocks no longer crash. |
| `verify --source-dir` | Reports each missing or extra path once, and compares an exFAT-wrapped image with the files inside its exFAT. |
| `unpack` | Refuses entry names that would escape the output folder. |
| exFAT images | Use the exFAT up-case table for name hashes, so names such as `Straße` stay readable on the console; `pack exfat` fails if a source file changes size while it is packed. |
| `batch` | Prints the real PS4/PS5 profile; `batch --verify` checks a folder item against the files inside its image. |
| Game titles | Use the `param.json` default language instead of the first language in the file (often Arabic). |
| `ampr_emu.index` | Hashed the way the console looks paths up, so files with non-ASCII names are found. |

### Name

The header line reads `MkPFS.CSharp <version> - https://github.com/danthespal/MkPFS.CSharp`. Tools that parse
the first lines of the output should accept both names.

## New in MkPFS.CSharp

- **`mkpfs repair`** for single-file `.ffpfsc` images, with `--scan`, `--bad-blocks` (PS5 Game Compressor
  `bad_blocks.tsv`), `--recompress`, `--mode` and `--report-dir`.
- **`verify` warnings** when compressed blocks use back-references the PS5 may decode wrongly, pointing to `repair`.
- **APR Emu:** `pack exfat` and `batch` also write `ampr_emu.index`, and `--ampr-libs` copies AMPR Emu into `fakelib/`.
- **`mkpfs ampr`** (experimental): LZ4 asset packs that AMPR Emu reads on the fly, with rules from a TOML
  profile or from traces recorded on the console.
- **Desktop app:** a Repair page with a block map, an AMPR Packs page, tooltips on every tuning option, and a
  CPU core picker. The desktop app builds exFAT and FFPFSC images; `pack folder` and `batch` stay on the
  command line.
