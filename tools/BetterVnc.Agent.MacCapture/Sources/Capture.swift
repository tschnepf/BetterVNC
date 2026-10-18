import CoreGraphics
import CoreMedia
import CoreVideo
import Foundation
import ScreenCaptureKit

struct Settings {
    var codec: Codec = .h264
    var bitrateKbps = 8_000
    var fps = 60
    /// Largest picture wanted (the viewer's window, say); 0 = the display's own size.
    var maxWidth = 0
    var maxHeight = 0
    /// The display to capture; nil = the main display (the one with the menu bar).
    var displayID: CGDirectDisplayID?
    var lowLatency = true
}

/// Size of the captured display in pixels and where it sits in the desktop of all displays.
struct DisplayGeometry: Equatable {
    var pixelWidth = 0
    var pixelHeight = 0
    var offsetX = 0
    var offsetY = 0
    var displayCount = 1

    static func of(_ id: CGDirectDisplayID) -> DisplayGeometry? {
        guard let mode = CGDisplayCopyDisplayMode(id), mode.pixelWidth > 0, mode.pixelHeight > 0 else { return nil }
        let bounds = CGDisplayBounds(id)
        let scale = bounds.width > 0 ? Double(mode.pixelWidth) / Double(bounds.width) : 1
        var capacity: UInt32 = 0
        _ = CGGetActiveDisplayList(0, nil, &capacity)
        var ids = [CGDirectDisplayID](repeating: 0, count: Int(capacity))
        var found: UInt32 = 0
        _ = CGGetActiveDisplayList(capacity, &ids, &found)
        var desktop = CGRect.null
        var count = 0
        for other in ids.prefix(Int(found)) where CGDisplayMirrorsDisplay(other) == kCGNullDirectDisplay {
            desktop = desktop.union(CGDisplayBounds(other))
            count += 1
        }
        if desktop.isNull { desktop = bounds }
        // Pixels from the desktop's top-left corner, at this display's scale: where Screen Sharing's framebuffer has it
        // when it puts all displays side by side.
        return DisplayGeometry(
            pixelWidth: mode.pixelWidth, pixelHeight: mode.pixelHeight,
            offsetX: Int((Double(bounds.minX - desktop.minX) * scale).rounded()),
            offsetY: Int((Double(bounds.minY - desktop.minY) * scale).rounded()),
            displayCount: max(count, 1))
    }
}

/// ScreenCaptureKit capture of one display into a `VideoEncoder`. ScreenCaptureKit scales the picture on the GPU to the
/// size asked for and delivers a frame only when something changed, at most `fps` times a second; a still screen
/// costs nothing. Everything here runs on `queue`.
final class Capture: NSObject, SCStreamOutput, SCStreamDelegate {
    private let queue = DispatchQueue(label: "bettervnc.capture", qos: .userInteractive)
    private let origin = CMClockGetTime(CMClockGetHostTimeClock())
    private var settings: Settings
    private var display: SCDisplay?
    private var captureStream: SCStream?
    private let configuration = SCStreamConfiguration()
    private var geometry = DisplayGeometry()
    private var encoder: VideoEncoder?
    private var latest: CVPixelBuffer?
    private var lastTime = CMTime.invalid
    private var started = false
    private var keyframeWanted = false
    private var timer: DispatchSourceTimer?

    init(settings: Settings) {
        self.settings = settings
        super.init()
    }

    func start() {
        if !CGPreflightScreenCaptureAccess() {
            // Shows macOS's Screen Recording prompt (the first time only). It names the agent, which started this helper.
            _ = CGRequestScreenCaptureAccess()
        }
        SCShareableContent.getExcludingDesktopWindows(false, onScreenWindowsOnly: true) { content, error in
            self.queue.async {
                guard let content else { Capture.fail(error) }
                let wanted = self.settings.displayID ?? CGMainDisplayID()
                guard let display = content.displays.first(where: { $0.displayID == wanted }) ?? content.displays.first else {
                    AgentPipe.fail("This Mac has no display to capture.")
                }
                self.begin(display)
            }
        }
    }

    /// A command from the agent (see main.swift).
    func perform(_ command: String, _ numbers: [Int]) {
        queue.async {
            switch command {
            case "bitrate" where numbers.count == 1:
                self.settings.bitrateKbps = max(numbers[0], 100)
                self.encoder?.setRates(bitrateKbps: self.settings.bitrateKbps, fps: self.settings.fps)
            case "fps" where numbers.count == 1:
                self.settings.fps = min(max(numbers[0], 1), 120)
                self.encoder?.setRates(bitrateKbps: self.settings.bitrateKbps, fps: self.settings.fps)
                self.configuration.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(self.settings.fps))
                self.reconfigure()
            case "keyframe":
                self.requestKeyframe()
            case "size" where numbers.count == 2:
                self.settings.maxWidth = max(numbers[0], 0)
                self.settings.maxHeight = max(numbers[1], 0)
                self.applySize()
            case "quit":
                exit(0)
            default:
                logLine("Unknown command: \(command) \(numbers)")
            }
        }
    }

    // ------------------------------------------------------------------ start

    private func begin(_ display: SCDisplay) {
        self.display = display
        guard let geometry = DisplayGeometry.of(display.displayID) else {
            AgentPipe.fail("Display \(display.displayID) has no current mode.")
        }
        self.geometry = geometry
        let size = outputSize()
        do { encoder = try makeEncoder(width: size.width, height: size.height) } catch { AgentPipe.fail("\(error)") }

        configuration.width = size.width
        configuration.height = size.height
        configuration.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(settings.fps))
        configuration.pixelFormat = kCVPixelFormatType_32BGRA
        configuration.colorSpaceName = CGColorSpace.sRGB
        configuration.showsCursor = false // the viewer draws the pointer itself, from the RFB cursor shape
        configuration.queueDepth = 5 // one picture is kept for key-frame requests on a still screen
        if #available(macOS 14.0, *) { configuration.captureResolution = .best }

        let stream = SCStream(filter: SCContentFilter(display: display, excludingWindows: []), configuration: configuration, delegate: self)
        do {
            try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: queue)
        } catch {
            AgentPipe.fail("Screen capture cannot start: \(error.localizedDescription)")
        }
        self.captureStream = stream
        stream.startCapture { error in
            self.queue.async {
                if let error { Capture.fail(error) }
                // Frames that came before this point were kept, not sent, so the agent hears about the stream first.
                self.started = true
                self.sendInfo()
                if let latest = self.latest { self.encode(latest, at: self.now(), keyframe: true) }
                self.watchDisplay()
            }
        }
    }

    private static func fail(_ error: Error?) -> Never {
        if !CGPreflightScreenCaptureAccess() {
            AgentPipe.fail("macOS has not allowed bettervnc-agent to record the screen. Allow it in System Settings > Privacy & Security > Screen Recording (Screen & System Audio Recording on macOS 15), then start the stream again.")
        }
        AgentPipe.fail("Screen capture failed: \(error?.localizedDescription ?? "unknown error")")
    }

    // ------------------------------------------------------------------ frames

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen, CMSampleBufferIsValid(sampleBuffer),
              let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
              let rawStatus = attachments.first?[.status] as? Int,
              SCFrameStatus(rawValue: rawStatus) == .complete, // .idle: nothing changed, nothing to send
              let pixels = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        latest = pixels
        guard started else { return }
        encode(pixels, at: CMSampleBufferGetPresentationTimeStamp(sampleBuffer), keyframe: keyframeWanted)
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        queue.async { Capture.fail(error) }
    }

    private func encode(_ pixels: CVPixelBuffer, at time: CMTime, keyframe: Bool) {
        let width = CVPixelBufferGetWidth(pixels), height = CVPixelBufferGetHeight(pixels)
        if encoder == nil || encoder?.width != width || encoder?.height != height {
            // A new size (display mode, or a new size asked for), or a session lost to sleep: a new encoder, which starts
            // with a key frame anyway.
            do { encoder = try makeEncoder(width: width, height: height) } catch { AgentPipe.fail("\(error)") }
            sendInfo()
        }
        var time = time
        if lastTime.isValid && CMTimeCompare(time, lastTime) <= 0 { time = CMTimeAdd(lastTime, CMTime(value: 1, timescale: 1_000_000)) }
        lastTime = time
        keyframeWanted = false
        if encoder?.encode(pixels, at: time, keyframe: keyframe) == false { encoder = nil }
    }

    /// The next frame becomes a key frame. A moving screen delivers one within a frame interval; a still one delivers
    /// nothing, so then the last picture is encoded again.
    private func requestKeyframe() {
        keyframeWanted = true
        let interval = 1.5 / Double(max(settings.fps, 1))
        queue.asyncAfter(deadline: .now() + interval) {
            guard self.keyframeWanted, self.started, let latest = self.latest else { return }
            self.encode(latest, at: self.now(), keyframe: true)
        }
    }

    private func makeEncoder(width: Int, height: Int) throws -> VideoEncoder {
        let codec = settings.codec, origin = self.origin
        return try VideoEncoder(codec: codec, width: width, height: height, bitrateKbps: settings.bitrateKbps,
                                fps: settings.fps, allowLowLatency: settings.lowLatency) { sample in
            // On VideoToolbox's thread; AgentPipe serialises the writes.
            guard let unit = AnnexB.accessUnit(from: sample, codec: codec) else { return }
            let seconds = CMTimeGetSeconds(CMTimeSubtract(CMSampleBufferGetPresentationTimeStamp(sample), origin))
            let micros = seconds.isFinite ? Int64(max(seconds, 0) * 1_000_000) : 0
            AgentPipe.send(.frame, keyframe: unit.keyframe, width: width, height: height, timestamp: micros, payload: unit.bytes)
        }
    }

    private func now() -> CMTime { CMClockGetTime(CMClockGetHostTimeClock()) }

    private func sendInfo() {
        guard let encoder, let display else { return }
        let kind = encoder.isHardware ? "hardware" : "software"
        let others = geometry.displayCount > 1 ? ", one of \(geometry.displayCount)" : ""
        let mode = encoder.lowLatency ? ", low-latency rate control" : ""
        let source = "ScreenCaptureKit, display \(display.displayID) (\(geometry.pixelWidth)x\(geometry.pixelHeight)\(others))"
        let description = "\(source) at \(encoder.width)x\(encoder.height), VideoToolbox \(encoder.codec.label) (\(kind)\(mode))"
        var fields: [(String, String)] = [
            ("width", "\(encoder.width)"),
            ("height", "\(encoder.height)"),
            ("encoder", kind),
            ("description", description),
        ]
        if geometry.displayCount > 1 {
            fields += [("offsetX", "\(geometry.offsetX)"), ("offsetY", "\(geometry.offsetY)")]
        }
        AgentPipe.info(fields)
    }

    // ------------------------------------------------------------------ size

    /// The display's own size, made smaller to fit the size asked for and the encoder's limit, keeping its shape.
    private func outputSize() -> (width: Int, height: Int) {
        let width = Double(geometry.pixelWidth), height = Double(geometry.pixelHeight)
        var scale = 1.0
        if geometry.displayCount > 1 {
            // One display of several is painted pixel for pixel onto its part of the viewer's picture, so it keeps its size.
            if geometry.pixelWidth > VideoEncoder.maxWidth || geometry.pixelHeight > VideoEncoder.maxHeight {
                let size = "\(geometry.pixelWidth)x\(geometry.pixelHeight)"
                AgentPipe.fail("The main display (\(size)) is too large to stream next to other displays; lower its resolution or disconnect the others.")
            }
        } else {
            if settings.maxWidth > 0 { scale = min(scale, Double(settings.maxWidth) / width) }
            if settings.maxHeight > 0 { scale = min(scale, Double(settings.maxHeight) / height) }
            scale = min(scale, Double(VideoEncoder.maxWidth) / width, Double(VideoEncoder.maxHeight) / height)
        }
        return (max(2, Int(width * scale) & ~1), max(2, Int(height * scale) & ~1))
    }

    private func applySize() {
        guard captureStream != nil else { return }
        let size = outputSize()
        guard size.width != configuration.width || size.height != configuration.height else { return }
        logLine("Capture size now \(size.width)x\(size.height)")
        configuration.width = size.width
        configuration.height = size.height
        reconfigure() // the encoder follows with the first picture of the new size
    }

    private func reconfigure() {
        captureStream?.updateConfiguration(configuration) { error in
            if let error { logLine("Capture settings not applied: \(error.localizedDescription)") }
        }
    }

    /// Resolution changes and displays coming or going: checked every two seconds.
    private func watchDisplay() {
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + 2, repeating: 2.0)
        timer.setEventHandler { [weak self] in
            guard let self, let display = self.display, let current = DisplayGeometry.of(display.displayID), current != self.geometry else { return }
            logLine("Display \(display.displayID) changed: \(current.pixelWidth)x\(current.pixelHeight), \(current.displayCount) display(s)")
            self.geometry = current
            self.applySize()
            self.sendInfo()
        }
        timer.resume()
        self.timer = timer
    }
}
