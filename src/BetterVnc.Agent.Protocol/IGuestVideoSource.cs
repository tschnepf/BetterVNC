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
}

/// <summary>One encoded frame produced by an <see cref="IVideoSession"/>.</summary>
/// <param name="Data">Encoded bytes; the receiver copies them before the callback returns if it needs them longer.</param>
/// <param name="Keyframe">Decodable on its own (always true for JPEG).</param>
/// <param name="TimestampMicros">Capture time in microseconds since the session started.</param>
/// <param name="Width">Frame size; may change when the guest changes resolution.</param>
/// <param name="OffsetX">Position of the frame in the desktop when it shows only part of it (see <see cref="IVideoSession.Offset"/>); null for the whole desktop.</param>
public readonly record struct VideoFrame(ReadOnlyMemory<byte> Data, bool Keyframe, long TimestampMicros, int Width, int Height, int? OffsetX = null, int? OffsetY = null);

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
    /// <summary>See <see cref="AgentVideoEncoder"/>.</summary>
    string EncoderKind { get; }

    /// <summary>Changes the bitrate and/or frame-rate cap on the fly; null leaves a value as it is.</summary>
    void Set(int? bitrateKbps, int? maxFps);

    /// <summary>
    /// The viewer's picture area changed (its window was resized, or it switched between fit and 1:1); 0 = no limit in that
    /// direction. A source that can scale before encoding (the macOS helper, on the GPU) then encodes at most this large,
    /// keeping the desktop's shape, so the viewer shows the picture 1:1. Frames carry their new size. Sources that cannot
    /// scale ignore it, which is the default.
    /// </summary>
    void SetMaxSize(int maxWidth, int maxHeight) { }

    /// <summary>Makes the next frame a key frame (and for sources that skip unchanged frames: sends one even if nothing changed).</summary>
    void RequestKeyframe();
}
