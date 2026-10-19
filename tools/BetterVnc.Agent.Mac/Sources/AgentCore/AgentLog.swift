import Foundation

/// The agent's log: "yyyy-MM-dd HH:mm:ss message" lines, appended to bettervnc-agent.log next to the config (kept to
/// 2 MiB, the previous part in bettervnc-agent.log.1) and copied to standard output, which the LaunchAgent sends to
/// ~/Library/Logs/bettervnc-agent.log.
public final class AgentLog {
    public static let maxBytes = 2 * 1024 * 1024

    public let path: String?
    private let echo: Bool
    private let lock = NSLock()
    private let formatter: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "yyyy-MM-dd HH:mm:ss"
        return f
    }()

    /// Called with every line after it was written; any thread.
    public var onLine: ((String) -> Void)?

    public init(path: String?, echo: Bool = true) {
        self.path = path
        self.echo = echo
    }

    public func write(_ message: String) {
        lock.lock()
        let line = "\(formatter.string(from: Date())) \(message)"
        if let path {
            let fm = FileManager.default
            if let size = (try? fm.attributesOfItem(atPath: path))?[.size] as? NSNumber, size.intValue > AgentLog.maxBytes {
                _ = try? fm.removeItem(atPath: path + ".1")
                _ = try? fm.moveItem(atPath: path, toPath: path + ".1")
            }
            if let handle = FileHandle(forWritingAtPath: path) ?? (fm.createFile(atPath: path, contents: nil) ? FileHandle(forWritingAtPath: path) : nil) {
                handle.seekToEndOfFile()
                handle.write(Data((line + "\n").utf8))
                try? handle.close()
            }
        }
        if echo { print(line); fflush(stdout) }
        let callback = onLine
        lock.unlock()
        callback?(line)
    }
}
