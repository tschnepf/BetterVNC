namespace BetterVnc.Viewer.Core.Models;

/// <summary>
/// Where the picture comes from and how it is compressed. The VNC modes change the Tight pseudo-encodings on the live RFB
/// connection; the stream modes take the picture from the guest agent's video channel while RFB keeps carrying input,
/// cursor shapes and the fallback picture.
/// </summary>
public enum PictureMode
{
    /// <summary>Lossless Tight / ZRLE: exact pixels, heavy when the whole screen moves. Always available.</summary>
    VncLossless,
    /// <summary>Tight JPEG quality 9, compression 1.</summary>
    VncHigh,
    /// <summary>Tight JPEG quality 6, compression 2.</summary>
    VncMedium,
    /// <summary>Tight JPEG quality 3, compression 6.</summary>
    VncLow,
    /// <summary>Agent video stream at a high bitrate (sharp text, 60 fps).</summary>
    StreamQuality,
    /// <summary>Agent video stream at a mid bitrate.</summary>
    StreamBalanced,
    /// <summary>Agent video stream at a low bitrate and frame rate for slow links.</summary>
    StreamLow,
}

/// <summary>Static description of every <see cref="PictureMode"/>: label, tooltip, and the parameters each side needs.</summary>
public static class PictureModes
{
    public sealed record Info(
        PictureMode Mode,
        string Label,
        string Description,
        bool IsStream,
        int JpegQuality,
        int CompressionLevel,
        int BitrateKbps,
        int MaxFps)
    {
        public bool IsLossy => !IsStream && JpegQuality >= 0;
    }

    public static readonly Info[] All =
    {
        new(PictureMode.VncLossless, "VNC lossless", "Exact pixels over VNC. Best on a LAN; costly when the whole screen moves.", false, -1, 1, 0, 0),
        new(PictureMode.VncHigh, "VNC compressed, high", "Tight JPEG quality 9 over VNC. Needs a server that honours lossy requests (QEMU with lossy=on).", false, 9, 1, 0, 0),
        new(PictureMode.VncMedium, "VNC compressed, medium", "Tight JPEG quality 6 over VNC.", false, 6, 2, 0, 0),
        new(PictureMode.VncLow, "VNC compressed, low bandwidth", "Tight JPEG quality 3 with maximum zlib compression over VNC.", false, 3, 6, 0, 0),
        new(PictureMode.StreamQuality, "Stream, quality", "Video stream from the guest agent at about 20 Mbit/s, 60 fps. Lossless VNC repaint whenever the screen is still.", true, -1, 1, 20_000, 60),
        new(PictureMode.StreamBalanced, "Stream, balanced", "Video stream from the guest agent at about 8 Mbit/s, 60 fps. Lossless VNC repaint whenever the screen is still.", true, -1, 1, 8_000, 60),
        new(PictureMode.StreamLow, "Stream, low bandwidth", "Video stream from the guest agent at about 2.5 Mbit/s, 30 fps. Lossless VNC repaint whenever the screen is still.", true, -1, 1, 2_500, 30),
    };

    public static Info Get(PictureMode mode) => All.FirstOrDefault(i => i.Mode == mode) ?? All[0];

    /// <summary>The mode an older profile's JPEG quality setting corresponds to.</summary>
    public static PictureMode FromLegacy(int jpegQuality) => jpegQuality switch
    {
        < 0 => PictureMode.VncLossless,
        >= 9 => PictureMode.VncHigh,
        >= 6 => PictureMode.VncMedium,
        _ => PictureMode.VncLow,
    };

    /// <summary>The VNC quality to use while a stream mode is showing (or when it cannot): lossless, so idle repaints are exact.</summary>
    public static readonly Info StreamFallback = All[0];
}
