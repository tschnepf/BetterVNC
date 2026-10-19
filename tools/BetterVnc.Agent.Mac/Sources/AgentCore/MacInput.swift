import ApplicationServices
import CoreGraphics
import Foundation

/// Scroll injection through CoreGraphics (MacInput.cs): pixel scroll-wheel events, as a trackpad makes, posted to the HID
/// event tap at the pointer. With phases on, the viewer's begin/continue/end become CGEvent scroll phases, so AppKit treats
/// the stream like a trackpad gesture. Posting needs Accessibility; without it macOS drops the events silently.
public final class MacInput: GuestInput {
    // CGEvent scroll phases (kCGScrollPhase*).
    static let phaseBegan: Int64 = 1, phaseChanged: Int64 = 2, phaseEnded: Int64 = 4

    private let scale: Double
    private let invert: Bool
    public let phases: Bool
    private let source = CGEventSource(stateID: .hidSystemState)
    private let lock = NSLock()
    private var restX = 0.0, restY = 0.0
    private var trustChecked = false
    /// Where the events go; replaced in tests.
    var post: (_ wheel1: Int32, _ wheel2: Int32, _ phase: Int64?) -> Void

    public var log: ((String) -> Void)?
    /// Called once, at the first scroll while Accessibility is missing; true when the menu bar's wizard takes care of it,
    /// false to have macOS show its own prompt.
    public var accessibilityMissing: (() -> Bool)?
    /// Whether events can be posted; replaced in tests.
    var isTrusted: () -> Bool = { AXIsProcessTrusted() }

    /// - Parameters:
    ///   - scale: multiplier for the pixel deltas (1.0 = what the viewer sends).
    ///   - invert: flip both axes, for setups where macOS applies "natural" direction to synthetic events too.
    ///   - phases: forward begin/continue/end as scroll phases.
    public init(scale: Double = 1.0, invert: Bool = false, phases: Bool = true) {
        self.scale = scale > 0 ? scale : 1.0
        self.invert = invert
        self.phases = phases
        let source = self.source
        post = { wheel1, wheel2, phase in
            guard let event = CGEvent(scrollWheelEvent2Source: source, units: .pixel, wheelCount: 2, wheel1: wheel1, wheel2: wheel2, wheel3: 0) else { return }
            event.setIntegerValueField(.scrollWheelEventIsContinuous, value: 1)
            if let phase { event.setIntegerValueField(.scrollWheelEventScrollPhase, value: phase) }
            event.post(tap: .cghidEventTap)
        }
    }

    public var description: String { "CoreGraphics pixel scroll" + (phases ? " with phases" : "") }

    public func scroll(dx: Double, dy: Double, wheelDx: Double?, wheelDy: Double?, phase: String?) throws {
        lock.lock()
        defer { lock.unlock() }
        if !trustChecked {
            trustChecked = true
            if !isTrusted() {
                log?("Scroll injection needs the Accessibility permission: System Settings > Privacy & Security > Accessibility, " +
                     "turn on BetterVNC Agent (or, when you started it from Terminal, Terminal). Events are dropped until then.")
                if accessibilityMissing?() != true { _ = Permissions.requestAccessibility() } // no wizard: macOS's own prompt
            }
        }

        let sign = invert ? -1.0 : 1.0
        restY += dy * scale * sign
        restX += dx * scale * sign
        let py = restY.rounded(.towardZero), px = restX.rounded(.towardZero)
        restY -= py
        restX -= px
        // wheel1 is vertical with positive = up (as on the wire); wheel2 is horizontal with positive = left.
        let wheel1 = Int32(clamping: Int(py)), wheel2 = Int32(clamping: -Int(px))
        let moved = wheel1 != 0 || wheel2 != 0

        guard phases else {
            if moved { post(wheel1, wheel2, nil) }
            return
        }
        switch phase {
        case AgentScrollPhase.begin:
            post(0, 0, MacInput.phaseBegan)
            if moved { post(wheel1, wheel2, MacInput.phaseChanged) }
        case AgentScrollPhase.end:
            if moved { post(wheel1, wheel2, MacInput.phaseChanged) }
            post(0, 0, MacInput.phaseEnded)
            restX = 0
            restY = 0
        default:
            if moved { post(wheel1, wheel2, MacInput.phaseChanged) }
        }
    }
}
