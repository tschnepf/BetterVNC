#!/bin/sh
# Removes the BetterVNC guest agent installed by the macOS package:
#   sudo /usr/local/libexec/bettervnc-agent/uninstall.sh
# The per-user part (LaunchAgent, config with the pairings, logs) is removed for the signed-in user through the agent's
# own "uninstall" command, then the app and the shared files go.
app="/Applications/BetterVNC Agent.app"
user="$(stat -f %Su /dev/console 2>/dev/null || true)"
if [ -n "$user" ] && [ "$user" != "root" ] && [ -x "$app/Contents/MacOS/bettervnc-agent" ]; then
  uid="$(id -u "$user")"
  launchctl asuser "$uid" sudo -u "$user" "$app/Contents/MacOS/bettervnc-agent" uninstall || true
fi
rm -rf "$app"
rm -f /usr/local/bin/bettervnc-agent
rm -rf /usr/local/libexec/bettervnc-agent
pkgutil --forget com.bettervnc.agent >/dev/null 2>&1 || true
echo "BetterVNC guest agent removed."
