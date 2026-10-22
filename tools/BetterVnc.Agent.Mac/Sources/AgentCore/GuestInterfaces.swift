import Foundation

/// The guest clipboard (IGuestClipboard.cs). Calls may block; the server serialises them and makes them off the main thread.
public protocol GuestClipboard: AnyObject {
    /// "macos", sent in hello and welcome.
    var osName: String { get }
    /// How often the server polls `changeToken()`.
    var pollInterval: TimeInterval { get }
    /// Changes whenever the clipboard changes; nil when it cannot be read.
    func changeToken() -> String?
    /// The formats on the clipboard now, in the order text, html, png, files.
    func formats() -> [String]
    /// The content in one format (not files); nil when it is not there.
    func data(_ format: String) -> Data?
    /// The files on the clipboard; nil when there are none.
    func filePaths() -> [String]?
    func setData(_ format: String, _ data: Data) throws
    func setFiles(_ paths: [String]) throws
}

/// Scroll injection (IGuestInput.cs). Pixels; positive dx scrolls right, positive dy scrolls up.
public protocol GuestInput: AnyObject {
    func scroll(dx: Double, dy: Double, wheelDx: Double?, wheelDy: Double?, phase: String?) throws
}

/// What a viewer asked for in startVideo, after the server picked a codec.
public struct VideoRequest {
    public var codec: String
    public var bitrateKbps = 8_000
    public var maxFps = 60
    /// The viewer's picture area in device pixels; 0 = no limit in that direction.
    public var maxWidth = 0
    public var maxHeight = 0

    public init(codec: String, bitrateKbps: Int = 8_000, maxFps: Int = 60, maxWidth: Int = 0, maxHeight: Int = 0) {
        self.codec = codec
        self.bitrateKbps = bitrateKbps
        self.maxFps = maxFps
        self.maxWidth = maxWidth
        self.maxHeight = maxHeight
    }
}

/// One encoded frame.
public struct VideoFrame {
    public var data: Data
    public var keyframe: Bool
    /// Capture time in microseconds since the stream started.
    public var timestampMicros: Int64
    public var width: Int
    public var height: Int
    /// Where the frame sits in the desktop when it shows only part of it; nil for the whole desktop.
    public var offsetX: Int?
    public var offsetY: Int?

    public init(data: Data, keyframe: Bool, timestampMicros: Int64, width: Int, height: Int, offsetX: Int? = nil, offsetY: Int? = nil) {
        self.data = data
        self.keyframe = keyframe
        self.timestampMicros = timestampMicros
        self.width = width
        self.height = height
        self.offsetX = offsetX
        self.offsetY = offsetY
    }
}

/// Screen capture plus encoder (IGuestVideoSource.cs). One source serves any number of sessions.
public protocol VideoSource: AnyObject {
    /// Codecs it can produce, most preferred first.
    var codecs: [String] { get }
    /// Starts capturing and encoding. `onFrame` is called on a capture thread and must return quickly; `onEnded` at most
    /// once, when the stream dies on its own, never after `stop()`. Throws when capture is impossible right now; the
    /// message goes to the viewer.
    func start(_ request: VideoRequest, onFrame: @escaping (VideoFrame) -> Void, onEnded: @escaping (String?) -> Void) throws -> VideoSession
}

/// A running stream. After `stop()` returns no frame arrives any more.
public protocol VideoSession: AnyObject {
    var codec: String { get }
    var bitrateKbps: Int { get }
    var maxFps: Int { get }
    var width: Int { get }
    var height: Int { get }
    var offset: (x: Int, y: Int)? { get }
    var encoderKind: String { get }
    func set(bitrateKbps: Int?, maxFps: Int?)
    func setMaxSize(width: Int, height: Int)
    func requestKeyframe()
    func stop()
}

/// Asks the person at the screen whether a viewer may pair. Returns false (never throws) when denied or not answered.
public protocol PairingApprover: AnyObject {
    func approve(viewerName: String, viewerId: String, address: String, timeout: TimeInterval) -> Bool
}
