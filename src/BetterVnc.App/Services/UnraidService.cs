using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BetterVnc.App.Models;
using Renci.SshNet;

namespace BetterVnc.App.Services;

public sealed record UnraidVmInfo(
    string Name,
    string State,
    int? VncPort,
    string? VncPassword,
    bool HasKeymap,
    bool HasVdagent,
    bool HasSound,
    string? GuestAddress = null)
{
    public bool IsRunning => State.StartsWith("running", StringComparison.OrdinalIgnoreCase);

    public string Warnings
    {
        get
        {
            var list = new List<string>();
            if (HasKeymap) list.Add("keymap set (remove for exact keyboard)");
            if (!HasVdagent) list.Add("no qemu-vdagent channel (VNC clipboard will not work)");
            if (!HasSound) list.Add("no sound device");
            if (IsRunning && GuestAddress == null) list.Add("address unknown (no QEMU guest agent answering; the BetterVNC agent needs it)");
            return string.Join("; ", list);
        }
    }
}

/// <summary>Talks to an Unraid host over SSH and uses virsh to discover and control VMs.</summary>
public sealed class UnraidService
{
    private readonly UnraidServer _server;

    public UnraidService(UnraidServer server)
    {
        _server = server;
    }

    private SshClient CreateClient()
    {
        var password = ProfileStore.Unprotect(_server.ProtectedPassword);
        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrEmpty(_server.PrivateKeyPath) && File.Exists(_server.PrivateKeyPath))
        {
            var key = string.IsNullOrEmpty(password) ? new PrivateKeyFile(_server.PrivateKeyPath) : new PrivateKeyFile(_server.PrivateKeyPath, password);
            methods.Add(new PrivateKeyAuthenticationMethod(_server.Username, key));
        }
        if (!string.IsNullOrEmpty(password))
            methods.Add(new PasswordAuthenticationMethod(_server.Username, password));
        if (methods.Count == 0)
            methods.Add(new PasswordAuthenticationMethod(_server.Username, ""));
        var info = new ConnectionInfo(_server.Host, _server.SshPort, _server.Username, methods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        return new SshClient(info);
    }

    private static string Run(SshClient ssh, string command)
    {
        using var cmd = ssh.CreateCommand(command);
        cmd.CommandTimeout = TimeSpan.FromSeconds(20);
        var output = cmd.Execute();
        if (cmd.ExitStatus != 0 && string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException($"'{command}' failed: {cmd.Error}".Trim());
        return output;
    }

    private static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    public Task<List<UnraidVmInfo>> ListVmsAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        using var ssh = CreateClient();
        ssh.Connect();
        var names = Run(ssh, "virsh list --all --name")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(n => n.Length > 0)
            .ToList();
        var result = new List<UnraidVmInfo>();
        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();
            result.Add(Inspect(ssh, name));
        }
        return result;
    }, ct);

    public Task<UnraidVmInfo> GetVmAsync(string name, CancellationToken ct = default) => Task.Run(() =>
    {
        using var ssh = CreateClient();
        ssh.Connect();
        return Inspect(ssh, name);
    }, ct);

    /// <summary>The VM's own IPv4 address, or null when neither the QEMU guest agent nor the host's ARP table knows it.</summary>
    public Task<string?> GetGuestAddressAsync(string name, CancellationToken ct = default) => Task.Run(() =>
    {
        using var ssh = CreateClient();
        ssh.Connect();
        ct.ThrowIfCancellationRequested();
        return FindGuestAddress(ssh, name, ReadMacs(Run(ssh, $"virsh dumpxml {Quote(name)}")));
    }, ct);

    public Task StartVmAsync(string name, CancellationToken ct = default) => Task.Run(() =>
    {
        using var ssh = CreateClient();
        ssh.Connect();
        Run(ssh, $"virsh start {Quote(name)}");
    }, ct);

    private static UnraidVmInfo Inspect(SshClient ssh, string name)
    {
        var state = Run(ssh, $"virsh domstate {Quote(name)}").Trim();
        var xmlText = Run(ssh, $"virsh dumpxml --security-info {Quote(name)}");
        int? port = null;
        string? password = null;
        bool keymap = false, vdagent = false, sound = false;
        var macs = ReadMacs(xmlText);
        try
        {
            var xml = XDocument.Parse(xmlText);
            var graphics = xml.Descendants("graphics").FirstOrDefault(g => (string?)g.Attribute("type") == "vnc");
            if (graphics != null)
            {
                if (int.TryParse((string?)graphics.Attribute("port"), out var p) && p > 0) port = p;
                password = (string?)graphics.Attribute("passwd");
                keymap = graphics.Attribute("keymap") != null;
            }
            vdagent = xml.Descendants("channel").Any(c => (string?)c.Attribute("type") == "qemu-vdagent");
            sound = xml.Descendants("sound").Any();
        }
        catch
        {
            // Fall back to vncdisplay if the XML could not be parsed.
            var disp = Run(ssh, $"virsh vncdisplay {Quote(name)}").Trim();
            var m = Regex.Match(disp, @":(\d+)$");
            if (m.Success) port = 5900 + int.Parse(m.Groups[1].Value);
        }
        var info = new UnraidVmInfo(name, state, port, password, keymap, vdagent, sound);
        return info.IsRunning ? info with { GuestAddress = FindGuestAddress(ssh, name, macs) } : info;
    }

    /// <summary>MAC addresses of the VM's network cards, lower case.</summary>
    private static List<string> ReadMacs(string xmlText)
    {
        try
        {
            return XDocument.Parse(xmlText).Descendants("interface")
                .Select(i => (string?)i.Element("mac")?.Attribute("address"))
                .OfType<string>()
                .Select(m => m.ToLowerInvariant())
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// The VM's own IPv4 address: from the QEMU guest agent when the guest runs one (the virtio-win guest tools include
    /// it), else from the host's ARP table, which only knows guests the host has talked to lately.
    /// </summary>
    private static string? FindGuestAddress(SshClient ssh, string name, IReadOnlyCollection<string> macs)
    {
        foreach (var source in new[] { "agent", "arp" })
        {
            string output;
            try { output = Run(ssh, $"virsh domifaddr {Quote(name)} --source {source}"); }
            catch (InvalidOperationException) { continue; } // no guest agent connected
            if (PickGuestAddress(output, macs) is { } address) return address;
        }
        return null;
    }

    /// <summary>
    /// Picks the first usable IPv4 address of one of the VM's own network cards from <c>virsh domifaddr</c> output. The
    /// guest agent also reports VPN adapters, loopback and link-local addresses, which the viewer cannot reach.
    /// </summary>
    /// <remarks>
    /// Rows look like <c> Ethernet Instance 0   52:54:00:9b:78:3a   ipv4   10.20.30.80/24</c>; further addresses of
    /// the same card follow with <c>-</c> in the name and MAC columns, and adapters without a MAC leave that column empty.
    /// </remarks>
    internal static string? PickGuestAddress(string domifaddrOutput, IReadOnlyCollection<string> macs)
    {
        var row = new Regex(@"^\s*(?<name>.*?)\s+(?:(?<mac>(?:[0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}|-)\s+)?(?<proto>ipv4|ipv6)\s+(?<addr>[^\s/]+)");
        string? mac = null;
        foreach (var line in domifaddrOutput.Split('\n'))
        {
            var m = row.Match(line);
            if (!m.Success) continue;
            var macCol = m.Groups["mac"].Value;
            if (macCol.Length > 1) mac = macCol.ToLowerInvariant();
            else if (macCol.Length == 0 && m.Groups["name"].Value != "-") mac = null;
            if (m.Groups["proto"].Value != "ipv4" || !IPAddress.TryParse(m.Groups["addr"].Value, out var ip)) continue;
            if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip)) continue;
            var b = ip.GetAddressBytes();
            if (b[0] == 169 && b[1] == 254) continue;
            if (macs.Count > 0 && (mac == null || !macs.Contains(mac))) continue;
            return ip.ToString();
        }
        return null;
    }
}
