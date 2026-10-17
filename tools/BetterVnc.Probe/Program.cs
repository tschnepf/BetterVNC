using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BetterVnc.Protocol;

// Connects to a VNC server with the protocol library and prints every event for diagnosis.
// Usage: BetterVnc.Probe host[:port] [--profile <name>] [--user u] [--password p] [--seconds n]
// With --profile the credentials are read from %AppData%\BetterVNC\profiles.json (DPAPI, current user).

string hostArg = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "127.0.0.1:5999";
string? Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
int seconds = int.TryParse(Opt("--seconds"), out var s) ? s : 8;
string? user = Opt("--user"), password = Opt("--password");

if (Opt("--profile") is { } profileName)
{
    var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BetterVNC", "profiles.json");
    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    foreach (var c in doc.RootElement.GetProperty("Connections").EnumerateArray())
    {
        if (!string.Equals(c.GetProperty("Name").GetString(), profileName, StringComparison.OrdinalIgnoreCase)) continue;
        user ??= c.TryGetProperty("Username", out var u) ? u.GetString() : null;
        if (c.TryGetProperty("ProtectedPassword", out var pp) && pp.GetString() is { } enc)
            password ??= Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(enc), null, DataProtectionScope.CurrentUser));
        hostArg = c.GetProperty("Host").GetString() + ":" + c.GetProperty("Port").GetInt32();
    }
}

int colon = hostArg.LastIndexOf(':');
string host = colon > 0 ? hostArg[..colon] : hostArg;
int port = colon > 0 ? int.Parse(hostArg[(colon + 1)..]) : 5900;

var t0 = DateTime.Now;
void Log(string m) => Console.WriteLine($"[{(DateTime.Now - t0).TotalMilliseconds,7:0} ms] {m}");

if (args.Contains("--check-ssh") && user != null && password != null)
{
    // Same credentials over SSH: tells apart "wrong password" from "wrong VNC handshake".
    try
    {
        using var ssh = new Renci.SshNet.SshClient(host, 22, user, password);
        ssh.Connect();
        Log($"SSH login as '{user}' succeeded, so the user name and password are valid on this Mac.");
        ssh.Disconnect();
    }
    catch (Exception ex)
    {
        Log($"SSH login as '{user}' failed: {ex.GetType().Name}: {ex.Message}");
    }
}

var client = new VncClient(new VncConnectionOptions { Host = host, Port = port, Username = user, Password = password });
client.Log += m => Log("log: " + m);
client.FramebufferUpdated += r => Log($"update: {r.Length} rects, first {r[0]}, total updates {client.Statistics.FramebufferUpdates}, bytes {client.Statistics.BytesReceived}");
client.FramebufferResized += (w, h) => Log($"resized: {w}x{h}");
client.CursorChanged += c => Log($"cursor: {c.Width}x{c.Height} hot {c.HotX},{c.HotY}");
client.ClipboardTextReceived += t => Log($"clipboard: {t.Length} chars");
client.Bell += () => Log("bell");
client.Disconnected += ex => Log("DISCONNECTED: " + (ex == null ? "(no error)" : ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex.StackTrace));

try
{
    Log($"connecting to {host}:{port} as {user ?? "(no user)"}{(password != null ? " with password" : "")}");
    await client.ConnectAsync();
    Log($"connected: {client.Capabilities}, desktop '{client.DesktopName}' {client.Framebuffer.Width}x{client.Framebuffer.Height}");
}
catch (Exception ex)
{
    Log("CONNECT FAILED: " + ex);
    return 1;
}

await Task.Delay(TimeSpan.FromSeconds(seconds));
var st = client.Statistics;
Log($"stats: updates {st.FramebufferUpdates}, rects {st.Rectangles} (tight {st.TightRects}, zrle {st.ZrleRects}, hextile {st.HextileRects}, zlib {st.ZlibRects}, raw {st.RawRects}, copy {st.CopyRects}), bytes {st.BytesReceived}, connected={client.IsConnected}");
client.Disconnect();
return 0;
