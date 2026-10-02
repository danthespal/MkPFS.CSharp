# MkPFS.C#

.NET 10 port of [MkPFS](https://github.com/PSBrew/MkPFS): build, verify, inspect and unpack PS4/PS5
PFS images (`.ffpfs`, `.ffpfsc`, `.exfat`), plus PFSC block repair ported from PS5 Game Compressor.

Status: Phase 1 (native zlib, core utilities, CI). Phase 0 oracle corpus is in place.

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

## Regenerate the oracle corpus

```bash
uv run --project ../MkPFS python tools/oracle/build_goldens.py --check
```

## License

GPL-3.0, same as MkPFS.
