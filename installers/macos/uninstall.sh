#!/bin/sh
# Removes the BetterVNC guest agent installed by the macOS package:
#   sudo /usr/local/libexec/bettervnc-agent/uninstall.sh
# The per-user part (LaunchAgent, copy in ~/Library/Application Support/BetterVNC, config with the pairings) is
# removed for the signed-in user through the agent's own "uninstall" command, then the shared files go.
user="$(stat -f %Su /dev/console 2>/dev/null || true)"
if [ -n "$user" ] && [ "$user" != "root" ] && [ -x /usr/local/bin/bettervnc-agent ]; then
  uid="$(id -u "$user")"
  launchctl asuser "$uid" sudo -u "$user" /usr/local/bin/bettervnc-agent uninstall || true
fi
rm -f /usr/local/bin/bettervnc-agent
rm -rf /usr/local/libexec/bettervnc-agent
pkgutil --forget com.bettervnc.agent >/dev/null 2>&1 || true
echo "BetterVNC guest agent removed."
