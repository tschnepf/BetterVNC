import Foundation

/// A decoded frame: JSON header plus optional binary payload.
public struct AgentFrame {
    public var header: AgentMessage
    public var payload: Data

    public init(_ header: AgentMessage, _ payload: Data = Data()) {
        self.header = header
        self.payload = payload
    }
}

/// The peer broke the protocol (bad lengths, bad JSON, a file bundle that does not add up). The message goes to the log
/// and, when possible, to the peer.
public struct AgentProtocolError: Error, CustomStringConvertible {
    public let description: String
    public init(_ description: String) { self.description = description }
}

/// Wire format of one frame, as in AgentFraming.cs:
///
///     u32 BE headerLength | u32 BE payloadLength | header (UTF-8 JSON) | payload
///
/// Both lengths are bounded so a bad peer cannot make us allocate arbitrarily.
public enum AgentFraming {
    public static let maxHeaderBytes = 4 * 1024 * 1024
    public static let defaultMaxPayloadBytes = 256 * 1024 * 1024

    private static let encoder: JSONEncoder = {
        let e = JSONEncoder()
        e.outputFormatting = [.withoutEscapingSlashes]
        return e
    }()

    public static func encodeHeader(_ header: AgentMessage) -> Data {
        // Every field is a string, number, bool or array of those; encoding cannot fail.
        (try? encoder.encode(header)) ?? Data("{\"type\":\"error\"}".utf8)
    }

    public static func decodeHeader(_ json: Data) throws -> AgentMessage {
        do { return try JSONDecoder().decode(AgentMessage.self, from: json) }
        catch { throw AgentProtocolError("Malformed frame header: \(error)") }
    }

    /// The bytes of one frame, ready to be written.
    public static func encode(_ header: AgentMessage, _ payload: Data = Data()) -> Data {
        let json = encodeHeader(header)
        var out = Data(capacity: 8 + json.count + payload.count)
        appendUInt32(&out, UInt32(json.count))
        appendUInt32(&out, UInt32(payload.count))
        out.append(json)
        out.append(payload)
        return out
    }

    /// Reads one frame; nil at a clean end of stream before any byte of a frame.
    public static func read(_ stream: SocketStream, maxPayloadBytes: Int) throws -> AgentFrame? {
        guard let prefix = try stream.readExactly(8, allowEndOfStream: true) else { return nil }
        let headerLength = Int(readUInt32(prefix, 0)), payloadLength = Int(readUInt32(prefix, 4))
        if headerLength == 0 || headerLength > maxHeaderBytes {
            throw AgentProtocolError("Frame header length \(headerLength) is out of range.")
        }
        if payloadLength > maxPayloadBytes {
            throw AgentProtocolError("Frame payload of \(payloadLength) bytes exceeds the limit of \(maxPayloadBytes) bytes.")
        }
        let json = try stream.readExactly(headerLength, allowEndOfStream: false)!
        let payload = payloadLength == 0 ? Data() : try stream.readExactly(payloadLength, allowEndOfStream: false)!
        return AgentFrame(try decodeHeader(json), payload)
    }

    static func appendUInt32(_ data: inout Data, _ value: UInt32) {
        data.append(contentsOf: [UInt8(value >> 24), UInt8(value >> 16 & 0xFF), UInt8(value >> 8 & 0xFF), UInt8(value & 0xFF)])
    }

    static func readUInt32(_ data: Data, _ offset: Int) -> UInt32 {
        let start = data.startIndex + offset
        return UInt32(data[start]) << 24 | UInt32(data[start + 1]) << 16 | UInt32(data[start + 2]) << 8 | UInt32(data[start + 3])
    }
}
