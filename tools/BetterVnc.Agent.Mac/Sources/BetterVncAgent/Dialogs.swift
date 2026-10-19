import AgentCore
import AppKit

/// Native dialogs for a menu bar app (the C# agent drew them with osascript). Callable from any thread: they run on the
/// main thread and the caller waits. The app has no Dock icon, so each one brings the app to the front first.
enum Dialogs {
    static let title = "BetterVNC Agent"

    /// Runs `body` on the main thread and returns its result.
    static func onMain<T>(_ body: () -> T) -> T {
        Thread.isMainThread ? body() : DispatchQueue.main.sync(execute: body)
    }

    static func info(_ text: String) {
        onMain {
            let alert = makeAlert(text, style: .informational)
            alert.addButton(withTitle: "OK")
            _ = run(alert, timeout: nil)
        }
    }

    /// A question with buttons, `buttons[0]` the default (rightmost). Returns the one clicked, or `cancel` when nobody
    /// answers within `timeout` seconds.
    static func choose(_ text: String, buttons: [String], cancel: String, timeout: TimeInterval?, style: NSAlert.Style = .warning) -> String {
        onMain {
            let alert = makeAlert(text, style: style)
            for b in buttons { alert.addButton(withTitle: b) }
            if let i = buttons.firstIndex(of: cancel) { alert.buttons[i].keyEquivalent = "\u{1b}" }
            let response = run(alert, timeout: timeout)
            let index = response.rawValue - NSApplication.ModalResponse.alertFirstButtonReturn.rawValue
            return buttons.indices.contains(index) ? buttons[index] : cancel
        }
    }

    /// A text field with buttons (`buttons[0]` default). Returns the button and the text.
    static func prompt(_ text: String, value: String, buttons: [String], cancel: String) -> (button: String, text: String) {
        onMain {
            let alert = makeAlert(text, style: .informational)
            for b in buttons { alert.addButton(withTitle: b) }
            if let i = buttons.firstIndex(of: cancel) { alert.buttons[i].keyEquivalent = "\u{1b}" }
            let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 320, height: 24))
            field.stringValue = value
            alert.accessoryView = field
            alert.window.initialFirstResponder = field
            let response = run(alert, timeout: nil)
            let index = response.rawValue - NSApplication.ModalResponse.alertFirstButtonReturn.rawValue
            return (buttons.indices.contains(index) ? buttons[index] : cancel, field.stringValue)
        }
    }

    /// A checkbox per item; returns the indexes ticked when `confirm` was clicked, else none.
    static func pick(_ text: String, items: [String], confirm: String) -> [Int] {
        onMain {
            let alert = makeAlert(text, style: .informational)
            alert.addButton(withTitle: confirm)
            alert.addButton(withTitle: "Cancel").keyEquivalent = "\u{1b}"
            let boxes = items.map { NSButton(checkboxWithTitle: $0, target: nil, action: nil) }
            let stack = NSStackView(views: boxes)
            stack.orientation = .vertical
            stack.alignment = .leading
            stack.spacing = 6
            stack.frame = NSRect(x: 0, y: 0, width: 460, height: CGFloat(items.count) * 24)
            alert.accessoryView = stack
            guard run(alert, timeout: nil) == .alertFirstButtonReturn else { return [] }
            return boxes.indices.filter { boxes[$0].state == .on }
        }
    }

    private static func makeAlert(_ text: String, style: NSAlert.Style) -> NSAlert {
        let alert = NSAlert()
        alert.alertStyle = style
        // The first paragraph is the headline, the rest the explanation.
        let parts = text.components(separatedBy: "\n\n")
        alert.messageText = parts.count > 1 ? parts[0] : title
        alert.informativeText = parts.count > 1 ? parts.dropFirst().joined(separator: "\n\n") : text
        return alert
    }

    /// Shows the alert in front of everything; after `timeout` seconds it is closed as if cancelled (.abort).
    private static func run(_ alert: NSAlert, timeout: TimeInterval?) -> NSApplication.ModalResponse {
        NSApp.activate(ignoringOtherApps: true)
        alert.window.level = .floating
        var timer: Timer?
        if let timeout {
            timer = Timer(timeInterval: timeout, repeats: false) { _ in NSApp.abortModal() }
            RunLoop.main.add(timer!, forMode: .modalPanel)
        }
        defer { timer?.invalidate() }
        return alert.runModal()
    }
}

/// The pairing question (MacApprover.cs): Allow or Deny, denied after the server's timeout.
final class AlertApprover: PairingApprover {
    func approve(viewerName: String, viewerId: String, address: String, timeout: TimeInterval) -> Bool {
        let text = "Allow BetterVNC on \(viewerName) to pair?\n\n" +
            "BetterVNC on \(viewerName) (\(address)) asks to use this Mac's clipboard (images, files, text), smooth scrolling and screen streaming through the guest agent.\n\n" +
            "You can remove it later from the BetterVNC menu bar icon."
        return Dialogs.choose(text, buttons: ["Allow", "Deny"], cancel: "Deny", timeout: timeout) == "Allow"
    }
}
