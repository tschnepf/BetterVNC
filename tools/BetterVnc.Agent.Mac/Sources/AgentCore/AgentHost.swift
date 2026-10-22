import Foundation

/// Owns the config, the log, the clipboard, scrolling, streaming and the server, and restarts the server when settings
/// change (AgentHost.cs). The menu bar item and the command line sit on top of it.
public final class AgentHost {
    public let configPath: String
    public let config: AgentConfig
    public let verbose: Bool
    public let log: AgentLog
    public let version: String
    public let clipboard: GuestClipboard
    public let input: MacInput?
    public let video: MacScreenSource?
    private var videoUnavailable: String?
    private var inputUnavailable: String?
    private let approver: PairingApprover?
    private let lock = NSLock()
    private var server: AgentServer?
    public private(set) var lastError: String?

    /// Status, viewers or pairings changed; any thread.
    public var onChanged: (() -> Void)?
    /// A viewer was paired and saved; any thread.
    public var onPaired: ((PairedViewer) -> Void)?
    /// A stream or a scroll was refused for a missing permission; any thread. nil: nobody shows a wizard.
    public var onPermissionsNeeded: (() -> Void)?

    public init(configPath: String, config: AgentConfig, verbose: Bool, approver: PairingApprover?, echo: Bool = true) {
        self.configPath = configPath
        self.config = config
        self.verbose = verbose
        self.approver = approver
        log = AgentLog(path: (configPath as NSString).deletingLastPathComponent + "/bettervnc-agent.log", echo: echo)
        version = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "dev"
        clipboard = MacClipboard()

        var input: MacInput?
        if config.enableScroll {
            input = MacInput(scale: config.scrollScale, invert: config.scrollInvert, phases: config.scrollPhases)
        }
        self.input = input
        var video: MacScreenSource?
        if config.enableVideo {
            do { video = try MacScreenSource() } catch {
                videoUnavailable = "\(error)"
            }
        }
        self.video = video
        input?.log = { [weak self] in self?.write($0) }
        input?.accessibilityMissing = { [weak self] in self?.permissionsNeeded() ?? false }
        video?.log = { [weak self] in self?.write($0) }
        video?.permissionMissing = { [weak self] in _ = self?.permissionsNeeded() }
        if let videoUnavailable { write("Screen streaming unavailable: \(videoUnavailable)") }
    }

    public var logPath: String { log.path ?? "" }
    public var appName: String { "BetterVNC agent \(version)" }

    public var isListening: Bool {
        lock.lock(); defer { lock.unlock() }
        return server != nil
    }

    public var port: Int {
        lock.lock(); defer { lock.unlock() }
        return server?.port ?? config.port
    }

    public var connectedViewers: Int {
        lock.lock(); defer { lock.unlock() }
        return server?.sessionCount ?? 0
    }

    public var statusLine: String {
        if isListening { return "Listening on \(config.listen):\(port)" }
        if let lastError { return "Not running: \(lastError)" }
        return "Stopped"
    }

    public var viewersLine: String {
        let hasToken = !(config.token ?? "").isEmpty
        let auth: String
        switch (hasToken, config.allowPairing) {
        case (false, true): auth = "pairing"
        case (false, false): auth = "no way to connect"
        case (true, true): auth = "pairing or token"
        case (true, false): auth = "token only"
        }
        return "\(connectedViewers) connected, \(config.snapshotViewers().count) paired (\(auth))"
    }

    public var videoDescription: String {
        if let video { return video.description }
        if !config.enableVideo { return "off" }
        return "unavailable: \(videoUnavailable ?? "unknown reason")"
    }

    public var inputDescription: String { input?.description ?? "off" }

    // ------------------------------------------------------------------ lifecycle

    public func start() throws {
        let cfg = config
        var options = AgentServerOptions()
        let token = (cfg.token ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        options.token = token.isEmpty ? nil : token
        options.findViewer = { [weak cfg] id in cfg?.findViewer(id) }
        options.viewerPaired = { [weak self] viewer in self?.viewerPaired(viewer) }
        options.pairingApprover = approver
        options.allowPairing = cfg.allowPairing
        options.port = cfg.port
        options.listenAddress = cfg.listen
        options.maxPayloadBytes = max(1, cfg.maxPayloadMb) * 1024 * 1024
        options.stagingDirectory = cfg.stagingDirectory ?? (NSTemporaryDirectory() as NSString).appendingPathComponent("bettervnc-agent")
        options.appName = appName

        let server: AgentServer
        do {
            server = try AgentServer(clipboard: clipboard, options: options, input: input, video: video)
            server.log = { [weak self] line in self?.serverLog(line) }
            server.viewerConnected = { [weak self] _, _, _ in self?.onChanged?() }
            try server.start()
        } catch {
            throw fail("\(error)")
        }
        lock.lock()
        self.server = server
        lastError = nil
        lock.unlock()
        onChanged?()
    }

    private func fail(_ message: String) -> Error {
        lock.lock(); lastError = message; lock.unlock()
        write("Cannot listen on \(config.listen):\(config.port): \(message)")
        onChanged?()
        return AgentProtocolError(message)
    }

    public func stop() {
        lock.lock()
        let s = server
        server = nil
        lock.unlock()
        s?.stop()
        onChanged?()
    }

    /// Saves the config and restarts the server with it; connected viewers reconnect by themselves.
    public func saveAndRestart() {
        saveQuietly()
        stop()
        try? start() // a failure shows in statusLine
        write("Settings applied: port \(config.port), listen \(config.listen), \(config.authSummary)")
    }

    public func saveQuietly() {
        do { try config.save(configPath) } catch { write("Could not save config: \(error.localizedDescription)") }
    }

    public func removeViewer(_ idOrName: String) {
        if config.removeViewer(idOrName) {
            saveQuietly()
            write("Removed paired viewer \(idOrName)")
            onChanged?()
        }
    }

    private func viewerPaired(_ viewer: PairedViewer) {
        config.addViewer(viewer)
        saveQuietly()
        onChanged?()
        onPaired?(viewer)
    }

    /// True when someone (the menu bar's wizard) takes care of it.
    private func permissionsNeeded() -> Bool {
        guard let handler = onPermissionsNeeded else { return false }
        handler()
        return true
    }

    // ------------------------------------------------------------------ log

    public func write(_ message: String) { log.write(message) }

    private func serverLog(_ line: String) {
        if !verbose && (line.hasPrefix("Clipboard changed") || line.hasPrefix("Clipboard set") || line.hasPrefix("Scroll:")) { return }
        write(line)
        if line.hasSuffix("disconnected") || line.contains(": connected") { onChanged?() }
    }
}
