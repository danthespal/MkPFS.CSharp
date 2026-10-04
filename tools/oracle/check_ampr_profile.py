"""Check ``mkpfs ampr profile`` against ampr_emu's ``tools/ampr_pack_profile.py`` (the oracle).

Writes synthetic APR traces (``AMPRCMD1`` journals and their ``AMPRIDX3`` indexes: mixed read patterns, several
runs, gather/scatter state, corrupt records, an unsafe path, content for LZ4 sampling), runs both tools on the same
inputs with option sets that reach every branch (budget enforcement, cache-sampling windows, pattern modes, include
sidecars, sampling, option errors; ``batch`` on folders and ZIP bundles) and compares exit codes, stdout, the last
stderr line and every output file (TOML, sidecars, report, metrics JSON, runtime header, batch summary) byte for
byte. Only the random temporary folder of an extracted ZIP is normalized.

Needs ampr_emu at ``ORACLE_COMMIT`` in ``../ampr_emu`` and a Python with python-lz4 4.4.5 for the oracle; build
MkPFS first. From the repo root:
    dotnet build src/MkPFS.Cli
    uv run --no-project --python 3.12 --with lz4==4.4.5 python tools/oracle/check_ampr_profile.py
"""

from __future__ import annotations

import argparse
import random
import re
import shutil
import struct
import subprocess
import sys
import zipfile
from pathlib import Path

HERE: Path = Path(__file__).resolve().parent
REPO: Path = HERE.parent.parent
AMPR_EMU: Path = REPO.parent / "ampr_emu"
DEFAULT_OUT: Path = REPO / "tests" / "fixtures" / "generated" / "ampr_profile"
DEFAULT_MKPFS: Path = REPO / "src" / "MkPFS.Cli" / "bin" / "Debug" / "net10.0" / "mkpfs"

HEADER = struct.Struct("<8sHHIQQQQQQIIIIIIII")


def fnv(data: bytes) -> int:
    h = 1469598103934665603
    for b in data:
        h ^= b
        h = (h * 1099511628211) & 0xFFFFFFFFFFFFFFFF
    return h or 1


def write_index(path: Path, entries: list[tuple[str, int]]) -> None:
    blob = b""
    records = b""
    for p, size in entries:
        raw = p.encode("utf-8", "surrogateescape")
        records += struct.pack("<IIQq", len(blob), len(raw), size, 1700000000)
        blob += raw
    header_size = 48
    path_start = header_size + len(records)
    hash_offset = path_start + len(blob)
    slots = b"\0" * 16 * 4
    head = struct.pack("<8sIIQQQII", b"AMPRIDX3", 3, 24, len(entries), len(blob), hash_offset, 16, 4)
    path.write_bytes(head + records + blob + slots)


def w(v: int) -> bytes:
    return struct.pack("<I", v & 0xFFFFFFFF)


def read_file(file_id: int, offset: int, length: int, wide: bool = False) -> bytes:
    dwords = 6 if (wide or offset >= 1 << 32) else 5
    w0 = 40 | ((dwords - 1) << 8) | ((offset & 0x3FFFF) << 12)
    w4 = (offset & 0xFFFC0000) | 0x1234
    out = w(w0) + w(length - 1) + w(file_id) + w(0xDEAD0000) + w(w4)
    if dwords == 6:
        out += w((offset >> 32) & 0xFF)
    return out


def gather(offset: int, length: int) -> bytes:
    dwords = 3 if offset >= 1 << 18 else 2
    w0 = 41 | ((dwords - 1) << 8) | ((offset & 0x3FFFF) << 12)
    out = w(w0) + w(length - 1)
    if dwords == 3:
        out += w((offset >> 18) & 0x3FFFFF)
    return out


def scatter(length: int) -> bytes:
    return w(0x22A | (0x12 << 12)) + w(length - 1) + w(0x1000)


def gather_scatter(offset: int, length: int, wide: bool = False) -> bytes:
    dwords = 5 if (wide or offset >= 1 << 32) else 4
    w0 = 43 | ((dwords - 1) << 8) | ((offset & 0x3FFFF) << 12)
    out = w(w0) + w(length - 1) + w(0x2000) + w((offset & 0xFFFC0000) | 7)
    if dwords == 5:
        out += w((offset >> 32) & 0xFF)
    return out


RESET = w(47)
MAP_END = w(46)


def noise(rng: random.Random) -> bytes:
    choice = rng.randrange(9)
    if choice == 0:  # WaitOnAddress 3 dwords
        return w(1 | (2 << 8)) + w(0x1000) + w(5)
    if choice == 1:  # WaitOnCounter 2 dwords with extra
        return w(2 | (1 << 8)) + w(0x31000005)
    if choice == 2:  # WriteAddress 3 dwords
        return w(5 | (2 << 8)) + w(0x2000 | 2) + w(9)
    if choice == 3:  # WriteCounter 3 dwords
        return w(118 | (2 << 8)) + w(0x00100001) + w(3)
    if choice == 4:  # WriteKernelEventQueue
        return w(1032) + w(1) + w(2) + w(3) + w(4)
    if choice == 5:  # marker set "hi"
        return w(0x5452000F | (1 << 12) | (1 << 8)) + b"hi\0\0"
    if choice == 6:  # MarkerPop
        return w(0x5452300F)
    if choice == 7:  # AprMapBegin + end
        return w(557) + w(1) + w(2) + MAP_END
    return w(0x325) + w(1) + w(2) + w(3)  # AmmMapDirect


class Trace:
    def __init__(self, path: Path, rng: random.Random) -> None:
        self.path = path
        self.rng = rng
        self.data = bytearray()
        self.seq = 0
        self.time = 1_000_000_000

    def record(self, commands: list[bytes], priority: int = 0, domain: int = 1, *, seq: int | None = None,
               bad_hash: bool = False, count: int | None = None, extra: bytes = b"", noise_rate: float = 0.2) -> None:
        parts: list[bytes] = []
        for c in commands:
            if self.rng.random() < noise_rate:
                parts.append(noise(self.rng))
            parts.append(c)
        payload = b"".join(parts)
        self.seq = self.seq + 1 if seq is None else seq
        self.time += self.rng.randrange(1000, 5_000_000)
        n = count if count is not None else 0
        h = fnv(payload) ^ (1 if bad_hash else 0)
        header_bytes = HEADER.size + len(extra)
        head = HEADER.pack(b"AMPRCMD1", 1, header_bytes, header_bytes + len(payload), self.seq, 0, self.time, 0, 0, h,
                           len(payload), 4096, n, priority, domain, 0, 0, 0)
        self.data += head + extra + payload

    def raw(self, data: bytes) -> None:
        self.data += data

    def save(self) -> None:
        self.path.write_bytes(bytes(self.data))


def make_files(rng: random.Random, count: int, prefix: str = "") -> list[tuple[str, int]]:
    exts = [".dat", ".bin", ".pak", ".bik", ".wem", ".prx", ".assets", ".bank", ".arc", ""]
    out = []
    for i in range(count):
        d = rng.choice(["data", "data/levels", "audio", "video", "bin", "data/levels/l1", "ui"])
        name = f"/app0/{prefix}{d}/file_{i:04d}{rng.choice(exts)}"
        out.append((name, rng.choice([300, 4096, 70_000, 1 << 20, 5 << 20, 40 << 20, 200 << 20, 3 << 30])))
    return out


def workload(t: Trace, rng: random.Random, entries: list[tuple[str, int]], reads: int) -> None:
    """Mix of random, streaming, gather/scatter and tiny-read patterns."""
    styles = {fid: rng.choice(["random", "stream", "tiny", "mixed", "gather"]) for fid in range(1, len(entries) + 1)}
    cursor = {fid: 0 for fid in styles}
    batch: list[bytes] = []
    priority = rng.randrange(3)
    for _ in range(reads):
        fid = rng.randrange(1, len(entries) + 1)
        size = entries[fid - 1][1]
        style = styles[fid]
        if style == "stream":
            length = rng.choice([256 << 10, 512 << 10, 1 << 20])
            off = cursor[fid] % max(1, size)
            cursor[fid] = off + length
            batch.append(read_file(fid, off, length))
        elif style == "tiny":
            off = rng.randrange(0, max(1, min(size, 1 << 16)))
            batch.append(read_file(fid, off, rng.choice([16, 64, 128, 200])))
        elif style == "random":
            off = rng.randrange(0, max(1, size))
            batch.append(read_file(fid, off, rng.choice([4096, 8192, 16384, 32768])))
        elif style == "gather":
            off = rng.randrange(0, max(1, size))
            batch.append(read_file(fid, off, 4096))
            for _ in range(rng.randrange(1, 4)):
                if rng.random() < 0.5:
                    batch.append(scatter(rng.choice([4096, 65536])))
                else:
                    batch.append(gather(rng.randrange(0, max(1, min(size, 1 << 40))), rng.choice([2048, 65536])))
            if rng.random() < 0.3:
                batch.append(gather_scatter(rng.randrange(0, max(1, size)), 8192, wide=rng.random() < 0.3))
            if rng.random() < 0.2:
                batch.append(RESET)
        else:
            off = rng.randrange(0, max(1, size))
            batch.append(read_file(fid, off, rng.choice([65536, 131072, 300_000]), wide=rng.random() < 0.1))
        if len(batch) >= rng.randrange(1, 12):
            t.record(batch, priority=priority, count=len(batch) if rng.random() < 0.3 else None, noise_rate=0.0)
            batch = []
            priority = rng.randrange(3)
    if batch:
        t.record(batch, priority=priority)


def case_dir(root: Path, name: str) -> Path:
    d = root / name
    d.mkdir(parents=True, exist_ok=True)
    return d


def build(root: Path) -> None:
    rng = random.Random(1234)

    # basic: one run, mixed patterns, unicode + '|' names, prx / eboot (known loose), streaming suffixes
    d = case_dir(root, "basic")
    entries = make_files(rng, 40)
    entries += [("/app0/eboot.bin", 5 << 20), ("/APP0/Data/ünïcode|name.dat", 3 << 20), ("/app0/sce_module/libc.prx", 1 << 20),
                ("/app0/./odd//double/slash.dat", 2 << 20), ("/app0", 10)]
    write_index(d / "ampr_emu.index", entries)
    t = Trace(d / "ampr_commands.bin", rng)
    workload(t, rng, entries, 3000)
    t.record([read_file(len(entries) - 3, 0, 4096), read_file(len(entries) - 2, 100, 8192), read_file(len(entries) - 1, 0, 4096)])
    t.save()

    # multi: three runs merged with --trace, sizes changing between runs, unseen ids, warnings
    d = case_dir(root, "multi")
    base = make_files(rng, 60, "m")
    for run in range(3):
        rd = case_dir(d, f"run{run}")
        ents = [(p, s + (4096 * run if i % 7 == 0 else 0)) for i, (p, s) in enumerate(base)]
        write_index(rd / "ampr_emu.index", ents)
        t = Trace(rd / "ampr_commands.bin", rng)
        workload(t, rng, ents, 1500)
        t.record([read_file(9999, 0, 10)])  # absent fileId
        t.record([read_file(1, ents[0][1] + 10, 10)])  # beyond size
        t.record([read_file(2, max(0, ents[1][1] - 5), 100)])  # clamped
        t.record([gather(0, 10)], priority=7)  # no state (fresh priority)
        t.record([scatter(10)], priority=8)
        t.record([gather_scatter(0, 10)], priority=9)
        t.save()

    # many: 400 small files read randomly -> externalized / chunked rules
    d = case_dir(root, "many")
    ents = [(f"/app0/pak/{i // 50:02d}/chunk_{i:05d}.bin", 256 << 10) for i in range(400)]
    write_index(d / "ampr_emu.index", ents)
    t = Trace(d / "ampr_commands.bin", rng)
    for _ in range(4000):
        fid = rng.randrange(1, 401)
        t.record([read_file(fid, rng.randrange(0, 256 << 10), rng.choice([4096, 16384]))], noise_rate=0.05)
    t.save()

    # big: many touches for windowed cache sampling, plus lots of files (1000+ / switches -> 8 workers)
    d = case_dir(root, "big")
    ents = [(f"/app0/big/{i // 100:03d}/b{i:05d}.dat", rng.choice([1 << 20, 4 << 20])) for i in range(1200)]
    write_index(d / "ampr_emu.index", ents)
    t = Trace(d / "ampr_commands.bin", rng)
    batch = []
    for _ in range(40000):
        fid = rng.randrange(1, 1201)
        batch.append(read_file(fid, rng.randrange(0, ents[fid - 1][1]), rng.choice([4096, 16384, 70000])))
        if len(batch) == 8:
            t.record(batch, noise_rate=0)
            batch = []
    t.save()

    # dirs: directory generalization (hybrid / directory)
    d = case_dir(root, "dirs")
    ents = []
    for dn in ["tex/a", "tex/b", "snd", "geo/x/y", "misc"]:
        for i in range(rng.randrange(5, 30)):
            ents.append((f"/app0/{dn}/f{i}.bin", rng.choice([1 << 20, 8 << 20])))
    write_index(d / "ampr_emu.index", ents)
    t = Trace(d / "ampr_commands.bin", rng)
    for _ in range(5000):
        fid = rng.randrange(1, len(ents) + 1)
        if ents[fid - 1][0].startswith("/app0/snd"):
            t.record([read_file(fid, rng.randrange(0, 4) << 18, 1 << 18)], noise_rate=0)
        elif rng.random() < 0.85:
            t.record([read_file(fid, rng.randrange(0, ents[fid - 1][1]), 8192)], noise_rate=0)
    t.save()

    # broken: corrupt journal pieces (decode errors, sequence gap, bad hash, AMM domain, truncation)
    d = case_dir(root, "broken")
    ents = make_files(rng, 10, "b") + [("/app0/../escape.dat", 4096)]
    write_index(d / "ampr_emu.index", ents)
    t = Trace(d / "ampr_commands.bin", rng)
    t.record([read_file(1, 0, 4096)], seq=5)
    t.record([read_file(2, 0, 4096)], seq=9, bad_hash=True)
    t.record([read_file(3, 0, 4096)], domain=2)
    t.record([w(40 | (2 << 8)) + w(0) + w(0)], count=3)  # invalid AprReadFile length -> decode error
    t.record([read_file(4, 0, 4096), read_file(5, 0, 4096)], count=5)  # count mismatch
    t.record([read_file(11, 0, 100)])  # unsafe path
    t.record([read_file(6, 0, 4096)], extra=b"\x01" * 8)  # extended header
    t.record([w(1 | (7 << 8))])  # WaitOnAddress invalid
    t.record([w(0xABC | (3 << 8))])  # Unknown, guessed 4 dwords but only 1 present -> 1
    t.record([read_file(7, 0, 4096)])
    for i in range(250):
        t.record([read_file(9999, i, 1)], noise_rate=0)  # >200 warnings -> omitted marker
    t.save()
    data = (d / "ampr_commands.bin").read_bytes()
    (d / "ampr_commands.bin").write_bytes(data + data[:HEADER.size + 10])  # truncated payload at the end

    # trunchead: truncated header
    d = case_dir(root, "trunchead")
    write_index(d / "ampr_emu.index", ents[:5])
    t = Trace(d / "ampr_commands.bin", rng)
    t.record([read_file(1, 0, 4096)])
    t.raw(b"AMPRCMD1" + b"\0" * 20)
    t.save()

    # badmagic
    d = case_dir(root, "badmagic")
    write_index(d / "ampr_emu.index", ents[:5])
    (d / "ampr_commands.bin").write_bytes(b"NOTMAGIC" + b"\0" * 88)

    # empty journal
    d = case_dir(root, "empty")
    write_index(d / "ampr_emu.index", ents[:5])
    (d / "ampr_commands.bin").write_bytes(b"")

    # sample: content root with compressible / incompressible files
    d = case_dir(root, "sample")
    content = d / "app0"
    ents = []
    for i in range(14):
        name = f"data/s{i}.bin"
        size = rng.choice([200 << 10, 1 << 20, 3 << 20]) if i < 12 else (65 << 20) + i
        p = content / name
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_bytes(rng.randbytes(size) if i % 2 else (b"abcdefgh" * (size // 8 + 1))[:size])
        ents.append((f"/app0/{name}", size))
    write_index(d / "ampr_emu.index", ents)
    t = Trace(d / "ampr_commands.bin", rng)
    workload(t, rng, ents, 800)
    t.save()



def build_batch_inputs(root: Path) -> None:
    """Folders and ZIP bundles for ``batch``: duplicate names, junk members, unsafe and empty archives."""
    dups = root / "dups"
    for run in ("x/y", "x-y", "z"):
        target = dups / run
        target.mkdir(parents=True, exist_ok=True)
        for f in ("ampr_commands.bin", "ampr_emu.index"):
            shutil.copy2(root / "multi" / "run1" / f, target / f)
    (dups / "orphan").mkdir()
    (dups / "orphan" / "ampr_commands.bin").write_bytes(b"")
    with zipfile.ZipFile(root / "bundle.zip", "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("support/", b"")
        z.writestr("support/logs/decoded.txt", b"x" * 1000)
        for run in ("run0", "run2"):
            for f in ("ampr_commands.bin", "ampr_emu.index", "ampr_emu.log"):
                source = root / "multi" / run / f
                z.writestr(f"support/{run}/{f}", source.read_bytes() if source.exists() else b"log")
        z.writestr("./support/./many/ampr_commands.bin", (root / "many" / "ampr_commands.bin").read_bytes())
        z.writestr("support/many/ampr_emu.index", (root / "many" / "ampr_emu.index").read_bytes())
    with zipfile.ZipFile(root / "unsafe.zip", "w") as z:
        z.writestr("ok/readme.txt", b"x")
        z.writestr("../evil/ampr_commands.bin", b"x")
    with zipfile.ZipFile(root / "notrace.zip", "w") as z:
        z.writestr("logs/a.txt", b"x")
    (root / "notzip.bin").write_bytes(b"PK\x03\x04 not really a zip" * 10)
    (root / "nothing").mkdir()

GENERATE: list[tuple[str, list[str]]] = [
    ("basic", ["data/basic"]),
    ("basic-nocache", ["data/basic", "--no-cache-sim", "--name", "x y", "--full-metrics"]),
    ("basic-budget", ["data/basic", "--pack-index-budget", "1MiB", "--strategy", "conservative"]),
    ("basic-aggr", ["data/basic", "--pack-index-budget", "1MiB", "--strategy", "aggressive", "--lanes", "3", "--runtime-workers", "5"]),
    ("basic-touches", ["data/basic", "--cache-max-touches", "5000", "--cache-candidates", "1MiB,4MiB,64MiB,64MiB,300MiB"]),
    ("basic-touches2", ["data/basic", "--cache-max-touches", "100000"]),
    ("basic-minreads", ["data/basic", "--min-reads", "5", "--min-requested-bytes", "64KiB", "--io-page-size", "16KiB"]),
    ("multi", ["--trace", "data/multi/run0/ampr_commands.bin", "data/multi/run0/ampr_emu.index",
               "--trace", "data/multi/run1/ampr_commands.bin", "data/multi/run1/ampr_emu.index",
               "--trace", "data/multi/run2/ampr_commands.bin", "data/multi/run2/ampr_emu.index"]),
    ("multi-hybrid", ["--trace", "data/multi/run0/ampr_commands.bin", "data/multi/run0/ampr_emu.index",
                      "--trace", "data/multi/run2/ampr_commands.bin", "data/multi/run2/ampr_emu.index", "--pattern-mode", "hybrid"]),
    ("multi-windows", ["--trace", "data/multi/run0/ampr_commands.bin", "data/multi/run0/ampr_emu.index",
                       "--trace", "data/multi/run1/ampr_commands.bin", "data/multi/run1/ampr_emu.index",
                       "--trace", "data/multi/run2/ampr_commands.bin", "data/multi/run2/ampr_emu.index", "--cache-max-touches", "8192"]),
    ("big-windows", ["data/big", "--cache-max-touches", "16384"]),
    ("big", ["data/big", "--pattern-mode", "hybrid"]),
    ("many-windows", ["data/many", "--cache-max-touches", "4100"]),
    ("many", ["data/many", "--full-metrics"]),
    ("many-inline", ["data/many", "--externalize-paths", "0", "--max-rule-files", "70"]),
    ("many-ext10", ["data/many", "--externalize-paths", "10"]),
    ("many-hybrid", ["data/many", "--pattern-mode", "hybrid"]),
    ("dirs-hybrid", ["data/dirs", "--pattern-mode", "hybrid"]),
    ("dirs-directory", ["data/dirs", "--pattern-mode", "directory", "--generalize-min-files", "3", "--generalize-coverage", "0.9"]),
    ("dirs-exact", ["data/dirs"]),
    ("broken", ["data/broken"]),
    ("trunchead", ["data/trunchead"]),
    ("empty", ["data/empty"]),
    ("badmagic", ["data/badmagic"]),
    ("sample", ["data/sample", "--content-root", "data/sample/app0", "--sample-budget", "8MiB", "--full-metrics"]),
    ("sample-fast", ["data/sample", "--content-root", "data/sample/app0", "--sample-budget", "300KiB", "--sample-mode", "fast",
                     "--sample-blocks-per-file", "1"]),
    ("err-none", []),
    ("err-dir", ["data/multi"]),
    ("err-missing", ["--trace", "data/nope.bin", "data/nope.index"]),
    ("err-io", ["data/basic", "--io-page-size", "3000"]),
    ("err-lanes", ["data/basic", "--lanes", "65"]),
    ("err-cov", ["data/basic", "--generalize-coverage", "1.5"]),
    ("err-file", ["data/basic/ampr_emu.index"]),
]


BATCH: list[tuple[str, list[str]]] = [
    ("batch-multi", ["data/multi", "--batch-jobs", "1"]),
    ("batch-multi-par", ["data/multi", "--batch-jobs", "3", "--full-metrics"]),
    ("batch-exact-cache", ["data/multi", "--pattern-mode", "exact", "--cache-sim", "--batch-jobs", "1"]),
    ("batch-dups", ["data/dups", "--batch-jobs", "1"]),
    ("batch-single", ["data/many", "--batch-jobs", "1", "--summary", "SUMMARY"]),
    ("batch-zip", ["data/bundle.zip", "--batch-jobs", "2", "--content-root", "data/sample/app0", "--sample-budget", "1MiB"]),
    ("batch-err-unsafe", ["data/unsafe.zip"]),
    ("batch-err-notrace", ["data/notrace.zip"]),
    ("batch-err-notzip", ["data/notzip.bin"]),
    ("batch-err-empty", ["data/nothing"]),
    ("batch-err-missing", ["data/missing-folder"]),
    ("batch-err-lanes", ["data/many", "--lanes", "99"]),
]


def run(cmd: list[str], cwd: Path) -> tuple[int, str, str]:
    p = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True)
    return p.returncode, p.stdout, p.stderr


def normalized(path: Path) -> bytes:
    # Batch ZIP inputs are extracted to a random temporary folder that the report and metrics name.
    return re.sub(rb"ampr-profile-[A-Za-z0-9_]+", b"ampr-profile-TEMP", path.read_bytes())


def compare_trees(pdir: Path, cdir: Path) -> list[str]:
    problems = []
    pfiles = sorted(str(p.relative_to(pdir)) for p in pdir.rglob("*") if p.is_file())
    cfiles = sorted(str(p.relative_to(cdir)) for p in cdir.rglob("*") if p.is_file())
    if pfiles != cfiles:
        problems.append(f"files {pfiles} vs {cfiles}")
    for f in sorted(set(pfiles) & set(cfiles)):
        if normalized(pdir / f) != normalized(cdir / f):
            diff = subprocess.run(["diff", str(pdir / f), str(cdir / f)], capture_output=True, text=True).stdout
            problems.append(f"{f} differs:\n{diff[:3000]}")
    return problems


def check(work: Path, python: str, mkpfs: Path, ampr_emu: Path) -> int:
    tool = ampr_emu / "tools" / "ampr_pack_profile.py"
    cases = [("generate", n, a) for n, a in GENERATE] + [("batch", n, a) for n, a in BATCH]
    failures = 0
    for kind, name, args in cases:
        outs = {}
        for side in ("py", "cs"):
            out = work / "out" / side / name
            if out.exists():
                shutil.rmtree(out)
            out.mkdir(parents=True)
            o = f"out/{side}/{name}"
            if kind == "generate":
                extra = ["--output", f"{o}/profile.toml", "--report", f"{o}/report.md", "--metrics", f"{o}/metrics.json",
                         "--runtime-header", f"{o}/runtime.h"]
                case_args = args
            else:
                extra = ["--output-dir", f"{o}/profiles"]
                case_args = [f"{o}/summary-custom.json" if a == "SUMMARY" else a for a in args]
            cmd = [python, str(tool), kind, *case_args, *extra] if side == "py" else [str(mkpfs), "ampr", "profile", kind, *case_args, *extra]
            code, so, se = run(cmd, work)
            outs[side] = (code, so.replace(f"out/{side}/", "out/X/"), se.strip().splitlines()[-1:] if se.strip() else [])
        (pc, pso, pse), (cc, cso, cse) = outs["py"], outs["cs"]
        problems = []
        if pc != cc:
            problems.append(f"exit {pc} vs {cc}")
        if pso != cso:
            problems.append(f"stdout\n  py: {pso!r}\n  cs: {cso!r}")
        if pse != cse:
            problems.append(f"stderr\n  py: {pse!r}\n  cs: {cse!r}")
        problems += compare_trees(work / "out" / "py" / name, work / "out" / "cs" / name)
        print(f"{'OK  ' if not problems else 'FAIL'} {kind} {name} (exit {pc})")
        for p in problems:
            print("   ", p)
        failures += bool(problems)
    print(f"{failures} failing case(s) of {len(cases)}")
    return 1 if failures else 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--out", type=Path, default=DEFAULT_OUT, help="work folder (default: tests/fixtures/generated/ampr_profile)")
    parser.add_argument("--python", default=sys.executable, help="interpreter for ampr_pack_profile.py (needs python-lz4)")
    parser.add_argument("--mkpfs", type=Path, default=DEFAULT_MKPFS, help="mkpfs executable")
    parser.add_argument("--ampr-emu", type=Path, default=AMPR_EMU, help="ampr_emu checkout (default: ../ampr_emu)")
    args = parser.parse_args()
    work: Path = args.out.resolve()
    if work.exists():
        shutil.rmtree(work)
    build(work / "data")
    build_batch_inputs(work / "data")
    return check(work, args.python, args.mkpfs.resolve(), args.ampr_emu.resolve())


if __name__ == "__main__":
    raise SystemExit(main())
