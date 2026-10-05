"""Build the AMPR asset-pack golden corpus with ampr_emu's ``tools/ampr_pack.py`` (oracle for docs/AMPR_PACK_PLAN.md).

Layout of ``--out`` (default ``tests/fixtures/generated/ampr``):
    trees/ampr_assets/            fixture /app0 source
    goldens/<case>/               app0/ (copy + ampr_emu.index), config.toml, out/, unpacked/, *.log,
                                  config.canonical.json (ampr_pack._canonical_config_bytes)
    goldens/manifest.json         case → argv, exit codes, sha256 of every output file, versions
    vectors/lz4_vectors.bin       raw blocks + oracle LZ4 output for codec byte tests

Needs Python >= 3.11 with python-lz4 (the oracle's encoder), and ampr_emu checked out at
``ORACLE_COMMIT`` in ``../ampr_emu``. The ``../MkPFS`` uv environment has no lz4; use:
    python tools/oracle/build_ampr_goldens.py --check
    uv run --no-project --python 3.11 --with lz4==4.4.5 python tools/oracle/build_ampr_goldens.py --check
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import random
import shutil
import struct
import subprocess
import sys
import tempfile
from dataclasses import dataclass, field
from pathlib import Path

HERE: Path = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from build_goldens import (
    normalize_log,  # ruff: ignore[module-import-not-at-top-of-file]  (sibling script)
)
from make_fixtures import (  # ruff: ignore[module-import-not-at-top-of-file]
    _param_json,
    _semi,
    _text,
    _write,
    pin_mtimes,
)

ORACLE_COMMIT: str = "cfa85df379f6eeeb165d7badf9b648e266fe77b7"
EXPECTED_LZ4: str = "4.4.5"  # python-lz4; bundles liblz4 1.9.4 (see AMPR_PACK_PLAN.md, hazard 1)
AMPR_EMU: Path = HERE.parent.parent.parent / "ampr_emu"
AMPR_TOOLS: Path = AMPR_EMU / "tools"
DEFAULT_OUT: Path = HERE.parent.parent / "tests" / "fixtures" / "generated" / "ampr"
MIB: int = 1024 * 1024

BASE: str = '[pack]\ndefault_action = "compress"\n'
# ampr_pack has no hard exclusions (only remove-packed-sources refuses these), so most configs end with
# a loose rule for modules and system files; ``cfg`` appends it (last match wins).
SYSTEM_LOOSE: str = (
    '\n[[rule]]\naction = "loose"\ninclude = ["eboot.bin", "**/*.prx", "**/*.sprx", "sce_sys/**", "sce_module/**", '
    '"fakelib/**"]\n'
)
GROUPS3: str = '\n[groups.default]\npack_count = 3\n'
RUNTIME: str = (
    '\n[runtime]\ndecoded_cache_bytes = "64MiB"\nphysical_cache_bytes = "16MiB"\n'
    "workers = 4\nlatency_reserve_workers = 1\n"
)
RUNTIME_ALT: str = (
    '[runtime]\ndecoded_cache_bytes = "128MiB"\nphysical_cache_bytes = "32MiB"\n'
    "workers = 8\nlatency_reserve_workers = 2\n"
)
TOUCHED: dict[str, int] = {"assets/text_05.dat": 1_600_000_500}
AUTO_LOOSE: str = (
    'auto_loose_min_file_size = "2MiB"\nauto_loose_sample_blocks = 8\nauto_loose_sample_bytes = "1MiB"\n'
)


def tree_ampr_assets(root: Path) -> None:
    """``/app0`` with every packer path: RAW fallback, dedup, auto-loose, store, hot scripts, edge sizes."""
    rng: random.Random = random.Random(6)
    _write(root, "sce_sys/param.json", _param_json("PPSA00006"))
    _write(root, "eboot.bin", _semi(rng, 90_000))
    _write(root, "sce_module/libfixture.prx", _semi(rng, 40_000))
    _write(root, "fakelib/libSceAmpr.sprx", _semi(rng, 20_000))
    for i, size in enumerate((0, 1, 16_384, 65_536, 65_537, 700_000)):
        _write(root, f"assets/text_{i:02d}.dat", _text(rng, size))
    _write(root, "assets/semi.dat", _semi(rng, 300_000))
    _write(root, "assets/random.bin", rng.randbytes(200_000))
    dup: bytes = _text(rng, 200_000)
    _write(root, "assets/dup_a.bin", dup)
    _write(root, "assets/sub/dup_b.bin", dup)
    _write(root, "assets/Sub2/Mixed.BIN", _text(rng, 30_000))
    _write(root, "assets/zeros.bin", bytes(MIB))
    _write(root, "archives/big.pak", rng.randbytes(3 * MIB))
    _write(root, "archives/big_text.pak", _text(rng, 3 * MIB))
    _write(root, "movies/intro.bik", rng.randbytes(3 * MIB // 2))
    for i in range(20):
        _write(root, f"scripts/s_{i:03d}.lua", _text(rng, rng.randint(100, 4000)))
    _write(root, "localization/en.txt", _text(rng, 50_000))


@dataclass
class Case:
    """One ``ampr_pack.py pack`` run plus its follow-up commands."""

    name: str
    toml: str | None = BASE
    flags: list[str] = field(default_factory=list)
    expect_ok: bool = True
    expect_fail_steps: list[str] = field(default_factory=list)  # follow-up steps that must fail
    remove_before_pack: list[str] = field(default_factory=list)  # deleted after the index is built
    touch_after_index: dict[str, int] = field(default_factory=dict)  # mtime changed after the index is built
    extra_files: dict[str, str] = field(default_factory=dict)  # written next to config.toml
    note: str = ""


def cfg(text: str) -> str:
    """Return ``text`` with the trailing system loose rule."""
    return text + SYSTEM_LOOSE


def cases() -> list[Case]:
    """Return the case matrix."""
    example: str = (AMPR_TOOLS / "ampr_pack.example.toml").read_text(encoding="utf-8")
    stripe: str = (
        BASE + '\n[groups.default]\npack_count = 2\nmax_pack_size = "1MiB"\nstripe_large_files = true\n'
        'stripe_threshold = "1MiB"\nstripe_group_blocks = 4\n'
    )
    return [
        Case("default", cfg(BASE), note="HC 12, 64 KiB, auto layout"),
        Case("unsafe_default", BASE, expect_fail_steps=["remove_plan"],
             note="no system loose rule: eboot.bin/PRX/sce_sys get packed; remove-packed-sources refuses"),
        Case("no_config", toml=None, note="default_action loose: nothing is packed"),
        Case("example", toml=example, note="upstream ampr_pack.example.toml"),
        Case("fast", cfg(BASE + 'compression_mode = "fast"\n')),
        Case("fast_accel8", cfg(BASE + 'compression_mode = "fast"\nacceleration = 8\n')),
        Case("hc9", cfg(BASE + "compression_level = 9\n")),
        Case("block16k_random", cfg(BASE + '\n[[rule]]\nblock_size = "16KiB"\nlayout = "random"\nhot = true\n')),
        Case("block1m", cfg(BASE + 'default_block_size = "1MiB"\n')),
        Case("streaming", cfg(BASE + '\n[[rule]]\nlayout = "streaming"\nblock_size = "256KiB"\n')),
        Case("store_movies", cfg(BASE + '\n[[rule]]\naction = "store"\ninclude = ["movies/**"]\nlayout = "streaming"\n')),
        Case("dedup_off", cfg(BASE + "deduplicate = false\n")),
        Case("dedup_group", cfg(BASE + 'deduplicate_scope = "group"\n' + GROUPS3)),
        Case("dedup_streaming", cfg(BASE + 'deduplicate_streaming = true\n\n[[rule]]\nlayout = "streaming"\n')),
        Case("lanes_balanced", cfg(BASE + GROUPS3)),
        Case("lanes_hash", cfg(BASE + GROUPS3 + 'assignment = "hash"\n')),
        Case("lanes_round_robin", cfg(BASE + GROUPS3 + 'assignment = "round_robin"\n')),
        Case("stripe_rollover", cfg(stripe)),
        Case("auto_loose", cfg(BASE + AUTO_LOOSE)),
        Case("self_contained", cfg(BASE + AUTO_LOOSE), ["--self-contained"]),
        Case("runtime", cfg(BASE + RUNTIME), extra_files={"runtime_alt.toml": RUNTIME_ALT}),
        Case("io_page_4k", cfg(BASE + 'io_page_size = "4KiB"\n')),
        Case("mtime_preserved", cfg(BASE), touch_after_index=TOUCHED, note="manifest takes the disk mtime"),
        Case("no_preserve_mtime", cfg(BASE + "preserve_mtime = false\n"), touch_after_index=TOUCHED,
             note="manifest takes the AMPRIDX3 mtime"),
        Case("cli_include_exclude", cfg(BASE), ["--include", "assets/**", "--exclude", "**/*.bin"]),
        Case(
            "include_from",
            BASE.replace('"compress"', '"loose"') + '\n[[rule]]\ninclude_from = ["list.txt"]\n',
            extra_files={"list.txt": "# scripts and text\nscripts/**\n\nassets/text_*.dat\n"},
        ),
        Case("allow_missing", cfg(BASE), ["--allow-missing"], remove_before_pack=["assets/semi.dat"]),
        Case("workers1", cfg(BASE), ["--workers", "1"], note="must equal workers8"),
        Case("workers8", cfg(BASE), ["--workers", "8"]),
        Case("neg_missing", cfg(BASE), expect_ok=False, remove_before_pack=["assets/semi.dat"]),
        Case("neg_bad_block", cfg(BASE + 'default_block_size = "48KiB"\n'), expect_ok=False),
        Case("neg_unknown_group", cfg(BASE + '\n[[rule]]\ngroup = "nope"\n'), expect_ok=False),
        Case("neg_require_packed", cfg(BASE + AUTO_LOOSE), ["--require-packed", "archives/big.pak"], expect_ok=False),
        Case("neg_runtime_keys", cfg(BASE + '\n[runtime]\nworkers = 4\n'), expect_ok=False),
    ]  # fmt: skip


def run_tool(script: str, argv: list[str], cwd: Path, log_name: str) -> int:
    """Run an ampr_emu tool in ``cwd`` and store normalized combined output in ``log_name``."""
    env: dict[str, str] = {**os.environ, "PYTHONIOENCODING": "utf-8"}
    proc: subprocess.CompletedProcess[str] = subprocess.run(
        [sys.executable, str(AMPR_TOOLS / script), *argv],
        cwd=cwd,
        env=env,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=False,
    )
    text: str = f"$ {script} {' '.join(argv)}\nexit={proc.returncode}\n--- stdout\n{proc.stdout}\n--- stderr\n{proc.stderr}"
    (cwd / log_name).write_text(normalize_log(text, cwd), encoding="utf-8", newline="\n")
    return proc.returncode


def canonical_config(config_path: Path | None) -> bytes:
    """Return ``ampr_pack._canonical_config_bytes`` for a TOML (the build-id input)."""
    import ampr_pack  # ../ampr_emu/tools is on sys.path (see main)

    return ampr_pack._canonical_config_bytes(ampr_pack.load_config(config_path))


def hash_tree(root: Path, prefix: str) -> dict[str, str]:
    """Return ``prefix/relative`` → sha256 for every file under ``root``."""
    out: dict[str, str] = {}
    if root.is_dir():
        for path in sorted(root.rglob("*")):
            if path.is_file():
                out[f"{prefix}/{path.relative_to(root).as_posix()}"] = hashlib.sha256(path.read_bytes()).hexdigest()
    return out


def build_case(case: Case, tree: Path, goldens: Path) -> dict[str, object]:
    """Build one case and its follow-up outputs; return its manifest entry."""
    work: Path = goldens / case.name
    if work.exists():
        shutil.rmtree(work)
    work.mkdir(parents=True)
    shutil.copytree(tree, work / "app0")
    pin_mtimes(work / "app0")
    for name, text in case.extra_files.items():
        (work / name).write_text(text, encoding="utf-8", newline="\n")
    config_args: list[str] = []
    if case.toml is not None:
        (work / "config.toml").write_text(case.toml, encoding="utf-8", newline="\n")
        config_args = ["--config", "config.toml"]

    exit_codes: dict[str, int] = {"index": run_tool("build_ampr_index.py", ["app0"], work, "index.log")}
    for rel in case.remove_before_pack:
        (work / "app0" / rel).unlink()
    for rel, mtime in case.touch_after_index.items():
        os.utime(work / "app0" / rel, (mtime, mtime))
    pack_argv: list[str] = ["pack", "--root", "app0", "--ampr-index", "app0/ampr_emu.index", "--output", "out",
                            *config_args, "--no-progress", *case.flags]  # fmt: skip
    steps: dict[str, list[str]] = {"pack": pack_argv}
    exit_codes["pack"] = run_tool("ampr_pack.py", pack_argv, work, "pack.log")

    built: bool = exit_codes["pack"] == 0
    index: str = "out/ampr_assets.index"
    if built:
        follow: dict[str, list[str]] = {
            "verify": ["verify", "--index", index, "--root", "app0"],
            "list": ["list", "--index", index, "--json"],
            "list_text": ["list", "--index", index],
            "inspect": ["inspect", "--index", index],
            "unpack": ["unpack", "--index", index, "--output", "unpacked"],
            "remove_plan": ["remove-packed-sources", "--index", index, "--root", "app0"],
        }
        if "runtime_alt.toml" in case.extra_files:
            follow["runtime_config"] = ["runtime-config", "--index", index, "--config", "runtime_alt.toml"]
        for step, argv in follow.items():
            steps[step] = argv
            exit_codes[step] = run_tool("ampr_pack.py", argv, work, f"{step}.log")
        if case.toml is not None:
            (work / "config.canonical.json").write_bytes(canonical_config(work / "config.toml"))

    # Every unpacked file must equal its source.
    unpack_ok: bool = True
    for path in sorted((work / "unpacked").rglob("*")) if (work / "unpacked").is_dir() else []:
        if path.is_file():
            source: Path = work / "app0" / path.relative_to(work / "unpacked")
            unpack_ok = unpack_ok and source.read_bytes() == path.read_bytes()

    hashes: dict[str, str] = {}
    for path in sorted(work.iterdir()):
        if path.is_file():
            hashes[path.name] = hashlib.sha256(path.read_bytes()).hexdigest()
    hashes |= hash_tree(work / "out", "out")
    hashes |= hash_tree(work / "unpacked", "unpacked")
    # Keep only the oracle index; tests rebuild app0 from trees/ampr_assets plus the recorded edits.
    if (work / "app0" / "ampr_emu.index").is_file():
        shutil.copy2(work / "app0" / "ampr_emu.index", work / "ampr_emu.index")
        hashes["ampr_emu.index"] = hashlib.sha256((work / "ampr_emu.index").read_bytes()).hexdigest()
    shutil.rmtree(work / "app0")
    shutil.rmtree(work / "unpacked", ignore_errors=True)
    follow_ok: bool = (
        all((code != 0) == (step in case.expect_fail_steps) for step, code in exit_codes.items()) if built else True
    )
    return {
        "toml": case.toml is not None,
        "flags": case.flags,
        "remove_before_pack": case.remove_before_pack,
        "touch_after_index": case.touch_after_index,
        "steps": steps,
        "exit_codes": exit_codes,
        "built": built,
        "expect_ok": case.expect_ok,
        "unpack_matches_source": unpack_ok,
        "status_ok": built == case.expect_ok and follow_ok and unpack_ok,
        "note": case.note,
        "sha256": hashes,
    }


def write_lz4_vectors(tree: Path, out_path: Path) -> int:
    """Write ``L4VEC001`` vectors with the oracle codec (``ampr_pack_format.Lz4Codec``).

    Format (little endian): magic[8], u32 count, then per entry u8 mode (0 fast, 1 hc), u8[3] zero,
    u32 param (acceleration or level), u32 raw_len, u32 comp_len, raw bytes, comp bytes.
    """
    from ampr_pack_format import Lz4Codec

    codec = Lz4Codec()
    small: list[bytes] = [
        (tree / "assets/text_05.dat").read_bytes()[:0x10000],
        (tree / "assets/semi.dat").read_bytes()[:0x10000],
        (tree / "assets/random.bin").read_bytes()[:0x4000],
        (tree / "scripts/s_000.lua").read_bytes(),
        b"\x00",
    ]
    large: list[bytes] = [(tree / "archives/big_text.pak").read_bytes()[:MIB], bytes(MIB)]
    params: list[tuple[int, int]] = [(0, 1), (0, 2), (0, 8), *((1, level) for level in range(1, 13))]
    entries: list[tuple[int, int, bytes, bytes]] = []
    for raw in small:
        for mode, param in params:
            entries.append((mode, param, raw, codec_compress(codec, raw, mode, param)))
    for raw in large:
        for mode, param in ((0, 1), (1, 9), (1, 12)):
            entries.append((mode, param, raw, codec_compress(codec, raw, mode, param)))
    out_path.parent.mkdir(parents=True, exist_ok=True)
    with out_path.open("wb") as fh:
        fh.write(b"L4VEC001" + struct.pack("<I", len(entries)))
        for mode, param, raw, comp in entries:
            fh.write(struct.pack("<B3xIII", mode, param, len(raw), len(comp)) + raw + comp)
    return len(entries)


def codec_compress(codec: object, raw: bytes, mode: int, param: int) -> bytes:
    """Compress like ``ampr_pack._compress_block`` does (raw LZ4 block, no size prefix)."""
    if mode == 0:
        return codec.compress(raw, mode="fast", acceleration=param)  # type: ignore[attr-defined]
    return codec.compress(raw, mode="hc", level=param)  # type: ignore[attr-defined]


def oracle_versions() -> dict[str, str]:
    """Check the pinned ampr_emu commit and the python-lz4 encoder; return the recorded versions."""
    head: str = subprocess.run(
        ["git", "-C", str(AMPR_EMU), "rev-parse", "HEAD"], capture_output=True, text=True, check=True
    ).stdout.strip()
    dirty: str = subprocess.run(
        ["git", "-C", str(AMPR_EMU), "status", "--porcelain", "--", "tools"], capture_output=True, text=True, check=True
    ).stdout.strip()
    if head != ORACLE_COMMIT or dirty:
        raise SystemExit(f"error: {AMPR_EMU} must be a clean checkout of {ORACLE_COMMIT} (HEAD {head})")
    try:
        import lz4
        import lz4.block
    except ImportError as exc:
        raise SystemExit("error: python-lz4 is required (pip install lz4==4.4.5)") from exc
    import ampr_pack

    return {
        "ampr_emu_commit": head,
        "ampr_pack_tool_version": ampr_pack.TOOL_VERSION,
        "python": sys.version,
        "python_lz4": lz4.__version__,
        "liblz4": lz4.library_version_string(),
    }


def build_all(out: Path) -> dict[str, object]:
    """Recreate the tree, build every case, write manifest and vectors."""
    tree: Path = out / "trees" / "ampr_assets"
    goldens: Path = out / "goldens"
    if tree.exists():
        shutil.rmtree(tree)
    tree.mkdir(parents=True)
    tree_ampr_assets(tree)
    pin_mtimes(tree)
    goldens.mkdir(parents=True, exist_ok=True)
    manifest: dict[str, object] = {"versions": oracle_versions(), "fixed_mtime": 1_600_000_000, "cases": {}}
    for case in cases():
        entry: dict[str, object] = build_case(case, tree, goldens)
        manifest["cases"][case.name] = entry  # type: ignore[index]
        print(f"{'OK  ' if entry['status_ok'] else 'FAIL'} {case.name} {entry['exit_codes']}")
    manifest["lz4_vectors"] = write_lz4_vectors(tree, out / "vectors" / "lz4_vectors.bin")
    (goldens / "manifest.json").write_text(json.dumps(manifest, indent=2, sort_keys=True), encoding="utf-8")
    return manifest


def compare(a: dict[str, object], b: dict[str, object]) -> list[str]:
    """Return per-file differences between two manifests."""
    diffs: list[str] = []
    for name, ea in a["cases"].items():  # type: ignore[union-attr]
        eb = b["cases"][name]  # type: ignore[index]
        for fname, h in ea["sha256"].items():
            if eb["sha256"].get(fname) != h:
                diffs.append(f"{name}/{fname}")
    return diffs


def main() -> int:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, default=DEFAULT_OUT, help="Default: tests/fixtures/generated/ampr")
    parser.add_argument("--check", action="store_true", help="Build twice and report nondeterministic files")
    parser.add_argument("--allow-lz4-version", action="store_true", help=f"Accept python-lz4 other than {EXPECTED_LZ4}")
    args: argparse.Namespace = parser.parse_args()
    sys.path.insert(0, str(AMPR_TOOLS))

    first: dict[str, object] = build_all(args.out)
    versions: dict[str, str] = first["versions"]  # type: ignore[assignment]
    print(f"python-lz4 {versions['python_lz4']} (liblz4 {versions['liblz4']}), ampr_pack {versions['ampr_pack_tool_version']}")
    failed: list[str] = [n for n, e in first["cases"].items() if not e["status_ok"]]  # type: ignore[union-attr]
    if versions["python_lz4"] != EXPECTED_LZ4 and not args.allow_lz4_version:
        failed.append(f"python-lz4 {versions['python_lz4']} != {EXPECTED_LZ4}")

    cases_: dict[str, dict[str, object]] = first["cases"]  # type: ignore[assignment]
    w1: dict[str, str] = cases_["workers1"]["sha256"]  # type: ignore[assignment]
    w8: dict[str, str] = cases_["workers8"]["sha256"]  # type: ignore[assignment]
    same_workers: bool = {k: v for k, v in w1.items() if k.startswith("out/")} == {
        k: v for k, v in w8.items() if k.startswith("out/")
    }
    print(f"workers1 == workers8 packs: {same_workers}")
    if not same_workers:
        failed.append("workers1 != workers8")

    if args.check:
        second_out: Path = Path(tempfile.mkdtemp(prefix="mkpfs_ampr_check_"))
        second: dict[str, object] = build_all(second_out)
        diffs: list[str] = compare(first, second)
        shutil.rmtree(second_out, ignore_errors=True)
        (args.out / "determinism.json").write_text(json.dumps(diffs, indent=2), encoding="utf-8")
        print(f"nondeterministic files: {len(diffs)}")
        for d in diffs:
            print(f"  {d}")
        if diffs:
            failed.append("nondeterministic output")
    print(f"failed: {failed}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
