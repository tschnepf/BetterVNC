// bettervnc-capture: the screen capture and encoder of the BetterVNC guest agent on macOS.
//
// The agent (tools/BetterVnc.Agent, MacScreenSource) starts it for each stream a viewer asks for:
//
//   bettervnc-capture [--codec h264|hevc] [--bitrate kbit/s] [--fps n] [--max-width px] [--max-height px]
//                     [--display id] [--no-low-latency]
//   bettervnc-capture --probe
//
// It captures the display with ScreenCaptureKit, encodes it with VideoToolbox and writes the access units to standard
// output (format: AgentPipe.swift). Standard input takes one command per line:
//
//   bitrate <kbit/s>    fps <n>    keyframe    size <width> <height> (largest picture; 0 0 = the display's size)    quit
//
// It stops when standard input closes. --probe prints what this Mac offers as key=value lines and exits;
// --permissions prints only whether screen recording is allowed (screenRecording=granted|denied).
import AppKit
import CoreGraphics
import Foundation

func usage(_ problem: String) -> Never {
    logLine("bettervnc-capture: \(problem)")
    logLine("usage: bettervnc-capture [--probe] [--codec h264|hevc] [--bitrate kbit/s] [--fps n] [--max-width px] [--max-height px] [--display id] [--no-low-latency]")
    exit(2)
}

func probeEncoder(_ codec: Codec) -> String {
    guard let encoder = try? VideoEncoder(codec: codec, width: 1920, height: 1080, bitrateKbps: 8_000, fps: 60,
                                          allowLowLatency: false, output: { _ in }) else { return "none" }
    return encoder.isHardware ? "hardware" : "software"
}

signal(SIGPIPE, SIG_IGN)

var settings = Settings()
var probe = false
let arguments = Array(CommandLine.arguments.dropFirst())
var index = 0

func value(_ option: String) -> String {
    index += 1
    guard index < arguments.count else { usage("\(option) needs a value") }
    return arguments[index]
}

func number(_ option: String) -> Int {
    guard let parsed = Int(value(option)), parsed >= 0 else { usage("\(option) needs a number") }
    return parsed
}

while index < arguments.count {
    switch arguments[index] {
    case "--probe": probe = true
    case "--permissions":
        // Asked by the agent's permission wizard; a new process sees a switch turned on since the agent started.
        print("screenRecording=\(CGPreflightScreenCaptureAccess() ? "granted" : "denied")")
        exit(0)
    case "--codec":
        guard let codec = Codec(rawValue: value("--codec")) else { usage("--codec is h264 or hevc") }
        settings.codec = codec
    case "--bitrate": settings.bitrateKbps = max(number("--bitrate"), 100)
    case "--fps": settings.fps = min(max(number("--fps"), 1), 120)
    case "--max-width": settings.maxWidth = number("--max-width")
    case "--max-height": settings.maxHeight = number("--max-height")
    case "--display": settings.displayID = CGDirectDisplayID(number("--display"))
    case "--no-low-latency": settings.lowLatency = false
    default: usage("unknown option \(arguments[index])")
    }
    index += 1
}

if probe {
    let version = ProcessInfo.processInfo.operatingSystemVersion
    print("macos=\(version.majorVersion).\(version.minorVersion).\(version.patchVersion)")
    print("screenRecording=\(CGPreflightScreenCaptureAccess() ? "granted" : "denied")")
    print("h264=\(probeEncoder(.h264))")
    print("hevc=\(probeEncoder(.hevc))")
    exit(0)
}

// A command-line tool has no connection to the window server until something makes one; ScreenCaptureKit expects it.
_ = NSApplication.shared
NSApplication.shared.setActivationPolicy(.prohibited)

let capture = Capture(settings: settings)
capture.start()

Thread {
    while let line = readLine() {
        let words = line.split(separator: " ").map(String.init)
        guard let command = words.first else { continue }
        capture.perform(command, words.dropFirst().compactMap { Int($0) })
    }
    exit(0) // the agent closed standard input: it stopped the stream, or went away
}.start()

dispatchMain()
