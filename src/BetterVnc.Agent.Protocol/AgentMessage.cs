using System.Text.Json.Serialization;

namespace BetterVnc.Agent.Protocol;

/// <summary>Message type names carried in <see cref="AgentMessage.Type"/>.</summary>
public static class AgentMessageType
{
    /// <summary>Sent by the agent on connect (with a nonce) and answered by the viewer (with the proof and its own nonce).</summary>
    public const string Hello = "hello";
    /// <summary>Agent accepts the viewer; carries the agent's proof and the current clipboard formats.</summary>
    public const string Welcome = "welcome";
    /// <summary>Error reply to a request, or a fatal error before the connection is closed.</summary>
    public const string Error = "error";
    /// <summary>Unsolicited: the clipboard on the sending side changed; lists the formats now available.</summary>
    public const string ClipboardChanged = "clipboardChanged";
    /// <summary>Request the clipboard content in one format.</summary>
    public const string GetClipboard = "getClipboard";
    /// <summary>Reply to <see cref="GetClipboard"/>; the payload carries the data.</summary>
    public const string ClipboardData = "clipboardData";
    /// <summary>Replace the receiving side's clipboard with the payload.</summary>
    public const string SetClipboard = "setClipboard";
    /// <summary>Generic success reply.</summary>
    public const string Ok = "ok";
    public const string Ping = "ping";
    public const string Pong = "pong";
    /// <summary>
    /// Viewer to agent instead of the viewer's hello: ask to be paired. Carries <see cref="AgentMessage.ViewerId"/>,
    /// <see cref="AgentMessage.ViewerName"/> and the viewer's ECDH <see cref="AgentMessage.PublicKey"/>. The agent asks the
    /// person at the guest screen; on approval it answers <see cref="Paired"/> and then restarts the handshake with a new hello.
    /// </summary>
    public const string Pair = "pair";
    /// <summary>Agent to viewer: pairing approved; carries the agent's ECDH public key. Both sides now derive the same key.</summary>
    public const string Paired = "paired";
    /// <summary>
    /// Viewer to agent, no reply: inject a high-resolution scroll at the guest pointer. <see cref="AgentMessage.Dx"/> and
    /// <see cref="AgentMessage.Dy"/> are pixels; only sent when the agent advertised <see cref="AgentFeature.Scroll"/>.
    /// </summary>
    public const string Scroll = "scroll";

    // ---- remote control (only when the agent advertised AgentFeature.Input); fire and forget, like scroll
    /// <summary>
    /// Viewer to agent, no reply: move the guest pointer and set its buttons. <see cref="AgentMessage.X"/>/<see cref="AgentMessage.Y"/>
    /// are pixels in the picture the viewer shows (the stream's frames) and <see cref="AgentMessage.Width"/>/<see cref="AgentMessage.Height"/>
    /// that picture's size; <see cref="AgentMessage.Buttons"/> holds the buttons that are down (<see cref="AgentButton"/>).
    /// The agent maps the picture onto the part of its screen the stream shows (the whole desktop while nothing streams),
    /// so the viewer needs to know nothing about the guest's displays or how the frames were scaled. Wheel movement
    /// travels as <see cref="Scroll"/> and applies at the position the last pointer message set.
    /// </summary>
    public const string Pointer = "pointer";
    /// <summary>
    /// Viewer to agent, no reply: a key went down (<see cref="AgentMessage.Down"/> true) or up. <see cref="AgentMessage.ScanCode"/>
    /// is the PC/AT set 1 make code with 0xE000 added for extended keys (Right Ctrl is 0xE01D, the Delete key 0xE053),
    /// so the guest applies its own keyboard layout; <see cref="AgentMessage.VirtualKey"/> is the Windows virtual-key code,
    /// used when the scan code is 0 (keys without one, such as Pause). Auto-repeat arrives as repeated presses.
    /// </summary>
    public const string Key = "key";
    /// <summary>
    /// Agent to viewer, unsolicited, while a stream that asked for it (<see cref="AgentMessage.Cursor"/> on
    /// <see cref="StartVideo"/>) runs: the guest pointer changed. With a payload it carries a new shape,
    /// <see cref="AgentMessage.Width"/>×<see cref="AgentMessage.Height"/> pixels of B, G, R, A (straight alpha, top row
    /// first, no padding) with the hot spot at <see cref="AgentMessage.HotX"/>/<see cref="AgentMessage.HotY"/>; without
    /// one the shape is the one sent before. <see cref="AgentMessage.Visible"/> says whether the pointer shows at all
    /// (apps hide it while typing, games for good). The viewer draws it at its own mouse position, so it never lags.
    /// </summary>
    public const string Cursor = "cursor";

    // ---- video (only when the agent advertised AgentFeature.Video)
    /// <summary>
    /// Viewer to agent: start streaming the screen. Carries the <see cref="AgentMessage.Codecs"/> the viewer can decode in
    /// order of preference, the wanted <see cref="AgentMessage.Bitrate"/> (kbit/s) and <see cref="AgentMessage.MaxFps"/>,
    /// and optionally the size of the viewer's picture area (<see cref="AgentMessage.Width"/>/<see cref="AgentMessage.Height"/>).
    /// Answered with <see cref="VideoStarted"/> (negotiated values) or an error.
    /// </summary>
    public const string StartVideo = "startVideo";
    /// <summary>Reply to <see cref="StartVideo"/>: the codec, bitrate, frame rate, size and encoder kind actually in use.</summary>
    public const string VideoStarted = "videoStarted";
    /// <summary>
    /// Viewer to agent: change <see cref="AgentMessage.Bitrate"/>, <see cref="AgentMessage.MaxFps"/> and/or the picture area
    /// (<see cref="AgentMessage.Width"/>/<see cref="AgentMessage.Height"/>) of the running stream, or ask for a key frame.
    /// Answered with <see cref="Ok"/>.
    /// </summary>
    public const string SetVideo = "setVideo";
    /// <summary>Viewer to agent: stop the stream. Answered with <see cref="Ok"/>.</summary>
    public const string StopVideo = "stopVideo";
    /// <summary>Agent to viewer, unsolicited while a stream runs: one encoded frame in the payload; see <see cref="AgentMessage.Keyframe"/>, <see cref="AgentMessage.Timestamp"/>.</summary>
    public const string VideoFrame = "videoFrame";
    /// <summary>Agent to viewer, unsolicited: the stream ended on the agent's side (capture failed, session locked); <see cref="AgentMessage.Message"/> says why.</summary>
    public const string VideoStopped = "videoStopped";
}

/// <summary>Codec names used in <see cref="AgentMessage.Codecs"/> and <see cref="AgentMessage.Codec"/>.</summary>
public static class AgentVideoCodec
{
    /// <summary>Motion JPEG: every frame is a complete JPEG image of the whole screen. Needs no codec library on either side.</summary>
    public const string Jpeg = "jpeg";
    /// <summary>H.264 Annex B elementary stream; the first frame and every frame flagged as key frame carry SPS/PPS.</summary>
    public const string H264 = "h264";
    /// <summary>HEVC (H.265) Annex B elementary stream; the first frame and every key frame carry VPS/SPS/PPS. The macOS agent offers it after H.264.</summary>
    public const string Hevc = "hevc";
}

/// <summary>Values of <see cref="AgentMessage.Encoder"/> in a <see cref="AgentMessageType.VideoStarted"/> reply.</summary>
public static class AgentVideoEncoder
{
    public const string Hardware = "hardware";
    public const string Software = "software";
}

/// <summary>Values of <see cref="AgentMessage.Code"/> on an <see cref="AgentMessageType.Error"/> during the handshake.</summary>
public static class AgentErrorCode
{
    /// <summary>The proof did not match any known key or the token.</summary>
    public const string AuthFailed = "authFailed";
    /// <summary>The viewer presented a viewer id the agent has not paired with (or no longer has); it should pair again.</summary>
    public const string NotPaired = "notPaired";
    /// <summary>The person at the guest screen denied the pairing (or did not answer in time).</summary>
    public const string PairingDenied = "pairingDenied";
    /// <summary>The agent does not accept new pairings; use a token.</summary>
    public const string PairingDisabled = "pairingDisabled";
}

/// <summary>Optional capabilities announced in <see cref="AgentMessage.Features"/> of the welcome message.</summary>
public static class AgentFeature
{
    /// <summary>The agent accepts <see cref="AgentMessageType.Scroll"/> messages.</summary>
    public const string Scroll = "scroll";
    /// <summary>The agent can stream its screen (<see cref="AgentMessageType.StartVideo"/>); the welcome lists the <see cref="AgentMessage.Codecs"/>.</summary>
    public const string Video = "video";
    /// <summary>
    /// The agent accepts <see cref="AgentMessageType.Pointer"/> and <see cref="AgentMessageType.Key"/> messages, so a
    /// viewer can control the guest without a VNC server: picture from the stream, input through the agent.
    /// </summary>
    public const string Input = "input";
    /// <summary>The agent sends the pointer's shape (<see cref="AgentMessageType.Cursor"/>) with a stream that asks for it.</summary>
    public const string Cursor = "cursor";
}

/// <summary>Bits of <see cref="AgentMessage.Buttons"/> in a pointer message.</summary>
public static class AgentButton
{
    public const int Left = 1 << 0;
    public const int Middle = 1 << 1;
    public const int Right = 1 << 2;
    /// <summary>The first side button (XBUTTON1, "back" in browsers).</summary>
    public const int Back = 1 << 3;
    /// <summary>The second side button (XBUTTON2, "forward").</summary>
    public const int Forward = 1 << 4;

    public const int All = Left | Middle | Right | Back | Forward;
}

/// <summary>Values of <see cref="AgentMessage.Phase"/> in a scroll message.</summary>
public static class AgentScrollPhase
{
    /// <summary>First movement of a gesture (the message carries the first delta).</summary>
    public const string Begin = "begin";
    public const string Continue = "continue";
    /// <summary>The gesture is over; the deltas are zero.</summary>
    public const string End = "end";
}

/// <summary>Clipboard format names used in <see cref="AgentMessage.Format"/> and <see cref="AgentMessage.Formats"/>.</summary>
public static class AgentFormat
{
    /// <summary>UTF-8 plain text.</summary>
    public const string Text = "text";
    /// <summary>UTF-8 HTML fragment (no CF_HTML header).</summary>
    public const string Html = "html";
    /// <summary>A PNG image.</summary>
    public const string Png = "png";
    /// <summary>A set of files; see <see cref="AgentMessage.Files"/> and <see cref="FileBundle"/>.</summary>
    public const string Files = "files";

    public static readonly string[] All = { Text, Html, Png, Files };
}

/// <summary>One file in a <see cref="AgentFormat.Files"/> payload. Names are relative and use '/' as separator.</summary>
public sealed class AgentFileEntry
{
    public string Name { get; set; } = "";
    public long Size { get; set; }
}

/// <summary>
/// The JSON header of a frame. A single class covers every message type; fields that do not apply are null and omitted.
/// </summary>
public sealed class AgentMessage
{
    public const int ProtocolVersion = 1;

    public string Type { get; set; } = "";

    /// <summary>Correlates a request with its reply. Set by the requester; echoed in the reply.</summary>
    public int? Id { get; set; }

    public int? Version { get; set; }
    public string? Os { get; set; }
    public string? App { get; set; }

    /// <summary>Base64 random nonce (hello). Each side issues one so both sides can prove knowledge of the token.</summary>
    public string? Nonce { get; set; }
    /// <summary>Base64 HMAC-SHA256(token, peer nonce).</summary>
    public string? Proof { get; set; }

    public string[]? Formats { get; set; }
    /// <summary>Optional capabilities of the sender (welcome); see <see cref="AgentFeature"/>. Absent on old agents.</summary>
    public string[]? Features { get; set; }
    public string? Format { get; set; }
    public List<AgentFileEntry>? Files { get; set; }

    /// <summary>True in a <see cref="AgentMessageType.ClipboardData"/> reply when the requested format is no longer on the clipboard.</summary>
    public bool? Empty { get; set; }

    public string? Message { get; set; }
    /// <summary>Machine-readable reason on an error; see <see cref="AgentErrorCode"/>.</summary>
    public string? Code { get; set; }

    // ---- pairing
    /// <summary>Stable random id of the viewer installation (one per Windows user); public, used to look up the paired key.</summary>
    public string? ViewerId { get; set; }
    /// <summary>Human-readable name shown in the approval dialog, usually the viewer's machine name.</summary>
    public string? ViewerName { get; set; }
    /// <summary>Base64 SubjectPublicKeyInfo of an ECDH P-256 key (pair / paired).</summary>
    public string? PublicKey { get; set; }

    // ---- scroll
    /// <summary>Horizontal scroll in pixels; positive scrolls to the right (wheel tilted right, the view moves towards the right edge).</summary>
    public double? Dx { get; set; }
    /// <summary>Vertical scroll in pixels; positive scrolls up (wheel rotated away from the user), negative scrolls down.</summary>
    public double? Dy { get; set; }
    /// <summary>
    /// Scroll: pointer position in framebuffer pixels when the scroll happened (optional, informational). Pointer: the
    /// position in the viewer's picture, which is <see cref="Width"/>×<see cref="Height"/> pixels.
    /// </summary>
    public int? X { get; set; }
    public int? Y { get; set; }
    /// <summary>Gesture phase; see <see cref="AgentScrollPhase"/>.</summary>
    public string? Phase { get; set; }
    /// <summary>The same movement in Windows wheel units (120 per notch, same signs as <see cref="Dx"/>/<see cref="Dy"/>), when known.</summary>
    public double? WheelDx { get; set; }
    public double? WheelDy { get; set; }

    // ---- pointer, keys and cursor (AgentFeature.Input, AgentFeature.Cursor)
    /// <summary>Pointer: the buttons held down, a combination of <see cref="AgentButton"/> bits.</summary>
    public int? Buttons { get; set; }
    /// <summary>Key: set 1 make code, 0xE000 added for extended keys; 0 when the key has none (see <see cref="VirtualKey"/>).</summary>
    public int? ScanCode { get; set; }
    /// <summary>Key: Windows virtual-key code.</summary>
    public int? VirtualKey { get; set; }
    /// <summary>Key: true for a press (and auto-repeat), false for a release.</summary>
    public bool? Down { get; set; }
    /// <summary>Cursor: the hot spot (the pixel that points) within the shape.</summary>
    public int? HotX { get; set; }
    public int? HotY { get; set; }
    /// <summary>Cursor: false while the guest hides its pointer.</summary>
    public bool? Visible { get; set; }
    /// <summary>
    /// startVideo: also send the pointer's shape and visibility (<see cref="AgentMessageType.Cursor"/>) while the stream
    /// runs, for a viewer with no VNC connection to get them from. videoStarted: true when the agent will.
    /// </summary>
    public bool? Cursor { get; set; }

    // ---- video
    /// <summary>Welcome: codecs the agent can encode. startVideo: codecs the viewer can decode, most preferred first.</summary>
    public string[]? Codecs { get; set; }
    /// <summary>The codec in use (videoStarted, videoFrame); see <see cref="AgentVideoCodec"/>.</summary>
    public string? Codec { get; set; }
    /// <summary>Target bitrate in kbit/s (startVideo, setVideo, videoStarted).</summary>
    public int? Bitrate { get; set; }
    /// <summary>Frame rate cap (startVideo, setVideo, videoStarted).</summary>
    public int? MaxFps { get; set; }
    /// <summary>Encoder kind in use; see <see cref="AgentVideoEncoder"/>.</summary>
    public string? Encoder { get; set; }
    /// <summary>
    /// videoStarted and videoFrame: the frame size in pixels. startVideo and setVideo: the size in device pixels of the area
    /// the viewer shows the picture in (its window at fit, the framebuffer's size at 1:1). An agent that can scale before
    /// encoding sends a picture no larger than that, keeping the desktop's shape, so the viewer shows it 1:1 and no bits go
    /// to detail it would scale away. Absent or 0 means no limit in that direction; setVideo without either leaves the
    /// limit as it is. Agents that cannot scale, and agents older than this field, ignore it.
    /// Pointer: the size of the picture <see cref="X"/>/<see cref="Y"/> refer to. Cursor: the size of the shape.
    /// </summary>
    public int? Width { get; set; }
    public int? Height { get; set; }
    /// <summary>
    /// Where the frame sits in the desktop, in pixels from the desktop's top-left corner (videoStarted, videoFrame).
    /// Present only when the frames show part of the desktop, such as the primary display of a multi-monitor desktop
    /// too large to encode as one H.264 picture; absent when they show the whole desktop. The viewer paints such frames
    /// onto that part of its framebuffer and keeps the RFB picture for the rest.
    /// </summary>
    public int? OffsetX { get; set; }
    public int? OffsetY { get; set; }
    /// <summary>True on a frame that can be decoded on its own (always true for JPEG).</summary>
    public bool? Keyframe { get; set; }
    /// <summary>Capture time of a frame in microseconds since the stream started.</summary>
    public long? Timestamp { get; set; }
    /// <summary>Frame counter since the stream started; gaps mean the agent dropped frames it could not send in time.</summary>
    public int? Sequence { get; set; }
    /// <summary>setVideo: ask for a key frame on the next opportunity.</summary>
    public bool? KeyframeRequest { get; set; }

    public static AgentMessage Of(string type, int? id = null) => new() { Type = type, Id = id };

    public static AgentMessage ErrorReply(int? id, string message) => new() { Type = AgentMessageType.Error, Id = id, Message = message };

    [JsonIgnore]
    public long TotalFileBytes => Files?.Sum(f => f.Size) ?? 0;
}
