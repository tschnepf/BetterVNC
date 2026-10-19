import CryptoKit
import Foundation

public struct AgentServerOptions {
    /// Shared token or password; nil disables token authentication (paired viewers only).
    public var token: String?
    /// Looks up a paired viewer by id; nil disables paired-key authentication.
    public var findViewer: ((String) -> PairedViewer?)?
    /// Called when a pairing was approved so the viewer can be stored.
    public var viewerPaired: ((PairedViewer) -> Void)?
    /// Shows the approval dialog; nil (or allowPairing false) refuses new pairings.
    public var pairingApprover: PairingApprover?
    public var allowPairing = true
    public var pairingTimeout: TimeInterval = 90
    public var listenAddress = "0.0.0.0"
    /// 0 picks a free port; read `AgentServer.port` after `start()`.
    public var port = AgentConfig.defaultPort
    public var maxPayloadBytes = AgentFraming.defaultMaxPayloadBytes
    /// Where files pushed by a viewer are written before they go on the clipboard.
    public var stagingDirectory = (NSTemporaryDirectory() as NSString).appendingPathComponent("bettervnc-agent")
    public var appName = "BetterVNC agent"
    public var handshakeTimeout: TimeInterval = 10

    public init() {}
}

/// The agent's network side (AgentServer.cs): accepts viewers, authenticates them, serves clipboard requests, pushes
/// clipboardChanged, injects scrolling and streams the screen. Behaviour, error texts and log lines follow the C# server,
/// which the viewer's tests describe. Threads: one accepting, one watching the clipboard, one per session, one per stream.
public final class AgentServer {
    private let clipboard: GuestClipboard
    private let input: GuestInput?
    let video: VideoSource?
    let options: AgentServerOptions
    private let clipboardLock = NSLock()
    private let sessionsLock = NSLock()
    private var sessions: [Session] = []
    private var listener: TcpListener?
    private let stopSignal = DispatchSemaphore(value: 0)
    private var stopped = false
    /// The clipboard's change token last seen; read and written under clipboardLock, so our own writes are never announced.
    private var lastChangeToken: String?

    public private(set) var port = 0
    /// Log lines; any thread.
    public var log: ((String) -> Void)?
    /// A viewer authenticated: (viewer id, viewer name, peer address).
    public var viewerConnected: ((String?, String?, String) -> Void)?

    public init(clipboard: GuestClipboard, options: AgentServerOptions, input: GuestInput? = nil, video: VideoSource? = nil) throws {
        self.clipboard = clipboard
        self.input = input
        self.video = (video?.codecs.isEmpty ?? true) ? nil : video
        self.options = options
        if (options.token ?? "").trimmingCharacters(in: .whitespaces).isEmpty && options.findViewer == nil
            && (options.pairingApprover == nil || !options.allowPairing) {
            throw AgentProtocolError("Nothing could ever authenticate: set a token, a viewer lookup, or allow pairing.")
        }
    }

    var clipboardOsName: String { clipboard.osName }

    public var sessionCount: Int {
        sessionsLock.lock(); defer { sessionsLock.unlock() }
        return sessions.count
    }

    /// Capabilities announced in the welcome message.
    public var features: [String] {
        var list: [String] = []
        if input != nil { list.append(AgentFeature.scroll) }
        if video != nil { list.append(AgentFeature.video) }
        return list
    }

    /// Codecs announced in the welcome message; nil when this agent cannot stream.
    public var videoCodecs: [String]? { video?.codecs }

    public var streamingSessions: Int {
        sessionsLock.lock(); defer { sessionsLock.unlock() }
        return sessions.filter { $0.isStreaming }.count
    }

    /// Binds the listener and starts accepting and watching the clipboard. Throws when the address or port cannot be used.
    public func start() throws {
        precondition(listener == nil, "Already started.")
        let listener = try TcpListener(address: options.listenAddress, port: options.port)
        self.listener = listener
        port = listener.port
        lastChangeToken = safeChangeToken()
        Thread.detachNewThread { [self] in acceptLoop(listener) }
        Thread.detachNewThread { [self] in watchLoop() }
        log?("Listening on \(options.listenAddress):\(port) (\(clipboard.osName))")
    }

    public func stop() {
        sessionsLock.lock()
        if stopped { sessionsLock.unlock(); return }
        stopped = true
        let all = sessions
        sessionsLock.unlock()
        listener?.stop()
        stopSignal.signal()
        for s in all { s.close() }
    }

    // ------------------------------------------------------------------ accept

    private func acceptLoop(_ listener: TcpListener) {
        while let stream = listener.accept() {
            let session = Session(server: self, stream: stream)
            sessionsLock.lock()
            if stopped { sessionsLock.unlock(); stream.close(); return }
            sessions.append(session)
            sessionsLock.unlock()
            Thread.detachNewThread { [self] in
                session.run()
                sessionsLock.lock()
                sessions.removeAll { $0 === session }
                sessionsLock.unlock()
            }
        }
    }

    // ------------------------------------------------------------------ clipboard watch

    private func watchLoop() {
        while true {
            if stopSignal.wait(timeout: .now() + clipboard.pollInterval) == .success { return }
            clipboardLock.lock()
            let token = safeChangeToken()
            let changed = token != lastChangeToken
            lastChangeToken = token
            clipboardLock.unlock()
            // Only changes while a viewer is connected are announced.
            guard changed, token != nil, sessionCount > 0 else { continue }
            let notice = describeClipboard()
            guard let formats = notice.formats, !formats.isEmpty else { continue }
            log?("Clipboard changed: \(formats.joined(separator: ", "))")
            broadcast(notice)
        }
    }

    private func safeChangeToken() -> String? { clipboard.changeToken() }

    func describeClipboard() -> AgentMessage {
        clipboardLock.lock(); defer { clipboardLock.unlock() }
        var msg = AgentMessage(type: AgentMessageType.clipboardChanged)
        let formats = clipboard.formats()
        msg.formats = formats
        if formats.contains(AgentFormat.files), let paths = clipboard.filePaths() {
            msg.files = AgentServer.describeFiles(paths)
        }
        return msg
    }

    static func describeFiles(_ paths: [String]) -> [AgentFileEntry] {
        var list: [AgentFileEntry] = []
        let fm = FileManager.default
        for p in paths {
            var isDir: ObjCBool = false
            guard fm.fileExists(atPath: p, isDirectory: &isDir) else { continue }
            let name = URL(fileURLWithPath: p).lastPathComponent
            if isDir.boolValue {
                list.append(AgentFileEntry(name: name + "/", size: FileBundle.directorySize(p)))
            } else {
                let size = ((try? fm.attributesOfItem(atPath: p))?[.size] as? NSNumber)?.int64Value ?? 0
                list.append(AgentFileEntry(name: name, size: size))
            }
        }
        return list
    }

    private func broadcast(_ msg: AgentMessage) {
        sessionsLock.lock()
        let all = sessions
        sessionsLock.unlock()
        for s in all where s.authenticated {
            do { try s.stream.send(msg) } catch { log?("Notify failed: \(error)") }
        }
    }

    // ------------------------------------------------------------------ requests

    func handleGetClipboard(_ req: AgentMessage) -> AgentFrame {
        let format = req.format ?? ""
        guard AgentFormat.all.contains(format) else { return AgentFrame(.errorReply(req.id, "Unknown format '\(format)'.")) }
        clipboardLock.lock(); defer { clipboardLock.unlock() }
        var reply = AgentMessage(type: AgentMessageType.clipboardData, id: req.id)
        reply.format = format
        if format == AgentFormat.files {
            guard let paths = clipboard.filePaths(), !paths.isEmpty else { reply.empty = true; return AgentFrame(reply) }
            do {
                let (entries, payload) = try FileBundle.pack(paths, maxBytes: options.maxPayloadBytes)
                reply.files = entries
                return AgentFrame(reply, payload)
            } catch {
                return AgentFrame(.errorReply(req.id, "\(error)"))
            }
        }
        guard let data = clipboard.data(format) else { reply.empty = true; return AgentFrame(reply) }
        if data.count > options.maxPayloadBytes { return AgentFrame(.errorReply(req.id, "Clipboard content exceeds the transfer limit.")) }
        return AgentFrame(reply, data)
    }

    func handleSetClipboard(_ frame: AgentFrame) -> AgentFrame {
        let req = frame.header
        let format = req.format ?? ""
        guard AgentFormat.all.contains(format) else { return AgentFrame(.errorReply(req.id, "Unknown format '\(format)'.")) }
        clipboardLock.lock(); defer { clipboardLock.unlock() }
        do {
            if format == AgentFormat.files {
                let stamp = DateFormatter()
                stamp.locale = Locale(identifier: "en_US_POSIX")
                stamp.dateFormat = "yyyyMMdd-HHmmss-SSS"
                let dir = (options.stagingDirectory as NSString).appendingPathComponent(stamp.string(from: Date()))
                let paths = try FileBundle.unpack(req.files ?? [], frame.payload, into: dir)
                try clipboard.setFiles(paths)
                log?("Clipboard set: \(paths.count) item(s) in \(dir)")
            } else {
                try clipboard.setData(format, frame.payload)
                log?("Clipboard set: \(format), \(frame.payload.count) bytes")
            }
            // Remember the token our own write produced, so the watcher does not announce it.
            lastChangeToken = safeChangeToken()
            return AgentFrame(AgentMessage(type: AgentMessageType.ok, id: req.id))
        } catch {
            return AgentFrame(.errorReply(req.id, "\(error)"))
        }
    }

    func handleScroll(_ req: AgentMessage) {
        guard let input else { return } // not announced, so a well-behaved viewer never sends it
        let dx = req.dx ?? 0, dy = req.dy ?? 0
        guard dx.isFinite, dy.isFinite else { return }
        log?("Scroll: dx=\(AgentServer.short(dx)) dy=\(AgentServer.short(dy)) \(req.phase ?? "")")
        do { try input.scroll(dx: dx, dy: dy, wheelDx: req.wheelDx, wheelDy: req.wheelDy, phase: req.phase) }
        catch { log?("Scroll failed: \(error)") }
    }

    /// .NET's "0.##".
    static func short(_ value: Double) -> String {
        var text = String(format: "%.2f", value)
        while text.contains(".") && (text.hasSuffix("0") || text.hasSuffix(".")) { text.removeLast() }
        return text == "-0" ? "0" : text
    }
}

// ---------------------------------------------------------------------- session

final class Session {
    private unowned let server: AgentServer
    let stream: SocketStream
    private let stateLock = NSLock()
    private var _authenticated = false

    init(server: AgentServer, stream: SocketStream) {
        self.server = server
        self.stream = stream
    }

    var authenticated: Bool {
        stateLock.lock(); defer { stateLock.unlock() }
        return _authenticated
    }

    var peer: String { stream.peer }

    func close() { stream.shutdown() }

    private func log(_ line: String) { server.log?(line) }

    func run() {
        let opts = server.options
        defer {
            stopVideo()
            if authenticated { log("\(peer): disconnected") }
            stream.close()
        }
        do {
            // Handshake. A viewer may answer our hello with "pair" first; after an approved pairing we send a fresh hello
            // and the viewer authenticates with the key both sides just derived.
            var helloFrame: AgentFrame
            var secret: String
            while true {
                let nonce = AgentAuth.newNonce()
                var hello = AgentMessage(type: AgentMessageType.hello)
                hello.version = AgentMessage.protocolVersion
                hello.os = server.clipboardOsName
                hello.app = opts.appName
                hello.nonce = nonce
                try stream.send(hello)

                stream.setReadTimeout(opts.handshakeTimeout)
                guard let first = try AgentFraming.read(stream, maxPayloadBytes: 64 * 1024) else { return }
                stream.setReadTimeout(0)

                if first.header.type == AgentMessageType.pair {
                    if !(try pair(first.header)) { return }
                    continue // new hello; the viewer proves the derived key
                }
                guard first.header.type == AgentMessageType.hello, let theirNonce = first.header.nonce, !theirNonce.isEmpty else {
                    try stream.send(.errorReply(nil, "Expected hello."))
                    return
                }
                let result = authenticate(first.header, nonce: nonce)
                guard result.ok else {
                    log("\(peer): rejected (\(result.reason))")
                    Thread.sleep(forTimeInterval: 0.5) // slow down brute force a little
                    try stream.send(.errorReply(nil, result.reason, code: result.code))
                    return
                }
                helloFrame = first
                secret = result.secret
                break
            }

            stateLock.lock(); _authenticated = true; stateLock.unlock()
            var welcome = AgentMessage(type: AgentMessageType.welcome)
            welcome.version = AgentMessage.protocolVersion
            welcome.os = server.clipboardOsName
            welcome.app = opts.appName
            welcome.proof = AgentAuth.prove(token: secret, nonce: helloFrame.header.nonce!)
            welcome.features = server.features
            welcome.codecs = server.videoCodecs
            welcome.formats = server.describeClipboard().formats ?? []
            try stream.send(welcome)
            let h = helloFrame.header
            log("\(peer): connected (\(h.app ?? "unknown viewer") on \(h.os ?? "?")\(h.viewerName.map { ", \($0)" } ?? ""))")
            server.viewerConnected?(h.viewerId, h.viewerName, peer)

            while let frame = try AgentFraming.read(stream, maxPayloadBytes: opts.maxPayloadBytes) {
                if let reply = dispatch(frame) { try stream.send(reply.header, reply.payload) }
            }
        } catch let error as AgentProtocolError {
            log("\(peer): protocol error: \(error.description)")
            try? stream.send(.errorReply(nil, error.description))
        } catch is ConnectionClosed {
            // closed, reset or timed out: nothing to report
        } catch {
            log("\(peer): \(error)")
        }
    }

    // ---- authentication and pairing

    /// Checks the proof against the viewer's paired key (by viewer id) and then the token; returns the secret that matched.
    private func authenticate(_ hello: AgentMessage, nonce: String) -> (ok: Bool, secret: String, reason: String, code: String) {
        let opts = server.options
        var viewer: PairedViewer?
        if let id = hello.viewerId, !id.isEmpty, let find = opts.findViewer {
            viewer = find(id)
            if let viewer, AgentAuth.verify(token: viewer.key, nonce: nonce, proof: hello.proof) {
                viewer.lastSeen = Date()
                return (true, viewer.key, "", "")
            }
        }
        let token = opts.token ?? ""
        if !token.isEmpty && AgentAuth.verify(token: token, nonce: nonce, proof: hello.proof) {
            return (true, token, "", "")
        }
        let hasId = !(hello.viewerId ?? "").isEmpty
        if hasId && viewer == nil && token.isEmpty {
            return (false, "", "This viewer is not paired with the agent.", AgentErrorCode.notPaired)
        }
        if hasId && viewer == nil {
            return (false, "", "This viewer is not paired with the agent and the token does not match.", AgentErrorCode.notPaired)
        }
        return viewer != nil
            ? (false, "", "The paired key does not match; pair again.", AgentErrorCode.notPaired)
            : (false, "", "Authentication failed: the token does not match.", AgentErrorCode.authFailed)
    }

    /// Runs the approval dialog and answers paired or an error. False when the session should end.
    private func pair(_ req: AgentMessage) throws -> Bool {
        let opts = server.options
        guard let viewerId = req.viewerId, !viewerId.isEmpty, let publicKey = req.publicKey, !publicKey.isEmpty else {
            try stream.send(.errorReply(nil, "Pairing needs a viewer id and a public key."))
            return false
        }
        guard opts.allowPairing, let approver = opts.pairingApprover, let viewerPaired = opts.viewerPaired else {
            try stream.send(.errorReply(nil, "This agent does not accept new pairings; use its token instead.", code: AgentErrorCode.pairingDisabled))
            return false
        }
        let trimmed = (req.viewerName ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        let name = trimmed.isEmpty ? "a BetterVNC viewer" : trimmed
        log("\(peer): pairing requested by \(name)")
        let allowed = approver.approve(viewerName: name, viewerId: viewerId, address: stream.peerAddress, timeout: opts.pairingTimeout)
        guard allowed else {
            log("\(peer): pairing denied")
            try stream.send(.errorReply(nil, "The pairing was denied in the remote screen.", code: AgentErrorCode.pairingDenied))
            return false
        }
        let mine = AgentPairing.newKeyPair()
        let key: String
        do { key = try AgentPairing.deriveKey(mine, peerPublicKey: publicKey) }
        catch let error as AgentProtocolError {
            try stream.send(.errorReply(nil, error.description))
            return false
        }
        viewerPaired(PairedViewer(id: viewerId, name: name, key: key, pairedAt: Date(), address: stream.peerAddress))
        log("\(peer): paired with \(name)")
        var paired = AgentMessage(type: AgentMessageType.paired)
        paired.publicKey = AgentPairing.exportPublicKey(mine)
        try stream.send(paired)
        return true
    }

    // ---- dispatch

    private func dispatch(_ frame: AgentFrame) -> AgentFrame? {
        let h = frame.header
        switch h.type {
        case AgentMessageType.ping: return AgentFrame(AgentMessage(type: AgentMessageType.pong, id: h.id))
        case AgentMessageType.getClipboard: return server.handleGetClipboard(h)
        case AgentMessageType.setClipboard: return server.handleSetClipboard(frame)
        case AgentMessageType.scroll:
            server.handleScroll(h)
            return nil // fire and forget: a reply per wheel tick would only add latency
        case AgentMessageType.startVideo: return startVideo(h)
        case AgentMessageType.setVideo: return setVideo(h)
        case AgentMessageType.stopVideo:
            stopVideo()
            return AgentFrame(AgentMessage(type: AgentMessageType.ok, id: h.id))
        case AgentMessageType.clipboardChanged, AgentMessageType.pong: return nil // informational
        default: return AgentFrame(.errorReply(h.id, "Unknown message type '\(h.type)'."))
        }
    }

    // ---- video

    private let videoLock = NSLock()
    private var videoSession: VideoSession?
    private var frames: FrameQueue?
    private var frameSequence = 0
    private var framesDropped = 0

    var isStreaming: Bool {
        videoLock.lock(); defer { videoLock.unlock() }
        return videoSession != nil
    }

    /// One side of the viewer's picture area: 0 = no limit, at most 16384.
    static func pictureSide(_ pixels: Int?) -> Int { min(max(pixels ?? 0, 0), 16_384) }

    static func area(_ width: Int, _ height: Int) -> String {
        "\(width > 0 ? "\(width)" : "any")x\(height > 0 ? "\(height)" : "any")"
    }

    private func startVideo(_ req: AgentMessage) -> AgentFrame {
        guard let source = server.video else { return AgentFrame(.errorReply(req.id, "This agent cannot stream its screen.")) }
        // The viewer lists what it can decode, most preferred first; the first one this source produces wins.
        let wanted = (req.codecs?.isEmpty == false ? req.codecs! : nil) ?? req.codec.map { [$0] } ?? source.codecs
        guard let codec = wanted.first(where: { c in source.codecs.contains { $0.caseInsensitiveCompare(c) == .orderedSame } }) else {
            return AgentFrame(.errorReply(req.id, "No common video codec: the viewer decodes \(wanted.joined(separator: ", ")), this agent encodes \(source.codecs.joined(separator: ", "))."))
        }
        let request = VideoRequest(
            codec: codec.lowercased(),
            bitrateKbps: min(max(req.bitrate ?? 8_000, 250), 200_000),
            maxFps: min(max(req.maxFps ?? 60, 1), 120),
            maxWidth: Session.pictureSide(req.width),
            maxHeight: Session.pictureSide(req.height))

        videoLock.lock()
        defer { videoLock.unlock() }
        stopVideoLocked()
        weak var started: VideoSession?
        let queue = FrameQueue(capacity: 3) { [weak self] dropped in
            guard let self else { return }
            self.videoLock.lock(); self.framesDropped += 1; self.videoLock.unlock()
            // With a delta codec the frames after a dropped one cannot be decoded; the next one must be a key frame.
            if dropped.header.keyframe != true { started?.requestKeyframe() }
        }
        frames = queue
        frameSequence = 0
        framesDropped = 0
        let session: VideoSession
        do {
            session = try source.start(request, onFrame: { [weak self] frame in self?.queueFrame(queue, frame) },
                                       onEnded: { [weak self] reason in
                                           DispatchQueue.global().async { self?.videoEnded(queue, reason) }
                                       })
        } catch {
            frames = nil
            log("\(peer): video start failed: \(error)")
            return AgentFrame(.errorReply(req.id, "Cannot capture the screen: \(error)"))
        }
        videoSession = session
        started = session
        Thread.detachNewThread { [weak self] in self?.pumpFrames(queue) }
        let at = session.offset.map { " at \($0.x),\($0.y)" } ?? ""
        let area = request.maxWidth > 0 || request.maxHeight > 0 ? ", viewer area " + Session.area(request.maxWidth, request.maxHeight) : ""
        log("\(peer): video started (\(session.codec), \(session.bitrateKbps) kbit/s, \(session.maxFps) fps max, \(session.width)x\(session.height)\(at), \(session.encoderKind)\(area))")
        var reply = AgentMessage(type: AgentMessageType.videoStarted, id: req.id)
        reply.codec = session.codec
        reply.bitrate = session.bitrateKbps
        reply.maxFps = session.maxFps
        reply.width = session.width
        reply.height = session.height
        reply.offsetX = session.offset?.x
        reply.offsetY = session.offset?.y
        reply.encoder = session.encoderKind
        return AgentFrame(reply)
    }

    private func queueFrame(_ queue: FrameQueue, _ frame: VideoFrame) {
        var header = AgentMessage(type: AgentMessageType.videoFrame)
        header.keyframe = frame.keyframe
        header.timestamp = frame.timestampMicros
        header.width = frame.width
        header.height = frame.height
        header.offsetX = frame.offsetX
        header.offsetY = frame.offsetY
        videoLock.lock()
        frameSequence += 1
        header.sequence = frameSequence
        videoLock.unlock()
        queue.push(AgentFrame(header, frame.data))
    }

    private func pumpFrames(_ queue: FrameQueue) {
        while let frame = queue.pop() {
            do { try stream.send(frame.header, frame.payload) }
            catch { return } // the session is going away; run()'s cleanup stops the capture
        }
    }

    private func videoEnded(_ queue: FrameQueue, _ reason: String?) {
        videoLock.lock()
        guard frames === queue else { videoLock.unlock(); return } // a newer stream replaced this one
        stopVideoLocked()
        videoLock.unlock()
        log("\(peer): video ended: \(reason ?? "capture stopped")")
        var msg = AgentMessage(type: AgentMessageType.videoStopped)
        msg.message = reason
        try? stream.send(msg)
    }

    private func setVideo(_ req: AgentMessage) -> AgentFrame {
        videoLock.lock()
        defer { videoLock.unlock() }
        guard let session = videoSession else { return AgentFrame(.errorReply(req.id, "No video stream is running.")) }
        if req.bitrate != nil || req.maxFps != nil {
            session.set(bitrateKbps: req.bitrate.map { min(max($0, 250), 200_000) }, maxFps: req.maxFps.map { min(max($0, 1), 120) })
        }
        if req.width != nil || req.height != nil {
            let width = Session.pictureSide(req.width), height = Session.pictureSide(req.height)
            session.setMaxSize(width: width, height: height)
            log("\(peer): viewer area now \(Session.area(width, height))")
        }
        if req.keyframeRequest == true { session.requestKeyframe() }
        var reply = AgentMessage(type: AgentMessageType.ok, id: req.id)
        reply.bitrate = session.bitrateKbps
        reply.maxFps = session.maxFps
        return AgentFrame(reply)
    }

    private func stopVideo() {
        videoLock.lock()
        let wasRunning = videoSession != nil
        stopVideoLocked()
        let dropped = framesDropped
        videoLock.unlock()
        if wasRunning { log("\(peer): video stopped" + (dropped > 0 ? " (\(dropped) frame(s) dropped on a slow link)" : "")) }
    }

    /// Caller holds videoLock.
    private func stopVideoLocked() {
        let session = videoSession
        videoSession = nil
        session?.stop()
        frames?.close()
        frames = nil
    }
}

/// A bounded queue of frames for one stream: when full, the oldest goes (a slow link sees fewer frames, never older ones).
final class FrameQueue {
    private let capacity: Int
    private let onDrop: (AgentFrame) -> Void
    private let condition = NSCondition()
    private var items: [AgentFrame] = []
    private var closed = false

    init(capacity: Int, onDrop: @escaping (AgentFrame) -> Void) {
        self.capacity = capacity
        self.onDrop = onDrop
    }

    func push(_ frame: AgentFrame) {
        condition.lock()
        if closed { condition.unlock(); return }
        var dropped: AgentFrame?
        if items.count >= capacity { dropped = items.removeFirst() }
        items.append(frame)
        condition.signal()
        condition.unlock()
        if let dropped { onDrop(dropped) }
    }

    /// The next frame; nil once closed.
    func pop() -> AgentFrame? {
        condition.lock()
        defer { condition.unlock() }
        while items.isEmpty && !closed { condition.wait() }
        if closed { return nil }
        return items.removeFirst()
    }

    func close() {
        condition.lock()
        closed = true
        items.removeAll()
        condition.broadcast()
        condition.unlock()
    }
}
