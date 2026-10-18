using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;
using BetterVnc.Agent.Protocol;
using BetterVnc.Agent.Video;
using Xunit;

namespace BetterVnc.Agent.Tests;

/// <summary>
/// The agent's side of the macOS capture helper's pipe (<see cref="CaptureHelperSession"/>), against a stand-in helper:
/// the real one (Swift) only runs on a Mac.
/// </summary>
public class CaptureHelperSessionTests
{
    /// <summary>Plays bettervnc-capture: writes messages the way it does, records the commands, exits on "quit".</summary>
    private sealed class FakeHelper : IDisposable
    {
        private readonly AnonymousPipeServerStream _out = new(PipeDirection.Out);
        private readonly AnonymousPipeServerStream _in = new(PipeDirection.In);
        private readonly Channel<string> _commands = Channel.CreateUnbounded<string>();

        public FakeHelper()
        {
            AgentReads = new AnonymousPipeClientStream(PipeDirection.In, _out.ClientSafePipeHandle);
            AgentWrites = new AnonymousPipeClientStream(PipeDirection.Out, _in.ClientSafePipeHandle);
            _ = Task.Run(async () =>
            {
                using var reader = new StreamReader(_in, Encoding.ASCII);
                while (await reader.ReadLineAsync() is { } line)
                {
                    _commands.Writer.TryWrite(line);
                    if (line == "quit") Exit();
                }
                _commands.Writer.TryComplete();
            });
        }

        public Stream AgentReads { get; }
        public Stream AgentWrites { get; }

        public void Send(byte kind, byte[] payload, bool keyframe = false, int width = 0, int height = 0, long timestamp = 0, uint magic = CaptureHelperSession.Magic)
        {
            var header = new byte[CaptureHelperSession.HeaderSize];
            BinaryPrimitives.WriteUInt32LittleEndian(header, magic);
            header[4] = kind;
            header[5] = keyframe ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), (ushort)width);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), (ushort)height);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)payload.Length);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16), timestamp);
            _out.Write(header);
            _out.Write(payload);
            _out.Flush();
        }

        public void Info(string lines) => Send(CaptureHelperSession.KindInfo, Encoding.UTF8.GetBytes(lines));

        public void Error(string text) => Send(CaptureHelperSession.KindError, Encoding.UTF8.GetBytes(text));

        public async Task<string?> NextCommand()
        {
            try { return await _commands.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (ChannelClosedException) { return null; }
        }

        /// <summary>The helper process ends: its standard output closes.</summary>
        public void Exit()
        {
            try { _out.Dispose(); } catch { }
        }

        public void Dispose()
        {
            Exit();
            AgentReads.Dispose();
            AgentWrites.Dispose();
            _in.Dispose();
        }
    }

    private sealed class Recorder
    {
        public Channel<(byte[] Data, VideoFrame Frame)> Frames { get; } = Channel.CreateUnbounded<(byte[], VideoFrame)>();
        public TaskCompletionSource<string?> Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void OnFrame(VideoFrame f) => Frames.Writer.TryWrite((f.Data.ToArray(), f));
        public void OnEnded(string? reason) => Ended.TrySetResult(reason);
        public async Task<(byte[] Data, VideoFrame Frame)> NextFrame() => await Frames.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static readonly VideoRequest H264 = new() { Codec = AgentVideoCodec.H264, BitrateKbps = 8000, MaxFps = 60 };

    private static (CaptureHelperSession Session, Recorder Recorder) Create(FakeHelper helper, VideoRequest? request = null)
    {
        var recorder = new Recorder();
        var session = new CaptureHelperSession(request ?? H264, helper.AgentReads, helper.AgentWrites, null, recorder.OnFrame, recorder.OnEnded, null);
        return (session, recorder);
    }

    [Fact]
    public async Task Starts_on_info_then_delivers_frames_and_forwards_commands()
    {
        using var helper = new FakeHelper();
        var (session, recorder) = Create(helper);
        var start = Task.Run(() => session.Start(TimeSpan.FromSeconds(5)));
        helper.Info("width=1920\nheight=1080\nencoder=hardware\ndescription=ScreenCaptureKit, display 1\n");
        await start;

        Assert.Equal(AgentVideoCodec.H264, session.Codec);
        Assert.Equal(1920, session.Width);
        Assert.Equal(1080, session.Height);
        Assert.Equal(AgentVideoEncoder.Hardware, session.EncoderKind);
        Assert.Null(session.Offset);
        Assert.Equal("ScreenCaptureKit, display 1", session.Description);

        var unit = new byte[] { 0, 0, 0, 1, 0x67, 0x42, 0, 0, 0, 1, 0x65, 0x88 };
        helper.Send(CaptureHelperSession.KindFrame, unit, keyframe: true, width: 1920, height: 1080, timestamp: 16_667);
        helper.Send(CaptureHelperSession.KindFrame, new byte[] { 0, 0, 0, 1, 0x41, 0x9A }, keyframe: false, width: 1920, height: 1080, timestamp: 33_333);
        var first = await recorder.NextFrame();
        Assert.Equal(unit, first.Data);
        Assert.True(first.Frame.Keyframe);
        Assert.Equal(16_667, first.Frame.TimestampMicros);
        Assert.Equal((1920, 1080), (first.Frame.Width, first.Frame.Height));
        Assert.Null(first.Frame.OffsetX);
        var second = await recorder.NextFrame();
        Assert.False(second.Frame.Keyframe);
        Assert.Equal(33_333, second.Frame.TimestampMicros);

        session.Set(4000, 30);
        Assert.Equal("bitrate 4000", await helper.NextCommand());
        Assert.Equal("fps 30", await helper.NextCommand());
        Assert.Equal(4000, session.BitrateKbps);
        Assert.Equal(30, session.MaxFps);
        session.Set(null, 500);
        Assert.Equal("fps 120", await helper.NextCommand());
        Assert.Equal(120, session.MaxFps);
        session.RequestKeyframe();
        Assert.Equal("keyframe", await helper.NextCommand());

        session.Dispose();
        Assert.Equal("quit", await helper.NextCommand());
        Assert.Null(await helper.NextCommand()); // standard input closed
        Assert.False(recorder.Ended.Task.IsCompleted); // a stream stopped on purpose does not "end"
    }

    [Fact]
    public async Task Info_with_an_offset_places_frames_in_the_desktop_and_later_info_updates_it()
    {
        using var helper = new FakeHelper();
        var (session, recorder) = Create(helper);
        var start = Task.Run(() => session.Start(TimeSpan.FromSeconds(5)));
        helper.Info("width=2880\nheight=1800\nencoder=software\noffsetX=1920\noffsetY=0\n");
        await start;
        Assert.Equal((1920, 0), session.Offset);
        Assert.Equal(AgentVideoEncoder.Software, session.EncoderKind);

        helper.Send(CaptureHelperSession.KindFrame, new byte[] { 1 }, keyframe: true, width: 2880, height: 1800);
        var frame = (await recorder.NextFrame()).Frame;
        Assert.Equal(1920, frame.OffsetX);
        Assert.Equal(0, frame.OffsetY);

        // The other display was unplugged: the helper says so, and frames show the whole desktop again.
        helper.Info("width=2880\nheight=1800\nencoder=hardware\n");
        helper.Send(CaptureHelperSession.KindFrame, new byte[] { 2 }, width: 2880, height: 1800);
        frame = (await recorder.NextFrame()).Frame;
        Assert.Null(frame.OffsetX);
        Assert.Null(session.Offset);
        Assert.Equal(AgentVideoEncoder.Hardware, session.EncoderKind);
        session.Dispose();
    }

    [Fact]
    public async Task An_error_before_capture_runs_fails_start_with_the_helpers_words()
    {
        using var helper = new FakeHelper();
        var (session, recorder) = Create(helper);
        var start = Task.Run(() => session.Start(TimeSpan.FromSeconds(5)));
        helper.Error("macOS has not allowed bettervnc-agent to record the screen.");
        helper.Exit();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => start);
        Assert.Equal("macOS has not allowed bettervnc-agent to record the screen.", ex.Message);
        Assert.False(recorder.Ended.Task.IsCompleted);
    }

    [Fact]
    public async Task A_helper_that_dies_mid_stream_ends_it_with_its_reason()
    {
        using var helper = new FakeHelper();
        var (session, recorder) = Create(helper);
        var start = Task.Run(() => session.Start(TimeSpan.FromSeconds(5)));
        helper.Info("width=1280\nheight=800\nencoder=hardware\n");
        await start;
        helper.Error("Screen capture failed: the display went away");
        helper.Exit();
        Assert.Equal("Screen capture failed: the display went away", await recorder.Ended.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        session.Dispose();
    }

    [Fact]
    public async Task A_helper_that_just_exits_ends_the_stream_too()
    {
        using var helper = new FakeHelper();
        var (session, recorder) = Create(helper);
        var start = Task.Run(() => session.Start(TimeSpan.FromSeconds(5)));
        helper.Info("width=1280\nheight=800\n");
        await start;
        helper.Exit();
        Assert.Equal("The capture helper exited.", await recorder.Ended.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        session.Dispose();
    }

    [Fact]
    public async Task Data_that_is_not_the_helper_format_ends_the_stream()
    {
        using var helper = new FakeHelper();
        var (session, recorder) = Create(helper);
        var start = Task.Run(() => session.Start(TimeSpan.FromSeconds(5)));
        helper.Info("width=1280\nheight=800\n");
        await start;
        helper.Send(CaptureHelperSession.KindFrame, new byte[] { 1, 2, 3 }, magic: 0xDEADBEEF);
        Assert.Contains("does not understand", await recorder.Ended.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        session.Dispose();
    }

    [Fact]
    public async Task Unknown_message_kinds_are_skipped()
    {
        using var helper = new FakeHelper();
        var (session, recorder) = Create(helper);
        var start = Task.Run(() => session.Start(TimeSpan.FromSeconds(5)));
        helper.Send(9, Encoding.UTF8.GetBytes("something a newer helper says"));
        helper.Info("width=1280\nheight=800\n");
        await start;
        helper.Send(9, new byte[100]);
        helper.Send(CaptureHelperSession.KindFrame, new byte[] { 7 }, keyframe: true, width: 1280, height: 800);
        Assert.Equal(new byte[] { 7 }, (await recorder.NextFrame()).Data);
        session.Dispose();
    }

    [Fact]
    public async Task A_helper_that_never_starts_capturing_times_out_and_is_told_to_quit()
    {
        using var helper = new FakeHelper();
        var (session, _) = Create(helper);
        var ex = await Assert.ThrowsAsync<TimeoutException>(() => Task.Run(() => session.Start(TimeSpan.FromMilliseconds(200))));
        Assert.Contains("did not start", ex.Message);
        Assert.Equal("quit", await helper.NextCommand());
    }
}
