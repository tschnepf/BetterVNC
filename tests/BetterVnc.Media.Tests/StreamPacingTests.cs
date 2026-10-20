using BetterVnc.Media;
using Xunit;

namespace BetterVnc.Media.Tests;

public class FrameRateGovernorTests
{
    /// <summary>Feeds <paramref name="seconds"/> of frames at the governor's current rate, each taking <paramref name="workMs"/>.</summary>
    private static List<string> Run(FrameRateGovernor g, ref long now, double seconds, double workMs)
    {
        var changes = new List<string>();
        long end = now + (long)(seconds * 1e6);
        while (now < end)
        {
            now += 1_000_000 / g.Fps;
            if (g.Record((long)(workMs * 1000), now) is { } c) changes.Add(c);
        }
        return changes;
    }

    [Fact]
    public void Steps_down_when_frames_take_most_of_the_interval_and_back_up_with_headroom()
    {
        var g = new FrameRateGovernor(60);
        long now = 0;
        Assert.Empty(Run(g, ref now, 3, workMs: 8)); // 48% of 16.7 ms: fine
        Assert.Equal(60, g.Fps);

        var down = Run(g, ref now, 2.2, workMs: 14); // 84%
        Assert.Single(down);
        Assert.Equal(30, g.Fps);
        Assert.Contains("stepping down to 30 fps", down[0]);
        Run(g, ref now, 2.5, workMs: 28); // 84% of 33 ms
        Assert.Equal(20, g.Fps);
        Run(g, ref now, 3, workMs: 45); // nothing below 20
        Assert.Equal(20, g.Fps);
        Assert.True(g.SteppedDown);

        // 14 ms is 42% of 33 ms but 84% of 16.7 ms: back to 30 after a few seconds, not to 60.
        var up = Run(g, ref now, 5.5, workMs: 14);
        Assert.Single(up);
        Assert.Contains("stepping back up to 30 fps", up[0]);
        Assert.Equal(30, g.Fps);
        Run(g, ref now, 6, workMs: 14);
        Assert.Equal(30, g.Fps);
        Run(g, ref now, 5.5, workMs: 3);
        Assert.Equal(60, g.Fps);
        Assert.False(g.SteppedDown);
    }

    [Fact]
    public void A_short_burst_or_a_still_screen_changes_nothing()
    {
        var g = new FrameRateGovernor(60);
        long now = 0;
        for (int i = 0; i < 3; i++) Assert.Null(g.Record(30_000, now += 16_667)); // a few slow frames, then nothing
        now += 5_000_000;
        Assert.Null(g.Record(30_000, now)); // the window had too few frames to judge
        Assert.Equal(60, g.Fps);
    }

    [Fact]
    public void The_ladder_starts_at_the_rate_asked_for()
    {
        var g = new FrameRateGovernor(24);
        long now = 0;
        Run(g, ref now, 1.5, workMs: 40);
        Assert.Equal(20, g.Fps);
        g.Reset(120);
        Assert.Equal(120, g.Fps);
        Run(g, ref now, 1.5, workMs: 7); // 84% of 8.3 ms
        Assert.Equal(60, g.Fps);
        var slow = new FrameRateGovernor(15);
        Run(slow, ref now, 3, workMs: 60);
        Assert.Equal(15, slow.Fps);
    }
}
