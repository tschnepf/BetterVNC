import Foundation

/// Packs files into one payload (entries listed in the header, contents concatenated in order) and unpacks them into a
/// directory, as FileBundle.cs does. Directories become relative names with '/'.
public enum FileBundle {
    public static func pack(_ paths: [String], maxBytes: Int = AgentFraming.defaultMaxPayloadBytes) throws -> ([AgentFileEntry], Data) {
        let fm = FileManager.default
        var files: [(name: String, path: String)] = []
        for p in paths {
            var isDir: ObjCBool = false
            guard fm.fileExists(atPath: p, isDirectory: &isDir) else {
                throw AgentProtocolError("File not found: \(p)")
            }
            if isDir.boolValue {
                let root = URL(fileURLWithPath: p).lastPathComponent
                for file in regularFiles(under: p) {
                    files.append(("\(root)/\(file.relative)", file.path))
                }
            } else {
                files.append((URL(fileURLWithPath: p).lastPathComponent, p))
            }
        }

        var total = 0
        for f in files {
            total += fileSize(f.path)
            if total > maxBytes {
                throw AgentProtocolError("The selected files exceed the transfer limit of \(maxBytes / (1024 * 1024)) MB.")
            }
        }

        var entries: [AgentFileEntry] = []
        var payload = Data(capacity: total)
        for f in files {
            let data = (try? Data(contentsOf: URL(fileURLWithPath: f.path), options: .mappedIfSafe)) ?? Data()
            let take = data.prefix(max(0, total - payload.count)) // a file that grew while we read is cut, as in C#
            payload.append(take)
            entries.append(AgentFileEntry(name: f.name, size: Int64(take.count)))
        }
        return (entries, payload)
    }

    /// Writes the bundle into `targetDir` and returns the top-level paths created (files or directories).
    public static func unpack(_ entries: [AgentFileEntry], _ payload: Data, into targetDir: String) throws -> [String] {
        let fm = FileManager.default
        try fm.createDirectory(atPath: targetDir, withIntermediateDirectories: true)
        let fullTarget = URL(fileURLWithPath: targetDir).standardizedFileURL.path
        var topLevel: [String] = []
        var offset = 0
        for e in entries {
            let name = try sanitize(e.name)
            guard e.size >= 0, offset + Int(e.size) <= payload.count else {
                throw AgentProtocolError("File bundle is shorter than its entries describe.")
            }
            let dest = URL(fileURLWithPath: fullTarget).appendingPathComponent(name).standardizedFileURL.path
            guard dest.hasPrefix(fullTarget + "/") else {
                throw AgentProtocolError("File entry '\(e.name)' escapes the target directory.")
            }
            try fm.createDirectory(atPath: (dest as NSString).deletingLastPathComponent, withIntermediateDirectories: true)
            let start = payload.startIndex + offset
            try payload.subdata(in: start..<(start + Int(e.size))).write(to: URL(fileURLWithPath: dest))
            offset += Int(e.size)

            let first = name.split(separator: "/", maxSplits: 1).first.map(String.init) ?? name
            let top = fullTarget + "/" + first
            if !topLevel.contains(top) { topLevel.append(top) }
        }
        return topLevel
    }

    /// Rejects absolute paths, drive letters and '..' segments; collapses separators.
    public static func sanitize(_ name: String) throws -> String {
        let n = name.replacingOccurrences(of: "\\", with: "/")
        if n.hasPrefix("/") { throw AgentProtocolError("Absolute path '\(name)' in bundle.") }
        let parts = n.split(separator: "/", omittingEmptySubsequences: true).map(String.init)
        if parts.isEmpty { throw AgentProtocolError("Empty file name in bundle.") }
        for part in parts where part == "." || part == ".." || part.unicodeScalars.contains(where: { invalid.contains($0) }) {
            throw AgentProtocolError("Invalid file name segment '\(part)' in bundle.")
        }
        return parts.joined(separator: "/")
    }

    /// Total size of a directory's files, for the clipboardChanged summary.
    public static func directorySize(_ path: String) -> Int64 {
        regularFiles(under: path).reduce(0) { $0 + Int64(fileSize($1.path)) }
    }

    // The Windows set, so a bundle made on any OS unpacks on any other.
    private static let invalid: Set<Unicode.Scalar> = {
        var set: Set<Unicode.Scalar> = ["\"", "<", ">", "|", ":", "*", "?", "\\", "/"]
        for v in 0..<32 { set.insert(Unicode.Scalar(UInt8(v))) }
        return set
    }()

    private static func regularFiles(under root: String) -> [(relative: String, path: String)] {
        let base = URL(fileURLWithPath: root).standardizedFileURL
        guard let walker = FileManager.default.enumerator(at: base, includingPropertiesForKeys: [.isRegularFileKey]) else { return [] }
        var out: [(String, String)] = []
        let prefix = base.path.hasSuffix("/") ? base.path : base.path + "/"
        for case let url as URL in walker {
            guard (try? url.resourceValues(forKeys: [.isRegularFileKey]))?.isRegularFile == true else { continue }
            let path = url.standardizedFileURL.path
            let relative = path.hasPrefix(prefix) ? String(path.dropFirst(prefix.count)) : url.lastPathComponent
            out.append((relative, path))
        }
        return out
    }

    private static func fileSize(_ path: String) -> Int {
        ((try? FileManager.default.attributesOfItem(atPath: path))?[.size] as? NSNumber)?.intValue ?? 0
    }
}
