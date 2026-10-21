# Still-screen repair: measured before and after

The agent's H.264 stream used to stay lossy when the screen went still. A viewer with VNC then switched to its VNC
picture after 700 ms; a viewer without VNC asked for a key frame. Now the agent sends the exact pixels of whatever its
frames showed lossy, about 150 ms after the screen goes still, and the viewer shows those. This is item 6.3 of
[JUMP-DESKTOP-COMPARISON.md](JUMP-DESKTOP-COMPARISON.md), still-screen refinement ("Sharpen still screens inside the
stream"), done losslessly instead of by re-encoding. This page has the measurements that led to it and the numbers
before and after.

## Setup

| | |
|---|---|
| Target | Win11-Coder on the Unraid server Tardis: AMD EPYC 7B13, 20 vCPUs, Red Hat QXL display (display-only driver) at 1920×1080, no GPU, so Microsoft's software H.264 encoder |
| Viewer | This PC (Ryzen AI 9 HX 370) on Wi-Fi 6, 573 Mbit/s link; ping to the guest 7–14 ms |
| Agent builds | before: `c4a3b23` (main); after: this change. Both ran in the guest as the signed-in user, with a token, on port 5971 |
| Tool | [`tools/BetterVnc.AgentBench`](../tools/BetterVnc.AgentBench) with its guest window `bench-target.ps1` |

The guest window shows a page of syntax-coloured code on a dark background. F14 scrolls it at 60 Hz, 6 px per frame,
which is close to a full-screen change every frame. F13 flips a marker square. The bench sends those keys through the
agent and decodes every frame. Its numbers are:
- **input to picture:** the time from the key to a decoded picture showing the marker flipped;
- **motion:** frame rate and bitrate while the page scrolls;
- **still:** after the scrolling stops, how close the shown picture is to the guest's real pixels. That is luma PSNR
  over the text area against QEMU's lossless VNC framebuffer; "exact" means identical.

## What the baseline showed

Two picture paths into the same VM, current code:

| | VNC lossless (QEMU) | Agent stream, 20 Mbit/s |
|---|---|---|
| Input to picture, still screen, changes 0.7–1.3 s apart (median / p90) | **707–740 / 851–871 ms** | 61–72 / 72–256 ms |
| … changes 0.2–0.4 s apart | 492 / 597 ms | — |
| … changes 3–5 s apart | 1228 / 1408 ms | — |
| Motion: frames per second | 9.4–9.8 | 7.2–7.3 |
| Motion: input to picture (median) | 282–306 ms | 180–196 ms |
| Motion: bitrate | 19.3–19.7 Mbit/s | 10.7 Mbit/s |

- **QEMU's VNC server is slow to show a single change.** A keystroke after a pause takes 0.5–1.2 s to appear, and the
  delay grows with the time since the previous change. [inference] QEMU looks at the guest's display on a refresh timer
  that backs off while nothing changes, and a client's update request does not make it look sooner.
  [VncClient.cs](../src/BetterVnc.Protocol/VncClient.cs) reads and decodes an update in 11–12 ms, so request pipelining
  would not help much here.
- **Motion is limited by the guest itself.** During the scroll, the guest's compositor only presents about 15 frames a
  second: the window draws 60, but the Basic Render Driver composes on the CPU. Desktop Duplication's
  `AccumulatedFrames` showed ~15 presents/s even when the agent did not read any pixels.
- **What the agent did not change.** Mapping the frame waits 15 ms for a small change and 50–80 ms during the scroll,
  for the software renderer. The QXL desktop is in system memory, so Desktop Duplication works on it (contrary to the
  comment in `DesktopDuplicator`). Two experiments made the agent's own work small, but the delivered frame rate stayed
  at 7–8 fps, the compositor's limit:
  - reading the frame in place with `MapDesktopSurface`;
  - releasing frames at once into a ring of staging textures.

  Neither was kept.
- **The stream was lossy at rest, and a VNC session saw a stale picture first.** After the scroll stopped, the stream
  stayed at 41.4–41.7 dB (20 Mbit/s) or 30–35 dB (8 Mbit/s). The viewer with VNC switched to its VNC framebuffer
  700 ms after the last frame. While streaming, that framebuffer is refreshed only once a second, so it showed an older
  scroll position (18–19 dB) for another 50–370 ms. Only then did QEMU's full update arrive.

So the measured gap the agent could close was the still picture: making it exact and current without depending on
QEMU's VNC server.

## The change

- **Agent** ([H264ScreenSession.cs](../tools/BetterVnc.Agent/Video/H264ScreenSession.cs)):
  - It records every region its H.264 frames encoded since the last repair. A key frame counts as the whole picture.
  - Once nothing has changed for 150 ms and the encoder is empty, it sends those regions from the captured picture as
    a `videoRepair` message, then clears the record.
  - Only when the viewer asks (`repair` on `startVideo`), and only for H.264 sessions.
- **Protocol** ([AgentMessage.cs](../src/BetterVnc.Agent.Protocol/AgentMessage.cs),
  [RepairPayload.cs](../src/BetterVnc.Agent.Protocol/RepairPayload.cs)):
  - A repair is rectangles of B, G, R pixels, each at most 64 rows and compressed with zlib on its own, so both sides
    work on them in parallel.
  - Repairs come on the same connection as the frames, in order, with their own sequence numbers. `keyframe` marks a
    repair of the whole picture.
  - A lost repair, or one the server dropped on a slow link, is followed by a key frame, which leads to a whole repair.
  - Old agents ignore the request, and old viewers never send it.
- **Viewer** ([VideoStreamPlayer.cs](../src/BetterVnc.Viewer.Core/Services/VideoStreamPlayer.cs)):
  - It paints repairs onto a picture of its own and, once that picture is complete, shows it through the same path
    as a motion JPEG frame (GPU, or WPF).
  - While that exact picture is showing, the 700 ms switch to VNC and the still-screen key frame do not happen.
  - Both remain the fallback: for agents without repairs, and when a repair comes too late.
  - The status bar adds "still: exact".

## Before and after

After the scrolling stops (each cell lists every run):

| | Before, 20 Mbit/s | After, 20 Mbit/s | Before, 8 Mbit/s | After, 8 Mbit/s |
|---|---|---|---|---|
| Stream picture at rest | 41.7, 41.4 dB | **exact** | 35.2, 30.2 dB | **exact** |
| Stream picture exact | never | **300, 336, 403 ms** after the stop key | never | **335, 362 ms** |
| Viewer with VNC: switch to the VNC picture | 843, 826 ms; that picture is 18.2, 19.1 dB (an older scroll position) | does not switch | 813, 846 ms; exact (its once-a-second update had just come), 18.7 dB | does not switch |
| Viewer with VNC: exact | 1093, 1059 ms | 300–403 ms (the repair) | 943, 1147 ms | 335, 362 ms |
| Viewer without VNC: best picture at rest | key frame, 42.7, 42.5 dB | exact | key frame, 36.3, 30.9 dB | exact |
| Repair size and time | — | 243–253 KB, 4.4–5.2 ms to compress (88–90% of the screen) | — | 238–249 KB |

The last frame of the scroll arrived 140–240 ms after the stop key. The repair followed 160–172 ms after it: 150 ms of
stillness, then compressing, sending and painting. An isolated change, such as the marker flip or a typed character,
gets a repair of about 1 KB that takes 0.3–1 ms. The first repair of a session covers the whole screen: 241–254 KB in
6.5–23 ms.

Motion and latency did not change (expected; the repair only runs when the screen is still):

| | Before (4 runs) | After (5 runs) |
|---|---|---|
| Input to picture, still (median) | 61–72 ms | 56–68 ms |
| Motion frames per second | 7.2–7.6 | 6.9–7.8 |
| Motion input to picture (median) | 177–196 ms | 172–214 ms |

## In the real viewer

The WPF viewer was run on a scratch profile store (`BETTERVNC_PROFILE_DIR`) in "Stream, quality" against the same
agent. Its status bar read:
- while scrolling: `stream h264 1920×1080 (DXVA → GPU)` at 8 fps;
- 1.5 s after the scroll stopped: `stream h264 1920×1080 (DXVA → GPU), still: exact`, with the text sharp on screen.

## Not covered

- **RustDesk A/B.** Not run: it means downloading RustDesk and running it on this PC and in the VM, which needs the
  user's approval. Expect the same ~15 fps ceiling during motion in this VM, since it captures through Desktop
  Duplication too.
- **The Mac mini.** Not measured. It needs a pairing prompt on the user's Mac and a different test window.
- **VNC lossless profiles keep QEMU's 0.5–1.2 s latency for single changes.** This change is in the stream modes.
  With an agent in the VM, a stream mode now gives ~60 ms input to picture and an exact picture at rest within
  ~0.35 s. Whether stream should become the default for VMs with an agent is a viewer decision.
- **Faster motion in VMs without a GPU** needs a cheaper compositor (a GPU, or a smaller display mode, see
  [WINDOWS-HOST-IN-VMS.md](WINDOWS-HOST-IN-VMS.md) 5.4), not a faster capture.

## Reproducing

In the guest, as the signed-in user: start the agent with a token (`bettervnc-agent --config <file with a token> --port
5971`), then run `powershell -w hidden -ep bypass -f bench-target.ps1`. On the viewer side:

```bash
dotnet tools/BetterVnc.AgentBench/bin/Debug/net10.0-windows/BetterVnc.AgentBench.dll 10.20.30.40:5971 <token> --vnc 10.20.30.2:5902 --kbps 20000 --label after --json results.json
```

`--no-repair` measures the old behaviour against a new agent; `--rfb` measures the VNC picture path instead.
