/*
 * mkpfs_lz4: raw LZ4 block API over lz4 1.9.4 for AMPR asset packs.
 *
 * The encoder calls lz4 exactly like python-lz4 4.4.5 lz4/block/_block.c (store_size=False), which
 * ampr_emu's ampr_pack.py uses: fast mode resets a stream and calls LZ4_compress_fast_continue, and
 * HC mode resets an HC stream and calls LZ4_compress_HC_continue. LZ4_compress_fast emits different
 * (larger) blocks, so it must not be used for parity.
 * All lz4 symbols stay hidden; only the mkpfs_lz4_* entry points are exported.
 */

#include <stdint.h>
#include <stdlib.h>

#include "lz4.h"
#include "lz4hc.h"

#if defined(_WIN32)
#define MKPFS_API __declspec(dllexport)
#else
#define MKPFS_API __attribute__((visibility("default")))
#endif

typedef struct mkpfs_lz4_encoder {
  LZ4_stream_t fast;
  LZ4_streamHC_t hc;
} mkpfs_lz4_encoder;

MKPFS_API const char *mkpfs_lz4_version(void) {
  return LZ4_versionString();
}

/* Returns 0 when source_len exceeds LZ4_MAX_INPUT_SIZE. */
MKPFS_API int32_t mkpfs_lz4_bound(int32_t source_len) {
  return LZ4_compressBound(source_len);
}

/* Holds both stream states (about 280 KB), so one encoder per worker avoids large stack frames. */
MKPFS_API void *mkpfs_lz4_encoder_create(void) {
  return calloc(1, sizeof(mkpfs_lz4_encoder));
}

MKPFS_API void mkpfs_lz4_encoder_free(void *handle) {
  free(handle);
}

/* Compress one independent raw block. Returns the block size, or 0 when dst is too small. */
MKPFS_API int32_t mkpfs_lz4_compress_fast(void *handle, const uint8_t *src, int32_t src_len,
                                          uint8_t *dst, int32_t dst_cap, int32_t acceleration) {
  mkpfs_lz4_encoder *encoder = (mkpfs_lz4_encoder *)handle;
  if (encoder == NULL) return 0;
  LZ4_resetStream(&encoder->fast);
  return LZ4_compress_fast_continue(&encoder->fast, (const char *)src, (char *)dst, src_len, dst_cap,
                                    acceleration);
}

/* Compress one independent raw block at HC level 1..12. Returns the block size, or 0 when dst is too small. */
MKPFS_API int32_t mkpfs_lz4_compress_hc(void *handle, const uint8_t *src, int32_t src_len,
                                        uint8_t *dst, int32_t dst_cap, int32_t level) {
  mkpfs_lz4_encoder *encoder = (mkpfs_lz4_encoder *)handle;
  if (encoder == NULL) return 0;
  LZ4_resetStreamHC(&encoder->hc, level);
  return LZ4_compress_HC_continue(&encoder->hc, (const char *)src, (char *)dst, src_len, dst_cap);
}

/*
 * Decode one raw block (LZ4_decompress_safe, as the ampr_emu runtime does).
 * Returns the decoded size, or a negative value for malformed input or a too small dst.
 */
MKPFS_API int32_t mkpfs_lz4_decompress_safe(const uint8_t *src, int32_t src_len, uint8_t *dst, int32_t dst_cap) {
  return LZ4_decompress_safe((const char *)src, (char *)dst, src_len, dst_cap);
}
