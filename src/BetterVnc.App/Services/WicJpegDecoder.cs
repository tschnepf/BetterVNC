using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterVnc.Protocol.Decoders;

namespace BetterVnc.App.Services;

/// <summary>Decodes Tight JPEG rectangles with Windows Imaging Component.</summary>
public sealed class WicJpegDecoder : IJpegDecoder
{
    public byte[] DecodeToBgrx(ReadOnlySpan<byte> jpeg, int width, int height)
    {
        using var ms = new MemoryStream(jpeg.ToArray());
        var decoder = new JpegBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        if (frame.Format != PixelFormats.Bgr32)
            frame = new FormatConvertedBitmap(frame, PixelFormats.Bgr32, null, 0);
        var result = new byte[width * height * 4];
        int w = Math.Min(width, frame.PixelWidth);
        int h = Math.Min(height, frame.PixelHeight);
        frame.CopyPixels(new System.Windows.Int32Rect(0, 0, w, h), result, width * 4, 0);
        return result;
    }
}
