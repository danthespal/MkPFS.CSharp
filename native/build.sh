#!/bin/sh
# Build native/mkpfs_zlib (zlib 1.3.1 + shim) for Linux/macOS into artifacts/native/<rid>/.
# Usage: sh native/build.sh linux-x64|linux-arm64|osx-x64|osx-arm64 [Release]
set -eu

rid="${1:?usage: build.sh <rid> [configuration]}"
configuration="${2:-Release}"
native_dir="$(cd "$(dirname "$0")" && pwd)"
repo="$(dirname "$native_dir")"
build_dir="$repo/artifacts/native-build/$rid"
out_dir="$repo/artifacts/native/$rid"

extra=""
case "$rid" in
  osx-x64) extra="-DCMAKE_OSX_ARCHITECTURES=x86_64"; lib="libmkpfs_zlib.dylib" ;;
  osx-arm64) extra="-DCMAKE_OSX_ARCHITECTURES=arm64"; lib="libmkpfs_zlib.dylib" ;;
  linux-*) lib="libmkpfs_zlib.so" ;;
  *) echo "unsupported RID: $rid" >&2; exit 1 ;;
esac

# shellcheck disable=SC2086  # $extra is intentionally word-split (empty or one flag)
cmake -S "$native_dir" -B "$build_dir" -DCMAKE_BUILD_TYPE="$configuration" $extra
cmake --build "$build_dir"
mkdir -p "$out_dir"
cp "$build_dir/$lib" "$out_dir/"
echo "Built $out_dir/$lib"
