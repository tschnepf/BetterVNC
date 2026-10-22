#!/bin/sh
# Builds the macOS installer package for the BetterVNC guest agent.
#
#   installers/macos/build-pkg.sh <BetterVNC Agent.app> <output dir> [installer signing identity]
#
# The app (universal, already signed by tools/BetterVnc.Agent.Mac/build.sh) goes to /Applications unchanged, so its
# signature stays intact. The package takes its version from the app's Info.plist, which build.sh stamped, so the two
# never disagree. The preinstall script stops the running agent; postinstall links /usr/local/bin/bettervnc-agent to the
# app and runs "bettervnc-agent install" as the signed-in user, which removes the C# agent of v0.0.4 and earlier (and any
# other installed copy), points the LaunchAgent at /Applications, keeps the config and starts the menu bar item, exactly
# as the command-line install does. With a "Developer ID Installer: …" identity the package is signed with productsign.
set -eu

app="$1"; out="$2"; installer_identity="${3:-}"
here="$(cd "$(dirname "$0")" && pwd)"
version="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$app/Contents/Info.plist")"
bundle_version="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "$app/Contents/Info.plist")"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

mkdir -p "$work/root/Applications" "$work/root/usr/local/libexec/bettervnc-agent" "$out"
ditto "$app" "$work/root/Applications/BetterVNC Agent.app"
cp "$here/uninstall.sh" "$work/root/usr/local/libexec/bettervnc-agent/uninstall.sh"
chmod 755 "$work/root/usr/local/libexec/bettervnc-agent/uninstall.sh"
xattr -cr "$work/root" 2>/dev/null || true # quarantine and the like; Installer restores what remains (com.apple.provenance) from "._" entries

mkdir -p "$work/scripts"
cp "$here/scripts/preinstall" "$here/scripts/postinstall" "$work/scripts/"
chmod 755 "$work/scripts/preinstall" "$work/scripts/postinstall"

# Without BundleIsRelocatable, Installer "relocates" the app to wherever another copy with the same bundle id is (a build
# folder, the old agent in ~/Library/Application Support) instead of putting it in /Applications. Without
# BundleIsVersionChecked, it would keep a copy already there whose version is higher (a development build) and install
# nothing.
pkgbuild --analyze --root "$work/root" "$work/components.plist"
plutil -replace 0.BundleIsRelocatable -bool false "$work/components.plist"
plutil -replace 0.BundleIsVersionChecked -bool false "$work/components.plist"

pkgbuild \
  --root "$work/root" \
  --component-plist "$work/components.plist" \
  --scripts "$work/scripts" \
  --identifier com.bettervnc.agent \
  --version "$bundle_version" \
  --install-location / \
  "$work/agent.pkg"

sed -e "s/@VERSION@/$version/g" -e "s/@BUNDLE_VERSION@/$bundle_version/g" "$here/distribution.xml" > "$work/distribution.xml"
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
