"""Build reference PS5 packages with PS5PkgTool's ProsperoPkgTool engine (FPKG plan F5).

PS5PkgTool (../PS5PkgTool, pearlxcore, GPL-3.0) is the format target for `pack fpkg`. Its package engine,
ThirdParty/ProsperoPkgTool/ProsperoPkgTool.dll (MIT), ships without source, so it is used as a black box:
this script builds runner/PPTOracle.csproj against the checkout and runs it on the fixture trees. Outputs go
to the git-ignored corpus, tests/fixtures/generated/fpkg/ppt/<tree>_<mode>/out.pkg, plus manifest.json.

With a seed the engine is byte-reproducible (fixed clock); `--check` builds every case twice and compares.

Usage:
    python tools/oracle-ppt/build_ppt_goldens.py [--check] [--trees hb_min app_multi]
"""

from __future__ import annotations

import argparse
import hashlib
import json
import random
import shutil
import subprocess
import sys
from pathlib import Path

REPO: Path = Path(__file__).resolve().parents[2]
CORPUS: Path = REPO / "tests" / "fixtures" / "generated" / "fpkg"
ORACLE: Path = REPO.parent / "PS5PkgTool"
PINNED_COMMIT = "61874dfe0f4bbf23c61d421010c9fd86bb3a68d2"
ENGINE_DLL = "PS5PKGTool/ThirdParty/ProsperoPkgTool/ProsperoPkgTool.dll"
ENGINE_SHA256 = "f6ca7f56c0d5577c426a7b569a939819dda77e419ffceaa42ded3472897aa6b8"

CONTENT_ID = "UP9000-PPSA99999_00-MKPFSORACLE00000"
PASSCODE = "0" * 32
SEED = "000102030405060708090a0b0c0d0e0f"
MODES = ("stored", "auto")


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def run(argv: list[str], cwd: Path | None = None) -> subprocess.CompletedProcess[str]:
    return subprocess.run(argv, cwd=cwd, capture_output=True, text=True, encoding="utf-8", errors="replace")


def check_oracle(allow_commit: bool) -> None:
    commit = run(["git", "rev-parse", "HEAD"], cwd=ORACLE).stdout.strip()
    if commit != PINNED_COMMIT and not allow_commit:
        raise SystemExit(f"{ORACLE} is at {commit}, expected {PINNED_COMMIT} (--allow-commit overrides)")
    dll = sha256(ORACLE / ENGINE_DLL)
    if dll != ENGINE_SHA256 and not allow_commit:
        raise SystemExit(f"{ENGINE_DLL} SHA-256 is {dll}, expected {ENGINE_SHA256}")


def build_runner() -> Path:
    bin_dir = REPO / "tmp" / "oracle-ppt" / "bin"
    proc = run(["dotnet", "build", str(Path(__file__).parent / "runner" / "PPTOracle.csproj"), "-c", "Release",
                "-o", str(bin_dir), "-nologo", "-v", "q", f"-p:PS5PkgToolDir={ORACLE}{Path('/')}"])
    if proc.returncode != 0:
        raise SystemExit("runner build failed:\n" + proc.stdout + proc.stderr)
    return bin_dir / "PPTOracle.exe"


# PS5PkgTool-only trees (fpkg/ppt-trees), each hb_min plus extra files:
# * app_pack pins the stored packing rule (a file joins the current run unless it would cross a 64 KiB block) and
#   a last zero-gap chunk of at most 128 KiB.
# * app_many: see its entry.
# * app_kraken pins the auto policy: thr.bin has 32 128 KiB chunks whose random prefix shrinks by 0x200 per chunk
#   (the compress-or-store threshold); compressible text under a SELF magic, a .prx name and in sce_sys (which
#   files are compressed at all).
def _text(size: int) -> bytes:
    line = b"PS5PkgTool probe line with some repeated words, words, words.\n"
    return (line * (size // len(line) + 1))[:size]


def _self_magic(size: int) -> bytes:
    return b"O=" + _text(size - 4)


def _threshold(rng: random.Random) -> bytes:
    return b"".join(rng.randbytes(0x20000 - (i + 1) * 0x200) + bytes((i + 1) * 0x200) for i in range(32))


PPT_TREES: dict[str, list[tuple[str, object]]] = {
    "app_pack": [(f"data/{stem}.bin", size) for stem, size in
                 [("a_100", 0x100), ("b_9000", 0x9000), ("c_7000", 0x7000), ("d_50", 0x50), ("e_fff0", 0xFFF0)]],
    "app_kraken": [("data/thr.bin", _threshold), ("data/magic.bin", lambda _: _self_magic(0x40000)),
                   ("data/named.prx", lambda _: _text(0x40000)), ("data/text.txt", lambda _: _text(0x40000)),
                   ("sce_sys/extra.dat", lambda _: _text(0x40000))],
    # app_many: 4,514 files, so the inode table, a directory's dirents and the FLTs span several blocks, a
    # ublock fills up with 30 records (the next file moves one ublock on) and the naps spans two blocks.
    "app_many": [(f"data/x/f{i:05d}.bin", lambda _, i=i: i.to_bytes(4, "little") * 2) for i in range(4500)]
                + [(f"data/y/g{i}.txt", lambda _, i=i: b"y" * (i + 1)) for i in range(10)],
}


def make_ppt_trees() -> None:
    for name, files in PPT_TREES.items():
        tree = CORPUS / "ppt-trees" / name
        if tree.exists():
            shutil.rmtree(tree)
        shutil.copytree(CORPUS / "trees" / "hb_min", tree)
        rng = random.Random(7)
        for rel, content in files:
            path = tree / rel
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(rng.randbytes(content) if isinstance(content, int) else content(rng))  # type: ignore[operator]


def trees() -> list[Path]:
    found = sorted(p for p in (CORPUS / "trees").iterdir() if p.is_dir())
    for extra in (CORPUS / "sdk-trees", CORPUS / "ppt-trees"):
        if extra.exists():
            found += sorted(p for p in extra.iterdir() if p.is_dir())
    return found


def build_case(runner: Path, tree: Path, mode: str, out_dir: Path) -> dict[str, object]:
    if out_dir.exists():
        shutil.rmtree(out_dir)
    out_dir.mkdir(parents=True)
    pkg = out_dir / "out.pkg"
    proc = run([str(runner), str(tree), str(pkg), mode, CONTENT_ID, PASSCODE, SEED])
    (out_dir / "build.log").write_text(proc.stdout + proc.stderr, encoding="utf-8")
    record: dict[str, object] = {"tree": tree.name, "mode": mode, "exit": proc.returncode}
    if proc.returncode == 0 and pkg.exists():
        record["sha256"] = sha256(pkg)
        record["size"] = pkg.stat().st_size
    else:
        record["error"] = next((line for line in proc.stdout.splitlines() if line.startswith("error ")), proc.stderr.strip())
    return record


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--trees", nargs="*", help="Fixture trees (default: all)")
    parser.add_argument("--check", action="store_true", help="Build twice and compare")
    parser.add_argument("--allow-commit", action="store_true", help="Accept another PS5PkgTool commit or engine")
    args = parser.parse_args()
    if sys.platform != "win32":
        raise SystemExit("PS5PkgTool targets Windows")
    if not (CORPUS / "trees").exists():
        raise SystemExit("fixture trees are missing: run tools/oracle-fpkg/build_fpkg_goldens.py first")

    check_oracle(args.allow_commit)
    make_ppt_trees()
    runner = build_runner()
    selected = [t for t in trees() if not args.trees or t.name in args.trees]
    root = CORPUS / "ppt"
    manifest_path = root / "manifest.json"
    manifest: dict[str, object] = {"oracle": PINNED_COMMIT, "engine_sha256": ENGINE_SHA256, "content_id": CONTENT_ID,
                                   "passcode": PASSCODE, "seed": SEED, "cases": {}}
    if args.trees and manifest_path.exists():
        manifest["cases"] = json.loads(manifest_path.read_text(encoding="utf-8")).get("cases", {})
    nondeterministic: list[str] = []
    for tree in selected:
        for mode in MODES:
            name = f"{tree.name}_{mode}"
            print(f"  {name} ...", flush=True)
            record = build_case(runner, tree, mode, root / name)
            if args.check and record["exit"] == 0:
                again = build_case(runner, tree, mode, REPO / "tmp" / "oracle-ppt" / "check" / name)
                if again.get("sha256") != record["sha256"]:
                    nondeterministic.append(name)
            manifest["cases"][name] = record  # type: ignore[index]
    root.mkdir(parents=True, exist_ok=True)
    manifest_path.write_text(json.dumps(manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    failed = [n for n, r in manifest["cases"].items() if r["exit"] != 0]  # type: ignore[union-attr]
    print(f"Built {len(manifest['cases']) - len(failed)}/{len(manifest['cases'])}; failed: {failed or 'none'}")  # type: ignore[arg-type]
    if args.check:
        print(f"Nondeterministic: {nondeterministic or 'none'}")
        return 1 if nondeterministic else 0
    return 0


if __name__ == "__main__":
    sys.exit(main())
