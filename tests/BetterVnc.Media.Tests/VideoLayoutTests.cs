using BetterVnc.Media;
using Xunit;

namespace BetterVnc.Media.Tests;

public class VideoLayoutTests
{
    [Fact]
    public void A_whole_desktop_frame_covers_the_fitted_picture()
    {
        // 1920x1080 fitted into a 1500x1000 view: 1500x843.75, centred.
        var image = new PixelRect(0, 78.125, 1500, 843.75);
        var p = VideoLayout.Place(image, 1920, 1080, 1920, 1080, null, new PixelRect(0, 0, 1500, 1000));
        Assert.Equal((0, 78, 1500, 844), (p.X, p.Y, p.Width, p.Height));
        Assert.Equal(new PixelRect(0, 0, 1500, 844), p.Destination);
    }

    [Fact]
    public void A_scrolled_one_to_one_picture_is_cut_to_the_viewport()
    {
        // 2256x1504 at 1:1, scrolled 300 px right and 200 down, in a 1200x800 viewport that starts 40 px down the window.
        var image = new PixelRect(-300, 40 - 200, 2256, 1504);
        var p = VideoLayout.Place(image, 2256, 1504, 2256, 1504, null, new PixelRect(0, 40, 1200, 800));
        Assert.Equal((0, 40, 1200, 800), (p.X, p.Y, p.Width, p.Height));
        // The picture keeps its size and reaches past the window: texel (300,200) lands on the window's corner.
        Assert.Equal(new PixelRect(-300, -200, 2256, 1504), p.Destination);
    }

    [Fact]
    public void A_partial_frame_lands_on_its_display_and_stays_inside_the_framebuffer()
    {
        // A 4000x2000 desktop drawn at half size; the streamed display (2000x1200) sits at 3000,1000 and overhangs the framebuffer.
        var image = new PixelRect(10, 20, 2000, 1000);
        var p = VideoLayout.Place(image, 4000, 2000, 2000, 1200, (3000, 1000), new PixelRect(0, 0, 3000, 3000));
        Assert.Equal((1510, 520, 500, 500), (p.X, p.Y, p.Width, p.Height));
        Assert.Equal(new PixelRect(0, 0, 1000, 600), p.Destination);
    }

    [Fact]
    public void Nothing_visible_means_an_empty_placement()
    {
        var image = new PixelRect(0, 0, 800, 600);
        Assert.True(VideoLayout.Place(image, 800, 600, 800, 600, null, new PixelRect(900, 0, 100, 100)).IsEmpty);
        Assert.True(VideoLayout.Place(image, 800, 600, 800, 600, (900, 0), new PixelRect(0, 0, 2000, 2000)).IsEmpty);
        Assert.True(VideoLayout.Place(default, 800, 600, 800, 600, null, new PixelRect(0, 0, 2000, 2000)).IsEmpty);
    }
}
