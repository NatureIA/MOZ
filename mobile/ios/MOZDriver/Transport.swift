import Foundation
import Security

struct DriverAccount: Codable, Equatable {
    let id: String
    let name: String
    let role: String
    let identityVerified: Bool
}
struct DriverTrip: Codable, Identifiable {
    let id: String
    let code: String
    let tripReference: String?
    let container: String?
    let tractor: String
    let trailer: String
    let status: String
    let scheduled: String
    let started: String?
    let finished: String?
    let trackingSince: String?
    let trackingActive: Bool
    let currentStage: Int
    var reference: String { tripReference ?? container ?? code }
    var terminal: Bool { finished != nil || status == "Finalizada" || status == "Cancelada" }
}
struct DriverStage: Codable, Identifiable {
    let id: String
    let order: Int
    let label: String
    let kind: String
    let address: String
    let cargoRole: String?
    let lat: Double?
    let lon: Double?
    let arrived: String?
    let departed: String?
    let workStart: String?
    let workEnd: String?
    let requirePod: Bool
    let finishOnPod: Bool
}
struct TripDetail: Codable {
    let trip: DriverTrip
    let stages: [DriverStage]
}
struct TripPage: Codable {
    let total: Int
    let items: [DriverTrip]
}
struct Grant: Codable, Equatable {
    let tripId: String?
    let trackingSince: String?
    let offlineAllowed: Bool
}
struct FixPayload: Codable {
    let id: String
    let at: String
    let lat: Double
    let lon: Double
    let accuracy: Double
    let speed: Double?
    let trackingSince: String
}
struct FixResult: Codable {
    let acceptedIds: [String]
    let discardIds: [String]
    let status: String
    let trackingActive: Bool
}
struct PodResult: Codable { let status: String; let id: String }
struct EmptyResponse: Codable {}
struct Notices: Codable {
    struct Entry: Codable, Identifiable {
        let id: String
        let tripId: String
        let kind: String
        let message: String
        let at: String
        let read: Bool
    }
    let unread: Int
    let items: [Entry]
}
struct ApiError: LocalizedError {
    let status: Int
    let message: String
    var errorDescription: String? { message }
}
enum MozDates {
    static func string(_ date: Date) -> String {
        let f = ISO8601DateFormatter(); f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f.string(from: date)
    }
    static func parse(_ string: String) -> Date? {
        let f = ISO8601DateFormatter(); f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = f.date(from: string) { return date }
        f.formatOptions = [.withInternetDateTime]; return f.date(from: string)
    }
    static func display(_ string: String?) -> String {
        guard let string, let date = parse(string) else { return "—" }
        return date.formatted(date: .abbreviated, time: .shortened)
    }
}
enum SessionVault {
    private static var query: [String: Any] { [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "net.moz.driver.session", kSecAttrAccount as String: "moz_session"] }
    static func read() -> String? {
        var q = query; q[kSecReturnData as String] = true; q[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        guard SecItemCopyMatching(q as CFDictionary, &result) == errSecSuccess, let data = result as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }
    static func write(_ value: String) throws {
        clear()
        var q = query
        q[kSecValueData as String] = Data(value.utf8)
        q[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        guard SecItemAdd(q as CFDictionary, nil) == errSecSuccess else { throw ApiError(status: 0, message: "Não foi possível proteger a sessão no aparelho.") }
    }
    static func clear() { SecItemDelete(query as CFDictionary) }
}
final class MozAPI {
    static let base = URL(string: "https://moz.runasp.net/api")!
    private let session: URLSession
    init() {
        let c = URLSessionConfiguration.ephemeral
        c.httpShouldSetCookies = false; c.httpCookieStorage = nil; c.urlCache = nil
        c.timeoutIntervalForRequest = 20; c.timeoutIntervalForResource = 45
        session = URLSession(configuration: c, delegate: NoRedirect(), delegateQueue: nil)
    }
    private final class NoRedirect: NSObject, URLSessionTaskDelegate {
        func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) { completionHandler(nil) }
    }
    func request<T: Decodable>(_ path: String, method: String = "GET", body: Data? = nil, contentType: String = "application/json", login: Bool = false) async throws -> T {
        guard let url = URL(string: Self.base.absoluteString + path), url.host == Self.base.host, url.scheme == "https" else { throw ApiError(status: 0, message: "Endereço inválido.") }
        var req = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData)
        req.httpMethod = method; req.httpBody = body
        req.setValue("1", forHTTPHeaderField: "X-MOZ-Request")
        req.setValue("application/json", forHTTPHeaderField: "Accept")
        if body != nil { req.setValue(contentType, forHTTPHeaderField: "Content-Type") }
        if !login, let token = SessionVault.read() { req.setValue("moz_session=" + token, forHTTPHeaderField: "Cookie") }
        let (data, raw) = try await session.data(for: req)
        guard let response = raw as? HTTPURLResponse else { throw ApiError(status: 0, message: "Resposta inválida.") }
        guard (200...299).contains(response.statusCode) else {
            let json = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
            throw ApiError(status: response.statusCode, message: json?["error"] as? String ?? "Falha de comunicação (HTTP \(response.statusCode)).")
        }
        let decoded = try JSONDecoder().decode(T.self, from: data.isEmpty ? Data("{}".utf8) : data)
        if login {
            guard let value = decoded as? DriverAccount, value.role == "driver" else { throw ApiError(status: 403, message: "Acesso exclusivo de motoristas.") }
            let headers = response.allHeaderFields.reduce(into: [String: String]()) { result, entry in result[String(describing: entry.key)] = String(describing: entry.value) }
            guard let token = HTTPCookie.cookies(withResponseHeaderFields: headers, for: url).first(where: { $0.name == "moz_session" })?.value else { throw ApiError(status: 0, message: "Sessão não recebida.") }
            try SessionVault.write(token)
        }
        return decoded
    }
    func post<T: Decodable>(_ path: String, _ value: [String: String], login: Bool = false) async throws -> T {
        try await request(path, method: "POST", body: JSONEncoder().encode(value), login: login)
    }
    func upload(_ entry: QueueEntry, file: Data) async throws -> PodResult {
        let boundary = "moz" + UUID().uuidString
        var body = Data()
        func append(_ text: String) { body.append(Data(text.utf8)) }
        for (key, value) in [("id", entry.id), ("captured", entry.at), ("stageId", entry.stageId ?? "")] {
            append("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(key)\"\r\n\r\n\(value)\r\n")
        }
        append("--\(boundary)\r\nContent-Disposition: form-data; name=\"file\"; filename=\"comprovante.jpg\"\r\nContent-Type: image/jpeg\r\n\r\n")
        body.append(file); append("\r\n--\(boundary)--\r\n")
        return try await request("/trips/\(entry.tripId)/pod", method: "POST", body: body, contentType: "multipart/form-data; boundary=\(boundary)")
    }
}
