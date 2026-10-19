import AppKit
import ApplicationServices
import CoreGraphics
import Foundation

/// The two privacy permissions the agent needs: Screen Recording for the stream and Accessibility for scrolling. Both
/// belong to BetterVNC Agent (bundle id com.bettervnc.agent).
public enum Permissions {
    public static let bundleId = "com.bettervnc.agent"

    /// System Settings, Privacy & Security, Screen & System Audio Recording (Screen Recording before macOS 15).
    public static let screenRecordingPane = "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture"
    /// System Settings, Privacy & Security, Accessibility.
    public static let accessibilityPane = "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility"

    /// tccutil's names for them.
    public static let screenRecordingService = "ScreenCapture"
    public static let accessibilityService = "Accessibility"

    /// Hidden command-line flags that answer for a new process of this app (see `screenRecording(fresh:)`).
    public static let checkFlag = "--permissions"
    public static let requestFlag = "--request-permission"

    /// Whether macOS lets the agent record the screen. With `fresh`, asked in a new process of this app, because a
    /// process that has already asked may keep its first answer after the switch is turned on.
    public static func screenRecording(fresh: Bool = false) -> Bool {
        if fresh, let answer = askNewProcess(checkFlag, timeout: 5) { return answer }
        return CGPreflightScreenCaptureAccess()
    }

    /// Asks macOS for Screen Recording (its prompt, and BetterVNC Agent listed, switched off), from a new process.
    public static func requestScreenRecording() {
        if askNewProcess(requestFlag, timeout: 10) == nil { _ = CGRequestScreenCaptureAccess() }
    }

    /// Whether macOS lets the agent post input events (scrolling). Not cached by macOS.
    public static var accessibility: Bool { AXIsProcessTrusted() }

    /// Asks macOS for Accessibility, which shows its prompt and lists BetterVNC Agent; true when it is already allowed.
    @discardableResult
    public static func requestAccessibility() -> Bool {
        if AXIsProcessTrusted() { return true }
        let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        return AXIsProcessTrustedWithOptions(options)
    }

    /// Whether this process is BetterVNC Agent.app (and not, say, a build started from Terminal, whose permissions belong
    /// to Terminal).
    public static var runningAsApp: Bool { Bundle.main.bundleIdentifier == bundleId }

    /// Removes BetterVNC Agent's entry for one permission, so the next request lists it afresh for the build installed
    /// now. macOS keeps the code requirement it saw when it created an entry; for an ad-hoc signed build that is one build,
    /// so after an update the entry looks switched on and is refused anyway. Returns why it did not work, or nil.
    public static func resetEntry(_ service: String) -> String? {
        guard runningAsApp else { return "the agent is not running as BetterVNC Agent.app" }
        let r = ProcessRunner.run("/usr/bin/tccutil", ["reset", service, bundleId])
        return r.ok ? nil : (r.stderr + r.stdout).trimmingCharacters(in: .whitespacesAndNewlines)
    }

    public static func openSettings(_ pane: String) {
        if let url = URL(string: pane) { NSWorkspace.shared.open(url) }
    }

    /// Answers the hidden flags in a new process: prints "screenRecording=granted|denied". Called from main before
    /// anything else.
    public static func answerFlag(_ flag: String) -> Never {
        let granted = flag == requestFlag ? CGRequestScreenCaptureAccess() : CGPreflightScreenCaptureAccess()
        print("screenRecording=\(granted ? "granted" : "denied")")
        exit(0)
    }

    private static func askNewProcess(_ flag: String, timeout: TimeInterval) -> Bool? {
        guard let exe = Bundle.main.executablePath else { return nil }
        let r = ProcessRunner.run(exe, [flag], timeout: timeout)
        guard r.ok, r.stdout.contains("screenRecording=") else { return nil }
        return r.stdout.contains("screenRecording=granted")
    }
}

/// Runs a tool and collects its output.
public enum ProcessRunner {
    public struct Result {
        public var status: Int32
        public var stdout: String
        public var stderr: String
        public var ok: Bool { status == 0 }
    }

    @discardableResult
    public static func run(_ executable: String, _ arguments: [String], timeout: TimeInterval = 15) -> Result {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = arguments
        let out = Pipe(), err = Pipe()
        process.standardOutput = out
        process.standardError = err
        process.standardInput = FileHandle.nullDevice
        do { try process.run() } catch { return Result(status: -1, stdout: "", stderr: "\(error)") }
        var outData = Data(), errData = Data()
        let group = DispatchGroup()
        group.enter()
        DispatchQueue.global().async { outData = out.fileHandleForReading.readDataToEndOfFile(); group.leave() }
        group.enter()
        DispatchQueue.global().async { errData = err.fileHandleForReading.readDataToEndOfFile(); group.leave() }
        let deadline = Date().addingTimeInterval(timeout)
        while process.isRunning && Date() < deadline { Thread.sleep(forTimeInterval: 0.02) }
        if process.isRunning {
            process.terminate()
            _ = group.wait(timeout: .now() + 1)
            return Result(status: -1, stdout: "", stderr: "\(executable) did not finish within \(Int(timeout)) s.")
        }
        process.waitUntilExit()
        _ = group.wait(timeout: .now() + 2)
        return Result(status: process.terminationStatus, stdout: String(decoding: outData, as: UTF8.self),
                      stderr: String(decoding: errData, as: UTF8.self))
    }
}
