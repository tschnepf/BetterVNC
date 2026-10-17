using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BetterVnc.Agent.Clipboards;
using BetterVnc.Agent.Protocol;

namespace BetterVnc.Agent.Tray;

/// <summary>
/// Menu bar item for the macOS agent, built straight on the Objective-C runtime (no Xamarin): an NSStatusItem with a
/// menu for status, pairing, token, port, paired viewers, autostart, log and quit. Edits use osascript dialogs, which
/// keeps this file small. Must be called on the main thread; <see cref="Run"/> does not return until Quit.
/// </summary>
public static class MacStatusItem
{
    private const string ObjC = "/usr/lib/libobjc.dylib";

    [DllImport(ObjC)] private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(ObjC)] private static extern IntPtr objc_allocateClassPair(IntPtr superclass, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nint extraBytes);
    [DllImport(ObjC)] private static extern void objc_registerClassPair(IntPtr cls);
    [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr self, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr self, IntPtr sel, IntPtr a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr self, IntPtr sel, IntPtr a, IntPtr b);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr self, IntPtr sel, IntPtr a, IntPtr b, IntPtr c);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr self, IntPtr sel, IntPtr a, IntPtr b, [MarshalAs(UnmanagedType.I1)] bool c);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendStr(IntPtr self, IntPtr sel, [MarshalAs(UnmanagedType.LPUTF8Str)] string s);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendLong(IntPtr self, IntPtr sel, long a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendBool(IntPtr self, IntPtr sel, [MarshalAs(UnmanagedType.I1)] bool a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendDouble(IntPtr self, IntPtr sel, double a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern long SendRetLong(IntPtr self, IntPtr sel);

    private static IntPtr Sel(string s) => sel_registerName(s);
    private static IntPtr NSStr(string s) => SendStr(objc_getClass("NSString"), Sel("stringWithUTF8String:"), s);

    private enum Item { Status = 1, Viewers = 2, AllowPairing = 3, PairedViewers = 4, Token = 5, Port = 6, ListenAll = 7, Autostart = 8, OpenLog = 9, Quit = 10 }

    private static AgentHost? _host;
    private static IntPtr _target;
    private static readonly Dictionary<Item, IntPtr> _items = new();

    public static unsafe void Run(AgentHost host)
    {
        _host = host;
        NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");

        // A tiny Objective-C class whose instance receives menu actions and refresh requests.
        var cls = objc_allocateClassPair(objc_getClass("NSObject"), "BVAgentMenuTarget", 0);
        class_addMethod(cls, Sel("menuAction:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&MenuAction, "v@:@");
        class_addMethod(cls, Sel("refresh:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&RefreshAction, "v@:@");
        objc_registerClassPair(cls);
        _target = Send(Send(cls, Sel("alloc")), Sel("init"));

        var app = Send(objc_getClass("NSApplication"), Sel("sharedApplication"));
        SendLong(app, Sel("setActivationPolicy:"), 1); // accessory: menu bar item only, no Dock icon

        var statusBar = Send(objc_getClass("NSStatusBar"), Sel("systemStatusBar"));
        var item = SendDouble(statusBar, Sel("statusItemWithLength:"), -1.0); // NSVariableStatusItemLength
        var button = Send(item, Sel("button"));
        var image = Send(objc_getClass("NSImage"), Sel("imageWithSystemSymbolName:accessibilityDescription:"), NSStr("doc.on.clipboard"), IntPtr.Zero);
        if (image != IntPtr.Zero)
        {
            SendBool(image, Sel("setTemplate:"), true);
            Send(button, Sel("setImage:"), image);
        }
        else
        {
            Send(button, Sel("setTitle:"), NSStr("BV"));
        }

        var menu = Send(Send(objc_getClass("NSMenu"), Sel("alloc")), Sel("init"));
        SendBool(menu, Sel("setAutoenablesItems:"), false);
        AddItem(menu, Item.Status, "", enabled: false);
        AddItem(menu, Item.Viewers, "", enabled: false);
        AddSeparator(menu);
        AddItem(menu, Item.AllowPairing, "Allow new viewers to pair");
        AddItem(menu, Item.PairedViewers, "Paired viewers…");
        AddItem(menu, Item.Token, "Token or password…");
        AddItem(menu, Item.Port, "Port…");
        AddItem(menu, Item.ListenAll, "Reachable from the network");
        AddSeparator(menu);
        AddItem(menu, Item.Autostart, "Start at login");
        AddItem(menu, Item.OpenLog, "Open log");
        AddSeparator(menu);
        AddItem(menu, Item.Quit, "Quit BetterVNC agent");
        Send(item, Sel("setMenu:"), menu);

        host.Changed += RequestRefresh;
        host.Paired += v => host.Log($"Paired with {v.Name}");
        Refresh();
        Send(app, Sel("run")); // blocks until Quit
    }

    private static void AddItem(IntPtr menu, Item id, string title, bool enabled = true)
    {
        var item = Send(Send(objc_getClass("NSMenuItem"), Sel("alloc")), Sel("initWithTitle:action:keyEquivalent:"), NSStr(title), Sel("menuAction:"), NSStr(""));
        Send(item, Sel("setTarget:"), _target);
        SendLong(item, Sel("setTag:"), (long)id);
        SendBool(item, Sel("setEnabled:"), enabled);
        Send(menu, Sel("addItem:"), item);
        _items[id] = item;
    }

    private static void AddSeparator(IntPtr menu) => Send(menu, Sel("addItem:"), Send(objc_getClass("NSMenuItem"), Sel("separatorItem")));

    private static void SetTitle(Item id, string title) => Send(_items[id], Sel("setTitle:"), NSStr(title));
    private static void SetChecked(Item id, bool on) => SendLong(_items[id], Sel("setState:"), on ? 1 : 0);

    /// <summary>Thread-safe: hops to the main thread.</summary>
    private static void RequestRefresh() => Send(_target, Sel("performSelectorOnMainThread:withObject:waitUntilDone:"), Sel("refresh:"), IntPtr.Zero, false);

    private static void Refresh()
    {
        var h = _host!;
        SetTitle(Item.Status, $"BetterVNC guest agent {h.Version}: {h.StatusLine}");
        SetTitle(Item.Viewers, h.ViewersLine);
        SetChecked(Item.AllowPairing, h.Config.AllowPairing);
        SetTitle(Item.Token, string.IsNullOrEmpty(h.Config.Token) ? "Token or password… (none set)" : "Token or password… (set)");
        SetTitle(Item.Port, $"Port… ({h.Config.Port})");
        SetChecked(Item.ListenAll, h.Config.Listen != "127.0.0.1");
        SetChecked(Item.Autostart, Installer.IsAutostartEnabled());
    }

    [UnmanagedCallersOnly]
    private static void RefreshAction(IntPtr self, IntPtr sel, IntPtr arg)
    {
        try { Refresh(); } catch (Exception ex) { _host?.Log("Menu refresh failed: " + ex.Message); }
    }

    [UnmanagedCallersOnly]
    private static void MenuAction(IntPtr self, IntPtr sel, IntPtr sender)
    {
        Item id;
        try { id = (Item)SendRetLong(sender, Sel("tag")); } catch { return; }
        // Dialogs are separate osascript processes; run them off the main thread so the menu stays responsive.
        _ = Task.Run(async () =>
        {
            try { await HandleAsync(id); }
            catch (Exception ex) { _host?.Log($"Menu action {id} failed: {ex.Message}"); }
            RequestRefresh();
        });
    }

    private static async Task HandleAsync(Item id)
    {
        var h = _host!;
        switch (id)
        {
            case Item.AllowPairing:
                h.Config.AllowPairing = !h.Config.AllowPairing;
                await h.SaveAndRestartAsync();
                break;
            case Item.PairedViewers:
            {
                PairedViewer[] viewers;
                lock (h.Config.Viewers) viewers = h.Config.Viewers.ToArray();
                if (viewers.Length == 0) { Dialog("No viewer has paired yet. Connect from BetterVNC and click Allow on this screen."); break; }
                var labels = viewers.Select(v => $"{v.Name} (paired {v.PairedAt:g}, last seen {(v.LastSeen?.ToString("g") ?? "never")})").ToArray();
                var chosen = ChooseFromList("Select the viewers to remove. They will have to pair again.", labels);
                foreach (var c in chosen)
                {
                    int i = Array.IndexOf(labels, c);
                    if (i >= 0) h.RemoveViewer(viewers[i].Id);
                }
                break;
            }
            case Item.Token:
            {
                var answer = Prompt("Token or password a viewer may use instead of pairing. Leave it empty to allow paired viewers only.", h.Config.Token ?? "", new[] { "Cancel", "Generate", "Save" }, hidden: false);
                if (answer.button == "Cancel") break;
                h.Config.Token = answer.button == "Generate" ? AgentAuth.NewToken() : (string.IsNullOrWhiteSpace(answer.text) ? null : answer.text.Trim());
                await h.SaveAndRestartAsync();
                if (answer.button == "Generate") Dialog($"New token:\n\n{h.Config.Token}\n\nEnter it in the BetterVNC connection (Guest agent, token mode).");
                break;
            }
            case Item.Port:
            {
                var answer = Prompt("TCP port the agent listens on (default 5970). Change the same value in the BetterVNC connection.", h.Config.Port.ToString(), new[] { "Cancel", "Save" }, hidden: false);
                if (answer.button != "Save") break;
                if (!int.TryParse(answer.text.Trim(), out var port) || port < 1 || port > 65535) { Dialog("That is not a valid port number."); break; }
                h.Config.Port = port;
                await h.SaveAndRestartAsync();
                if (!h.IsListening) Dialog(h.StatusLine);
                break;
            }
            case Item.ListenAll:
                h.Config.Listen = h.Config.Listen == "127.0.0.1" ? "0.0.0.0" : "127.0.0.1";
                await h.SaveAndRestartAsync();
                break;
            case Item.Autostart:
                Installer.SetAutostart(!Installer.IsAutostartEnabled(), h.ConfigPath);
                break;
            case Item.OpenLog:
                if (!File.Exists(h.LogPath)) File.WriteAllText(h.LogPath, "");
                Process.Start(new ProcessStartInfo("open", new[] { h.LogPath }) { UseShellExecute = false });
                break;
            case Item.Quit:
                try { await h.DisposeAsync(); } catch { }
                Environment.Exit(0);
                break;
        }
    }

    // ------------------------------------------------------------------ osascript dialogs

    private static ProcessRunner.Result Osa(string script, string text, string? answer = null)
    {
        var env = new Dictionary<string, string> { ["BVNC_TEXT"] = text };
        if (answer != null) env["BVNC_ANSWER"] = answer;
        return ProcessRunner.Run("osascript", new[] { "-e", "set bvncText to system attribute \"BVNC_TEXT\"\nset bvncAnswer to system attribute \"BVNC_ANSWER\"\n" + script }, env: env, timeoutMs: 600_000);
    }

    private static void Dialog(string text) =>
        Osa("display dialog bvncText with title \"BetterVNC guest agent\" buttons {\"OK\"} default button \"OK\" with icon note", text);

    private static (string button, string text) Prompt(string text, string current, string[] buttons, bool hidden)
    {
        var btns = string.Join(", ", buttons.Select(b => $"\"{b}\""));
        var r = Osa($"display dialog bvncText default answer bvncAnswer with title \"BetterVNC guest agent\" buttons {{{btns}}} default button \"{buttons[^1]}\" cancel button \"{buttons[0]}\"{(hidden ? " with hidden answer" : "")}", text, current);
        if (!r.Ok) return (buttons[0], "");
        // "button returned:Save, text returned:value"
        var output = r.Text.Trim();
        var button = buttons.FirstOrDefault(b => output.StartsWith($"button returned:{b}")) ?? buttons[0];
        int idx = output.IndexOf("text returned:", StringComparison.Ordinal);
        return (button, idx >= 0 ? output[(idx + "text returned:".Length)..] : "");
    }

    private static string[] ChooseFromList(string text, string[] items)
    {
        var r = Osa("set theItems to paragraphs of bvncAnswer\nset chosen to choose from list theItems with title \"BetterVNC guest agent\" with prompt bvncText OK button name \"Remove\" cancel button name \"Cancel\" with multiple selections allowed\nif chosen is false then return \"\"\nset AppleScript's text item delimiters to linefeed\nreturn chosen as text",
            text, string.Join("\n", items));
        if (!r.Ok) return Array.Empty<string>();
        return r.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
