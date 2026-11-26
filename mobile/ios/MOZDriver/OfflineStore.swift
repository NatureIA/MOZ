import Foundation

struct QueueEntry: Codable, Identifiable {
    let id: String
    let owner: String
    let tripId: String
    let at: String
    let fix: FixPayload?
    let stageId: String?
    let finish: Bool
    var review: String?
}
struct DriverSnapshot: Codable {
    var user: DriverAccount
    var trips: [DriverTrip] = []
    var details: [String: TripDetail] = [:]
    var grant: Grant?
}
final class OfflineStore {
    let root: URL
    init(root: URL? = nil) {
        self.root = root ?? FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("MOZ", isDirectory: true)
    }
    private func folder(_ name: String) throws -> URL {
        var url = root.appendingPathComponent(name, isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true, attributes: [.protectionKey: FileProtectionType.completeUntilFirstUserAuthentication])
        var values = URLResourceValues(); values.isExcludedFromBackup = true
        try url.setResourceValues(values)
        return url
    }
    private func write(_ data: Data, to url: URL) throws {
        try data.write(to: url, options: [.atomic, .completeFileProtectionUntilFirstUserAuthentication])
    }
    private func valid(_ id: String) throws -> String {
        guard let uuid = UUID(uuidString: id) else { throw ApiError(status: 0, message: "Identificador inválido.") }
        return uuid.uuidString.lowercased()
    }
    private func entryURL(_ id: String) throws -> URL { try folder("queue").appendingPathComponent(valid(id) + ".json") }
    private func photoURL(_ id: String) throws -> URL { try folder("proofs").appendingPathComponent(valid(id) + ".jpg") }
    func snapshot() -> DriverSnapshot? {
        guard let url = try? folder("state").appendingPathComponent("driver.json"), let data = try? Data(contentsOf: url) else { return nil }
        return try? JSONDecoder().decode(DriverSnapshot.self, from: data)
    }
    func save(_ snapshot: DriverSnapshot) throws {
        try write(JSONEncoder().encode(snapshot), to: folder("state").appendingPathComponent("driver.json"))
    }
    func clearSnapshot() throws {
        let url = try folder("state").appendingPathComponent("driver.json")
        if FileManager.default.fileExists(atPath: url.path) { try FileManager.default.removeItem(at: url) }
    }
    func enqueue(_ entry: QueueEntry, photo: Data? = nil) throws {
        if let photo {
            guard entry.fix == nil, !photo.isEmpty, photo.count <= 20 * 1024 * 1024 else { throw ApiError(status: 0, message: "Comprovante inválido ou acima de 20 MB.") }
            try write(photo, to: photoURL(entry.id))
        }
        do { try write(JSONEncoder().encode(entry), to: entryURL(entry.id)) }
        catch { if photo != nil { try? FileManager.default.removeItem(at: photoURL(entry.id)) }; throw error }
    }
    func entries(owner: String) throws -> [QueueEntry] {
        let urls = try FileManager.default.contentsOfDirectory(at: folder("queue"), includingPropertiesForKeys: nil)
        return try urls.filter { $0.pathExtension == "json" }.map { try JSONDecoder().decode(QueueEntry.self, from: Data(contentsOf: $0)) }.filter { $0.owner.caseInsensitiveCompare(owner) == .orderedSame }.sorted {
            let a = MozDates.parse($0.at) ?? .distantPast, b = MozDates.parse($1.at) ?? .distantPast
            if a != b { return a < b }
            if ($0.fix != nil) != ($1.fix != nil) { return $0.fix != nil }
            return $0.id < $1.id
        }
    }
    func photo(_ id: String) throws -> Data { try Data(contentsOf: photoURL(id)) }
    func markReview(_ entry: QueueEntry, _ reason: String) throws {
        var updated = entry; updated.review = reason
        try write(JSONEncoder().encode(updated), to: entryURL(entry.id))
    }
    func remove(_ entry: QueueEntry) throws {
        try FileManager.default.removeItem(at: entryURL(entry.id))
        if entry.fix == nil { try? FileManager.default.removeItem(at: photoURL(entry.id)) }
    }
    func pendingFinish(owner: String, trip: String) throws -> Bool {
        try entries(owner: owner).contains { $0.tripId == trip && $0.finish }
    }
}
