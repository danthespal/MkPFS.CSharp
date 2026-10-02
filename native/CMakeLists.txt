cmake_minimum_required(VERSION 3.20)
project(mkpfs_zlib C)

# Static CRT on Windows (no VC++ redist needed); hidden symbols elsewhere so a
# system libz loaded into the same process can never interpose our zlib.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")
set(CMAKE_C_VISIBILITY_PRESET hidden)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

add_library(mkpfs_zlib SHARED
  mkpfs_zlib.c
  zlib/adler32.c
  zlib/compress.c
  zlib/crc32.c
  zlib/deflate.c
  zlib/inffast.c
  zlib/inflate.c
  zlib/inftrees.c
  zlib/trees.c
  zlib/uncompr.c
  zlib/zutil.c
)
target_include_directories(mkpfs_zlib PRIVATE zlib)

if(MSVC)
  target_compile_definitions(mkpfs_zlib PRIVATE _CRT_SECURE_NO_DEPRECATE _CRT_NONSTDC_NO_DEPRECATE)
elseif(NOT APPLE)
  target_link_options(mkpfs_zlib PRIVATE "-Wl,-Bsymbolic")
endif()
