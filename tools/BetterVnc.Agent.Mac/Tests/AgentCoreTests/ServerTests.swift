import XCTest
@testable import AgentCore

/// The server end to end over TCP, with a small client that does what the viewer's AgentConnection does.
final class ServerTests: XCTestCase {
    var clipboard: FakeClipboard!
    var input: FakeInput!
    var video: FakeVideo!
    var approver: FakeApprover!
    var config: AgentConfig!
    var server: AgentServer!
    var logLines: [String] = []
    let logLock = NSLock()

    func startServer(token: String? = "test-token", allowPairing: Bool = true, staging: String? = nil) throws {
        clipboard = FakeClipboard()
        input = FakeInput()
        video = FakeVideo()
        approver = FakeApprover()
        config = AgentConfig()
        var options = AgentServerOptions()
        options.token = token
        options.findViewer = { [unowned self] in self.config.findViewer($0) }
        options.viewerPaired = { [unowned self] in self.config.addViewer($0) }
        options.pairingApprover = approver
        options.allowPairing = allowPairing
        options.listenAddress = "127.0.0.1"
        options.port = 0
        options.stagingDirectory = staging ?? temporaryDirectory().path
        options.appName = "BetterVNC agent test"
        server = try AgentServer(clipboard: clipboard, options: options, input: input, video: video)
        server.log = { [unowned self] line in self.logLock.lock(); self.logLines.append(line); self.logLock.unlock() }
        try server.start()
    }

    override func tearDown() {
        server?.stop()
        super.tearDown()
    }

    func testTokenHandshakeAnnouncesFeaturesCodecsAndFormats() throws {
        try startServer()
        clipboard.formatsNow = [AgentFormat.text]
        let client = try TestClient(port: server.port)
        XCTAssertEqual(client.hello.type, "hello")
        XCTAssertEqual(client.hello.version, 1)
        XCTAssertEqual(client.hello.os, "macos")
        XCTAssertEqual(client.hello.app, "BetterVNC agent test")
        let welcome = try client.authenticate(secret: "test-token")
        XCTAssertEqual(welcome.type, "welcome")
        XCTAssertEqual(welcome.features ?? [], ["scroll", "video"])
        XCTAssertEqual(welcome.codecs ?? [], ["h264"])
        XCTAssertEqual(welcome.formats ?? [], ["text"])
        let pong = try client.request(AgentMessage(type: "ping", id: 5))
        XCTAssertEqual(pong.header.type, "pong")
        XCTAssertEqual(pong.header.id, 5)
    }

    func testWrongTokenIsRefusedWithAuthFailed() throws {
        try startServer()
        let client = try TestClient(port: server.port)
        let reply = try client.authenticate(secret: "wrong")
        XCTAssertEqual(reply.type, "error")
        XCTAssertEqual(reply.code, AgentErrorCode.authFailed)
        XCTAssertEqual(reply.message, "Authentication failed: the token does not match.")
    }

    func testUnknownViewerWithoutTokenMustPair() throws {
        try startServer(token: nil)
        let client = try TestClient(port: server.port)
        let reply = try client.authenticate(secret: "whatever", viewerId: "unknown")
        XCTAssertEqual(reply.code, AgentErrorCode.notPaired)
        XCTAssertEqual(reply.message, "This viewer is not paired with the agent.")
    }

    func testPairingThenAuthenticatingWithTheDerivedKey() throws {
        try startServer(token: nil)
        approver.answer = true
        let client = try TestClient(port: server.port)
        let key = try client.pair(viewerId: "viewer-1", viewerName: "FRAMEWORK")
        XCTAssertEqual(approver.asked, ["FRAMEWORK"])
        XCTAssertEqual(config.findViewer("viewer-1")?.key, key)
        XCTAssertEqual(config.findViewer("viewer-1")?.address, "127.0.0.1")
        // The agent sent a new hello after "paired"; the viewer now proves the derived key.
        let welcome = try client.authenticate(secret: key, viewerId: "viewer-1")
        XCTAssertEqual(welcome.type, "welcome")
        XCTAssertNotNil(config.findViewer("viewer-1")?.lastSeen)

        // Next time it connects with the key directly.
        let again = try TestClient(port: server.port)
        XCTAssertEqual(try again.authenticate(secret: key, viewerId: "viewer-1").type, "welcome")
        let wrong = try TestClient(port: server.port)
        XCTAssertEqual(try wrong.authenticate(secret: "bad", viewerId: "viewer-1").message, "The paired key does not match; pair again.")
    }

    func testDeniedAndDisabledPairing() throws {
        try startServer(token: "t")
        approver.answer = false
        let client = try TestClient(port: server.port)
        XCTAssertThrowsError(try client.pair(viewerId: "v", viewerName: "X")) {
            XCTAssertEqual(($0 as? AgentProtocolError)?.description, AgentErrorCode.pairingDenied)
        }
        server.stop()
        try startServer(token: "t", allowPairing: false)
        let other = try TestClient(port: server.port)
        XCTAssertThrowsError(try other.pair(viewerId: "v", viewerName: "X")) {
            XCTAssertEqual(($0 as? AgentProtocolError)?.description, AgentErrorCode.pairingDisabled)
        }
    }

    func testClipboardGetSetAndChangeNotices() throws {
        try startServer()
        let client = try TestClient(port: server.port)
        _ = try client.authenticate(secret: "test-token")

        var set = AgentMessage(type: "setClipboard", id: 1)
        set.format = "text"
        XCTAssertEqual(try client.request(set, Data("from the viewer".utf8)).header.type, "ok")
        XCTAssertEqual(clipboard.stored["text"], Data("from the viewer".utf8))

        var get = AgentMessage(type: "getClipboard", id: 2)
        get.format = "text"
        let reply = try client.request(get)
        XCTAssertEqual(reply.header.type, "clipboardData")
        XCTAssertEqual(String(decoding: reply.payload, as: UTF8.self), "from the viewer")
        get.format = "png"
        XCTAssertEqual(try client.request(get).header.empty, true)
        get.format = "rtf"
        XCTAssertEqual(try client.request(get).header.message, "Unknown format 'rtf'.")

        // A change on the Mac is announced; our own write above was not.
        clipboard.simulateChange(formats: ["html"])
        let notice = try client.next(type: "clipboardChanged")
        XCTAssertEqual(notice.header.formats ?? [], ["html"])
    }

    func testFilesArePushedIntoStagingAndPulledAsABundle() throws {
        let staging = temporaryDirectory().path
        try startServer(staging: staging)
        let client = try TestClient(port: server.port)
        _ = try client.authenticate(secret: "test-token")
        var set = AgentMessage(type: "setClipboard", id: 1)
        set.format = "files"
        set.files = [AgentFileEntry(name: "dir/a.txt", size: 2), AgentFileEntry(name: "b.txt", size: 3)]
        XCTAssertEqual(try client.request(set, Data("aabbb".utf8)).header.type, "ok")
        XCTAssertEqual(clipboard.files.map { URL(fileURLWithPath: $0).lastPathComponent }, ["dir", "b.txt"])
        XCTAssertTrue(clipboard.files[0].hasPrefix(staging + "/"))

        var get = AgentMessage(type: "getClipboard", id: 2)
        get.format = "files"
        let reply = try client.request(get)
        XCTAssertEqual(Set(reply.header.files?.map(\.name) ?? []), ["dir/a.txt", "b.txt"])
        XCTAssertEqual(reply.payload.count, 5)
    }

    func testScrollReachesTheInput() throws {
        try startServer()
        let client = try TestClient(port: server.port)
        _ = try client.authenticate(secret: "test-token")
        var scroll = AgentMessage(type: "scroll")
        scroll.dx = 0
        scroll.dy = -48
        scroll.phase = "begin"
        try client.stream.send(scroll)
        _ = try client.request(AgentMessage(type: "ping", id: 9)) // processed in order
        XCTAssertEqual(input.calls.count, 1)
        XCTAssertEqual(input.calls.first?.dy, -48)
        XCTAssertEqual(input.calls.first?.phase, "begin")
    }

    func testVideoNegotiationFramesAndStop() throws {
        try startServer()
        let client = try TestClient(port: server.port)
        _ = try client.authenticate(secret: "test-token")

        var start = AgentMessage(type: "startVideo", id: 1)
        start.codecs = ["hevc"]
        XCTAssertTrue(try client.request(start).header.message?.hasPrefix("No common video codec") == true)

        start.codecs = ["H264", "jpeg"]
        start.bitrate = 50 // clamped to 250
        start.width = 1600
        start.height = 1000
        let started = try client.request(start).header
        XCTAssertEqual(started.type, "videoStarted")
        XCTAssertEqual(started.codec, "h264")
        XCTAssertEqual(started.bitrate, 250)
        XCTAssertEqual(started.maxFps, 60)
        XCTAssertEqual(started.width, 1600)
        XCTAssertEqual(started.encoder, "hardware")
        XCTAssertEqual(video.lastRequest?.maxWidth, 1600)

        video.emit(keyframe: true)
        video.emit(keyframe: false)
        let first = try client.next(type: "videoFrame"), second = try client.next(type: "videoFrame")
        XCTAssertEqual(first.header.sequence, 1)
        XCTAssertEqual(first.header.keyframe, true)
        XCTAssertEqual(second.header.sequence, 2)
        XCTAssertEqual(second.payload, Data([1, 2, 3]))

        var set = AgentMessage(type: "setVideo", id: 2)
        set.keyframeRequest = true
        set.width = 800
        XCTAssertEqual(try client.request(set).header.type, "ok")
        XCTAssertEqual(video.session?.keyframes, 1)
        XCTAssertEqual(video.session?.maxSize?.0, 800)

        XCTAssertEqual(try client.request(AgentMessage(type: "stopVideo", id: 3)).header.type, "ok")
        XCTAssertEqual(video.session?.stopped, true)
        XCTAssertEqual(try client.request(set).header.message, "No video stream is running.")
    }

    func testStreamEndingOnItsOwnTellsTheViewer() throws {
        try startServer()
        let client = try TestClient(port: server.port)
        _ = try client.authenticate(secret: "test-token")
        var start = AgentMessage(type: "startVideo", id: 1)
        start.codecs = ["h264"]
        _ = try client.request(start)
        video.end("display went to sleep")
        let stopped = try client.next(type: "videoStopped")
        XCTAssertEqual(stopped.header.message, "display went to sleep")
    }

    func testNothingCanAuthenticateIsRefused() {
        var options = AgentServerOptions()
        options.allowPairing = false
        XCTAssertThrowsError(try AgentServer(clipboard: FakeClipboard(), options: options))
    }

    func testShortNumbersLikeDotNet() {
        XCTAssertEqual(AgentServer.short(-48), "-48")
        XCTAssertEqual(AgentServer.short(1.5), "1.5")
        XCTAssertEqual(AgentServer.short(0.333), "0.33")
        XCTAssertEqual(AgentServer.short(-0.001), "0")
    }
}

// ---------------------------------------------------------------------- client and fakes

/// The viewer's side of the handshake, enough to drive the server.
final class TestClient {
    let stream: SocketStream
    var hello: AgentMessage
    private var nextId = 100

    init(port: Int) throws {
        stream = try SocketStream.connect(host: "127.0.0.1", port: port)
        stream.setReadTimeout(5)
        hello = try XCTUnwrap(try AgentFraming.read(stream, maxPayloadBytes: 1 << 20)).header
    }

    deinit { stream.close() }

    /// Answers the current hello; returns the welcome (checked) or the error.
    func authenticate(secret: String, viewerId: String? = nil) throws -> AgentMessage {
        let mine = AgentAuth.newNonce()
        var answer = AgentMessage(type: "hello")
        answer.version = 1
        answer.os = "windows"
        answer.app = "BetterVNC test"
        answer.nonce = mine
        answer.proof = AgentAuth.prove(token: secret, nonce: hello.nonce!)
        answer.viewerId = viewerId
        try stream.send(answer)
        let reply = try XCTUnwrap(try AgentFraming.read(stream, maxPayloadBytes: 1 << 20)).header
        if reply.type == "welcome" {
            XCTAssertTrue(AgentAuth.verify(token: secret, nonce: mine, proof: reply.proof), "the agent proves the same secret")
        }
        return reply
    }

    /// Pairs; returns the derived key, or throws a protocol error carrying the error code.
    func pair(viewerId: String, viewerName: String) throws -> String {
        let mine = AgentPairing.newKeyPair()
        var pair = AgentMessage(type: "pair")
        pair.version = 1
        pair.viewerId = viewerId
        pair.viewerName = viewerName
        pair.publicKey = AgentPairing.exportPublicKey(mine)
        try stream.send(pair)
        let reply = try XCTUnwrap(try AgentFraming.read(stream, maxPayloadBytes: 1 << 20)).header
        guard reply.type == "paired", let theirs = reply.publicKey else { throw AgentProtocolError(reply.code ?? reply.message ?? "?") }
        let key = try AgentPairing.deriveKey(mine, peerPublicKey: theirs)
        hello = try XCTUnwrap(try AgentFraming.read(stream, maxPayloadBytes: 1 << 20)).header
        XCTAssertEqual(hello.type, "hello")
        return key
    }

    /// Sends a request and returns the reply with the same id, skipping notices.
    func request(_ message: AgentMessage, _ payload: Data = Data()) throws -> AgentFrame {
        var m = message
        if m.id == nil { nextId += 1; m.id = nextId }
        try stream.send(m, payload)
        while true {
            let frame = try XCTUnwrap(try AgentFraming.read(stream, maxPayloadBytes: 1 << 26))
            if frame.header.id == m.id { return frame }
        }
    }

    func next(type: String) throws -> AgentFrame {
        while true {
            let frame = try XCTUnwrap(try AgentFraming.read(stream, maxPayloadBytes: 1 << 26))
            if frame.header.type == type { return frame }
        }
    }
}

final class FakeClipboard: GuestClipboard {
    let osName = "macos"
    let pollInterval: TimeInterval = 0.05
    private let lock = NSLock()
    private var count = 0
    var formatsNow: [String] = []
    var stored: [String: Data] = [:]
    var files: [String] = []

    func changeToken() -> String? { lock.lock(); defer { lock.unlock() }; return String(count) }
    func formats() -> [String] { lock.lock(); defer { lock.unlock() }; return formatsNow }
    func data(_ format: String) -> Data? { lock.lock(); defer { lock.unlock() }; return stored[format] }
    func filePaths() -> [String]? { lock.lock(); defer { lock.unlock() }; return files.isEmpty ? nil : files }

    func setData(_ format: String, _ data: Data) throws {
        lock.lock(); defer { lock.unlock() }
        stored[format] = data
        formatsNow = [format]
        count += 1
    }

    func setFiles(_ paths: [String]) throws {
        lock.lock(); defer { lock.unlock() }
        files = paths
        formatsNow = ["files"]
        count += 1
    }

    func simulateChange(formats: [String]) {
        lock.lock(); defer { lock.unlock() }
        formatsNow = formats
        count += 1
    }
}

final class FakeInput: GuestInput {
    private let lock = NSLock()
    private var _calls: [(dx: Double, dy: Double, phase: String?)] = []
    var calls: [(dx: Double, dy: Double, phase: String?)] { lock.lock(); defer { lock.unlock() }; return _calls }

    func scroll(dx: Double, dy: Double, wheelDx: Double?, wheelDy: Double?, phase: String?) throws {
        lock.lock(); defer { lock.unlock() }
        _calls.append((dx, dy, phase))
    }
}

final class FakeApprover: PairingApprover {
    var answer = true
    var asked: [String] = []
    func approve(viewerName: String, viewerId: String, address: String, timeout: TimeInterval) -> Bool {
        asked.append(viewerName)
        return answer
    }
}

final class FakeVideo: VideoSource {
    let codecs = ["h264"]
    var lastRequest: VideoRequest?
    var session: FakeVideoSession?
    private var onFrame: ((VideoFrame) -> Void)?
    private var onEnded: ((String?) -> Void)?

    func start(_ request: VideoRequest, onFrame: @escaping (VideoFrame) -> Void, onEnded: @escaping (String?) -> Void) throws -> VideoSession {
        lastRequest = request
        self.onFrame = onFrame
        self.onEnded = onEnded
        let s = FakeVideoSession(request: request)
        session = s
        return s
    }

    func emit(keyframe: Bool) {
        onFrame?(VideoFrame(data: Data([1, 2, 3]), keyframe: keyframe, timestampMicros: 10, width: 1600, height: 1000))
    }

    func end(_ reason: String) { onEnded?(reason) }
}

final class FakeVideoSession: VideoSession {
    let codec: String
    let bitrateKbps: Int
    let maxFps: Int
    let width: Int
    let height: Int
    let offset: (x: Int, y: Int)? = nil
    let encoderKind = "hardware"
    var keyframes = 0
    var maxSize: (Int, Int)?
    var stopped = false

    init(request: VideoRequest) {
        codec = request.codec
        bitrateKbps = request.bitrateKbps
        maxFps = request.maxFps
        width = request.maxWidth > 0 ? request.maxWidth : 1920
        height = request.maxHeight > 0 ? request.maxHeight : 1080
    }

    func set(bitrateKbps: Int?, maxFps: Int?) {}
    func setMaxSize(width: Int, height: Int) { maxSize = (width, height) }
    func requestKeyframe() { keyframes += 1 }
    func stop() { stopped = true }
}
