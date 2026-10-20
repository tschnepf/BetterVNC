#!/bin/sh
# The spike measurements that need a live (unlocked) screen. Each run shows a topmost, non-activating test window at
# the bottom right of the primary display for about 10 s. Output: the file given as $1 (default suite-out.txt).
export PATH="/c/Program Files/dotnet:$PATH"
cd /c/Users/Tim/Source/BetterVNC || exit 1
SPIKE=tools/BetterVnc.AvaloniaSpike/bin/Release/net10.0-windows/BetterVnc.AvaloniaSpike.dll
WPF=tools/BetterVnc.AvaloniaSpike.Wpf/bin/Release/net10.0-windows/BetterVnc.AvaloniaSpike.Wpf.dll
OUT="${1:-suite-out.txt}"
: > "$OUT"
run() { echo "### $*" >> "$OUT"; timeout 90 dotnet "$@" 2>&1 | grep -v '^$' >> "$OUT"; }
for size in 1920x1080 3840x2160; do
  for d in small full; do
    run "$WPF" --size $size --dirty $d --seconds 6
    run "$SPIKE" fb --size $size --dirty $d --draw bitmap --seconds 6
    run "$SPIKE" fb --size $size --dirty $d --draw gl --seconds 6
    run "$SPIKE" fb --size $size --dirty $d --draw gl --composition dcomp --ui-render --seconds 6
    run "$SPIKE" fb --size $size --dirty $d --draw swapchain --seconds 6
  done
done
for p in bitmap sw-swapchain dxva-swapchain composition; do
  run "$SPIKE" video --path $p --size 1280x720 --seconds 6
done
run "$SPIKE" host
echo "### done" >> "$OUT"
