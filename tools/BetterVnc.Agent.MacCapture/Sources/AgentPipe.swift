import Foundation

/// Kinds of message on standard output; see `AgentPipe`.
enum MessageKind: UInt8 {
    /// One Annex B access unit; the header carries its size, key-frame flag and capture time.
    case frame = 1
    /// UTF-8 "key=value" lines describing the running stream (sent once capture runs, and again when it changes).
    case info = 2
    /// UTF-8 text saying why the helper gives up; it exits right after.
    case error = 3
}

/// What goes to the agent on standard output. Every message is a 24-byte little-endian header and a payload:
///
///      0  u32  magic "BVC1"
///      4  u8   kind (`MessageKind`)
///      5  u8   flags: bit 0 = key frame
///      6  u16  reserved, 0
///      8  u16  frame width
///     10  u16  frame height
///     12  u32  payload length in bytes
///     16  i64  capture time in microseconds since the helper started
///
/// The agent's reader is `CaptureHelperSession` in tools/BetterVnc.Agent/Video.
enum AgentPipe {
    private static let lock = NSLock()

    static func send(_ kind: MessageKind, keyframe: Bool = false, width: Int = 0, height: Int = 0, timestamp: Int64 = 0, payload: [UInt8]) {
        var header = [UInt8](repeating: 0, count: 24)
        put(&header, at: 0, 0x3143_5642, bytes: 4)
        header[4] = kind.rawValue
        header[5] = keyframe ? 1 : 0
        put(&header, at: 8, UInt64(min(max(width, 0), 0xFFFF)), bytes: 2)
        put(&header, at: 10, UInt64(min(max(height, 0), 0xFFFF)), bytes: 2)
        put(&header, at: 12, UInt64(payload.count), bytes: 4)
        put(&header, at: 16, UInt64(bitPattern: timestamp), bytes: 8)
        lock.lock()
        defer { lock.unlock() }
        writeAll(header)
        writeAll(payload)
    }

    static func info(_ fields: [(String, String)]) {
        send(.info, payload: Array(fields.map { "\($0.0)=\($0.1)\n" }.joined().utf8))
    }

    /// Tells the agent why streaming cannot go on (the text reaches the viewer) and exits.
    static func fail(_ message: String) -> Never {
        send(.error, payload: Array(message.utf8))
        exit(1)
    }

    private static func put(_ buffer: inout [UInt8], at offset: Int, _ value: UInt64, bytes: Int) {
        for i in 0..<bytes { buffer[offset + i] = UInt8(truncatingIfNeeded: value >> UInt64(8 * i)) }
    }

    private static func writeAll(_ bytes: [UInt8]) {
        bytes.withUnsafeBytes { raw in
            guard let base = raw.baseAddress else { return }
            var offset = 0
            while offset < raw.count {
                let written = write(STDOUT_FILENO, base + offset, raw.count - offset)
                if written > 0 {
                    offset += written
                } else if written < 0 && errno == EINTR {
                    continue
                } else {
                    exit(0) // the agent closed the pipe: nobody wants the stream any more
                }
            }
        }
    }
}

/// A line for the agent's log (standard error).
func logLine(_ message: String) {
    let bytes = Array((message + "\n").utf8)
    bytes.withUnsafeBytes { raw in _ = write(STDERR_FILENO, raw.baseAddress, raw.count) }
}

struct HelperError: Error, CustomStringConvertible {
    let description: String
    init(_ description: String) { self.description = description }
}
