#!/bin/sh
# Builds the macOS installer package for the BetterVNC guest agent.
#
#   installers/macos/build-pkg.sh <version> <published osx-arm64 dir> <published osx-x64 dir> <output dir>
#
# One package serves both Apple silicon and Intel: both builds go to /usr/local/libexec/bettervnc-agent/<arch>/,
# and the postinstall script links the right one to /usr/local/bin/bettervnc-agent and runs "bettervnc-agent install"
# as the signed-in user, which copies it into ~/Library/Application Support/BetterVNC, writes the LaunchAgent and
# starts the menu bar item, exactly as the command-line install does.
#
# Each published directory must also hold bettervnc-capture, the screen capture helper (one universal binary, built
# by tools/BetterVnc.Agent.MacCapture/build.sh); it goes next to each build, where the agent looks for it.
set -eu

version="$1"; arm64="$2"; x64="$3"; out="$4"
here="$(cd "$(dirname "$0")" && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

root="$work/root/usr/local/libexec/bettervnc-agent"
mkdir -p "$root/arm64" "$root/x64" "$work/root/usr/local/bin" "$out"
for arch in arm64 x64; do
  if [ "$arch" = arm64 ]; then from="$arm64"; else from="$x64"; fi
  cp "$from/bettervnc-agent" "$from/bettervnc-capture" "$root/$arch/"
  chmod 755 "$root/$arch/bettervnc-agent" "$root/$arch/bettervnc-capture"
done
cp "$here/uninstall.sh" "$root/uninstall.sh"
chmod 755 "$root/uninstall.sh"

mkdir -p "$work/scripts"
cp "$here/scripts/postinstall" "$work/scripts/postinstall"
chmod 755 "$work/scripts/postinstall"

pkgbuild \
  --root "$work/root" \
  --scripts "$work/scripts" \
  --identifier com.bettervnc.agent \
  --version "$version" \
  --install-location / \
  "$work/agent.pkg"

sed "s/@VERSION@/$version/g" "$here/distribution.xml" > "$work/distribution.xml"
productbuild \
  --distribution "$work/distribution.xml" \
  --package-path "$work" \
  --resources "$here/resources" \
  "$out/bettervnc-agent-$version-macos.pkg"

ls -l "$out/bettervnc-agent-$version-macos.pkg"
