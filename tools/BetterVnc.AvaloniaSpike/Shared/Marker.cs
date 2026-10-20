namespace BetterVnc.AvaloniaSpike;

/// <summary>
/// A frame number drawn into a picture as a row of black/white blocks, so a screen capture can tell which frame the
/// screen shows: [white][black] sentinels, then 16 bits, most significant first. Blocks are <see cref="Block"/> pixels.
/// </summary>
internal static class Marker
{
    public const int Bits = 16;
    public const int Blocks = Bits + 2;

    /// <summary>Block size for a picture of this width: 18 blocks take about 45% of the width.</summary>
    public static int BlockFor(int pictureWidth) => Math.Max(8, pictureWidth / 40);

    public static int Width(int block) => Blocks * block;

    /// <summary>Draws <paramref name="value"/> into a BGRA picture at its top-left corner.</summary>
    public static void Draw(byte[] bgra, int stride, int block, int value)
    {
        for (int k = 0; k < Blocks; k++)
        {
            bool white = k == 0 || (k >= 2 && ((value >> (Bits - 1 - (k - 2))) & 1) != 0);
            byte v = white ? (byte)255 : (byte)0;
            for (int y = 0; y < block; y++)
            {
                int row = y * stride + k * block * 4;
                for (int x = 0; x < block; x++)
                {
                    int i = row + x * 4;
                    bgra[i] = v; bgra[i + 1] = v; bgra[i + 2] = v; bgra[i + 3] = 255;
                }
            }
        }
    }

    /// <summary>Reads a marker from 18 sampled block centres (grey levels); null when the sentinels are not there.</summary>
    public static int? Read(ReadOnlySpan<byte> grey)
    {
        if (grey.Length < Blocks || grey[0] < 180 || grey[1] > 75) return null;
        int value = 0;
        for (int k = 0; k < Bits; k++)
        {
            byte g = grey[k + 2];
            if (g is > 75 and < 180) return null; // between black and white: a blended or half-drawn frame
            value = (value << 1) | (g >= 128 ? 1 : 0);
        }
        return value;
    }
}
