using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using BetterVnc.Agent.Protocol;
using BetterVnc.Media;
using BetterVnc.Protocol;

namespace BetterVnc.AgentBench;

// Usage: BetterVnc.AgentBench <agent host[:port]> <token> [--vnc host:port] [--rfb] [--no-repair] [--kbps 20000] [--fps 60]
//          [--latency-samples 15] [--latency-gap 700] [--motion-seconds 10] [--still-seconds 6] [--label text] [--json results.json]
//
// bench-target.ps1 must be running in the guest, full screen and in front: it draws a page of code that F14 scrolls and a
// marker that F13 flips between black and white. The bench sends those keys through the agent's input channel, decodes
// every frame in software and looks at the marker, so a latency sample is the time from sending F13 to the first frame
// (and decoded picture) showing the flipped marker: input, guest drawing, capture, encode, network and decode.
//   1. still screen: latency samples spaced --latency-gap to 1.86 times that apart (0.7-1.3 s);
//   2. motion: the page scrolls at 60 Hz for --motion-seconds; frame rate, bitrate, frame gaps and latency samples;
//   3. still again: the scrolling stops, and the stream's picture at fixed times after that is compared with the guest's
//      real pixels from QEMU's VNC server (--vnc), as luma PSNR over the text area. The stream asks for repairs of the
//      still screen unless --no-repair; an agent that sends them makes the picture exact, and the bench says when.
//      It also replays what a viewer with VNC does when the stream goes quiet without repairs: 700 ms after the last
//      frame it shows its VNC framebuffer (updated once a second while streaming), and asks for a full update. Measured:
//      how far that framebuffer is from the real picture, and how long the update takes. Last, the key frame a viewer
//      without VNC asks for.
// --rfb takes the picture from the VNC server alone instead, as the viewer's VNC picture modes do: phases 1 and 2.
internal static class Program
{
    private sealed record FrameRecord(int Sequence, long Arrived, long Decoded, int Bytes, bool Keyframe, int MarkerLuma, bool Beacon, bool Repair = false);

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    private static long Now => Clock.ElapsedTicks;
    private static long Ticks(double ms) => (long)(ms * Stopwatch.Frequency / 1000);

    private static readonly List<FrameRecord> Frames = new();
    private static readonly object Sync = new();
    private static int _width, _height;
    private static (int X, int Y) _marker;
    private static int _textWidth;
    // The picture shown last (luma), the one shown at each still-phase checkpoint, and every one shown after the stop.
    private static byte[] _latestY = Array.Empty<byte>();
    private static long[] _checkpoints = Array.Empty<long>();
    private static byte[]?[] _checkpointPictures = Array.Empty<byte[]?>();
    private static long _keepAfter = long.MaxValue;
    private static readonly List<(long Shown, bool Repair, byte[] Luma)> ShownAfterStop = new();
    // A latency sample waits for the marker to show this colour in a frame that arrived after it was asked for.
    private static (bool White, long Since, TaskCompletionSource<FrameRecord> Done)? _waiter;
    // A key-frame request waits for the next key frame.
    private static (long Since, TaskCompletionSource<(FrameRecord, byte[])> Done)? _keyWaiter;

    private static readonly string[] Flags = { "--rfb", "--no-repair" };

    private static async Task<int> Main(string[] args)
    {
        string? Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var positional = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (Flags.Contains(args[i])) continue;
            if (args[i].StartsWith("--")) { i++; continue; }
            positional.Add(args[i]);
        }
        if (positional.Count < 2)
        {
            Console.Error.WriteLine("Usage: BetterVnc.AgentBench <agent host[:port]> <token> [--vnc host:port] [--rfb] [--no-repair] [--kbps n] [--fps n] [--latency-samples n] [--latency-gap ms] [--motion-seconds n] [--still-seconds n] [--label text] [--json file]");
            return 2;
        }
        var (host, port) = HostPort(positional[0], AgentServer.DefaultPort);
        string token = positional[1];
        int kbps = int.Parse(Opt("--kbps") ?? "20000"), fps = int.Parse(Opt("--fps") ?? "60");
        int latencySamples = int.Parse(Opt("--latency-samples") ?? "15");
        int latencyGap = int.Parse(Opt("--latency-gap") ?? "700");
        double motionSeconds = double.Parse(Opt("--motion-seconds") ?? "10", CultureInfo.InvariantCulture);
        double stillSeconds = double.Parse(Opt("--still-seconds") ?? "6", CultureInfo.InvariantCulture);
        string label = Opt("--label") ?? "";
        bool rfb = args.Contains("--rfb"), repair = !args.Contains("--no-repair");
        var results = new Dictionary<string, object?> { ["label"] = label, ["kbps"] = kbps, ["fps"] = fps, ["at"] = DateTime.Now.ToString("s") };

        VncClient? vnc = null;
        if (Opt("--vnc") is { } vncTarget)
        {
            var (vh, vp) = HostPort(vncTarget, 5900);
            vnc = new VncClient(new VncConnectionOptions { Host = vh, Port = vp, EnableQemuAudio = false, Shared = true });
            await vnc.ConnectAsync();
            // While the stream carries the picture the viewer keeps VNC at a trickle; on VNC alone it asks for updates nonstop.
            vnc.UpdateInterval = rfb ? TimeSpan.Zero : TimeSpan.FromSeconds(1);
        }
        if (rfb && vnc == null) { Console.Error.WriteLine("--rfb needs --vnc."); return 2; }
        results["source"] = rfb ? "rfb" : "stream";

        using var conn = new AgentConnection(new AgentConnectionOptions { Host = host, Port = port, Token = token, AppName = "BetterVnc.AgentBench" });
        await conn.ConnectAsync(CancellationToken.None);
        if (!conn.SupportsInput || (!rfb && !conn.SupportsVideo)) { Console.Error.WriteLine("The agent must offer input, and video unless --rfb."); return 1; }
        var ping = await conn.PingAsync(CancellationToken.None);
        results["agent"] = conn.RemoteApp;
        results["pingMs"] = ping.TotalMilliseconds;

        var queue = Channel.CreateUnbounded<(long Arrived, AgentFrame Frame)>(new UnboundedChannelOptions { SingleReader = true });
        var decodeLoop = Task.CompletedTask;
        bool repairs = false;
        if (rfb)
        {
            var fb = vnc!.Framebuffer;
            SetSize(fb.Width, fb.Height);
            long lastBytes = vnc.Statistics.BytesReceived;
            int updates = 0;
            vnc.FramebufferUpdated += _ =>
            {
                // Raised on the receive thread once the whole update is decoded into the framebuffer: what the viewer draws next.
                long now = Now;
                var (luma, beacon) = ReadFramebuffer(vnc.Framebuffer);
                long bytes = vnc.Statistics.BytesReceived; // as of the end of the previous message: right over a window of updates
                lock (Sync) RfbUpdateMs.Add(vnc.Statistics.LastUpdateMilliseconds);
                Record(new FrameRecord(++updates, now, now, (int)(bytes - lastBytes), false, luma, beacon), null);
                lastBytes = bytes;
            };
            Console.WriteLine($"{label}: VNC {fb.Width}x{fb.Height} lossless, agent {conn.RemoteApp} for input, ping {ping.TotalMilliseconds:0.0} ms");
            results["size"] = $"{fb.Width}x{fb.Height}";
            vnc.RefreshPicture(); // a first update to look at
        }
        else
        {
            conn.VideoFrameReceived += f => queue.Writer.TryWrite((Now, f));
            decodeLoop = Task.Run(() => DecodeLoop(queue.Reader));
            var info = await conn.StartVideoAsync(new[] { AgentVideoCodec.H264 }, kbps, fps, null, cursor: false, repair, CancellationToken.None);
            repairs = info.Repair;
            SetSize(info.Width, info.Height);
            Console.WriteLine($"{label}: agent {conn.RemoteApp}, ping {ping.TotalMilliseconds:0.0} ms, {info.Codec} {info.Width}x{info.Height}, {info.BitrateKbps} kbit/s, {info.MaxFps} fps max, {info.EncoderKind} encoder, {(repairs ? "repairs the still screen" : "no repairs")}");
            results["encoder"] = info.EncoderKind;
            results["size"] = $"{info.Width}x{info.Height}";
            results["repairs"] = repairs;
        }

        // F13 and F14 by virtual key: keys no other program reacts to, should one land elsewhere.
        const int ToggleKey = 0x7C, ScrollKey = 0x7D;
        async Task Key(int vk)
        {
            await conn.SendKeyAsync(0, vk, true, CancellationToken.None);
            await Task.Delay(15);
            await conn.SendKeyAsync(0, vk, false, CancellationToken.None);
        }

        // No input before the target's beacon shows: whatever else is on the guest's screen must not get clicks.
        var firstDeadline = Now + Ticks(10000);
        while (LastFrame() is not { Beacon: true } && Now < firstDeadline) await Task.Delay(20);
        if (LastFrame() is not { Beacon: true } first)
        {
            Console.Error.WriteLine(LastFrame() is null ? "No picture arrived." : "bench-target.ps1 is not showing in the guest (its red, green and blue squares are missing).");
            if (!rfb) await conn.StopVideoAsync(CancellationToken.None);
            return 1;
        }
        // A click in the target's empty right column gives it the keyboard; the pointer then stays there, out of the way.
        await conn.SendPointerAsync(_width - 120, _height - 60, _width, _height, 1, CancellationToken.None);
        await Task.Delay(30);
        await conn.SendPointerAsync(_width - 120, _height - 60, _width, _height, 0, CancellationToken.None);
        bool white = first.MarkerLuma >= 128;
        await Task.Delay(1500);

        async Task<(double Arrive, double Show)?> Toggle()
        {
            white = !white;
            var done = new TaskCompletionSource<FrameRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
            long since = Now;
            lock (Sync) _waiter = (white, since, done);
            await Key(ToggleKey);
            var finished = await Task.WhenAny(done.Task, Task.Delay(3000));
            lock (Sync) _waiter = null;
            if (finished != done.Task) return null;
            var f = done.Task.Result;
            return (Ms(f.Arrived - since), Ms(f.Decoded - since));
        }

        if (await Toggle() is null)
        {
            Console.Error.WriteLine("The marker did not flip: bench-target.ps1 does not get the keys.");
            if (!rfb) await conn.StopVideoAsync(CancellationToken.None);
            return 1;
        }

        // ---- 1. still screen
        var rnd = new Random(7);
        var idle = new List<(double Arrive, double Show)>();
        int idleLost = 0;
        for (int i = 0; i < latencySamples; i++)
        {
            await Task.Delay(latencyGap + rnd.Next(latencyGap * 6 / 7));
            if (await Toggle() is { } s) idle.Add(s); else idleLost++;
        }
        Console.WriteLine($"  still screen, input to picture: {Stats(idle.Select(s => s.Show))} (frame arrival {Stats(idle.Select(s => s.Arrive))}){(idleLost > 0 ? $", {idleLost} lost" : "")}");
        results["stillLatencyMs"] = Summary(idle.Select(s => s.Show));
        results["stillArrivalMs"] = Summary(idle.Select(s => s.Arrive));

        // ---- 2. motion
        await Key(ScrollKey); // start scrolling
        await Task.Delay(1500);
        int decodes0;
        lock (Sync) decodes0 = RfbUpdateMs.Count;
        long m0 = Now;
        var moving = new List<(double Arrive, double Show)>();
        int movingLost = 0;
        while (Ms(Now - m0) < motionSeconds * 1000 - 900)
        {
            await Task.Delay(400 + rnd.Next(300));
            if (await Toggle() is { } s) moving.Add(s); else movingLost++;
        }
        while (Ms(Now - m0) < motionSeconds * 1000) await Task.Delay(10);
        long m1 = Now;
        FrameRecord[] window;
        lock (Sync) window = Frames.Where(f => !f.Repair && f.Arrived >= m0 && f.Arrived < m1).ToArray();
        double seconds = Ms(m1 - m0) / 1000;
        var gaps = window.Zip(window.Skip(1), (a, b) => Ms(b.Arrived - a.Arrived)).ToList();
        double motionFps = window.Length / seconds, motionKbps = window.Sum(f => (long)f.Bytes) * 8 / seconds / 1000;
        Console.WriteLine($"  motion: {motionFps:0.0} fps, {motionKbps:0} kbit/s, {window.Count(f => f.Keyframe)} key frame(s), frame gaps {Stats(gaps)}");
        Console.WriteLine($"  motion, input to picture: {Stats(moving.Select(s => s.Show))} (frame arrival {Stats(moving.Select(s => s.Arrive))}){(movingLost > 0 ? $", {movingLost} lost" : "")}");
        results["motionFps"] = motionFps;
        results["motionKbps"] = motionKbps;
        results["motionGapMs"] = Summary(gaps);
        results["motionLatencyMs"] = Summary(moving.Select(s => s.Show));
        results["motionArrivalMs"] = Summary(moving.Select(s => s.Arrive));
        if (rfb)
        {
            List<double> decodes;
            lock (Sync) decodes = RfbUpdateMs.Skip(decodes0).Take(window.Length).ToList();
            Console.WriteLine($"  motion, VNC update read and decoded in: {Stats(decodes)}");
            results["motionRfbDecodeMs"] = Summary(decodes);
        }

        // ---- 3. still again: how close the picture gets to the real one, and when
        if (!rfb) await StillPhaseAsync(vnc, conn, () => Key(ScrollKey), stillSeconds, repairs, results);
        else await Key(ScrollKey);

        if (!rfb) await conn.StopVideoAsync(CancellationToken.None);
        queue.Writer.TryComplete();
        await decodeLoop;
        vnc?.Disconnect();
        if (Opt("--json") is { } jsonPath)
        {
            var all = File.Exists(jsonPath) ? JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(File.ReadAllText(jsonPath)) ?? new() : new();
            all.Add(results);
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
        }
        return 0;
    }

    /// <summary>The viewer's switch to the VNC picture after this long without a frame (VideoStreamPlayer.IdleAfter).</summary>
    private const int VideoIdle = 700;

    private static async Task StillPhaseAsync(VncClient? vnc, AgentConnection conn, Func<Task> stopScrolling, double stillSeconds, bool repairs, Dictionary<string, object?> results)
    {
        double[] offsets = { 250, 500, 750, 1000, 1500, 2000, 3000, 4000, 5000, 6000 };
        offsets = offsets.Where(o => o <= stillSeconds * 1000).ToArray();
        long stop = Now;
        lock (Sync)
        {
            _checkpoints = offsets.Select(o => stop + Ticks(o)).ToArray();
            _checkpointPictures = new byte[]?[offsets.Length];
            _keepAfter = stop;
        }
        await stopScrolling();

        // A viewer with VNC and an agent without repairs: once no frame came for VideoIdle it shows its VNC framebuffer,
        // which the stream kept at one update a second, and asks for all of it again.
        double? switchAt = null, refreshMs = null;
        long refreshBytes = 0;
        byte[]? atSwitch = null, reference = null;
        if (vnc != null)
        {
            while (true)
            {
                long lastFrame;
                lock (Sync) lastFrame = Frames.LastOrDefault(f => !f.Repair)?.Arrived ?? stop;
                long due = Math.Max(lastFrame, stop) + Ticks(VideoIdle);
                if (Now >= due) break;
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, Ms(due - Now))));
            }
            switchAt = Ms(Now - stop);
            atSwitch = LumaOf(vnc);
            (refreshMs, refreshBytes) = await FullVncUpdate(vnc);
            reference = LumaOf(vnc);
        }
        while (Ms(Now - stop) < stillSeconds * 1000 + 100) await Task.Delay(20);
        FrameRecord[] after;
        List<(long Shown, bool Repair, byte[] Luma)> shown;
        lock (Sync)
        {
            for (int i = 0; i < _checkpoints.Length; i++) _checkpointPictures[i] ??= (byte[])_latestY.Clone();
            after = Frames.Where(f => f.Arrived >= stop).ToArray();
            shown = ShownAfterStop.ToList();
            _keepAfter = long.MaxValue;
        }
        var frames = after.Where(f => !f.Repair).ToArray();
        var repaired = after.Where(f => f.Repair).ToArray();
        Console.WriteLine($"  after the scrolling stopped: {frames.Length} frame(s), {frames.Sum(f => (long)f.Bytes) / 1024} KB, last one {(frames.Length == 0 ? "-" : $"{Ms(frames[^1].Arrived - stop):0} ms")} after the stop key; " +
                          $"{repaired.Length} repair(s), {repaired.Sum(f => (long)f.Bytes) / 1024} KB{(repaired.Length == 0 ? "" : $", the first {Ms(repaired[0].Decoded - stop):0} ms after the stop key")}");
        results["stillFrames"] = frames.Length;
        results["stillKB"] = frames.Sum(f => (long)f.Bytes) / 1024;
        results["lastFrameAfterStopMs"] = frames.Length == 0 ? null : Ms(frames[^1].Arrived - stop);
        results["stillRepairs"] = repaired.Length;
        results["stillRepairKB"] = repaired.Sum(f => (long)f.Bytes) / 1024;
        if (reference == null) return;

        // The screen must really be still: a second look must give the same pixels.
        await FullVncUpdate(vnc!);
        if (!LumaOf(vnc!).AsSpan().SequenceEqual(reference)) Console.WriteLine("  WARNING: the guest's screen changed during the still phase; the comparisons are not meaningful.");

        var psnr = new List<string>();
        var psnrValues = new Dictionary<string, double>();
        for (int i = 0; i < offsets.Length; i++)
        {
            double p = Psnr(_checkpointPictures[i]!, reference);
            psnr.Add($"{offsets[i] / 1000:0.##} s {Db(p)}");
            psnrValues[$"{offsets[i]:0}"] = Math.Round(p, 2);
        }
        Console.WriteLine($"  stream picture vs the real one (luma PSNR, dB, text area): {string.Join(", ", psnr)}");
        results["stillPsnr"] = psnrValues;
        var exact = shown.FirstOrDefault(s => s.Luma.AsSpan().SequenceEqual(reference));
        if (exact.Luma != null)
        {
            Console.WriteLine($"  stream picture exact {Ms(exact.Shown - stop):0} ms after the stop key ({(exact.Repair ? "a repair" : "a frame")})");
            results["streamExactMs"] = Ms(exact.Shown - stop);
        }
        else Console.WriteLine("  stream picture never exact");

        // Without repairs, a viewer with VNC showed this from switchAt until the full update came refreshMs later.
        double stale = Psnr(atSwitch!, reference);
        string atSwitchText = stale >= 99 ? "already exact (its once-a-second update came after the stop)" : $"{Db(stale)} dB (an older picture)";
        Console.WriteLine($"  a viewer with VNC and no repairs: switches to its VNC picture {switchAt:0} ms after the stop key, which is then {atSwitchText}, " +
                          $"for {refreshMs:0} ms until the full update ({refreshBytes / 1024} KB): exact {switchAt + refreshMs:0} ms after the stop key");
        results["vncSwitchMs"] = switchAt;
        results["vncSwitchPsnr"] = Math.Round(stale, 2);
        results["rfbFullUpdateMs"] = refreshMs;
        results["rfbFullUpdateKB"] = refreshBytes / 1024;
        results["vncExactMs"] = switchAt + refreshMs;

        // A viewer without VNC and repairs asks for a key frame once the screen has been still for a while.
        var keyDone = new TaskCompletionSource<(FrameRecord, byte[])>(TaskCreationOptions.RunContinuationsAsynchronously);
        long asked = Now;
        lock (Sync) _keyWaiter = (asked, keyDone);
        await conn.SetVideoAsync(null, null, keyframe: true, CancellationToken.None);
        if (await Task.WhenAny(keyDone.Task, Task.Delay(3000)) == keyDone.Task)
        {
            var (kf, picture) = keyDone.Task.Result;
            Console.WriteLine($"  key frame on request: {Ms(kf.Decoded - asked):0} ms, {kf.Bytes / 1024} KB, PSNR {Db(Psnr(picture, reference))} dB");
            results["keyframeMs"] = Ms(kf.Decoded - asked);
            results["keyframeKB"] = kf.Bytes / 1024;
            results["keyframePsnr"] = Math.Round(Psnr(picture, reference), 2);
        }
        else Console.WriteLine("  key frame on request: none within 3 s");
    }

    private static string Db(double psnr) => psnr >= 99 ? "exact" : psnr.ToString("0.0", CultureInfo.InvariantCulture);

    private static void SetSize(int width, int height)
    {
        _width = width;
        _height = height;
        _marker = (width - 120, 120);
        _textWidth = width - 240;
    }

    private static FrameRecord? LastFrame() { lock (Sync) return Frames.Count > 0 ? Frames[^1] : null; }

    /// <summary>The target's red, green and blue squares under the marker, as BT.601 limited-range Y, U, V.</summary>
    private static readonly (int Dx, int Y, int U, int V)[] BeaconSquares = { (20, 81, 90, 240), (80, 145, 54, 34), (140, 41, 240, 110) };

    private static unsafe bool IsBeacon(byte* y, byte* uv, int stride, int width, int height)
    {
        int left = _width - 200, top = 260;
        foreach (var (dx, ey, eu, ev) in BeaconSquares)
        {
            int x = (left + dx) & ~1, row = top & ~1;
            if (x + 2 > width || row + 2 > height) return false;
            int luma = y[row * stride + x];
            byte* c = uv + (row / 2) * stride + x;
            if (Math.Abs(luma - ey) > 30 || Math.Abs(c[0] - eu) > 30 || Math.Abs(c[1] - ev) > 30) return false;
        }
        return true;
    }

    private static unsafe int MarkerLuma(byte* y, int stride)
    {
        int sum = 0, n = 0;
        for (int dy = -4; dy <= 4; dy++)
            for (int dx = -4; dx <= 4; dx++) { sum += y[(_marker.Y + dy) * stride + _marker.X + dx]; n++; }
        return sum / n;
    }

    private static unsafe void DecodeLoop(ChannelReader<(long Arrived, AgentFrame Frame)> reader)
    {
        using var decoder = new H264Decoder();
        // The repaired picture, as the viewer keeps it: BGRX, whole once a whole repair arrived.
        byte[]? canvas = null;
        bool complete = false;
        while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (reader.TryRead(out var item))
            {
                var h = item.Frame.Header;
                if (h.Type == AgentMessageType.VideoRepair)
                {
                    canvas ??= new byte[_width * _height * 4];
                    RepairPayload.Decode(item.Frame.Payload, canvas, _width, _height, Environment.ProcessorCount);
                    complete |= h.Keyframe == true;
                    if (!complete) continue;
                    var nv12 = new byte[_width * _height * 3 / 2];
                    PixelConvert.BgraToNv12(canvas, _width * 4, _width, _height, nv12);
                    fixed (byte* p = nv12)
                    {
                        var rec = new FrameRecord(h.Sequence ?? 0, item.Arrived, Now, item.Frame.Payload.Length, h.Keyframe == true,
                            MarkerLuma(p, _width), IsBeacon(p, p + _width * _height, _width, _width, _height), Repair: true);
                        Record(rec, nv12.AsSpan(0, _width * _height).ToArray());
                    }
                    continue;
                }
                int luma = -1;
                bool beacon = false;
                byte[]? picture = null;
                decoder.Decode(item.Frame.Payload.Span, h.Timestamp ?? 0, (in DecodedFrame f) =>
                {
                    if (f.IsTexture) return;
                    byte* y = (byte*)f.Y;
                    luma = MarkerLuma(y, f.Stride);
                    beacon = IsBeacon(y, (byte*)f.UV, f.Stride, f.Width, f.Height);
                    picture = new byte[f.Width * f.Height];
                    for (int row = 0; row < f.Height; row++)
                        new ReadOnlySpan<byte>(y + (long)row * f.Stride, f.Width).CopyTo(picture.AsSpan(row * f.Width));
                });
                if (picture == null) continue;
                Record(new FrameRecord(h.Sequence ?? 0, item.Arrived, Now, item.Frame.Payload.Length, h.Keyframe == true, luma, beacon), picture);
            }
        }
    }

    /// <summary>A picture that reached the screen: stored, and handed to whoever waits for it. <paramref name="picture"/> is its luma (stream only).</summary>
    private static void Record(FrameRecord rec, byte[]? picture)
    {
        lock (Sync)
        {
            Frames.Add(rec);
            if (picture != null)
            {
                // The picture shown until now was the one on screen at every checkpoint since it was decoded.
                for (int i = 0; i < _checkpoints.Length; i++)
                    if (_checkpointPictures[i] == null && _checkpoints[i] < rec.Decoded && _latestY.Length > 0) _checkpointPictures[i] = (byte[])_latestY.Clone();
                _latestY = picture;
                if (rec.Decoded >= _keepAfter && ShownAfterStop.Count < 200) ShownAfterStop.Add((rec.Decoded, rec.Repair, picture));
            }
            if (_waiter is { } w && rec.Arrived >= w.Since && (rec.MarkerLuma >= 128) == w.White) { _waiter = null; w.Done.TrySetResult(rec); }
            if (picture != null && !rec.Repair && _keyWaiter is { } k && rec.Arrived >= k.Since && rec.Keyframe) { _keyWaiter = null; k.Done.TrySetResult((rec, picture)); }
        }
    }

    /// <summary>Read and decode time of each VNC update (--rfb), header to last rectangle.</summary>
    private static readonly List<double> RfbUpdateMs = new();

    /// <summary>The marker's luma and whether the beacon shows, from the VNC framebuffer (BGRX).</summary>
    private static (int Luma, bool Beacon) ReadFramebuffer(Framebuffer fb)
    {
        lock (fb.SyncRoot)
        {
            var px = fb.Pixels;
            int Luma(int x, int y) { int o = fb.Offset(x, y); return (66 * px[o + 2] + 129 * px[o + 1] + 25 * px[o] + 128 >> 8) + 16; }
            int sum = 0, n = 0;
            for (int dy = -4; dy <= 4; dy++)
                for (int dx = -4; dx <= 4; dx++) { sum += Luma(_marker.X + dx, _marker.Y + dy); n++; }
            bool beacon = true;
            int left = _width - 200;
            foreach (var (dx, b, g, r) in new[] { (20, 0, 0, 255), (80, 0, 255, 0), (140, 255, 0, 0) })
            {
                int o = fb.Offset(left + dx, 260);
                beacon &= Math.Abs(px[o] - b) < 40 && Math.Abs(px[o + 1] - g) < 40 && Math.Abs(px[o + 2] - r) < 40;
            }
            return (sum / n, beacon);
        }
    }

    /// <summary>Asks for the whole framebuffer and waits until all of it has arrived; time and bytes.</summary>
    private static async Task<(double Ms, long Bytes)> FullVncUpdate(VncClient vnc)
    {
        long area = (long)vnc.Framebuffer.Width * vnc.Framebuffer.Height, covered = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnUpdate(RfbRect[] rects)
        {
            foreach (var r in rects) covered += (long)r.Width * r.Height;
            if (covered >= area) done.TrySetResult();
        }
        long bytes0 = vnc.Statistics.BytesReceived;
        long t0 = Now;
        vnc.FramebufferUpdated += OnUpdate;
        vnc.RefreshPicture();
        await Task.WhenAny(done.Task, Task.Delay(10000));
        vnc.FramebufferUpdated -= OnUpdate;
        return (Ms(Now - t0), vnc.Statistics.BytesReceived - bytes0);
    }

    /// <summary>The VNC framebuffer as the agent's encoder sees it: BT.601 limited-range luma, the same conversion.</summary>
    private static byte[] LumaOf(VncClient vnc)
    {
        var fb = vnc.Framebuffer;
        byte[] bgrx;
        int w, h;
        lock (fb.SyncRoot) { bgrx = fb.CopyOut(fb.Bounds); w = fb.Width; h = fb.Height; }
        var nv12 = new byte[w * h * 3 / 2];
        PixelConvert.BgraToNv12(bgrx, w * 4, w & ~1, h & ~1, nv12);
        return nv12.AsSpan(0, w * h).ToArray();
    }

    /// <summary>Luma PSNR over the text area (left of the marker column); 99 for identical pictures.</summary>
    private static double Psnr(byte[] picture, byte[] reference)
    {
        if (picture.Length != reference.Length || picture.Length != _width * _height) return double.NaN;
        double sum = 0;
        for (int y = 0; y < _height; y++)
        {
            int row = y * _width;
            for (int x = 0; x < _textWidth; x++)
            {
                int d = picture[row + x] - reference[row + x];
                sum += d * d;
            }
        }
        double mse = sum / ((double)_textWidth * _height);
        return mse == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    private static (string, int) HostPort(string text, int defaultPort)
    {
        int colon = text.LastIndexOf(':');
        return colon > 0 && int.TryParse(text[(colon + 1)..], out var p) ? (text[..colon], p) : (text, defaultPort);
    }

    private static double Percentile(List<double> sorted, double q) => sorted.Count == 0 ? double.NaN : sorted[Math.Min(sorted.Count - 1, (int)Math.Round(q * (sorted.Count - 1)))];

    private static string Stats(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToList();
        return v.Count == 0 ? "no samples" : $"median {Percentile(v, 0.5):0} ms, p90 {Percentile(v, 0.9):0}, max {v[^1]:0} (n={v.Count})";
    }

    private static object Summary(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToList();
        return new { n = v.Count, median = Math.Round(Percentile(v, 0.5), 1), p90 = Math.Round(Percentile(v, 0.9), 1), max = v.Count == 0 ? double.NaN : Math.Round(v[^1], 1) };
    }
}
