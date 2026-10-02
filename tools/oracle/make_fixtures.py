"""Generate deterministic fixture source trees for the .NET port parity tests.

Every byte and every mtime is derived from fixed seeds, so two runs produce identical trees.

Usage:
    uv run --project ../MkPFS python tools/oracle/make_fixtures.py --out tests/fixtures/generated/trees
    uv run --project ../MkPFS python tools/oracle/make_fixtures.py --out ... --perf   # adds ~330 MiB tree
"""

from __future__ import annotations

import argparse
import json
import os
import random
import shutil
import struct
import zlib
from collections.abc import Callable
from pathlib import Path

FIXED_MTIME: int = 1_600_000_000
BLOCK: int = 0x10000


def _png_1x1() -> bytes:
    """Return a valid 1x1 RGBA PNG."""

    def chunk(kind: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))

    ihdr: bytes = struct.pack(">IIBBBBB", 1, 1, 8, 6, 0, 0, 0)
    idat: bytes = zlib.compress(b"\x00\xff\x00\x00\xff", 9)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + chunk(b"IDAT", idat) + chunk(b"IEND", b"")


def _text(rng: random.Random, size: int) -> bytes:
    """Return compressible pseudo-text."""
    words: list[bytes] = [b"pfs", b"block", b"inode", b"sony", b"game", b"asset", b"texture", b"mesh", b"\n"]
    out: bytearray = bytearray()
    while len(out) < size:
        out += rng.choice(words) + b" "
    return bytes(out[:size])


def _semi(rng: random.Random, size: int) -> bytes:
    """Return executable-like data: random runs mixed with repeated structures."""
    out: bytearray = bytearray()
    while len(out) < size:
        if rng.random() < 0.5:
            out += rng.randbytes(rng.randint(16, 512))
        else:
            out += bytes([rng.randint(0, 255)]) * rng.randint(8, 256)
    return bytes(out[:size])


def _long_range(rng: random.Random, blocks: int) -> bytes:
    """Return 64 KiB blocks whose second half repeats the first at distance 32700.

    zlib never emits back-references beyond 32506 bytes, ISA-L does; this data lets tests tell
    the encoders apart (see the PS5 bad-block investigation).
    """
    out: bytearray = bytearray()
    for _ in range(blocks):
        head: bytes = _semi(rng, 32700)  # random data makes ISA-L levels 1-2 skip matching
        out += (head + head)[:BLOCK]
    return bytes(out)


def _param_json(title_id: str) -> bytes:
    data: dict[str, object] = {
        "titleId": title_id,
        "contentId": f"UP0000-{title_id}_00-0000000000000000",
        "applicationCategoryType": 0,
        "localizedParameters": {"defaultLanguage": "en-US", "en-US": {"titleName": "Fixture Game"}},
    }
    return json.dumps(data, indent=2, sort_keys=True).encode("ascii")


def _write(root: Path, rel: str, data: bytes) -> None:
    path: Path = root / rel
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)


def tree_app_basic(root: Path) -> None:
    """Typical small game layout with every size edge case."""
    rng: random.Random = random.Random(1)
    _write(root, "sce_sys/param.json", _param_json("PPSA00001"))
    _write(root, "sce_sys/icon0.png", _png_1x1())
    _write(root, "eboot.bin", _semi(rng, 300_000))
    _write(root, "sce_module/libfixture.prx", _semi(rng, 150_000))
    _write(root, "data/text.txt", _text(rng, 700_000))
    _write(root, "data/random.bin", rng.randbytes(200_000))
    _write(root, "data/zero_len.bin", b"")
    _write(root, "data/exact_block.bin", _text(rng, BLOCK))
    _write(root, "data/block_plus_one.bin", _text(rng, BLOCK + 1))
    _write(root, "data/zeros.bin", bytes(1024 * 1024))
    _write(root, "data/long_range.bin", _long_range(rng, blocks=4))
    _write(root, "a/b/c/d/deep.txt", _text(rng, 5000))
    (root / "empty_dir").mkdir(parents=True, exist_ok=True)


def tree_fpt_collision(root: Path) -> None:
    """Two paths with the same flat_path_table hash ("AZ" and "B;" collide: 31*65+90 == 31*66+59)."""
    rng: random.Random = random.Random(2)
    _write(root, "sce_sys/param.json", _param_json("PPSA00002"))
    _write(root, "eboot.bin", _semi(rng, 70_000))
    _write(root, "col/dataAZ.bin", _text(rng, 90_000))
    _write(root, "col/dataB;.bin", _text(rng, 80_000))


def tree_many_files(root: Path) -> None:
    """600 files with 100-char names in one directory.

    600 inodes need 2 inode blocks (390 D32 inodes per 64 KiB block) and the dirents
    (~120 bytes each) span 2 directory blocks, while the tree stays small on disk.
    """
    rng: random.Random = random.Random(3)
    _write(root, "sce_sys/param.json", _param_json("PPSA00003"))
    _write(root, "eboot.bin", _semi(rng, 40_000))
    for i in range(600):
        _write(root, f"many/{'n' * 87}_file_{i:04d}.bin", _text(rng, rng.randint(0, 3000)))


def tree_ampr(root: Path) -> None:
    """Emulation layout that triggers ampr_emu.index generation."""
    rng: random.Random = random.Random(4)
    _write(root, "sce_sys/param.json", _param_json("PPSA00004"))
    _write(root, "eboot.bin", _semi(rng, 90_000))
    _write(root, "fakelib/libSceAmpr.sprx", _semi(rng, 20_000))
    _write(root, "assets/pack0.bin", _text(rng, 260_000))
    _write(root, "assets/Sub/Pack1.BIN", _text(rng, 30_000))


def tree_non_ascii(root: Path) -> None:
    """Negative case: PFS dirents only allow ASCII names."""
    _write(root, "sce_sys/param.json", _param_json("PPSA00005"))
    _write(root, "données.txt", b"hello")


def tree_perf(root: Path) -> None:
    """~330 MiB mixed tree for baseline timings; above the 256 MiB single-file parallel threshold."""
    rng: random.Random = random.Random(5)
    _write(root, "sce_sys/param.json", _param_json("PPSA00009"))
    _write(root, "eboot.bin", _semi(rng, 8 * 1024 * 1024))
    for i in range(40):
        _write(root, f"assets/text_{i:02d}.dat", _text(rng, 4 * 1024 * 1024))
        _write(root, f"assets/rand_{i:02d}.dat", rng.randbytes(2 * 1024 * 1024))
        _write(root, f"assets/semi_{i:02d}.dat", _semi(rng, 2 * 1024 * 1024))


TREES: dict[str, Callable[[Path], None]] = {
    "app_basic": tree_app_basic,
    "fpt_collision": tree_fpt_collision,
    "many_files": tree_many_files,
    "ampr": tree_ampr,
    "non_ascii": tree_non_ascii,
}


def pin_mtimes(root: Path) -> None:
    """Set every file and directory mtime/atime to ``FIXED_MTIME`` (deepest first)."""
    for dirpath, dirnames, filenames in os.walk(root, topdown=False):
        for name in [*filenames, *dirnames]:
            os.utime(Path(dirpath) / name, (FIXED_MTIME, FIXED_MTIME))
    os.utime(root, (FIXED_MTIME, FIXED_MTIME))


def make_trees(out: Path, *, perf: bool) -> list[Path]:
    """Recreate all fixture trees under ``out`` and return their roots."""
    builders: dict[str, Callable[[Path], None]] = dict(TREES)
    if perf:
        builders["perf"] = tree_perf
    roots: list[Path] = []
    for name, build in builders.items():
        root: Path = out / name
        if root.exists():
            shutil.rmtree(root)
        root.mkdir(parents=True)
        build(root)
        pin_mtimes(root)
        roots.append(root)
    return roots


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--perf", action="store_true", help="Also build the ~330 MiB perf tree")
    args: argparse.Namespace = parser.parse_args()
    for root in make_trees(args.out, perf=args.perf):
        print(root)


if __name__ == "__main__":
    main()
