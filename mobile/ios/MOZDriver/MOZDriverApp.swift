import SwiftUI
import UIKit
import PhotosUI
import BackgroundTasks

@main
@MainActor
struct MOZDriverApp: App {
    @StateObject private var model = DriverModel()
    @Environment(\.scenePhase) private var phase
    var body: some Scene {
        WindowGroup {
            RootView().environmentObject(model)
                .onAppear { model.scene(phase) }
                .onChange(of: phase) { model.scene($0) }
        }
        .backgroundTask(.appRefresh("net.moz.driver.refresh")) { await model.backgroundRefresh() }
    }
}
struct RootView: View {
    @EnvironmentObject var model: DriverModel
    var body: some View {
        NavigationStack {
            Group {
                if model.user == nil || model.sessionExpired { SignInView() }
                else if model.user?.identityVerified == false {
                    VStack(spacing: 20) {
                        Text("Atualize sua sessão").font(.title2.bold())
                        Text("Sua sessão precisa ser atualizada para consultar as viagens disponíveis.")
                        Button("Atualizar cadastro") { Task { await model.refresh() } }.buttonStyle(.borderedProminent)
                        Button("Sair") { Task { await model.logout() } }
                    }.padding()
                } else { DemandList() }
            }
            .navigationTitle("MOZ Driver")
        }
        .safeAreaInset(edge: .bottom) {
            if !model.message.isEmpty {
                Text(model.message).font(.footnote).padding(12).frame(maxWidth: .infinity)
                    .background(.thinMaterial)
            }
        }
    }
}
struct SignInView: View {
    @EnvironmentObject var model: DriverModel
    @State private var cpf = ""
    @State private var password = ""
    @State private var name = ""
    @State private var phone = ""
    @State private var registering = false
    var body: some View {
        Form {
            Section(registering ? "Cadastro gratuito" : "Acesso do motorista") {
                if model.sessionExpired { Text("Seus dados locais foram preservados. Entre novamente com o mesmo CPF para sincronizar.") }
                if registering { TextField("Nome completo", text: $name).textContentType(.name); TextField("Celular com DDD", text: $phone).keyboardType(.phonePad).textContentType(.telephoneNumber) }
                TextField("CPF", text: $cpf).keyboardType(.numberPad).textContentType(.username)
                SecureField(registering ? "Senha (mínimo 10 caracteres)" : "Senha", text: $password).textContentType(registering ? .newPassword : .password)
                Button(registering ? "Criar conta" : "Entrar") {
                    Task {
                        if registering {
                            if await model.register(name: name, cpf: cpf, phone: phone, password: password) { registering = false; password = "" }
                        } else { await model.login(cpf: cpf, password: password); password = "" }
                    }
                }.disabled(model.busy || cpf.isEmpty || password.isEmpty)
                if !model.sessionExpired { Button(registering ? "Já tenho conta" : "Sou motorista — criar conta") { registering.toggle() } }
                if model.sessionExpired { Button("Sair e interromper GPS", role: .destructive) { Task { await model.logout() } } }
            }
        }
    }
}
struct DemandList: View {
    @EnvironmentObject var model: DriverModel
    @State private var notices = false
    var body: some View {
        List {
            Section {
                Text(model.user?.name ?? "").font(.headline)
                Label(model.collecting ? "GPS em operação" : "GPS sem coleta", systemImage: model.collecting ? "location.fill" : "location.slash")
                if !model.pending.isEmpty { Text("\(model.pending.count) registros aguardando sincronização ou conferência.").font(.footnote) }
                Button("Sincronizar agora") { Task { await model.refresh() } }
            }
            Section("Minhas viagens") {
                let ordered = model.trips.sorted { a, b in
                    if (a.id == model.grant?.tripId) != (b.id == model.grant?.tripId) { return a.id == model.grant?.tripId }
                    return a.scheduled > b.scheduled
                }
                if ordered.isEmpty { Text("Nenhuma viagem disponível.").foregroundStyle(.secondary) }
                ForEach(ordered) { t in
                    NavigationLink { TripScreen(id: t.id) } label: {
                        VStack(alignment: .leading, spacing: 8) {
                            Text(t.reference).font(.headline)
                            Text(model.pending.contains(where: { $0.tripId == t.id && $0.finish }) ? "Encerrada no aparelho · pendente de validação" : t.status)
                            Text("Placa: " + t.tractor + " · " + MozDates.display(t.scheduled)).font(.caption).foregroundStyle(.secondary)
                        }.padding(.vertical, 8)
                    }
                }
            }
        }
        .refreshable { await model.refresh() }
        .toolbar {
            ToolbarItem(placement: .navigationBarLeading) { Button("Notificações (\(model.unread))") { notices = true } }
            ToolbarItem(placement: .navigationBarTrailing) { Button("Sair") { Task { await model.logout() } } }
        }
        .sheet(isPresented: $notices) {
            NavigationStack {
                List(model.notices) { n in
                    NavigationLink { TripScreen(id: n.tripId).task { await model.readNotice(n) } } label: {
                        VStack(alignment: .leading) { Text(n.kind).font(.headline); Text(n.message); Text(MozDates.display(n.at)).font(.caption) }
                    }
                }.navigationTitle("Notificações").toolbar { Button("Fechar") { notices = false } }
            }
        }
    }
}
struct TripScreen: View {
    @EnvironmentObject var model: DriverModel
    let id: String
    @State private var stageId = ""
    @State private var camera = false
    @State private var chosenPhoto: PhotosPickerItem?
    @State private var occurrence = ""
    @State private var cameraStage: DriverStage?
    @State private var cameraError = ""
    var body: some View {
        Group {
            if let d = model.details[id] {
                let t = d.trip
                let authorized = model.grant?.tripId == id && !t.terminal
                let proofQueue = model.pending.filter { $0.tripId == id && $0.fix == nil }
                let localEnd = proofQueue.contains { $0.finish }
                let choices = d.stages.filter { $0.order >= t.currentStage && ($0.requirePod || ($0.order == t.currentStage && $0.arrived != nil)) }
                let selectedStage = choices.first(where: { $0.id == stageId }) ?? choices.first
                List {
                    Section("Viagem") {
                        Text(t.reference).font(.title2.bold())
                        Text(localEnd ? "Encerrada no aparelho · aguardando confirmação" : t.status)
                        Text("Cavalo: " + t.tractor)
                        Text("Carreta: " + t.trailer)
                        Text("Programada: " + MozDates.display(t.scheduled))
                        if !t.terminal && !localEnd { Text(authorized ? "Ativada pelo operador." : "Aguardando ativação pelo operador. GPS desligado.") }
                    }
                    Section("Origem e destino") {
                        ForEach(["origin", "destination"], id: \.self) { role in
                            VStack(alignment: .leading, spacing: 6) {
                                Text(role == "origin" ? "Origem — coleta" : "Destino — entrega").font(.headline)
                                if let stage = d.stages.first(where: { $0.cargoRole == role }) {
                                    Text(stage.label); Text(stage.address.isEmpty ? "Endereço não informado" : stage.address).font(.caption)
                                    if let lat = stage.lat, let lon = stage.lon, let url = URL(string: "https://maps.apple.com/?ll=\(lat),\(lon)") { Link("Abrir localização", destination: url) }
                                } else { Text("Não identificada na programação original.").foregroundStyle(.secondary) }
                            }
                        }
                    }
                    if authorized && !localEnd {
                        if !choices.isEmpty {
                            Section("Comprovante") {
                                Picker("Etapa", selection: Binding(get: { selectedStage?.id ?? "" }, set: { stageId = $0 })) {
                                    ForEach(choices) { Text($0.label).tag($0.id) }
                                }
                                Button {
                                    cameraStage = selectedStage
                                    if UIImagePickerController.isSourceTypeAvailable(.camera) { camera = true } else { cameraError = "Câmera indisponível neste aparelho. Selecione uma foto." }
                                } label: { Label("FOTOGRAFAR COMPROVANTE", systemImage: "camera.fill").frame(maxWidth: .infinity, minHeight: 44) }.buttonStyle(.borderedProminent)
                                PhotosPicker(selection: $chosenPhoto, matching: .images) { Label("Selecionar foto", systemImage: "photo") }
                                Text("A foto será gravada no aparelho, mesmo sem internet. A etapa final configurada para encerrar por comprovante interrompe o GPS local.").font(.caption)
                                if !cameraError.isEmpty { Text(cameraError).font(.caption) }
                            }
                        }
                        Section("Ocorrência") {
                            TextField("Descreva a ocorrência", text: $occurrence)
                            Button("Enviar ocorrência") { let text = occurrence; Task { await model.action(id, "occurrence", message: text) } }.disabled(occurrence.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                        }
                    }
                    if !proofQueue.isEmpty {
                        Section("Comprovantes locais") {
                            ForEach(proofQueue) { p in
                                VStack(alignment: .leading) { Text(MozDates.display(p.at)); Text(p.review ?? "Pendente de sincronização").font(.caption) }
                            }
                        }
                    }
                    Section("Etapas") {
                        ForEach(d.stages.sorted(by: { $0.order < $1.order })) { s in
                            VStack(alignment: .leading, spacing: 4) {
                                Text(s.label).font(.headline); Text(s.address).font(.caption)
                                Text("Chegada: " + MozDates.display(s.arrived)).font(.caption)
                                Text("Saída: " + MozDates.display(s.departed)).font(.caption)
                            }
                        }
                    }
                }
                .sheet(isPresented: $camera) {
                    CameraCapture { image in
                        camera = false
                        if let image, let stage = cameraStage, let data = image.jpegData(compressionQuality: 0.8) { model.saveProof(tripId: id, stage: stage, photo: data) }
                    }.ignoresSafeArea()
                }
                .onChange(of: chosenPhoto) { item in
                    guard let item, let stage = selectedStage else { return }
                    Task {
                        do {
                            guard let raw = try await item.loadTransferable(type: Data.self), let image = UIImage(data: raw), let data = image.jpegData(compressionQuality: 0.8) else { cameraError = "Não foi possível ler a foto."; return }
                            model.saveProof(tripId: id, stage: stage, photo: data)
                        } catch { cameraError = error.localizedDescription }
                        chosenPhoto = nil
                    }
                }
            } else { VStack(spacing: 12) { Text("Dados da viagem ainda não disponíveis neste aparelho."); Button("Atualizar") { Task { await model.loadDetail(id) } } }.padding() }
        }.navigationTitle("Viagem").navigationBarTitleDisplayMode(.inline).task { await model.loadDetail(id) }
    }
}
struct CameraCapture: UIViewControllerRepresentable {
    let complete: (UIImage?) -> Void
    func makeCoordinator() -> Coordinator { Coordinator(complete: complete) }
    func makeUIViewController(context: Context) -> UIImagePickerController {
        let picker = UIImagePickerController()
        picker.sourceType = .camera; picker.delegate = context.coordinator
        return picker
    }
    func updateUIViewController(_ uiViewController: UIImagePickerController, context: Context) {}
    final class Coordinator: NSObject, UIImagePickerControllerDelegate, UINavigationControllerDelegate {
        let complete: (UIImage?) -> Void
        init(complete: @escaping (UIImage?) -> Void) { self.complete = complete }
        func imagePickerControllerDidCancel(_ picker: UIImagePickerController) { complete(nil) }
        func imagePickerController(_ picker: UIImagePickerController, didFinishPickingMediaWithInfo info: [UIImagePickerController.InfoKey: Any]) { complete(info[.originalImage] as? UIImage) }
    }
}
