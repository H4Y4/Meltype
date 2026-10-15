// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 H4Y4

import XCTest
@testable import MeltypeIME

final class ServerLockTests: XCTestCase {
    private var url: URL!

    override func setUp() {
        url = FileManager.default.temporaryDirectory
            .appendingPathComponent("MeltypeServerLockTests-\(UUID().uuidString)", isDirectory: true)
            .appendingPathComponent("server.lock")
    }

    override func tearDown() {
        try? FileManager.default.removeItem(at: url.deletingLastPathComponent())
    }

    func testSecondServerCannotTakeTheLock() throws {
        let first = try XCTUnwrap(ServerLock.acquire(at: url, wait: false))
        XCTAssertGreaterThanOrEqual(first, 0)
        XCTAssertNil(ServerLock.acquire(at: url, wait: false))
        close(first)
        let next = try XCTUnwrap(ServerLock.acquire(at: url, wait: false))
        close(next)
    }

    func testWaitingServerTakesOverWhenTheFirstEnds() throws {
        let first = try XCTUnwrap(ServerLock.acquire(at: url, wait: false))
        let tookOver = expectation(description: "took over")
        let url = self.url!
        Thread.detachNewThread {
            if let descriptor = ServerLock.acquire(at: url, wait: true) {
                tookOver.fulfill()
                close(descriptor)
            }
        }
        // 先の Meltype が動いている間は待つ
        let waiting = XCTWaiter.wait(for: [tookOver], timeout: 0.3)
        XCTAssertEqual(waiting, .timedOut)
        close(first)
        wait(for: [tookOver], timeout: 5)
    }

    func testLockIsNotInheritedByChildProcesses() throws {
        let descriptor = try XCTUnwrap(ServerLock.acquire(at: url, wait: false))
        defer { close(descriptor) }
        XCTAssertNotEqual(fcntl(descriptor, F_GETFD) & FD_CLOEXEC, 0)
    }
}
