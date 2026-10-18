#!/bin/sh
# Builds bettervnc-capture, the macOS screen capture helper of the guest agent, as one universal binary (Apple silicon
# and Intel, macOS 12.3 or later). Needs Xcode or the Command Line Tools (swiftc, lipo, codesign).
#
#   sh tools/BetterVnc.Agent.MacCapture/build.sh [output file]
#
# The default output, tools/BetterVnc.Agent.MacCapture/build/bettervnc-capture, is where the agent's project looks:
# "dotnet publish tools/BetterVnc.Agent -r osx-arm64" (or osx-x64) afterwards puts it next to the agent.
set -eu

here="$(cd "$(dirname "$0")" && pwd)"
out="${1:-$here/build/bettervnc-capture}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$(dirname "$out")"

for arch in arm64 x86_64; do
  xcrun swiftc -O -swift-version 5 -target "$arch-apple-macos12.3" -o "$work/$arch" "$here"/Sources/*.swift
done
lipo -create -output "$out" "$work/arm64" "$work/x86_64"
# Apple silicon runs no unsigned code; an ad-hoc signature is enough for a program the agent starts itself.
codesign --force --sign - "$out"
lipo -info "$out"
