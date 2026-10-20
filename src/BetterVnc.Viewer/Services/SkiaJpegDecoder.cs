using BetterVnc.Protocol.Decoders;
using SkiaSharp;

namespace BetterVnc.Viewer.Services;

/// <summary>Decodes Tight JPEG rectangles and motion JPEG frames with SkiaSharp (libjpeg-turbo), which ships with Avalonia.</summary>
public sealed class SkiaJpegDecoder : IJpegDecoder
{
    public unsafe byte[] DecodeToBgrx(ReadOnlySpan<byte> jpeg, int width, int height)
    {
        using var data = SKData.CreateCopy(jpeg);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("The JPEG data cannot be read.");
        var size = codec.Info;
        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var result = new byte[width * height * 4];
        if (size.Width == width && size.Height == height)
        {
            fixed (byte* d = result) Check(codec.GetPixels(info, (IntPtr)d));
            return result;
        }
        // A picture of another size than announced: keep the part that fits, as the WIC decoder did.
        var whole = new byte[size.Width * size.Height * 4];
        fixed (byte* d = whole) Check(codec.GetPixels(info, (IntPtr)d));
        int rowBytes = Math.Min(width, size.Width) * 4;
        for (int y = 0; y < Math.Min(height, size.Height); y++)
            Buffer.BlockCopy(whole, y * size.Width * 4, result, y * width * 4, rowBytes);
        return result;
    }

    private static void Check(SKCodecResult result)
    {
        // A truncated picture still fills what arrived, like any decoder would show it.
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            throw new InvalidDataException($"JPEG decode failed: {result}");
    }
}

/// <summary>PNG encoding with SkiaSharp: screenshots, for the clipboard and for files.</summary>
public static class PngEncoder
{
    /// <summary>Encodes a BGRA picture (width*4 bytes per row, opaque) as PNG.</summary>
    public static unsafe byte[] Encode(byte[] bgra, int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        fixed (byte* p = bgra)
        {
            using var image = SKImage.FromPixels(info, (IntPtr)p, width * 4) ?? throw new ArgumentException("Not a picture of that size.");
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
    }
}
