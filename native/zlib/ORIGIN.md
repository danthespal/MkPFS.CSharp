# zlib 1.3.1

| | |
|---|---|
| **Source** | https://github.com/madler/zlib/tree/v1.3.1 |
| **Commit** | `51b7f2abdade71cd9bb0e7a373ef2610ec6f9daf` |
| **Files** | The C and header files PS5 Game Compressor's `third_party/zlib` needs, plus `LICENSE` |
| **Changes** | One trailing blank line removed from `trees.h` |

Copied from PS5 Game Compressor `third_party/zlib`, which took them from that release. Game Compressor and
Python 3.11 (`zlib.ZLIB_RUNTIME_VERSION`) use the same release, so PFSC blocks compressed here are
byte-identical to theirs.
