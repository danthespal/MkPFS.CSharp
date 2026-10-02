"""Record Python baseline timings on the ~330 MiB perf tree (zlib backend, level 7).

Usage (from the MkPFS.C# root; ../MkPFS is the Python repo):
    uv run --project ../MkPFS python tools/oracle/bench.py
"""

from __future__ import annotations

import argparse
import json
import os
import platform
import shutil
import subprocess
import sys
import time
from pathlib import Path

HERE: Path = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from make_fixtures import (  # ruff: ignore[module-import-not-at-top-of-file]  (sibling script, not a package)
    pin_mtimes,
    tree_perf,
)

ORACLE: Path = HERE / "oracle.py"
DEFAULT_OUT: Path = HERE.parent.parent / "tests" / "fixtures" / "generated"


def timed(argv: list[str], cwd: Path) -> float:
    """Run one oracle command and return wall seconds (raises on failure)."""
    start: float = time.perf_counter()
    subprocess.run([sys.executable, str(ORACLE), *argv], cwd=cwd, check=True, capture_output=True)
    return time.perf_counter() - start


def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, default=DEFAULT_OUT, help="Default: tests/fixtures/generated")
    args: argparse.Namespace = parser.parse_args()

    work: Path = args.out / "bench"
    if work.exists():
        shutil.rmtree(work)
    (work / "src").mkdir(parents=True)
    tree_perf(work / "src")
    pin_mtimes(work / "src")
    src_bytes: int = sum(p.stat().st_size for p in (work / "src").rglob("*") if p.is_file())

    no_verify: list[str] = ["--no-verify-structure"]
    results: dict[str, float] = {
        "pack_exfat": timed(["pack", "exfat", "src", "perf.exfat", "--overwrite", "--no-progress"], work),
        "pack_file_cpu1": timed(["pack", "file", "perf.exfat", "f1.ffpfsc", "--cpu-count", "1", *no_verify], work),
        "pack_file_auto": timed(["pack", "file", "perf.exfat", "fa.ffpfsc", "--cpu-count", "0", *no_verify], work),
        "pack_folder_auto": timed(["pack", "folder", "src", "fo.ffpfsc", *no_verify], work),
        "pack_raw_auto": timed(["pack", "folder", "src", "raw.ffpfs", "--raw", *no_verify], work),
        "verify_file": timed(["verify", "fa.ffpfsc", "--source-file", "perf.exfat"], work),
        "unpack_deep": timed(["unpack", "fa.ffpfsc", "unpacked", "--deep", "--no-progress"], work),
    }
    report: dict[str, object] = {
        "source_mib": round(src_bytes / 2**20, 1),
        "cpu_count": os.cpu_count(),
        "platform": platform.platform(),
        "python": sys.version.split()[0],
        "seconds": {k: round(v, 2) for k, v in results.items()},
    }
    (args.out / "baseline_timings.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))
    shutil.rmtree(work)


if __name__ == "__main__":
    main()
