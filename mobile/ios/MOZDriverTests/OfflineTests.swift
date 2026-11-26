import XCTest
@testable import MOZDriver

final class OfflineTests: XCTestCase {
    func testDurableChronologicalQueueAndAccountIsolation() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let store = OfflineStore(root: root)
        let owner = UUID().uuidString, other = UUID().uuidString, trip = UUID().uuidString
        let epoch = "2026-11-26T12:00:00.0000000+00:00"
        let start = try XCTUnwrap(MozDates.parse(epoch))
        let later = MozDates.string(start.addingTimeInterval(5400))
        let earlyId = UUID().uuidString, lateId = UUID().uuidString, photoId = UUID().uuidString
        func item(_ id: String, _ at: String) -> QueueEntry {
            let fix = FixPayload(id: id, at: at, lat: -27.8, lon: -50.3, accuracy: 5, speed: 0, trackingSince: epoch)
            return QueueEntry(id: id, owner: owner, tripId: trip, at: at, fix: fix, stageId: nil, finish: false, review: nil)
        }
        try store.enqueue(item(lateId, later))
        try store.enqueue(item(earlyId, epoch))
        let proof = QueueEntry(id: photoId, owner: owner, tripId: trip, at: later, fix: nil, stageId: UUID().uuidString, finish: true, review: nil)
        let bytes = Data([255,216,255,217])
        try store.enqueue(proof, photo: bytes)
        let reopened = OfflineStore(root: root)
        XCTAssertEqual(try reopened.entries(owner: owner).map(\.id), [earlyId, lateId, photoId])
        XCTAssertTrue(try reopened.entries(owner: other).isEmpty)
        XCTAssertTrue(try reopened.pendingFinish(owner: owner, trip: trip))
        XCTAssertEqual(try reopened.photo(photoId), bytes)
        try reopened.markReview(proof, "Conferência")
        XCTAssertEqual(try reopened.entries(owner: owner).last?.review, "Conferência")
        try reopened.remove(proof)
        XCTAssertFalse(try reopened.pendingFinish(owner: owner, trip: trip))
    }
    func testGrantSnapshotSurvivesReopen() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let user = DriverAccount(id: UUID().uuidString, name: "Motorista fictício", role: "driver", identityVerified: true)
        let grant = Grant(tripId: UUID().uuidString, trackingSince: "2026-11-26T12:00:00Z", offlineAllowed: true)
        try OfflineStore(root: root).save(DriverSnapshot(user: user, grant: grant))
        let reopened = OfflineStore(root: root)
        XCTAssertEqual(reopened.snapshot()?.grant, grant)
        try reopened.clearSnapshot()
        XCTAssertNil(reopened.snapshot())
    }
}
