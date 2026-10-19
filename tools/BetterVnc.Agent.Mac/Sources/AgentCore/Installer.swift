import AppKit
import Foundation

/// `bettervnc-agent install`: puts BetterVNC Agent.app into /Applications (~/Applications when /Applications is not
/// writable), keeps the config (pairings, token), registers a LaunchAgent for this user and starts it. The app is built
/// and signed by build.sh; install copies it unchanged, so its signature, and with it the privacy permissions, stay
/// intact. `uninstall` reverses all of it. No administrator rights are needed.
public enum Installer {
    public static let label = "com.bettervnc.agent"
    public static let appName = "BetterVNC Agent.app"

    static var home: URL { FileManager.default.homeDirectoryForCurrentUser }
    public static var launchAgentPath: String { home.appendingPathComponent("Library/LaunchAgents/\(label).plist").path }
    /// Where launchd sends the agent's standard output.
    public static var stdoutLogPath: String { home.appendingPathComponent("Library/Logs/bettervnc-agent.log").path }
    /// Where the C# agent installed its app; removed on install.
    static var legacyBundle: String { home.appendingPathComponent("Library/Application Support/BetterVNC/\(appName)").path }
    static let oldPackageDir = "/usr/local/libexec/bettervnc-agent"

    /// /Applications when this user can write there, else ~/Applications.
    public static var applicationsDir: String {
        FileManager.default.isWritableFile(atPath: "/Applications") ? "/Applications" : home.appendingPathComponent("Applications").path
    }

    public static func executable(inBundle bundle: String) -> String { bundle + "/Contents/MacOS/bettervnc-agent" }

    /// The app this process runs from, or nil for a bare build.
    public static var runningBundle: String? {
        let url = Bundle.main.bundleURL
        return url.pathExtension == "app" && Bundle.main.bundleIdentifier == Permissions.bundleId ? url.path : nil
    }

    public static func install(dryRun: Bool, port: Int?, listen: String?) -> Int32 {
        guard let source = runningBundle else {
            fputs("'install' needs \(appName); build it with tools/BetterVnc.Agent.Mac/build.sh and run the bettervnc-agent inside it.\n", stderr)
            return 2
        }
        let fm = FileManager.default
        let dest = applicationsDir + "/" + appName
        let exe = executable(inBundle: dest)
        let configPath = AgentConfig.defaultPath
        let logPath = (configPath as NSString).deletingLastPathComponent + "/bettervnc-agent.log"
        print("Installing the BetterVNC guest agent for the current user\(dryRun ? " (dry run, nothing is changed)" : ""):")
        print("  app      : \(dest)")
        print("  config   : \(configPath)")
        print("  autostart: LaunchAgent \(launchAgentPath)")
        print("  log      : \(logPath)")
        print("  signature: \(signatureSummary(source))")

        let cfg: AgentConfig
        do { cfg = try AgentConfig.load(configPath) ?? AgentConfig.createNew() }
        catch {
            fputs("Cannot read \(configPath): \(error.localizedDescription)\n", stderr)
            return 1
        }
        if let port { cfg.port = port }
        if let listen { cfg.listen = listen }
        if dryRun {
            print("  auth     : \(cfg.authSummary)")
            return 0
        }

        stopRunning()
        if URL(fileURLWithPath: source).standardizedFileURL.path != URL(fileURLWithPath: dest).standardizedFileURL.path {
            do {
                try fm.createDirectory(atPath: applicationsDir, withIntermediateDirectories: true)
                if fm.fileExists(atPath: dest) { try fm.removeItem(atPath: dest) } // never overwritten in place: macOS kills a program whose signed file changes
                let copy = ProcessRunner.run("/usr/bin/ditto", [source, dest], timeout: 120)
                if !copy.ok { throw AgentProtocolError(copy.stderr.trimmingCharacters(in: .whitespacesAndNewlines)) }
            } catch {
                fputs("Could not copy \(appName) to \(applicationsDir): \(error)\n", stderr)
                return 1
            }
        }
        ProcessRunner.run("/usr/bin/xattr", ["-dr", "com.apple.quarantine", dest])
        if legacyBundle != dest, fm.fileExists(atPath: legacyBundle) {
            try? fm.removeItem(atPath: legacyBundle)
            print("  removed  : the previous agent, \(legacyBundle)")
        }
        let legacyDir = (configPath as NSString).deletingLastPathComponent
        for f in ["bettervnc-agent", "bettervnc-capture"] { try? fm.removeItem(atPath: legacyDir + "/" + f) }

        do {
            try cfg.save(configPath)
            try setAutostart(true, configPath: configPath, exe: exe)
            try startNow()
        } catch {
            fputs("Autostart registration failed: \(error)\n", stderr)
            fputs("The app and config are in place; start it by hand with: \"\(exe)\" --config \"\(configPath)\"\n", stderr)
            return 1
        }

        print("")
        print("Installed and running. Look for the BetterVNC icon in the menu bar: it opens the settings.")
        print("")
        if cfg.allowPairing {
            print("Pairing is on: in BetterVNC, enable the guest agent for the connection and connect. A dialog appears")
            print("on this screen asking to allow the viewer; click Allow once and the two stay paired.")
        }
        if let token = cfg.token, !token.isEmpty {
            print("Token or password (for viewers that use token mode): \(token)")
        } else {
            print("No token is set; set one in the menu bar item or with 'bettervnc-agent token generate' if you prefer token mode.")
        }
        print("It listens on \(cfg.listen):\(cfg.port). Remove everything with 'bettervnc-agent uninstall'.")
        if fm.fileExists(atPath: oldPackageDir) {
            print("")
            print("An older package left files in \(oldPackageDir); remove them with:")
            print("  sudo rm -rf \(oldPackageDir) /usr/local/bin/bettervnc-agent && sudo pkgutil --forget \(label)")
        }
        return 0
    }

    public static func uninstall(dryRun: Bool) -> Int32 {
        let fm = FileManager.default
        let configPath = AgentConfig.defaultPath
        let dir = (configPath as NSString).deletingLastPathComponent
        let bundles = ["/Applications/\(appName)", home.appendingPathComponent("Applications/\(appName)").path, legacyBundle]
            .filter { fm.fileExists(atPath: $0) }
        print("Removing the BetterVNC guest agent for the current user\(dryRun ? " (dry run)" : ""):")
        print("  LaunchAgent \(launchAgentPath)")
        for b in bundles { print("  \(b)") }
        print("  \(dir)")
        if dryRun { return 0 }

        stopRunning()
        try? fm.removeItem(atPath: launchAgentPath)
        for f in [configPath, SingleInstance.lockPath(forConfig: configPath), dir + "/bettervnc-agent.log", dir + "/bettervnc-agent.log.1", stdoutLogPath] {
            try? fm.removeItem(atPath: f)
        }
        for b in bundles {
            do { try fm.removeItem(atPath: b) } catch { print("  could not delete \(b): \(error.localizedDescription)") }
        }
        if let left = try? fm.contentsOfDirectory(atPath: dir), left.isEmpty { try? fm.removeItem(atPath: dir) }
        print("Removed.")
        return 0
    }

    // ------------------------------------------------------------------ autostart

    /// "Start at login" is on when the LaunchAgent is there.
    public static func isAutostartEnabled() -> Bool { FileManager.default.fileExists(atPath: launchAgentPath) }

    /// Writes or removes the LaunchAgent for `exe` (default: this program). Does not start or stop anything.
    public static func setAutostart(_ enable: Bool, configPath: String, exe: String? = nil) throws {
        if !enable {
            try? FileManager.default.removeItem(atPath: launchAgentPath)
            return
        }
        let program = exe ?? Bundle.main.executablePath ?? CommandLine.arguments[0]
        try FileManager.default.createDirectory(atPath: (launchAgentPath as NSString).deletingLastPathComponent, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(atPath: (stdoutLogPath as NSString).deletingLastPathComponent, withIntermediateDirectories: true)
        try launchAgentPlist(exe: program, configPath: configPath, logPath: stdoutLogPath).write(toFile: launchAgentPath, atomically: true, encoding: .utf8)
    }

    public static func launchAgentPlist(exe: String, configPath: String, logPath: String) -> String {
        func xml(_ s: String) -> String { s.replacingOccurrences(of: "&", with: "&amp;").replacingOccurrences(of: "<", with: "&lt;").replacingOccurrences(of: ">", with: "&gt;") }
        return """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict>
              <key>Label</key><string>\(label)</string>
              <key>ProgramArguments</key><array>
                <string>\(xml(exe))</string><string>--config</string><string>\(xml(configPath))</string>
              </array>
              <key>RunAtLoad</key><true/>
              <!-- Started again after a crash, but not after Quit, nor after a second copy found the agent already running. -->
              <key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>
              <key>ProcessType</key><string>Interactive</string>
              <!-- Login Items in System Settings shows the app's name and icon. -->
              <key>AssociatedBundleIdentifiers</key><string>\(Permissions.bundleId)</string>
              <key>StandardOutPath</key><string>\(xml(logPath))</string>
              <key>StandardErrorPath</key><string>\(xml(logPath))</string>
            </dict></plist>

            """
    }

    /// Starts the LaunchAgent now, the way a login does.
    static func startNow() throws {
        let domain = "gui/\(getuid())"
        ProcessRunner.run("/bin/launchctl", ["bootout", "\(domain)/\(label)"])
        let r = ProcessRunner.run("/bin/launchctl", ["bootstrap", domain, launchAgentPath])
        if !r.ok {
            let load = ProcessRunner.run("/bin/launchctl", ["load", "-w", launchAgentPath])
            if !load.ok { throw AgentProtocolError("launchctl bootstrap failed: \((r.stderr + r.stdout).trimmingCharacters(in: .whitespacesAndNewlines))") }
        }
    }

    /// Stops the LaunchAgent and every other running copy of the app (one LaunchServices started runs outside it).
    static func stopRunning() {
        ProcessRunner.run("/bin/launchctl", ["bootout", "gui/\(getuid())/\(label)"])
        let me = ProcessInfo.processInfo.processIdentifier
        for app in NSRunningApplication.runningApplications(withBundleIdentifier: Permissions.bundleId) where app.processIdentifier != me {
            app.terminate()
            let deadline = Date().addingTimeInterval(3)
            while !app.isTerminated && Date() < deadline { Thread.sleep(forTimeInterval: 0.1) }
            if !app.isTerminated { app.forceTerminate() }
        }
    }

    /// "ad hoc" or the signing certificate's name, from codesign.
    static func signatureSummary(_ bundle: String) -> String {
        let r = ProcessRunner.run("/usr/bin/codesign", ["-dvv", bundle])
        let text = r.stderr + r.stdout
        if text.contains("Signature=adhoc") { return "ad hoc (permissions have to be granted again after every rebuild)" }
        if let line = text.split(separator: "\n").first(where: { $0.hasPrefix("Authority=") }) { return String(line.dropFirst("Authority=".count)) }
        return r.ok ? "signed" : "not signed"
    }
}
