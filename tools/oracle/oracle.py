"""Deterministic Python MkPFS runner used as the parity oracle for the .NET port.

Pins every nondeterministic input without touching MkPFS source code:
- ``time.time()`` inside ``mkpfs.pfs`` returns ``MKPFS_ORACLE_EPOCH`` (header/inode timestamps).
- ``uuid.uuid4()`` inside ``mkpfs.pfs`` returns a counter-based UUID (fallback inner names, spools).
- Pack commands get ``--compression-backend zlib`` unless a backend is given explicitly.

Usage:
    uv run --project ../MkPFS python tools/oracle/oracle.py pack file in.exfat out.ffpfsc
"""

from __future__ import annotations

import os
import sys
import time as _real_time
import uuid as _real_uuid
from types import SimpleNamespace

DEFAULT_EPOCH: int = 1_700_000_000
PACK_WITH_BACKEND: frozenset[str] = frozenset({"folder", "file"})


def _frozen_time_module(epoch: int) -> SimpleNamespace:
    """Return a ``time`` stand-in whose ``time()`` is constant."""
    shim: SimpleNamespace = SimpleNamespace(
        **{k: getattr(_real_time, k) for k in dir(_real_time) if not k.startswith("__")}
    )
    shim.time = lambda: float(epoch)
    return shim


def _counter_uuid_module() -> SimpleNamespace:
    """Return a ``uuid`` stand-in whose ``uuid4()`` is a deterministic counter."""
    state: dict[str, int] = {"n": 0}

    def uuid4() -> _real_uuid.UUID:
        state["n"] += 1
        return _real_uuid.UUID(int=state["n"])

    shim: SimpleNamespace = SimpleNamespace(
        **{k: getattr(_real_uuid, k) for k in dir(_real_uuid) if not k.startswith("__")}
    )
    shim.uuid4 = uuid4
    return shim


def inject_backend(argv: list[str]) -> list[str]:
    """Append ``--compression-backend zlib`` to pack folder/file and batch commands when missing."""
    if any(a.startswith("--compression-backend") for a in argv):
        return argv
    is_pack: bool = len(argv) >= 2 and argv[0] == "pack" and argv[1] in PACK_WITH_BACKEND
    is_batch: bool = len(argv) >= 1 and argv[0] == "batch"
    if is_pack or is_batch:
        return [*argv, "--compression-backend", "zlib"]
    return argv


def main(argv: list[str]) -> int:
    """Patch nondeterminism, then run the regular MkPFS CLI."""
    epoch: int = int(os.environ.get("MKPFS_ORACLE_EPOCH", str(DEFAULT_EPOCH)))
    import mkpfs.pfs as pfs_module  # imported after env parsing so the patch targets the live module
    from mkpfs.cli import main as cli_main

    pfs_module.time = _frozen_time_module(epoch)
    pfs_module.uuid = _counter_uuid_module()
    return cli_main(inject_backend(argv))


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
