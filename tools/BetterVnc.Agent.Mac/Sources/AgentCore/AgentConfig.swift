import Foundation

/// A viewer the agent has paired with (PairedViewer in AgentPairing.cs). A class: the server updates `lastSeen` on the
/// config's own entry.
public final class PairedViewer: Codable {
    public var id: String
    public var name: String
    /// Base64 of the derived 32-byte key; the viewer's token.
    public var key: String
    public var pairedAt: Date
    public var lastSeen: Date?
    /// Address the viewer paired from (informational).
    public var address: String?

    public init(id: String, name: String, key: String, pairedAt: Date = Date(), lastSeen: Date? = nil, address: String? = nil) {
        self.id = id
        self.name = name
        self.key = key
        self.pairedAt = pairedAt
        self.lastSeen = lastSeen
        self.address = address
    }
}

/// bettervnc-agent.json. The same file and fields as the C# agent's AgentConfig, so an existing install keeps its pairings
/// and token: camelCase keys, missing keys take their defaults, comments are allowed, unknown keys are ignored.
public final class AgentConfig: Codable {
    public static let fileName = "bettervnc-agent.json"
    public static let defaultPort = 5970

    /// Optional shared token or password. nil: paired viewers only.
    public var token: String?
    /// Accept new viewers that ask to pair (a dialog on this screen asks).
    public var allowPairing = true
    public var viewers: [PairedViewer] = []
    public var port = AgentConfig.defaultPort
    /// Address to bind: 0.0.0.0 for every interface, 127.0.0.1 for a tunnel-only setup.
    public var listen = "0.0.0.0"
    public var maxPayloadMb = 256
    /// Where files pushed by the viewer are written before they go on the clipboard; nil = $TMPDIR/bettervnc-agent.
    public var stagingDirectory: String?
    public var enableScroll = true
    public var enableVideo = true
    public var scrollScale = 1.0
    public var scrollInvert = false
    public var scrollPhases = true

    private let lock = NSLock()

    public init() {}

    /// ~/Library/Application Support/BetterVNC/bettervnc-agent.json. Never inside the app: that would break its signature.
    public static var defaultPath: String {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/BetterVNC", isDirectory: true)
            .appendingPathComponent(fileName).path
    }

    enum CodingKeys: String, CodingKey {
        case token, allowPairing, viewers, port, listen, maxPayloadMb, stagingDirectory
        case enableScroll, enableVideo, scrollScale, scrollInvert, scrollPhases
    }

    public required init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        token = try c.decodeIfPresent(String.self, forKey: .token)
        allowPairing = try c.decodeIfPresent(Bool.self, forKey: .allowPairing) ?? true
        viewers = try c.decodeIfPresent([PairedViewer].self, forKey: .viewers) ?? []
        port = try c.decodeIfPresent(Int.self, forKey: .port) ?? AgentConfig.defaultPort
        listen = try c.decodeIfPresent(String.self, forKey: .listen) ?? "0.0.0.0"
        maxPayloadMb = try c.decodeIfPresent(Int.self, forKey: .maxPayloadMb) ?? 256
        stagingDirectory = try c.decodeIfPresent(String.self, forKey: .stagingDirectory)
        enableScroll = try c.decodeIfPresent(Bool.self, forKey: .enableScroll) ?? true
        enableVideo = try c.decodeIfPresent(Bool.self, forKey: .enableVideo) ?? true
        scrollScale = try c.decodeIfPresent(Double.self, forKey: .scrollScale) ?? 1.0
        scrollInvert = try c.decodeIfPresent(Bool.self, forKey: .scrollInvert) ?? false
        scrollPhases = try c.decodeIfPresent(Bool.self, forKey: .scrollPhases) ?? true
    }

    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encodeIfPresent(token, forKey: .token)
        try c.encode(allowPairing, forKey: .allowPairing)
        try c.encode(snapshotViewers(), forKey: .viewers)
        try c.encode(port, forKey: .port)
        try c.encode(listen, forKey: .listen)
        try c.encode(maxPayloadMb, forKey: .maxPayloadMb)
        try c.encodeIfPresent(stagingDirectory, forKey: .stagingDirectory)
        try c.encode(enableScroll, forKey: .enableScroll)
        try c.encode(enableVideo, forKey: .enableVideo)
        try c.encode(scrollScale, forKey: .scrollScale)
        try c.encode(scrollInvert, forKey: .scrollInvert)
        try c.encode(scrollPhases, forKey: .scrollPhases)
    }

    /// The config at `path`, nil when there is none. Throws when the file is there but cannot be read.
    public static func load(_ path: String) throws -> AgentConfig? {
        guard FileManager.default.fileExists(atPath: path) else { return nil }
        return try decode(Data(contentsOf: URL(fileURLWithPath: path)))
    }

    public static func decode(_ data: Data) throws -> AgentConfig {
        let decoder = JSONDecoder()
        decoder.allowsJSON5 = true // comments, as the C# agent allows
        decoder.dateDecodingStrategy = .custom { d in
            let text = try d.singleValueContainer().decode(String.self)
            guard let date = DotNetDate.parse(text) else {
                throw DecodingError.dataCorrupted(.init(codingPath: d.codingPath, debugDescription: "Not a date: \(text)"))
            }
            return date
        }
        return try decoder.decode(AgentConfig.self, from: data)
    }

    public func encoded() throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .withoutEscapingSlashes]
        encoder.dateEncodingStrategy = .custom { date, e in
            var c = e.singleValueContainer()
            try c.encode(DotNetDate.format(date))
        }
        return try encoder.encode(self)
    }

    /// Writes atomically and readable by this user only (it holds the token and the paired keys).
    public func save(_ path: String) throws {
        let url = URL(fileURLWithPath: path)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        let tmp = path + ".tmp"
        try encoded().write(to: URL(fileURLWithPath: tmp))
        chmod(tmp, 0o600)
        if rename(tmp, path) != 0 { throw CocoaError(.fileWriteUnknown) }
    }

    public static func createNew() -> AgentConfig { AgentConfig() }

    public func findViewer(_ id: String) -> PairedViewer? {
        lock.lock(); defer { lock.unlock() }
        return viewers.first { $0.id == id }
    }

    public func addViewer(_ viewer: PairedViewer) {
        lock.lock(); defer { lock.unlock() }
        viewers.removeAll { $0.id == viewer.id }
        viewers.append(viewer)
    }

    /// Removes every viewer with this id, or this name (ignoring case); true when one went.
    @discardableResult
    public func removeViewer(_ idOrName: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        let before = viewers.count
        viewers.removeAll { $0.id == idOrName || $0.name.caseInsensitiveCompare(idOrName) == .orderedSame }
        return viewers.count != before
    }

    public func snapshotViewers() -> [PairedViewer] {
        lock.lock(); defer { lock.unlock() }
        return viewers
    }

    public var authSummary: String {
        (allowPairing ? "pairing on" : "pairing off") + ((token ?? "").isEmpty ? ", no token" : ", token set") + ", \(snapshotViewers().count) paired"
    }
}

/// Dates as .NET writes a local DateTime: "2026-10-16T18:46:41.418859-07:00" (0–7 fraction digits, offset optional).
public enum DotNetDate {
    public static func parse(_ text: String) -> Date? {
        let pattern = #"^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,9}))?(Z|[+-]\d{2}:\d{2})?$"#
        guard let regex = try? NSRegularExpression(pattern: pattern),
              let m = regex.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)) else { return nil }
        func group(_ i: Int) -> String? {
            guard let r = Range(m.range(at: i), in: text) else { return nil }
            return String(text[r])
        }
        var parts = DateComponents()
        parts.year = Int(group(1)!)
        parts.month = Int(group(2)!)
        parts.day = Int(group(3)!)
        parts.hour = Int(group(4)!)
        parts.minute = Int(group(5)!)
        parts.second = Int(group(6)!)
        var calendar = Calendar(identifier: .gregorian)
        if let zone = group(8) {
            if zone == "Z" {
                calendar.timeZone = TimeZone(secondsFromGMT: 0)!
            } else {
                let sign = zone.hasPrefix("-") ? -1 : 1
                let hours = Int(zone.dropFirst().prefix(2))!, minutes = Int(zone.suffix(2))!
                calendar.timeZone = TimeZone(secondsFromGMT: sign * (hours * 3600 + minutes * 60))!
            }
        } else {
            calendar.timeZone = .current
        }
        guard let whole = calendar.date(from: parts) else { return nil }
        let fraction = group(7).map { Double("0." + $0) ?? 0 } ?? 0
        return whole.addingTimeInterval(fraction)
    }

    /// Local time with offset and seven fraction digits, as .NET writes it.
    public static func format(_ date: Date) -> String {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.calendar = Calendar(identifier: .gregorian)
        f.timeZone = .current
        f.dateFormat = "yyyy-MM-dd'T'HH:mm:ss.SSSSSSSXXXXX"
        return f.string(from: date)
    }
}
