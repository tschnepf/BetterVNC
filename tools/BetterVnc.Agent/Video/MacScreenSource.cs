using System.Diagnostics;
using System.Globalization;
using BetterVnc.Agent.Protocol;

namespace BetterVnc.Agent.Video;

/// <summary>
/// Screen streaming for the macOS agent. Capture and encoding live in bettervnc-capture, a small Swift program installed
/// next to the agent (source in tools/BetterVnc.Agent.MacCapture): ScreenCaptureKit captures the display and scales it
/// on the GPU, VideoToolbox encodes it, in hardware on every Mac that has ScreenCaptureKit, and the access units come
/// over a pipe (<see cref="CaptureHelperSession"/>). Each stream gets its own helper process. ScreenCaptureKit delivers
/// a frame only when the screen changed, so a still screen sends nothing and the viewer's lossless idle fallback keeps
/// working, as with the Windows agent. macOS asks for the Screen Recording permission when the first stream starts.
/// </summary>
public sealed class MacScreenSource : IGuestVideoSource
{
    public const string HelperName = "bettervnc-capture";
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);

    private readonly string _helper;
    private readonly Dictionary<string, string> _probe;

    /// <summary>Finds the helper and asks it which encoders this Mac has; throws when streaming is impossible here.</summary>
    public MacScreenSource()
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(12, 3)) throw new PlatformNotSupportedException("screen streaming needs macOS 12.3 or later (ScreenCaptureKit).");
        _helper = FindHelper() ?? throw new FileNotFoundException($"{HelperName} is not installed next to the agent; reinstall the agent from the macOS package or tarball.");
        _probe = Probe(_helper);
        var codecs = new List<string>();
        if (HasEncoder("h264")) codecs.Add(AgentVideoCodec.H264);
        if (HasEncoder("hevc")) codecs.Add(AgentVideoCodec.Hevc);
        if (codecs.Count == 0) throw new InvalidOperationException("VideoToolbox offers no H.264 or HEVC encoder on this Mac.");
        Codecs = codecs;
    }

    public IReadOnlyList<string> Codecs { get; }

    public Action<string>? Log { get; set; }

    /// <summary>Called when a stream could not start because macOS does not allow screen recording (the menu bar then offers its permission wizard).</summary>
    public Action? PermissionMissing { get; set; }

    public string Description
    {
        get
        {
            var encoders = string.Join(" and ", new[] { ("H.264", "h264"), ("HEVC", "hevc") }
                .Where(c => HasEncoder(c.Item2)).Select(c => $"{c.Item1} ({_probe[c.Item2]})"));
            var text = $"ScreenCaptureKit capture, VideoToolbox {encoders}";
            if (_probe.GetValueOrDefault("screenRecording") == "denied")
                text += "; Screen Recording permission not granted yet (macOS asks when the first stream starts)";
            return text;
        }
    }

    /// <summary>The helper next to the running agent (following a symbolic link such as /usr/local/bin/bettervnc-agent); null when there is none.</summary>
    public static string? FindHelper()
    {
        var self = Environment.ProcessPath;
        if (self != null)
        {
            try { self = File.ResolveLinkTarget(self, returnFinalTarget: true)?.FullName ?? self; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        foreach (var dir in new[] { Path.GetDirectoryName(self), AppContext.BaseDirectory })
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var path = Path.Combine(dir, HelperName);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public IVideoSession Start(VideoRequest request, Action<VideoFrame> onFrame, Action<string?> onEnded)
    {
        string codec;
        if (string.Equals(request.Codec, AgentVideoCodec.H264, StringComparison.OrdinalIgnoreCase)) codec = "h264";
        else if (string.Equals(request.Codec, AgentVideoCodec.Hevc, StringComparison.OrdinalIgnoreCase)) codec = "hevc";
        else throw new NotSupportedException($"Codec '{request.Codec}' is not available; this agent encodes {string.Join(" and ", Codecs)}.");

        var psi = new ProcessStartInfo(_helper)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in HelperArguments(codec, request))
            psi.ArgumentList.Add(arg);
        var process = Process.Start(psi) ?? throw new InvalidOperationException($"{HelperName} did not start.");
        var log = Log;
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log?.Invoke("Screen capture: " + e.Data); };
        process.BeginErrorReadLine();

        var session = new CaptureHelperSession(request, process.StandardOutput.BaseStream, process.StandardInput.BaseStream, process, onFrame, onEnded, log);
        try { session.Start(StartTimeout); }
        catch
        {
            if (PermissionMissing is { } missing && !MacPermissions.ScreenRecording) missing();
            throw;
        }
        Log?.Invoke($"{(codec == "hevc" ? "HEVC" : "H.264")} stream: {session.Description}");
        return session;
    }

    /// <summary>The helper's command line for one stream (options in tools/BetterVnc.Agent.MacCapture/Sources/main.swift).</summary>
    internal static List<string> HelperArguments(string codec, VideoRequest request)
    {
        var args = new List<string> { "--codec", codec, "--bitrate", Invariant(request.BitrateKbps), "--fps", Invariant(Math.Clamp(request.MaxFps, 1, 120)) };
        // The viewer's picture area: ScreenCaptureKit scales the display down to fit it, on the GPU, so the viewer shows
        // the stream 1:1. Later changes reach the running helper as "size" commands (CaptureHelperSession.SetMaxSize).
        if (request.MaxWidth > 0) args.AddRange(new[] { "--max-width", Invariant(request.MaxWidth) });
        if (request.MaxHeight > 0) args.AddRange(new[] { "--max-height", Invariant(request.MaxHeight) });
        return args;
    }

    private bool HasEncoder(string codec) => _probe.GetValueOrDefault(codec) is "hardware" or "software";

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Runs <c>bettervnc-capture --probe</c>: key=value lines (macos, screenRecording, h264, hevc).</summary>
    private static Dictionary<string, string> Probe(string helper)
    {
        var psi = new ProcessStartInfo(helper) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("--probe");
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"{HelperName} did not start.");
        var output = p.StandardOutput.ReadToEndAsync();
        var errors = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(10_000))
        {
            try { p.Kill(); } catch { }
            throw new TimeoutException($"{HelperName} --probe did not finish.");
        }
        if (p.ExitCode != 0) throw new InvalidOperationException($"{HelperName} --probe failed with code {p.ExitCode}: {errors.Result.Trim()}");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Result.Split('\n'))
        {
            int eq = line.IndexOf('=');
            if (eq > 0) values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return values;
    }
}
