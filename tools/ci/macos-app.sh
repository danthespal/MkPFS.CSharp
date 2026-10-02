#!/usr/bin/env bash
# Wrap a published mkpfs-gui folder into "MkPFS.C#.app" (macOS only: uses sips and iconutil).
# Usage: tools/ci/macos-app.sh <publish dir> <output dir> <version>
set -euo pipefail

src="${1:?usage: macos-app.sh <publish dir> <output dir> <version>}"
out="${2:?usage: macos-app.sh <publish dir> <output dir> <version>}"
version="${3:-0.0.0}"
app="${out}/MkPFS.C#.app"
icon_png="$(dirname "$0")/../../src/MkPFS.Gui/Assets/icon.png"

rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
# The executable and its native libraries (mkpfs_zlib, Skia, HarfBuzz) sit side by side.
cp -R "$src"/. "$app/Contents/MacOS/"
find "$app/Contents/MacOS" \( -name '*.pdb' -o -name '*.dbg' -o -name '*.dSYM' -o -name '*.runtimeconfig.json' \) -prune -exec rm -rf {} +

iconset="$(mktemp -d)/AppIcon.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" "$icon_png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z "$double" "$double" "$icon_png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/AppIcon.icns"

cat > "$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>MkPFS.C#</string>
  <key>CFBundleDisplayName</key><string>MkPFS.C#</string>
  <key>CFBundleIdentifier</key><string>io.github.danthespal.mkpfs-csharp</string>
  <key>CFBundleExecutable</key><string>mkpfs-gui</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>${version%%-*}</string>
  <key>CFBundleVersion</key><string>${version}</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
EOF
chmod +x "$app/Contents/MacOS/mkpfs-gui"
echo "$app"
