using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BetterVnc.Agent.Protocol;
using BetterVnc.App.Controls;
using BetterVnc.App.Models;
using static BetterVnc.App.Input.NativeMethods;

namespace BetterVnc.App.Services;

/// <summary>
/// Keeps a connection to the BetterVNC guest agent next to a VNC session and mirrors the rich clipboard both ways:
/// images and files copied in the guest land on the Windows clipboard, images and dropped files go to the guest.
/// Reconnects by itself; everything it raises arrives on the window's dispatcher.
/// </summary>
public sealed class AgentClient : IDisposable, IScrollInjector
{
    /// <summary>Files copied in the guest are pulled automatically up to this total; bigger sets are only announced.</summary>
    public const long AutoFetchFileLimit = 64L * 1024 * 1024;

    private readonly ConnectionProfile _profile;
    private readonly string _host;
    private readonly string _token;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _cts = new();
    private AgentConnection? _connection;
    private uint _ownClipboardSequence;
    private byte[]? _lastReceivedPngHash;
    private byte[]? _lastSentPngHash;
    private bool _disposed;

    public AgentClient(ConnectionProfile profile, string vncHost, Dispatcher dispatcher)
    {
        _profile = profile;
        _dispatcher = dispatcher;
        _host = string.IsNullOrWhiteSpace(profile.AgentHost) ? vncHost : profile.AgentHost!.Trim();
        _token = ProfileStore.Unprotect(profile.AgentProtectedToken) ?? "";
    }

    public static bool IsConfigured(ConnectionProfile p) =>
        p.AgentEnabled && (p.AgentAuth == AgentAuthMode.Pair || !string.IsNullOrEmpty(p.AgentProtectedToken));

    public bool IsConnected => _connection?.IsConnected == true;
    /// <summary>True when the agent is connected and accepts high-resolution scroll messages.</summary>
    public bool CanScroll => _connection is { IsConnected: true, SupportsScroll: true };
    public string? RemoteOs => _connection?.RemoteOs;
    public string? RemoteApp => _connection?.RemoteApp;

    // ------------------------------------------------------------------ video

    /// <summary>Codecs this viewer can decode, most preferred first. JPEG (motion JPEG) needs nothing beyond WIC.</summary>
    public static readonly string[] DecodableCodecs = { AgentVideoCodec.Jpeg };

    /// <summary>Codecs both sides agree on, in this viewer's order of preference; empty when streaming is impossible.</summary>
    public string[] StreamCodecs
    {
        get
        {
            var remote = _connection?.RemoteCodecs;
            return remote == null ? Array.Empty<string>() : DecodableCodecs.Where(c => remote.Contains(c, StringComparer.OrdinalIgnoreCase)).ToArray();
        }
    }

    /// <summary>True when the agent is connected, streams video, and in a codec this viewer decodes.</summary>
    public bool CanStream => _connection is { IsConnected: true, SupportsVideo: true } && StreamCodecs.Length > 0;

    /// <summary>Why <see cref="CanStream"/> is false, for a tooltip; null when it is true.</summary>
    public string? StreamUnavailableReason
    {
        get
        {
            var conn = _connection;
            if (conn == null || !conn.IsConnected) return $"The guest agent is not connected ({Status}).";
            if (!conn.SupportsVideo) return $"The guest agent ({conn.RemoteApp} on {conn.RemoteOs}) does not stream its screen. Update it, or turn screen streaming on in its settings.";
            if (StreamCodecs.Length == 0) return $"The agent streams {string.Join(", ", conn.RemoteCodecs)} but this viewer decodes only {string.Join(", ", DecodableCodecs)}.";
            return null;
        }
    }

    /// <summary>Parameters of the running stream; null when none runs.</summary>
    public AgentConnection.VideoStartInfo? StreamInfo { get; private set; }

    /// <summary>An encoded frame arrived; raised on the agent's receive thread, so decode elsewhere and return quickly.</summary>
    public event Action<AgentFrame>? StreamFrame;
    /// <summary>The stream ended without this side asking (agent stopped it, or disconnected); on the dispatcher.</summary>
    public event Action<string?>? StreamStopped;

    public async Task<AgentConnection.VideoStartInfo> StartStreamAsync(int bitrateKbps, int maxFps)
    {
        var conn = _connection ?? throw new InvalidOperationException("The guest agent is not connected.");
        var info = await conn.StartVideoAsync(StreamCodecs, bitrateKbps, maxFps, _cts.Token).ConfigureAwait(false);
        StreamInfo = info;
        return info;
    }

    public async Task SetStreamAsync(int? bitrateKbps, int? maxFps, bool keyframe = false)
    {
        var conn = _connection;
        if (conn == null || StreamInfo == null) return;
        await conn.SetVideoAsync(bitrateKbps, maxFps, keyframe, _cts.Token).ConfigureAwait(false);
        if (bitrateKbps != null || maxFps != null)
            StreamInfo = StreamInfo with { BitrateKbps = bitrateKbps ?? StreamInfo.BitrateKbps, MaxFps = maxFps ?? StreamInfo.MaxFps };
    }

    public async Task StopStreamAsync()
    {
        var conn = _connection;
        StreamInfo = null;
        if (conn == null || !conn.IsConnected) return;
        try { await conn.StopVideoAsync(_cts.Token).ConfigureAwait(false); }
        catch (Exception) when (!conn.IsConnected) { }
    }
    public string Status { get; private set; } = "agent: connecting";
    public string Target => $"{_host}:{_profile.AgentPort}";

    /// <summary>Connected, disconnected or failed; read <see cref="IsConnected"/> and <see cref="Status"/>.</summary>
    public event Action? StateChanged;
    /// <summary>Short human-readable progress messages for the status bar.</summary>
    public event Action<string>? Message;
    /// <summary>Plain text copied in the guest (UTF-8, unlike the Latin-1 legacy VNC path).</summary>
    public event Action<string>? RemoteTextReceived;

    public void Start()
    {
        if (_profile.AgentAuth == AgentAuthMode.Token && string.IsNullOrEmpty(_token))
        {
            SetStatus("agent: no token");
            return;
        }
        _ = Task.Run(() => ConnectLoopAsync(_cts.Token));
    }

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        var identity = ViewerIdentity.Current;
        bool pairMode = _profile.AgentAuth == AgentAuthMode.Pair;
        string? pairKey = pairMode ? ProfileStore.Unprotect(_profile.AgentProtectedPairKey) : null;
        while (!ct.IsCancellationRequested)
        {
            var conn = new AgentConnection(new AgentConnectionOptions
            {
                Host = _host,
                Port = _profile.AgentPort,
                Token = pairMode ? null : _token,
                PairKey = pairKey,
                ViewerId = identity.Id,
                ViewerName = identity.Name,
                AppName = $"BetterVNC {typeof(AgentClient).Assembly.GetName().Version?.ToString(3)}",
            });
            var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            conn.Disconnected += ex => closed.TrySetResult(ex);
            conn.ClipboardChanged += msg => _ = OnGuestClipboardChangedAsync(conn, msg);
            conn.VideoFrameReceived += frame => { if (StreamInfo != null) StreamFrame?.Invoke(frame); };
            conn.VideoStopped += reason =>
            {
                if (StreamInfo == null) return;
                StreamInfo = null;
                Post(() => StreamStopped?.Invoke(reason ?? "the agent stopped the stream"));
            };
            try
            {
                if (pairMode && pairKey == null)
                {
                    SetStatus("agent: waiting for approval in the remote");
                    Post(() => Message?.Invoke($"Guest agent at {Target}: click Allow in the dialog on the remote screen to pair this computer"));
                    await conn.PairAsync(ct).ConfigureAwait(false);
                    pairKey = conn.PairedKey;
                    var protectedKey = ProfileStore.Protect(pairKey);
                    Post(() =>
                    {
                        _profile.AgentProtectedPairKey = protectedKey;
                        try { App.Profiles.Save(); } catch { }
                    });
                }
                else
                {
                    SetStatus("agent: connecting");
                    await conn.ConnectAsync(ct).ConfigureAwait(false);
                }
                _connection = conn;
                delay = TimeSpan.FromSeconds(2);
                SetStatus($"agent: connected ({conn.RemoteOs})");
                Post(() => Message?.Invoke($"Guest agent connected ({conn.RemoteApp} on {conn.RemoteOs})"));
                var ex = await closed.Task.WaitAsync(ct).ConfigureAwait(false);
                _connection = null;
                if (StreamInfo != null)
                {
                    StreamInfo = null;
                    Post(() => StreamStopped?.Invoke("the guest agent disconnected"));
                }
                SetStatus("agent: reconnecting");
                Post(() => Message?.Invoke("Guest agent disconnected" + (ex != null ? $": {ex.Message}" : "")));
            }
            catch (OperationCanceledException) { break; }
            catch (AgentPairingException ex) when (ex.NeedsPairing && pairMode)
            {
                // The agent no longer knows us (removed in its settings, or reinstalled): pair again right away.
                pairKey = null;
                Post(() =>
                {
                    _profile.AgentProtectedPairKey = null;
                    try { App.Profiles.Save(); } catch { }
                    Message?.Invoke("The guest agent no longer recognises this computer; pairing again");
                });
                conn.Dispose();
                continue;
            }
            catch (AgentPairingException ex)
            {
                SetStatus(ex.Code == AgentErrorCode.PairingDisabled ? "agent: pairing is off in the remote" : "agent: pairing denied in the remote");
                Post(() => Message?.Invoke($"Guest agent at {Target}: {ex.Message}"));
                conn.Dispose();
                return; // the person in the remote decided; retrying would only nag
            }
            catch (AgentAuthenticationException ex)
            {
                SetStatus("agent: token rejected");
                Post(() => Message?.Invoke($"Guest agent at {Target} rejected the token: {ex.Message}"));
                conn.Dispose();
                return; // retrying would not help
            }
            catch (Exception ex)
            {
                SetStatus($"agent: unreachable ({ex.Message})");
            }
            finally
            {
                conn.Dispose();
            }
            try { await Task.Delay(delay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            if (delay < TimeSpan.FromSeconds(15)) delay += delay;
        }
    }

    private void SetStatus(string status)
    {
        Status = status;
        Post(() => StateChanged?.Invoke());
    }

    private void Post(Action action)
    {
        if (_disposed) return;
        try { _dispatcher.BeginInvoke(action); } catch (TaskCanceledException) { }
    }

    // ------------------------------------------------------------------ guest -> local

    private async Task OnGuestClipboardChangedAsync(AgentConnection conn, AgentMessage msg)
    {
        var formats = msg.Formats ?? Array.Empty<string>();
        try
        {
            if (formats.Contains(AgentFormat.Png))
            {
                var frame = await conn.GetClipboardAsync(AgentFormat.Png, _cts.Token).ConfigureAwait(false);
                if (frame == null) return;
                var png = frame.Payload.ToArray();
                var hash = System.Security.Cryptography.SHA256.HashData(png);
                if (_lastSentPngHash != null && hash.AsSpan().SequenceEqual(_lastSentPngHash)) return;         // our own push echoed back
                if (_lastReceivedPngHash != null && hash.AsSpan().SequenceEqual(_lastReceivedPngHash)) return; // already on the local clipboard
                _lastReceivedPngHash = hash;
                await _dispatcher.InvokeAsync(() => SetLocalImage(png));
                Post(() => Message?.Invoke($"Image received from guest clipboard ({FormatSize(png.Length)})"));
            }
            else if (formats.Contains(AgentFormat.Files))
            {
                long total = msg.TotalFileBytes;
                if (total > AutoFetchFileLimit)
                {
                    Post(() => Message?.Invoke($"Guest copied {msg.Files?.Count ?? 0} file(s), {total / (1024 * 1024)} MB: too large to mirror automatically"));
                    return;
                }
                var frame = await conn.GetClipboardAsync(AgentFormat.Files, _cts.Token).ConfigureAwait(false);
                if (frame?.Header.Files == null || frame.Header.Files.Count == 0) return;
                var dir = Path.Combine(ReceiveRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
                var paths = FileBundle.Unpack(frame.Header.Files, frame.Payload, dir);
                await _dispatcher.InvokeAsync(() => SetLocalFiles(paths));
                Post(() => Message?.Invoke($"{frame.Header.Files.Count} file(s) received from guest clipboard, ready to paste"));
            }
            else if (formats.Contains(AgentFormat.Text))
            {
                var frame = await conn.GetClipboardAsync(AgentFormat.Text, _cts.Token).ConfigureAwait(false);
                if (frame == null || frame.Payload.Length == 0) return;
                var text = Encoding.UTF8.GetString(frame.Payload.Span);
                Post(() => RemoteTextReceived?.Invoke(text));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Post(() => Message?.Invoke("Guest clipboard: " + ex.Message));
        }
    }

    private string ReceiveRoot
    {
        get
        {
            var safe = string.Concat(_profile.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var root = Path.Combine(Path.GetTempPath(), "BetterVNC", string.IsNullOrWhiteSpace(safe) ? _profile.Id : safe);
            CleanOld(root);
            return root;
        }
    }

    private static void CleanOld(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var d in Directory.GetDirectories(root))
                if (Directory.GetLastWriteTimeUtc(d) < DateTime.UtcNow.AddDays(-1))
                    try { Directory.Delete(d, true); } catch { }
        }
        catch { }
    }

    private void SetLocalImage(byte[] png)
    {
        var decoder = new PngBitmapDecoder(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var image = decoder.Frames[0];
        var obj = new DataObject();
        obj.SetData("PNG", new MemoryStream(png), false); // keeps alpha for apps that read PNG
        obj.SetImage(image);                               // CF_DIB for everything else
        SetLocalClipboard(obj);
    }

    private void SetLocalFiles(IReadOnlyList<string> paths)
    {
        var list = new StringCollection();
        foreach (var p in paths) list.Add(p);
        var obj = new DataObject();
        obj.SetFileDropList(list);
        obj.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1)), false); // copy, not move
        SetLocalClipboard(obj);
    }

    private void SetLocalClipboard(DataObject obj)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(obj, true);
                break;
            }
            catch (COMException) when (attempt < 5) { Thread.Sleep(50); }
        }
        _ownClipboardSequence = GetClipboardSequenceNumber();
    }

    /// <summary>True when the current clipboard content is what this client just put there (so it must not be sent back).</summary>
    public bool IsOwnClipboardChange() => _ownClipboardSequence != 0 && GetClipboardSequenceNumber() == _ownClipboardSequence;

    // ------------------------------------------------------------------ local -> guest

    /// <summary>Sends the image currently on the local clipboard (as PNG) to the guest clipboard. Call on the UI thread.</summary>
    public async Task<bool> PushClipboardImageAsync()
    {
        var conn = _connection;
        if (conn == null) return false;
        byte[]? png = null;
        for (int attempt = 0; attempt < 3 && png == null; attempt++)
        {
            try { png = ReadClipboardPng(); }
            catch (COMException) { await Task.Delay(40); }
        }
        if (png == null) return false;
        var hash = System.Security.Cryptography.SHA256.HashData(png);
        if (_lastReceivedPngHash != null && hash.AsSpan().SequenceEqual(_lastReceivedPngHash)) return false; // came from the guest
        if (_lastSentPngHash != null && hash.AsSpan().SequenceEqual(_lastSentPngHash)) return false;         // already there
        _lastSentPngHash = hash;
        await conn.SetClipboardAsync(AgentFormat.Png, png, _cts.Token);
        Message?.Invoke($"Image sent to guest clipboard ({FormatSize(png.Length)})");
        return true;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1 << 20 => $"{bytes / (double)(1 << 20):0.0} MB",
        >= 1 << 10 => $"{bytes / (double)(1 << 10):0} KB",
        _ => $"{bytes} bytes",
    };

    private static byte[]? ReadClipboardPng()
    {
        var data = Clipboard.GetDataObject();
        if (data == null) return null;
        if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream ms) return ms.ToArray();
        if (!data.GetDataPresent(DataFormats.Bitmap)) return null;
        var image = Clipboard.GetImage();
        if (image == null) return null;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        return buffer.ToArray();
    }

    public async Task PushFilesAsync(IReadOnlyList<string> paths, IProgress<string>? progress)
    {
        var conn = _connection ?? throw new InvalidOperationException("The guest agent is not connected.");
        progress?.Report($"Sending {paths.Count} item(s) to the guest clipboard…");
        await conn.SetClipboardFilesAsync(paths, _cts.Token);
    }

    /// <summary>
    /// Fire-and-forget scroll injection (see <see cref="IScrollInjector"/>). Returns false when no agent that supports it is
    /// connected, so the caller falls back to VNC wheel clicks. Never throws: a failed send surfaces as a disconnect.
    /// </summary>
    public bool TrySendScroll(double dx, double dy, int x, int y, string phase, double wheelDx, double wheelDy)
    {
        var conn = _connection;
        if (conn == null || !conn.IsConnected || !conn.SupportsScroll) return false;
        try
        {
            _ = conn.ScrollAsync(dx, dy, x, y, phase, wheelDx, wheelDy, _cts.Token)
                .ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            return true;
        }
        catch (Exception) { return false; }
    }

    public async Task PushTextAsync(string text)
    {
        var conn = _connection;
        if (conn == null) return;
        await conn.SetClipboardAsync(AgentFormat.Text, Encoding.UTF8.GetBytes(text), _cts.Token);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _connection?.Dispose();
        _connection = null;
        _cts.Dispose();
    }
}
