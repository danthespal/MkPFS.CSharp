# Python oracle (Phase 0)

Builds reference images with Python MkPFS so the C# port can be checked byte for byte.
Python MkPFS (`D:\TOOLS\PS5\MkPFS`, expected at `../MkPFS`) stays the oracle until parity sign-off.

## Files

| File | Purpose |
|---|---|
| `oracle.py` | Runs the MkPFS CLI with a pinned clock (`MKPFS_ORACLE_EPOCH`, default 1700000000), counter `uuid4`, and `--compression-backend zlib` (also forced inside worker processes) |
| `make_fixtures.py` | Generates deterministic source trees (seeded data, mtimes pinned to 1600000000) |
| `build_goldens.py` | Builds 33 cases, captures inspect/tree/verify output, writes `manifest.json` and zlib vectors |
| `bench.py` | Python baseline timings on a ~330 MiB tree |

## Run

Run from the repo root. `uv` uses the Python repo's environment.

```bash
uv run --project ../MkPFS python tools/oracle/build_goldens.py --check
```

```bash
uv run --project ../MkPFS python tools/oracle/bench.py
```

Both default to `tests/fixtures/generated/` (git-ignored, about 270 MB).

## Output (`tests/fixtures/generated/`)

- `trees/<tree>/`: fixture sources (`app_basic`, `fpt_collision`, `many_files`, `ampr`, `non_ascii`).
- `goldens/<case>/`: `src/` copy, `out.*` image, `build.log`, `inspect.log`, `tree.log`,
  `verify.log`, `tree_deep.log`. Paths are replaced with `<CASE>` and progress lines are removed.
- `goldens/manifest.json`: per case argv, exit codes, SHA-256 of every file.
- `vectors/zlib_vectors.bin`: `ZVEC0001`, u32 count, then per entry
  `u32 level, u32 raw_len, u32 comp_len, raw, comp` (zlib 1.3.1, 61 entries).
- `baseline_timings.json`: Python timings.
- `determinism.json`: files that differed between two builds (expected `[]`).

## Case matrix

| Group | Cases |
|---|---|
| `pack exfat` | 4 trees, cluster 32K/64K, non-ASCII names |
| `pack file` | cpu 1 vs 4 (must match), user flags from the bad-block report, no-compress, encrypted, many files, ISA-L (repair input only) |
| `pack folder` | exFAT-wrapped default for 3 trees |
| `pack folder --raw` | PS5, PS4, inode 64, case-sensitive, no-compress, signed, signed 64, encrypted, encrypted + key, filters, level 1, FPT collision (CI/CS), many files, AMPR, non-ASCII (expected failure) |

Logs (`*.log`) contain Windows path separators; compare them only on Windows or normalize `\` first.
Images are platform independent.

## Results (2026-10-02, Windows 11, 32 threads, Python 3.11.15, zlib 1.3.1)

- 33/33 cases match expectations; two full builds are byte-identical.
- `pack file` output is identical for `--cpu-count 1` and `4`.
- Baseline (328 MiB source): `pack exfat` 0.36 s, `pack file` 9.42 s (1 CPU) / 1.63 s (auto, zlib forced;
  the earlier 1.48 s was ISA-L, see finding 8),
  `pack folder` 1.14 s, `pack folder --raw` 1.51 s, `verify` 1.37 s, `unpack --deep` 0.66 s.

## Findings to carry into the port

1. **Bug:** `--skip-verification` alone always fails ("--verify-structure and
   --skip-verification cannot be used together") because `--verify-structure` defaults on
   (Python `cli.py:1053`). Port: skip wins over the default. The harness uses `--no-verify-structure`.
2. `pack file` only parallelizes inputs ≥ 256 MiB (`PFSC_SINGLE_FILE_PARALLEL_MIN_SIZE`).
   Below that it is single-threaded even with `--cpu-count 0`. The port can lower this.
3. `inspect` rejects exFAT images (exit 1); `inspect --format json` is minimal
   (block size, version, errors, warnings, image path).
4. Every PFS image warns `sce_sys/pfs-version.dat not found` when the source lacks it.
5. `exfat_writer` always uses 64 KiB clusters by default; its docstring still says 32 KiB.
6. The default `auto` backend picks ISA-L, whose streams caused PS5 bad blocks. The port uses zlib only.
7. Python images contain the build time; the C# CLI needs `--timestamp` / `SOURCE_DATE_EPOCH`
   to compare against goldens (epoch 1700000000).
8. **Bug:** MkPFS process pools ignore `--compression-backend`. Workers re-import
   `mkpfs.compression` and fall back to `auto` (ISA-L): the single-file block pool (`pack file`
   on inputs ≥ 256 MiB, `pfs.py:1221`) and the `pack folder --raw` file pools (`pfs.py:3332`,
   `3411`, no initializer). So `--compression-backend zlib` alone does not avoid ISA-L on real
   game images; `--cpu-count 1` does. `oracle.py` wraps `mp.Pool` to force the backend, and
   `build_goldens.py` re-compresses every stored block to prove the corpus is pure zlib.
