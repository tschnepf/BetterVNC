using BetterVnc.Media;
using Xunit;

namespace BetterVnc.Media.Tests;

public class PointerShapeTests
{
    private const uint Transparent = 0, Black = 0xFF000000, White = 0xFFFFFFFF;

    private static uint Pixel(PointerShape s, int x, int y) => BitConverter.ToUInt32(s.Bgra, (y * s.Width + x) * 4);

    /// <summary>One row of a 1-bit plane from a string: '1' sets the bit, anything else clears it; rows padded to <paramref name="pitch"/>.</summary>
    private static byte[] Plane(int pitch, params string[] rows)
    {
        var bytes = new byte[pitch * rows.Length];
        for (int y = 0; y < rows.Length; y++)
            for (int x = 0; x < rows[y].Length; x++)
                if (rows[y][x] == '1') bytes[y * pitch + (x >> 3)] |= (byte)(0x80 >> (x & 7));
        return bytes;
    }

    [Fact]
    public void Monochrome_pointer_maps_each_and_xor_pair_and_outlines_the_inverted_pixels()
    {
        // 4x3: AND mask rows, then XOR mask rows. Column 0 black, column 1 white, column 2 inverted, column 3 transparent.
        var planes = Plane(2,
            "0011", "0011", "0011",   // AND
            "0110", "0110", "0110");  // XOR
        var s = PointerShape.FromMonochrome(planes, 4, 3, 2, hotX: 2, hotY: 1);
        Assert.Equal((4, 3, 2, 1), (s.Width, s.Height, s.HotX, s.HotY));
        for (int y = 0; y < 3; y++)
        {
            Assert.Equal(Black, Pixel(s, 0, y));
            Assert.Equal(White, Pixel(s, 1, y));
            Assert.Equal(Black, Pixel(s, 2, y));  // inverts the screen in Windows: black here...
            Assert.Equal(White, Pixel(s, 3, y));  // ...with a white edge next to it where it was transparent
        }
    }

    [Fact]
    public void An_inverting_i_beam_stays_visible_on_any_background()
    {
        // The text cursor: a vertical bar of inverting pixels on a transparent field.
        var planes = Plane(1,
            "111", "111", "111",
            "010", "010", "010");
        var s = PointerShape.FromMonochrome(planes, 3, 3, 1, 1, 1);
        for (int y = 0; y < 3; y++) Assert.Equal(new[] { White, Black, White }, new[] { Pixel(s, 0, y), Pixel(s, 1, y), Pixel(s, 2, y) });
    }

    [Fact]
    public void Masked_colour_draws_opaque_pixels_drops_black_xor_and_outlines_inverting_ones()
    {
        // 3x2, BGRA with the alpha byte as mask: 0 = draw the colour, 0xFF = XOR it.
        uint[] px =
        {
            0x00336699, 0xFF000000, 0xFF000000,   // opaque colour, then XOR black (no change) twice
            0xFF000000, 0xFF000000, 0xFFFFFFFF,   // XOR black, XOR black, XOR white (inverts)
        };
        var bytes = new byte[px.Length * 4];
        Buffer.BlockCopy(px, 0, bytes, 0, bytes.Length);
        var s = PointerShape.FromMaskedColor(bytes, 3, 2, 12, 0, 0);
        Assert.Equal(0xFF336699, Pixel(s, 0, 0));
        Assert.Equal(Transparent, Pixel(s, 1, 0));
        Assert.Equal(White, Pixel(s, 2, 0));       // above the inverting pixel
        Assert.Equal(Transparent, Pixel(s, 0, 1)); // not next to it
        Assert.Equal(White, Pixel(s, 1, 1));       // left of it
        Assert.Equal(Black, Pixel(s, 2, 1));
    }

    [Fact]
    public void Colour_pointer_keeps_its_alpha_and_drops_the_row_padding()
    {
        var bytes = new byte[2 * 12];
        bytes[0] = 10; bytes[1] = 20; bytes[2] = 30; bytes[3] = 128;   // (0,0) half transparent
        bytes[12 + 4 + 3] = 255;                                       // (1,1) opaque black
        bytes[8] = 0xEE;                                               // padding after the 2 pixels of row 0
        var s = PointerShape.FromColor(bytes, 2, 2, 12, 5, 5);
        Assert.Equal(new byte[] { 10, 20, 30, 128, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 255 }, s.Bgra);
        Assert.Equal((1, 1), (s.HotX, s.HotY)); // the hot spot stays inside the shape
    }

    [Fact]
    public void Gdi_colour_cursor_without_alpha_uses_its_mask()
    {
        uint[] color = { 0x00FF0000, 0x00000000, 0x00FFFFFF };
        var bytes = new byte[12];
        Buffer.BlockCopy(color, 0, bytes, 0, 12);
        var mask = Plane(2, "011");
        var s = PointerShape.FromColorAndMask(bytes, 12, mask, 2, 3, 1, 0, 0);
        Assert.Equal(new[] { 0xFFFF0000, White, Black }, new[] { Pixel(s, 0, 0), Pixel(s, 1, 0), Pixel(s, 2, 0) });
    }

    [Fact]
    public void Data_shorter_than_its_size_is_refused()
    {
        Assert.Throws<ArgumentException>(() => PointerShape.FromColor(new byte[15], 2, 2, 8, 0, 0));
        Assert.Throws<ArgumentException>(() => PointerShape.FromMonochrome(new byte[3], 8, 2, 1, 0, 0));
    }
}
