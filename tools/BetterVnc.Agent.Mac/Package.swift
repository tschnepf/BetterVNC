// swift-tools-version:5.10
// The BetterVNC guest agent for macOS: a native menu bar app that gives the BetterVNC viewer a rich clipboard, smooth
// scrolling and a hardware-encoded H.264 stream of the screen. It speaks the same protocol as the C# agent on Windows and
// Linux (src/BetterVnc.Agent.Protocol). build.sh turns the executable into "BetterVNC Agent.app".
import PackageDescription

let package = Package(
    name: "BetterVncAgent",
    platforms: [.macOS("12.3")], // ScreenCaptureKit
    products: [
        .executable(name: "bettervnc-agent", targets: ["BetterVncAgent"]),
    ],
    targets: [
        // Everything that can be tested without a person at the screen: protocol, server, config, clipboard, scrolling,
        // capture and encoding, permissions, install.
        .target(name: "AgentCore"),
        // The app: command line, menu bar item, dialogs, permission wizard.
        .executableTarget(name: "BetterVncAgent", dependencies: ["AgentCore"]),
        .testTarget(name: "AgentCoreTests", dependencies: ["AgentCore"]),
    ],
    swiftLanguageVersions: [.v5]
)
