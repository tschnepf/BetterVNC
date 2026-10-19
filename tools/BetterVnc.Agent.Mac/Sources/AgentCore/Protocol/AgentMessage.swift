import Foundation

// The agent protocol, as defined by src/BetterVnc.Agent.Protocol (AgentMessage.cs). Names and values here must stay the
// same as there: the Windows viewer talks to this agent through that library.

/// Message type names carried in `AgentMessage.type`.
public enum AgentMessageType {
    public static let hello = "hello"
    public static let welcome = "welcome"
    public static let error = "error"
    public static let clipboardChanged = "clipboardChanged"
    public static let getClipboard = "getClipboard"
    public static let clipboardData = "clipboardData"
    public static let setClipboard = "setClipboard"
    public static let ok = "ok"
    public static let ping = "ping"
    public static let pong = "pong"
    public static let pair = "pair"
    public static let paired = "paired"
    public static let scroll = "scroll"
    public static let startVideo = "startVideo"
    public static let videoStarted = "videoStarted"
    public static let setVideo = "setVideo"
    public static let stopVideo = "stopVideo"
    public static let videoFrame = "videoFrame"
    public static let videoStopped = "videoStopped"
}

/// Codec names used in `codecs` and `codec`.
public enum AgentVideoCodec {
    public static let jpeg = "jpeg"
    public static let h264 = "h264"
    public static let hevc = "hevc"
}

/// Values of `encoder` in a videoStarted reply.
public enum AgentVideoEncoder {
    public static let hardware = "hardware"
    public static let software = "software"
}

/// Values of `code` on an error during the handshake.
public enum AgentErrorCode {
    public static let authFailed = "authFailed"
    public static let notPaired = "notPaired"
    public static let pairingDenied = "pairingDenied"
    public static let pairingDisabled = "pairingDisabled"
}

/// Optional capabilities announced in the welcome message.
public enum AgentFeature {
    public static let scroll = "scroll"
    public static let video = "video"
}

/// Values of `phase` in a scroll message.
public enum AgentScrollPhase {
    public static let begin = "begin"
    public static let `continue` = "continue"
    public static let end = "end"
}

/// Clipboard format names.
public enum AgentFormat {
    public static let text = "text"
    public static let html = "html"
    public static let png = "png"
    public static let files = "files"
    public static let all = [text, html, png, files]
}

/// One file in a files payload. Names are relative and use '/' as separator.
public struct AgentFileEntry: Codable, Equatable {
    public var name: String
    public var size: Int64

    public init(name: String, size: Int64) {
        self.name = name
        self.size = size
    }
}

/// The JSON header of a frame. One type covers every message; fields that do not apply are nil and left out.
public struct AgentMessage: Codable, Equatable {
    public static let protocolVersion = 1

    public var type: String
    public var id: Int?
    public var version: Int?
    public var os: String?
    public var app: String?
    public var nonce: String?
    public var proof: String?
    public var formats: [String]?
    public var features: [String]?
    public var format: String?
    public var files: [AgentFileEntry]?
    public var empty: Bool?
    public var message: String?
    public var code: String?

    // pairing
    public var viewerId: String?
    public var viewerName: String?
    public var publicKey: String?

    // scroll
    public var dx: Double?
    public var dy: Double?
    public var x: Int?
    public var y: Int?
    public var phase: String?
    public var wheelDx: Double?
    public var wheelDy: Double?

    // video
    public var codecs: [String]?
    public var codec: String?
    public var bitrate: Int?
    public var maxFps: Int?
    public var encoder: String?
    public var width: Int?
    public var height: Int?
    public var offsetX: Int?
    public var offsetY: Int?
    public var keyframe: Bool?
    public var timestamp: Int64?
    public var sequence: Int?
    public var keyframeRequest: Bool?

    public init(type: String, id: Int? = nil) {
        self.type = type
        self.id = id
    }

    public static func errorReply(_ id: Int?, _ message: String, code: String? = nil) -> AgentMessage {
        var m = AgentMessage(type: AgentMessageType.error, id: id)
        m.message = message
        m.code = code
        return m
    }
}
