using BetterVnc.Agent.Protocol;
using BetterVnc.Media;
using BetterVnc.Protocol;

namespace BetterVnc.Viewer.Core.Services;

/// <summary>
/// The guest agent's video channel as <see cref="VideoStreamPlayer"/> drives it: <see cref="AgentClient"/>, behind an
/// interface so the player can be tested without an agent.
/// </summary>
public interface IAgentStream
{
    /// <summary>Parameters of the running stream, its size following the frames; null when none runs.</summary>
    AgentConnection.VideoStartInfo? StreamInfo { get; }

    /// <summary>
    /// An encoded frame arrived, or a repair of the still screen (<see cref="AgentMessageType.VideoRepair"/> as the
    /// header's type) for a stream started with repairs; raised on the agent's receive thread, so decode elsewhere and
    /// return quickly.
    /// </summary>
    event Action<AgentFrame>? StreamFrame;
    /// <summary>The stream ended without this side asking (agent stopped it, or disconnected); on the UI thread.</summary>
    event Action<string?>? StreamStopped;

    /// <param name="area">Where the picture is shown, in device pixels (see <see cref="AgentMessage.Width"/>); null for no limit.</param>
    /// <param name="cursor">Also have the agent send the pointer's shape: for a session without VNC.</param>
    /// <param name="repair">Have the agent repair the still screen with exact pixels when it can (the result's <see cref="AgentConnection.VideoStartInfo.Repair"/> says).</param>
    Task<AgentConnection.VideoStartInfo> StartStreamAsync(int bitrateKbps, int maxFps, (int Width, int Height)? area = null, bool cursor = false, bool repair = false);
    /// <summary>The area the picture is shown in changed (device pixels); an agent that can scale encodes no larger than that.</summary>
    Task SetStreamAreaAsync(int width, int height);
    Task SetStreamAsync(int? bitrateKbps, int? maxFps, bool keyframe = false);
    Task StopStreamAsync();
}

/// <summary>
/// The RFB side of the picture while the agent streams: <see cref="VncClient"/> (see <see cref="VncClientPicture"/>),
/// behind an interface so the player can be tested without a server.
/// </summary>
public interface IRfbPicture
{
    bool IsConnected { get; }
    /// <summary>How often framebuffer updates are requested; zero is as fast as they come.</summary>
    TimeSpan UpdateInterval { get; set; }
    /// <summary>Asks for a full (non-incremental) update, so the whole picture is exact again.</summary>
    void RefreshPicture();
}

/// <summary>A <see cref="VncClient"/> as the stream player's <see cref="IRfbPicture"/>.</summary>
public sealed class VncClientPicture : IRfbPicture
{
    private readonly VncClient _client;

    public VncClientPicture(VncClient client) => _client = client;

    public bool IsConnected => _client.IsConnected;
    public TimeSpan UpdateInterval { get => _client.UpdateInterval; set => _client.UpdateInterval = value; }
    public void RefreshPicture() => _client.RefreshPicture();
}

/// <summary>
/// Where the stream's pictures go: the session's picture surface (the WPF viewer's VncView), which shows either the RFB
/// framebuffer or the stream. Its video backend decodes H.264 on its own GPU device where it can, so hardware pictures
/// reach the screen without leaving the GPU.
/// </summary>
public interface IVideoSurface
{
    /// <summary>
    /// The area the picture is shown in, in device pixels; null while there is none. A guest agent that can scale sends a
    /// stream no larger than this. UI thread.
    /// </summary>
    (int Width, int Height)? PictureArea { get; }

    /// <summary><see cref="PictureArea"/> may have changed; raised on the UI thread, often several times in a row.</summary>
    event Action? PictureAreaChanged;

    /// <summary>How streamed frames reach the screen: the GPU surface and its adapter, or why they do not.</summary>
    string GpuVideoStatus { get; }

    /// <summary>True while GPU presentation is set up, so hardware-decoded pictures can be shown. Any thread.</summary>
    bool HasGpuVideo { get; }

    /// <summary>What draws frames when the GPU surface does not, for the status bar ("WPF").</summary>
    string CpuPathName { get; }

    /// <summary>Sets up GPU presentation; false when it is unavailable (frames then go through the CPU path). UI thread.</summary>
    bool EnableGpuVideo();

    /// <summary>Shows the RFB framebuffer instead of the stream. UI thread.</summary>
    void ShowFramebuffer();

    /// <summary>
    /// An H.264 decoder for this surface: on its GPU device when <paramref name="hardware"/> and GPU presentation is set
    /// up, falling back to software by itself. Called on the decode thread.
    /// </summary>
    IH264Decoder CreateH264Decoder(bool hardware);

    /// <summary>
    /// Shows a decoded picture through the GPU surface, from the decoder's frame callback (the decode thread). False when
    /// the GPU path is not set up or refused it; the caller then converts the frame and uses <see cref="PresentExternalFrame"/>.
    /// <paramref name="offset"/> is where the frame sits in the desktop when it shows only part of it.
    /// </summary>
    bool PresentVideoFrame(in DecodedFrame frame, (int X, int Y)? offset = null);

    /// <summary>The same for a BGRX picture (width*4 bytes per row): a motion JPEG frame.</summary>
    bool PresentVideoFrame(byte[] bgrx, int width, int height, (int X, int Y)? offset = null);

    /// <summary>
    /// Shows a BGRX picture through the CPU path, from any thread; <paramref name="release"/> gets the buffer back once it
    /// has been copied.
    /// </summary>
    void PresentExternalFrame(byte[] bgrx, int width, int height, Action<byte[]>? release = null, (int X, int Y)? offset = null);

    /// <summary>The pointer's shape from the guest agent, for sessions without VNC. Any thread.</summary>
    void SetRemoteCursor(CursorShape shape);

    /// <summary>Hides the pointer over the picture while the remote hides its own (typing, a game). Any thread.</summary>
    void SetRemoteCursorVisible(bool visible);
}

/// <summary>
/// Something that can scroll the remote machine by pixels (the guest agent). <paramref name="dx"/>/<paramref name="dy"/>
/// are pixels, positive = right / up; <paramref name="wheelDx"/>/<paramref name="wheelDy"/> the same movement in Windows
/// wheel units (120 per notch). Returns false when it cannot take the event right now, in which case the caller falls back
/// to VNC wheel clicks.
/// </summary>
public interface IScrollInjector
{
    bool TrySendScroll(double dx, double dy, int x, int y, string phase, double wheelDx, double wheelDy);
}

/// <summary>
/// Keyboard and mouse for a session without a VNC server, injected by the guest agent. Pointer positions are pixels in
/// the picture the view shows (the stream's frames), which is <c>width</c>×<c>height</c>; buttons are
/// <see cref="AgentButton"/> bits. Keys carry the set 1 scan code (0xE000 added for extended keys, 0 for none) and the
/// virtual key. Both return false when the agent cannot take the event right now.
/// </summary>
public interface IAgentInput
{
    bool TrySendPointer(int x, int y, int width, int height, int buttons);
    bool TrySendKey(int scanCode, int virtualKey, bool down);
}
