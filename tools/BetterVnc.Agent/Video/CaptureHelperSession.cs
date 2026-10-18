using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using BetterVnc.Agent.Protocol;

namespace BetterVnc.Agent.Video;

/// <summary>
/// A stream that a separate capture program produces (bettervnc-capture on macOS, see <see cref="MacScreenSource"/>),
/// read from its standard output. Each message is a 24-byte little-endian header and a payload:
/// <code>
///  0  u32  magic "BVC1"
///  4  u8   kind: 1 frame (one Annex B access unit), 2 info (UTF-8 key=value lines), 3 error (UTF-8 text; the helper exits)
///  5  u8   flags: bit 0 = key frame
///  6  u16  reserved
///  8  u16  frame width
/// 10  u16  frame height
/// 12  u32  payload length
/// 16  i64  capture time in microseconds
/// </code>
/// Info comes first, once capture runs, and again when the size, the encoder or the display layout changes. Its keys are
/// <c>width</c>, <c>height</c>, <c>encoder</c> (hardware or software), <c>description</c> and, when the captured display is
/// one of several, <c>offsetX</c>/<c>offsetY</c>. Commands go the other way as text lines on standard input:
/// <c>bitrate &lt;kbit/s&gt;</c>, <c>fps &lt;n&gt;</c>, <c>keyframe</c>, <c>size &lt;w&gt; &lt;h&gt;</c> and <c>quit</c>; the helper also
/// stops when its standard input closes. Like the Windows capture, the helper sends nothing while the screen is still.
/// </summary>
internal sealed class CaptureHelperSession : IVideoSession
{
    public const uint Magic = 0x31435642; // "BVC1" read as a little-endian number
    public const int HeaderSize = 24;
    public const byte KindFrame = 1, KindInfo = 2, KindError = 3;
    private const int MaxPayload = 64 * 1024 * 1024;

    private readonly Stream _fromHelper;
    private readonly Stream _toHelper;
    private readonly Process? _process;
    private readonly Action<VideoFrame> _onFrame;
    private readonly Action<string?> _onEnded;
    private readonly Action<string>? _log;
    private readonly Thread _thread;
    private readonly object _writeLock = new();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile Info _info = new(0, 0, false, null, "");
    private volatile int _bitrateKbps, _maxFps;
    private volatile bool _disposed;
    private string? _error;

    private sealed record Info(int Width, int Height, bool Hardware, (int X, int Y)? Offset, string Description);

    /// <param name="process">The helper, when there is one to wait for and kill on <see cref="Dispose"/>.</param>
    public CaptureHelperSession(VideoRequest request, Stream fromHelper, Stream toHelper, Process? process,
        Action<VideoFrame> onFrame, Action<string?> onEnded, Action<string>? log)
    {
        Codec = request.Codec;
        _bitrateKbps = request.BitrateKbps;
        _maxFps = Math.Clamp(request.MaxFps, 1, 120);
        _fromHelper = fromHelper;
        _toHelper = toHelper;
        _process = process;
        _onFrame = onFrame;
        _onEnded = onEnded;
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = "screen capture helper" };
    }

    public string Codec { get; }
    public int BitrateKbps => _bitrateKbps;
    public int MaxFps => _maxFps;
    public int Width => _info.Width;
    public int Height => _info.Height;
    public (int X, int Y)? Offset => _info.Offset;
    public string EncoderKind => _info.Hardware ? AgentVideoEncoder.Hardware : AgentVideoEncoder.Software;
    public string Description => _info.Description;

    /// <summary>
    /// Starts reading and waits until the helper says capture runs. Throws with the helper's own reason when it gives
    /// up first (no permission, no display), and disposes the session then.
    /// </summary>
    public void Start(TimeSpan timeout)
    {
        _thread.Start();
        try
        {
            _started.Task.WaitAsync(timeout).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            Dispose();
            throw new TimeoutException($"The capture helper did not start capturing within {timeout.TotalSeconds:0} s.");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Set(int? bitrateKbps, int? maxFps)
    {
        if (bitrateKbps is { } b)
        {
            _bitrateKbps = b;
            Send(FormattableString.Invariant($"bitrate {b}"));
        }
        if (maxFps is { } f)
        {
            _maxFps = Math.Clamp(f, 1, 120);
            Send(FormattableString.Invariant($"fps {_maxFps}"));
        }
    }

    public void RequestKeyframe() => Send("keyframe");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Send("quit");
        lock (_writeLock)
        {
            try { _toHelper.Dispose(); } catch { }
        }
        if (_process != null)
        {
            try { if (!_process.WaitForExit(2000)) _process.Kill(); } catch { }
        }
        if (Thread.CurrentThread != _thread && _thread.IsAlive) _thread.Join(3000);
        _process?.Dispose();
    }

    private void Send(string command)
    {
        var line = Encoding.ASCII.GetBytes(command + "\n");
        lock (_writeLock)
        {
            try
            {
                _toHelper.Write(line);
                _toHelper.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException)
            {
                // The helper is gone; the reader finds out why.
            }
        }
    }

    private void Run()
    {
        var header = new byte[HeaderSize];
        var payload = new byte[256 * 1024];
        string? reason = null;
        try
        {
            while (true)
            {
                if (_fromHelper.ReadAtLeast(header, HeaderSize, throwOnEndOfStream: false) < HeaderSize) break;
                if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic)
                {
                    reason = "The capture helper sent data this agent does not understand.";
                    break;
                }
                byte kind = header[4], flags = header[5];
                int width = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8));
                int height = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10));
                uint length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
                long timestamp = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16));
                if (length > MaxPayload)
                {
                    reason = $"The capture helper sent a message of {length} bytes.";
                    break;
                }
                int size = (int)length;
                if (payload.Length < size) payload = new byte[Math.Max(size, payload.Length * 2)];
                if (_fromHelper.ReadAtLeast(payload.AsSpan(0, size), size, throwOnEndOfStream: false) < size) break;

                switch (kind)
                {
                    case KindFrame:
                        if (_disposed) break;
                        var at = _info.Offset;
                        _onFrame(new VideoFrame(payload.AsMemory(0, size), (flags & 1) != 0, timestamp, width, height, at?.X, at?.Y));
                        break;
                    case KindInfo:
                        _info = ParseInfo(Encoding.UTF8.GetString(payload, 0, size));
                        _started.TrySetResult();
                        break;
                    case KindError:
                        _error = Encoding.UTF8.GetString(payload, 0, size).Trim();
                        break;
                    // Anything else is skipped: a newer helper may say more than this agent knows.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The pipe closed under us: the helper died, or Dispose killed it.
        }
        catch (Exception ex) when (!_disposed)
        {
            reason = "Screen streaming stopped: " + ex.Message;
        }
        reason ??= _error ?? ExitReason();
        if (_started.TrySetException(new InvalidOperationException(reason))) return; // Start reports it
        if (_disposed) return;
        _log?.Invoke(reason);
        try { _onEnded(reason); } catch { }
    }

    private string ExitReason()
    {
        try
        {
            if (_process != null && !_disposed && _process.WaitForExit(2000))
                return $"The capture helper exited with code {_process.ExitCode}.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
        return "The capture helper exited.";
    }

    private static Info ParseInfo(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            int eq = line.IndexOf('=');
            if (eq > 0) values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        int Number(string key) => values.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        (int X, int Y)? offset = values.ContainsKey("offsetX") || values.ContainsKey("offsetY") ? (Number("offsetX"), Number("offsetY")) : null;
        return new Info(Number("width"), Number("height"), values.GetValueOrDefault("encoder") == "hardware", offset,
            values.GetValueOrDefault("description") ?? "");
    }
}
