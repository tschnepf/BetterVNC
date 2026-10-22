import XCTest
@testable import AgentCore

/// Scroll arithmetic and phases (as MacInput.cs), and the capture size rules.
final class InputAndCaptureTests: XCTestCase {
    private func makeInput(scale: Double = 1, invert: Bool = false, phases: Bool = true) -> (MacInput, () -> [[Int64]]) {
        let input = MacInput(scale: scale, invert: invert, phases: phases)
        var posted: [[Int64]] = []
        input.post = { w1, w2, phase in posted.append([Int64(w1), Int64(w2), phase ?? -1]) }
        input.isTrusted = { true }
        return (input, { posted })
    }

    func testGestureBecomesBeganChangedEnded() throws {
        let (input, posted) = makeInput()
        try input.scroll(dx: 0, dy: -48, wheelDx: nil, wheelDy: nil, phase: "begin")
        try input.scroll(dx: 10, dy: 0, wheelDx: nil, wheelDy: nil, phase: "continue")
        try input.scroll(dx: 0, dy: 0, wheelDx: nil, wheelDy: nil, phase: "end")
        XCTAssertEqual(posted(), [[0, 0, 1], [-48, 0, 2], [0, -10, 2], [0, 0, 4]])
    }

    func testFractionsAccumulateUntilTheEnd() throws {
        let (input, posted) = makeInput()
        try input.scroll(dx: 0, dy: 0.4, wheelDx: nil, wheelDy: nil, phase: "continue")
        try input.scroll(dx: 0, dy: 0.4, wheelDx: nil, wheelDy: nil, phase: "continue")
        XCTAssertEqual(posted(), [])
        try input.scroll(dx: 0, dy: 0.4, wheelDx: nil, wheelDy: nil, phase: "continue")
        XCTAssertEqual(posted(), [[1, 0, 2]])
        try input.scroll(dx: 0, dy: 0.5, wheelDx: nil, wheelDy: nil, phase: "end") // 0.2 + 0.5: nothing whole, then reset
        try input.scroll(dx: 0, dy: 0.5, wheelDx: nil, wheelDy: nil, phase: "continue")
        XCTAssertEqual(posted(), [[1, 0, 2], [0, 0, 4]])
    }

    func testScaleInvertAndNoPhases() throws {
        let (input, posted) = makeInput(scale: 2, invert: true, phases: false)
        try input.scroll(dx: 3, dy: 10, wheelDx: nil, wheelDy: nil, phase: "begin")
        try input.scroll(dx: 0, dy: 0, wheelDx: nil, wheelDy: nil, phase: "end")
        XCTAssertEqual(posted(), [[-20, 6, -1]])
    }

    func testMissingAccessibilityIsReportedOnce() throws {
        let input = MacInput()
        input.post = { _, _, _ in }
        input.isTrusted = { false }
        var asked = 0
        input.accessibilityMissing = { asked += 1; return true }
        try input.scroll(dx: 0, dy: 1, wheelDx: nil, wheelDy: nil, phase: nil)
        try input.scroll(dx: 0, dy: 1, wheelDx: nil, wheelDy: nil, phase: nil)
        XCTAssertEqual(asked, 1)
    }

    func testOneDisplayIsScaledToTheViewerKeepingItsShape() {
        let g = DisplayGeometry(pixelWidth: 3840, pixelHeight: 2160, displayCount: 1)
        XCTAssertEqual(g.outputSize(maxWidth: 0, maxHeight: 0).map { [$0.width, $0.height] }, [3840, 2160])
        XCTAssertEqual(g.outputSize(maxWidth: 1600, maxHeight: 1000).map { [$0.width, $0.height] }, [1600, 900])
        XCTAssertEqual(g.outputSize(maxWidth: 0, maxHeight: 1001).map { [$0.width, $0.height] }, [1778, 1000]) // even sides
        let big = DisplayGeometry(pixelWidth: 5120, pixelHeight: 2880, displayCount: 1)
        XCTAssertEqual(big.outputSize(maxWidth: 0, maxHeight: 0).map { [$0.width, $0.height] }, [4096, 2304])
    }

    func testOneOfSeveralDisplaysKeepsItsSize() {
        let g = DisplayGeometry(pixelWidth: 1920, pixelHeight: 1080, offsetX: 2560, offsetY: 0, displayCount: 2)
        XCTAssertEqual(g.outputSize(maxWidth: 800, maxHeight: 600).map { [$0.width, $0.height] }, [1920, 1080])
        XCTAssertNil(DisplayGeometry(pixelWidth: 5120, pixelHeight: 2880, displayCount: 2).outputSize(maxWidth: 0, maxHeight: 0))
    }

    func testThisMacHasAnH264Encoder() {
        XCTAssertNotEqual(MacScreenSource.probeEncoder(.h264), "none")
    }
}
