/*
 * mkpfs_zlib: minimal block API over zlib 1.3.1 for MkPFS.
 *
 * Exposes only flat functions with fixed-size arguments, so the managed side never
 * marshals z_stream (whose uLong fields differ between Windows and Unix).
 * All zlib symbols stay hidden; only the mkpfs_* entry points are exported.
 */

#include <stdint.h>
#include <stdlib.h>

#include "zlib.h"

#if defined(_WIN32)
#define MKPFS_API __declspec(dllexport)
#else
#define MKPFS_API __attribute__((visibility("default")))
#endif

MKPFS_API const char *mkpfs_zlib_version(void) {
  return zlibVersion();
}

MKPFS_API uint32_t mkpfs_compress_bound(uint32_t source_len) {
  return (uint32_t)compressBound((uLong)source_len);
}

/* Same parameters as compress2() and Python zlib.compress(): window 15, memLevel 8, default strategy. */
MKPFS_API void *mkpfs_deflater_create(int level) {
  z_stream *stream = (z_stream *)calloc(1, sizeof(*stream));
  if (stream == NULL) return NULL;
  if (deflateInit(stream, level) != Z_OK) {
    free(stream);
    return NULL;
  }
  return stream;
}

/*
 * Compress one whole buffer as an independent zlib stream.
 * Returns Z_OK and sets *out_len on success, Z_BUF_ERROR when dst is too small,
 * or another zlib error code.
 */
MKPFS_API int mkpfs_deflater_compress(void *handle, const uint8_t *src, uint32_t src_len,
                                      uint8_t *dst, uint32_t dst_cap, uint32_t *out_len) {
  z_stream *stream = (z_stream *)handle;
  int rc;
  if (stream == NULL || out_len == NULL) return Z_STREAM_ERROR;
  *out_len = 0;
  rc = deflateReset(stream);
  if (rc != Z_OK) return rc;
  stream->next_in = (Bytef *)src;
  stream->avail_in = src_len;
  stream->next_out = dst;
  stream->avail_out = dst_cap;
  rc = deflate(stream, Z_FINISH);
  if (rc == Z_STREAM_END) {
    *out_len = dst_cap - stream->avail_out;
    return Z_OK;
  }
  /* Z_OK or Z_BUF_ERROR here means the output buffer filled up before the stream ended. */
  return (rc == Z_OK || rc == Z_BUF_ERROR) ? Z_BUF_ERROR : rc;
}

MKPFS_API void mkpfs_deflater_free(void *handle) {
  z_stream *stream = (z_stream *)handle;
  if (stream == NULL) return;
  deflateEnd(stream);
  free(stream);
}

/*
 * Decompress one complete zlib stream into dst.
 * Returns Z_OK with *out_len and *consumed set, Z_BUF_ERROR when dst is too small,
 * or Z_DATA_ERROR for corrupt or truncated input.
 */
MKPFS_API int mkpfs_inflate(const uint8_t *src, uint32_t src_len, uint8_t *dst, uint32_t dst_cap,
                            uint32_t *out_len, uint32_t *consumed) {
  uLongf dest_len = (uLongf)dst_cap;
  uLong source_len = (uLong)src_len;
  int rc;
  if (out_len == NULL || consumed == NULL) return Z_STREAM_ERROR;
  rc = uncompress2(dst, &dest_len, src, &source_len);
  *out_len = (uint32_t)dest_len;
  *consumed = (uint32_t)source_len;
  return rc;
}
