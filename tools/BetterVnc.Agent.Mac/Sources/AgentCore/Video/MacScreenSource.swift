import CoreGraphics
import Foundation

/// Screen streaming on macOS: ScreenCaptureKit and VideoToolbox in this process (the C# agent ran a helper for each
/// stream). Offers H.264, and HEVC when this Mac encodes it. The capture runs as BetterVNC Agent, so the Screen
/// Recording permission is the app's own.
public final class MacScreenSource: VideoSource {
    public let codecs: [String]
    private let h264Kind: String
    private let hevcKind: String
    public var log: ((String) -> Void)?
    /// Called when a stream cannot start because Screen Recording is not allowed (opens the permission wizard).
    public var permissionMissing: (() -> Void)?

    /// Checks which encoders this Mac has. Throws when it has none.
    public init() throws {
        h264Kind = MacScreenSource.probeEncoder(.h264)
        hevcKind = MacScreenSource.probeEncoder(.hevc)
        var codecs: [String] = []
        if h264Kind != "none" { codecs.append(AgentVideoCodec.h264) }
        if hevcKind != "none" { codecs.append(AgentVideoCodec.hevc) }
        if codecs.isEmpty { throw VideoError("VideoToolbox offers no H.264 or HEVC encoder on this Mac.") }
        self.codecs = codecs
    }

    /// Printed at startup as "Screen streaming: …".
    public var description: String {
        var parts: [String] = []
        if h264Kind != "none" { parts.append("H.264 (\(h264Kind))") }
        if hevcKind != "none" { parts.append("HEVC (\(hevcKind))") }
        var text = "ScreenCaptureKit capture, VideoToolbox " + parts.joined(separator: " and ")
        if !Permissions.screenRecording() {
            text += "; Screen Recording permission not granted yet (macOS asks when the first stream starts)"
        }
        return text
    }

    static func probeEncoder(_ codec: Codec) -> String {
        guard let encoder = try? VideoEncoder(codec: codec, width: 1920, height: 1080, bitrateKbps: 8_000, fps: 60,
                                              allowLowLatency: false, output: { _ in }) else { return "none" }
        return encoder.isHardware ? "hardware" : "software"
    }

    public func start(_ request: VideoRequest, onFrame: @escaping (VideoFrame) -> Void, onEnded: @escaping (String?) -> Void) throws -> VideoSession {
        guard let codec = Codec(rawValue: request.codec.lowercased()), codecs.contains(codec.rawValue) else {
            throw VideoError("Codec '\(request.codec)' is not available; this agent encodes \(codecs.joined(separator: " and ")).")
        }
        let session = MacVideoSession(codec: codec, request: request, log: log)
        do {
            try session.start(onFrame: onFrame, onEnded: onEnded)
        } catch {
            if let permissionMissing, !Permissions.screenRecording() { permissionMissing() }
            throw error
        }
        log?("\(codec.label) stream: \(session.description)")
        return session
    }
}

/// One running stream: a `Capture` and what it last said about itself.
final class MacVideoSession: VideoSession {
    let codec: String
    private let capture: Capture
    private let lock = NSLock()
    private var info: CaptureInfo?
    private var bitrate: Int
    private var fps: Int

    init(codec: Codec, request: VideoRequest, log: ((String) -> Void)?) {
        self.codec = codec.rawValue
        bitrate = request.bitrateKbps
        fps = min(max(request.maxFps, 1), 120)
        capture = Capture(settings: CaptureSettings(codec: codec, bitrateKbps: request.bitrateKbps, fps: fps,
                                                    maxWidth: request.maxWidth, maxHeight: request.maxHeight))
        capture.log = { log?("Screen capture: \($0)") }
    }

    /// Starts the capture and waits (up to ten seconds) until it runs, or throws why it does not.
    func start(onFrame: @escaping (VideoFrame) -> Void, onEnded: @escaping (String?) -> Void) throws {
        let ready = DispatchSemaphore(value: 0)
        var startError: String?
        var starting = true
        capture.onInfo = { [weak self] info in
            guard let self else { return }
            self.lock.lock()
            let first = self.info == nil
            self.info = info
            self.lock.unlock()
            if first { ready.signal() }
        }
        capture.onFrame = { [weak self] data, keyframe, micros, width, height in
            guard let self else { return }
            let offset = self.offset
            onFrame(VideoFrame(data: data, keyframe: keyframe, timestampMicros: micros, width: width, height: height,
                               offsetX: offset?.x, offsetY: offset?.y))
        }
        capture.onFailed = { [weak self] reason in
            guard let self else { return }
            self.lock.lock()
            let duringStart = starting
            if duringStart { startError = reason }
            self.lock.unlock()
            if duringStart { ready.signal() } else { onEnded(reason) }
        }
        capture.start()
        let timedOut = ready.wait(timeout: .now() + 10) == .timedOut
        lock.lock()
        starting = false
        let failure = startError
        lock.unlock()
        if timedOut {
            capture.stop()
            throw VideoError("The screen capture did not start within 10 s.")
        }
        if let failure { throw VideoError(failure) }
    }

    private var current: CaptureInfo? {
        lock.lock(); defer { lock.unlock() }
        return info
    }

    var description: String { current?.description ?? "" }
    var bitrateKbps: Int { lock.lock(); defer { lock.unlock() }; return bitrate }
    var maxFps: Int { lock.lock(); defer { lock.unlock() }; return fps }
    var width: Int { current?.width ?? 0 }
    var height: Int { current?.height ?? 0 }
    var offset: (x: Int, y: Int)? { current?.offset }
    var encoderKind: String { current?.hardware == false ? AgentVideoEncoder.software : AgentVideoEncoder.hardware }

    func set(bitrateKbps: Int?, maxFps: Int?) {
        lock.lock()
        if let bitrateKbps { bitrate = bitrateKbps }
        if let maxFps { fps = min(max(maxFps, 1), 120) }
        let fpsNow = fps
        lock.unlock()
        capture.setRates(bitrateKbps: bitrateKbps, fps: maxFps == nil ? nil : fpsNow)
    }

    func setMaxSize(width: Int, height: Int) { capture.setMaxSize(width: width, height: height) }

    func requestKeyframe() { capture.requestKeyframe() }

    func stop() { capture.stop() }
}
