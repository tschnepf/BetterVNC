// BetterVNC guest agent for macOS. Runs in the signed-in user's session as BetterVNC Agent.app; the BetterVNC viewer
// connects to it next to the VNC session for the clipboard (text, HTML, images, files), smooth scrolling and an H.264
// stream of the screen. Viewers get in by pairing (a dialog on this screen asks to Allow) or with a token.
import AgentCore
import AppKit
import Foundation

let arguments = Array(CommandLine.arguments.dropFirst())

// A new process asked by the permission wizard whether Screen Recording is allowed: answer and leave.
if let flag = arguments.first, flag == Permissions.checkFlag || flag == Permissions.requestFlag {
    Permissions.answerFlag(flag)
}

var configPath: String?
var port: Int?
var listen: String?
var token = ProcessInfo.processInfo.environment["BETTERVNC_AGENT_TOKEN"]
var verbose = false, console = false, dryRun = false
var command: String?
var rest: [String] = []

func usage() {
    print("""
        BetterVNC guest agent

          bettervnc-agent install [--port <n>] [--listen <addr>] [--dry-run]
                                     copy BetterVNC Agent.app to /Applications, start it at login (LaunchAgent) and now
          bettervnc-agent uninstall [--dry-run]
          bettervnc-agent [--config <file>] [--port <n>] [--listen <addr>] [--token <t>] [--verbose] [--console]
                                     serve, in the menu bar unless --console
          bettervnc-agent token show | set <value> | generate | clear      token or password for token-mode viewers
          bettervnc-agent viewers [remove <name or id>]                     paired viewers
          bettervnc-agent pairing on | off                                  accept new pairings
          bettervnc-agent init [--config <file>]                            create a config with a token

        The config, ~/Library/Application Support/BetterVNC/bettervnc-agent.json, is created on first start. Viewers get
        in by pairing (you click Allow in a dialog on this screen) or with the token. The file also holds port, listen,
        maxPayloadMb, enableVideo (screen streaming for the viewer's Stream picture modes) and the scroll settings
        (enableScroll, scrollScale, scrollInvert, scrollPhases).
        """)
}

do {
    var queue = arguments[...]
    func need(_ option: String) throws -> String {
        guard let value = queue.popFirst() else { throw AgentProtocolError("\(option) needs a value.") }
        return value
    }
    while let arg = queue.popFirst() {
        switch arg {
        case "--config": configPath = try need(arg)
        case "--port":
            guard let p = Int(try need(arg)) else { throw AgentProtocolError("--port needs a number.") }
            port = p
        case "--listen": listen = try need(arg)
        case "--token": token = try need(arg)
        case "--verbose", "-v": verbose = true
        case "--console": console = true
        case "--dry-run": dryRun = true
        case "--help", "-h": usage(); exit(0)
        default:
            if arg.hasPrefix("-psn_") { continue } // added by older LaunchServices
            if command == nil && !arg.hasPrefix("-") { command = arg } else { rest.append(arg) }
        }
    }
} catch {
    fputs("\(error)\n", stderr)
    exit(2)
}

let config = configPath ?? AgentConfig.defaultPath

func loadOrCreate(_ path: String) -> (AgentConfig, Bool) {
    do {
        if let cfg = try AgentConfig.load(path) { return (cfg, false) }
        let cfg = AgentConfig.createNew()
        try cfg.save(path)
        return (cfg, true)
    } catch {
        fputs("Cannot use the config \(path): \(error.localizedDescription)\n", stderr)
        exit(1)
    }
}

func save(_ cfg: AgentConfig) {
    do { try cfg.save(config) } catch {
        fputs("Cannot save \(config): \(error.localizedDescription)\n", stderr)
        exit(1)
    }
}

// ---------------------------------------------------------------------------------------------- serve

func serve() -> Never {
    let lockPath = SingleInstance.lockPath(forConfig: config)
    guard let instance = SingleInstance.acquire(lockPath) else {
        // Exit 0: the LaunchAgent restarts the agent only after a failure, so this copy is not started again.
        print("Another BetterVNC guest agent is already running with \(config) (it holds \(lockPath)); this copy exits.")
        exit(0)
    }
    let (cfg, created) = loadOrCreate(config)
    if let port { cfg.port = port }
    if let listen { cfg.listen = listen }
    if let token, !token.trimmingCharacters(in: .whitespaces).isEmpty { cfg.token = token }

    // A connection to the window server: menu bar item, dialogs and ScreenCaptureKit need it.
    let app = NSApplication.shared
    app.setActivationPolicy(.accessory)

    let host = AgentHost(configPath: config, config: cfg, verbose: verbose, approver: AlertApprover())
    print("BetterVNC guest agent \(host.version) on macos")
    print("Config: \(config)  (\(cfg.authSummary))")
    print("Scroll injection: \(host.inputDescription)")
    print("Screen streaming: \(host.videoDescription)")
    if host.input != nil && !Permissions.accessibility {
        print("  Accessibility permission missing: System Settings > Privacy & Security > Accessibility > turn on BetterVNC Agent. Scroll events are dropped until then.")
    }
    if created {
        print("New config written. Connect from BetterVNC and click Allow in the dialog that appears here, or set a token with 'bettervnc-agent token generate'.")
    }
    fflush(stdout)

    do { try host.start() } catch {
        fputs("Cannot listen on \(cfg.listen):\(cfg.port): \(error)\n", stderr)
        if console { exit(4) }
        // In the menu bar stay alive, so the port can be changed there.
    }

    var signalSources: [DispatchSourceSignal] = []
    for sig in [SIGINT, SIGTERM] {
        signal(sig, SIG_IGN)
        let source = DispatchSource.makeSignalSource(signal: sig, queue: .main)
        source.setEventHandler {
            if console { print("Stopping.") }
            host.stop()
            exit(0)
        }
        source.resume()
        signalSources.append(source)
    }

    var menu: StatusMenu?
    if console {
        if !host.isListening { exit(4) }
        print("Press Ctrl+C to stop.")
    } else {
        menu = StatusMenu(host: host)
    }
    withExtendedLifetime((instance, menu, signalSources)) { app.run() }
    exit(0)
}

// ---------------------------------------------------------------------------------------------- settings commands

func tokenCommand() -> Int32 {
    let (cfg, _) = loadOrCreate(config)
    switch rest.first ?? "show" {
    case "show":
        print((cfg.token ?? "").isEmpty ? "(no token set; viewers pair by clicking Allow on this screen)" : cfg.token!)
        return 0
    case "generate":
        cfg.token = AgentAuth.newToken()
    case "set":
        let value = rest.count > 1 ? rest.dropFirst().joined(separator: " ") : token
        guard let value, !value.trimmingCharacters(in: .whitespaces).isEmpty else {
            fputs("token set needs a value.\n", stderr)
            return 2
        }
        cfg.token = value.trimmingCharacters(in: .whitespacesAndNewlines)
    case "clear":
        cfg.token = nil
    default:
        fputs("token show | set <value> | generate | clear\n", stderr)
        return 2
    }
    save(cfg)
    print(cfg.token == nil ? "Token cleared; only paired viewers can connect." : "Token: \(cfg.token!)")
    print("A running agent picks this up after a restart (or a change in its menu bar item).")
    return 0
}

func viewersCommand() -> Int32 {
    let (cfg, _) = loadOrCreate(config)
    if rest.count >= 2 && rest[0] == "remove" {
        let who = rest.dropFirst().joined(separator: " ")
        guard cfg.removeViewer(who) else {
            fputs("No paired viewer named '\(who)'.\n", stderr)
            return 1
        }
        save(cfg)
        print("Removed \(who). It has to pair again before it can connect.")
        return 0
    }
    let list = cfg.snapshotViewers()
    if list.isEmpty { print("No paired viewers."); return 0 }
    let f = DateFormatter()
    f.dateStyle = .short
    f.timeStyle = .short
    for v in list {
        let name = v.name.padding(toLength: max(24, v.name.count), withPad: " ", startingAt: 0)
        print("\(name) paired \(f.string(from: v.pairedAt))  last seen \(v.lastSeen.map(f.string) ?? "never")  from \(v.address ?? "?")  id \(v.id)")
    }
    return 0
}

func pairingCommand() -> Int32 {
    let (cfg, _) = loadOrCreate(config)
    guard let value = rest.first else {
        print(cfg.allowPairing ? "Pairing is on." : "Pairing is off.")
        return 0
    }
    if value == "on" { cfg.allowPairing = true }
    if value == "off" { cfg.allowPairing = false }
    if !cfg.allowPairing && (cfg.token ?? "").isEmpty && cfg.snapshotViewers().isEmpty {
        print("Warning: pairing off, no token and no paired viewers: nothing can connect until you set a token.")
    }
    save(cfg)
    print(cfg.allowPairing ? "Pairing is on." : "Pairing is off.")
    return 0
}

func initCommand() -> Int32 {
    let cfg = AgentConfig.createNew()
    if let port { cfg.port = port }
    if let listen { cfg.listen = listen }
    let given = (token ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
    cfg.token = given.isEmpty ? AgentAuth.newToken() : given
    save(cfg)
    print("Wrote \(config)")
    print("Token: \(cfg.token!)")
    print("Pairing stays on as well; enter the token in BetterVNC only if you choose token mode there.")
    return 0
}

switch command {
case nil, "run": serve()
case "install": exit(Installer.install(dryRun: dryRun, port: port, listen: listen))
case "uninstall": exit(Installer.uninstall(dryRun: dryRun))
case "token": exit(tokenCommand())
case "show-token": rest = ["show"]; exit(tokenCommand())
case "viewers": exit(viewersCommand())
case "pairing": exit(pairingCommand())
case "init": exit(initCommand())
default:
    fputs("Unknown command '\(command!)'.\n", stderr)
    usage()
    exit(2)
}
