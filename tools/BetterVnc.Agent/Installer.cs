using System.Diagnostics;
using System.Text;
using BetterVnc.Platform;
using Microsoft.Win32;
using BetterVnc.Agent.Protocol;

namespace BetterVnc.Agent;

/// <summary>
/// <c>bettervnc-agent install</c>: copies the executable into a per-user location, creates the config (keeping existing
/// pairings and token), registers it to start at login with the OS's own mechanism and starts it right away.
/// <c>uninstall</c> reverses all of it. <see cref="IsAutostartEnabled"/> / <see cref="SetAutostart"/> back the
/// "Start at login" switch in the tray and menu bar UIs. No administrator rights are needed on any OS (the Windows
/// firewall rule is attempted and reported if it needs elevation).
/// </summary>
public static class Installer
{
    private const string TaskName = "BetterVNC agent";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LaunchLabel = "com.bettervnc.agent";
    private const string ServiceName = "bettervnc-agent";

    public static int Install(bool dryRun, int? port, string? listen)
    {
        var source = Environment.ProcessPath ?? "";
        if (!dryRun && (source.Length == 0 || Path.GetFileNameWithoutExtension(source).Equals("dotnet", StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine("'install' needs the published single-file agent (bettervnc-agent or bettervnc-agent.exe), not 'dotnet bettervnc-agent.dll'.");
            return 2;
        }
        var layout = Layout.ForThisOs();
        Console.WriteLine($"Installing the BetterVNC guest agent for the current user{(dryRun ? " (dry run, nothing is changed)" : "")}:");
        Console.WriteLine($"  program  : {layout.Exe}");
        Console.WriteLine($"  config   : {layout.Config}");
        Console.WriteLine($"  autostart: {layout.AutostartDescription}");
        Console.WriteLine($"  log      : {Path.Combine(layout.Dir, "bettervnc-agent.log")}");

        // Keep an existing config (pairings, token) so reinstalling does not break saved connections.
        var cfg = AgentConfig.Load(layout.Config) ?? AgentConfig.CreateNew();
        if (port is { } p) cfg.Port = p;
        if (listen != null) cfg.Listen = listen;

        if (OperatingSystem.IsLinux())
        {
            bool wayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
            if (wayland && !ProcessRunner.Exists("wl-copy")) Console.WriteLine("  warning  : wl-clipboard is not installed (apt install wl-clipboard); the agent needs it on Wayland.");
            if (!wayland && !ProcessRunner.Exists("xclip")) Console.WriteLine("  warning  : xclip is not installed (apt install xclip); the agent needs it on X11.");
            if (!ProcessRunner.Exists("zenity") && !ProcessRunner.Exists("kdialog") && !ProcessRunner.Exists("xmessage"))
                Console.WriteLine("  warning  : no dialog tool (zenity, kdialog or xmessage) for pairing approvals; set a token instead.");
        }

        if (dryRun)
        {
            Console.WriteLine($"  auth     : {cfg.AuthSummary}");
            return 0;
        }

        Directory.CreateDirectory(layout.Dir);
        Directory.CreateDirectory(Path.GetDirectoryName(layout.Config)!);
        StopRunning(layout);
        if (OperatingSystem.IsMacOS())
        {
            try { InstallAppBundle(source, layout); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not build {MacAppBundle.Name}: {ex.Message}");
                if (File.Exists(layout.Exe)) StartNow(layout); // the previous install is untouched; bring it back
                return 1;
            }
        }
        else if (!PathsEqual(source, layout.Exe))
        {
            File.Copy(source, layout.Exe, overwrite: true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(layout.Exe, Executable);
        }
        cfg.Save(layout.Config);

        try
        {
            SetAutostart(true, layout.Config, layout.Exe, addFirewallRule: true);
            StartNow(layout);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Autostart registration failed: " + ex.Message);
            Console.Error.WriteLine($"The program and config are in place; start it by hand with: \"{layout.Exe}\" --config \"{layout.Config}\"");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("Installed and running.");
        if (OperatingSystem.IsWindows()) Console.WriteLine("Look for the BetterVNC icon in the notification area (tray): it opens the settings.");
        else if (OperatingSystem.IsMacOS()) Console.WriteLine("Look for the BetterVNC icon in the menu bar: it opens the settings.");
        Console.WriteLine();
        if (cfg.AllowPairing)
        {
            Console.WriteLine("Pairing is on: in BetterVNC, enable the guest agent for the connection and connect. A dialog appears");
            Console.WriteLine("on this screen asking to allow the viewer; click Allow once and the two stay paired.");
        }
        if (!string.IsNullOrEmpty(cfg.Token))
        {
            Console.WriteLine($"Token or password (for viewers that use token mode): {cfg.Token}");
        }
        else
        {
            Console.WriteLine("No token is set; set one in the settings or with 'bettervnc-agent token generate' if you prefer token mode.");
        }
        Console.WriteLine($"It listens on {cfg.Listen}:{cfg.Port}. Remove everything with '{Path.GetFileName(layout.Exe)} uninstall'.");
        return 0;
    }

    public static int Uninstall(bool dryRun)
    {
        var layout = Layout.ForThisOs();
        Console.WriteLine($"Removing the BetterVNC guest agent for the current user{(dryRun ? " (dry run)" : "")}:");
        Console.WriteLine($"  {layout.AutostartDescription}");
        Console.WriteLine($"  {layout.Dir}");
        Console.WriteLine($"  {layout.Config}");
        if (dryRun) return 0;

        StopRunning(layout);
        try { SetAutostart(false, layout.Config, layout.Exe, addFirewallRule: false); } catch { }
        if (OperatingSystem.IsWindows()) Quiet("netsh", "advfirewall", "firewall", "delete", "rule", $"name={TaskName}");
        if (OperatingSystem.IsLinux()) TryDelete(Path.Combine(Home, ".config", "autostart", "bettervnc-agent.desktop"));
        TryDelete(layout.Config);
        foreach (var f in new[] { "bettervnc-agent.log", "bettervnc-agent.log.1", "run-hidden.vbs" }) TryDelete(Path.Combine(layout.Dir, f));
        if (OperatingSystem.IsMacOS())
        {
            TryDeleteTree(MacAppBundle.EnclosingBundle(layout.Exe)!);
            foreach (var f in LegacyMacFiles) TryDelete(Path.Combine(layout.Dir, f));
        }
        TryDelete(Path.Combine(Home, "Library", "Logs", "bettervnc-agent.log"));
        var self = Environment.ProcessPath;
        if (self != null && PathsEqual(self, layout.Exe) && OperatingSystem.IsWindows())
        {
            Console.WriteLine($"Delete {layout.Dir} by hand (this program is running from it)."); // Windows cannot delete a running executable
        }
        else
        {
            TryDelete(layout.Exe);
            TryDeleteDir(layout.Dir);
        }
        Console.WriteLine("Removed.");
        return 0;
    }

    private const UnixFileMode Executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    /// <summary>What versions before the app bundle installed on macOS, next to the config.</summary>
    private static readonly string[] LegacyMacFiles = { MacAppBundle.ExecutableName, Video.MacScreenSource.HelperName };

    /// <summary>
    /// macOS: builds <see cref="MacAppBundle.Name"/> from this agent and the capture helper next to it, signs it ad hoc and
    /// puts it in place of the installed one. It is built in a staging folder first, so reinstalling from the installed
    /// copy works and a failure leaves the old install alone; the old bundle is then removed rather than overwritten,
    /// because macOS kills a program whose signed file changed in place. The bare program and helper of earlier versions go.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static void InstallAppBundle(string source, Layout layout)
    {
        var bundle = MacAppBundle.EnclosingBundle(layout.Exe)!;
        var stagingDir = Path.Combine(layout.Dir, ".staging");
        var staging = Path.Combine(stagingDir, MacAppBundle.Name);
        TryDeleteTree(stagingDir);
        var helper = Video.MacScreenSource.FindHelper();
        if (helper == null) Console.WriteLine($"  warning  : {Video.MacScreenSource.HelperName} is not next to this agent, so screen streaming will not be available.");
        var version = typeof(Installer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var exe = MacAppBundle.Write(staging, source, helper, version, MacAppBundle.EmbeddedIcon());
        File.SetUnixFileMode(exe, Executable);
        if (helper != null)
        {
            var stagedHelper = Path.Combine(Path.GetDirectoryName(exe)!, Video.MacScreenSource.HelperName);
            File.SetUnixFileMode(stagedHelper, Executable);
            Run("codesign", "--force", "--sign", "-", stagedHelper);
        }
        // Apple silicon runs only signed code, and the bundle's signature is what ties Info.plist, and with it the
        // privacy permissions, to the program.
        Run("codesign", "--force", "--sign", "-", staging);
        TryDeleteTree(bundle);
        Directory.Move(staging, bundle);
        TryDeleteTree(stagingDir);
        Quiet("xattr", "-dr", "com.apple.quarantine", bundle);
        foreach (var f in LegacyMacFiles) TryDelete(Path.Combine(layout.Dir, f));
    }

    // ------------------------------------------------------------------ autostart

    /// <summary>Whether the agent is registered to start at login (Run entry, LaunchAgent plist or systemd user unit).</summary>
    public static bool IsAutostartEnabled()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(TaskName) != null;
            if (OperatingSystem.IsMacOS()) return File.Exists(Layout.ForThisOs().Autostart);
            if (ProcessRunner.Exists("systemctl")) return ProcessRunner.Run("systemctl", new[] { "--user", "is-enabled", ServiceName }).Ok;
            return File.Exists(Path.Combine(Home, ".config", "autostart", "bettervnc-agent.desktop"));
        }
        catch { return false; }
    }

    /// <summary>Registers or unregisters the running (or given) executable to start at login. Does not stop a running agent.</summary>
    public static void SetAutostart(bool enable, string configPath, string? exePath = null, bool addFirewallRule = false)
    {
        var exe = exePath ?? Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path.");
        var dir = Path.GetDirectoryName(exe)!;
        if (OperatingSystem.IsWindows())
        {
            // A per-user Run entry: no administrator rights, and the Windows build is a GUI-subsystem program, so no
            // console window appears at sign-in. Older versions used a logon task that started a hidden VBS through
            // wscript; Microsoft Defender blocks creating that task as Trojan:Win32/Commando.A!ml (the usual shape of
            // malware persistence), so remove it if an older install left one behind.
            Quiet("schtasks", "/delete", "/tn", TaskName, "/f");
            TryDelete(Path.Combine(dir, "run-hidden.vbs"));
            using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (!enable) { run.DeleteValue(TaskName, throwOnMissingValue: false); return; }
                run.SetValue(TaskName, $"\"{exe}\" --config \"{configPath}\"");
            }
            if (addFirewallRule)
            {
                // profile=any: VMs on a bridged network often sit on a "Public" network profile. Delete first so a
                // reinstall does not stack up duplicate rules.
                Quiet("netsh", "advfirewall", "firewall", "delete", "rule", $"name={TaskName}", $"program={exe}");
                var fw = ProcessRunner.Run("netsh", new[] { "advfirewall", "firewall", "add", "rule", $"name={TaskName}", "dir=in", "action=allow", "protocol=TCP", $"program={exe}", "profile=any", "enable=yes" });
                if (!fw.Ok)
                {
                    Console.WriteLine("  firewall : could not add a rule (needs an elevated prompt). Windows asks once when the agent first listens;");
                    Console.WriteLine("             tick Public networks there too if this machine's network is Public, or run elevated:");
                    Console.WriteLine($"             netsh advfirewall firewall add rule name=\"{TaskName}\" dir=in action=allow protocol=TCP program=\"{exe}\" profile=any enable=yes");
                }
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            var plist = Path.Combine(Home, "Library", "LaunchAgents", LaunchLabel + ".plist");
            if (!enable) { TryDelete(plist); return; }
            var log = Path.Combine(Home, "Library", "Logs", "bettervnc-agent.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">");
            sb.AppendLine("<plist version=\"1.0\"><dict>");
            sb.AppendLine($"  <key>Label</key><string>{LaunchLabel}</string>");
            sb.AppendLine("  <key>ProgramArguments</key><array>");
            sb.AppendLine($"    <string>{Xml(exe)}</string><string>--config</string><string>{Xml(configPath)}</string>");
            sb.AppendLine("  </array>");
            sb.AppendLine("  <key>RunAtLoad</key><true/>");
            sb.AppendLine("  <key>KeepAlive</key><true/>");
            sb.AppendLine("  <key>ProcessType</key><string>Interactive</string>");
            // Login Items in System Settings then shows the app's name and icon instead of the bare program.
            if (MacAppBundle.EnclosingBundle(exe) != null)
                sb.AppendLine($"  <key>AssociatedBundleIdentifiers</key><string>{MacAppBundle.BundleId}</string>");
            sb.AppendLine($"  <key>StandardOutPath</key><string>{Xml(log)}</string>");
            sb.AppendLine($"  <key>StandardErrorPath</key><string>{Xml(log)}</string>");
            sb.AppendLine("</dict></plist>");
            Directory.CreateDirectory(Path.GetDirectoryName(plist)!);
            File.WriteAllText(plist, sb.ToString());
        }
        else
        {
            if (ProcessRunner.Exists("systemctl"))
            {
                var unit = Path.Combine(Home, ".config", "systemd", "user", ServiceName + ".service");
                if (!enable) { Quiet("systemctl", "--user", "disable", ServiceName); return; }
                var text = $"""
                    [Unit]
                    Description=BetterVNC guest agent (rich clipboard and smooth scrolling for the BetterVNC viewer)
                    After=graphical-session.target
                    PartOf=graphical-session.target

                    [Service]
                    ExecStart={exe} --config {configPath}
                    Restart=on-failure
                    RestartSec=3

                    [Install]
                    WantedBy=graphical-session.target default.target

                    """;
                Directory.CreateDirectory(Path.GetDirectoryName(unit)!);
                File.WriteAllText(unit, text);
                Run("systemctl", "--user", "daemon-reload");
                Run("systemctl", "--user", "enable", ServiceName);
            }
            else
            {
                var desktop = Path.Combine(Home, ".config", "autostart", "bettervnc-agent.desktop");
                if (!enable) { TryDelete(desktop); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(desktop)!);
                File.WriteAllText(desktop, $"[Desktop Entry]\nType=Application\nName=BetterVNC guest agent\nExec={exe} --config {configPath}\nX-GNOME-Autostart-enabled=true\n");
            }
        }
    }

    /// <summary>Starts the installed agent the way it starts after a login (through launchd or systemd where those are used).</summary>
    private static void StartNow(Layout l)
    {
        if (OperatingSystem.IsMacOS())
        {
            var uid = ProcessRunner.Run("id", new[] { "-u" }).Text.Trim();
            Quiet("launchctl", "bootout", $"gui/{uid}/{LaunchLabel}");
            var r = ProcessRunner.Run("launchctl", new[] { "bootstrap", $"gui/{uid}", l.Autostart });
            if (!r.Ok) Run("launchctl", "load", "-w", l.Autostart); // older macOS
        }
        else if (ProcessRunner.Exists("systemctl"))
        {
            Run("systemctl", "--user", "restart", ServiceName);
        }
        else
        {
            // Windows (the same command line as the Run entry) and Linux without systemd.
            var psi = new ProcessStartInfo(l.Exe) { UseShellExecute = false, WorkingDirectory = l.Dir };
            psi.ArgumentList.Add("--config"); psi.ArgumentList.Add(l.Config);
            Process.Start(psi);
        }
    }

    private static void StopRunning(Layout l)
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var p in Process.GetProcessesByName("bettervnc-agent"))
            {
                if (p.Id == Environment.ProcessId) continue;
                try { if (PathsEqual(p.MainModule?.FileName ?? "", l.Exe)) { p.Kill(); p.WaitForExit(3000); } } catch { }
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            var uid = ProcessRunner.Run("id", new[] { "-u" }).Text.Trim();
            Quiet("launchctl", "bootout", $"gui/{uid}/{LaunchLabel}");
        }
        else if (ProcessRunner.Exists("systemctl"))
        {
            Quiet("systemctl", "--user", "stop", ServiceName);
        }
    }

    // ------------------------------------------------------------------ helpers

    private sealed record Layout(string Dir, string Exe, string Config, string Autostart, string AutostartDescription)
    {
        public static Layout ForThisOs()
        {
            if (OperatingSystem.IsWindows())
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "BetterVNC Agent");
                return new Layout(dir, Path.Combine(dir, "bettervnc-agent.exe"), Path.Combine(dir, AgentConfig.FileName), "",
                    $"Run entry \"{TaskName}\" in HKEY_CURRENT_USER (at sign-in)");
            }
            if (OperatingSystem.IsMacOS())
            {
                var dir = Path.Combine(Home, "Library", "Application Support", "BetterVNC");
                var plist = Path.Combine(Home, "Library", "LaunchAgents", LaunchLabel + ".plist");
                return new Layout(dir, MacAppBundle.ExecutablePath(Path.Combine(dir, MacAppBundle.Name)), Path.Combine(dir, AgentConfig.FileName), plist, $"LaunchAgent {plist}");
            }
            var bin = Path.Combine(Home, ".local", "bin");
            var unit = Path.Combine(Home, ".config", "systemd", "user", ServiceName + ".service");
            return new Layout(bin, Path.Combine(bin, "bettervnc-agent"), Path.Combine(Home, ".config", AgentConfig.FileName), unit,
                ProcessRunner.Exists("systemctl") ? $"systemd user service {unit}" : "~/.config/autostart/bettervnc-agent.desktop");
        }
    }

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void Run(string file, params string[] args)
    {
        var r = ProcessRunner.Run(file, args);
        if (!r.Ok) throw new InvalidOperationException($"{file} {string.Join(' ', args)} failed ({r.ExitCode}): {(r.Stderr + r.Text).Trim()}");
    }

    private static void Quiet(string file, params string[] args)
    {
        try { ProcessRunner.Run(file, args); } catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (path.Length > 0 && File.Exists(path)) File.Delete(path); } catch (Exception ex) { Console.WriteLine($"  could not delete {path}: {ex.Message}"); }
    }

    private static void TryDeleteTree(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (Exception ex) { Console.WriteLine($"  could not delete {dir}: {ex.Message}"); }
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
    }

    private static string Xml(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
