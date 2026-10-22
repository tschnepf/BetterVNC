#!/bin/sh
# Shipped inside the Linux agent tarballs. Installs the BetterVNC guest agent for the current user: copies it into
# place, writes the config, registers it to start at login (systemd user service or autostart entry) and starts it.
# Any arguments go to "bettervnc-agent install" (for example --port 5971). The macOS agent is a native app with its
# own package.
set -e
cd "$(dirname "$0")"
chmod +x ./bettervnc-agent
exec ./bettervnc-agent install "$@"
