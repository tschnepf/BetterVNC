namespace BetterVnc.Agent.Protocol;

/// <summary>What a viewer asked for in <see cref="AgentMessageType.StartVideo"/>, after the server picked a codec.</summary>
public sealed class VideoRequest
{
    public required string Codec { get; init; }
    public int BitrateKbps { get; init; } = 8_000;
    public int MaxFps { get; init; } = 60;
}

/// <summary>One encoded frame produced by an <see cref="IVideoSession"/>.</summary>
/// <param name="Data">Encoded bytes; the receiver copies them before the callback returns if it needs them longer.</param>
/// <param name="Keyframe">Decodable on its own (always true for JPEG).</param>
/// <param name="TimestampMicros">Capture time in microseconds since the session started.</param>
/// <param name="Width">Frame size; may change when the guest changes resolution.</param>
public readonly record struct VideoFrame(ReadOnlyMemory<byte> Data, bool Keyframe, long TimestampMicros, int Width, int Height);

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
    /// <summary>See <see cref="AgentVideoEncoder"/>.</summary>
    string EncoderKind { get; }

    /// <summary>Changes the bitrate and/or frame-rate cap on the fly; null leaves a value as it is.</summary>
    void Set(int? bitrateKbps, int? maxFps);

    /// <summary>Makes the next frame a key frame (and for sources that skip unchanged frames: sends one even if nothing changed).</summary>
    void RequestKeyframe();
}
