import Darwin
import Foundation

/// The connection ended: closed by the peer, reset, timed out, or shut down by us. Not worth a log line on its own.
public struct ConnectionClosed: Error, CustomStringConvertible {
    public let description: String
    public init(_ description: String) { self.description = description }
}

/// A connected TCP socket with blocking reads and writes. One thread reads; any thread may write, one frame at a time.
/// `shutdown()` wakes a blocked reader from another thread; the fd is closed only once, by `close()`.
public final class SocketStream {
    private let fd: Int32
    private let writeLock = NSLock()
    private let stateLock = NSLock()
    private var closed = false

    /// "address:port" of the other end, as the C# agent prints it.
    public let peer: String
    /// The other end's address alone.
    public let peerAddress: String

    public init(fd: Int32) {
        self.fd = fd
        var one: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &one, socklen_t(MemoryLayout<Int32>.size))
        setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &one, socklen_t(MemoryLayout<Int32>.size))
        (peerAddress, peer) = SocketStream.describePeer(fd)
    }

    /// Connects to host:port (used by the tests and by anything that plays the viewer's part).
    public static func connect(host: String, port: Int) throws -> SocketStream {
        var hints = addrinfo(ai_flags: 0, ai_family: AF_UNSPEC, ai_socktype: SOCK_STREAM, ai_protocol: IPPROTO_TCP,
                             ai_addrlen: 0, ai_canonname: nil, ai_addr: nil, ai_next: nil)
        var result: UnsafeMutablePointer<addrinfo>?
        guard getaddrinfo(host, String(port), &hints, &result) == 0, let first = result else {
            throw ConnectionClosed("Cannot resolve \(host).")
        }
        defer { freeaddrinfo(result) }
        var info: UnsafeMutablePointer<addrinfo>? = first
        while let ai = info {
            let fd = socket(ai.pointee.ai_family, ai.pointee.ai_socktype, ai.pointee.ai_protocol)
            if fd >= 0 {
                if Darwin.connect(fd, ai.pointee.ai_addr, ai.pointee.ai_addrlen) == 0 { return SocketStream(fd: fd) }
                Darwin.close(fd)
            }
            info = ai.pointee.ai_next
        }
        throw ConnectionClosed("Cannot connect to \(host):\(port): \(String(cString: strerror(errno)))")
    }

    /// Reads time out after this many seconds (0 = never), which ends the connection with `ConnectionClosed`.
    public func setReadTimeout(_ seconds: Double) {
        var tv = timeval(tv_sec: Int(seconds), tv_usec: Int32((seconds - Double(Int(seconds))) * 1_000_000))
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))
    }

    /// Exactly `count` bytes. At the end of the stream before the first byte: nil when allowed, else an error.
    public func readExactly(_ count: Int, allowEndOfStream: Bool) throws -> Data? {
        var data = Data(count: count)
        var total = 0
        while total < count {
            let n = data.withUnsafeMutableBytes { raw in recv(fd, raw.baseAddress! + total, count - total, 0) }
            if n > 0 { total += n; continue }
            if n == 0 {
                if total == 0 && allowEndOfStream { return nil }
                throw AgentProtocolError("Connection closed in the middle of a frame.")
            }
            if errno == EINTR { continue }
            if errno == EAGAIN || errno == EWOULDBLOCK { throw ConnectionClosed("timed out") }
            throw ConnectionClosed(String(cString: strerror(errno)))
        }
        return data
    }

    /// Writes all of `data` (one or more whole frames) before another writer may start.
    public func write(_ data: Data) throws {
        writeLock.lock()
        defer { writeLock.unlock() }
        var offset = 0
        try data.withUnsafeBytes { raw in
            while offset < raw.count {
                let n = Darwin.send(fd, raw.baseAddress! + offset, raw.count - offset, 0)
                if n > 0 { offset += n; continue }
                if n < 0 && errno == EINTR { continue }
                throw ConnectionClosed(n == 0 ? "closed" : String(cString: strerror(errno)))
            }
        }
    }

    public func send(_ header: AgentMessage, _ payload: Data = Data()) throws {
        try write(AgentFraming.encode(header, payload))
    }

    /// Ends the connection in both directions; a thread blocked in a read returns. Safe from any thread, any number of times.
    public func shutdown() {
        stateLock.lock()
        defer { stateLock.unlock() }
        if !closed { Darwin.shutdown(fd, SHUT_RDWR) }
    }

    public func close() {
        stateLock.lock()
        defer { stateLock.unlock() }
        if closed { return }
        closed = true
        Darwin.shutdown(fd, SHUT_RDWR)
        Darwin.close(fd)
    }

    private static func describePeer(_ fd: Int32) -> (String, String) {
        var storage = sockaddr_storage()
        var length = socklen_t(MemoryLayout<sockaddr_storage>.size)
        let ok = withUnsafeMutablePointer(to: &storage) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { getpeername(fd, $0, &length) == 0 }
        }
        guard ok, let (address, port) = SocketAddress.describe(storage) else { return ("?", "?") }
        return (address, storage.ss_family == sa_family_t(AF_INET6) ? "[\(address)]:\(port)" : "\(address):\(port)")
    }
}

enum SocketAddress {
    /// Address text and port of an IPv4 or IPv6 socket address; IPv4-mapped IPv6 addresses are shown as IPv4.
    static func describe(_ storage: sockaddr_storage) -> (String, Int)? {
        var storage = storage
        var buffer = [CChar](repeating: 0, count: Int(INET6_ADDRSTRLEN))
        switch Int32(storage.ss_family) {
        case AF_INET:
            return withUnsafePointer(to: &storage) {
                $0.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { sin in
                    var addr = sin.pointee.sin_addr
                    inet_ntop(AF_INET, &addr, &buffer, socklen_t(buffer.count))
                    return (String(cString: buffer), Int(UInt16(bigEndian: sin.pointee.sin_port)))
                }
            }
        case AF_INET6:
            return withUnsafePointer(to: &storage) {
                $0.withMemoryRebound(to: sockaddr_in6.self, capacity: 1) { sin6 in
                    var addr = sin6.pointee.sin6_addr
                    inet_ntop(AF_INET6, &addr, &buffer, socklen_t(buffer.count))
                    var text = String(cString: buffer)
                    if text.hasPrefix("::ffff:"), text.contains(".") { text.removeFirst(7) }
                    return (text, Int(UInt16(bigEndian: sin6.pointee.sin6_port)))
                }
            }
        default:
            return nil
        }
    }
}

/// A listening TCP socket. `accept()` polls, so `stop()` from another thread ends it within a quarter of a second.
public final class TcpListener {
    private let fd: Int32
    private let lock = NSLock()
    private var stopped = false
    public let port: Int

    /// Binds and listens on `address` (an IPv4 or IPv6 literal; 0.0.0.0 = every IPv4 interface) and `port` (0 = any free one).
    public init(address: String, port: Int) throws {
        var v4 = in_addr(), v6 = in6_addr()
        let family: Int32
        if inet_pton(AF_INET, address, &v4) == 1 { family = AF_INET }
        else if inet_pton(AF_INET6, address, &v6) == 1 { family = AF_INET6 }
        else { throw ConnectionClosed("'\(address)' is not an IP address.") }

        let fd = socket(family, SOCK_STREAM, IPPROTO_TCP)
        guard fd >= 0 else { throw ConnectionClosed(String(cString: strerror(errno))) }
        var one: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &one, socklen_t(MemoryLayout<Int32>.size))
        var result: Int32
        if family == AF_INET {
            var sin = sockaddr_in()
            sin.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
            sin.sin_family = sa_family_t(AF_INET)
            sin.sin_port = UInt16(port).bigEndian
            sin.sin_addr = v4
            result = withUnsafePointer(to: &sin) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) } }
        } else {
            var sin6 = sockaddr_in6()
            sin6.sin6_len = UInt8(MemoryLayout<sockaddr_in6>.size)
            sin6.sin6_family = sa_family_t(AF_INET6)
            sin6.sin6_port = UInt16(port).bigEndian
            sin6.sin6_addr = v6
            result = withUnsafePointer(to: &sin6) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in6>.size)) } }
        }
        if result != 0 || listen(fd, 16) != 0 {
            let message = String(cString: strerror(errno))
            Darwin.close(fd)
            throw ConnectionClosed(message)
        }
        var storage = sockaddr_storage()
        var length = socklen_t(MemoryLayout<sockaddr_storage>.size)
        _ = withUnsafeMutablePointer(to: &storage) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { getsockname(fd, $0, &length) } }
        self.fd = fd
        self.port = SocketAddress.describe(storage)?.1 ?? port
    }

    /// The next connection, or nil once stopped.
    public func accept() -> SocketStream? {
        while true {
            lock.lock()
            if stopped { lock.unlock(); return nil }
            lock.unlock()
            var pfd = pollfd(fd: fd, events: Int16(POLLIN), revents: 0)
            let ready = poll(&pfd, 1, 250)
            if ready <= 0 { continue }
            lock.lock()
            if stopped { lock.unlock(); return nil }
            let client = Darwin.accept(fd, nil, nil)
            lock.unlock()
            if client >= 0 { return SocketStream(fd: client) }
        }
    }

    public func stop() {
        lock.lock()
        defer { lock.unlock() }
        if stopped { return }
        stopped = true
        Darwin.close(fd)
    }
}
