using System.IO;
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
    bool HasSound)
{
    public bool IsRunning => State.StartsWith("running", StringComparison.OrdinalIgnoreCase);

    public string Warnings
    {
        get
        {
            var list = new List<string>();
            if (HasKeymap) list.Add("keymap set (remove for exact keyboard)");
            if (!HasVdagent) list.Add("no qemu-vdagent channel (clipboard will not work)");
            if (!HasSound) list.Add("no sound device");
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
        return new UnraidVmInfo(name, state, port, password, keymap, vdagent, sound);
    }
}
