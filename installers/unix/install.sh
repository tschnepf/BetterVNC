#!/bin/sh
# Shipped inside the Linux and macOS agent tarballs. Installs the BetterVNC guest agent for the current user: copies
# it into place, writes the config, registers it to start at login (systemd user service or autostart entry on Linux,
# LaunchAgent on macOS) and starts it. Any arguments go to "bettervnc-agent install" (for example --port 5971).
set -e
cd "$(dirname "$0")"
chmod +x ./bettervnc-agent
if [ "$(uname)" = "Darwin" ]; then
  xattr -d com.apple.quarantine ./bettervnc-agent 2>/dev/null || true
  # The screen capture helper; "install" copies it next to the installed agent.
  if [ -f ./bettervnc-capture ]; then
    chmod +x ./bettervnc-capture
    xattr -d com.apple.quarantine ./bettervnc-capture 2>/dev/null || true
  fi
fi
exec ./bettervnc-agent install "$@"
