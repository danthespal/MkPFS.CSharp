# Moving from Python MkPFS

MkPFS.C# is a drop-in replacement for the Python [MkPFS](https://github.com/PSBrew/MkPFS) 1.0.0 command
line. Scripts keep working: the program is still `mkpfs`, the commands and every flag have the same
names, and the console output has the same layout.

## Install

| Python MkPFS | MkPFS.C# |
|---|---|
| `pip install mkpfs` or `uv tool install mkpfs` | Unpack `mkpfs-<version>-<system>` from the [releases page](https://github.com/danthespal/MkPFS.CSharp/releases) |
| Needs Python 3.9+ with `cryptography` and, on x64 and ARM, `isal` | Single native executable plus `mkpfs_zlib`; no runtime to install |
| `mkpfs-gui` (customtkinter) | `mkpfs-gui-<version>-<system>`; on macOS `MkPFS.C#.app` |

## Commands and flags

Every Python command and flag exists with the same name and default:

| Command | Change |
|---|---|
| `pack folder`, `pack file`, `pack exfat` | Same flags. `--compression-backend` is accepted but ignored (see below). |
| `batch` | Same flags. |
| `verify`, `inspect`, `tree`, `unpack` | Same flags. |
| `repair` | New: finds and fixes PFSC blocks the PS5 may decode wrongly. |

Exit codes match Python: 0 on success, 1 on errors, 2 on usage errors (missing arguments, unknown
or conflicting options). `repair --scan` adds 3 for "blocks need repair". Usage error messages
are worded differently from Python's argparse.

## Behavior that differs

**Compression backend.** Python's default `auto` backend picks ISA-L, which Python MkPFS installs
on x64 and ARM. ISA-L streams use back-references the PS5 decodes wrongly (corrupted game files
after mounting). MkPFS.C# always compresses with zlib 1.3.1 at the same level (7 by default):
`auto` and `zlib` are accepted silently, `isal` and `zlib-ng` print a warning and use zlib.

**Same images.** With the same source and flags, MkPFS.C# writes the same bytes as Python MkPFS
compressing with zlib, except for the build time stored in the image. In Python that takes
`--compression-backend zlib`, plus `--cpu-count 1` for large inputs, because its worker processes
ignore the backend option. Set `SOURCE_DATE_EPOCH` (seconds since 1970) to pin the build time for
reproducible builds.

**Fixed Python bugs.** These Python results change (details in
[tools/oracle/README.md](tools/oracle/README.md), "Findings"):

- `--skip-verification` works on its own; Python always rejected it.
- `verify` of signed images larger than 12 blocks no longer crashes.
- `verify --source-dir` reports each missing or extra path once, not twice, and compares an
  exFAT-wrapped image with the files inside its exFAT instead of reporting every file missing.
- `unpack` refuses entry names that would escape the output folder.
- exFAT images use the exFAT up-case table for name hashes, so names such as `Straße` stay readable
  on the console; `pack exfat` fails if a source file changes size while it is packed.
- `batch` prints the real PS4/PS5 profile, and `batch --verify` checks a folder item against the
  files inside its image instead of failing every time.
- Game titles use the `param.json` default language instead of the first language in the file
  (often Arabic).

**Name.** The header line reads `MkPFS.C# <version> - https://github.com/danthespal/MkPFS.CSharp`.
Tools that parse the first lines of the output should accept both names.

## New in MkPFS.C#

- `mkpfs repair` for single-file `.ffpfsc` images, with `--scan`, `--bad-blocks` (PS5 Game Compressor
  `bad_blocks.tsv`), `--recompress`, `--mode`, and `--report-dir`.
- `verify` warns when compressed blocks use back-references the PS5 may decode wrongly and points to
  `repair`.
- The GUI has a Repair page with a block map of the image.
