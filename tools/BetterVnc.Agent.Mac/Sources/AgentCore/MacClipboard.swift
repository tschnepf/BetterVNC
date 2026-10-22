import AppKit
import Foundation

/// The general pasteboard, read and written in process (the C# agent ran osascript and pbcopy for this). Formats and
/// pasteboard types as MacClipboard.cs: text, HTML, PNG (TIFF converted), and files as file URLs.
public final class MacClipboard: GuestClipboard {
    public let osName = "macos"
    /// Reading changeCount costs nothing now, so changes are noticed quickly.
    public let pollInterval: TimeInterval = 0.5

    private static let textTypes = ["public.utf8-plain-text", "NSStringPboardType", "public.plain-text"]
    private static let htmlTypes = ["public.html", "Apple HTML pasteboard type"]
    private static let imageTypes = ["public.png", "public.tiff"]
    private static let fileTypes = ["public.file-url", "NSFilenamesPboardType"]

    private var pasteboard: NSPasteboard { NSPasteboard.general }

    public init() {}

    public func changeToken() -> String? { String(pasteboard.changeCount) }

    public func formats() -> [String] {
        let types = Set((pasteboard.types ?? []).map(\.rawValue))
        var formats: [String] = []
        if MacClipboard.textTypes.contains(where: types.contains) { formats.append(AgentFormat.text) }
        if MacClipboard.htmlTypes.contains(where: types.contains) { formats.append(AgentFormat.html) }
        if MacClipboard.imageTypes.contains(where: types.contains) { formats.append(AgentFormat.png) }
        if MacClipboard.fileTypes.contains(where: types.contains) { formats.append(AgentFormat.files) }
        return formats
    }

    public func data(_ format: String) -> Data? {
        switch format {
        case AgentFormat.text:
            return pasteboard.string(forType: .string).map { Data($0.utf8) }
        case AgentFormat.html:
            guard let html = pasteboard.string(forType: .html), !html.isEmpty else { return nil }
            return Data(html.utf8)
        case AgentFormat.png:
            if let png = pasteboard.data(forType: .png) { return png }
            guard let tiff = pasteboard.data(forType: .tiff), let image = NSBitmapImageRep(data: tiff) else { return nil }
            return image.representation(using: .png, properties: [:])
        default:
            return nil // files go through filePaths()
        }
    }

    public func filePaths() -> [String]? {
        let urls = pasteboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL] ?? []
        let paths = urls.map { ($0 as NSURL).filePathURL?.path ?? $0.path }.filter { !$0.isEmpty }
        return paths.isEmpty ? nil : paths
    }

    public func setData(_ format: String, _ data: Data) throws {
        let pb = pasteboard
        switch format {
        case AgentFormat.text:
            pb.clearContents()
            pb.setString(String(decoding: data, as: UTF8.self), forType: .string)
        case AgentFormat.html:
            let html = String(decoding: data, as: UTF8.self)
            pb.clearContents()
            pb.setString(html, forType: .html)
            // Apps that take plain text only get the text without tags, as the C# agent gave them.
            pb.setString(html.replacingOccurrences(of: "<[^>]*>", with: "", options: .regularExpression), forType: .string)
        case AgentFormat.png:
            pb.clearContents()
            pb.setData(data, forType: .png)
            // Some apps read only TIFF.
            if let tiff = NSBitmapImageRep(data: data)?.tiffRepresentation { pb.setData(tiff, forType: .tiff) }
        default:
            throw AgentProtocolError("Unsupported format '\(format)'.")
        }
    }

    public func setFiles(_ paths: [String]) throws {
        let pb = pasteboard
        pb.clearContents()
        if !pb.writeObjects(paths.map { URL(fileURLWithPath: $0) as NSURL }) {
            throw AgentProtocolError("The files could not be put on the clipboard.")
        }
    }
}
