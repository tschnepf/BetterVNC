# Cross-platform viewer: Avalonia and a video backend per OS

The viewer (`src/BetterVnc.App`) runs only on Windows. That is not because of C#: the VNC protocol library, the agent
protocol and the agent itself already run on Windows, macOS and Linux. The viewer is tied to Windows by WPF and by the
Windows APIs it uses for video, keyboard, cursor, clipboard, audio and secrets. This plan ports the UI to
[Avalonia](https://avaloniaui.net/) and puts each OS-specific piece behind an interface with one implementation per OS.
It stays on C# and .NET 10 throughout.

What this says about BetterVNC comes from the source at `5d70dc2`. What it says about Avalonia comes from its API docs
and release notes; the current stable line is 12.1. Items marked **[check]** are assumptions that Phase 0 must confirm.

---

## 1. The short answer

1. **Cut the seams first, with WPF still on top.** Move everything that is not UI into portable libraries, so the port
   never breaks the shipping viewer.
2. **Replace WPF with Avalonia on Windows** and reach parity, GPU video and Windows-key capture included.
3. **Add macOS, then Linux.** Each is a set of platform services plus a video backend.

Every phase ends with a Windows viewer that can ship.

---

## 2. What ties the viewer to Windows today

| Area | Where | Windows API | Cross-platform replacement |
|---|---|---|---|
| Windows and theme | `Views/*.xaml(.cs)`, `Themes/Dark.xaml` (864 lines of XAML) | WPF | Avalonia XAML and styles |
| Framebuffer drawing | [VncView.cs:540](../src/BetterVnc.App/Controls/VncView.cs#L540), [:580](../src/BetterVnc.App/Controls/VncView.cs#L580): `WriteableBitmap`, only dirty rectangles copied | WPF | Avalonia `WriteableBitmap`, or an OpenGL texture with sub-rectangle uploads (Phase 0 decides) |
| Pointer and wheel | VncView.cs:685-852 | WPF mouse events, `SystemParameters.WheelScrollLines`, `SPI_GETWHEELSCROLLCHARS` | Avalonia pointer events. Wheel `Delta` comes in lines, so multiply by 120 to keep today's notch accumulator |
| Keyboard | [SessionWindow.xaml.cs:461](../src/BetterVnc.App/Views/SessionWindow.xaml.cs#L461) reads raw `WM_KEYDOWN`; [KeyboardTranslator.cs:96](../src/BetterVnc.App/Input/KeyboardTranslator.cs#L96) gets keysyms from `ToUnicodeEx` | user32 | `KeyEventArgs.PhysicalKey` (layout-independent) mapped to XT scancodes with one table for every OS; keysym from the OS keyboard layout |
| Windows-key, Alt+Tab capture | `Input/LowLevelKeyboardHook.cs` (`WH_KEYBOARD_LL`) | user32 | Windows unchanged; macOS `CGEventTap`; Linux X11 `XGrabKeyboard` |
| Remote cursor | `Input/CursorFactory.cs` (`CreateIconIndirect`) | user32, gdi32 | Avalonia `Cursor(Bitmap, PixelPoint hotSpot)` |
| Clipboard | SessionWindow.xaml.cs:535-615; [AgentClient.cs:386-475](../src/BetterVnc.App/Services/AgentClient.cs#L386): text, PNG, CF_DIB, file lists, `AddClipboardFormatListener`, `GetClipboardSequenceNumber` | user32, OLE | The `IGuestClipboard` implementations the agent already has for all three OSes |
| JPEG decode (Tight, motion JPEG) | `Services/WicJpegDecoder.cs` | WIC | SkiaSharp, which ships with Avalonia |
| PNG (screenshots, clipboard images) | [SessionWindow.xaml.cs:744](../src/BetterVnc.App/Views/SessionWindow.xaml.cs#L744), AgentClient.cs:386 | WIC | SkiaSharp |
| Audio | `Services/AudioPlayer.cs` (NAudio `WaveOutEvent`) | winmm | `IAudioSink` per OS |
| Saved passwords | [ProfileStore.cs:61](../src/BetterVnc.App/Services/ProfileStore.cs#L61) (DPAPI) | crypt32 | DPAPI, macOS Keychain, Linux Secret Service |
| SMB drop folder | [FileTransferService.cs:161](../src/BetterVnc.App/Services/FileTransferService.cs#L161) (`WNetAddConnection2`) | mpr | A managed SMB2 client (SMBLibrary), or SMB only on Windows |
| GPU video | `Controls/VideoSurfaceHost.cs` (child HWND); `BetterVnc.Media`: `D3D11VideoDevice`, `VideoRenderer` (flip-model swap chain), `H264Decoder` (Media Foundation, DXVA) | D3D11, DXGI, MF | A video backend per OS (section 5) |
| Window placement, fullscreen | [SessionManager.cs:70](../src/BetterVnc.App/Services/SessionManager.cs#L70) (`SystemParameters.WorkArea`), [SessionWindow.xaml.cs:766](../src/BetterVnc.App/Views/SessionWindow.xaml.cs#L766) | WPF | Avalonia `Screens`, `WindowState.FullScreen` |
| Dialogs | `MessageBox`, `OpenFileDialog`, `SaveFileDialog` | WPF, Win32 | Avalonia `StorageProvider`, plus a small message dialog (Avalonia has no MessageBox) |
| `vnc://` links | `App.xaml.cs`, the installer's registry entries | registry | macOS: Info.plist URL type and Avalonia's activation event. Linux: `.desktop` file with `x-scheme-handler/vnc` |

**Already portable:**

- `VncClient` and its decoders
- `AgentConnection`
- `UnraidService` and the SFTP upload (SSH.NET)
- The profile JSON, `ViewerIdentity`, `PictureModes` and `HostParser`
- `VideoStreamPlayer`'s logic, apart from its `Dispatcher` timers
- In `BetterVnc.Media`: `PixelConvert` (SIMD NV12↔BGRX), `AnnexB`, `DirtyRegion`, `FrameRateGovernor` and
  `VideoLayout`, which are plain C#

**One more constraint:** `BetterVnc.Media` is shared with the Windows agent, which uses `H264Encoder` and
`DesktopDuplicator`. Splitting it must not change the agent.

---

## 3. Target layout

| Project | Target | Holds |
|---|---|---|
| `BetterVnc.Protocol`, `BetterVnc.Agent.Protocol` | net10.0 | Unchanged |
| `BetterVnc.Viewer.Core` (new) | net10.0 | Models, `ProfileStore`, `ViewerIdentity`, `UnraidService`, `FileTransferService`, `AgentClient`, `VideoStreamPlayer`, and a `SessionController` lifted out of SessionWindow.xaml.cs: connection lifecycle, authentication retry, picture-mode logic, clipboard sync rules, status text. No UI types |
| `BetterVnc.Media` | net10.0 | The portable parts listed above, `DecodedFrame`, and the decoder and presenter interfaces |
| `BetterVnc.Media.Windows` (split out) | net10.0-windows | `D3D11VideoDevice`, `H264Decoder`, `H264Encoder`, `DesktopDuplicator`, `VideoRenderer`, the interop code. The Windows agent references this |
| `BetterVnc.Media.Mac` (new) | net10.0 + Swift dylib | VideoToolbox decode, Metal presentation |
| `BetterVnc.Media.Linux` (new) | net10.0 | libavcodec decode, OpenGL presentation |
| `BetterVnc.Platform` (new) | net10.0 (+ -windows) | The clipboards moved out of the agent, secret stores, audio sinks, keyboard-layout lookup, system-key capture. Used by both the agent and the viewer |
| `BetterVnc.Viewer` (new, replaces `BetterVnc.App`) | net10.0; win-x64, osx-arm64, osx-x64, linux-x64 | Avalonia windows, the `VncView` control, choosing the platform services at startup |

Interfaces (the names are suggestions):

- **`IUiThread`:** post, invoke and timers. Replaces `Dispatcher` and `DispatcherTimer` in `AgentClient` and
  `VideoStreamPlayer`.
- **`ISecretStore`:** protect and unprotect. Values written on Windows stay as they are today: base64 of the DPAPI
  bytes, with **no prefix**, because older viewers share `profiles.json` and can't read a prefixed value. Only the new
  stores tag what they write: `keychain:` on macOS and `secret-service:` on Linux. Base64 has no colon, so the rule is
  unambiguous: no prefix means DPAPI. A `profiles.json` copied to another OS then fails visibly ("This password was
  saved on Windows; enter it again") instead of quietly reading as "no password", with no migration and no cut-over
  date. Decided 2026-10-20.
- **`ILocalClipboard`:** `IGuestClipboard` (formats, change token, files) plus a change event where the OS has one.
- **`IAudioSink`:** `Start(rate, channels, bits)`, `Feed`, `Stop`. The QEMU audio stream is plain PCM.
- **`IKeyboardLayout`:** physical key plus modifiers to keysym, with Ctrl and Alt stripped and AltGr kept, as
  `ToKeysym` does today.
- **`ISystemKeyCapture`:** install and uninstall, with a callback that gets the physical key and down/up.
- **`IVideoBackend`:** creates an H.264 decoder and a surface.
  - The decoder delivers either CPU NV12 or an opaque GPU picture. `DecodedFrame` already has both shapes; its D3D11
    texture becomes a handle owned by the backend.
  - The surface takes `Present`, `SetLayout` and `CaptureFrame`, as `VideoRenderer` does today.

---

## 4. Phases

### Phase 0: spike, on Windows, before any port

Answer the four questions that decide the design.

1. **Framebuffer speed.** Can Avalonia's `WriteableBitmap` keep up with the RFB framebuffer at 2560×1440 and
   3840×2160, 60 fps, from `BetterVnc.DemoServer`? Or does the framebuffer need an OpenGL texture with sub-rectangle
   uploads? Today's WPF path copies only the dirty rectangles. Measure how much Avalonia uploads per change. **[check]**
2. **GPU video.** Compare two ways to show the stream:
   - The existing child-window swap chain inside a `NativeControlHost`. Does it place and clip the way
     `VideoSurfaceHost` does today: inside the scroll viewport, and at zero size under overlays? Today that needs the
     `WM_WINDOWPOSCHANGING` override in [VideoSurfaceHost.cs:77](../src/BetterVnc.App/Controls/VideoSurfaceHost.cs#L77).
   - A D3D11 shared texture imported through `ICompositionGpuInterop.ImportImage`, which accepts DXGI shared handles
     and macOS `IOSurfaceRef`. This removes airspace, so overlays can draw over the video. But how much latency does
     Avalonia's compositor add, compared with today's flip-model swap chain at frame latency 1?

   Measure both with StreamBench-style timestamps.
3. **Keyboard.** Does `Win32Properties.AddWndProcHookCallback` see `WM_KEYDOWN`/`WM_SYSKEYDOWN` before Avalonia handles
   them, so raw scancodes, Alt and F10 behave as they do today? Does `PhysicalKey` cover every key the XT table needs:
   numpad, AltGr, Pause, Print Screen, and the Japanese and Korean keys? **[check]**
4. **UI tests.** Can the UIA-based GUI checks used on the WPF viewer drive Avalonia windows? Avalonia exposes
   automation peers on Windows. **[check]**

**Done when:** a short note in `docs/` gives the numbers and the option chosen for each question.

**Done (2026-10-20):** see [AVALONIA-SPIKE.md](AVALONIA-SPIKE.md).

- **Framebuffer and video:** both go through one native surface: on Windows, today's `VideoRenderer` swap chain in a
  viewport-sized `NativeControlHost`.
- **Fallback:** `OpenGlControlBase` with dirty-rectangle uploads.
- **Keyboard:** raw scancodes through the WndProc hook on Windows, and a `PhysicalKey` → XT table elsewhere.
- **UI tests:** UIA works as on WPF.

### Phase 1: cut the seams, WPF still on top

1. **Split `BetterVnc.Media`** into `BetterVnc.Media` (portable) and `BetterVnc.Media.Windows`.
   - The agent's Windows target and StreamBench reference `Media.Windows`.
   - Split the tests the same way: `CodecTests` and `GpuVideoTests` go with Windows; `Nv12ConversionTests` and
     `StreamPacingTests` stay portable.
2. **Create `BetterVnc.Viewer.Core`** and move in the models, `ProfileStore` (without its MessageBox and DPAPI),
   `ViewerIdentity`, `UnraidService`, `FileTransferService`, `AgentClient` and `VideoStreamPlayer`.
   - Replace `Dispatcher` and `DispatcherTimer` with `IUiThread`.
   - Replace the WPF clipboard calls in `AgentClient` with `ILocalClipboard`.
   - Replace `BitmapSource` with PNG bytes and a small image codec interface.
3. **Extract `SessionController`** from SessionWindow.xaml.cs: `ConnectAsync`, the authentication retry, `StartAgent`,
   `ApplyPictureAsync`, the clipboard rules and the `UpdateStatus` text. The window keeps only controls and events.
4. **Move the agent's clipboards into `BetterVnc.Platform`.** `WindowsClipboard` becomes the viewer's Windows clipboard
   too. Merge it with what `AgentClient` does today (PNG plus CF_DIB, the "Preferred DropEffect" on file lists),
   keeping whichever behaviour is richer.
5. **Add tests** for `SessionController` and `VideoStreamPlayer` with fakes and no GUI:
   - the idle fallback to RFB
   - key-frame requests after a sequence gap
   - lossless VNC while the agent cannot stream

**Done when:**

- The WPF viewer behaves exactly as before: the existing tests pass, plus one manual pass.
- `Viewer.Core`, `Media` and `Platform` build for net10.0, and their tests run on the Linux and macOS CI runners.

### Phase 2: Avalonia viewer on Windows, at parity

1. **New `BetterVnc.Viewer` project** on Avalonia 12.1 with Skia.
   - Port `Dark.xaml` to Avalonia styles.
   - Port `MainWindow`, `ConnectionEditorWindow`, `UnraidServerWindow`, `CredentialsDialog` and `SessionWindow`.
   - Add a message dialog, and use `StorageProvider` for the file dialogs.
2. **Port `VncView`** to an Avalonia control:
   - The framebuffer goes through the same swap chain as the stream. Add a dirty-rectangle upload to `VideoRenderer`,
     and present from a thread of its own, since `Present` blocks about one frame. The host is sized to the visible
     picture area, with scrolling and fit scaling in the destination rectangle.
   - An `OpenGlControlBase` fallback with `DirectComposition` and `ShouldRenderOnUIThread`.
   - An automation peer, so UIA can find the view.
   - mouse buttons and pointer capture
   - the wheel: Avalonia's `Delta` × 120 into the existing accumulator; smooth scrolling through the agent unchanged
   - DPI from `RenderScaling`, and `PictureArea`
3. **Keyboard.**
   - Put the `PhysicalKey` → XT scancode table in `Viewer.Core`, with a test that replays Windows VK/scancode pairs
     through today's `KeyboardTranslator` and through the new path and compares the results.
   - Windows keysyms keep `ToUnicodeEx`, now behind `IKeyboardLayout`.
   - `LowLevelKeyboardHook` moves behind `ISystemKeyCapture`.
4. **Cursor:** build an Avalonia `Cursor` from the premultiplied BGRA bitmap `CursorFactory` already computes.
5. **GPU video:** `VideoRenderer` in the same `NativeControlHost`. Phase 0 measured it at every frame and 18 ms; the
   composition import took 32–41 ms. The overlay rule stays: the surface steps aside under in-window overlays, and the
   fullscreen toolbar becomes a popup.
6. **JPEG and PNG through SkiaSharp.** Compare Tight JPEG decode speed with WIC, using the protocol tests' JPEG
   rectangles.
7. **Ship side by side.**
   - `release.yml` adds a `BetterVNC-<version>-preview-win-x64.zip` built from the new project.
   - The WPF viewer stays the one the installer installs.
8. **Retire WPF** once the preview matches: delete `src/BetterVnc.App` and point the installer at the new project.

**Done when:** every row of the README's feature table works in the Avalonia build on Windows, including the stream
modes on the GPU path and Windows-key capture in fullscreen.

### Phase 3: macOS viewer

**Platform services:**

- **Saved passwords:** Keychain generic-password items (`SecItemAdd`, `SecItemCopyMatching`), through P/Invoke to
  Security.framework.
  - The value in `profiles.json` is `keychain:` plus a reference to the item, not ciphertext, so deleting a profile
    must also delete its items.
  - A value without a prefix was saved on Windows: say so and ask for the password again.
- **Clipboard:** the agent's `MacClipboard` works as is: `pbcopy`/`osascript`, polled once a second. If that delay
  shows, write a native `NSPasteboard` version through `objc_msgSend`, as `Tray/MacStatusItem.cs` already does for the
  menu bar.
- **Audio:** AudioQueue (AudioToolbox) through P/Invoke; it's small for fixed-format PCM.
- **Keysyms:** `UCKeyTranslate` with the current input source, using a table from `PhysicalKey` to macOS key codes.
- **System keys:** a `CGEventTap` while the session window is focused, so Cmd+Tab, Cmd+Space and Cmd+Q reach the
  remote. This needs the Accessibility permission. Reuse the check and prompt in `tools/BetterVnc.Agent/MacPermissions.cs`.
- **Modifier mapping:** a Mac keyboard on a Windows or Linux remote needs a Cmd→Ctrl option. That's the mirror of
  `MacModifierMode`, which today maps a Windows keyboard onto a Mac remote.

**Video: VideoToolbox, through a small Swift dylib.**

- It sits beside `tools/BetterVnc.Agent.MacCapture` and is built by the same CI job.
- Its C API creates a decoder session and decodes Annex B. Pictures are then either presented into a CAMetalLayer view
  (hosted in a `NativeControlHost`) or handed back as IOSurfaces for `ICompositionGpuInterop`.
- Software fallback: VideoToolbox falls back to software itself, and CPU NV12 into a `WriteableBitmap` works too.

**Packaging:**

- `BetterVNC.app` for Apple silicon and Intel, with `CFBundleURLTypes` for `vnc`, an `.icns` icon, and a `.dmg` or
  `.pkg` from the macOS job in `release.yml`.
- It's unsigned, like the agent. macOS ties the Accessibility grant to the code signature, so expect to grant it again
  after updates. Reuse the approach in `MacAppBundle.cs`.

**Done when:** from a Mac, connecting to an Unraid VM and to another Mac works with clipboard, files, audio, the stream
modes, and fullscreen with Cmd capture.

### Phase 4: Linux viewer

**Platform services:**

- **Saved passwords:** Secret Service over D-Bus, through libsecret or `secret-tool`.
  - Values are written with the `secret-service:` prefix.
  - Give a clear error when no keyring is running, as on headless setups or bare window managers, and offer an opt-in
    unencrypted fallback with a prefix of its own.
  - A value without a prefix was saved on Windows, as on macOS.
- **Clipboard:** the agent's `LinuxClipboard` (`wl-copy`/`wl-paste` or `xclip`) as is.
- **Audio:** libpulse-simple (`pa_simple_new`, `pa_simple_write`). PipeWire serves it through pipewire-pulse.
- **Keysyms:** xkbcommon (`xkb_state_key_get_one_sym`), using a table from `PhysicalKey` to evdev codes.
- **System keys:** `XGrabKeyboard` on X11 while fullscreen and focused. On Wayland, capture needs the compositor's
  keyboard-shortcuts-inhibit protocol **[check]** whether Avalonia exposes it. Until then, prefer XWayland.

**Video: libavcodec (FFmpeg) through P/Invoke.**

- Decode in software first and add VA-API later.
- Present with an NV12 shader in `OpenGlControlBase`, or convert NV12→BGRX on the CPU into a `WriteableBitmap`.
- H.264 availability varies by distro. Fedora's libavcodec has no H.264 without RPM Fusion; openh264 is the usual
  fallback. Detect this at startup and say so in the picture menu. Motion JPEG streaming works everywhere.

**Packaging:** a `tar.gz` with `install.sh` (like the agent), a `.desktop` file with `x-scheme-handler/vnc`, and the
icon. AppImage or Flatpak can come later.

**Done when:** the macOS checklist passes on Ubuntu, under both an X11 and a Wayland session, and on Fedora.

### Phase 5: CI and release

- A build-and-test matrix on `windows-latest`, `macos-latest` and `ubuntu-latest` for the Protocol, Viewer.Core, Media
  and Platform tests.
- Viewer artifacts for all three OSes in `release.yml`, included in `SHA256SUMS.txt` and the release notes.
- Viewer rows for macOS and Linux in the README's "Download and install" table.

---

## 5. Per-OS choices at a glance

| | Windows | macOS | Linux |
|---|---|---|---|
| H.264 decode | Media Foundation + DXVA (today) | VideoToolbox | libavcodec, VA-API later |
| Video presentation | Flip-model swap chain (today) or composition import | CAMetalLayer view or IOSurface import | OpenGL (`OpenGlControlBase`) or `WriteableBitmap` |
| JPEG, PNG | SkiaSharp | SkiaSharp | SkiaSharp |
| Audio | NAudio (today) | AudioQueue | libpulse-simple |
| Saved passwords | DPAPI (today) | Keychain | Secret Service |
| Clipboard | `WindowsClipboard` | `MacClipboard` | `LinuxClipboard` |
| Keysym lookup | `ToUnicodeEx` (today) | `UCKeyTranslate` | xkbcommon |
| System-key capture | `WH_KEYBOARD_LL` (today) | `CGEventTap` + Accessibility | `XGrabKeyboard` (X11) |
| SMB drop folder | `WNetAddConnection2` (today) | SMBLibrary, or SFTP and agent only | SMBLibrary, or SFTP and agent only |

**Audio alternative:** one library for all three OSes, such as OpenAL Soft or SDL3. That means fewer code paths but one
more native library to ship with each build. With a single fixed PCM format, the per-OS P/Invoke code is small, so this
plan prefers it.

---

## 6. Risks and open questions

1. **Framebuffer drawing speed** (Phase 0, question 1). **Confirmed:** `WriteableBitmap` re-uploads the whole bitmap,
   and 4K gets about 29 frames a second. Resolved by the native surface, or the OpenGL fallback.
2. **Video latency** through Avalonia's compositor. **Confirmed:** at least one display frame more than the direct swap
   chain. Resolved by keeping the swap chain.
3. **Airspace.** **Confirmed** for `NativeControlHost`, including the scroll-viewport leak. Today's rule that the
   surface steps aside under overlays ([VncView.cs:304](../src/BetterVnc.App/Controls/VncView.cs#L304)) carries over,
   with the host sized to the viewport.
4. **Keyboard fidelity.** Today's scancodes come straight from `WM_KEYDOWN`. The `PhysicalKey` path must produce the
   same scancode for every key; the replay test in Phase 2 guards this.
5. **macOS permissions.** Accessibility for key capture, the prompt flow, and granting it again after unsigned updates.
6. **Wayland.** No keyboard grab, and cursor and window-position behaviour differ. Prefer XWayland until this is solved.
7. **H.264 on Linux.** Codec availability per distro, and FFmpeg ABI versions: probe for the libavcodec major versions
   at load time.
8. **Profiles don't travel between OSes,** because saved passwords are bound to the OS. An export without secrets
   could come later.

---

## 7. Not in scope

- The agent's Windows capture and encoder, which keep their current code in `BetterVnc.Media.Windows`.
- Mobile viewers.
- Code signing and notarization, which need an Apple Developer account and a Windows code-signing certificate.

---

## Sources

- [Avalonia 12 breaking changes](https://docs.avaloniaui.net/docs/avalonia12-breaking-changes) and the
  [12.1 release notes](https://newreleases.io/project/github/AvaloniaUI/Avalonia/release/12.1.0): .NET 8 or later
  (.NET 10 recommended), Skia as the only desktop renderer, the Wayland backend with X11 fallback.
- [`Win32Properties.AddWndProcHookCallback`](https://api-docs.avaloniaui.net/docs/M_Avalonia_Controls_Win32Properties_AddWndProcHookCallback):
  raw window messages on Windows.
- [`KeyEventArgs`](https://api-docs.avaloniaui.net/docs/T_Avalonia_Input_KeyEventArgs): `PhysicalKey`, independent of
  the keyboard layout.
- [`ICompositionGpuInterop`](https://api-docs.avaloniaui.net/docs/T_Avalonia_Rendering_Composition_ICompositionGpuInterop)
  and its [`ImportImage`](https://api-docs.avaloniaui.net/docs/M_Avalonia_Rendering_Composition_ICompositionGpuInterop_ImportImage)
  method: importing DXGI shared handles and IOSurfaces into Avalonia's compositor.
