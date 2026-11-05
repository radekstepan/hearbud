#!/usr/bin/env bash
# Packages the Avalonia app as a macOS .app bundle (ad-hoc signed).
#
#   ./tools/package-macos.sh [RID]        # default RID=osx-arm64
#
# Output: dist/Hearbud.app (plus dist/<rid>/ with the raw publish output).
#
# Real distribution needs a paid Apple Developer ID: replace CFBundleIdentifier
# in Hearbud.Avalonia/Assets/Info.plist with your own, then:
#   codesign --force --deep --options runtime -s "Developer ID Application: NAME" dist/Hearbud.app
#   ditto -c -k --keepParent dist/Hearbud.app dist/Hearbud.zip
#   xcrun notarytool submit dist/Hearbud.zip --apple-id ... --team-id ... --password ...
#   xcrun stapler staple dist/Hearbud.app
set -euo pipefail

RID="${1:-osx-arm64}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP="$ROOT/dist/Hearbud.app"
PUB="$ROOT/dist/$RID"

echo "==> publish ($RID)"
dotnet publish "$ROOT/Hearbud.Avalonia/Hearbud.Avalonia.csproj" \
  -c Release -r "$RID" --self-contained \
  -p:PublishSingleFile=true \
  -o "$PUB"

echo "==> assemble bundle"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUB/"* "$APP/Contents/MacOS/"
cp "$ROOT/Hearbud.Avalonia/Assets/Info.plist" "$APP/Contents/Info.plist"
cp "$ROOT/Hearbud.Avalonia/Assets/hearbud.icns" "$APP/Contents/Resources/hearbud.icns"
# Icon regeneration (512px source): sips-resize into a 10-file .iconset, then
# iconutil -c icns (or tools/build-icns.py if iconutil misbehaves).

echo "==> ad-hoc sign"
codesign --force --deep -s - "$APP" 2>&1 | tail -n 2 || true

echo "==> verify"
codesign -dv "$APP" 2>&1 | head -n 4
ls "$APP/Contents/MacOS" | head -n 8
test -f "$APP/Contents/MacOS/runtimes/osx-arm64/native/libmp3lame.dylib" \
  -o -f "$APP/Contents/MacOS/runtimes/osx-x64/native/libmp3lame.dylib" \
  && echo "lame dylib: present" || echo "lame dylib: MISSING"

echo
echo "Bundle: $APP"
