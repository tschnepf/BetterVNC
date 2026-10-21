namespace BetterVnc.Agent.Protocol;

/// <summary>What a viewer asked for in <see cref="AgentMessageType.StartVideo"/>, after the server picked a codec.</summary>
public sealed class VideoRequest
{
    public required string Codec { get; init; }
    public int BitrateKbps { get; init; } = 8_000;
    public int MaxFps { get; init; } = 60;
    /// <summary>
    /// The viewer's picture area in device pixels (see <see cref="IVideoSession.SetMaxSize"/>); 0 = no limit in that
    /// direction, which is also what viewers that do not send a size get.
    /// </summary>
    public int MaxWidth { get; init; }
    public int MaxHeight { get; init; }
    /// <summary>Send repairs: exact pixels of what the lossy frames showed, once the screen is still (see <see cref="VideoFrame.Repair"/>).</summary>
    public bool Repair { get; init; }
}

/// <summary>One encoded frame produced by an <see cref="IVideoSession"/>.</summary>
/// <param name="Data">Encoded bytes; the receiver copies them before the callback returns if it needs them longer.</param>
/// <param name="Keyframe">Decodable on its own (always true for JPEG).</param>
/// <param name="TimestampMicros">Capture time in microseconds since the session started.</param>
/// <param name="Width">Frame size; may change when the guest changes resolution.</param>
/// <param name="OffsetX">Position of the frame in the desktop when it shows only part of it (see <see cref="IVideoSession.Offset"/>); null for the whole desktop.</param>
/// <param name="Repair">
/// Not a frame but a repair (<see cref="AgentMessageType.VideoRepair"/>): <paramref name="Data"/> is a <see cref="RepairPayload"/>
/// in the frames' coordinates, and <paramref name="Keyframe"/> says it covers the whole picture.
/// </param>
public readonly record struct VideoFrame(ReadOnlyMemory<byte> Data, bool Keyframe, long TimestampMicros, int Width, int Height, int? OffsetX = null, int? OffsetY = null, bool Repair = false);

/// <summary>A pointer image: <paramref name="Width"/>×<paramref name="Height"/> pixels of B, G, R, A with straight alpha, top row first.</summary>
public sealed record CursorImage(int Width, int Height, int HotX, int HotY, byte[] Bgra);

/// <summary>The guest pointer as a video source saw it: whether it shows, and its image when that changed (null: as before).</summary>
public sealed record GuestCursor(bool Visible, CursorImage? Shape);

/// <summary>
/// Screen capture plus encoder on the machine the agent runs on. Implementations are OS specific; an agent without one
/// simply does not announce <see cref="AgentFeature.Video"/>. One source serves any number of sessions.
/// </summary>
public interface IGuestVideoSource
{
    /// <summary>Codecs this source can produce, most preferred first; see <see cref="AgentVideoCodec"/>.</summary>
    IReadOnlyList<string> Codecs { get; }

    /// <summary>
    /// Starts capturing and encoding. <paramref name="onFrame"/> is called from a capture thread for every encoded frame and
    /// must return quickly (the server queues and drops frames itself). <paramref name="onEnded"/> is called once if the
    /// session dies on its own (capture failed, desktop locked), with a reason; it is not called after <see cref="IDisposable.Dispose"/>.
    /// Throws when capturing is impossible right now; the message is sent to the viewer.
    /// </summary>
    IVideoSession Start(VideoRequest request, Action<VideoFrame> onFrame, Action<string?> onEnded);

    /// <summary>True when <see cref="Start(VideoRequest, Action{VideoFrame}, Action{string?}, Action{GuestCursor}?)"/> reports the pointer; the server then announces <see cref="AgentFeature.Cursor"/>.</summary>
    bool SupportsCursor => false;

    /// <summary>
    /// Like <see cref="Start(VideoRequest, Action{VideoFrame}, Action{string?})"/>, and reports the pointer through
    /// <paramref name="onCursor"/> (from the capture thread): first soon after the start, with its shape, then whenever
    /// the shape or its visibility changes. Sources without <see cref="SupportsCursor"/> ignore it.
    /// </summary>
    IVideoSession Start(VideoRequest request, Action<VideoFrame> onFrame, Action<string?> onEnded, Action<GuestCursor>? onCursor) =>
        Start(request, onFrame, onEnded);

    /// <summary>True when some of its sessions can send repairs (<see cref="VideoRequest.Repair"/>); the server then announces <see cref="AgentFeature.Repair"/>.</summary>
    bool SupportsRepair => false;
}

/// <summary>A running stream. Disposing stops capture and encoding; no frames arrive after Dispose returns.</summary>
public interface IVideoSession : IDisposable
{
    string Codec { get; }
    int BitrateKbps { get; }
    int MaxFps { get; }
    /// <summary>Size of the frames at start; frames carry their own size afterwards.</summary>
    int Width { get; }
    int Height { get; }
    /// <summary>
    /// Where the frames sit in the desktop (pixels from its top-left corner) when they show only part of it, such as the
    /// primary display of a desktop too large to encode as one picture; null when they show the whole desktop.
    /// </summary>
    (int X, int Y)? Offset => null;
    /// <summary>
    /// The part of the guest's screen the frames show, in the OS's screen coordinates; pointer messages are mapped onto
    /// it. Null when the source cannot say, and pointer messages then map onto the whole desktop.
    /// </summary>
    ScreenArea? Area => null;
    /// <summary>See <see cref="AgentVideoEncoder"/>.</summary>
    string EncoderKind { get; }
    /// <summary>True when this session sends repairs: it was asked to, and its codec and capture allow it.</summary>
    bool Repairs => false;

    /// <summary>Changes the bitrate and/or frame-rate cap on the fly; null leaves a value as it is.</summary>
    void Set(int? bitrateKbps, int? maxFps);

    /// <summary>
    /// The viewer's picture area changed (its window was resized, or it switched between fit and 1:1); 0 = no limit in that
    /// direction. A source that can scale before encoding (the macOS helper, on the GPU) then encodes at most this large,
    /// keeping the desktop's shape, so the viewer shows the picture 1:1. Frames carry their new size. Sources that cannot
    /// scale ignore it, which is the default.
    /// </summary>
    void SetMaxSize(int maxWidth, int maxHeight) { }

    /// <summary>
    /// Makes the next frame a key frame (and for sources that skip unchanged frames: sends one even if nothing changed).
    /// A session that sends repairs then repairs the whole picture once the screen is still.
    /// </summary>
    void RequestKeyframe();
}
