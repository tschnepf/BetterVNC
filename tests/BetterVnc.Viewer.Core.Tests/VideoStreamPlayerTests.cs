using BetterVnc.Viewer.Core.Models;
using BetterVnc.Viewer.Core.Services;
using Xunit;

namespace BetterVnc.Viewer.Core.Tests;

public sealed class VideoStreamPlayerTests : IDisposable
{
    private static readonly PictureModes.Info Stream = PictureModes.Get(PictureMode.StreamBalanced);

    private readonly TestUiThread _ui = new();
    private readonly FakeAgentStream _agent = new();
    private readonly FakeRfbPicture _rfb = new();
    private readonly FakeVideoSurface _view = new();
    private readonly VideoStreamPlayer _player;
    private int _framesSent;

    public VideoStreamPlayerTests()
    {
        _player = _ui.RunAsync(() => new VideoStreamPlayer(_agent, _rfb, _view, _ui, new FakeJpegDecoder(), "fake JPEG")).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _ui.RunAsync(() => { _player.Dispose(); return true; }).Wait(5000);
        _ui.Dispose();
    }

    private Task StartAsync() => _ui.RunAsync(() => _player.StartAsync(Stream));

    /// <summary>Sends one frame and waits until the decode loop has dealt with it and the UI thread has heard.</summary>
    private async Task SendAsync(int sequence, bool keyframe)
    {
        int expected = ++_framesSent;
        _agent.SendFrame(sequence, keyframe);
        await _ui.UntilAsync(() => _player.FramesDecoded + _player.FramesSkipped >= expected, $"frame {sequence} to be decoded or skipped");
        // The decode loop posts its "frame presented" to the UI thread right after counting the frame: let that run too.
        await Task.Delay(20);
        await _ui.RunAsync(() => true);
    }

    private TestTimer IdleTimer => _ui.Timer(VideoStreamPlayer.IdleAfter);

    [Fact]
    public async Task Starting_puts_RFB_on_a_trickle_and_arms_the_idle_timer()
    {
        await StartAsync();

        Assert.True(_player.IsStreaming);
        Assert.Equal(1, _agent.Starts);
        Assert.Equal(VideoStreamPlayer.RfbIntervalWhileStreaming, _rfb.UpdateInterval);
        Assert.True(IdleTimer.IsEnabled);
        Assert.False(_player.IdleFallbackActive);
    }

    [Fact]
    public async Task A_still_screen_falls_back_to_lossless_RFB_and_the_next_frame_brings_the_stream_back()
    {
        await StartAsync();
        await SendAsync(0, keyframe: true);
        Assert.Equal(1, _view.FramesShown);
        Assert.Equal("software → test", _player.DecodePath);

        // Nothing for IdleAfter: the exact RFB picture is shown, refreshed in full, and RFB runs at full speed.
        await IdleTimer.FireAsync();
        Assert.True(_player.IdleFallbackActive);
        Assert.Equal(1, _view.FramebufferShown);
        Assert.Equal(1, _rfb.Refreshes);
        Assert.Equal(TimeSpan.Zero, _rfb.UpdateInterval);
        Assert.False(IdleTimer.IsEnabled);

        // Movement again: the stream is back on screen and RFB returns to its trickle.
        await SendAsync(1, keyframe: false);
        Assert.False(_player.IdleFallbackActive);
        Assert.Equal(VideoStreamPlayer.RfbIntervalWhileStreaming, _rfb.UpdateInterval);
        Assert.True(IdleTimer.IsEnabled);
        Assert.Equal(2, _view.FramesShown);
        Assert.Equal(1, _view.FramebufferShown);
        _ui.ThrowIfFailed();
    }

    [Fact]
    public async Task Frames_keep_the_idle_timer_from_firing_while_the_screen_moves()
    {
        await StartAsync();
        for (int i = 0; i < 5; i++) await SendAsync(i, keyframe: i == 0);

        Assert.True(IdleTimer.IsEnabled);
        Assert.False(_player.IdleFallbackActive);
        Assert.Equal(0, _view.FramebufferShown);
        Assert.Equal(5, _player.FramesDecoded);
    }

    [Fact]
    public async Task A_gap_in_the_sequence_skips_delta_frames_and_asks_for_a_key_frame()
    {
        await StartAsync();
        await SendAsync(0, keyframe: true);
        await SendAsync(1, keyframe: false);
        Assert.Equal(0, _agent.KeyframeRequests);

        // Frame 2 was lost on the way: frame 3 cannot be decoded without it.
        await SendAsync(3, keyframe: false);
        await _ui.UntilAsync(() => _agent.KeyframeRequests == 1, "a key frame request");
        // Further delta frames are skipped too, without asking again right away (at most two requests a second).
        await SendAsync(4, keyframe: false);
        Assert.Equal(2, _player.FramesSkipped);
        Assert.Equal(1, _agent.KeyframeRequests);

        // The key frame resumes decoding.
        await SendAsync(5, keyframe: true);
        await SendAsync(6, keyframe: false);
        var decoder = Assert.Single(_view.Decoders);
        Assert.Equal(new[] { 0, 1, 5, 6 }, decoder.Decoded);
        Assert.Equal(4, _player.FramesDecoded);
        Assert.Equal(2, _player.FramesSkipped);
        _ui.ThrowIfFailed();
    }

    [Fact]
    public async Task A_stream_that_starts_without_a_key_frame_is_decoded_from_the_first_frame()
    {
        // Sequence numbers say nothing about the first frame: the decoder gets it, and waits for a key frame only after a gap.
        await StartAsync();
        await SendAsync(7, keyframe: false);
        await SendAsync(8, keyframe: false);
        Assert.Equal(2, _player.FramesDecoded);
        Assert.Equal(0, _agent.KeyframeRequests);
    }

    [Fact]
    public async Task The_agent_stopping_the_stream_returns_the_picture_to_RFB()
    {
        await StartAsync();
        await SendAsync(0, keyframe: true);
        string? ended = null;
        await _ui.RunAsync(() => { _player.Ended += r => ended = r; return true; });

        await _ui.RunAsync(() => { _agent.StopFromAgent("capture failed"); return true; });

        Assert.False(_player.IsStreaming);
        Assert.Equal("capture failed", ended);
        Assert.Equal(1, _view.FramebufferShown);
        Assert.Equal(1, _rfb.Refreshes);
        Assert.Equal(TimeSpan.Zero, _rfb.UpdateInterval);
        Assert.False(IdleTimer.IsEnabled);
    }

    [Fact]
    public async Task Stopping_returns_to_RFB_and_ignores_later_frames()
    {
        await StartAsync();
        await SendAsync(0, keyframe: true);

        await _ui.RunAsync(() => _player.StopAsync());
        _agent.SendFrame(1, keyframe: false);
        await Task.Delay(100);

        Assert.False(_player.IsStreaming);
        Assert.Equal(1, _agent.Stops);
        Assert.Equal(1, _view.FramebufferShown);
        Assert.Equal(TimeSpan.Zero, _rfb.UpdateInterval);
        Assert.Equal(1, _player.FramesReceived);
        Assert.Equal(1, _view.FramesShown);
    }

    [Fact]
    public async Task A_new_picture_area_reaches_the_agent_once_it_has_settled()
    {
        await StartAsync();
        var areaTimer = _ui.Timer(VideoStreamPlayer.AreaSettleTime);

        await _ui.RunAsync(() => { _view.ChangePictureArea(1000, 700); _view.ChangePictureArea(1024, 768); return true; });
        Assert.True(areaTimer.IsEnabled);
        Assert.Empty(_agent.Areas);

        await areaTimer.FireAsync();
        await _ui.UntilAsync(() => !_agent.Areas.IsEmpty, "the area to be sent");
        Assert.Equal(new[] { (1024, 768) }, _agent.Areas);
        Assert.False(areaTimer.IsEnabled);
    }

    /// <summary>Sends a repair and waits until the player has shown it and the UI thread has heard.</summary>
    private async Task RepairAsync(int sequence, byte blue, params BetterVnc.Agent.Protocol.RepairRect[] rects)
    {
        long expected = _player.RepairsShown + 1;
        _agent.SendRepair(sequence, blue, rects);
        await _ui.UntilAsync(() => _player.RepairsShown >= expected, $"repair {sequence} to be shown");
        await Task.Delay(20);
        await _ui.RunAsync(() => true);
    }

    private static byte BlueAt(byte[] bgrx, int x, int y) => bgrx[(y * 64 + x) * 4];

    [Fact]
    public async Task An_agent_that_repairs_the_still_screen_keeps_its_exact_picture_instead_of_falling_back_to_RFB()
    {
        _agent.CanRepair = true;
        await StartAsync();
        Assert.True(_agent.RepairRequested);
        await SendAsync(0, keyframe: true);
        Assert.False(await _ui.RunAsync(() => _player.ShowingExactPicture));

        // The screen went still: the whole picture, exact, replaces the lossy frame.
        await RepairAsync(1, blue: 7);
        var picture = Assert.IsType<byte[]>(_view.LastPicture);
        Assert.All(Enumerable.Range(0, 64 * 48), i => { Assert.Equal(7, picture[i * 4]); Assert.Equal(255, picture[i * 4 + 3]); });
        Assert.True(await _ui.RunAsync(() => _player.ShowingExactPicture));

        // IdleAfter later nothing switches to the VNC framebuffer, whose picture may be older.
        await IdleTimer.FireAsync();
        Assert.False(_player.IdleFallbackActive);
        Assert.Equal(0, _view.FramebufferShown);
        Assert.Equal(0, _rfb.Refreshes);
        Assert.Equal(VideoStreamPlayer.RfbIntervalWhileStreaming, _rfb.UpdateInterval);

        // A later repair paints over the earlier ones: only its rectangle changes.
        await SendAsync(1, keyframe: false);
        Assert.False(await _ui.RunAsync(() => _player.ShowingExactPicture));
        await RepairAsync(2, blue: 9, new BetterVnc.Agent.Protocol.RepairRect(8, 8, 16, 16));
        picture = _view.LastPicture!;
        Assert.Equal(9, BlueAt(picture, 8, 8));
        Assert.Equal(9, BlueAt(picture, 23, 23));
        Assert.Equal(7, BlueAt(picture, 24, 8));
        Assert.Equal(7, BlueAt(picture, 0, 0));
        Assert.True(await _ui.RunAsync(() => _player.ShowingExactPicture));
        Assert.Equal(0, _agent.KeyframeRequests);
        _ui.ThrowIfFailed();
    }

    [Fact]
    public async Task A_lost_repair_hides_the_picture_until_a_whole_one_comes()
    {
        _agent.CanRepair = true;
        await StartAsync();
        await SendAsync(0, keyframe: true);
        await RepairAsync(1, blue: 7);

        // Repair 2 never arrived: repair 3 alone would leave part of the picture as it was before 2, so it is not shown.
        _agent.SendRepair(3, 9, new BetterVnc.Agent.Protocol.RepairRect(0, 0, 8, 8));
        await _ui.UntilAsync(() => _agent.KeyframeRequests == 1, "a key frame request");
        Assert.Equal(1, _player.RepairsShown);

        // The key frame brings a whole repair once the screen is still again, and that is shown.
        await SendAsync(1, keyframe: true);
        await RepairAsync(4, blue: 11);
        Assert.Equal(11, BlueAt(_view.LastPicture!, 0, 0));
        _ui.ThrowIfFailed();
    }

    [Fact]
    public async Task A_repair_that_comes_after_the_fallback_to_RFB_brings_the_exact_stream_picture_back()
    {
        _agent.CanRepair = true;
        await StartAsync();
        await SendAsync(0, keyframe: true);

        // The repair is late (a slow link): the old fallback runs first...
        await IdleTimer.FireAsync();
        Assert.True(_player.IdleFallbackActive);
        Assert.Equal(TimeSpan.Zero, _rfb.UpdateInterval);

        // ...and the repair puts the exact picture back on the stream's surface, with RFB on its trickle again.
        await RepairAsync(1, blue: 7);
        Assert.False(_player.IdleFallbackActive);
        Assert.True(await _ui.RunAsync(() => _player.ShowingExactPicture));
        Assert.Equal(VideoStreamPlayer.RfbIntervalWhileStreaming, _rfb.UpdateInterval);
        _ui.ThrowIfFailed();
    }

    [Fact]
    public async Task An_agent_without_repairs_is_still_asked_and_the_stream_falls_back_to_RFB_as_before()
    {
        await StartAsync();
        Assert.True(_agent.RepairRequested);
        await SendAsync(0, keyframe: true);
        await IdleTimer.FireAsync();
        Assert.True(_player.IdleFallbackActive);
        Assert.Equal(1, _rfb.Refreshes);
    }
}
