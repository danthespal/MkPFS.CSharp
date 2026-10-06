"""Build the FPKG oracle corpus from LibProsperoPkg (PS5 debug packages).

Copies ../LibProsperoPKG/src/LibProsperoPkg into tmp/oracle-fpkg/lib, applies the deterministic
hooks (OracleHooks.cs), builds the runner, generates fixture trees, builds one package per case and
records hashes in <out>/manifest.json. The user's LibProsperoPKG checkout is never modified.

Usage (stdlib only; plain python works too):
    uv run --project ../MkPFS python tools/oracle-fpkg/build_fpkg_goldens.py
    uv run --project ../MkPFS python tools/oracle-fpkg/build_fpkg_goldens.py --check   # build twice
"""

from __future__ import annotations

import argparse
import hashlib
import json
import random
import shutil
import struct
import subprocess
import sys
import zlib
from dataclasses import dataclass, field
from pathlib import Path

REPO: Path = Path(__file__).resolve().parents[2]
TOOL: Path = Path(__file__).resolve().parent
DEFAULT_ORACLE: Path = REPO.parent / "LibProsperoPKG"
DEFAULT_OUT: Path = REPO / "tests" / "fixtures" / "generated" / "fpkg"
SCRATCH: Path = REPO / "tmp" / "oracle-fpkg"

ORACLE_COMMIT: str = "748eabf1b7d17819528cabf367d8e27109d8fce3"
CONTENT_ID: str = "UP9000-PPSA99999_00-MKPFSORACLE00000"
PASSCODE: str = "0" * 32
TIMESTAMP: int = 1_700_000_000
SEED: str = "000102030405060708090a0b0c0d0e0f"

# sce_sys files the oracle moves into CNT entries instead of the inner image (ProsperoCntEntryNames).
OUTER_ONLY: frozenset[str] = frozenset(
    {
        "sce_sys/param.json",
        "sce_sys/icon0.png",
        "sce_sys/pic0.png",
        "sce_sys/pic1.png",
        "sce_sys/pic2.png",
        "sce_sys/snd0.at9",
        "sce_sys/changeinfo/changeinfo.xml",
        "sce_sys/pubtoolinfo.dat",
    }
)
# Files the oracle adds to the inner image.
INNER_ADDED: frozenset[str] = frozenset({"sce_sys/keystone", "sce_sys/about/right.sprx"})

# (file, exact text, replacement). Each text must occur exactly once in the pinned commit.
PATCHES: list[tuple[str, str, str]] = [
    (
        "PKG/ProsperoPkgBuilder.cs",
        "byte[] outerSeed = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);",
        "byte[] outerSeed = LibProsperoPkg.OracleHooks.NextOuterSeed();",
    ),
    (
        "Util/Crypto.cs",
        "    public static byte[] RsaPkcs1EncryptKey(byte[] modulus, byte[] data)\n    {\n",
        "    public static byte[] RsaPkcs1EncryptKey(byte[] modulus, byte[] data)\n    {\n"
        "        if (LibProsperoPkg.OracleHooks.RsaPkcs1Encrypt(modulus, data) is { } fixedWrap)\n"
        "            return fixedWrap;\n",
    ),
    (
        "PKG/ProsperoPkgSigner.cs",
        "        return rsa.Encrypt(sha3Digest, RSAEncryptionPadding.Pkcs1);\n",
        "        RSAParameters pub = rsa.ExportParameters(false);\n"
        "        return LibProsperoPkg.OracleHooks.RsaPkcs1Encrypt(pub.Modulus!, sha3Digest, pub.Exponent)\n"
        "            ?? rsa.Encrypt(sha3Digest, RSAEncryptionPadding.Pkcs1);\n",
    ),
    (
        "PFS/ProsperoPfsStructs.cs",
        "SetTime((long)DateTime.UtcNow.Subtract(",
        "SetTime((long)LibProsperoPkg.OracleHooks.UtcNow.Subtract(",
    ),
    (
        "ProsperoPackageBuilder.cs",
        "            VolumeType = ProsperoVolumeTypeForMode(options.Mode),\n",
        "            VolumeType = ProsperoVolumeTypeForMode(options.Mode),\n"
        "            TimeStamp = LibProsperoPkg.OracleHooks.UtcNow,\n",
    ),
    (
        "PFS/ProsperoPs5InnerImageAssembler.cs",
        "            // An unsigned executable image is meant to become a signed module before it reaches the\n",
        "            policy = LibProsperoPkg.OracleHooks.Policy(policy);\n"
        "            // An unsigned executable image is meant to become a signed module before it reaches the\n",
    ),
]


# ---- oracle preparation ------------------------------------------------------------------------


def run(argv: list[str], cwd: Path | None = None) -> subprocess.CompletedProcess[str]:
    return subprocess.run(argv, cwd=cwd, capture_output=True, text=True, encoding="utf-8", errors="replace")


def oracle_commit(oracle: Path) -> str:
    proc = run(["git", "rev-parse", "HEAD"], cwd=oracle)
    if proc.returncode != 0:
        raise SystemExit(f"cannot read the oracle commit in {oracle}: {proc.stderr.strip()}")
    return proc.stdout.strip()


def prepare_lib(oracle: Path, dest: Path) -> None:
    """Copy LibProsperoPkg to dest and apply the hooks."""
    src: Path = oracle / "src" / "LibProsperoPkg"
    if dest.exists():
        shutil.rmtree(dest)
    shutil.copytree(src, dest, ignore=shutil.ignore_patterns("bin", "obj"))
    # Stop MSBuild from importing MkPFS's Directory.Build.props (warnings as errors, implicit usings).
    (dest / "Directory.Build.props").write_text("<Project />\n", encoding="utf-8")
    (dest / "Directory.Build.targets").write_text("<Project />\n", encoding="utf-8")
    shutil.copyfile(TOOL / "OracleHooks.cs", dest / "OracleHooks.cs")
    for rel, old, new in PATCHES:
        path: Path = dest / rel
        text: str = path.read_text(encoding="utf-8-sig")
        count: int = text.count(old)
        if count != 1:
            raise SystemExit(f"patch anchor in {rel} found {count} times (expected 1): {old.strip()}")
        path.write_text(text.replace(old, new), encoding="utf-8")


def build_runner(lib: Path, bin_dir: Path) -> Path:
    project: Path = TOOL / "runner" / "FPKGOracle.csproj"
    proc = run(
        ["dotnet", "build", str(project), "-c", "Release", "-o", str(bin_dir), "-nologo", "-v", "q",
         "-p:FPKGOracleLib=" + str(lib) + "/"]
    )
    if proc.returncode != 0:
        raise SystemExit(f"runner build failed:\n{proc.stdout}\n{proc.stderr}")
    exe: Path = bin_dir / ("FPKGOracle.exe" if sys.platform == "win32" else "FPKGOracle")
    return exe if exe.exists() else bin_dir / "FPKGOracle.dll"


def runner_argv(runner: Path, args: list[str]) -> list[str]:
    return (["dotnet", str(runner)] if runner.suffix == ".dll" else [str(runner)]) + args


# ---- fixtures ----------------------------------------------------------------------------------


def png(width: int, height: int, seed: int) -> bytes:
    """Return a deterministic RGB gradient PNG."""

    def chunk(kind: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))

    rows: bytearray = bytearray()
    for y in range(height):
        rows.append(0)
        for x in range(width):
            rows += bytes(((x + seed) & 0xFF, (y * 2) & 0xFF, ((x ^ y) + seed * 7) & 0xFF))
    ihdr: bytes = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + chunk(b"IDAT", zlib.compress(bytes(rows), 9)) + chunk(b"IEND", b"")


def elf(rng: random.Random, e_type: int, text_size: int, data_size: int, module_data: bool = False) -> bytes:
    """Return a 64-bit x86-64 FreeBSD ELF with PT_LOAD text/data (and optional SCE module-data) segments."""
    segments: list[tuple[int, int, int]] = [(1, 5, text_size), (1, 6, data_size)]  # (p_type, p_flags, size)
    if module_data:
        segments.append((0x61000000, 4, 0x200))
    phnum: int = len(segments)
    offset: int = 0x4000
    phdrs: bytearray = bytearray()
    body: bytearray = bytearray()
    vaddr: int = 0x400000
    for p_type, p_flags, size in segments:
        phdrs += struct.pack("<IIQQQQQQ", p_type, p_flags, offset, vaddr, vaddr, size, size, 0x4000)
        body += rng.randbytes(size)
        pad: int = (-size) % 0x4000
        body += b"\x00" * pad
        offset += size + pad
        vaddr += 0x100000
    ident: bytes = b"\x7fELF" + bytes([2, 1, 1, 9, 0]) + b"\x00" * 7
    ehdr: bytes = ident + struct.pack("<HHIQQQIHHHHHH", e_type, 0x3E, 1, 0x400000, 0x40, 0, 0, 0x40, 0x38, phnum, 0x40, 0, 0)
    head: bytes = ehdr + bytes(phdrs)
    return head + b"\x00" * (0x4000 - len(head)) + bytes(body)


def text(rng: random.Random, size: int) -> bytes:
    words: list[bytes] = [b"prospero", b"block", b"inode", b"asset", b"texture", b"mesh", b"\n"]
    out: bytearray = bytearray()
    while len(out) < size:
        out += rng.choice(words) + b" "
    return bytes(out[:size])


def param_json(title: str) -> bytes:
    doc: dict[str, object] = {
        "applicationCategoryType": 0,
        "applicationDrmType": "standard",
        "attribute": 0,
        "attribute2": 0,
        "attribute3": 0,
        "conceptId": "99999",
        "contentId": CONTENT_ID,
        "contentVersion": "01.000.000",
        "downloadDataSize": 0,
        "localizedParameters": {"defaultLanguage": "en-US", "en-US": {"titleName": title}},
        "masterVersion": "01.00",
        "requiredSystemSoftwareVersion": "0x0000000000000000",
        "sdkVersion": "0x0000000000000000",
        "titleId": CONTENT_ID[7:16],
    }
    return (json.dumps(doc, indent=2) + "\n").encode("utf-8")


def write(root: Path, rel: str, data: bytes) -> None:
    path: Path = root / rel
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)


def make_trees(out: Path) -> None:
    if out.exists():
        shutil.rmtree(out)

    rng = random.Random(1)
    t: Path = out / "hb_min"
    write(t, "eboot.bin", elf(rng, 0xFE10, 0x3000, 0x800))
    write(t, "sce_sys/param.json", param_json("MkPFS Oracle Min"))
    write(t, "sce_sys/icon0.png", png(512, 512, 1))

    rng = random.Random(2)
    t = out / "hb_noparam"
    write(t, "eboot.bin", elf(rng, 0xFE10, 0x2000, 0x400))
    write(t, "sce_sys/icon0.png", png(512, 512, 2))

    rng = random.Random(3)
    t = out / "app_multi"
    write(t, "eboot.bin", elf(rng, 0xFE10, 0x9000, 0x2000))
    write(t, "sce_sys/param.json", param_json("MkPFS Oracle Multi"))
    write(t, "sce_sys/icon0.png", png(512, 512, 3))
    write(t, "sce_sys/pic0.png", png(1920, 1080, 4))
    write(t, "sce_sys/changeinfo/changeinfo.xml", b'<?xml version="1.0" encoding="utf-8"?>\n<changeinfo>\n</changeinfo>\n')
    write(t, "modules/libtest.prx", elf(rng, 0xFE18, 0x5000, 0x1000, module_data=True))
    write(t, "modules/plain.elf", elf(rng, 2, 0x1000, 0x100))
    write(t, "data/empty.bin", b"")
    write(t, "data/text_300k.txt", text(rng, 300_000))
    write(t, "data/random_70000.bin", rng.randbytes(70_000))
    write(t, "data/block_exact.bin", text(rng, 0x10000))
    write(t, "data/ublock_exact.bin", text(rng, 0x40000))
    write(t, "data/ublock_plus1.bin", text(rng, 0x40001))
    write(t, "data/mixed_2m.bin", b"".join(rng.randbytes(4096) if i % 3 == 0 else text(rng, 4096) for i in range(512)))
    write(t, "data/deep/a/b/c/d/leaf.txt", b"leaf\n")
    write(t, "Assets/UPPER.DAT", text(rng, 5000))

    # Incompressible files of assorted sizes: shows how Publishing Tools stores raw chunks of 64 KiB to
    # 256 KiB, and puts the metadata base off a 256 KiB boundary (data end ~0x1D9800 -> block 93).
    rng = random.Random(5)
    t = out / "app_raw"
    write(t, "eboot.bin", elf(rng, 0xFE10, 0x3000, 0x800))
    write(t, "sce_sys/param.json", param_json("MkPFS Oracle Raw"))
    write(t, "sce_sys/icon0.png", png(512, 512, 6))
    write(t, "data/rand_600000.bin", rng.randbytes(600_000))
    write(t, "data/rand_131072.bin", rng.randbytes(0x20000))
    write(t, "data/rand_65537.bin", rng.randbytes(0x10001))
    write(t, "data/rand_65536.bin", rng.randbytes(0x10000))

    # Incompressible files at 64 KiB / 128 KiB / 256 KiB boundaries: every raw-chunk encoding of the naps
    # layout (length field, even flag, kind 0 vs 4) and a sub-literal Kraken metadata chunk (kde 3).
    rng = random.Random(9)
    t = out / "app_sizes"
    write(t, "eboot.bin", elf(random.Random(1), 0xFE10, 0x3000, 0x800))
    write(t, "sce_sys/param.json", param_json("MkPFS Oracle Sizes"))
    write(t, "sce_sys/icon0.png", png(512, 512, 7))
    sizes: list[int] = [0x8000, 0xFFFF, 0x10000, 0x10001, 0x18000, 0x1FFFF, 0x20000, 0x20001, 0x30000,
                        0x3FFFF, 0x40000, 0x40001, 0x50000, 0x80000, 0xA0000]
    for i, size in enumerate(sizes):
        write(t, f"data/f{i:02d}_{size:x}.bin", rng.randbytes(size))

    rng = random.Random(4)
    t = out / "app_unicode"
    write(t, "eboot.bin", elf(rng, 0xFE10, 0x1000, 0x400))
    write(t, "sce_sys/param.json", param_json("MkPFS Oracle Ünicode"))
    write(t, "sce_sys/icon0.png", png(512, 512, 5))
    write(t, "données/ñandú.txt", text(rng, 1000))
    write(t, "日本語/テスト.bin", rng.randbytes(1000))


# ---- cases -------------------------------------------------------------------------------------


@dataclass(frozen=True)
class Case:
    name: str
    tree: str
    raw: bool = False
    extra: list[str] = field(default_factory=list)


def cases() -> list[Case]:
    out: list[Case] = []
    for tree in ("hb_min", "hb_noparam", "app_multi", "app_raw", "app_sizes", "app_unicode"):
        out.append(Case(f"{tree}_kraken", tree))
        out.append(Case(f"{tree}_raw", tree, raw=True))
    out.append(Case("hb_noparam_freemium", "hb_noparam", extra=["--app-type", "FreemiumApp", "--title", "Freemium Oracle"]))
    out.append(Case("hb_min_passcode", "hb_min", extra=["--passcode", "abcdefghijklmnopqrstuvwxyz012345"]))
    return out


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def hash_tree(root: Path) -> dict[str, str]:
    if not root.exists():
        return {}
    return {p.relative_to(root).as_posix(): sha256(p) for p in sorted(root.rglob("*")) if p.is_file()}


def build_case(runner: Path, case: Case, trees: Path, goldens: Path) -> dict[str, object]:
    src: Path = trees / case.tree
    out: Path = goldens / case.name
    args: list[str] = [
        "build", "--src", str(src), "--out", str(out), "--content-id", CONTENT_ID,
        "--seed", SEED, "--timestamp", str(TIMESTAMP),
    ]
    if "--passcode" not in case.extra:
        args += ["--passcode", PASSCODE]
    if case.raw:
        args.append("--raw")
    args += case.extra
    proc = run(runner_argv(runner, args))
    record: dict[str, object] = {"tree": case.tree, "raw": case.raw, "argv": args[5:], "exit": proc.returncode}
    if proc.returncode != 0:
        record["error"] = proc.stderr.strip()
        return record

    passcode: str = case.extra[case.extra.index("--passcode") + 1] if "--passcode" in case.extra else PASSCODE
    record["passcode"] = passcode
    record["out.pkg"] = sha256(out / "out.pkg")
    result: dict[str, object] = json.loads((out / "result.json").read_text(encoding="utf-8"))
    record["segments"] = result["segments"]
    extract: dict[str, object] = result["extract"]  # type: ignore[assignment]
    record["extract_ok"] = extract["ok"]

    # The oracle must leave the source tree as it was, apart from a generated param.json.
    source: dict[str, str] = hash_tree(src)
    work: dict[str, str] = hash_tree(out / "work")
    record["source_changed"] = sorted(k for k in source.keys() | work.keys() if source.get(k) != work.get(k))

    if not extract["ok"]:
        # Known at 748eabf: the oracle's reader cannot rebuild the inner mount (its metadata region is
        # always Kraken-compressed). See README "Findings".
        record["extract_error"] = extract["error"]
        return record

    # Inner files extracted from the package vs the source tree.
    extracted: dict[str, str] = hash_tree(out / "extract")
    expected: dict[str, str] = {k: v for k, v in work.items() if k not in OUTER_ONLY}
    record["extract_missing"] = sorted(k for k in expected if k not in extracted)
    record["extract_extra"] = sorted(k for k in extracted if k not in expected)
    # Modules are fake-signed in the package, so their bytes differ by design.
    record["extract_changed"] = sorted(k for k in expected if k in extracted and expected[k] != extracted[k])
    return record


def build_all(out: Path, runner: Path) -> dict[str, object]:
    trees: Path = out / "trees"
    goldens: Path = out / "goldens"
    make_trees(trees)
    if goldens.exists():
        shutil.rmtree(goldens)
    goldens.mkdir(parents=True)

    manifest: dict[str, object] = {
        "oracle": {"repo": "LibProsperoPKG", "commit": ORACLE_COMMIT, "patches": [p[0] for p in PATCHES]},
        "content_id": CONTENT_ID,
        "timestamp": TIMESTAMP,
        "seed": SEED,
        "trees": {p.name: hash_tree(p) for p in sorted(trees.iterdir())},
        "cases": {},
    }
    for case in cases():
        print(f"  {case.name} ...", flush=True)
        manifest["cases"][case.name] = build_case(runner, case, trees, goldens)  # type: ignore[index]

    proc = run(runner_argv(runner, ["vectors", "--trees", str(trees), "--out", str(out / "vectors")]))
    if proc.returncode != 0:
        raise SystemExit(f"vectors failed: {proc.stderr}")
    manifest["vectors"] = hash_tree(out / "vectors")
    (goldens / "manifest.json").write_text(json.dumps(manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return manifest


def compare(a: object, b: object, path: str = "") -> list[str]:
    if isinstance(a, dict) and isinstance(b, dict):
        diffs: list[str] = []
        for key in sorted(a.keys() | b.keys()):
            diffs += compare(a.get(key), b.get(key), f"{path}/{key}")
        return diffs
    return [] if a == b else [path]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--oracle", type=Path, default=DEFAULT_ORACLE, help="LibProsperoPKG checkout")
    parser.add_argument("--out", type=Path, default=DEFAULT_OUT, help="Default: tests/fixtures/generated/fpkg")
    parser.add_argument("--check", action="store_true", help="Build twice and report nondeterministic outputs")
    parser.add_argument("--allow-commit", action="store_true", help=f"Accept an oracle commit other than {ORACLE_COMMIT[:7]}")
    args = parser.parse_args()

    commit: str = oracle_commit(args.oracle)
    if commit != ORACLE_COMMIT and not args.allow_commit:
        raise SystemExit(f"oracle is at {commit[:7]}, expected {ORACLE_COMMIT[:7]} (use --allow-commit)")

    print("Preparing the patched oracle ...", flush=True)
    lib: Path = SCRATCH / "lib"
    prepare_lib(args.oracle, lib)
    runner: Path = build_runner(lib, SCRATCH / "bin")

    print(f"Building the corpus in {args.out} ...", flush=True)
    first: dict[str, object] = build_all(args.out, runner)
    failures: list[str] = [name for name, rec in first["cases"].items() if rec["exit"] != 0]  # type: ignore[union-attr]

    if args.check:
        print("Second build for the determinism check ...", flush=True)
        second_dir: Path = SCRATCH / "check"
        second: dict[str, object] = build_all(second_dir, runner)
        diffs: list[str] = compare(first, second)
        (args.out / "determinism.json").write_text(json.dumps(diffs, indent=2) + "\n", encoding="utf-8")
        print(f"Determinism: {len(diffs)} difference(s)")
        for d in diffs[:20]:
            print(f"  {d}")
        if diffs:
            return 1

    print(f"Cases: {len(first['cases'])}, failed: {failures or 'none'}")  # type: ignore[arg-type]
    return 0


if __name__ == "__main__":
    sys.exit(main())
