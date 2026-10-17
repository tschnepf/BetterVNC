namespace BetterVnc.Protocol.Decoders;

/// <summary>
/// Decodes a JPEG image (from the Tight encoding) to tightly packed 32-bit B,G,R,X pixels.
/// The protocol library has no image codec of its own; the host application supplies one
/// (the WPF app uses Windows Imaging Component).
/// </summary>
public interface IJpegDecoder
{
    /// <summary>Returns width*height*4 bytes in B,G,R,X order.</summary>
    byte[] DecodeToBgrx(ReadOnlySpan<byte> jpeg, int width, int height);
}
