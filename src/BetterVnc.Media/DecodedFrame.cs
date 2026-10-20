namespace BetterVnc.Media;

/// <summary>
/// A decoded picture in NV12: either in system memory (<see cref="Y"/>, <see cref="UV"/>) or, from a hardware decoder,
/// a picture on the GPU (<see cref="IsTexture"/>) that only the video backend which decoded it can show.
/// Pointers and textures are valid only inside the callback that delivers the frame.
/// </summary>
public readonly struct DecodedFrame
{
    public DecodedFrame(IntPtr y, IntPtr uv, int stride, int width, int height, long timestampMicros)
    {
        Y = y; UV = uv; Stride = stride; Width = width; Height = height; TimestampMicros = timestampMicros;
    }

    /// <summary>A GPU picture: <paramref name="texture"/> is a handle owned by the backend that decoded it.</summary>
    public DecodedFrame(IntPtr texture, uint subresource, int offsetX, int offsetY, int width, int height, long timestampMicros)
    {
        Texture = texture; Subresource = subresource; OffsetX = offsetX; OffsetY = offsetY;
        Width = width; Height = height; TimestampMicros = timestampMicros;
    }

    /// <summary>First visible luma byte (zero for a texture frame).</summary>
    public IntPtr Y { get; }
    /// <summary>First visible interleaved chroma pair (zero for a texture frame).</summary>
    public IntPtr UV { get; }
    public int Stride { get; }
    public int Width { get; }
    public int Height { get; }
    public long TimestampMicros { get; }

    /// <summary>The picture lives on the GPU (hardware decode); <see cref="ToBgrx"/> is not available.</summary>
    public bool IsTexture => Texture != IntPtr.Zero;
    /// <summary>
    /// The backend's handle to the GPU picture and the slice holding it. On Windows an ID3D11Texture2D (NV12, often a
    /// texture array the decoder owns).
    /// </summary>
    public IntPtr Texture { get; }
    public uint Subresource { get; }
    /// <summary>Where the visible picture starts inside the texture (the decoder's surfaces are padded to whole macroblocks).</summary>
    public int OffsetX { get; }
    public int OffsetY { get; }

    /// <summary>Writes the picture as BGRX, Width*4 bytes per row, into <paramref name="bgrx"/> (at least Width*Height*4 bytes).</summary>
    public unsafe void ToBgrx(byte[] bgrx)
    {
        if (IsTexture) throw new InvalidOperationException("The picture is a GPU texture; present it through the backend that decoded it.");
        if (bgrx.Length < Width * Height * 4) throw new ArgumentException("Buffer too small.");
        fixed (byte* d = bgrx)
            PixelConvert.Nv12ToBgrx((byte*)Y, Stride, (byte*)UV, Stride, Width, Height, d, Width * 4);
    }
}

/// <summary>Receives each picture a decoder delivers; the frame is valid only during the call.</summary>
public delegate void DecodedFrameHandler(in DecodedFrame frame);

/// <summary>
/// An H.264 decoder of some video backend (Media Foundation on Windows). Every access unit fed in comes out as a picture
/// right away; resolution changes mid-stream are followed. All methods must be called from one thread.
/// </summary>
public interface IH264Decoder : IDisposable
{
    /// <summary>The decoder's name, for logs and the status bar.</summary>
    string Name { get; }
    /// <summary>The decoder runs on the GPU and delivers texture frames.</summary>
    bool IsHardware { get; }
    /// <summary>Why the GPU was asked for but decoding runs in software; null otherwise.</summary>
    string? HardwareFailure { get; }

    /// <summary>Decodes one Annex B access unit; returns the number of pictures delivered to <paramref name="onFrame"/>.</summary>
    int Decode(ReadOnlySpan<byte> annexB, long timestampMicros, DecodedFrameHandler onFrame);
}
