#!/bin/sh
# Builds BetterVNC Agent.app, the macOS guest agent: one universal binary (Apple silicon and Intel, macOS 12.3 or later),
# an Info.plist and an icon, signed. Needs Xcode (swift, lipo, codesign, iconutil).
#
#   sh tools/BetterVnc.Agent.Mac/build.sh [--version 1.2.3] [--sign <identity>] [--out <folder>]
#
# Version: --version (release.yml passes the tag's), else BETTERVNC_VERSION. Without either it is a development build
# and says so: the last release tag, the commits since and the commit, as git describe gives them (0.0.4-12-g5e4e859,
# with -dirty for local changes), never a number of its own that could pass for a release.
#
# The app lands in tools/BetterVnc.Agent.Mac/build/ unless --out says otherwise. Signing: --sign, else the
# BETTERVNC_CODESIGN_IDENTITY variable, else ad hoc. A certificate ("Apple Development", "Developer ID Application: …",
# any name or hash from 'security find-identity -v -p codesigning') keeps the privacy permissions across rebuilds; with
# an ad-hoc signature macOS ties them to one build. Certificates get the hardened runtime (needed for notarization).
#
# Then: "build/BetterVNC Agent.app/Contents/MacOS/bettervnc-agent" install
set -eu

here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
version="${BETTERVNC_VERSION:-}"
identity="${BETTERVNC_CODESIGN_IDENTITY:--}"
out="$here/build"
while [ $# -gt 0 ]; do
  case "$1" in
    --version) version="$2"; shift 2 ;;
    --sign) identity="$2"; shift 2 ;;
    --out) out="$2"; shift 2 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done

if [ -z "$version" ]; then
  described="$(git -C "$repo" describe --tags --match 'v[0-9]*' --long --dirty 2>/dev/null || true)"
  if [ -n "$described" ]; then
    version="${described#v}"
  else
    # No tags (a shallow clone): only the commit is known.
    version="0.0.0-dev-g$(git -C "$repo" rev-parse --short HEAD 2>/dev/null || echo unknown)"
  fi
fi
case "$version" in
  [0-9]*) ;;
  *) echo "--version must start with a number, such as 1.2.3 (got '$version')" >&2; exit 2 ;;
esac
# CFBundleShortVersionString is the version as given; CFBundleVersion, which macOS compares, takes up to three numbers.
bundle_version="$(printf '%s\n' "$version" | sed -E 's/^([0-9]+(\.[0-9]+){0,2}).*/\1/')"

swift build --package-path "$here" -c release --arch arm64 --arch x86_64
bin="$(swift build --package-path "$here" -c release --arch arm64 --arch x86_64 --show-bin-path)/bettervnc-agent"

app="$out/BetterVNC Agent.app"
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp "$bin" "$app/Contents/MacOS/bettervnc-agent"
sed -e "s/@VERSION@/$version/g" -e "s/@BUNDLE_VERSION@/$bundle_version/g" "$here/Info.plist" > "$app/Contents/Info.plist"

# The icon, from the viewer's 256-pixel PNG.
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
iconset="$work/BetterVNC.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256; do
  sips -z "$size" "$size" "$repo/docs/icon.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
done
for size in 16 32 128; do
  double=$((size * 2))
  sips -z "$double" "$double" "$repo/docs/icon.png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/BetterVNC.icns"

if [ "$identity" = "-" ]; then
  codesign --force --sign - "$app"
else
  case "$identity" in
    "Developer ID"*) timestamp="--timestamp" ;;
    *) timestamp="--timestamp=none" ;;
  esac
  codesign --force --options runtime $timestamp --sign "$identity" "$app"
fi
codesign --verify --strict "$app"
lipo -info "$app/Contents/MacOS/bettervnc-agent"
codesign -d -r- "$app" 2>&1 | sed -n "s/^#* *designated => /designated requirement: /p"
echo "$app (version $version, bundle version $bundle_version)"
