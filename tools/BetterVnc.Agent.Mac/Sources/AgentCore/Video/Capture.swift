import CoreGraphics
import CoreMedia
import CoreVideo
import Foundation
import ScreenCaptureKit

struct CaptureSettings {
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

/// What a running capture produces, sent when it starts and again whenever it changes.
struct CaptureInfo {
    var width: Int
    var height: Int
    var hardware: Bool
    var description: String
    /// Where the display sits in the desktop of all displays, when there is more than one.
    var offset: (x: Int, y: Int)?
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

    /// The display's own size, made smaller to fit the size asked for and the encoder's limit, keeping its shape. Nil when
    /// one display of several is too large to send pixel for pixel.
    func outputSize(maxWidth: Int, maxHeight: Int) -> (width: Int, height: Int)? {
        let width = Double(pixelWidth), height = Double(pixelHeight)
        var scale = 1.0
        if displayCount > 1 {
            // One display of several is painted pixel for pixel onto its part of the viewer's picture, so it keeps its size.
            if pixelWidth > VideoEncoder.maxWidth || pixelHeight > VideoEncoder.maxHeight { return nil }
        } else {
            if maxWidth > 0 { scale = min(scale, Double(maxWidth) / width) }
            if maxHeight > 0 { scale = min(scale, Double(maxHeight) / height) }
            scale = min(scale, Double(VideoEncoder.maxWidth) / width, Double(VideoEncoder.maxHeight) / height)
        }
        return (max(2, Int(width * scale) & ~1), max(2, Int(height * scale) & ~1))
    }
}

/// ScreenCaptureKit capture of one display into a `VideoEncoder`. ScreenCaptureKit scales the picture on the GPU to the
/// size asked for and delivers a frame only when something changed, at most `fps` times a second; a still screen costs
/// nothing. Everything here runs on `queue`; the callbacks are made under `deliveryLock`, and none comes after `stop()`.
final class Capture: NSObject, SCStreamOutput, SCStreamDelegate {
    private let queue = DispatchQueue(label: "bettervnc.capture", qos: .userInteractive)
    private let origin = CMClockGetTime(CMClockGetHostTimeClock())
    private var settings: CaptureSettings
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
    private let deliveryLock = NSLock()
    private var finished = false

    var onInfo: ((CaptureInfo) -> Void)?
    var onFrame: ((_ data: Data, _ keyframe: Bool, _ micros: Int64, _ width: Int, _ height: Int) -> Void)?
    /// The capture gave up; called once, never after `stop()`.
    var onFailed: ((String) -> Void)?
    var log: ((String) -> Void)?

    init(settings: CaptureSettings) {
        self.settings = settings
        super.init()
    }

    func start() {
        if !CGPreflightScreenCaptureAccess() {
            // Shows macOS's Screen Recording prompt (the first time only) and lists BetterVNC Agent.
            _ = CGRequestScreenCaptureAccess()
        }
        SCShareableContent.getExcludingDesktopWindows(false, onScreenWindowsOnly: true) { content, error in
            self.queue.async {
                guard let content else { return self.fail(error) }
                let wanted = self.settings.displayID ?? CGMainDisplayID()
                guard let display = content.displays.first(where: { $0.displayID == wanted }) ?? content.displays.first else {
                    return self.fail("This Mac has no display to capture.")
                }
                self.begin(display)
            }
        }
    }

    func setRates(bitrateKbps: Int?, fps: Int?) {
        queue.async {
            if let bitrateKbps { self.settings.bitrateKbps = max(bitrateKbps, 100) }
            if let fps { self.settings.fps = min(max(fps, 1), 120) }
            self.encoder?.setRates(bitrateKbps: self.settings.bitrateKbps, fps: self.settings.fps)
            if fps != nil {
                self.configuration.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(self.settings.fps))
                self.reconfigure()
            }
        }
    }

    func setMaxSize(width: Int, height: Int) {
        queue.async {
            self.settings.maxWidth = max(width, 0)
            self.settings.maxHeight = max(height, 0)
            self.applySize()
        }
    }

    /// The next frame becomes a key frame. A moving screen delivers one within a frame interval; a still one delivers
    /// nothing, so then the last picture is encoded again.
    func requestKeyframe() {
        queue.async {
            self.keyframeWanted = true
            let interval = 1.5 / Double(max(self.settings.fps, 1))
            self.queue.asyncAfter(deadline: .now() + interval) {
                guard self.keyframeWanted, self.started, let latest = self.latest else { return }
                self.encode(latest, at: self.now(), keyframe: true)
            }
        }
    }

    /// Stops capturing and encoding. No callback comes after this returns.
    func stop() {
        deliveryLock.lock()
        finished = true
        deliveryLock.unlock()
        queue.async { self.tearDown() }
    }

    // ------------------------------------------------------------------ start

    private func begin(_ display: SCDisplay) {
        guard !isFinished else { return }
        self.display = display
        guard let geometry = DisplayGeometry.of(display.displayID) else { return fail("Display \(display.displayID) has no current mode.") }
        self.geometry = geometry
        guard let size = outputSize() else { return }
        do { encoder = try makeEncoder(width: size.width, height: size.height) } catch { return fail("\(error)") }

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
            return fail("Screen capture cannot start: \(error.localizedDescription)")
        }
        captureStream = stream
        stream.startCapture { error in
            self.queue.async {
                if let error { return self.fail(error) }
                guard !self.isFinished else { return self.tearDown() }
                // Frames that came before this point were kept, not sent, so the agent hears about the stream first.
                self.started = true
                self.sendInfo()
                if let latest = self.latest { self.encode(latest, at: self.now(), keyframe: true) }
                self.watchDisplay()
            }
        }
    }

    private func fail(_ error: Error?) {
        if !CGPreflightScreenCaptureAccess() {
            return fail("macOS has not allowed the BetterVNC agent to record the screen. Turn on BetterVNC Agent in System Settings > Privacy & Security > Screen & System Audio Recording (Screen Recording before macOS 15), then pick the Stream mode again.")
        }
        fail("Screen capture failed: \(error?.localizedDescription ?? "unknown error")")
    }

    /// On `queue`: gives up, once.
    private func fail(_ message: String) {
        deliveryLock.lock()
        let wasFinished = finished
        finished = true
        let callback = onFailed
        deliveryLock.unlock()
        tearDown()
        if !wasFinished { callback?(message) }
    }

    private var isFinished: Bool {
        deliveryLock.lock(); defer { deliveryLock.unlock() }
        return finished
    }

    /// On `queue`.
    private func tearDown() {
        timer?.cancel()
        timer = nil
        captureStream?.stopCapture { _ in }
        captureStream = nil
        encoder = nil
        latest = nil
        started = false
    }

    // ------------------------------------------------------------------ frames

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen, CMSampleBufferIsValid(sampleBuffer),
              let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
              let rawStatus = attachments.first?[.status] as? Int,
              SCFrameStatus(rawValue: rawStatus) == .complete, // .idle: nothing changed, nothing to send
              let pixels = CMSampleBufferGetImageBuffer(sampleBuffer), !isFinished else { return }
        latest = pixels
        guard started else { return }
        encode(pixels, at: CMSampleBufferGetPresentationTimeStamp(sampleBuffer), keyframe: keyframeWanted)
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        queue.async { self.fail(error) }
    }

    private func encode(_ pixels: CVPixelBuffer, at time: CMTime, keyframe: Bool) {
        let width = CVPixelBufferGetWidth(pixels), height = CVPixelBufferGetHeight(pixels)
        if encoder == nil || encoder?.width != width || encoder?.height != height {
            // A new size (display mode, or a new size asked for), or a session lost to sleep: a new encoder, which starts
            // with a key frame anyway.
            do { encoder = try makeEncoder(width: width, height: height) } catch { return fail("\(error)") }
            sendInfo()
        }
        var time = time
        if lastTime.isValid && CMTimeCompare(time, lastTime) <= 0 { time = CMTimeAdd(lastTime, CMTime(value: 1, timescale: 1_000_000)) }
        lastTime = time
        keyframeWanted = false
        if encoder?.encode(pixels, at: time, keyframe: keyframe) == false { encoder = nil }
    }

    private func makeEncoder(width: Int, height: Int) throws -> VideoEncoder {
        let codec = settings.codec, origin = self.origin
        return try VideoEncoder(codec: codec, width: width, height: height, bitrateKbps: settings.bitrateKbps,
                                fps: settings.fps, allowLowLatency: settings.lowLatency, log: log) { [weak self] sample in
            // On VideoToolbox's thread.
            guard let self, let unit = AnnexB.accessUnit(from: sample, codec: codec) else { return }
            let seconds = CMTimeGetSeconds(CMTimeSubtract(CMSampleBufferGetPresentationTimeStamp(sample), origin))
            let micros = seconds.isFinite ? Int64(max(seconds, 0) * 1_000_000) : 0
            self.deliveryLock.lock()
            if !self.finished { self.onFrame?(Data(unit.bytes), unit.keyframe, micros, width, height) }
            self.deliveryLock.unlock()
        }
    }

    private func now() -> CMTime { CMClockGetTime(CMClockGetHostTimeClock()) }

    private func sendInfo() {
        guard let encoder, let display else { return }
        let kind = encoder.isHardware ? "hardware" : "software"
        let others = geometry.displayCount > 1 ? ", one of \(geometry.displayCount)" : ""
        let mode = encoder.lowLatency ? ", low-latency rate control" : ""
        let source = "ScreenCaptureKit, display \(display.displayID) (\(geometry.pixelWidth)x\(geometry.pixelHeight)\(others))"
        let info = CaptureInfo(
            width: encoder.width, height: encoder.height, hardware: encoder.isHardware,
            description: "\(source) at \(encoder.width)x\(encoder.height), VideoToolbox \(encoder.codec.label) (\(kind)\(mode))",
            offset: geometry.displayCount > 1 ? (geometry.offsetX, geometry.offsetY) : nil)
        deliveryLock.lock()
        if !finished { onInfo?(info) }
        deliveryLock.unlock()
    }

    // ------------------------------------------------------------------ size

    private func outputSize() -> (width: Int, height: Int)? {
        guard let size = geometry.outputSize(maxWidth: settings.maxWidth, maxHeight: settings.maxHeight) else {
            fail("The main display (\(geometry.pixelWidth)x\(geometry.pixelHeight)) is too large to stream next to other displays; lower its resolution or disconnect the others.")
            return nil
        }
        return size
    }

    private func applySize() {
        guard captureStream != nil, let size = outputSize() else { return }
        guard size.width != configuration.width || size.height != configuration.height else { return }
        log?("Capture size now \(size.width)x\(size.height)")
        configuration.width = size.width
        configuration.height = size.height
        reconfigure() // the encoder follows with the first picture of the new size
    }

    private func reconfigure() {
        captureStream?.updateConfiguration(configuration) { error in
            if let error { self.log?("Capture settings not applied: \(error.localizedDescription)") }
        }
    }

    /// Resolution changes and displays coming or going: checked every two seconds.
    private func watchDisplay() {
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + 2, repeating: 2.0)
        timer.setEventHandler { [weak self] in
            guard let self, let display = self.display, let current = DisplayGeometry.of(display.displayID), current != self.geometry else { return }
            self.log?("Display \(display.displayID) changed: \(current.pixelWidth)x\(current.pixelHeight), \(current.displayCount) display(s)")
            self.geometry = current
            self.applySize()
            self.sendInfo()
        }
        timer.resume()
        self.timer = timer
    }
}
