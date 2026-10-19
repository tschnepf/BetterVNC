import AgentCore
import AppKit

/// The menu bar item (MacStatusItem.cs): status, pairing, token, port, paired viewers, permissions, start at login, log
/// and quit.
final class StatusMenu: NSObject, NSMenuDelegate {
    private let host: AgentHost
    private let wizard: PermissionWizard
    private let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    private let menu = NSMenu()
    private var status = NSMenuItem(), viewers = NSMenuItem(), allowPairing = NSMenuItem(), token = NSMenuItem()
    private var port = NSMenuItem(), listenAll = NSMenuItem(), permissions = NSMenuItem(), autostart = NSMenuItem()

    init(host: AgentHost) {
        self.host = host
        wizard = PermissionWizard(host: host)
        super.init()
        if let button = item.button {
            if let image = NSImage(systemSymbolName: "doc.on.clipboard", accessibilityDescription: "BetterVNC Agent") {
                image.isTemplate = true
                button.image = image
            } else {
                button.title = "BV"
            }
        }
        menu.autoenablesItems = false
        menu.delegate = self
        status = add("", nil, enabled: false)
        viewers = add("", nil, enabled: false)
        menu.addItem(.separator())
        allowPairing = add("Allow new viewers to pair", #selector(toggleAllowPairing))
        _ = add("Paired viewers…", #selector(pairedViewers))
        token = add("Token or password…", #selector(editToken))
        port = add("Port…", #selector(editPort))
        listenAll = add("Reachable from the network", #selector(toggleListenAll))
        menu.addItem(.separator())
        permissions = add("Permissions…", #selector(openPermissions))
        autostart = add("Start at login", #selector(toggleAutostart))
        _ = add("Open log", #selector(openLog))
        menu.addItem(.separator())
        _ = add("Quit BetterVNC Agent", #selector(quit))
        item.menu = menu

        host.onChanged = { [weak self] in DispatchQueue.main.async { self?.refresh() } }
        host.onPaired = { [weak host] viewer in host?.write("Paired with \(viewer.name)") }
        host.onPermissionsNeeded = { [weak self] in self?.wizard.show(fromMenu: false) }
        wizard.onChange = { [weak self] in DispatchQueue.main.async { self?.refresh() } }
        refresh()
        wizard.show(fromMenu: false, delay: 2) // at login: only when something is missing
    }

    private func add(_ title: String, _ action: Selector?, enabled: Bool = true) -> NSMenuItem {
        let mi = NSMenuItem(title: title, action: action, keyEquivalent: "")
        mi.target = self
        mi.isEnabled = enabled
        menu.addItem(mi)
        return mi
    }

    func menuWillOpen(_ menu: NSMenu) { refresh() }

    private func refresh() {
        let cfg = host.config
        status.title = "BetterVNC Agent \(host.version): \(host.statusLine)"
        viewers.title = host.viewersLine
        allowPairing.state = cfg.allowPairing ? .on : .off
        token.title = (cfg.token ?? "").isEmpty ? "Token or password… (none set)" : "Token or password… (set)"
        port.title = "Port… (\(cfg.port))"
        listenAll.state = cfg.listen != "127.0.0.1" ? .on : .off
        autostart.state = Installer.isAutostartEnabled() ? .on : .off
        permissions.title = wizard.missingPermissions.map { "Permissions… (\($0) not allowed)" } ?? "Permissions…"
    }

    // ------------------------------------------------------------------ actions (main thread)

    @objc private func toggleAllowPairing() {
        host.config.allowPairing.toggle()
        host.saveAndRestart()
    }

    @objc private func pairedViewers() {
        let list = host.config.snapshotViewers()
        if list.isEmpty {
            Dialogs.info("No viewer has paired yet.\n\nConnect from BetterVNC and click Allow on this screen.")
            return
        }
        let f = DateFormatter()
        f.dateStyle = .short
        f.timeStyle = .short
        let labels = list.map { "\($0.name) (paired \(f.string(from: $0.pairedAt)), last seen \($0.lastSeen.map(f.string) ?? "never"))" }
        let chosen = Dialogs.pick("Paired viewers\n\nSelect the viewers to remove. They will have to pair again.", items: labels, confirm: "Remove")
        for i in chosen { host.removeViewer(list[i].id) }
    }

    @objc private func editToken() {
        let answer = Dialogs.prompt("Token or password\n\nA viewer may use it instead of pairing. Leave it empty to allow paired viewers only.",
                                    value: host.config.token ?? "", buttons: ["Save", "Generate", "Cancel"], cancel: "Cancel")
        switch answer.button {
        case "Generate":
            host.config.token = AgentAuth.newToken()
            host.saveAndRestart()
            Dialogs.info("New token\n\n\(host.config.token ?? "")\n\nEnter it in the BetterVNC connection (Guest agent, token mode).")
        case "Save":
            let text = answer.text.trimmingCharacters(in: .whitespacesAndNewlines)
            host.config.token = text.isEmpty ? nil : text
            host.saveAndRestart()
        default:
            break
        }
    }

    @objc private func editPort() {
        let answer = Dialogs.prompt("Port\n\nThe TCP port the agent listens on (default 5970). Change the same value in the BetterVNC connection.",
                                    value: String(host.config.port), buttons: ["Save", "Cancel"], cancel: "Cancel")
        guard answer.button == "Save" else { return }
        guard let value = Int(answer.text.trimmingCharacters(in: .whitespaces)), (1...65535).contains(value) else {
            Dialogs.info("That is not a valid port number.")
            return
        }
        host.config.port = value
        host.saveAndRestart()
        if !host.isListening { Dialogs.info(host.statusLine) }
    }

    @objc private func toggleListenAll() {
        host.config.listen = host.config.listen == "127.0.0.1" ? "0.0.0.0" : "127.0.0.1"
        host.saveAndRestart()
    }

    @objc private func openPermissions() { wizard.show(fromMenu: true) }

    @objc private func toggleAutostart() {
        do { try Installer.setAutostart(!Installer.isAutostartEnabled(), configPath: host.configPath) }
        catch { host.write("Start at login could not be changed: \(error.localizedDescription)") }
        refresh()
    }

    @objc private func openLog() {
        if !FileManager.default.fileExists(atPath: host.logPath) { FileManager.default.createFile(atPath: host.logPath, contents: nil) }
        NSWorkspace.shared.open(URL(fileURLWithPath: host.logPath))
    }

    @objc private func quit() {
        host.stop()
        exit(0)
    }
}
