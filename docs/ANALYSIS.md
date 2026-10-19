# BetterVNC: Target analysis

This document analyses the two VNC servers BetterVNC must talk to, what each one
can and cannot do over the RFB protocol, and how that shapes the design of the
Windows viewer. Everything here assumes LAN or VPN-to-LAN connectivity, so
bandwidth is plentiful and latency is low; the design optimises for
responsiveness and fidelity rather than for narrow links.

---

## 1. Unraid VMs (QEMU built-in VNC server)

Unraid runs VMs with libvirt + QEMU. Each VM's XML carries a `<graphics type='vnc'>`
element. The defaults Unraid generates look like:

```xml
<graphics type='vnc' port='-1' autoport='yes' websocket='-1' listen='0.0.0.0' keymap='en-us'>
  <listen type='address' address='0.0.0.0'/>
</graphics>
<video>
  <model type='qxl' ram='65536' vram='65536' vgamem='16384' heads='1' primary='yes'/>
</video>
```

* `autoport='yes'` means the first VM gets TCP **5900**, the next **5901**, and so on.
  The port is reassigned on every VM start, so a viewer must not cache ports.
* `websocket='-1'` adds a second, WebSocket-wrapped listener used by the Unraid web
  UI's noVNC. BetterVNC connects to the plain TCP port directly, which is faster
  and bypasses the web UI session entirely.
* `listen='0.0.0.0'` makes it reachable from the LAN without any tunnelling.
* An optional `passwd='...'` attribute (set in the VM form as "VNC password")
  enables classic VNC authentication (security type 2, DES challenge).
  Without it the server offers security type 1 (None).
* `keymap='en-us'` matters a lot; see the keyboard section below.

### 1.1 Protocol capabilities of QEMU's VNC server (QEMU 7.x to 9.x as shipped in Unraid 6.12 / 7.x)

| Area | Support | Notes |
|---|---|---|
| RFB version | 3.3, 3.7, 3.8 | We speak 3.8. |
| Security | None (1), VNC auth (2), VeNCrypt (19) when TLS configured | Unraid only uses 1 or 2. |
| Encodings | Raw, CopyRect, Hextile, Tight (zlib, JPEG, PNG), ZRLE, ZYWRLE, zlib | **Tight** is the best choice on LAN: lossless zlib + palette/gradient filters, JPEG only if the client asks for it. |
| Pixel formats | Any true-colour format via SetPixelFormat; honours **WMVi** (0x574D5669) to tell the client when the guest changes depth | We request 32bpp/depth 24 BGRX so decoded pixels land straight in a Windows bitmap. |
| Cursor | Rich cursor (-239), alpha cursor (-314) | Server stops drawing the pointer into the framebuffer; the client renders it locally, giving a zero-latency mouse feel. |
| Resize | DesktopSize (-223), ExtendedDesktopSize (-308) | Guest resolution changes arrive as framebuffer resizes. Client-initiated SetDesktopSize is **not** supported by QEMU. |
| Keyboard | KeyEvent (keysyms) and **QEMU Extended Key Event** (-258) carrying raw XT scancodes | See 1.3. |
| Clipboard | Legacy ClientCutText/ServerCutText (Latin-1) and the **Extended Clipboard** extension (TigerVNC style, pseudo-encoding 0xC0A1E5CE) with UTF-8 text | Only functional when the guest runs a vdagent; see 1.2. |
| Audio | **QEMU Audio** extension (-259) | Raw PCM pushed from the guest's sound device to the viewer. Needs a `<sound>` device in the VM. libvirt's default `<audio id='1' type='none'/>` backend explicitly allows VNC audio. noVNC does not implement this, so it is a differentiator. |
| LED state | QEMU LED state (-261) | Lets the client show Caps/Num/Scroll lock state. |
| Continuous updates / Fence | Not supported | Use the classic incremental FramebufferUpdateRequest loop. |
| File transfer | **None** | RFB has no standard file transfer. A side channel is required (see section 3). |

### 1.2 Clipboard on Unraid VMs

QEMU's VNC clipboard only works when QEMU has a "clipboard peer" for the guest.
Since QEMU 6.1 / libvirt 8.4 that is the `qemu-vdagent` channel: QEMU speaks the
SPICE vdagent protocol itself over a virtio-serial port, so the ordinary
`spice-vdagent` (Linux) or `spice-guest-tools` / `vdagent` (Windows) inside the
guest provides clipboard sync without SPICE. Unraid 6.12+ ships new enough
libvirt and QEMU.

Add this to the VM XML (Unraid: VM, Edit, XML view, inside `<devices>`):

```xml
<channel type='qemu-vdagent'>
  <source>
    <clipboard copypaste='yes'/>
    <mouse mode='client'/>
  </source>
  <target type='virtio' name='com.redhat.spice.0'/>
</channel>
```

Then install the agent in the guest:

* Windows guest: install `spice-guest-tools` (or just `vdagent` from the virtio-win ISO). The "Spice VDAgent" service must be running.
* Linux guest: `apt install spice-vdagent` (or distro equivalent); it autostarts in most desktop sessions.

Message flow the viewer implements (TigerVNC extended clipboard semantics):

1. Viewer includes pseudo-encoding 0xC0A1E5CE in SetEncodings; QEMU replies with a
   caps message (flags = Caps | Text).
2. Viewer sends its own caps (Text, max size).
3. Local clipboard changes: viewer sends **Notify(Text)**. When the guest user
   pastes, vdagent asks QEMU, QEMU sends **Request(Text)**, viewer answers
   **Provide(Text)** (zlib-compressed, UTF-8, CRLF, NUL-terminated).
4. Guest copies: QEMU sends **Notify(Text)**; viewer sends **Request**; QEMU
   sends **Provide**; viewer sets the Windows clipboard.

Without vdagent, QEMU still advertises the extension but no clipboard data ever
flows, which is why the Unraid noVNC console appears to "support" clipboard yet
nothing happens. BetterVNC's Unraid integration reads the domain XML and warns
when the channel is missing.

QEMU's clipboard peer carries **text only**. Images and files cannot go through
the VNC channel at all (section 3).

### 1.3 Keyboard on Unraid VMs

QEMU maps RFB keysyms back to PC scancodes using the `-k <keymap>` table. That
mapping is lossy (dead keys, AltGr combinations, non-US layouts, numpad) and is
the source of most "wrong character typed" complaints with noVNC.

The QEMU Extended Key Event lets the client send the **physical XT scancode**
along with the keysym, which is exactly what RDP does. QEMU uses the scancode
*only when no keymap was specified*. Unraid's template sets `keymap='en-us'`, so
for the best experience remove the `keymap` attribute from `<graphics>`; then the
guest OS's own layout applies and every key (including Win/Super, AltGr, Pause,
Print Screen, the numpad) behaves exactly like a physically attached keyboard.

BetterVNC always sends extended key events when the server advertises -258
(scancode + keysym), so it works correctly either way, and the Unraid
integration flags VMs that still carry the keymap attribute.

Scancode packing for the extension: XT scancode, with `0xE0`-prefixed extended
keys encoded as `(code & 0x7F) | 0x80`.

### 1.4 Mouse

Unraid templates include a USB tablet device (`<input type='tablet' bus='usb'/>`),
so pointer events are absolute and the guest cursor tracks the viewer cursor
1:1. With `<mouse mode='client'/>` in the vdagent channel this also holds for
guests where the tablet is missing. Scroll wheel = RFB buttons 4/5 (and 6/7 for
horizontal). There is no relative-mouse mode in RFB; games needing raw mouse
are out of scope.

### 1.5 Discovering VMs

The Unraid host runs an SSH server (root). `virsh` is available, so a viewer can:

```
virsh list --all --name              # all VM names
virsh domstate <vm>                  # running / shut off / paused
virsh vncdisplay <vm>                # ":0" means port 5900, ":1" means 5901
virsh dumpxml --security-info <vm>   # includes the VNC passwd attribute
virsh start <vm>                     # power on a stopped VM
```

This lets BetterVNC present an "Unraid server" with a live VM list, auto-fill
host/port/password, start VMs, and warn about the keymap/vdagent configuration.

---

## 2. macOS Screen Sharing / Remote Management (Apple's VNC server)

macOS ships a VNC-compatible server (`screensharingd`, port 5900) enabled via
System Settings, General, Sharing, Screen Sharing (or Remote Management).

### 2.1 Handshake peculiarities

* The server announces **`RFB 003.889`**. A client must answer with a standard
  version (`RFB 003.008`); the server then falls back to standard behaviour.
  Replying 3.889 enables Apple-private extensions used only by Apple's client.
* Security types offered: **30** (Apple ARD authentication: Diffie-Hellman +
  AES-128), 33, 35, 36 (Apple private), and **2** (classic VNC password) only if
  "VNC viewers may control screen with password" is enabled in Screen Sharing
  settings.
* Type 30 is what makes third-party viewers "just work" with a normal macOS user
  account. Algorithm (implemented in `AppleAuthentication`):
  1. Server sends generator (u16), key length (u16), prime modulus and server
     public key (key length bytes each).
  2. Client picks a random private key, computes public = g^priv mod p and the
     shared secret = serverPub^priv mod p.
  3. AES key = MD5(shared secret, big-endian, key-length bytes).
  4. Credentials block = 128 bytes: username (64 bytes, NUL-terminated, rest
     random) + password (64 bytes, same).
  5. AES-128-ECB encrypt the block (no padding), send ciphertext (128 bytes)
     followed by the client public key (key length bytes).
  6. Standard SecurityResult follows. Failure reasons are not sent reliably,
     so the viewer prompts again on failure.
* After `ClientInit` the server may take a second or two before the first
  framebuffer update while it brings up the shared session.

### 2.2 Framebuffer behaviour

| Area | Behaviour |
|---|---|
| Encodings | Raw, CopyRect, RRE, Hextile, Zlib, **ZRLE**. No Tight. We list Tight first anyway (harmless), then ZRLE; macOS picks ZRLE. |
| Pixel format | Honours SetPixelFormat; 32bpp depth 24 works. |
| Resolution | The framebuffer is the **logical (scaled) desktop size**, e.g. 1728 x 1117 on a 16" MacBook Pro even though the panel is 3456 x 2234. Text is therefore rendered at 1x and looks soft when the viewer upscales it. Nothing in RFB lets a third-party client request the native 2x buffer. |
| Multiple displays | All displays are stitched into one framebuffer. |
| Cursor | Rich cursor pseudo-encoding (-239) works, so the pointer is rendered locally. |
| Resize | DesktopSize (-223) on display changes. |
| Clipboard | Legacy ServerCutText / ClientCutText (ISO-8859-1 only). Non-Latin text is lost; the extended clipboard extension is not implemented. |
| Idle / screen lock | If the Mac is at the login window or locked, the server still works and shows the lock screen; "headless" Macs (no monitor) run at a tiny default resolution unless a dummy display plug is used. |
| Bell | Sent as RFB Bell. |
| File transfer, audio | None in RFB. See section 3. |

### 2.3 Keyboard mapping

macOS consumes X11 keysyms and maps them onto its virtual key codes:

* `Super_L/R` (0xFFEB/0xFFEC) becomes **Command**
* `Alt_L/R` (0xFFE9/0xFFEA) becomes **Option**
* `Control_L/R` becomes Control
* Latin keysyms are interpreted by character, so a US viewer typing `@` produces `@` regardless of the Mac's layout.

A Windows keyboard therefore needs a modifier mapping policy. BetterVNC offers
"Mac keyboard mode": physical **Win = Command**, physical **Alt = Option**,
plus an alternative "swap" layout (Alt = Command, Win = Option) for people who
prefer Command under the thumb like on a Mac keyboard. Common Windows chords are
translated when the mode is on: Ctrl+C/V/X/Z/A/S/F/W/T/Tab become Cmd+..., so
muscle memory works.

### 2.4 Mouse

Absolute pointer events work. Right-click = button 3 (macOS treats it as
secondary click). Scroll wheel via buttons 4/5; Screen Sharing turns each press
into roughly one line, so without the agent BetterVNC sends a configurable number
of press/release pairs per notch with a velocity boost; with the agent it sends
pixel deltas instead (section 3).

---

## 3. What RFB cannot do, and how BetterVNC fills the gaps

RFB carries pixels, keys, pointer, bell and (text) clipboard. Everything else
that makes RDP pleasant is outside the protocol for both targets:

| RDP-like feature | Unraid/QEMU | macOS | BetterVNC approach |
|---|---|---|---|
| Text clipboard both ways | Yes (with vdagent) | Yes (Latin-1) | Native RFB; extended clipboard when offered. |
| Image clipboard | No | No | **Guest agent** (below): PNG both ways, automatically. Without the agent: local screenshot of the remote framebuffer to the Windows clipboard or a PNG file; pasting an image *into* the remote is converted to a PNG file and sent through the file channel below. |
| File copy/paste, drag-and-drop | No | No | **Guest agent**: files are copied to the other side and put on its clipboard. Fallback side channel per connection: **SFTP** (SSH.NET) or an **SMB** UNC path. Dropped files or Ctrl+V'd files are uploaded to a configured "drop folder" on the remote. Works for macOS (Remote Login), Linux guests (sshd) and Windows guests (OpenSSH feature or a share). |
| Smooth / high-resolution scrolling | Wheel buttons only (QEMU maps 4/5 to a wheel notch) | Wheel buttons map to about one line each; Apple's own client uses private pixel-scroll messages | **Guest agent** `scroll` message: pixel deltas plus gesture phase, injected with CoreGraphics pixel scroll events (macOS), `SendInput` (Windows) or xdotool/ydotool (Linux). Fallback: VNC wheel clicks with a per-connection multiplier and velocity boost. |
| Audio | QEMU audio extension | No | Implemented for QEMU; nothing possible for macOS. |
| Bandwidth-efficient picture (video codec, motion handled) | Tight JPEG only, and only with `lossy=on` on the QEMU command line (libvirt does not expose it; default off, so the quality hint is ignored) | None: Raw, Hextile, Zlib, ZRLE only | **Guest agent** video channel (`startVideo` / `videoFrame`): the agent captures and encodes, the viewer decodes and paints while RFB keeps carrying input, cursor shapes and resizes at one update a second. Lossless RFB repaint whenever the screen is still. A **Picture** box in the toolbar switches modes live; entries the server or agent cannot deliver are greyed out with the reason. First codec is motion JPEG (GDI capture, Windows agent); H.264 with the guest's hardware encoder slots in through the same negotiation. |
| Dynamic resolution (resize window resizes remote) | Not supported by QEMU | Not supported | Viewer-side scaling (fit / 1:1 / custom) with high-quality resampling. |
| Multi-monitor | Single stitched framebuffer | Single stitched framebuffer | Fullscreen on any monitor; "fit" scaling. |
| Smart-card, printer, USB redirection | No | No | Out of scope. |

The **BetterVNC guest agent** (`tools/BetterVnc.Agent`, one self-contained
executable for Windows and for Linux; on macOS the native app in
`tools/BetterVnc.Agent.Mac`, which implements the same protocol in Swift) fills
the clipboard gap over its own TCP port (default 5970):

* Wire format: `u32 headerLength, u32 payloadLength, UTF-8 JSON header, payload`.
  Messages: `hello`/`welcome` (handshake), `clipboardChanged` (push from the
  agent, lists formats and, for files, names and sizes), `getClipboard` /
  `clipboardData`, `setClipboard` / `ok`, `ping` / `pong`, `error`, and `scroll`
  (viewer to agent, fire-and-forget: `dx`/`dy` pixels, `phase`, optional `x`/`y`
  and the raw Windows wheel delta; announced through `features: ["scroll"]` in
  the `welcome`), and the video set announced through `features: ["video"]` plus
  `codecs`: `startVideo` (viewer's decodable `codecs` in order of preference,
  `bitrate` kbit/s, `maxFps`) / `videoStarted` (negotiated codec, bitrate, fps,
  size, `encoder`), `setVideo` / `ok` (re-tune, key-frame request), `stopVideo` /
  `ok`, `videoFrame` (agent to viewer, encoded frame as payload with `sequence`,
  `timestamp`, `keyframe`, `width`, `height`; the agent drops frames a slow link
  cannot take, never delays them) and `videoStopped` (capture ended). Formats are
  `text`, `html`, `png` and `files` (header lists relative names and sizes, the
  payload is the concatenated contents).
* Authentication: pairing by default. The viewer sends `pair` with an ephemeral
  ECDH P-256 public key; the agent shows an Allow/Deny dialog on the guest
  screen (which the user sees through VNC), answers `paired` with its own key,
  and both derive a 32-byte secret stored per viewer id in `bettervnc-agent.json`
  and DPAPI-protected in the viewer profile. Alternatively a token or password
  set in the agent. Every connection then runs a nonce/HMAC-SHA256 exchange in
  both directions, so neither key nor token is ever transmitted. Transport is
  plain TCP for the LAN; bind to 127.0.0.1 and tunnel over SSH when it must
  leave the LAN. The agent has a tray icon (Windows) or menu bar item (macOS)
  for its settings.
* Clipboard back ends: Windows Forms clipboard on a dedicated STA thread with
  `GetClipboardSequenceNumber` for change detection; `xclip` or `wl-clipboard`
  on Linux with a content hash as change token; `NSPasteboard` in process on
  macOS with `changeCount`.
* Echo prevention: the agent records the change token its own `setClipboard`
  produced and does not announce it; the viewer remembers the sequence number
  of what it just placed on the Windows clipboard and the hashes of the last
  PNG sent and received.
* Viewer policy: images copied locally go to the guest automatically unless the
  copy also carries text (Office copies). Files go to the guest on drop, "Files…"
  or "Paste", never on every local copy. Files copied in the guest are pulled
  automatically up to 64 MB.

The viewer stays fully usable against stock servers; the agent only adds to it.
Resolution control is the next capability planned for the same channel.

---

## 4. Viewer design decisions

* **Language/UI:** C# / .NET 10, WPF. WPF gives a hardware-composited surface,
  `WriteableBitmap` for cheap partial updates, trivial high-DPI handling, and
  easy Win32 interop for raw keyboard input and custom cursors.
* **Protocol library** (`BetterVnc.Protocol`): zero UI dependencies, a single
  receive thread that decodes straight into a BGRA framebuffer, dirty-rectangle
  notifications batched per `FramebufferUpdate`. Decoders are self-contained so
  they can be unit-tested against a fake server.
* **Pixel format:** 32bpp, depth 24, little-endian, R shift 16 / G 8 / B 0. This
  is exactly WPF's `Bgr32`, so no conversion step on the hot path.
* **Encoding preference:** Tight (compression level 1, JPEG off, so lossless on
  LAN), ZRLE, Hextile, Zlib, RRE, CopyRect, Raw, plus the pseudo-encodings
  listed above.
* **Picture modes:** the Tight compression level and JPEG quality can change on
  the live connection (a new `SetEncodings` followed by a non-incremental
  update request), and the client counts JPEG rectangles so the UI can tell
  whether a lossy request is honoured. The client can also pace its update
  requests (`UpdateInterval`): while the agent's video stream carries the
  picture, RFB is polled once a second so cursor shapes and resizes still
  arrive without doubling the pixel traffic. The stream player decodes off the
  UI thread with a latest-frame-wins queue, and after 0.7 s without a frame it
  shows the RFB framebuffer again and asks for a full lossless update, so an
  idle desktop is never a stale JPEG. A stream mode chosen before the agent is
  up starts on lossless VNC and switches when the agent announces `video`.
* **Keyboard:** window-procedure hook captures `WM_(SYS)KEYDOWN/UP` with scancode
  and extended bit; keysym derived via `ToUnicodeEx` (with Ctrl/Alt stripped
  so `Ctrl+C` yields keysym `c`). Optional low-level keyboard hook when
  fullscreen so Win, Alt+Tab, Alt+F4 go to the remote like in RDP fullscreen.
* **Cursor:** server cursor bitmaps become real Win32 cursors via
  `CreateIconIndirect`, so the local pointer is the remote pointer with no
  compositing delay.
* **Clipboard:** a clipboard-format listener (`AddClipboardFormatListener`)
  drives the Notify/Provide state machine; a loop guard prevents echoing data
  the remote just gave us.
* **Secrets:** stored in `%AppData%\BetterVNC\connections.json`, encrypted with
  DPAPI (current user).
