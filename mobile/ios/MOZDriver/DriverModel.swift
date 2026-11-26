import SwiftUI
import CoreLocation
import UIKit
import BackgroundTasks

@MainActor
final class DriverModel: NSObject, ObservableObject, CLLocationManagerDelegate {
    @Published private(set) var user: DriverAccount?
    @Published private(set) var trips: [DriverTrip] = []
    @Published private(set) var details: [String: TripDetail] = [:]
    @Published private(set) var grant: Grant?
    @Published private(set) var pending: [QueueEntry] = []
    @Published private(set) var notices: [Notices.Entry] = []
    @Published private(set) var unread = 0
    @Published private(set) var collecting = false
    @Published private(set) var sessionExpired = false
    @Published var message = ""
    @Published var busy = false
    private let store = OfflineStore()
    private let api = MozAPI()
    private let location = CLLocationManager()
    private var refreshing = false
    private var flushing = false
    private var lastFix: Date?
    private var lastSync = Date.distantPast
    private var active = false
    private var alwaysRequested = false
    private var generation = UUID()
    private var refreshTask: Task<Void, Never>?
    override init() {
        super.init()
        location.delegate = self
        location.desiredAccuracy = kCLLocationAccuracyBest
        location.distanceFilter = kCLDistanceFilterNone
        location.activityType = .automotiveNavigation
        location.pausesLocationUpdatesAutomatically = false
        location.showsBackgroundLocationIndicator = true
        if let snapshot = store.snapshot(), snapshot.user.role == "driver" {
            user = snapshot.user; trips = snapshot.trips; details = snapshot.details; grant = snapshot.grant
            sessionExpired = SessionVault.read() == nil
            updatePending()
        }
    }
    private func persist() throws {
        guard let user else { return }
        try store.save(DriverSnapshot(user: user, trips: trips, details: details, grant: grant))
    }
    private func updatePending() {
        guard let user else { pending = []; return }
        do { pending = try store.entries(owner: user.id) }
        catch { message = "Não foi possível ler a fila local: " + error.localizedDescription }
    }
    func scene(_ phase: ScenePhase) {
        active = phase == .active
        if active {
            updatePending(); reconcileGPS()
            refreshTask?.cancel()
            refreshTask = Task { [weak self] in
                while !Task.isCancelled {
                    guard let self else { return }
                    await self.refresh()
                    try? await Task.sleep(nanoseconds: 5_000_000_000)
                }
            }
        } else {
            refreshTask?.cancel(); refreshTask = nil
            scheduleBackgroundRefresh()
        }
    }
    func scheduleBackgroundRefresh() {
        guard user != nil else { return }
        let task = BGAppRefreshTaskRequest(identifier: "net.moz.driver.refresh")
        task.earliestBeginDate = Date(timeIntervalSinceNow: 15 * 60)
        try? BGTaskScheduler.shared.submit(task)
    }
    func backgroundRefresh() async {
        defer { scheduleBackgroundRefresh() }
        await refresh()
    }
    func login(cpf: String, password: String) async {
        guard !busy else { return }; busy = true; defer { busy = false }
        do {
            let account: DriverAccount = try await api.post("/login", ["login": cpf, "password": password], login: true)
            if user?.id != account.id { stopGPS(); generation = UUID(); trips = []; details = [:]; grant = nil; notices = []; unread = 0; lastFix = nil }
            user = account; sessionExpired = false
            try persist(); updatePending()
            await refresh()
        } catch { message = error.localizedDescription }
    }
    func register(name: String, cpf: String, phone: String, password: String) async -> Bool {
        guard !busy else { return false }; busy = true; defer { busy = false }
        do {
            let _: EmptyResponse = try await api.post("/register-driver", ["name": name, "cpf": cpf, "phone": phone, "password": password])
            message = "Conta criada e liberada automaticamente. Acesse com seu CPF e senha."; return true
        } catch { message = error.localizedDescription; return false }
    }
    func logout() async {
        stopGPS(); generation = UUID(); grant = nil
        do { let _: EmptyResponse = try await api.post("/logout", [:]) } catch {}
        SessionVault.clear()
        do { try store.clearSnapshot() } catch { message = error.localizedDescription }
        user = nil; trips = []; details = [:]; pending = []; notices = []; unread = 0; sessionExpired = false
        // Queues remain protected and scoped to their original driver.
    }
    private func terminal(_ tripId: String) {
        if grant?.tripId == tripId { stopGPS(); grant = nil; try? persist() }
    }
    private func handle(_ error: Error) {
        if let e = error as? ApiError {
            if e.status == 401 { sessionExpired = true; message = "Sessão expirada. Entre novamente para sincronizar; os registros locais foram preservados."; return }
            if e.status == 403 { stopGPS(); grant = nil; try? persist(); message = "Acesso não autorizado. Dados pendentes preservados."; return }
        }
        if collecting { message = "Sem comunicação com o MOZ. GPS continua gravando no aparelho." }
        else { message = error.localizedDescription }
    }
    func refresh() async {
        guard !refreshing, let owner = user?.id, !sessionExpired else { return }
        refreshing = true; defer { refreshing = false }
        let token = generation
        do {
            let account: DriverAccount = try await api.request("/me")
            guard generation == token, user?.id == owner else { return }
            guard account.id == owner, account.role == "driver" else { stopGPS(); grant = nil; try? persist(); throw ApiError(status: 403, message: "Sessão não corresponde ao motorista.") }
            user = account
            if !account.identityVerified { stopGPS(); grant = nil; try persist(); return }
            let newGrant: Grant = try await api.request("/driver/tracking")
            guard generation == token, user?.id == owner else { return }
            let previous = grant
            if newGrant != previous { stopGPS(); lastFix = nil }
            grant = newGrant.offlineAllowed && newGrant.tripId != nil ? newGrant : nil
            // Persist positive authorization only after the server confirms it.
            try persist(); updatePending(); reconcileGPS()
            if let id = grant?.tripId, details[id]?.trip.trackingSince != grant?.trackingSince {
                if let d: TripDetail = try? await api.request("/trips/\(id)") {
                    guard generation == token, user?.id == owner else { return }
                    details[id] = d; if let i = trips.firstIndex(where: { $0.id == id }) { trips[i] = d.trip } else { trips.insert(d.trip, at: 0) }; try persist()
                }
            }
            await flush()
            guard generation == token, user?.id == owner else { return }
            if active {
                var all: [DriverTrip] = [], page = 0, total = 0
                repeat {
                    let result: TripPage = try await api.request("/trips?page=\(page)")
                    guard generation == token, user?.id == owner else { return }
                    all.append(contentsOf: result.items); total = result.total; page += 1
                    if result.items.isEmpty { break }
                } while all.count < total && page < 100
                trips = all
                if let id = grant?.tripId {
                    let detail: TripDetail = try await api.request("/trips/\(id)")
                    guard generation == token, user?.id == owner else { return }
                    details[id] = detail
                }
                let n: Notices = try await api.request("/notices")
                guard generation == token, user?.id == owner else { return }
                notices = n.items; unread = n.unread
                try persist()
            }
            lastSync = Date()
            message = collecting ? "GPS ativo · sincronização automática." : pending.contains(where: { $0.finish }) ? "Encerramento local · aguardando confirmação." : "Dados atualizados."
        } catch { if generation == token { handle(error) } }
    }
    func loadDetail(_ id: String) async {
        let token = generation
        do {
            let result: TripDetail = try await api.request("/trips/\(id)")
            guard generation == token else { return }
            details[id] = result; try persist()
        } catch { if generation == token && details[id] == nil { handle(error) } }
    }
    func readNotice(_ notice: Notices.Entry) async {
        do { let _: EmptyResponse = try await api.post("/notices/\(notice.id)/read", [:]); await refresh() }
        catch { handle(error) }
    }
    func action(_ id: String, _ action: String, message: String = "") async {
        guard grant?.tripId == id, !pending.contains(where: { $0.tripId == id && $0.finish }) else { return }
        do {
            let _: EmptyResponse = try await api.post("/trips/\(id)/action", ["action": action, "message": message])
            await loadDetail(id)
        } catch { handle(error) }
    }
    func reconcileGPS() {
        guard let user, user.identityVerified, let g = grant, g.offlineAllowed, let id = g.tripId, g.trackingSince != nil else { stopGPS(); return }
        do { if try store.pendingFinish(owner: user.id, trip: id) { stopGPS(); return } }
        catch { stopGPS(); message = error.localizedDescription; return }
        switch location.authorizationStatus {
        case .notDetermined: if active { location.requestWhenInUseAuthorization() }
        case .denied, .restricted: stopGPS(); message = "Localização não autorizada. Altere a permissão nos Ajustes do iPhone."
        case .authorizedWhenInUse, .authorizedAlways:
            if active && location.authorizationStatus == .authorizedWhenInUse && !alwaysRequested { alwaysRequested = true; location.requestAlwaysAuthorization() }
            if collecting { return }
            guard active else { return }
            location.allowsBackgroundLocationUpdates = true
            collecting = true; location.startUpdatingLocation()
        @unknown default: stopGPS()
        }
    }
    private func stopGPS() {
        collecting = false; location.stopUpdatingLocation()
    }
    func locationManagerDidChangeAuthorization(_ manager: CLLocationManager) { reconcileGPS() }
    func locationManager(_ manager: CLLocationManager, didFailWithError error: Error) { message = "GPS indisponível: " + error.localizedDescription }
    func locationManager(_ manager: CLLocationManager, didUpdateLocations locations: [CLLocation]) {
        guard collecting, let user, let g = grant, let trip = g.tripId, let epoch = g.trackingSince, let start = MozDates.parse(epoch) else { return }
        for l in locations.sorted(by: { $0.timestamp < $1.timestamp }) {
            guard l.timestamp >= start, l.horizontalAccuracy >= 0, CLLocationCoordinate2DIsValid(l.coordinate), lastFix.map({ l.timestamp.timeIntervalSince($0) >= 5 }) ?? true else { continue }
            let id = UUID().uuidString.lowercased(), at = MozDates.string(l.timestamp)
            let fix = FixPayload(id: id, at: at, lat: l.coordinate.latitude, lon: l.coordinate.longitude, accuracy: l.horizontalAccuracy, speed: l.speed >= 0 ? l.speed * 3.6 : nil, trackingSince: epoch)
            do {
                try store.enqueue(QueueEntry(id: id, owner: user.id, tripId: trip, at: at, fix: fix, stageId: nil, finish: false, review: nil))
                lastFix = l.timestamp
            } catch { message = "Falha ao gravar GPS. " + error.localizedDescription; stopGPS(); return }
        }
        if Date().timeIntervalSince(lastSync) >= 15 {
            lastSync = Date()
            Task { await refresh() }
        }
    }
    func saveProof(tripId: String, stage: DriverStage, photo: Data) {
        guard let user, grant?.tripId == tripId, let detail = details[tripId], detail.stages.contains(where: { $0.id == stage.id }), !pending.contains(where: { $0.tripId == tripId && $0.finish }) else { message = "Viagem ou etapa não autorizada."; return }
        let finish = stage.finishOnPod && stage.order == detail.stages.map(\.order).max()
        let item = QueueEntry(id: UUID().uuidString.lowercased(), owner: user.id, tripId: tripId, at: MozDates.string(Date()), fix: nil, stageId: stage.id, finish: finish, review: nil)
        do {
            try store.enqueue(item, photo: photo); updatePending()
            if finish { stopGPS() }
            message = finish ? "Encerrada no aparelho. Aguardando validação e sincronização do comprovante." : "Comprovante salvo no aparelho."
            scheduleBackgroundRefresh()
            Task { await flush() }
        } catch { message = error.localizedDescription }
    }
    func flush() async {
        guard !flushing, let owner = user?.id, !sessionExpired else { return }
        flushing = true; defer { flushing = false; updatePending() }
        let token = generation
        do {
            // A background time allowance permits in-flight synchronization, not an unlimited background execution promise.
            let task = UIApplication.shared.beginBackgroundTask(withName: "MOZ synchronization", expirationHandler: nil)
            defer { if task != .invalid { UIApplication.shared.endBackgroundTask(task) } }
            let entries = try store.entries(owner: owner).filter { $0.review == nil }
            var index = 0
            while index < entries.count && generation == token && !Task.isCancelled {
                let e = entries[index]
                if let fix = e.fix {
                    var batch = [e]; var fixes = [fix]
                    while index + batch.count < entries.count && batch.count < 100 {
                        let next = entries[index + batch.count]
                        guard next.tripId == e.tripId, let f = next.fix else { break }
                        batch.append(next); fixes.append(f)
                    }
                    let result: FixResult = try await api.request("/trips/\(e.tripId)/fixes", method: "POST", body: JSONEncoder().encode(fixes))
                    guard generation == token else { return }
                    let acknowledged = Set((result.acceptedIds + result.discardIds).map { $0.lowercased() })
                    for entry in batch where acknowledged.contains(entry.id.lowercased()) { try store.remove(entry) }
                    if !result.trackingActive { terminal(e.tripId) }
                    if batch.contains(where: { !acknowledged.contains($0.id.lowercased()) }) { break }
                    index += batch.count
                } else {
                    do {
                        let result = try await api.upload(e, file: store.photo(e.id))
                        guard generation == token else { return }
                        if e.finish && result.status != "Finalizada" {
                            try store.markReview(e, "Comprovante recebido; encerramento requer conferência.")
                            stopGPS()
                        } else { try store.remove(e) }
                        if result.status == "Finalizada" || result.status == "Cancelada" { terminal(e.tripId) }
                    } catch let error as ApiError where error.status == 400 || error.status == 404 {
                        try store.markReview(e, error.message)
                        if e.finish { stopGPS() }
                    }
                    index += 1
                }
            }
        } catch { if generation == token { handle(error) } }
    }
}
