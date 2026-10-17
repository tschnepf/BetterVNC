namespace BetterVnc.Protocol;

/// <summary>RFB PIXEL_FORMAT structure (16 bytes on the wire).</summary>
public readonly record struct PixelFormat(
    byte BitsPerPixel,
    byte Depth,
    bool BigEndian,
    bool TrueColour,
    ushort RedMax,
    ushort GreenMax,
    ushort BlueMax,
    byte RedShift,
    byte GreenShift,
    byte BlueShift)
{
    /// <summary>
    /// The only format BetterVNC renders with: 32bpp, depth 24, little-endian, R shift 16, G 8, B 0.
    /// In memory that is B,G,R,X per pixel, which is WPF's Bgr32 / GDI's 32-bit DIB layout.
    /// </summary>
    public static readonly PixelFormat Bgrx32 = new(32, 24, false, true, 255, 255, 255, 16, 8, 0);

    public int BytesPerPixel => BitsPerPixel / 8;

    public static PixelFormat Read(RfbReader r)
    {
        var bpp = r.ReadU8();
        var depth = r.ReadU8();
        var bigEndian = r.ReadU8() != 0;
        var trueColour = r.ReadU8() != 0;
        var redMax = r.ReadU16();
        var greenMax = r.ReadU16();
        var blueMax = r.ReadU16();
        var redShift = r.ReadU8();
        var greenShift = r.ReadU8();
        var blueShift = r.ReadU8();
        r.Skip(3);
        return new PixelFormat(bpp, depth, bigEndian, trueColour, redMax, greenMax, blueMax, redShift, greenShift, blueShift);
    }

    public void Write(RfbMessageBuilder b)
    {
        b.U8(BitsPerPixel).U8(Depth).U8(BigEndian ? 1 : 0).U8(TrueColour ? 1 : 0)
         .U16(RedMax).U16(GreenMax).U16(BlueMax)
         .U8(RedShift).U8(GreenShift).U8(BlueShift)
         .Pad(3);
    }

    public override string ToString() =>
        $"{BitsPerPixel}bpp depth {Depth} {(BigEndian ? "BE" : "LE")} rgb max {RedMax}/{GreenMax}/{BlueMax} shift {RedShift}/{GreenShift}/{BlueShift}";
}
