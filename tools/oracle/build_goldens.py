"""Build the golden corpus (images + inspect/tree/verify outputs) with the deterministic oracle.

Layout of ``--out``:
    trees/<tree>/                 fixture sources (from make_fixtures.py)
    goldens/<case>/               one folder per case: src copy, outputs, logs
    goldens/manifest.json         case → argv, exit code, sha256 of every output file
    vectors/zlib_vectors.bin      raw 64 KiB blocks + zlib outputs for codec byte tests

Usage (from the MkPFS.CSharp root; ../MkPFS is the Python repo):
    uv run --project ../MkPFS python tools/oracle/build_goldens.py
    uv run --project ../MkPFS python tools/oracle/build_goldens.py --check    # build twice, compare
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import zlib
from dataclasses import dataclass, field
from pathlib import Path

HERE: Path = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from make_fixtures import (  # ruff: ignore[module-import-not-at-top-of-file]  (sibling script, not a package)
    make_trees,
    pin_mtimes,
)

ORACLE: Path = HERE / "oracle.py"
DEFAULT_OUT: Path = HERE.parent.parent / "tests" / "fixtures" / "generated"
PROGRESS_LINE: re.Pattern[str] = re.compile(r"^\s*\[[#-]+\]\s+\d+%")
TEST_EKPFS_HEX: str = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff"
USER_FILE_FLAGS: list[str] = [
    "--compress", "--block-size", "65536", "--compression-level", "7", "--threshold-gain", "5",
    "--max-compressed-ratio", "100", "--min-compress-size", "65536", "--skip-executable-compression",
    "--cpu-count", "0", "--version", "PS5", "--inode-bits", "32",
]  # fmt: skip


@dataclass
class Case:
    """One golden build case."""

    name: str
    tree: str
    kind: str  # exfat | file | folder | raw | batch
    flags: list[str] = field(default_factory=list)
    expect_ok: bool = True
    ekpfs_hex: str | None = None
    note: str = ""
    post_flags: list[str] = field(default_factory=lambda: ["--no-verify-structure"])  # post-pack verification


def cases() -> list[Case]:
    """Return the full case matrix."""
    out: list[Case] = []
    for tree in ("app_basic", "fpt_collision", "many_files", "ampr"):
        out.append(Case(f"exfat_{tree}", tree, "exfat"))
    out.append(Case("exfat_app_basic_c32k", "app_basic", "exfat", ["--cluster-size", "32768"]))
    out.append(Case("exfat_app_basic_c64k", "app_basic", "exfat", ["--cluster-size", "65536"]))
    out.append(Case("exfat_non_ascii", "non_ascii", "exfat", note="records current behavior"))

    out.append(Case("file_app_cpu1", "app_basic", "file", ["--compress", "--cpu-count", "1"]))
    out.append(Case("file_app_cpu4", "app_basic", "file", ["--compress", "--cpu-count", "4"], note="must equal cpu1"))
    out.append(
        Case("file_app_user_flags", "app_basic", "file", USER_FILE_FLAGS, note="flags from the bad-block report")
    )
    out.append(Case("file_app_nc", "app_basic", "file", ["--no-compress"]))
    out.append(Case("file_app_enc", "app_basic", "file", ["--compress", "--encrypted"]))
    out.append(Case("file_many_files", "many_files", "file", ["--compress"]))
    out.append(Case("file_app_isal", "app_basic", "file", ["--compress", "--compression-backend", "isal"],
                    note="repair-phase input: ISA-L streams, not a C# byte golden"))  # fmt: skip

    for tree in ("app_basic", "many_files", "ampr"):
        out.append(Case(f"folder_{tree}", tree, "folder"))

    raw: list[tuple[str, str, list[str], str | None]] = [
        ("raw_app", "app_basic", [], None),
        ("raw_app_ps4", "app_basic", ["--version", "PS4"], None),
        ("raw_app_inode64", "app_basic", ["--inode-bits", "64"], None),
        ("raw_app_case_sensitive", "app_basic", ["--case-sensitive"], None),
        ("raw_app_nc", "app_basic", ["--no-compress"], None),
        ("raw_app_signed", "app_basic", ["--signed"], None),
        ("raw_app_signed64", "app_basic", ["--signed", "--inode-bits", "64"], None),
        ("raw_app_enc", "app_basic", ["--encrypted"], None),
        ("raw_app_enc_key", "app_basic", ["--encrypted", "--ekpfs-key", TEST_EKPFS_HEX], TEST_EKPFS_HEX),
        ("raw_app_filters", "app_basic", ["--skip-executable-compression", "--min-compress-size", "65536",
                                          "--threshold-gain", "5", "--max-compressed-ratio", "100"], None),
        ("raw_app_level1", "app_basic", ["--compression-level", "1"], None),
        ("raw_fpt_collision", "fpt_collision", [], None),
        ("raw_fpt_collision_cs", "fpt_collision", ["--case-sensitive"], None),
        ("raw_many_files", "many_files", [], None),
        ("raw_ampr", "ampr", [], None),
    ]  # fmt: skip
    for name, tree, flags, key in raw:
        out.append(Case(name, tree, "raw", ["--raw", *flags], ekpfs_hex=key))
    out.append(Case("raw_non_ascii", "non_ascii", "raw", ["--raw"], expect_ok=False))

    # Post-pack verification output (default structure check, and full check with --verify).
    out.append(Case("file_app_postcheck", "app_basic", "file", ["--compress"], post_flags=[]))
    out.append(Case("file_app_fullcheck", "app_basic", "file", ["--compress", "--verify"], post_flags=[]))
    out.append(Case("raw_app_postcheck", "app_basic", "raw", ["--raw"], post_flags=[]))
    out.append(Case("raw_app_fullcheck", "app_basic", "raw", ["--raw", "--verify"], post_flags=[]))
    out.append(Case("folder_app_fullcheck", "app_basic", "folder", ["--verify"], post_flags=[]))

    # batch over a folder item and an exFAT file item: convert, rerun (skipped), dry run.
    out.append(Case("batch_mixed", "app_basic", "batch", ["--cpu-count", "1"]))
    return out


BATCH_TIMING: re.Pattern[str] = re.compile(r"((?:  in |^Total elapsed: ))\d+\.\ds$", re.MULTILINE)


def normalize_log(text: str, cwd: Path) -> str:
    """Replace the absolute case folder with ``<CASE>`` and drop timing-dependent progress lines."""
    root: str = str(cwd.resolve())
    text = text.replace(root.replace("\\", "\\\\"), "<CASE>").replace(root, "<CASE>")
    # batch prints wall-clock times (MkPFS pins time only inside mkpfs.pfs).
    text = BATCH_TIMING.sub(lambda m: m.group(1) + "<t>s", text)
    lines: list[str] = [ln for ln in text.splitlines() if not PROGRESS_LINE.match(ln)]
    return "\n".join(lines) + "\n"


def run_oracle(argv: list[str], cwd: Path, log_name: str) -> int:
    """Run the oracle CLI in ``cwd`` and store combined output in ``log_name``."""
    # UTF-8 so non-ASCII names survive the pipe; MKPFS_NO_UTF8 keeps MkPFS' ASCII icons (WARN/ERROR).
    env: dict[str, str] = {**os.environ, "PYTHONIOENCODING": "utf-8", "MKPFS_NO_UTF8": "1"}
    proc: subprocess.CompletedProcess[str] = subprocess.run(
        [sys.executable, str(ORACLE), *argv],
        cwd=cwd,
        env=env,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=False,
    )
    text: str = (
        f"$ mkpfs {' '.join(argv)}\nexit={proc.returncode}\n--- stdout\n{proc.stdout}\n--- stderr\n{proc.stderr}"
    )
    (cwd / log_name).write_text(normalize_log(text, cwd), encoding="utf-8", newline="\n")
    return proc.returncode


def build_case(case: Case, trees: Path, goldens: Path) -> dict[str, object]:
    """Build one case and its inspection outputs; return its manifest entry."""
    work: Path = goldens / case.name
    if work.exists():
        shutil.rmtree(work)
    work.mkdir(parents=True)
    shutil.copytree(trees / case.tree, work / "src")
    pin_mtimes(work / "src")

    steps: list[tuple[str, list[str]]] = []
    key_flags: list[str] = ["--ekpfs-key", case.ekpfs_hex] if case.ekpfs_hex else []
    if case.kind == "exfat":
        image = "out.exfat"
        steps.append(("build", ["pack", "exfat", "src", image, "--overwrite", "--no-progress", *case.flags]))
        verify_src: list[str] = ["--source-dir", "src"]
    elif case.kind == "batch":
        image = "out"
        shutil.copytree(work / "src", work / "batch" / "PPSA01234-app")
        pin_mtimes(work / "batch")
        steps.append(("pre", ["pack", "exfat", "src", "batch/data.exfat", "--overwrite", "--no-progress"]))
        steps.append(("build", ["batch", "batch", "out", *case.flags]))
        steps.append(("rerun", ["batch", "batch", "out", *case.flags]))
        steps.append(("dry", ["batch", "batch", "dry", "--dry-run", *case.flags]))
        verify_src = []
    elif case.kind == "file":
        image = "out.ffpfsc"
        steps.append(("pre", ["pack", "exfat", "src", "in.exfat", "--overwrite", "--no-progress"]))
        steps.append(("build", ["pack", "file", "in.exfat", image, *case.post_flags, *case.flags]))
        verify_src = ["--source-file", "in.exfat"]
    else:
        image = "out.ffpfsc" if case.kind == "folder" else "out.ffpfs"
        steps.append(("build", ["pack", "folder", "src", image, *case.post_flags,
                                "--no-adjust-output-file-extension", *case.flags]))  # fmt: skip
        verify_src = ["--source-dir", "src"] if case.kind == "raw" else []

    exit_codes: dict[str, int] = {}
    for step, argv in steps:
        exit_codes[step] = run_oracle(argv, work, f"{step}.log")
        if exit_codes[step] != 0:
            break

    built: bool = exit_codes.get("build") == 0 and (work / image).exists()
    if built and case.kind != "batch":
        if case.kind != "exfat":
            exit_codes["inspect"] = run_oracle(["inspect", image, "--format", "json", *key_flags], work, "inspect.log")
        exit_codes["tree"] = run_oracle(["tree", image, *key_flags], work, "tree.log")
        exit_codes["verify"] = run_oracle(["verify", image, *verify_src, *key_flags], work, "verify.log")
        if case.kind in ("file", "folder"):
            exit_codes["tree_deep"] = run_oracle(["tree", image, "--deep", *key_flags], work, "tree_deep.log")
        if not key_flags and "enc" not in case.name:
            write_metadata(work / image, work / "metadata.json")
            write_metadata(work / "src", work / "metadata_src.json")

    # Every stored compressed block must be exactly what Python zlib produces, except in the
    # ISA-L case (MkPFS worker pools used to ignore --compression-backend; see oracle.py).
    zlib_check: dict[str, int] = {"blocks": 0, "mismatches": 0}
    if built and case.kind not in ("exfat", "batch"):
        zlib_check = check_zlib_blocks(work / image, compression_level(case.flags))
    elif built:
        for produced in sorted((work / image).glob("*.ffpfsc")):
            found: dict[str, int] = check_zlib_blocks(produced, compression_level(case.flags))
            zlib_check = {k: zlib_check[k] + found[k] for k in zlib_check}
    expect_pure: bool = "isal" not in case.name
    zlib_ok: bool = (zlib_check["mismatches"] == 0) if expect_pure else (zlib_check["mismatches"] > 0)

    hashes: dict[str, str] = {}
    for path in sorted(work.iterdir()):
        if path.is_file():
            hashes[path.name] = hashlib.sha256(path.read_bytes()).hexdigest()
    if case.kind == "batch" and (work / "out").is_dir():
        for path in sorted((work / "out").iterdir()):
            hashes[f"out/{path.name}"] = hashlib.sha256(path.read_bytes()).hexdigest()
    return {
        "tree": case.tree,
        "kind": case.kind,
        "steps": {s: a for s, a in steps},
        "exit_codes": exit_codes,
        "built": built,
        "expect_ok": case.expect_ok,
        "zlib_check": zlib_check,
        "status_ok": built == case.expect_ok and zlib_ok,
        "note": case.note,
        "sha256": hashes,
    }


def write_metadata(target: Path, out_path: Path) -> None:
    """Store Python ``read_game_metadata`` for ``target`` (icon as SHA-256, no absolute path)."""
    from mkpfs.game_metadata import read_game_metadata  # MkPFS is importable: this script runs in its environment

    meta = read_game_metadata(target)
    record: dict[str, object] = {
        "file_name": meta.file_name,
        "file_size": meta.file_size,
        "game_title": meta.game_title,
        "content_id": meta.content_id,
        "title_id": meta.title_id,
        "package_type": meta.package_type,
        "version": meta.version,
        "region": meta.region,
        "icon_sha256": hashlib.sha256(meta.icon_bytes).hexdigest() if meta.icon_bytes else None,
        "has_apr_emu": meta.has_apr_emu,
        "size_display": meta.size_display,
        "error": meta.error,
    }
    out_path.write_text(json.dumps(record, indent=2, sort_keys=True) + "\n", encoding="utf-8", newline="\n")


def compression_level(flags: list[str]) -> int:
    """Return the ``--compression-level`` in ``flags`` (MkPFS default 7)."""
    for i, flag in enumerate(flags):
        if flag == "--compression-level" and i + 1 < len(flags):
            return int(flags[i + 1])
    return 7


PFSC_SIGNATURE: bytes = b"PFSC" + struct.pack("<II", 0, 6)


def check_zlib_blocks(image: Path, level: int) -> dict[str, int]:
    """Re-compress every compressed PFSC block in ``image`` and count blocks Python zlib would not produce.

    PFSC payloads start on 64 KiB boundaries; encrypted images are skipped (no visible magic).
    """
    data: bytes = image.read_bytes()
    blocks: int = 0
    mismatches: int = 0
    for start in range(0, len(data) - 0x30, 0x10000):
        if data[start : start + 12] != PFSC_SIGNATURE:
            continue
        count: int = struct.unpack_from("<q", data, start + 0x28)[0] // 0x10000
        offsets: tuple[int, ...] = struct.unpack_from(f"<{count + 1}Q", data, start + 0x400)
        for i in range(count):
            stored: bytes = data[start + offsets[i] : start + offsets[i + 1]]
            if len(stored) >= 0x10000:
                continue
            blocks += 1
            if zlib.compress(zlib.decompress(stored), level) != stored:
                mismatches += 1
    return {"blocks": blocks, "mismatches": mismatches}


def write_zlib_vectors(source: Path, out_path: Path, max_blocks: int = 512) -> int:
    """Write ``ZVEC0001`` vectors: (level, raw, zlib.compress(raw, level)) per 64 KiB block.

    Format (little endian): magic[8], u32 count, then per entry u32 level, u32 raw_len,
    u32 comp_len, raw bytes, comp bytes.
    """
    data: bytes = source.read_bytes()
    entries: list[tuple[int, bytes, bytes]] = []
    for i in range(0, min(len(data), max_blocks * 0x10000), 0x10000):
        raw: bytes = data[i : i + 0x10000].ljust(0x10000, b"\x00")
        entries.append((7, raw, zlib.compress(raw, 7)))
    for level in (1, 6, 9):
        raw = data[0x10000 * 3 : 0x10000 * 4].ljust(0x10000, b"\x00")
        entries.append((level, raw, zlib.compress(raw, level)))
    out_path.parent.mkdir(parents=True, exist_ok=True)
    with out_path.open("wb") as fh:
        fh.write(b"ZVEC0001" + struct.pack("<I", len(entries)))
        for level, raw, comp in entries:
            fh.write(struct.pack("<III", level, len(raw), len(comp)) + raw + comp)
    return len(entries)


def build_all(out: Path) -> dict[str, object]:
    """Recreate trees, build every case, write manifest and vectors."""
    trees: Path = out / "trees"
    goldens: Path = out / "goldens"
    make_trees(trees, perf=False)
    goldens.mkdir(parents=True, exist_ok=True)
    manifest: dict[str, object] = {
        "python": sys.version,
        "zlib_runtime": zlib.ZLIB_RUNTIME_VERSION,
        "epoch": 1_700_000_000,
        "cases": {},
    }
    for case in cases():
        entry: dict[str, object] = build_case(case, trees, goldens)
        manifest["cases"][case.name] = entry  # type: ignore[index]
        print(
            f"{'OK  ' if entry['status_ok'] else 'FAIL'} {case.name} {entry['exit_codes']} zlib={entry['zlib_check']}"
        )
    count: int = write_zlib_vectors(goldens / "exfat_app_basic" / "out.exfat", out / "vectors" / "zlib_vectors.bin")
    manifest["zlib_vectors"] = count
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
    parser.add_argument("--out", type=Path, default=DEFAULT_OUT, help="Default: tests/fixtures/generated")
    parser.add_argument("--check", action="store_true", help="Build twice and report nondeterministic files")
    args: argparse.Namespace = parser.parse_args()

    first: dict[str, object] = build_all(args.out)
    failed: list[str] = [n for n, e in first["cases"].items() if not e["status_ok"]]  # type: ignore[union-attr]

    cpu1 = first["cases"]["file_app_cpu1"]["sha256"].get("out.ffpfsc")  # type: ignore[index]
    cpu4 = first["cases"]["file_app_cpu4"]["sha256"].get("out.ffpfsc")  # type: ignore[index]
    print(f"cpu1 == cpu4 image: {cpu1 == cpu4}")

    if args.check:
        second_out: Path = Path(tempfile.mkdtemp(prefix="mkpfs_goldens_check_"))
        second: dict[str, object] = build_all(second_out)
        diffs: list[str] = compare(first, second)
        shutil.rmtree(second_out, ignore_errors=True)
        (args.out / "determinism.json").write_text(json.dumps(diffs, indent=2), encoding="utf-8")
        print(f"nondeterministic files: {len(diffs)}")
        for d in diffs:
            print(f"  {d}")
    print(f"failed cases: {failed}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
