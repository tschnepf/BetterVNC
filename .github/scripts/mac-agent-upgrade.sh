#!/bin/sh
# Upgrades this Mac from the v0.0.4 agent (the .NET one, installed from its release package) to the package built here,
# the way a user would, and checks what the upgrade has to do: the old app gone and exactly one left, in /Applications;
# the LaunchAgent pointing at it; the config with its token and paired viewer kept; the old package's files gone; and,
# when the runner has a desktop session to start agents in, one agent running, from /Applications, that lets the token in.
# Failures become error annotations.
#
#   .github/scripts/mac-agent-upgrade.sh <new .pkg> <probe command...>
#   (the probe command is the C# agent's client, e.g. dotnet out/probe/bettervnc-agent.dll probe)
set -u
new_pkg="$1"; shift

support="$HOME/Library/Application Support/BetterVNC"
legacy="$support/BetterVNC Agent.app"
config="$support/bettervnc-agent.json"
plist="$HOME/Library/LaunchAgents/com.bettervnc.agent.plist"
app="/Applications/BetterVNC Agent.app"
exe="$app/Contents/MacOS/bettervnc-agent"
token="ci-upgrade-token"
failed=0

check() { # check <what must be true> <command...>
  what="$1"; shift
  if "$@"; then echo "ok      $what"; else echo "::error title=Upgrade from v0.0.4::$what"; failed=1; fi
}
agents() { ps -axo command= | grep -F "BetterVNC Agent.app/Contents/MacOS/bettervnc-agent" | grep -v -e grep -e ' install' || true; }
install_log() { echo "--- install.log"; grep -i -e bettervnc -e 'BetterVNC Agent' /var/log/install.log | tail -n 25; }

echo "== console user: $(stat -f %Su /dev/console 2>/dev/null)"

echo "== v0.0.4"
curl -fsSL -o "$RUNNER_TEMP/old.pkg" https://github.com/tschnepf/BetterVNC/releases/download/v0.0.4/bettervnc-agent-0.0.4-macos.pkg
sudo installer -pkg "$RUNNER_TEMP/old.pkg" -target / || { echo "::error title=Upgrade from v0.0.4::the v0.0.4 package did not install"; exit 1; }
install_log
if [ ! -f "$plist" ]; then
  echo "(the package set nothing up for this user; running its install by hand)"
  /usr/local/bin/bettervnc-agent install
fi
if [ ! -d "$legacy" ]; then
  echo "::error title=Upgrade from v0.0.4::v0.0.4 did not install its app in $support; nothing to upgrade from"
  exit 1
fi
"$legacy/Contents/MacOS/bettervnc-agent" token set "$token" --config "$config"
# A paired viewer, as clicking Allow would have stored it.
perl -0pi -e 's/"viewers": \[\]/"viewers": [ { "id": "ci-viewer", "name": "CI viewer", "key": "Y2ktdmlld2VyLWtleS1mb3ItdGhlLXVwZ3JhZGUtdGVzdA==", "pairedAt": "2026-10-21T12:00:00-07:00" } ]/' "$config"
grep -q '"ci-viewer"' "$config" || { echo "::error title=Upgrade from v0.0.4::could not add a paired viewer to the v0.0.4 config"; cat "$config"; exit 1; }
sleep 3
old_running="$(agents)"
echo "running: ${old_running:-nothing}"

echo "== this build: $new_pkg"
sudo installer -pkg "$new_pkg" -target / || { echo "::error title=Upgrade from v0.0.4::the new package did not install"; install_log; exit 1; }
install_log
if [ "$(plutil -extract ProgramArguments.0 raw "$plist" 2>/dev/null)" != "$exe" ]; then
  echo "(the package set nothing up for this user; running its install by hand)"
  "$exe" install
fi
sleep 3

echo "== checks"
version="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$app/Contents/Info.plist" 2>/dev/null)"
echo "app version: $version"
check "the new app is in /Applications" test -x "$exe"
check "the old agent's app is gone ($legacy)" test ! -e "$legacy"
check "no second copy in ~/Applications" test ! -e "$HOME/Applications/BetterVNC Agent.app"
check "the LaunchAgent starts /Applications/BetterVNC Agent.app" test "$(plutil -extract ProgramArguments.0 raw "$plist" 2>/dev/null)" = "$exe"
check "the LaunchAgent uses the old config" test "$(plutil -extract ProgramArguments.2 raw "$plist" 2>/dev/null)" = "$config"
check "the LaunchAgent restarts the agent only after a crash" test "$(plutil -extract KeepAlive.SuccessfulExit raw "$plist" 2>/dev/null)" = "false"
# plutil reads the JSON, whichever way it is spaced (.NET writes "token": "…", Swift "token" : "…").
check "the config kept the token" test "$(plutil -extract token raw "$config" 2>/dev/null)" = "$token"
check "the config kept the paired viewer" test "$(plutil -extract viewers.0.id raw "$config" 2>/dev/null)" = "ci-viewer"
check "the v0.0.4 package's agent is gone" test ! -e /usr/local/libexec/bettervnc-agent/arm64 -a ! -e /usr/local/libexec/bettervnc-agent/x64
check "the uninstall script is in place" test -x /usr/local/libexec/bettervnc-agent/uninstall.sh
check "/usr/local/bin/bettervnc-agent is the new agent" test "$(readlink /usr/local/bin/bettervnc-agent)" = "$exe"

running="$(agents)"
echo "running: ${running:-nothing}"
if [ -n "$old_running" ]; then
  check "exactly one agent runs" test "$(printf '%s\n' "$running" | grep -c .)" = 1
  check "it runs from /Applications" sh -c "printf '%s\n' \"\$1\" | grep -q '^/Applications/BetterVNC Agent.app/'" _ "$running"
  answer="$("$@" 127.0.0.1:5970 "$token" info 2>&1)"
  echo "$answer"
  check "the new agent ($version) lets the old token in" sh -c "printf '%s\n' \"\$1\" | grep -qF \"Agent: BetterVNC agent \$2 on macos\"" _ "$answer" "$version"
else
  echo "::warning title=Upgrade from v0.0.4::the v0.0.4 agent did not start on this runner (no desktop session?), so the running agent was not checked"
fi

echo "--- config"
sed -E 's/"key" *: *"[^"]*"/"key": "…"/' "$config"
echo "--- agent log"
tail -n 30 "$support/bettervnc-agent.log" 2>/dev/null
tail -n 30 "$HOME/Library/Logs/bettervnc-agent.log" 2>/dev/null
exit $failed
