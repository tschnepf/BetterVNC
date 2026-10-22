import CryptoKit
import XCTest
@testable import AgentCore

/// The wire format, authentication and pairing must match src/BetterVnc.Agent.Protocol byte for byte.
final class ProtocolTests: XCTestCase {
    func testHeaderIsCamelCaseJsonWithoutNulls() throws {
        var m = AgentMessage(type: AgentMessageType.videoStarted, id: 7)
        m.maxFps = 60
        m.offsetX = 0
        m.viewerId = "abc"
        let json = String(decoding: AgentFraming.encodeHeader(m), as: UTF8.self)
        XCTAssertTrue(json.contains("\"type\":\"videoStarted\""))
        XCTAssertTrue(json.contains("\"maxFps\":60"))
        XCTAssertTrue(json.contains("\"offsetX\":0"))
        XCTAssertTrue(json.contains("\"viewerId\":\"abc\""))
        XCTAssertFalse(json.contains("null"))
        XCTAssertFalse(json.contains("bitrate"))
    }

    func testDecodesWhatTheCSharpViewerSends() throws {
        let json = #"{"type":"startVideo","id":3,"codecs":["h264","jpeg"],"bitrate":8000,"maxFps":60,"width":1600,"height":1000,"unknownField":true}"#
        let m = try AgentFraming.decodeHeader(Data(json.utf8))
        XCTAssertEqual(m.type, "startVideo")
        XCTAssertEqual(m.id, 3)
        XCTAssertEqual(m.codecs ?? [], ["h264", "jpeg"])
        XCTAssertEqual(m.width, 1600)
        let scroll = try AgentFraming.decodeHeader(Data(#"{"type":"scroll","dx":-1.5,"dy":120,"phase":"begin","wheelDy":120}"#.utf8))
        XCTAssertEqual(scroll.dx, -1.5)
        XCTAssertEqual(scroll.dy, 120)
        XCTAssertThrowsError(try AgentFraming.decodeHeader(Data("{nope".utf8)))
        // What today's viewer adds when the agent announces the cursor and repairs (this one does not).
        let flags = try AgentFraming.decodeHeader(Data(#"{"type":"startVideo","id":4,"codecs":["h264"],"cursor":true,"repair":true}"#.utf8))
        XCTAssertEqual(flags.cursor, true)
        XCTAssertEqual(flags.repair, true)
    }

    func testFrameLayoutIsTwoBigEndianLengthsThenHeaderThenPayload() throws {
        let frame = AgentFraming.encode(AgentMessage(type: "ping", id: 1), Data([9, 8, 7]))
        let header = AgentFraming.encodeHeader(AgentMessage(type: "ping", id: 1))
        XCTAssertEqual(Array(frame.prefix(8)), [0, 0, 0, UInt8(header.count), 0, 0, 0, 3])
        XCTAssertEqual(frame.subdata(in: 8..<(8 + header.count)), header)
        XCTAssertEqual(Array(frame.suffix(3)), [9, 8, 7])
    }

    func testFramesRoundTripOverASocket() throws {
        let (a, b) = try socketPair()
        try a.send(AgentMessage(type: "getClipboard", id: 2), Data("hello".utf8))
        let frame = try XCTUnwrap(try AgentFraming.read(b, maxPayloadBytes: 1024))
        XCTAssertEqual(frame.header.type, "getClipboard")
        XCTAssertEqual(frame.header.id, 2)
        XCTAssertEqual(String(decoding: frame.payload, as: UTF8.self), "hello")
        try a.write(AgentFraming.encode(AgentMessage(type: "x"), Data(count: 2048)))
        XCTAssertThrowsError(try AgentFraming.read(b, maxPayloadBytes: 1024)) // over the limit
        a.close()
        b.close()
    }

    func testCleanEndOfStreamIsNil() throws {
        let (a, b) = try socketPair()
        a.close()
        XCTAssertNil(try AgentFraming.read(b, maxPayloadBytes: 1024))
        b.close()
    }

    // ---- authentication

    func testProofIsHmacSha256OfTheNonceTextKeyedWithTheTrimmedToken() {
        // Python: base64(hmac.new(b"my-token", b"bm9uY2U=", sha256).digest())
        let expected = "t8BBzKvkn52nmHEHjxE49hIJ2mXjbC3K3kBnGCSSuZg="
        XCTAssertEqual(AgentAuth.prove(token: "my-token", nonce: "bm9uY2U="), expected)
        XCTAssertEqual(AgentAuth.prove(token: "  my-token\n", nonce: "bm9uY2U="), expected)
        XCTAssertTrue(AgentAuth.verify(token: "my-token", nonce: "bm9uY2U=", proof: expected))
        XCTAssertFalse(AgentAuth.verify(token: "other", nonce: "bm9uY2U=", proof: expected))
        XCTAssertFalse(AgentAuth.verify(token: "my-token", nonce: "bm9uY2U=", proof: "not base64!"))
        XCTAssertFalse(AgentAuth.verify(token: "my-token", nonce: "bm9uY2U=", proof: nil))
    }

    func testTokensAre32Base64UrlCharacters() {
        let token = AgentAuth.newToken()
        XCTAssertEqual(token.count, 32)
        XCTAssertTrue(token.allSatisfy { $0.isLetter || $0.isNumber || $0 == "-" || $0 == "_" })
        XCTAssertNotEqual(token, AgentAuth.newToken())
        XCTAssertEqual(Data(base64Encoded: AgentAuth.newNonce())?.count, 32)
    }

    // ---- pairing

    func testBothSidesDeriveTheSameKeyFromSpkiPublicKeys() throws {
        let viewer = AgentPairing.newKeyPair(), agent = AgentPairing.newKeyPair()
        let viewerPublic = AgentPairing.exportPublicKey(viewer)
        let spki = try XCTUnwrap(Data(base64Encoded: viewerPublic))
        XCTAssertEqual(spki.count, 91) // SubjectPublicKeyInfo with an uncompressed P-256 point, as .NET exports it
        XCTAssertEqual(spki.first, 0x30)
        let a = try AgentPairing.deriveKey(agent, peerPublicKey: viewerPublic)
        let b = try AgentPairing.deriveKey(viewer, peerPublicKey: AgentPairing.exportPublicKey(agent))
        XCTAssertEqual(a, b)
        XCTAssertEqual(Data(base64Encoded: a)?.count, 32)
    }

    func testDerivedKeyIsSha256OfSharedSecretAndLabel() throws {
        let viewer = AgentPairing.newKeyPair(), agent = AgentPairing.newKeyPair()
        let shared = try agent.sharedSecretFromKeyAgreement(with: viewer.publicKey)
        var raw = shared.withUnsafeBytes { Data($0) }
        raw.append(Data("bettervnc-agent-pairing-v1".utf8))
        let expected = Data(SHA256.hash(data: raw)).base64EncodedString()
        XCTAssertEqual(try AgentPairing.deriveKey(agent, peerPublicKey: AgentPairing.exportPublicKey(viewer)), expected)
    }

    func testBadPublicKeysAreProtocolErrors() {
        let mine = AgentPairing.newKeyPair()
        XCTAssertThrowsError(try AgentPairing.deriveKey(mine, peerPublicKey: "***")) { XCTAssertTrue($0 is AgentProtocolError) }
        XCTAssertThrowsError(try AgentPairing.deriveKey(mine, peerPublicKey: Data([1, 2, 3]).base64EncodedString())) { XCTAssertTrue($0 is AgentProtocolError) }
    }

    // ---- file bundles

    func testFileBundleRoundTripsFilesAndFolders() throws {
        let root = temporaryDirectory()
        let src = root.appendingPathComponent("src")
        try FileManager.default.createDirectory(at: src.appendingPathComponent("folder/sub"), withIntermediateDirectories: true)
        try Data("one".utf8).write(to: src.appendingPathComponent("a.txt"))
        try Data("two".utf8).write(to: src.appendingPathComponent("folder/b.txt"))
        try Data("three".utf8).write(to: src.appendingPathComponent("folder/sub/c.txt"))

        let (entries, payload) = try FileBundle.pack([src.appendingPathComponent("a.txt").path, src.appendingPathComponent("folder").path])
        XCTAssertEqual(Set(entries.map(\.name)), ["a.txt", "folder/b.txt", "folder/sub/c.txt"])
        XCTAssertEqual(payload.count, 11)

        let dest = root.appendingPathComponent("dest").path
        let top = try FileBundle.unpack(entries, payload, into: dest)
        XCTAssertEqual(Set(top.map { URL(fileURLWithPath: $0).lastPathComponent }), ["a.txt", "folder"])
        XCTAssertEqual(try String(contentsOfFile: dest + "/folder/sub/c.txt"), "three")
        XCTAssertEqual(FileBundle.directorySize(dest + "/folder"), 8)
    }

    func testFileBundleRejectsEscapes() {
        for bad in ["../x", "/etc/passwd", "a/../../b", "c:/x", "a\u{1}b", ""] {
            XCTAssertThrowsError(try FileBundle.sanitize(bad), bad)
        }
        XCTAssertEqual(try FileBundle.sanitize("a\\b//c"), "a/b/c")
        XCTAssertThrowsError(try FileBundle.unpack([AgentFileEntry(name: "a", size: 10)], Data(count: 3), into: temporaryDirectory().path))
        XCTAssertThrowsError(try FileBundle.pack(["/no/such/file"]))
    }
}

func socketPair() throws -> (SocketStream, SocketStream) {
    var fds: [Int32] = [0, 0]
    guard socketpair(AF_UNIX, SOCK_STREAM, 0, &fds) == 0 else { throw ConnectionClosed("socketpair") }
    return (SocketStream(fd: fds[0]), SocketStream(fd: fds[1]))
}

func temporaryDirectory() -> URL {
    let url = FileManager.default.temporaryDirectory.appendingPathComponent("bettervnc-tests-" + UUID().uuidString)
    try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    return url
}
