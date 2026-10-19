#!/bin/sh
# Builds the macOS installer package for the BetterVNC guest agent.
#
#   installers/macos/build-pkg.sh <version> <BetterVNC Agent.app> <output dir> [installer signing identity]
#
# The app (universal, already signed by tools/BetterVnc.Agent.Mac/build.sh) goes to /Applications unchanged, so its
# signature stays intact. The postinstall script links /usr/local/bin/bettervnc-agent to it and runs
# "bettervnc-agent install" as the signed-in user, which writes the LaunchAgent and starts the menu bar item, exactly as
# the command-line install does. With a "Developer ID Installer: …" identity the package is signed with productsign.
set -eu

version="$1"; app="$2"; out="$3"; installer_identity="${4:-}"
here="$(cd "$(dirname "$0")" && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

mkdir -p "$work/root/Applications" "$work/root/usr/local/libexec/bettervnc-agent" "$out"
ditto "$app" "$work/root/Applications/BetterVNC Agent.app"
cp "$here/uninstall.sh" "$work/root/usr/local/libexec/bettervnc-agent/uninstall.sh"
chmod 755 "$work/root/usr/local/libexec/bettervnc-agent/uninstall.sh"
xattr -cr "$work/root" 2>/dev/null || true # quarantine and the like; Installer restores what remains (com.apple.provenance) from "._" entries

mkdir -p "$work/scripts"
cp "$here/scripts/postinstall" "$work/scripts/postinstall"
chmod 755 "$work/scripts/postinstall"

# Without this, Installer "relocates" the app to wherever another copy with the same bundle id is (a build folder, the
# old agent in ~/Library/Application Support) instead of putting it in /Applications.
pkgbuild --analyze --root "$work/root" "$work/components.plist"
plutil -replace BundleIsRelocatable -bool false "$work/components.plist"

pkgbuild \
  --root "$work/root" \
  --component-plist "$work/components.plist" \
  --scripts "$work/scripts" \
  --identifier com.bettervnc.agent \
  --version "$version" \
  --install-location / \
  "$work/agent.pkg"

sed "s/@VERSION@/$version/g" "$here/distribution.xml" > "$work/distribution.xml"
pkg="$out/bettervnc-agent-$version-macos.pkg"
productbuild \
  --distribution "$work/distribution.xml" \
  --package-path "$work" \
  --resources "$here/resources" \
  "$work/unsigned.pkg"
if [ -n "$installer_identity" ]; then
  productsign --sign "$installer_identity" "$work/unsigned.pkg" "$pkg"
else
  mv "$work/unsigned.pkg" "$pkg"
fi

ls -l "$pkg"
