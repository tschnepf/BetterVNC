import AgentCore
import Foundation

/// Says which of Screen Recording and Accessibility BetterVNC Agent still lacks and what each is for, with a button per
/// missing one. The button clears the agent's old entry (macOS refuses an entry an earlier ad-hoc build left, even when
/// its switch looks on), asks macOS again so the agent is listed afresh, and opens that page of System Settings. Then it
/// waits for the switch and comes back for the other one, or says that all is set. At login and when a stream or a
/// scroll is refused it appears only when something is missing, and not within two minutes of "Later"; from the menu
/// always. One at a time; any thread.
final class PermissionWizard {
    static let openScreenRecording = "Open Screen Recording"
    static let openAccessibility = "Open Accessibility"

    private let host: AgentHost
    private let lock = NSLock()
    private var running = false
    private var dismissedAt = Date.distantPast
    private var missing: String?
    /// Called when `missingPermissions` changed; any thread.
    var onChange: (() -> Void)?

    init(host: AgentHost) { self.host = host }

    /// "Screen Recording, Accessibility", one of them, or nil when both are allowed (as of the last check).
    var missingPermissions: String? {
        lock.lock(); defer { lock.unlock() }
        return missing
    }

    func show(fromMenu: Bool, delay: TimeInterval = 0) {
        lock.lock()
        if running || (!fromMenu && Date().timeIntervalSince(dismissedAt) < 120) { lock.unlock(); return }
        running = true
        lock.unlock()
        DispatchQueue.global().async {
            if delay > 0 { Thread.sleep(forTimeInterval: delay) }
            self.run(confirmWhenDone: fromMenu)
            self.lock.lock()
            self.running = false
            self.lock.unlock()
            self.onChange?()
        }
    }

    private func run(confirmWhenDone: Bool) {
        var confirm = confirmWhenDone
        while true {
            let screen = Permissions.screenRecording(fresh: true), access = Permissions.accessibility
            lock.lock()
            missing = [screen ? nil : "Screen Recording", access ? nil : "Accessibility"].compactMap { $0 }.joined(separator: ", ")
            if missing == "" { missing = nil }
            lock.unlock()
            onChange?()
            if screen && access {
                if confirm {
                    Dialogs.info("BetterVNC Agent has both permissions it needs.\n\n" +
                                 "Screen Recording: allowed, so the viewer's Stream picture modes work.\n" +
                                 "Accessibility: allowed, so smooth scrolling works.")
                }
                return
            }

            let text = "BetterVNC Agent needs two macOS permissions.\n\n" +
                "Screen Recording: \(screen ? "allowed" : "NOT ALLOWED YET"). The viewer's Stream picture modes need it.\n" +
                "Accessibility: \(access ? "allowed" : "NOT ALLOWED YET"). Smooth scrolling needs it.\n\n" +
                "Click a button to open that page of System Settings, then switch on BetterVNC Agent there. " +
                "The button first clears the entry an earlier build of the agent may have left, which macOS refuses even when it looks switched on, " +
                "so BetterVNC Agent is listed afresh. If macOS offers Quit & Reopen, choose it: the agent starts again by itself."
            var buttons: [String] = []
            if !screen { buttons.append(PermissionWizard.openScreenRecording) }
            if !access { buttons.append(PermissionWizard.openAccessibility) }
            buttons.append("Later")
            let choice = Dialogs.choose(text, buttons: buttons, cancel: "Later", timeout: 540)

            let granted: () -> Bool
            switch choice {
            case PermissionWizard.openScreenRecording:
                resetEntry(Permissions.screenRecordingService)
                Permissions.requestScreenRecording() // lists BetterVNC Agent again, switched off
                Permissions.openSettings(Permissions.screenRecordingPane)
                granted = { Permissions.screenRecording(fresh: true) }
            case PermissionWizard.openAccessibility:
                resetEntry(Permissions.accessibilityService)
                Permissions.requestAccessibility()
                Permissions.openSettings(Permissions.accessibilityPane)
                granted = { Permissions.accessibility }
            default:
                lock.lock(); dismissedAt = Date(); lock.unlock()
                return
            }

            // Wait for the switch (up to ten minutes), then come back for the other one, or confirm.
            let until = Date().addingTimeInterval(600)
            while !granted() {
                if Date() > until { return }
                Thread.sleep(forTimeInterval: 2)
            }
            confirm = true
        }
    }

    private func resetEntry(_ service: String) {
        if let problem = Permissions.resetEntry(service) {
            host.write("Could not clear BetterVNC Agent's \(service) entry (\(problem)); if it is listed and on, remove it with − and click the button again.")
        } else {
            host.write("Cleared BetterVNC Agent's \(service) entry; asking macOS again.")
        }
    }
}
