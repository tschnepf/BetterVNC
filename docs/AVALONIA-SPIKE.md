# Avalonia spike: Phase 0 results

Phase 0 of [CROSS-PLATFORM-VIEWER.md](CROSS-PLATFORM-VIEWER.md): four questions that decide how the viewer is ported to
Avalonia, answered on Windows with measurements before any real port starts.

- **Spike app:** [tools/BetterVnc.AvaloniaSpike](../tools/BetterVnc.AvaloniaSpike), on Avalonia 12.1.4 (ANGLE over
  Direct3D 11, OpenGL ES 3.0).
- **WPF baseline:** [tools/BetterVnc.AvaloniaSpike.Wpf](../tools/BetterVnc.AvaloniaSpike.Wpf).
- **Machine:** the dev laptop, an AMD Radeon 890M with one display at 2256×1504, 60 Hz, 150% scaling. No external
  monitor was attached, so 4K pictures were measured scaled into a 1280×720 window. The upload and composition work is
  the same as at 1:1.
- **When:** 2026-10-19 and 2026-10-20, BetterVNC at `5d70dc2`.

---

## 1. The short answer

| Question | Answer |
|---|---|
| 1. Framebuffer | **Not with `WriteableBitmap`:** Avalonia re-uploads the whole bitmap on every change, so a 4K framebuffer gets about 29 of 60 frames a second to the screen, even when only a small strip changed. An OpenGL texture with dirty-rectangle uploads gets every frame to the screen at every size. The best path is the existing D3D11 swap chain in a child window: every frame at 4K, a steady 16–18 ms, close to WPF's 7–16 ms. |
| 2. GPU video | **Keep today's `VideoRenderer` swap chain, hosted in a `NativeControlHost`.** It runs unchanged and shows every frame in 18 ms median. Importing a shared texture into Avalonia's compositor works too (once `TopLeftOrigin` is set), but takes 32–41 ms. The child window has the same airspace and scroll-clipping problems as WPF's `HwndHost`, with the same fix: size it to the viewport. |
| 3. Keyboard | **Yes on both counts.** `Win32Properties.AddWndProcHookCallback` sees every key message with its scancode before Avalonia, and `handled = true` keeps it from Avalonia and from menu mode. `PhysicalKey` covers every key tested, with no two scancodes sharing one; only Ctrl+Break and Alt+SysRq map to `None`. |
| 4. UI Automation | **Yes.** Avalonia windows and controls are in the UIA tree with the usual patterns (Value, Invoke, Toggle, ExpandCollapse, Selection), and they work without focus, even with the PC locked. Custom controls need an automation peer. |

**Design consequence:** on each OS, the remote picture is one native surface, used for both the RFB framebuffer and
the stream. Avalonia draws everything around it. Section 8 has the details.

---

## 2. How it was measured

- **Frame number on screen.** Every test picture carries its frame number as a row of 18 black and white blocks
  (`Shared/Marker.cs`).
- **Screen probe.** A thread duplicates the display with DXGI Desktop Duplication and reads those blocks off each new
  desktop frame (`Shared/ScreenProbe.cs`). It takes the time from `LastPresentTime`, the QPC time DWM presented that
  desktop image.
- **Latency** is the time from when a frame is handed to the presentation path (`Run.Submit`) to when it first shows on
  screen. It includes DWM's own frame, which is the same for every path, so the numbers compare paths but are not
  absolute input-to-photon times.
- **Frames on screen** counts distinct frame numbers the probe saw, per second. The producer submits about 57 a second
  (60 Hz pacing with a 1 ms sleep), so "every frame" here means about 57/s.
- **Run-to-run spread.** The producer's clock drifts against the display's 60 Hz refresh, so a run's median latency
  can land a whole display frame (about 16 ms) higher or lower than the last run of the same configuration. The key
  comparisons were run three times each, and the ranges are given.
- **CPU** is process CPU time as a percentage of one core, plus a per-thread breakdown: the UI thread, the test's own
  named threads (`producer`, `decode`, `probe`), and unnamed threads, which are Avalonia's, Media Foundation's or the
  driver's.
- **Test windows** are borderless, topmost and never activated. The spike injects no keyboard or mouse input.

---

## 3. Question 1: is drawing the framebuffer fast enough?

A producer thread changes a synthetic framebuffer 60 times a second
([SyntheticFramebuffer.cs](../tools/BetterVnc.AvaloniaSpike/Shared/SyntheticFramebuffer.cs)). Either a strip changes
(the frame number, a clock's or a blinking cursor's worth of pixels: 864×48 at 1080p, 1728×96 at 4K), or the whole
picture scrolls. Five ways to draw it:

| Way | What it does |
|---|---|
| **WPF** (today) | `WriteableBitmap`, dirty rectangles copied with `AddDirtyRect`, as `VncView.Render` does |
| **Avalonia bitmap** | `Image` + `WriteableBitmap`, dirty rectangles copied under `Lock()`, then `InvalidateVisual()` |
| **Avalonia OpenGL** | `OpenGlControlBase`; a texture created once, then each dirty rectangle uploaded with `glTexSubImage2D` (`GL_UNPACK_ROW_LENGTH`); rendered on the UI thread, then composed by Avalonia |
| **Avalonia OpenGL, tuned** | the same with `Win32PlatformOptions` `CompositionMode = DirectComposition` and `ShouldRenderOnUIThread = true` |
| **Swap chain** | today's `VideoRenderer` in a `NativeControlHost` child window, the whole picture uploaded on every change (worst case: `VideoRenderer` has no dirty-rectangle upload yet) |

**Frames on screen per second, with the median latency** (picture fitted into a 1280×720 window; a range means
several runs):

| Framebuffer | Changes | WPF | Avalonia bitmap | Avalonia OpenGL | OpenGL tuned | Swap chain |
|---|---|---|---|---|---|---|
| 1920×1080 | small | 57, 12 ms | 53–57, 23–28 ms | 57, 32 ms | 57, 19–31 ms ¹ | 57, 18 ms |
| 1920×1080 | full | 45, 8 ms | 31–57, 26–32 ms | 57, 31 ms | 57, 17 ms | — |
| 3840×2160 | small | 56–57, 7–16 ms | **29**, 36–45 ms | 55–57, 33 ms | 57, 16–30 ms | 56–57, 16 ms |
| 3840×2160 | full | 55, 19 ms | **27–29**, 36–46 ms | 57, 31 ms | 57, 23 ms | 57, 16 ms |

¹ A third run of this configuration got only 36 frames a second on screen, while two repeats straight after got 57.
That run is left out of the range, but the configuration should be watched in Phase 2.

**CPU, process total, at 4K:**

| Changes | WPF | Avalonia bitmap | Avalonia OpenGL | Swap chain |
|---|---|---|---|---|
| small | 14% | 34–39% | 12–23% | 33–35% |
| full | 86% | 52–56% | 41–50% | 36–37% |

The swap chain's figure includes a full-frame upload every frame, which dirty-rectangle uploads would cut.

**What it shows:**

- **`WriteableBitmap` is out for large framebuffers.** At 4K, about half the frames never reach the screen, whatever
  changed: Avalonia's Skia backend re-uploads the whole 33 MB bitmap on every change. It's fine at 1080p, but the
  viewer has to handle 4K remote desktops and multi-monitor Macs.
- **OpenGL with dirty rectangles fixes the throughput.** Every frame reaches the screen at every size, and the UI
  thread spends 0.2 ms per frame on a small change and 1.2–1.7 ms on a full 4K frame.
- **Everything Avalonia composes is about one display frame later than WPF.** Avalonia's default (`WinUIComposition`)
  gave 31–33 ms on every run. DirectComposition alone gave 25–27 ms. DirectComposition plus rendering on the UI thread
  gave 16–31 ms depending on the run. `LowLatencyDxgiSwapChain` (35 ms) and `RedirectionSurface` (46 ms) were worse.
- **The child-window swap chain bypasses Avalonia's compositor.** It gave 16–18 ms on every run, with every frame at
  4K, even with a full upload per frame. `Present` blocks about one frame waiting for the refresh, so it belongs on its
  own thread, as the decode thread already is for the stream.

---

## 4. Question 2: which way should the H.264 stream reach the screen?

A five-second 1280×720 stream with the frame number drawn in is encoded once with the agent's encoder
(AMDh264Encoder, 8.2 Mbit/s). It is then decoded at 60 fps the way `VideoStreamPlayer` does, and each picture goes one
of four ways:

| Path | Decode | To the screen |
|---|---|---|
| `bitmap` | software (Media Foundation) | CPU NV12→BGRX, Avalonia `WriteableBitmap`: today's "→ WPF" fallback |
| `sw-swapchain` | software | NV12 upload + shader, today's `VideoRenderer` swap chain in a `NativeControlHost` child window |
| `dxva-swapchain` | DXVA (GPU) | GPU copy + shader, the same swap chain: today's main path |
| `composition` | software | CPU NV12→BGRX, upload into one of three shared D3D11 textures (keyed mutex), `CompositionDrawingSurface.UpdateWithKeyedMutexAsync` |

| Path | Frames on screen | Median latency (p95) | Process CPU |
|---|---|---|---|
| `bitmap` | about 78% (233 of the 300-frame loop in 6 s) | 17 ms (32 ms) | 102–112% |
| `sw-swapchain` | every frame | 18 ms (19–21 ms), 6 runs | 49–64% |
| `dxva-swapchain` | every frame | 18 ms (19 ms) | **9–16%** |
| `composition`, default | 98% | 32 ms (49 ms) | 107–119% |
| `composition`, DirectComposition + UI-thread rendering | 97–98% | 20–41 ms (37–49 ms), 4 runs | 81% (1 run) |

**What it shows:**

- **Today's main path runs unchanged under Avalonia** (DXVA decode, then `VideoRenderer`'s swap chain in a child
  window): every frame, 18 ms, 9–16% of a core. GPU thread priority 7 is granted as in WPF.
- **The composition import works,** with three conditions:
  - The compositor imports `D3D11TextureGlobalSharedHandle` and `D3D11TextureNtHandle` on the same AMD adapter, and
    the keyed-mutex hand-off never stalled (0 frames skipped).
  - **`PlatformGraphicsExternalImageProperties.TopLeftOrigin` must be `true`.** Without it the picture shows upside
    down; the first run read 3 of 362 frames because the frame number sat at the bottom.
  - It costs a display frame or more of latency, and it jitters more.

  Its CPU figure here is inflated by the CPU NV12→BGRX conversion; a real version would convert on the GPU into the
  shared texture. Its advantage is that the video is an ordinary part of Avalonia's visual tree, so there's no
  airspace problem (by construction; not separately measured).
- **`bitmap` drops about a fifth of the frames,** and costs the most CPU. It stays useful only as a last-resort
  fallback.

### Child-window placement (`host` mode)

| Check | Result |
|---|---|
| Child window covers the control when it fills the window | ok (960,696 1280×720 = control) |
| Control hidden → child hidden | ok |
| 40 window resizes while frames keep coming | ok: renderer usable, child back at 1280×720 |
| Avalonia element drawn over the video | **hidden by the video:** the screen shows the video's red where the green overlay is (airspace) |
| 1:1 picture inside a smaller `ScrollViewer`, scrolled | **WRONG:** the child keeps its full 1280×720 at the scrolled position, with no window region, and the video's red shows in the margin outside the viewport |

`NativeControlHost` behaves like WPF's `HwndHost`. It places the native window over the control's whole bounding box,
ignores clipping by a scroll viewport, and draws over Avalonia content. Today's `VideoSurfaceHost` handles this with
manual placement and the `VideoSurfaceBlocked` rule. For the port:

- **Size the host to the viewport.** Keep the `NativeControlHost` the size of the visible picture area, never larger,
  and pass the scroll offset as the destination rectangle in `VideoRenderer.SetLayout`. The renderer already crops a
  destination that reaches outside its window, and `VideoLayout.Place` already computes this rectangle with a clip.
- **Keep overlays clear of the surface.** The connecting/disconnected overlay keeps the `VideoSurfaceBlocked` rule, so
  the surface steps aside while it shows. The fullscreen toolbar that slides over the picture has to be a popup (its
  own window) or shrink the surface. Avalonia's menus, tooltips and ComboBox drop-downs are already popups, which on
  Windows are separate windows (`OverlayPopups = false`), so they show above the child window. **[check]** in Phase 2.

---

## 5. Question 3: keyboard

### A. Mapping: Windows scancode → `PhysicalKey` → XT

This uses Avalonia's own Win32 mapping, `KeyInterop.PhysicalKeyFromVirtualKey`, run over every scancode, with and
without E0, on the US layout:

- **152 scancodes** have a virtual key, and they give **140 distinct `PhysicalKey`s**.
- **No two scancodes share a `PhysicalKey`**, so a `PhysicalKey` → XT table loses nothing.
- **12 map to `None`.** They are rare OEM codes 0x5A–0x5F, 0x62, 0x63, 0x6F and 0x7C, plus two combinations that
  Windows reports under their own scancodes: **Ctrl+Break** (E046) and **Alt+Print Screen / SysRq** (0x54).
- **Every special key** tested in the form `WM_KEYDOWN` delivers it maps to the right `PhysicalKey`:
  - Pause, NumLock, Print Screen, AltGr's two halves, Numpad Enter and /, the numpad with NumLock off, the Win and Menu
    keys
  - Japanese: Hankaku, Kana, Henkan, Muhenkan, Yen, Ro
  - Korean: Hangul and Hanja, both encodings
  - ISO `\`, Brazilian ABNT C1 and C2, the media keys, F13 and F24
- **`PhysicalKey` has 165 members.** None of the W3C codes a VNC keyboard needs is missing (IntlBackslash, IntlRo,
  IntlYen, KanaMode, Convert, NonConvert, Lang1, Lang2, NumPadComma and so on).

**Consequences for the port:**

- **On Windows, keep sending the raw scancode from the window message**, as today. It's identical to what a
  `PhysicalKey` table would give, and it also covers Ctrl+Break and SysRq, which `PhysicalKey` can't express.
- **On macOS and Linux, use a fixed table from `PhysicalKey` (W3C code) to XT.** Don't derive it from
  `MapVirtualKeyEx`: that depends on the current layout, and on the US layout it doesn't produce the Japanese and
  Korean keys at all. For Ctrl+Break and SysRq there, send Pause and Print Screen with the modifiers held.

### B. Window messages: `Win32Properties.AddWndProcHookCallback`

Messages were posted to the test window; the PC's real keyboard was left alone.

- The hook sees `WM_KEYDOWN`, `WM_SYSKEYDOWN`, `WM_KEYUP`, `WM_SYSKEYUP`, `WM_CHAR` and `WM_SYSCHAR` with the scancode
  and E0 flag, before Avalonia handles them. That's the same position `SessionWindow`'s `HwndSource` hook has today.
- **With `handled = true`,** Avalonia raises no KeyDown or KeyUp, and F10 doesn't start menu mode: no `WM_SYSCOMMAND`.
- **With `handled = false`,** Avalonia raises KeyDown/KeyUp with the right `PhysicalKey`, and releasing F10 sends
  `WM_SYSCOMMAND SC_KEYMENU`. That's what the hook has to prevent, as today.
- **AltGr** arrives as LCtrl (`ControlLeft`) followed by RAlt (E0, `AltRight`), the same as in WPF.
- Avalonia delivered key events to the focused element even though the window was never activated.

**Not covered:** real typing, because injecting `SendInput` keystrokes would have needed the window to take focus from
the session in use. The low-level keyboard hook for Win/Alt+Tab is a global Windows hook, independent of the UI
framework, so it carries over as is.

---

## 6. Question 4: UI Automation

A PowerShell UIA script ([uia-check.ps1](../tools/BetterVnc.AvaloniaSpike/uia-check.ps1)) drove the `ui` window with
UIA patterns only: no mouse, no keyboard, no focus change. It worked even with the PC locked.

| Control | UIA control type and patterns | Action | The app saw it |
|---|---|---|---|
| Window | Name "BetterVNC UIA test", FrameworkId "Avalonia" | found by process id | — |
| `TextBox` (AutomationId `HostBox`) | Edit; Value | SetValue | yes |
| `Button` (`ConnectButton`) | Button; Invoke | Invoke | yes |
| `CheckBox` (`FullscreenBox`) | CheckBox; Toggle | Toggle → On | yes |
| `ComboBox` (`PictureBox`) | ComboBox; Selection, Value, ExpandCollapse | Expand, then Select "Stream" | yes |
| `ListBox` (`ConnectionList`) | List; Selection | Select "Mac mini" | yes |
| `Border` with AutomationId and Name (`RemoteScreen`) | **not in the UIA tree** | — | — |

The checklist from the WPF viewer carries over. One change: a custom control like the future Avalonia `VncView` must
override `OnCreateAutomationPeer`, or UIA can't find it. Plain `Border`s and decorators get no automation peer.

---

## 7. Side finding: NumLock and Pause on QEMU

Today's `KeyboardTranslator` ([KeyboardTranslator.cs:63](../src/BetterVnc.App/Input/KeyboardTranslator.cs#L63)) sends
`(extended ? 0xE000 : 0) | scancode`, and `VncClient` turns E0 codes into `0x80 | code` for the QEMU extended key event.

Windows reports **NumLock as scancode 0x45 with the E0 flag** and **Pause as 0x45 without it**; Avalonia's mapping
above confirms both. So:

- NumLock goes out as 0xC5.
- Pause goes out as 0x45, which is NumLock's XT code. QEMU's code for Pause is 0xC6 (E0 46).

On a QEMU guest, Pause most likely acts as NumLock, and NumLock as something else. This is not verified on a VM yet.
It's a small fix in the translator, separate from the port.

---

## 8. Recommendations for the port

These replace the "decided by Phase 0" points in [CROSS-PLATFORM-VIEWER.md](CROSS-PLATFORM-VIEWER.md).

1. **One native surface per OS for the whole remote picture.** Both the RFB framebuffer and the agent stream go
   through it; Avalonia draws the windows, toolbar and dialogs around it.
   - **Windows:** today's `VideoRenderer` in a `NativeControlHost`. Add a dirty-rectangle upload (`UpdateSubresource`
     with a box) for the framebuffer, and present from a thread of its own, since `Present` blocks about one frame.
   - **macOS:** the CAMetalLayer view planned for Phase 3.
   - **Linux:** an OpenGL or Vulkan surface.
2. **Fallback where the native surface is unavailable:** `OpenGlControlBase` with dirty-rectangle uploads, with
   `CompositionMode = DirectComposition` and `ShouldRenderOnUIThread = true` on Windows. `WriteableBitmap` only as a
   last resort.
3. **Placement rule:** size the native host to the visible picture area, and put the scroll offset and fit scaling in
   the renderer's destination rectangle, as `VideoLayout.Place` already computes. In-window overlays use the
   `VideoSurfaceBlocked` rule; anything that must float over the picture is a popup.
4. **Don't pursue composition import for now.** It works, but costs at least a display frame of latency. Revisit it
   if a later Avalonia release removes the extra frame, or if airspace becomes the bigger problem.
5. **Keyboard:** on Windows, the WndProc hook with raw scancodes and `ToUnicodeEx` keysyms, as today, plus
   `LowLevelKeyboardHook` unchanged. On macOS and Linux, a fixed `PhysicalKey` → XT table, with special cases for
   Ctrl+Break and SysRq.
6. **UI tests:** keep the UIA approach, and give `VncView` an automation peer.

---

## 9. Rerunning

```bash
dotnet build tools/BetterVnc.AvaloniaSpike -c Release
dotnet build tools/BetterVnc.AvaloniaSpike.Wpf -c Release
sh tools/BetterVnc.AvaloniaSpike/run-screen-suite.sh /path/to/results.txt
```

**Modes** (`dotnet tools/BetterVnc.AvaloniaSpike/bin/Release/net10.0-windows/BetterVnc.AvaloniaSpike.dll <mode>`):

| Mode | Options |
|---|---|
| `fb` | `--size WxH`, `--dirty small\|full`, `--draw bitmap\|gl\|swapchain` |
| `video` | `--path bitmap\|sw-swapchain\|dxva-swapchain\|composition` |
| `host` | none |
| `keys` | none |
| `ui` | none; run `uia-check.ps1 -Dll <spike dll> -Out <file>` to drive it |

- **Avalonia's composition settings:** add `--composition winui|dcomp|lowlatency|redirection` and `--ui-render` to any
  mode.
- **WPF baseline:** takes the `fb` options `--size`, `--dirty` and `--seconds`.
- **Screen:** the on-screen numbers need an unlocked screen with no Click to Do overlay. With the PC locked, Desktop
  Duplication is refused, and the tests run without on-screen numbers.
- **Smart App Control:** both projects set `<Deterministic>false</Deterministic>`, because Smart App Control on the dev
  PC blocks some build hashes. If `dotnet <dll>` is refused, rebuild; it took up to six rebuilds here.
- **Not in the solution:** neither project is in `BetterVNC.slnx`, so CI doesn't build them.
