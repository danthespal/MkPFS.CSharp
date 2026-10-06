"""Build reference PS5 packages with Sony's Publishing Tools (local SDK toolkit) for the FPKG port.

Uses the plaintext/no-auth toolkit at ../sdk-fpkg279-fix12 (prospero-pub-cmd img_create --oformat
nwonly). The SDK is not part of MkPFS: it is never copied, committed or shipped, and its outputs stay in
the git-ignored corpus. They are format references only (real NAPS, Kraken framing and inner layout).

Run build_fpkg_goldens.py first: this script reuses its fixture trees, keystone and fSELF vectors.

Usage:
    python tools/oracle-fpkg/build_sdk_refs.py
    python tools/oracle-fpkg/build_sdk_refs.py --sdk D:/tools/sdk-fpkg279-fix12 --trees hb_min app_multi
"""

from __future__ import annotations

import argparse
import hashlib
import json
import random
import re
import shutil
import subprocess
import sys
from pathlib import Path

REPO: Path = Path(__file__).resolve().parents[2]
CORPUS: Path = REPO / "tests" / "fixtures" / "generated" / "fpkg"
DEFAULT_SDK: Path = REPO.parent / "sdk-fpkg279-fix12"


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


# Trees only this script builds (MkPFS reader coverage, FPKG plan F5): hb_min plus generated data.
# app_fill: constant-filled files (Publishing Tools stores each as one 8-byte Kraken fill block).
# app_large: ~120 MiB of seeded random data, past 12 + 1820 outer blocks (double-indirect pfs_image.dat).
SDK_ONLY_TREES: dict[str, str] = {"app_fill": "hb_min", "app_large": "hb_min"}


def make_sdk_only_tree(name: str, dest: Path) -> None:
    base: Path = CORPUS / "trees" / SDK_ONLY_TREES[name]
    if dest.exists():
        shutil.rmtree(dest)
    shutil.copytree(base, dest)
    data: Path = dest / "data"
    data.mkdir()
    if name == "app_fill":
        for value in (0x00, 0x01, 0x78, 0x83, 0xFF):
            for size in (1, 3000, 0x10000, 0x20001, 0x50000):
                (data / f"v{value:02X}_{size:X}.bin").write_bytes(bytes([value]) * size)
    else:
        rng = random.Random(0x5EED)
        with open(data / "large.bin", "wb") as out:
            for _ in range(120):
                out.write(rng.randbytes(1 << 20))
        (data / "after.txt").write_bytes(b"x" * 1001)


def stage_tree(tree: Path, dest: Path, vectors: dict[str, object]) -> None:
    """Copy a fixture tree, add the passcode keystone and swap ELF modules for their fSELF vectors."""
    shutil.copytree(tree, dest)
    keystone: str = vectors["keystone"][0]["keystone"]  # type: ignore[index]
    (dest / "sce_sys").mkdir(exist_ok=True)
    (dest / "sce_sys" / "keystone").write_bytes(bytes.fromhex(keystone))
    vector_tree: str = SDK_ONLY_TREES.get(tree.name, tree.name)
    for item in vectors["fself"]:  # type: ignore[union-attr]
        source: str = item["source"]
        if not source.startswith(vector_tree + "/") or "sha256" not in item:
            continue
        rel: str = source[len(vector_tree) + 1:]
        (dest / rel).write_bytes((CORPUS / "vectors" / item["vector"]).read_bytes())


def build(sdk: Path, src: Path, out_dir: Path) -> dict[str, object]:
    pkg: Path = out_dir / "out.pkg"
    proc = subprocess.run(
        ["cmd", "/c", str(sdk / "build.bat"), str(src), str(pkg), "force", "keep"],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
    )
    (out_dir / "build.log").write_text(proc.stdout + proc.stderr, encoding="utf-8")
    record: dict[str, object] = {"exit": proc.returncode}
    if proc.returncode == 0 and pkg.exists():
        record["out.pkg"] = sha256(pkg)
        record["size"] = pkg.stat().st_size
    return record


def single_chunk(sdk: Path, out_dir: Path, name: str, drm: str) -> dict[str, object]:
    """Rebuild the wrapper's GP5 with one PlayGo chunk and no language payload (the pack fpkg profile).

    The GP5 wrapper always declares 100 chunks and adds a 1 MiB language file; Publishing Tools accepts a
    project without either. The param.json is the wrapper's normalized copy with applicationDrmType set.
    """
    gp5: str = (out_dir / "out.gp5").read_text(encoding="utf-8")
    gp5 = gp5.replace('chunk_count="100"', 'chunk_count="1"').replace('initial_chunk_count="100"', 'initial_chunk_count="1"')
    gp5 = gp5.replace(">0-99<", ">0<")
    gp5 = re.sub(r'\s*<chunk id="[1-9][0-9]*"[^>]*/>', "", gp5)
    gp5 = re.sub(r'\s*<file dst_path="playgo-languages/[^>]*/>', "", gp5)
    param_src: Path = out_dir / ".gp5-assets" / "out" / "sce_sys" / "param.json"
    param: dict[str, object] = json.loads(param_src.read_text(encoding="utf-8"))
    param["applicationDrmType"] = drm
    param_dst: Path = out_dir / f"{name}.param.json"
    param_dst.write_text(json.dumps(param, indent=2) + "\n", encoding="utf-8")
    gp5 = gp5.replace(str(param_src), str(param_dst))
    project: Path = out_dir / f"{name}.gp5"
    project.write_text(gp5, encoding="utf-8")
    pkg: Path = out_dir / f"{name}.pkg"
    proc = subprocess.run(
        [str(sdk / "toolchain" / "prospero-pub-cmd.exe"), "img_create", "--oformat", "nwonly",
         "--compression_level", "7", str(project), str(pkg)],
        capture_output=True, text=True, encoding="utf-8", errors="replace", cwd=out_dir,
    )
    (out_dir / f"{name}.log").write_text(proc.stdout + proc.stderr, encoding="utf-8")
    record: dict[str, object] = {"exit": proc.returncode}
    if proc.returncode == 0 and pkg.exists():
        record["sha256"] = sha256(pkg)
        record["size"] = pkg.stat().st_size
    return record


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--sdk", type=Path, default=DEFAULT_SDK, help="sdk-fpkg279 toolkit folder")
    parser.add_argument("--trees", nargs="*", help="Fixture trees (default: all)")
    args = parser.parse_args()

    if sys.platform != "win32":
        raise SystemExit("the SDK toolkit is Windows-only")
    if not (args.sdk / "toolchain" / "prospero-pub-cmd.exe").exists():
        raise SystemExit(f"Publishing Tools not found under {args.sdk}")
    vectors_path: Path = CORPUS / "vectors" / "vectors.json"
    if not vectors_path.exists():
        raise SystemExit("run build_fpkg_goldens.py first")
    vectors: dict[str, object] = json.loads(vectors_path.read_text(encoding="utf-8"))

    trees: list[Path] = sorted(p for p in (CORPUS / "trees").iterdir() if p.is_dir())
    trees += [CORPUS / "sdk-trees" / name for name in SDK_ONLY_TREES]
    if args.trees:
        trees = [t for t in trees if t.name in args.trees]
    for tree in trees:
        if tree.name in SDK_ONLY_TREES:
            make_sdk_only_tree(tree.name, tree)
    # A partial run (--trees) keeps the other trees' records.
    manifest_path: Path = CORPUS / "sdk" / "manifest.json"
    manifest: dict[str, object] = {"sdk": str(args.sdk), "trees": {}}
    if args.trees and manifest_path.exists():
        manifest["trees"] = json.loads(manifest_path.read_text(encoding="utf-8")).get("trees", {})
    for tree in trees:
        print(f"  {tree.name} ...", flush=True)
        out_dir: Path = CORPUS / "sdk" / tree.name
        if out_dir.exists():
            shutil.rmtree(out_dir)
        # The staged input stays next to the package: the parity tests compare extracted files with it.
        src: Path = out_dir / "src"
        stage_tree(tree, src, vectors)
        record: dict[str, object] = build(args.sdk, src, out_dir)
        if record["exit"] == 0:
            record["single"] = single_chunk(args.sdk, out_dir, "single", "standard")
            record["free"] = single_chunk(args.sdk, out_dir, "free", "free")
        manifest["trees"][tree.name] = record  # type: ignore[index]
    (CORPUS / "sdk").mkdir(exist_ok=True)
    manifest_path.write_text(json.dumps(manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    failed: list[str] = [name for name, rec in manifest["trees"].items()  # type: ignore[union-attr]
                         if any(t.name == name for t in trees) and (rec["exit"] != 0
                         or any(rec.get(v, {"exit": 0})["exit"] != 0 for v in ("single", "free")))]
    print(f"Built {len(trees) - len(failed)}/{len(trees)}; failed: {failed or 'none'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
