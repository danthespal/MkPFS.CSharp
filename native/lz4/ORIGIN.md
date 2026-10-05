# lz4 1.9.4

| | |
|---|---|
| **Source** | https://github.com/lz4/lz4/tree/v1.9.4 |
| **Commit** | `5ff839680134437dbf4678f3d0c7b371d84f4964` |
| **Files** | `lib/lz4.c`, `lib/lz4.h`, `lib/lz4hc.c`, `lib/lz4hc.h` and `lib/LICENSE` (BSD-2-Clause) |
| **Changes** | None |

The files are identical (apart from line endings) to `lz4libs/` in python-lz4 v4.4.5, the encoder of the AMPR
pack oracle (ampr_emu `tools/ampr_pack.py`). lz4 1.10.0 changes HC levels 1 and 2, so it is not used. The
decoder accepts blocks from any lz4 version; the ampr_emu runtime decodes with 1.10.0.
