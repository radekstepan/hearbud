#!/bin/sh
# Reproducible macOS libmp3lame.dylib build from the vendored LAME 3.100 tarball.
#
# Usage (run from the repo root):
#   ./tools/lame/build-lame.sh [OUT_DIR]  # default OUT_DIR=Hearbud.Core/runtimes
#   ARCHS="arm64" ./build-lame.sh         # thin arm64-only build
#   TARBALL=/path/to/lame-3.100.tar.gz ./build-lame.sh
#
# Output layout (matches the .NET RuntimeTargets convention consumed by
# LameMp3Encoder.cs):
#   <OUT_DIR>/osx-arm64/native/libmp3lame.dylib   (thin arm64 slice)
#   <OUT_DIR>/osx-x64/native/libmp3lame.dylib     (thin x64 slice)
#   <OUT_DIR>/universal/libmp3lame.dylib          (lipo'd fat binary)
#
# Each dylib's install id is set to @rpath/libmp3lame.dylib so the app can
# load it from its own runtimes/ directory regardless of install location.
#
# X64 TODO (only relevant if the x86_64 leg ever fails in this script): the
# script falls back to shipping the arm64 slice alone and prints a TODO. As
# of Nov 2026 both legs build on an arm64 Mac via `clang -arch x86_64`
# cross-compile, so the fallback has not been needed.
set -eu

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
TARBALL="${TARBALL:-$SCRIPT_DIR/lame-3.100.tar.gz}"
OUT="${1:-$SCRIPT_DIR/../../Hearbud.Core/runtimes}"
ARCHS="${ARCHS:-arm64 x86_64}"
JOBS="$(sysctl -n hw.ncpu 2>/dev/null || echo 4)"

# The 8 LAME ABI entry points LameMp3Encoder.cs P/Invokes. Every shipped
# dylib is checked for exactly these exports before the script reports success.
REQUIRED_SYMBOLS="lame_init lame_set_in_samplerate lame_set_num_channels lame_set_brate lame_init_params lame_encode_buffer_ieee_float lame_encode_flush lame_close"

if [ ! -f "$TARBALL" ]; then
  echo "error: tarball not found: $TARBALL (override with TARBALL=...)" >&2
  exit 1
fi

WORK="$(mktemp -d /tmp/lamebuild.XXXXXX)"
trap 'rm -rf "$WORK"' EXIT INT TERM
echo "work dir: $WORK"
echo "output:   $OUT (archs: $ARCHS)"

BUILT=""
for ARCH in $ARCHS; do
  echo "=== building $ARCH ==="
  SRC="$WORK/lame-$ARCH"
  mkdir -p "$SRC"
  tar xzf "$TARBALL" -C "$SRC" --strip-components=1

  # ONE-LINE .sym FIX (reproduced from the proven /tmp/lamebuild logs):
  # include/libmp3lame.sym lists lame_init_old, but lame.c compiles that
  # function as `static` whenever DEPRECATED_OR_OBSOLETE_CODE_REMOVED=1
  # (the default in include/lame.h), so no global _lame_init_old exists.
  # Modern Apple ld treats the stale -exported_symbols_list entry as a hard
  # error ("symbol(s) not found for architecture ..."), failing the
  # libmp3lame.la link. Deleting the line fixes the link; the symbol was
  # obsolete and nothing references it.
  grep -v '^lame_init_old$' "$SRC/include/libmp3lame.sym" > "$SRC/include/libmp3lame.sym.fixed"
  mv "$SRC/include/libmp3lame.sym.fixed" "$SRC/include/libmp3lame.sym"
  if grep -q '^lame_init_old$' "$SRC/include/libmp3lame.sym"; then
    echo "error: .sym fix did not apply" >&2
    exit 1
  fi

  (
    cd "$SRC"
    if [ "$ARCH" = "x86_64" ]; then
      # Cross-compile from arm64 host: -arch selects the slice, --host tells
      # autoconf we are cross (avoids run-tests), --build keeps libtool honest.
      CC="clang -arch x86_64" CFLAGS="-O3 -arch x86_64" \
        ./configure --enable-shared --disable-static --disable-frontend \
          --host=x86_64-apple-darwin --build="$(./config.guess)" \
          >"$WORK/configure-$ARCH.log" 2>&1
    else
      CC="clang -arch arm64" CFLAGS="-O3 -arch arm64" \
        ./configure --enable-shared --disable-static --disable-frontend \
          >"$WORK/configure-$ARCH.log" 2>&1
    fi
    make -j"$JOBS" >"$WORK/make-$ARCH.log" 2>&1
  ) || {
    echo "warning: $ARCH build failed (see $WORK/make-$ARCH.log), skipping" >&2
    continue
  }

  DYLIB="$SRC/libmp3lame/.libs/libmp3lame.0.dylib"
  if [ ! -f "$DYLIB" ]; then
    echo "warning: expected dylib missing for $ARCH, skipping" >&2
    continue
  fi

  case "$ARCH" in
    arm64)  RID="osx-arm64" ;;
    x86_64) RID="osx-x64" ;;
    *)      RID="osx-$ARCH" ;;
  esac
  DEST="$OUT/$RID/native"
  mkdir -p "$DEST"
  cp "$DYLIB" "$DEST/libmp3lame.dylib"
  # @rpath id: loadable from the app's own runtimes/ dir at any install path.
  install_name_tool -id "@rpath/libmp3lame.dylib" "$DEST/libmp3lame.dylib"
  BUILT="$BUILT $ARCH:$DEST/libmp3lame.dylib"
  echo "built: $DEST/libmp3lame.dylib"
done

if [ -z "$BUILT" ]; then
  echo "error: no architecture built successfully" >&2
  exit 1
fi

# Fat universal binary when both slices exist.
if [ -f "$OUT/osx-arm64/native/libmp3lame.dylib" ] && [ -f "$OUT/osx-x64/native/libmp3lame.dylib" ]; then
  mkdir -p "$OUT/universal"
  lipo -create "$OUT/osx-arm64/native/libmp3lame.dylib" \
    "$OUT/osx-x64/native/libmp3lame.dylib" \
    -output "$OUT/universal/libmp3lame.dylib"
  install_name_tool -id "@rpath/libmp3lame.dylib" "$OUT/universal/libmp3lame.dylib"
  echo "built: $OUT/universal/libmp3lame.dylib"
else
  echo "TODO(x64): only one slice built; ship arm64 now and re-run with ARCHS=\"arm64 x86_64\" on a Mac with Xcode CLT to add the x64 slice." >&2
fi

# --- verification: file / lipo / install id / exported symbols ---
echo "=== verification ==="
FAIL=0
for f in "$OUT"/*/native/libmp3lame.dylib "$OUT"/universal/libmp3lame.dylib; do
  [ -f "$f" ] || continue
  file "$f"
  lipo -info "$f"
  echo "install id: $(otool -D "$f" | tail -1)"
  for sym in $REQUIRED_SYMBOLS; do
    if ! nm -gU "$f" | grep -q " T _$sym\$"; then
      echo "error: $f missing export _$sym" >&2
      FAIL=1
    fi
  done
  echo "exports OK: $f"
done
[ "$FAIL" -eq 0 ] || exit 1
echo "ALL CHECKS PASSED"
