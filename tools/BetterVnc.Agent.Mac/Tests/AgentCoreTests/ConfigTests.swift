import XCTest
@testable import AgentCore

/// The Swift agent must read the config the C# agent wrote, so an update keeps the pairings and the token.
final class ConfigTests: XCTestCase {
    let written = """
        {
          // written by the C# agent
          "token": "secret-token",
          "allowPairing": true,
          "viewers": [
            {
              "id": "0123456789abcdef",
              "name": "FRAMEWORK",
              "key": "a2V5",
              "pairedAt": "2026-10-16T18:46:41.418859-07:00",
              "lastSeen": "2026-10-16T18:46:41.4649931-07:00",
              "address": "10.20.30.142"
            },
            { "id": "x", "name": "Other", "key": "b", "pairedAt": "2026-01-02T03:04:05Z" }
          ],
          "port": 5971,
          "listen": "127.0.0.1",
          "maxPayloadMb": 64,
          "enableScroll": true,
          "enableVideo": false,
          "scrollScale": 1.5,
          "scrollInvert": false,
          "scrollPhases": true
        }
        """

    func testReadsTheCSharpConfig() throws {
        let cfg = try AgentConfig.decode(Data(written.utf8))
        XCTAssertEqual(cfg.token, "secret-token")
        XCTAssertEqual(cfg.port, 5971)
        XCTAssertEqual(cfg.listen, "127.0.0.1")
        XCTAssertEqual(cfg.maxPayloadMb, 64)
        XCTAssertFalse(cfg.enableVideo)
        XCTAssertEqual(cfg.scrollScale, 1.5)
        XCTAssertEqual(cfg.viewers.count, 2)
        let v = try XCTUnwrap(cfg.findViewer("0123456789abcdef"))
        XCTAssertEqual(v.name, "FRAMEWORK")
        XCTAssertEqual(v.key, "a2V5")
        XCTAssertEqual(v.address, "10.20.30.142")
        // 2026-10-17T01:46:41.418859Z
        XCTAssertEqual(v.pairedAt.timeIntervalSince1970, 1_792_201_601.418859, accuracy: 0.001)
        XCTAssertEqual(try XCTUnwrap(v.lastSeen).timeIntervalSince(v.pairedAt), 0.0461, accuracy: 0.001)
        XCTAssertNil(cfg.findViewer("x")?.lastSeen)
    }

    func testMissingFieldsTakeDefaults() throws {
        let cfg = try AgentConfig.decode(Data("{}".utf8))
        XCTAssertNil(cfg.token)
        XCTAssertTrue(cfg.allowPairing)
        XCTAssertEqual(cfg.port, 5970)
        XCTAssertEqual(cfg.listen, "0.0.0.0")
        XCTAssertEqual(cfg.maxPayloadMb, 256)
        XCTAssertTrue(cfg.enableScroll && cfg.enableVideo && cfg.scrollPhases)
        XCTAssertFalse(cfg.scrollInvert)
    }

    func testSaveAndLoadRoundTripWithPrivateFile() throws {
        let path = temporaryDirectory().appendingPathComponent("bettervnc-agent.json").path
        let cfg = try AgentConfig.decode(Data(written.utf8))
        cfg.removeViewer("other") // by name, ignoring case
        try cfg.save(path)
        let text = try String(contentsOfFile: path)
        XCTAssertTrue(text.contains("\"maxPayloadMb\" : 64") || text.contains("\"maxPayloadMb\": 64"))
        XCTAssertFalse(text.contains("stagingDirectory"))
        let mode = (try FileManager.default.attributesOfItem(atPath: path)[.posixPermissions] as? NSNumber)?.intValue
        XCTAssertEqual(mode, 0o600)
        let back = try XCTUnwrap(try AgentConfig.load(path))
        XCTAssertEqual(back.viewers.map(\.name), ["FRAMEWORK"])
        XCTAssertEqual(back.viewers[0].pairedAt.timeIntervalSince1970, 1_792_201_601.418859, accuracy: 0.001)
        XCTAssertNil(try AgentConfig.load(path + ".missing"))
    }

    func testDotNetDates() throws {
        for text in ["2026-10-16T18:46:41-07:00", "2026-10-16T18:46:41.4-07:00", "2026-10-16T18:46:41.1234567-07:00", "2026-10-17T01:46:41Z"] {
            let date = try XCTUnwrap(DotNetDate.parse(text), text)
            XCTAssertEqual(date.timeIntervalSince1970, 1_792_201_601, accuracy: 0.5, text)
        }
        XCTAssertNotNil(DotNetDate.parse("2026-10-16T18:46:41")) // local time
        XCTAssertNil(DotNetDate.parse("yesterday"))
        let now = Date()
        XCTAssertEqual(try XCTUnwrap(DotNetDate.parse(DotNetDate.format(now))).timeIntervalSince(now), 0, accuracy: 0.001)
    }

    func testAuthSummary() {
        let cfg = AgentConfig()
        XCTAssertEqual(cfg.authSummary, "pairing on, no token, 0 paired")
        cfg.token = "t"
        cfg.allowPairing = false
        cfg.addViewer(PairedViewer(id: "1", name: "A", key: "k"))
        cfg.addViewer(PairedViewer(id: "1", name: "A again", key: "k2"))
        XCTAssertEqual(cfg.authSummary, "pairing off, token set, 1 paired")
    }

    func testSingleInstanceLock() throws {
        let config = temporaryDirectory().appendingPathComponent("bettervnc-agent.json").path
        let path = SingleInstance.lockPath(forConfig: config)
        XCTAssertTrue(path.hasSuffix("/bettervnc-agent.lock"))
        var first = SingleInstance.acquire(path)
        XCTAssertNotNil(first)
        XCTAssertNil(SingleInstance.acquire(path))
        first = nil
        XCTAssertNotNil(SingleInstance.acquire(path))
    }

    func testLaunchAgentRestartsOnlyAfterACrash() {
        let plist = Installer.launchAgentPlist(exe: "/Applications/BetterVNC Agent.app/Contents/MacOS/bettervnc-agent", configPath: "/a&b.json", logPath: "/l.log")
        XCTAssertTrue(plist.contains("<key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>"))
        XCTAssertTrue(plist.contains("<string>com.bettervnc.agent</string>"))
        XCTAssertTrue(plist.contains("<string>/a&amp;b.json</string>"))
        XCTAssertTrue(plist.contains("<key>AssociatedBundleIdentifiers</key><string>com.bettervnc.agent</string>"))
        XCTAssertNoThrow(try PropertyListSerialization.propertyList(from: Data(plist.utf8), format: nil))
    }

    func testInstallLeavesOneCopyAndKeepsAnAppInApplicationsWhereItIs() {
        let home = URL(fileURLWithPath: "/Users/tim", isDirectory: true)
        // The package put it in /Applications; a user who cannot write there must not get a second copy in ~/Applications.
        XCTAssertEqual(Installer.destination(source: "/Applications/BetterVNC Agent.app", home: home, applicationsDir: "/Users/tim/Applications"),
                       "/Applications/BetterVNC Agent.app")
        XCTAssertEqual(Installer.destination(source: "/Users/tim/Applications/BetterVNC Agent.app", home: home, applicationsDir: "/Applications"),
                       "/Users/tim/Applications/BetterVNC Agent.app")
        // From a build folder or a download it is copied.
        XCTAssertEqual(Installer.destination(source: "/Users/tim/Downloads/BetterVNC Agent.app", home: home, applicationsDir: "/Applications"),
                       "/Applications/BetterVNC Agent.app")
        // Every other one of these goes on install, the C# agent's bundle (v0.0.4) included.
        XCTAssertEqual(Installer.installLocations(home: home), [
            "/Applications/BetterVNC Agent.app",
            "/Users/tim/Applications/BetterVNC Agent.app",
            "/Users/tim/Library/Application Support/BetterVNC/BetterVNC Agent.app",
        ])
    }
}
