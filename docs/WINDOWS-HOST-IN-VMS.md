# Windows as the host, inside VMs: what Jump does and what the agent should do

"Host" here means the machine whose screen is shared: a Windows guest on Unraid, running the
BetterVNC agent. The question is how to stream that screen well when the VM has no hardware
video encoder, and often no GPU at all. This builds on
[JUMP-DESKTOP-COMPARISON.md](JUMP-DESKTOP-COMPARISON.md), which covers the transport and the
viewer.

## Sources

| Source | What it gave |
|---|---|
| REA 6.7.0 + Ghidra 12.1.4 on `JumpConnectCore.dll` 10.15.46.0 (Jump's Windows host) | Capture fallbacks, encoder selection and fallback, software AV1 and VP8 settings, thread sizing, degradation preference |
| REA + Ghidra on `drivers\graphics\JumpVirtualMonitorDriver.dll` and `drivers\hid\jumpvms.sys` | How Jump's virtual monitor driver works; what the HID driver is |
| String inventory of the same files | Log formats, setting names and class names, used to choose what to decompile |
| BetterVNC source at `5f997f1` | The agent's current behaviour, with file and line |

Labels as before: **[bin]** decompiled unless it says "string", **[src]** BetterVNC source,
**[inference]** reasoning not directly observed.

---

## 1. The short answer

In a VM without a GPU, every stage of the stream runs on the guest's vCPUs, which the
guest's own work also needs. Bandwidth is rarely the limit (virtio-net on the same LAN); CPU is.
Jump's Windows host is built to **do less work**, not to do the same work faster:

* **Encode only what changed.** Dirty rectangles from Desktop Duplication become a VP8 active
  map, so unchanged 16×16 blocks are skipped.
* **Encode fewer pixels.** A virtual display driver gives the guest a monitor at the viewer's
  size, and stands in when the guest has no monitor at all.
* **Use codecs built for screen content.** The software default is AV1 with palette mode and
  text-aware detection; without AVX2 it is VP8.
* **Scale effort to the budget.** Encoder speed and static threshold follow the bitrate, the
  thread count is capped, and libwebrtc may trade frame rate and resolution when the CPU falls
  behind.
* **Fall back automatically.** Hardware encoder, then software AV1, then VP8. Desktop
  Duplication, then Windows Graphics Capture, then GDI.

BetterVNC's agent in a GPU-less VM copies the **whole frame**, converts the **whole frame** to
NV12 with scalar code, and hands the **whole frame** to Microsoft's software H.264 encoder. It
does this at a fixed frame rate and bitrate, whatever the CPU can sustain.

---

## 2. What Windows offers in each kind of Unraid VM [inference]

| VM setup | Who renders the desktop | Hardware encoder | What the agent gets today |
|---|---|---|---|
| No GPU (VGA, QXL, virtio-gpu or Basic Display) | Microsoft Basic Render Driver (software rasterizer on the vCPUs) | none | Microsoft's software H.264 MFT, or motion JPEG when Media Foundation lacks H.264 (N editions, some Server installs) |
| GPU passthrough with a monitor or dummy plug | the GPU | NVENC / AMF / Quick Sync | a hardware MFT, but fed from system memory |
| GPU passthrough with no monitor, emulated display as primary | depends on Windows' choice of render adapter | on the GPU, which may not own the output being captured | duplication of the default adapter's outputs only |
| Intel iGPU shared into the VM (SR-IOV / GVT-g) | the iGPU | Quick Sync | a hardware MFT |

The first row is the common case for utility and test VMs, and the one this document is mostly
about.

---

## 3. Jump's Windows host, piece by piece [bin]

### 3.1 Capture

* Desktop Duplication via `DuplicateOutput1`/`IDXGIOutput5`, with the D3D device created for the
  adapter that owns the output (`Could not create dx device for preferred LUID`) [string].
* It reads `GetFrameDirtyRects` and `GetFrameMovedRects` and logs `AccumulatedFrames`,
  `RectsCoalesced` and `ProtectedContentMaskedOut` [string].
* Colour conversion and rotation run as compute shaders: `pixelconvert_dx_cs_420.hlsl`
  (ARGB→NV12), `_ayuv.hlsl` (4:4:4), `_hdr_to_sdr.hlsl` and a render-target fallback for GPUs
  without UAV support. The setting `RtcUseHardwareYUVConversion` switches GPU conversion off
  [string].
* Fallback chain: `DD failure, falling back` → Windows Graphics Capture
  (`windowsgraphicscapturer_win.cpp`) → `Falling back to slow capturer` (GDI), announced as
  `Capturer changed: {}` [string].

### 3.2 Virtual display driver (`JumpVirtualMonitorDriver.dll`, IddCx)

* A UMDF indirect-display driver (`IddCx`, `WUDFx02000`) that adds and removes monitors on
  demand (`IddCxMonitorCreate`, `IddCxMonitorArrival`, `IddCxMonitorDeparture`) with mode lists
  pushed from the host process through the registry (`DriverSettings.cpp`, per-monitor UID and
  resolutions).
* Its swap-chain loop (`SwapChainProcessor::RunCore`, `FUN_180018d40`) is the IddCx sample
  pattern. It acquires each frame, releases it at once, calls `FinishedProcessingFrame`, and
  waits up to 16 ms for the next. **The driver does not transport pixels.** It gives Windows a
  real monitor, and the host captures that monitor like any other output.
* It enables a **hardware cursor** on each monitor (256×256, alpha and XOR; `FUN_180017d94`), so
  the pointer is not drawn into the frames and travels separately.
* The host uses it for:
  * matching the viewer's displays;
  * a **fallback display** when the machine is headless (`Headless host after grace period,
    engaging fallback virtual display`, `Jump Desktop Fallback Display`,
    `RtcHeadlessFallbackWin`) [string];
  * optionally disabling the physical monitors while virtual ones are in use (`Disabling monitor`,
    `Restoring physical displays`) [string].

In a GPU-less VM [inference], this means a monitor at the viewer's resolution, whatever modes
the emulated display adapter offers, and no dependence on that adapter's driver at all.

### 3.3 Encoder line-up, choice and fallback

* **Hardware** through vendor SDKs: NVENC, AMD AMF, Intel oneVPL (see the comparison doc).
* **Software**: `jump.aom.av1` (id 5) and `jump.vpx.vp8` (id 1). The order is AV1 first when the
  CPU has AVX2, VP8 first otherwise (`CPU doesn't support AVX2, prefer vpx as default`,
  `FUN_180613910`).
* **When an encoder breaks mid-session** (for example a hardware encoder failing), the encoder
  selector switches the track to **id 5, software AV1** (`Encoder broken for track: {}`,
  `FUN_1803070c0`).
* Settings the host honours [string]: `HardwareAccelerationPreference`,
  `SoftwareEncoderPreference`, `HardwareVendorPreference`, `RtcInitialVideoCodecId`,
  `RtcMaxEncoderThreads`, `RtcMaxFPS`, `RtcMinBPS`, `RtcStartBPS`, `RtcMaxBPS`,
  `RtcAllowPeerToChangeVideoCodec`.

### 3.4 Software AV1 (libaom) settings

From `FUN_18061c130`:

| Setting | Value |
|---|---|
| Content tuning | screen; screen-content detection mode 2 (anti-aliased text aware); palette on |
| Tools off for speed | IntraBC, TPL model, OBMC, warped motion, order hints; AQ off |
| Rate control | VBR; QP 12–63; undershoot 50%, overshoot 100%; buffer 3000 ms (initial 2100, optimal 1500); no lag frames |
| Threads | `min(std::thread::hardware_concurrency(), RtcMaxEncoderThreads)`, logged as `Using threads:{}, logical:{}, pcores:{}, max:{}` |
| Refinement QP targets by bitrate | QP 12 below about 20 Mbit/s, 10 below 23.5, 8 below 27.6, 6 above |

### 3.5 Software VP8 (libvpx) settings, applied per frame

From `FUN_180620b50`, `FUN_180350c40`, `FUN_1803508a0` and `FUN_180350fc0`:

| Setting | Value |
|---|---|
| Active map (`VP8E_SET_ACTIVEMAP`) | built from the frame's dirty rectangles on the 16×16 macroblock grid; all blocks active after a size change or keyframe |
| Static threshold (`VP8E_SET_STATIC_THRESHOLD`) | by bitrate: 200 below 3 Mbit/s, 100 below 5, 50 below 10, 25 below 20.5, 10 below 25.6, then 5 |
| Speed (`VP8E_SET_CPUUSED`) | by bitrate: −10 up to about 18 Mbit/s, −12 to 25.6, −14 to 30.7, −16 to 41 (realtime, faster as the bitrate rises) |
| Target bitrate | **85%** of what congestion control allows |
| QP | max 63; the minimum comes from the same bitrate→QP table as refinement |
| After each frame | reads `VP8E_GET_LAST_QUANTIZER` to drive still-screen refinement |

### 3.6 When the CPU cannot keep up

* Degradation preference **3**, BALANCED in libwebrtc's enum (`FUN_1803a2bf0`). libwebrtc's
  overuse detector then steps frame rate and resolution down together when encoding falls behind.
* The encoder sits behind a bounded queue and drops frames rather than queuing
  (`Dropping frame - queue too large`).
* The frame governor sends at most one frame per interval, and nothing when the screen is still
  (unless refinement is running).
* The bandwidth monitor's overshoot compensation (comparison doc, 3.4) works for software
  encoders too.

### 3.7 Process model and input

* A SYSTEM service with per-session proxy processes (`RtcSessionsServer`, `RtcSessionProxy`,
  `RtcDesktopProxy`, `RtcSameUserProxyLauncher`). It follows the input desktop (`OpenInputDesktop`,
  `SetThreadDesktop`), so it can stream the lock screen, sign-in and UAC prompts, and send
  Ctrl+Alt+Del through `sas.dll` [string].
* The encoder can run out of process (`renc_server`/`renc_client`) [string].
* `jumpvms.sys` is the "Jump Desktop Virtual Mouse Driver" (`jvmouse.sys`), a kernel driver for
  pointer injection [string].

---

## 4. The BetterVNC agent in a VM today [src]

| Stage | Behaviour | Where |
|---|---|---|
| Process | Logon task with limited rights, in the user's session. No lock screen, sign-in or UAC capture; the viewer falls back to QEMU's RFB there, which already covers those screens. | `Installer.cs:155-158` |
| Device and outputs | `D3D11CreateDevice(null, HARDWARE)`: default adapter only, and only its attached outputs are duplicated. The class comment assumes duplication is unavailable on basic display adapters, so such VMs get GDI. | `DesktopDuplicator.cs:14-17, 58-73` |
| Duplication path | Every changed frame gets `CopyResource` of the whole output to staging, `Map`, and a row copy into a managed buffer. Dirty and move rects are never read. | `DesktopDuplicator.cs:150-180`, `Direct3D.cs:177-178` |
| GDI path | `Thread.Sleep(timeout)`, then `CopyFromScreen` of the whole screen, a whole-buffer copy, `SequenceEqual` against the previous frame and another whole-buffer copy | `H264ScreenSession.cs:361-391` |
| Colour conversion | Scalar per-pixel BGRA→NV12 over the **whole frame**, on every core (`Parallel.For`), into a new MF buffer each frame | `PixelConvert.cs:15-45`, `H264Encoder.cs:242-258` |
| Encoder | Hardware MFT if one exists, else Microsoft's software H.264 MFT (`QualityVsSpeed` 33). It encodes the whole frame every time. Motion JPEG over GDI when Media Foundation has no H.264. | `H264Encoder.cs:72-101, 131`, `WindowsScreenSource.cs` |
| Frame rate and bitrate | Fixed by picture mode (60 fps at 20 or 8 Mbit/s, 30 fps at 2.5). No measurement of encode time and no automatic step-down. | `PictureMode.cs:48-50`, `H264ScreenSession.cs:146-237` |
| Threads | The converter uses every core; the software encoder picks its own. Nothing caps the agent to leave room for the guest's work. | `PixelConvert.cs:17-19` |
| Resolution | Whatever the guest uses; the viewer scales | — |
| Cursor | Desktop Duplication pointer shapes are ignored; the shape arrives through RFB, polled once a second while streaming | `DesktopDuplicator.cs:154`, `VideoStreamPlayer.cs:26` |

---

## 5. What to change, in order of payoff for GPU-less VMs

### 5.1 Do less work per frame

This is the largest CPU saving, and none of it needs a new codec.

* **Use dirty rectangles.** After `AcquireNextFrame`, read `GetFrameMoveRects` and
  `GetFrameDirtyRects`. Map only the staging texture's dirty regions (or `CopySubresourceRegion`
  just those), and copy and convert only those regions into a persistent BGRA and NV12 frame.
  Return "unchanged" when there are no dirty rects (pointer-only frames already are).
* **Vectorise the conversion.** `System.Runtime.Intrinsics` (`Vector256`/AVX2 with a `Vector128`
  fallback) for BGRA→NV12 over the dirty regions. Today's scalar loop runs over every pixel of
  every frame.
* **Reuse the encoder input buffer** instead of allocating a new MF buffer per frame.
* **Smarter GDI fallback.** Compare tiles (for example 64×64, with a cheap hash) instead of the
  whole buffer, and produce dirty rects from the changed tiles. Copy and convert only those.
* **Open duplication on the adapter that owns each output**, not the default adapter:
  enumerate `IDXGIFactory1` adapters and their outputs, with WARP as the last resort. Then test
  duplication on QXL, virtio-gpu and Basic Display in an Unraid VM. Jump uses Desktop
  Duplication first everywhere and drops to Windows Graphics Capture, then GDI, only on failure.
  If duplication works on those adapters, the "unavailable on basic display adapters"
  assumption in `DesktopDuplicator.cs` can go, and those VMs leave GDI.

### 5.2 Adapt to the CPU instead of falling behind

* **Measure** capture, convert and encode time per frame, and log them the way Jump does
  (`Encode Done: {}ms`, `Encoded frame: time, size, type`).
* **Step down when encoding takes most of the frame interval** (say more than 70% for a second):
  60 → 30 → 20 fps first, then scale the encode resolution by 0.75. Step back up when there is
  headroom. That is the BALANCED policy Jump picks from libwebrtc.
* **Cap encoder threads** to leave the guest room: `min(logical cores − 1, cap)`, with a setting
  for it as Jump has (`RtcMaxEncoderThreads`).
* **Keep at most one frame inside the encoder.** If the previous one has not come out, skip
  capture, as Jump's `Dropping frame - queue too large` does.

### 5.3 A software encoder made for screen content

Microsoft's software H.264 MFT is a general camera encoder. It re-codes the whole frame and has
no notion of unchanged regions. Options, smallest change first:

| Option | Why | Viewer side |
|---|---|---|
| **OpenH264** in `SCREEN_CONTENT_REAL_TIME` mode | Still H.264, so the viewer's decoder (and its hardware path) stays as is. Its screen-content mode is built for desktop sharing. BSD source; Cisco's prebuilt binary carries the patent licence. | unchanged |
| **libaom AV1** realtime, Jump's settings (3.4) | Jump's own choice. Palette mode and text-aware detection give the sharpest text per bit. Use it when `Avx2.IsSupported`, as Jump does. | AV1 decode: Media Foundation with the AV1 Video Extension (hardware on this PC's AMD GPU) or dav1d |
| **libvpx VP8**, Jump's per-frame settings (3.5) | The cheapest on mostly static desktops: the dirty-rect active map and static threshold skip unchanged blocks. Jump's choice when the CPU lacks AVX2. | VP8 decode with libvpx (software, light) |

Whichever is chosen, copy Jump's ordering and fallback: hardware encoder when present; AV1 when
the CPU has AVX2, VP8 otherwise; and **switch to the software encoder when the hardware one
fails mid-session**, rather than ending the stream. Take Jump's rate-control choices too: target
85% of the allowed bitrate, and a bitrate-dependent QP floor.

**Still-screen refinement** (comparison doc, 3.4) suits CPU-only VMs well. It spends encoder
time exactly when the screen is still, which is when the vCPUs are otherwise idle, and it can
replace the 700 ms switch to a lossless RFB refresh. Done (2026-10-20) as lossless repairs from the
captured picture, with no second encode. Measurements, including the ~15 fps composition ceiling
of a VM without a GPU and QEMU's 0.5–1.2 s VNC latency, are in
[STILL-SCREEN-REPAIR.md](STILL-SCREEN-REPAIR.md).

### 5.4 Encode fewer pixels: match the viewer's size

Encode cost scales with pixel count. A 2560×1440 guest shown in a 1600×900 window encodes 2.6
times the pixels the viewer can show.

* **First step:** when the viewer asks, switch the guest to a mode that matches the window
  (`ChangeDisplaySettingsEx`) and restore it on disconnect. [inference] QXL and virtio-gpu offer
  many modes; test what each adapter lists.
* **Later:** an IddCx virtual display like Jump's (3.2). It gives any resolution, works when the
  VM is headless, and its hardware cursor keeps the pointer out of the frames. It needs a signed
  UMDF driver (attestation signing through Microsoft's Partner Center), so it is a project of its
  own. An existing open-source IddCx driver could be evaluated first.

### 5.5 Cursor without the one-second lag

When `DXGI_OUTDUPL_FRAME_INFO.PointerShapeBufferSize` is non-zero, read
`GetFramePointerShape` and send the shape over the agent channel at once. Today shape changes
while streaming wait for the once-a-second RFB poll.

### 5.6 VMs with a passed-through GPU

* Keep frames on the GPU: duplication texture → `ID3D11VideoProcessor` (BGRA→NV12) → hardware
  MFT through `IMFDXGIDeviceManager`. No staging copy at all.
* Check that the duplicated output and the encoder are on the same adapter (compare LUIDs). If
  they are not, as with a passthrough GPU and no monitor, the frame must cross adapters. Either
  do that copy deliberately or prefer the software path for that output. A virtual display (5.4)
  removes the problem.
* Later, NVENC, AMF and oneVPL directly, as Jump does. That gives live bitrate and max-QP
  changes, per-frame QP feedback for refinement, and 4:4:4.

### 5.7 What to leave alone

* **Lock screen, sign-in, UAC:** streaming these needs Jump's model of a SYSTEM service with
  per-session helpers. On Unraid, QEMU's RFB already shows those screens and the viewer
  switches to it, so this can wait.
* **Transport:** in a VM on the same LAN, virtio-net bandwidth is plentiful. The transport
  changes in the comparison doc (6.4) still matter, but CPU efficiency comes first here.

---

## 6. Suggested order

1. Dirty-rect copy and convert, SIMD conversion, buffer reuse, encode-time logging (5.1, 5.2
   logging). Small, contained in `DesktopDuplicator.cs`, `PixelConvert.cs`, `H264Encoder.cs` and
   `H264ScreenSession.cs`, and it makes every later step measurable.
2. CPU-aware step-down and thread cap (5.2).
3. Cursor shapes from duplication (5.5).
4. A screen-content software encoder (5.3), starting with OpenH264, or with AV1 together with
   AV1 decoding in the viewer.
5. Resolution matching by display mode (5.4, first step).
6. GPU-resident path for passthrough VMs (5.6), then a virtual display driver (5.4, later).
