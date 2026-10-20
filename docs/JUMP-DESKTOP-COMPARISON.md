# Jump Desktop vs BetterVNC: why Jump's picture is better

This compares how Jump Desktop (Fluid protocol) gets the picture from the Mac mini to this PC
with how BetterVNC does it today. It then lists what BetterVNC would have to change to close
the gap, in order of payoff.

## How this was established

| Source | What it gave |
|---|---|
| Jump's own logs (`%LOCALAPPDATA%\Jump Desktop\Client\Logs\Viewer_2026_10_12..18.log`, plus the Connect service and agent logs) | What actually happened in the sessions to `mac-mini-2`: transport, codec, displays, frame rate, recovery events |
| The saved connection (`…\Client\Servers\Computer - mac-mini-2 - ….jump`) | Session settings: Fluid protocol (RTC v2), Match Display Resolution on, frame rate and bitrate automatic |
| [REA](https://github.com/morluto/rea) 6.7.0 driving Ghidra 12.1.4 headless through REA's MCP server | Decompiled functions and string cross-references in `JumpConnectCore.dll` 10.15.46.0 (Windows host), `jrtc.dll` (Jump's libwebrtc build) and `JumpClient.exe` 10.15.31.0 (viewer) |
| String inventory of the same binaries | Log formats, encoder property names and field-trial strings, used to choose what to decompile |
| BetterVNC source at `5f997f1` | Everything said about BetterVNC, with file and line |

The Mac host binary (Jump Desktop Connect for macOS) is not on this PC. What this document
says the Mac host does comes from the viewer's logs: what the Mac advertised and what it
actually sent. The Windows host shares the protocol and libwebrtc layer and shows how Jump
designs a host.

Labels: **[log]** observed in Jump's logs. **[bin]** read from the binaries, decompiled unless
it says "string". **[src]** BetterVNC source. **[inference]** reasoning not directly observed.

---

## 1. The short answer

Jump and BetterVNC are different kinds of system, and that matters more than any one
setting.

* **Jump is a tuned video call.** The Mac drives a display at this PC's exact resolution. It
  encodes it in **software AV1** at up to 60 fps. On the Windows host that is libaom with its
  screen-content tools (palette mode, text-aware content detection), and [inference] the Mac
  build is the same. libwebrtc carries it over UDP with delay-based congestion control,
  NACK retransmission and FEC, and Jump's patches let whole frames leave in one burst. This PC's
  AMD GPU decodes in hardware. The frame is shown the moment it is complete, with GPU frame
  latency 1 and no playout delay. When the screen stops moving, the host keeps re-encoding the
  still picture for as long as its quantiser keeps improving, so text sharpens within moments.
* **BetterVNC talking to the Mac is classic VNC.** Apple's `screensharingd` encodes the Mac's
  real display losslessly (ZRLE or Hextile, 32 bpp) on the CPU. BetterVNC pulls one update at a
  time over TCP and waits a full round trip plus decode before asking again. It decodes on one
  CPU thread, copies into a WPF `WriteableBitmap`, and WPF scales the result to the window.
  BetterVNC's own video stream does not exist for macOS yet.

So on the Mac, BetterVNC loses on every stage at once: resolution, capture, encoder, codec
tools, transport, decode and display.

---

## 2. What a Jump session to the Mac mini looks like [log]

From `Viewer_2026_10_18.log`, session at 08:24:25:

| Stage | What happened |
|---|---|
| Signalling | `neuron.jumpdesktop.com:443`, then ICE with host (IPv4 and IPv6), server-reflexive, TCP-passive and TURN candidates (6 TURN servers) |
| Path chosen | Direct UDP, **host ↔ host over IPv6 on the Wi-Fi LAN** (`"candidateType":"host"`, `"networkType":"wifi"` for every logged pair). TURN is never used. |
| Security | DTLS (`WebRTC-ForceDtls13`) and SRTP; SCTP data channels (`dcsctp`) |
| Time to connect | About 0.5 s from first candidate to `Connection State: Connected`; video decoder up 0.67 s later |
| Codec | The viewer offers AMD hardware decoders `amd.1 amd.2 amd.3`; the Mac offers VideoToolbox encoders `vt.2 vt.1`. Then `AMD decoder initialized for codec 5`. In Jump's numbering 3 = H.264, 4 = H.265, 5 = **AV1** (decompiled AMD decoder setup in `JumpClient.exe`, `FUN_1403ea540`). **All 23 decoder starts in the logs were AV1.** The Mac's hardware encoders on offer were VideoToolbox's (`vt.1`, `vt.2`: H.264/HEVC), so the AV1 came from a software encoder. Jump chose that over hardware HEVC. |
| Display | `Display: … (0,0,1920,1080) 60hz pri`: the Mac drives a display matching this PC's 1920×1080 screen (Match Display Resolution). Other sessions show 2560×1440 and multi-display layouts. |
| Frame rate | `Clamping max fps to: 60` |
| Tracks | `desktopvideo` (video) and `desktopaudio` (Opus 48 kHz stereo with in-band FEC and 10 ms packets [bin, string]; 5 ms WASAPI buffers [log]) |
| Control | Data channels with negotiated compression (`rtc_compress: Starting compressor: 1`) for input, clipboard, cursor and display layout |
| Recovery | `NACK list full, clearing NACK list and requesting keyframe`, `Packet buffer flushed`, `No decodable frame in … requesting keyframe`. The receiver asks again only for the packets it lost, and falls back to a keyframe when that fails. |

---

## 3. How Jump builds its pipeline [bin]

### 3.1 Transport: libwebrtc with Jump's own tuning

`jrtc.dll` is a Chromium libwebrtc build (libc++ `std::__Cr`) wrapped in a C API (`JRTC_*`
exports). Jump plugs its own pieces into it: `JumpVideoEncoderFactory`, `JumpVideoDecoderFactory`,
`JumpVideoEncoderSelector` (`OnAvailableBitrate`, `OnResolutionChange`, `OnEncoderBroken`, so the
encoder can be switched mid-session) and `JumpNetworkMonitor`.

Every peer connection is created with this field-trial table. It is a static list in
`JumpConnectCore.dll` (`FUN_180398be0`); the viewer carries the same strings.

| Field trial | Jump's value | libwebrtc default and effect |
|---|---|---|
| `WebRTC-JumpExt` (Jump's own libwebrtc patch) | `pacer_max_burst_size_bytes:10485760` | The pacer's burst limit goes from **63,000 bytes** to **10 MiB** (`jrtc.dll`, pacing controller constructor `FUN_1804b9f60`). On a LAN, a whole frame, keyframes included, leaves at once. |
| | `inactive_duration:2500ms` | Read by Jump's patched video receive stream (`FUN_18034bf30`), next to the stock 200 ms keyframe and 3 s frame waits. [inference] A host that goes quiet on a still screen is not treated as a broken stream. |
| | `turn_rtt_equivalency_duration:20ms` | `JumpExt_TurnPortEquivalencyDelayController` in TURN allocation |
| `WebRTC-Video-Pacing` | `max_delay:2ms` | Default **2 s** (`FUN_180351740`). The pacer must drain its queue within 2 ms, so video is effectively not paced. |
| `WebRTC-Pacer-KeyframeFlushing` | Enabled | A new keyframe flushes stale queued packets |
| `WebRTC-Pacer-FastRetransmissions` | Enabled | Retransmissions skip the queue |
| `WebRTC-Pacer-DrainQueue` | Enabled | Queue drained instead of held when the estimate drops |
| `WebRTC-Bwe-TrendlineEstimatorSettings` | `window_size:100` | Default 20. The delay-based estimator smooths over 100 samples and does not overreact to Wi-Fi jitter. |
| `WebRTC-Bwe-InitialProbing`, `WebRTC-Bwe-ProbingBehavior` | Enabled | Probe for capacity at start and during the session, so a LAN reaches high bitrate quickly |
| `WebRTC-ZeroPlayoutDelay` | `max_decode_queue_size:0` | Default 8 (`FUN_18048e640`). With zero playout delay, frames are released to the decoder at once, never paced. |
| `WebRTC-PacketBufferMaxSize` | `4096` | Default 2048 packets (`FUN_1804860f0`). Room for large keyframes and refinement frames. |
| `WebRTC-Video-H26xPacketBuffer` | Enabled | H.264/H.265-aware packet buffer |
| `WebRTC-SendBufferSizeBytes`, `WebRTC-ReceiveBufferSize` | `4194304`, `size_bytes:4194304` | 4 MiB UDP socket buffers, so the OS does not drop those bursts |
| `WebRTC-FlexFEC-03`, `WebRTC-FlexFEC-03-Advertised` | Enabled | Forward error correction for video |
| `WebRTC-QpParsingKillSwitch` | Enabled | Bitstream QP parsing off. [inference] libwebrtc's quality scaler cannot then shrink screen content because of high QP. |

The pattern is consistent: on a LAN, do not smooth, pace or buffer anything. Send each frame
the moment it exists, decode it the moment it is complete, and leave loss to NACK and FEC.

### 3.2 Host encoding

* **Default software codec is AV1.** Jump keeps two orders of software encoders: `jump.aom.av1`
  (codec 5) then `jump.vpx.vp8` (codec 1) when the CPU has AVX2, and VP8 first otherwise (`CPU
  doesn't support AVX2, prefer vpx as default`, `FUN_180613910`). The logs show the Mac also used
  AV1.
* **libaom is configured for screen content** (`FUN_18061c130`):

  | Control | Value |
  |---|---|
  | `AV1E_SET_TUNE_CONTENT` | screen |
  | Screen-content detection (`aa screen content`) | mode 2, anti-aliased text aware |
  | Palette mode | on (flat UI colours and text become exact palettes) |
  | IntraBC, TPL model, OBMC, warped motion, order hints | off (too slow for realtime) |
  | AQ mode | off |
  | Rate control (`aom_codec_enc_cfg`) | VBR; QP 12–63; undershoot 50%, overshoot 100%; buffer 3000 ms (initial 2100, optimal 1500); no lag frames; no frame dropping inside the encoder |
  | Profile | chosen from the input pixel format: main for 4:2:0, high for 4:4:4, professional above that |
  | Timebase | 1/90000 |
  | Threads | from a CPU-class table (`Using threads:{}, logical:{}, pcores:{}, max:{}`) |

  The generous buffer and overshoot let a scene change or a refinement frame take the bits it
  needs. The bandwidth monitor (3.4) then corrects the average.

* **Hardware encoders through their native SDKs**, not Media Foundation [bin, string]:
  * **NVENC**: textures registered with `nvEncRegisterResource`, ARGB input when encoding 4:4:4,
    `Reconfiguring encoder: br:{} fr:{} mqp:{}` (bitrate, frame rate and max QP changed live).
  * **AMD AMF**: DX11 surfaces in. Sets `Usage`, `QualityPreset`, `RateControlMethod`,
    `TargetBitrate`, `PeakBitrate`, `VBVBufferSize`, `MinQP`, `IDRPeriod`, `BPicturesPattern`,
    `EnforceHRD`, `LowLatencyInternal`, `HevcEnableVBAQ` and `Av1EncodingLatencyMode`, and reads
    back average QP per frame through statistics feedback.
  * **Intel oneVPL**: `ImportFrameSurface … shared (zero-copy)`; reports 4:4:4 support.
  * **libvpx VP8/VP9** with an **active map built from dirty rectangles** (`Dirty Rect: {} ->
    activemap: {}`), so unchanged blocks are not encoded at all.
* **The encoder runs in its own process** behind a bounded queue (`renc_server`/`renc_client`).
  `Dropping frame - queue too large` drops a frame before it can turn into latency.

### 3.3 Capture (Windows host) [bin, string]

DXGI Desktop Duplication through `DuplicateOutput1`/`IDXGIOutput5`, with Windows Graphics
Capture and GDI as fallbacks. It reads `GetFrameDirtyRects` and `GetFrameMovedRects`, and logs
`AccumulatedFrames`, `RectsCoalesced` and `ProtectedContentMaskedOut`. Pixel conversion and
rotation run in GPU shaders (`UAV+SHARED textures`, render-target fallback). HDR monitors are
handled (`SDR white level for monitor`). The frame stays on the GPU from capture to encoder.

### 3.4 Three control loops on the host

**Frame governor** (`jump::FrameRateGovernor::OnTick`, `FUN_18044ba60`):

* A newly captured frame is held only until the frame interval has passed since the last send,
  then sent (`send-tick`).
* If nothing new arrived but refinement is active (below), the last frame is sent again on its
  own timer (`send-dupe`).
* The governor keeps ticking only while refinement is active. A still screen with nothing to
  improve costs no frames.

**Still-screen refinement** (`rtc_venc_quality`, `FUN_180350a10` and `FUN_1803508f0`), run after
every encoded frame with its QP:

1. Keep the QPs of the last three frames and take the best (lowest).
2. If the new frame's QP beats that by more than 5% of the QP range (at least 1), log `QP
   Improved` and push a deadline **2 s** into the future (`Touch dupe frame`).
3. While the current QP is above a target that depends on the current bitrate (a table of
   bitrate→QP pairs), and the deadline has not passed, keep refinement on, so the governor
   re-sends the unchanged frame.
4. Otherwise turn it off and reset.

On a still screen the encoder therefore receives the same picture again and again. Each pass
spends bits on what the previous one left blurry. It stops once the picture reaches the quality
target for the link, or stops improving for 2 s.

**Bandwidth monitor** (`bw_monitor`, `FUN_1803585f0`): measures the encoder's actual output
over about a second and compares it with the target from congestion control.

* Ratio above 1.05 for at least a second (the encoder overshoots, as hardware encoders do on
  sudden screen changes): set the encoder's setpoint to target² / actual, at least 10% of the
  target (`Throttling: {}bps -> {}bps (actual, ratio)`). Escalate up to three drop levels.
* 0.95–1.05: hold.
* Below 0.95 for a second: step back down a drop level, or restore the target (`Recovery
  bitrate`, `Target increased … resetting`).

The bits on the wire therefore match the network estimate, rather than what the encoder was
asked for.

### 3.5 Viewer decode and display

* Hardware decoders by vendor SDK: AMD AMF (H.264, HEVC 8/10-bit, AV1), NVIDIA NVDEC through CUDA
  with D3D interop, Intel oneVPL, and dav1d as the software AV1 fallback [bin, string]. On this
  PC: `Found 3 AMD hardware decoders` [log].
* The AMD decoder is opened with `ReorderMode = 2` (low latency), `LowLatencyDecode = true` and
  `SurfacePoolSize = 32` (`FUN_1403e9220`). Output stays a D3D11 texture.
* The D3D11 device is multithread-protected and gets `IDXGIDevice::SetGPUThreadPriority(7)`
  (the maximum) and `IDXGIDevice1::SetMaximumFrameLatency(1)` (`FUN_140566bc0`).
* No playout delay (3.1), and a built-in frame tracer (`encode_input`, `decode_output`,
  `compositor_output`) for measuring glass-to-glass latency [bin, string].

### 3.6 Cursor, input, displays [bin, string]

* The cursor travels apart from the video. Shapes are sent once with id, hotspot and scale, then
  cached (`Requesting pointer with id`, `Pointer updated id:{}, hotspot:{}, image:{}`). The
  viewer draws it locally, so pointer movement never waits for a video frame.
* Input, clipboard, cursor and display layout are protobuf messages on data channels.
* **Virtual displays**: the host creates displays that match the viewer's screens, so the
  picture is encoded and shown 1:1 with no scaling at either end.

---

## 4. What BetterVNC does today [src]

### 4.1 Connecting to the Mac (RFB to Apple's server)

| Stage | BetterVNC | Where |
|---|---|---|
| Server encoder | Apple's `screensharingd`, lossless only for non-Apple clients (Raw, Hextile, Zlib, ZRLE). Lossy modes are disabled for Apple. | `SessionWindow.xaml.cs:291-294` |
| Pixel format | Always 32 bpp BGRX | `PixelFormat.cs:20`, `VncClient.cs:108` |
| Resolution | The Mac's real display resolution. No `SetDesktopSize`, no virtual display; WPF scales (`Stretch.Uniform`, HighQuality) | `RfbConstants.cs:73-84`, `VncView.cs:57,78-85` |
| Request model | One `FramebufferUpdateRequest` outstanding. The next is sent only after the whole update is read and decoded, so each update costs RTT + server encode + transfer + client decode. `ContinuousUpdates` and `Fence` are defined but never advertised. | `VncClient.cs:348-354, 366-386, 677-679` |
| Transport | TCP, `NoDelay`, 1 MB receive buffer | `VncClient.cs:88` |
| Decode | One "VNC receive" thread reads the socket and runs every decoder. Zlib is SharpZipLib's managed inflater, which allocates per ZRLE rectangle. | `VncClient.cs:112`, `ZlibInflater.cs`, `ZrleDecoder.cs:19` |
| Lock contention | The receive thread holds `fb.SyncRoot` for the whole update, socket reads included. The UI thread's `Render` takes the same lock, so it can block until a large update has fully arrived. | `VncClient.cs:563-663`, `VncView.cs:351` |
| Display | Dirty rectangles copied row by row into a `WriteableBitmap` on the UI thread; WPF then uploads and scales | `VncView.cs:333-374` |
| Video stream | Not available: the Mac agent has no video source | `AgentHost.cs:219-226` |

### 4.2 Agent H.264 stream (Windows guests only)

| Stage | BetterVNC | Where |
|---|---|---|
| Capture | Desktop Duplication, but every changed frame gets `CopyResource` to staging, `Map` (a CPU/GPU sync stall) and a full-frame memcpy, even for a blinking caret. Dirty and move rects are declared but never read. | `DesktopDuplicator.cs:150-180`, `Direct3D.cs:177-178` |
| Colour | BGRA→NV12 on the CPU (BT.601), into a new system-memory buffer each frame | `PixelConvert.cs:15-45`, `H264Encoder.cs:242-258` |
| Encoder | Media Foundation MFT, hardware first. No D3D manager, so input comes from system memory. CBR at a fixed bitrate (20 / 8 / 2.5 Mbit/s by mode), infinite GOP, no B-frames, low-latency mode. No VBV or max-bitrate cap, no QP limits, no intra refresh. | `H264Encoder.cs:110-135`, `PictureMode.cs:48-50` |
| Rate adaptation | None. The bitrate changes only when the user changes mode. | `VideoStreamPlayer.cs:97,176` |
| Pacing | A change captured mid-interval waits for the interval boundary. Async encoder output is polled from the capture loop, which [inference] adds about one frame interval. | `H264ScreenSession.cs:146-237` |
| Still screens | Not re-encoded. After 700 ms the viewer switches to a lossless RFB refresh, which can show a stale frame first. | `VideoStreamPlayer.cs:24, 287-296` |
| Transport | One TCP socket for video, clipboard and files (up to 256 MB) behind one write lock, so a file transfer blocks video. On overflow it drops from a 3-frame queue and forces an IDR at the same bitrate. No NACK, FEC or congestion control. | `AgentServer.cs:315-321, 454-463`, `AgentFraming.cs:42,65-75` |
| Decode | Sync MFT without a D3D manager: software decode to system-memory NV12 | `H264Decoder.cs:52-84` |
| Display | CPU NV12→BGRX, then `WritePixels` of the whole frame on the UI thread, then WPF upload | `PixelConvert.cs:60-85`, `VncView.cs:128-175` |

---

## 5. Side by side

| | Jump (to the Mac) | BetterVNC (to the Mac) | BetterVNC agent stream (Windows guests) |
|---|---|---|---|
| Resolution | Display = viewer's screen | Mac's real display, scaled in WPF | Guest's display, scaled in WPF |
| Encoder | Software AV1, tuned for screen content | Apple RFB, lossless ZRLE, CPU | MF H.264, hardware if present, CBR |
| Frames in flight | Pushed as captured, up to 60 fps | One request at a time | Pushed, 3-frame queue |
| Transport | UDP; delay-based congestion control, NACK, FEC; burst send | TCP | TCP, shared with clipboard and files |
| Rate adaptation | Continuous: BWE, encoder `SetRates`, overshoot compensation | None (lossless) | None |
| Loss | Retransmit only the lost packets; keyframe as last resort | TCP stalls | TCP stalls; drop, then IDR |
| Still screen | Re-encode until QP reaches the link's target | Lossless, but slow to arrive | Switch to RFB after 700 ms |
| Decode | Hardware, low-latency mode, stays on GPU | CPU, one thread | Software MF, CPU |
| Present | D3D11, frame latency 1, top GPU priority, no playout delay | WriteableBitmap + WPF | WritePixels + WPF |
| Cursor | Local, shapes cached by id | Local (RFB cursor) | Local; shape via RFB, polled once a second |

---

## 6. What to change, in order of payoff

### 6.1 Give the Mac agent a video source (largest win for the Mac)

Everything downstream already exists for Windows guests: the agent protocol, `VideoStreamPlayer`
and the decoder. The Mac needs the capture and encode end.

* Capture with **ScreenCaptureKit** (`SCStream`), with output at the **viewer's window size**
  (`SCStreamConfiguration.width/height`). The Mac scales on its GPU and the viewer shows the
  picture 1:1. A virtual display (`CGVirtualDisplay`, which Jump and BetterDisplay use) can come
  later for true native resolution.
* Start with **VideoToolbox HEVC** (H.264 as fallback): hardware, simple, and decodable by this
  PC's AMD GPU. Use `kVTCompressionPropertyKey_RealTime`, no frame reordering, `DataRateLimits`
  for a VBV cap and `ExpectedFrameRate`.
* Jump's own choice shows where the ceiling is: on this link it ran **software AV1 with
  screen-content tools** rather than VideoToolbox. Palette mode and text-aware detection keep
  text and flat UI crisp at bitrates where H.264/HEVC smear them. After VideoToolbox works, a
  libaom (or SVT-AV1) realtime encoder with `tune-content=screen` and palette on is the next
  step. Windows decodes AV1 with the AV1 Video Extension (hardware on this GPU) or dav1d.
* The simplest bridge from .NET is a small Swift helper process that writes access units to the
  agent over a pipe; the agent forwards them unchanged.

### 6.2 Decode in hardware and present from the GPU (viewer)

* Give the decoder MFT a D3D11 device manager (`MFT_MESSAGE_SET_D3D_MANAGER`) so it uses DXVA,
  and enable its low-latency mode (already set). Add HEVC and AV1 decoders.
* Keep the output as a D3D11 texture. Present it through a flip-model swap chain (`HwndHost`,
  or `D3DImage` with D3D11 interop) with `SetMaximumFrameLatency(1)`. Do NV12→RGB and scaling
  in a shader. This removes the CPU conversion, `WritePixels` and WPF's upload.
* Present the newest decoded frame at once, with no queue in front of the swap chain.

### 6.3 Sharpen still screens inside the stream

Replace the 700 ms fall-back to a lossless RFB refresh with Jump's approach. When the capture
source reports no change, re-send the last frame to the encoder at the frame interval. Read
each frame's QP (or size). Keep going while QP keeps falling and is above a target for the
current bitrate, and stop after about 2 s without improvement. Text becomes crisp with no
stale-frame flash and no second protocol. With Media Foundation, the same effect comes from
re-encoding the unchanged frame at a lower `CODECAPI_AVEncVideoEncodeQP` (or a raised bitrate)
for a few frames.

### 6.4 Make the video channel latency-controlled

On a LAN, most of Jump's transport advantage comes from never letting queues build up. A first
step that keeps TCP:

* Put video on its **own connection**, so clipboard and file transfers never sit in front of a
  frame.
* Have the viewer **acknowledge each frame** (sequence number and receive time). The agent then
  knows its frames in flight and the delivery rate. Allow one or two frames in flight; when
  more are outstanding, skip capture instead of queuing.
* **Adapt the bitrate** from delivery rate and frame RTT: cut at once when frames back up,
  raise slowly when they flow. Add Jump's overshoot compensation: measure actual encoder output
  over a second, and scale the setpoint by target/actual when it overshoots by more than 5%.
  Move from plain CBR to low-delay VBR with a VBV of about one frame and a max-QP cap.
* Recover with **intra refresh** instead of full IDR frames where the encoder supports it.

The complete answer is UDP with per-packet loss recovery: WebRTC itself, or RTP with NACK and a
delay-based controller. That is a bigger project, worth it mainly on Wi-Fi and WAN. SIPSorcery
is the .NET-only route; libwebrtc through a native wrapper is what Jump does.

### 6.5 Windows agent capture without CPU copies

For Windows guests in VMs without a GPU or hardware encoder, see
[WINDOWS-HOST-IN-VMS.md](WINDOWS-HOST-IN-VMS.md).

* Read `GetFrameDirtyRects`/`GetFrameMoveRects`. Skip frames where nothing (or only the cursor)
  changed, and use the rects for encoder ROI or to drive refinement.
* Convert BGRA→NV12 on the GPU (`ID3D11VideoProcessor`, or the MF video processor). Feed the
  encoder D3D11 textures through `IMFDXGIDeviceManager`: no staging copy, no `Map`.
* Drive the async encoder from its own events (`METransformHaveOutput`) and send output as soon
  as it exists.

### 6.6 RFB path (Unraid and the fallback)

* Send the next `FramebufferUpdateRequest` as soon as an update's header arrives, not after
  decoding. That pipelines one request and hides one round trip per update. Advertise
  `ContinuousUpdates` and `Fence` for servers that support them (TigerVNC, newer QEMU).
* Read each update into a buffer first and take `fb.SyncRoot` only to apply it, so the UI
  thread never waits on the network.
* Use `System.IO.Compression.ZLibStream` (native zlib) instead of SharpZipLib, and reuse buffers.
* Send `SetDesktopSize` to servers that support `ExtendedDesktopSize` resizing.

---

## 7. Other observations

* Jump also embeds an older libwebrtc for its RTC v1 protocol (logs tagged `[wrtc]`), alongside
  the newer `jrtc.dll` (`[wrtc2]`).
* Jump's logs on this PC show audio device churn (`Did not get audio pull callback in sometime`),
  which is unrelated to the picture.
* Reproducing the analysis: install REA's published package (it ships the Windows native addon
  that a source checkout lacks), set `GHIDRA_INSTALL_DIR`, `JAVA_HOME` (JDK 21+) and
  `REA_ANALYSIS_PROVIDER=ghidra`, and raise `REA_GHIDRA_STARTUP_TIMEOUT_MS`. The first analysis
  of `JumpConnectCore.dll` and `JumpClient.exe` took about 11 minutes each.
