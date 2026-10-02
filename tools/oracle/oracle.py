"""Deterministic Python MkPFS runner used as the parity oracle for the .NET port.

Pins every nondeterministic input without touching MkPFS source code:
- ``time.time()`` inside ``mkpfs.pfs`` returns ``MKPFS_ORACLE_EPOCH`` (header/inode timestamps).
- ``uuid.uuid4()`` inside ``mkpfs.pfs`` returns a counter-based UUID (fallback inner names, spools).
- Pack commands get ``--compression-backend zlib`` unless a backend is given explicitly.
- Worker processes use that backend too. MkPFS itself does not pass it to its process pools
  (they fall back to the ``auto`` default, ISA-L), so ``mp.Pool`` is wrapped to set it.

Usage:
    uv run --project ../MkPFS python tools/oracle/oracle.py pack file in.exfat out.ffpfsc
"""

from __future__ import annotations

import multiprocessing as _real_mp
import os
import sys
import time as _real_time
import uuid as _real_uuid
from collections.abc import Callable
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


def _init_worker_backend(backend: str, initializer: Callable[..., None] | None, initargs: tuple[object, ...]) -> None:
    """Run MkPFS' own pool initializer, then force the requested compression backend."""
    if initializer is not None:
        initializer(*initargs)
    from mkpfs import compression  # imported in the worker process

    compression.set_backend(backend)


def _backend_pool_module(backend: str) -> SimpleNamespace:
    """Return a ``multiprocessing`` stand-in whose ``Pool`` workers use ``backend``."""

    def pool(
        *args: object,
        initializer: Callable[..., None] | None = None,
        initargs: tuple[object, ...] = (),
        **kwargs: object,
    ) -> object:
        return _real_mp.Pool(
            *args, initializer=_init_worker_backend, initargs=(backend, initializer, initargs), **kwargs
        )

    shim: SimpleNamespace = SimpleNamespace(
        **{k: getattr(_real_mp, k) for k in dir(_real_mp) if not k.startswith("__")}
    )
    shim.Pool = pool
    return shim


def selected_backend(argv: list[str]) -> str:
    """Return the ``--compression-backend`` value in ``argv`` (``zlib`` when absent)."""
    for i, arg in enumerate(argv):
        if arg.startswith("--compression-backend="):
            return arg.split("=", 1)[1]
        if arg == "--compression-backend" and i + 1 < len(argv):
            return argv[i + 1]
    return "zlib"


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
    full_argv: list[str] = inject_backend(argv)
    pfs_module.mp = _backend_pool_module(selected_backend(full_argv))
    return cli_main(full_argv)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
