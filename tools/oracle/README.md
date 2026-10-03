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
| Post-pack checks | default structure verify (`pack file`, `--raw`), `--verify` (`pack file`, `--raw`, exFAT-wrapped `pack folder`) |
| `batch` | folder + exFAT file: convert, rerun (skipped), dry run; output images hashed |
| Game metadata | `metadata.json` / `metadata_src.json`: Python `read_game_metadata` for each image and its source |

Logs (`*.log`) are UTF-8 with `
` line endings: Python runs with `PYTHONIOENCODING=utf-8` and
`MKPFS_NO_UTF8=1` (ASCII icons such as `WARN`). They contain Windows path separators; compare them
only on Windows or normalize `\` first.
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
9. **Bug:** signed-image verification reads the `ib[0]` signature records only when the `ib[0]`
   signature mismatches (`verify_signed_image_signatures`), so a valid signed file larger than
   12 blocks fails with an unbound `records` variable. Port: records are always read.
10. **Bug:** `verify --source-dir` runs the path comparison twice (`run_image_check` calls
    `validate_source_paths`, then `validate_source_match` calls it again), so every
    "missing in image" / "extra in image" error is printed twice. Port: reported once.
11. `extract_pfs_image` and the exFAT extractors do not check that entry names stay inside the
    output folder. Port: refuses paths that escape it ("unsafe path in image").
12. **Bug:** `exfat_writer` hashes names with Python `str.upper()` and stores the code-point count
    as `NameLength`. exFAT requires the volume up-case table and UTF-16 units, so names with
    characters such as `ß` (upper "SS") or ligatures, or characters outside the BMP, get a
    NameHash or length that exFAT drivers reject. Port: up-case table and UTF-16 length (same
    bytes for ASCII and common accented names, including every fixture).
13. `exfat_writer` writes whatever a source file holds at read time, so a file that changes size
    after the scan yields a corrupt volume. Port: fails with "source file changed size".
14. **Bug:** `batch` prints `Version : PS4` for every run: `print_batch_pre_stats` compares the profile with
    `0x5000000` while PS5 is `2`. Port: prints the real profile.
15. **Bug:** `batch --verify` verifies folder items against the source folder, but the image holds one
    exFAT file, so every folder item fails with "missing in image". Port: verifies the image payloads and
    compares the inner exFAT with the folder.
16. **Bug:** `game_metadata._extract_game_title` takes the first `localizedParameters` locale in file
    order. Retail `param.json` files list locales alphabetically, so the title shows in Arabic (`ar-AE`)
    although `defaultLanguage` is `en-US`. Port: uses `defaultLanguage`, then `en-US`, then the first
    locale. The fixture trees only carry `en-US`, so the metadata goldens still match.
17. **Bug:** `verify --source-dir` compares the folder with the outer image. For an exFAT-wrapped image
    (`pack folder` without `--raw`) the outer image holds one `.exfat`, so every game file reads
    "missing in image". Port: compares the folder with the files inside the exFAT, unless the folder
    holds that `.exfat` itself. The goldens only use `--source-dir` with raw images and bare exFATs.
18. **Bug:** only `pack folder` calls `ensure_ampr_index`. `pack exfat` and `batch` folder items pack
    an APR Emu build (`fakelib/libSceAmpr.sprx`) without `ampr_emu.index`, so the game cannot resolve
    its files. Port: all three build the index. The `exfat_ampr` parity case passes `--no-ampr-index`
    to match the golden.
19. **Bug:** `validate_ampr_index` (`--ampr-skip-regen-if-exists`) only compares the row count with the
    file count, so an index whose files were resized or swapped for others is kept. Port: compares every
    path (case-insensitive) and size; modification times are ignored because copies change them.
20. **Bug:** `verify` rejects a bad `--expect-crc32` or `--expect-manifest-sha256` with messages that name
    `--expected-crc32` and `--expected-manifest-sha256`, options that do not exist. Port: names the real options.
21. **Bug:** the directory walks recurse once per level, so a crafted image nested about 1000 levels deep stops
    with `RecursionError`. Port: walks PFS trees without recursion and reports directories nested deeper than
    1024 levels (PFS and exFAT) as errors instead of ending the process.
