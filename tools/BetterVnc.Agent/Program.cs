using System.Text;
using BetterVnc.Agent.Protocol;
using BetterVnc.Agent.Tray;

namespace BetterVnc.Agent;

// BetterVNC guest agent. Run it inside the VM (or on the Mac) in the logged-in user's session; the viewer connects
// to it next to the VNC session, mirrors images, files and HTML between the two clipboards and injects
// high-resolution scrolling (the RFB wheel buttons only move a line per click on macOS).
//
// Viewers are let in either by pairing (the viewer asks, a dialog on this screen asks you to Allow, both sides keep a
// derived key) or by a token/password set in the agent. On Windows the agent lives in the tray, on macOS in the menu
// bar; both open the settings.
public static class Program
{
    private static string Version => typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "?";

    [STAThread]
    public static int Main(string[] args)
    {
        var a = new Queue<string>(args);
        string? configPath = null;
        int? port = null;
        string? listen = null;
        string? token = Environment.GetEnvironmentVariable("BETTERVNC_AGENT_TOKEN");
        bool verbose = false, console = false, dryRun = false;
        string? command = null;
        var rest = new List<string>();

        try
        {
            while (a.Count > 0)
            {
                var arg = a.Dequeue();
                switch (arg)
                {
                    case "--config": configPath = Need(a, arg); break;
                    case "--port": port = int.Parse(Need(a, arg)); break;
                    case "--listen": listen = Need(a, arg); break;
                    case "--token": token = Need(a, arg); break;
                    case "--verbose" or "-v": verbose = true; break;
                    case "--console": console = true; break;
                    case "--dry-run": dryRun = true; break;
                    case "--help" or "-h" or "/?": PrintUsage(); return 0;
                    default:
                        if (command == null && !arg.StartsWith('-')) command = arg;
                        else rest.Add(arg);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        configPath ??= AgentConfig.DefaultPath;

        switch (command)
        {
            case null:
            case "run":
                return Serve(configPath, port, listen, token, verbose, console);
            case "install":
                return Installer.Install(dryRun, port, listen);
            case "uninstall":
                return Installer.Uninstall(dryRun);
            case "probe":
                return Probe(rest).GetAwaiter().GetResult();
            case "init":
            {
                var cfg = AgentConfig.CreateNew();
                if (port is { } p) cfg.Port = p;
                if (listen != null) cfg.Listen = listen;
                cfg.Token = string.IsNullOrWhiteSpace(token) ? AgentAuth.NewToken() : token;
                cfg.Save(configPath);
                Console.WriteLine($"Wrote {configPath}");
                Console.WriteLine($"Token: {cfg.Token}");
                Console.WriteLine("Pairing stays on as well; enter the token in BetterVNC only if you choose token mode there.");
                return 0;
            }
            case "show-token":
                return Token(configPath, new List<string> { "show" }, token);
            case "token":
                return Token(configPath, rest, token);
            case "viewers":
                return Viewers(configPath, rest);
            case "pairing":
                return PairingSwitch(configPath, rest);
            default:
                Console.Error.WriteLine($"Unknown command '{command}'.");
                PrintUsage();
                return 2;
        }
    }

    private static string Need(Queue<string> a, string option)
    {
        if (a.Count == 0) throw new ArgumentException($"{option} needs a value.");
        return a.Dequeue();
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            BetterVNC guest agent

              bettervnc-agent install [--port <n>] [--listen <addr>] [--dry-run]
                                         copy into place, register to start at login (scheduled task / LaunchAgent /
                                         systemd user service) and start now
              bettervnc-agent uninstall [--dry-run]
              bettervnc-agent [--config <file>] [--port <n>] [--listen <addr>] [--token <t>] [--verbose] [--console]
                                         serve; on Windows in the tray and on macOS in the menu bar unless --console
              bettervnc-agent token show | set <value> | generate | clear      token or password for token-mode viewers
              bettervnc-agent viewers [remove <name or id>]                     paired viewers
              bettervnc-agent pairing on | off                                  accept new pairings
              bettervnc-agent init [--config <file>]                            create a config with a token
              bettervnc-agent probe <host[:port]> <token or 'pair'> info | watch | get <text|html|png|files> [out]
                                                            | set text <string> | set png <file> | set files <path>...
                                                            | scroll <dx px> <dy px> [steps]   (dy > 0 scrolls up)
                                                            | video [kbit/s] [fps] [seconds] [first-frame.jpg|.h264|-] [codec|-]
                                                                    [WxH [WxH]]   (viewer area at start, and halfway)

            Without a command the agent starts serving and creates bettervnc-agent.json next to the executable on first
            start. Viewers get in by pairing (you click Allow in a dialog on this screen) or with the token. The file also
            holds port, listen, maxPayloadMb, enableVideo (screen streaming for the viewer's Stream picture modes) and the
            scroll settings (enableScroll, scrollScale, scrollInvert, scrollPhases).
            """);
    }

    /// <summary>"1600x1000" as a viewer's picture area for the probe's video command.</summary>
    private static (int Width, int Height) ParseArea(string text)
    {
        var parts = text.Split('x', 'X');
        if (parts.Length == 2 && int.TryParse(parts[0], out int width) && int.TryParse(parts[1], out int height) && width >= 0 && height >= 0)
            return (width, height);
        throw new FormatException($"'{text}' is not a size such as 1600x1000.");
    }

    private static AgentConfig LoadOrCreate(string configPath, out bool created)
    {
        var cfg = AgentConfig.Load(configPath);
        created = cfg == null;
        if (cfg == null) { cfg = AgentConfig.CreateNew(); cfg.Save(configPath); }
        return cfg;
    }

    // ---------------------------------------------------------------------------------------------- serve

    private static int Serve(string configPath, int? port, string? listen, string? token, bool verbose, bool console)
    {
        var cfg = LoadOrCreate(configPath, out var created);
        if (port is { } p) cfg.Port = p;
        if (listen != null) cfg.Listen = listen;
        if (!string.IsNullOrWhiteSpace(token)) cfg.Token = token;
#if WINDOWS
        // Before any window or screen capture: otherwise GDI hands a DPI-unaware process a scaled-down picture of the screen.
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
#endif

        AgentHost host;
        try { host = new AgentHost(configPath, cfg, verbose); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Cannot access the clipboard: " + ex.Message);
            return 3;
        }
        host.Logged += line =>
        {
            // After the tray detached the console (or when started hidden) writes can fail; the log file still has everything.
            try { Console.WriteLine(line); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        };

        Console.WriteLine($"BetterVNC guest agent {Version} on {host.Clipboard.OsName}");
        Console.WriteLine($"Config: {configPath}  ({cfg.AuthSummary})");
        Console.WriteLine($"Scroll injection: {host.InputDescription}");
        Console.WriteLine($"Screen streaming: {host.VideoDescription}");
#if !WINDOWS
        if (host.Input is Input.MacInput mac && !mac.IsTrusted(prompt: false))
            Console.WriteLine("  Accessibility permission missing: System Settings > Privacy & Security > Accessibility > add bettervnc-agent. Scroll events are dropped until then.");
#endif
        if (created) Console.WriteLine("New config written. Connect from BetterVNC and click Allow in the dialog that appears here, or set a token with 'bettervnc-agent token generate'.");

        try { host.Start(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Cannot listen on {cfg.Listen}:{cfg.Port}: {ex.Message}");
            if (console) return 4;
            // In tray / menu bar mode stay alive so the port can be changed in the settings.
        }

        try
        {
#if WINDOWS
            if (!console) { TrayApp.Run(host); return 0; }
#else
            if (!console && OperatingSystem.IsMacOS()) { MacStatusItem.Run(host); return 0; }
#endif
            if (!host.IsListening) return 4;
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();
            Console.WriteLine("Press Ctrl+C to stop.");
            try { Task.Delay(Timeout.Infinite, cts.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            Console.WriteLine("Stopping.");
            return 0;
        }
        finally
        {
            try { host.DisposeAsync().AsTask().Wait(3000); } catch { }
        }
    }

    // ---------------------------------------------------------------------------------------------- settings commands

    private static int Token(string configPath, List<string> rest, string? tokenArg)
    {
        var cfg = LoadOrCreate(configPath, out _);
        var verb = rest.Count > 0 ? rest[0] : "show";
        switch (verb)
        {
            case "show":
                Console.WriteLine(string.IsNullOrEmpty(cfg.Token) ? "(no token set; viewers pair by clicking Allow on this screen)" : cfg.Token);
                return 0;
            case "generate":
                cfg.Token = AgentAuth.NewToken();
                break;
            case "set":
                var value = rest.Count > 1 ? string.Join(' ', rest.Skip(1)) : tokenArg;
                if (string.IsNullOrWhiteSpace(value)) { Console.Error.WriteLine("token set needs a value."); return 2; }
                cfg.Token = value.Trim();
                break;
            case "clear":
                cfg.Token = null;
                break;
            default:
                Console.Error.WriteLine("token show | set <value> | generate | clear");
                return 2;
        }
        cfg.Save(configPath);
        Console.WriteLine(cfg.Token == null ? "Token cleared; only paired viewers can connect." : $"Token: {cfg.Token}");
        Console.WriteLine("A running agent picks this up after a restart (or Save in its settings window).");
        return 0;
    }

    private static int Viewers(string configPath, List<string> rest)
    {
        var cfg = LoadOrCreate(configPath, out _);
        if (rest.Count >= 2 && rest[0] == "remove")
        {
            var who = string.Join(' ', rest.Skip(1));
            if (!cfg.RemoveViewer(who)) { Console.Error.WriteLine($"No paired viewer named '{who}'."); return 1; }
            cfg.Save(configPath);
            Console.WriteLine($"Removed {who}. It has to pair again before it can connect.");
            return 0;
        }
        if (cfg.Viewers.Count == 0) { Console.WriteLine("No paired viewers."); return 0; }
        foreach (var v in cfg.Viewers)
            Console.WriteLine($"{v.Name,-24} paired {v.PairedAt:g}  last seen {(v.LastSeen?.ToString("g") ?? "never"),-18} from {v.Address}  id {v.Id}");
        return 0;
    }

    private static int PairingSwitch(string configPath, List<string> rest)
    {
        var cfg = LoadOrCreate(configPath, out _);
        if (rest.Count == 0) { Console.WriteLine(cfg.AllowPairing ? "Pairing is on." : "Pairing is off."); return 0; }
        cfg.AllowPairing = rest[0] switch { "on" => true, "off" => false, _ => cfg.AllowPairing };
        if (!cfg.AllowPairing && string.IsNullOrEmpty(cfg.Token) && cfg.Viewers.Count == 0)
            Console.WriteLine("Warning: pairing off, no token and no paired viewers: nothing can connect until you set a token.");
        cfg.Save(configPath);
        Console.WriteLine(cfg.AllowPairing ? "Pairing is on." : "Pairing is off.");
        return 0;
    }

    // ---------------------------------------------------------------------------------------------- probe

    private static async Task<int> Probe(List<string> rest)
    {
        if (rest.Count < 3) { PrintUsage(); return 2; }
        var target = rest[0];
        var host = target;
        int port = AgentServer.DefaultPort;
        int colon = target.LastIndexOf(':');
        if (colon > 0 && int.TryParse(target[(colon + 1)..], out var p)) { host = target[..colon]; port = p; }
        var token = rest[1];
        var verb = rest[2];
        var args = rest.Skip(3).ToList();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        // "pair" instead of a token: pair like the viewer does (a dialog appears on the agent's screen).
        bool pair = token == "pair";
        using var conn = new AgentConnection(new AgentConnectionOptions
        {
            Host = host,
            Port = port,
            Token = pair ? null : token,
            ViewerId = pair ? "probe-" + Environment.MachineName.ToLowerInvariant() : null,
            ViewerName = pair ? $"bettervnc-agent probe on {Environment.MachineName}" : null,
            AppName = "bettervnc-agent probe",
        });
        try
        {
            if (pair)
            {
                Console.WriteLine("Pairing: click Allow in the dialog on the agent's screen…");
                await conn.PairAsync(cts.Token);
                Console.WriteLine("Paired (the agent remembers this probe until you remove it from its paired viewers).");
            }
            else
            {
                await conn.ConnectAsync(cts.Token);
            }
            switch (verb)
            {
                case "info":
                {
                    var rtt = await conn.PingAsync(cts.Token);
                    Console.WriteLine($"Agent: {conn.RemoteApp} on {conn.RemoteOs}, protocol {conn.RemoteVersion}, ping {rtt.TotalMilliseconds:0.0} ms");
                    Console.WriteLine($"Clipboard formats: {(conn.RemoteFormats.Length == 0 ? "(empty)" : string.Join(", ", conn.RemoteFormats))}");
                    Console.WriteLine($"Features: {(conn.RemoteFeatures.Length == 0 ? "(none)" : string.Join(", ", conn.RemoteFeatures))}");
                    return 0;
                }
                case "watch":
                {
                    Console.WriteLine($"Connected to {conn.RemoteApp} on {conn.RemoteOs}; waiting for clipboard changes (Ctrl+C to stop).");
                    conn.ClipboardChanged += m =>
                    {
                        var files = m.Files == null ? "" : " " + string.Join(", ", m.Files.Select(f => $"{f.Name} ({f.Size} bytes)"));
                        Console.WriteLine($"{DateTime.Now:HH:mm:ss} clipboardChanged: {string.Join(", ", m.Formats ?? Array.Empty<string>())}{files}");
                    };
                    conn.Disconnected += ex => { Console.WriteLine("Disconnected: " + (ex?.Message ?? "closed")); cts.Cancel(); };
                    try { await Task.Delay(Timeout.Infinite, cts.Token); } catch (OperationCanceledException) { }
                    return 0;
                }
                case "get":
                {
                    if (args.Count < 1) { PrintUsage(); return 2; }
                    var format = args[0];
                    var frame = await conn.GetClipboardAsync(format, cts.Token);
                    if (frame == null) { Console.WriteLine($"The guest clipboard has no '{format}' content."); return 1; }
                    if (format == AgentFormat.Files)
                    {
                        var dir = args.Count > 1 ? args[1] : Path.Combine(Environment.CurrentDirectory, "clipboard-files");
                        var written = FileBundle.Unpack(frame.Header.Files ?? new(), frame.Payload, dir);
                        Console.WriteLine($"Wrote {frame.Header.Files?.Count ?? 0} file(s), {frame.Payload.Length} bytes:");
                        foreach (var w in written) Console.WriteLine("  " + w);
                    }
                    else if (args.Count > 1)
                    {
                        await File.WriteAllBytesAsync(args[1], frame.Payload.ToArray(), cts.Token);
                        Console.WriteLine($"Wrote {frame.Payload.Length} bytes to {args[1]}");
                    }
                    else if (format == AgentFormat.Png)
                    {
                        Console.WriteLine($"PNG, {frame.Payload.Length} bytes (give an output file name to save it).");
                    }
                    else
                    {
                        Console.Out.Write(Encoding.UTF8.GetString(frame.Payload.Span));
                        Console.Out.WriteLine();
                    }
                    return 0;
                }
                case "set":
                {
                    if (args.Count < 2) { PrintUsage(); return 2; }
                    switch (args[0])
                    {
                        case AgentFormat.Text:
                        case AgentFormat.Html:
                            await conn.SetClipboardAsync(args[0], Encoding.UTF8.GetBytes(string.Join(' ', args.Skip(1))), cts.Token);
                            break;
                        case AgentFormat.Png:
                            await conn.SetClipboardAsync(AgentFormat.Png, await File.ReadAllBytesAsync(args[1], cts.Token), cts.Token);
                            break;
                        case AgentFormat.Files:
                            await conn.SetClipboardFilesAsync(args.Skip(1), cts.Token);
                            break;
                        default:
                            Console.Error.WriteLine($"Unknown format '{args[0]}'.");
                            return 2;
                    }
                    Console.WriteLine("Guest clipboard updated.");
                    return 0;
                }
                case "scroll":
                {
                    if (args.Count < 2) { PrintUsage(); return 2; }
                    if (!conn.SupportsScroll) { Console.Error.WriteLine("This agent does not support scroll injection (old version, or enableScroll is false)."); return 1; }
                    double dx = double.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture);
                    double dy = double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
                    int steps = args.Count > 2 ? Math.Max(1, int.Parse(args[2])) : 8;
                    // Send the movement as a gesture of several frames, as the viewer does for a turning wheel.
                    for (int i = 0; i < steps; i++)
                    {
                        var phase = i == 0 ? AgentScrollPhase.Begin : AgentScrollPhase.Continue;
                        await conn.ScrollAsync(dx / steps, dy / steps, null, null, phase, null, null, cts.Token);
                        await Task.Delay(16, cts.Token);
                    }
                    await conn.ScrollAsync(0, 0, null, null, AgentScrollPhase.End, null, null, cts.Token);
                    await conn.PingAsync(cts.Token); // make sure everything was processed before we hang up
                    Console.WriteLine($"Scrolled dx={dx}, dy={dy} px in {steps} steps on {conn.RemoteOs}.");
                    return 0;
                }
                case "video":
                {
                    if (!conn.SupportsVideo)
                    {
                        Console.Error.WriteLine($"This agent does not stream its screen (features: {(conn.RemoteFeatures.Length == 0 ? "none" : string.Join(", ", conn.RemoteFeatures))}; old version, Linux, macOS before 12.3, or enableVideo is false; the agent's log says why).");
                        return 1;
                    }
                    int bitrate = args.Count > 0 ? int.Parse(args[0]) : 8000;
                    int fps = args.Count > 1 ? int.Parse(args[1]) : 30;
                    int seconds = args.Count > 2 ? int.Parse(args[2]) : 5;
                    string? saveTo = args.Count > 3 && args[3] != "-" ? args[3] : null;
                    (int Width, int Height)? area = args.Count > 5 ? ParseArea(args[5]) : null;
                    (int Width, int Height)? laterArea = args.Count > 6 ? ParseArea(args[6]) : null;
                    long frames = 0, bytes = 0, keyframes = 0;
                    int w = 0, h = 0;
                    string at = "";
                    byte[]? first = null;
                    var sizes = new List<string>(); // every frame size seen, in order
                    conn.VideoFrameReceived += f =>
                    {
                        Interlocked.Increment(ref frames);
                        Interlocked.Add(ref bytes, f.Payload.Length);
                        if (f.Header.Keyframe == true) Interlocked.Increment(ref keyframes);
                        w = f.Header.Width ?? w;
                        h = f.Header.Height ?? h;
                        at = f.Header.OffsetX is { } ox ? $" at {ox},{f.Header.OffsetY ?? 0}" : "";
                        lock (sizes) { if (sizes.Count == 0 || sizes[^1] != $"{w}x{h}") sizes.Add($"{w}x{h}"); }
                        first ??= f.Payload.ToArray();
                    };
                    conn.VideoStopped += r => Console.WriteLine("Stream stopped by the agent: " + (r ?? "no reason given"));
                    var codecs = args.Count > 4 && args[4] != "-" ? new[] { args[4] } : new[] { AgentVideoCodec.H264, AgentVideoCodec.Jpeg };
                    var info = await conn.StartVideoAsync(codecs, bitrate, fps, area, cts.Token);
                    Console.WriteLine($"Streaming {info.Codec} {info.Width}x{info.Height}{(info.OffsetX is { } iox ? $" at {iox},{info.OffsetY ?? 0} of the desktop (part of it)" : "")}, {info.BitrateKbps} kbit/s, {info.MaxFps} fps max, {info.EncoderKind} encoder{(area is { } a ? $", viewer area {a.Width}x{a.Height}" : "")}.");
                    Console.WriteLine("Move something on the agent's screen: unchanged frames are not sent.");
                    long lastFrames = 0, lastBytes = 0;
                    for (int i = 0; i < seconds && !cts.IsCancellationRequested; i++)
                    {
                        if (laterArea is { } later && i == seconds / 2)
                        {
                            // As a viewer whose window was resized: the agent rescales, and a key frame shows the new size
                            // even when nothing on the screen moves.
                            await conn.SetVideoAreaAsync(later.Width, later.Height, cts.Token);
                            await conn.SetVideoAsync(null, null, keyframe: true, cts.Token);
                            Console.WriteLine($"Viewer area now {later.Width}x{later.Height}.");
                        }
                        try { await Task.Delay(1000, cts.Token); } catch (OperationCanceledException) { break; }
                        long f = Interlocked.Read(ref frames), b = Interlocked.Read(ref bytes);
                        Console.WriteLine($"  {i + 1,3} s  {f - lastFrames,4} frames  {(b - lastBytes) * 8 / 1000.0,8:0} kbit/s  {w}x{h}{at}");
                        lastFrames = f;
                        lastBytes = b;
                    }
                    await conn.StopVideoAsync(CancellationToken.None);
                    Console.WriteLine($"Total: {frames} frames ({keyframes} key frames), {bytes / 1024} KB.");
                    lock (sizes) Console.WriteLine($"Frame sizes: {(sizes.Count == 0 ? "none" : string.Join(" then ", sizes))}.");
                    if (saveTo != null && first != null)
                    {
                        await File.WriteAllBytesAsync(saveTo, first);
                        Console.WriteLine($"First frame written to {saveTo}");
                    }
                    return 0;
                }
                default:
                    PrintUsage();
                    return 2;
            }
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}
