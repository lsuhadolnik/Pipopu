import SwiftUI
import UniformTypeIdentifiers

@main
struct PipopuApp: App {
    var body: some Scene {
        WindowGroup { ReceiverView() }
    }
}

private struct ReceiverView: View {
    @Environment(\.scenePhase) private var phase
    @StateObject private var bluetooth = Bluetooth()
    @AppStorage("receiverName") private var receiverName = ""
    @AppStorage("saveReceivedFiles") private var saveReceivedFiles = false
    @AppStorage("receiveEnabled") private var listening = true
    @AppStorage("hasFinishedOnboarding") private var hasFinishedOnboarding = false
    @State private var showingSetup = false
    @State private var tab = Tab.send
    @State private var unreadReceipt = false

    private enum Tab { case send, activity, settings }
    @State private var exporting = false
    @State private var status = ""
    @State private var fileExport: ExportFile?
    @State private var latestReceipt: UUID?
    @State private var draft = ""
    @State private var attachment: Transfer?
    @State private var importing = false
    @State private var recipient: UUID?
    @State private var fileSend: FileSend?
    @State private var sendStatus = ""
    @State private var sendAttempt: UUID?
    @State private var finishedAttempt: UUID?
    @FocusState private var editingDraft: Bool

    private let accent = Color(red: 0.48, green: 0.32, blue: 0.89)

    var body: some View {
        NavigationStack {
            if let fileSend {
                FileSendView(file: fileSend, bluetooth: bluetooth, tint: accent) { self.fileSend = nil }
                    .toolbar(.hidden, for: .navigationBar)
            } else {
                tabs
            }
        }
        .onAppear {
            if !hasFinishedOnboarding { showingSetup = true }
            updateListening()
        }
        .onChange(of: hasFinishedOnboarding) { _, _ in updateListening() }
        .onChange(of: tab) { _, value in
            editingDraft = false
            if value == .activity { unreadReceipt = false }
        }
        .sheet(isPresented: $showingSetup) {
            SetupView(tint: accent) {
                hasFinishedOnboarding = true
                showingSetup = false
            }
            .interactiveDismissDisabled(!hasFinishedOnboarding)
        }
        .onChange(of: phase) { _, _ in updateListening() }
        .onChange(of: listening) { _, _ in updateListening() }
        .onChange(of: draft) { _, _ in sendStatus = "" }
        .onReceive(bluetooth.$received, perform: receive)
        .sensoryFeedback(.impact(weight: .light), trigger: sendAttempt)
        .sensoryFeedback(trigger: finishedAttempt) { _, _ in
            bluetooth.sendCompleted ? .success : .error
        }
        .task(id: receiverName) {
            try? await Task.sleep(for: .milliseconds(350))
            guard !Task.isCancelled else { return }
            updateListening()
        }
        .fileImporter(isPresented: $importing, allowedContentTypes: [.item]) { chooseFile($0) }
        .alert("Trust this device?", isPresented: Binding(get: { bluetooth.pairing != nil }, set: { _ in }),
               presenting: bluetooth.pairing) { _ in
            Button("Trust device") { bluetooth.answerPairing(true) }
            Button("Decline", role: .cancel) { bluetooth.answerPairing(false) }
        } message: { request in
            Text("Only trust \(request.name) if it shows code \(request.code).")
        }
        .sheet(item: $fileExport) { file in
            LargeFileExporter(file: file)
        }
        .fileExporter(
            isPresented: $exporting,
            document: bluetooth.received.map { ReceivedDocument(data: $0.data) },
            contentType: exportType,
            defaultFilename: exportName
        ) { result in
            switch result {
            case .success: status = "Saved"
            case .failure(let error): status = error.localizedDescription
            }
        }
    }

    private var tabs: some View {
        TabView(selection: $tab) {
            List {
                sendHeader
                    .listRowBackground(Color.clear)
                    .listRowInsets(EdgeInsets())
                composer
                recipients
            }
            .listStyle(.insetGrouped)
            .scrollDismissesKeyboard(.interactively)
            .tabItem { Label("Send", systemImage: "arrow.up.right") }
            .tag(Tab.send)

            activity
                .tabItem { Label("Activity", systemImage: "clock") }
                .badge(unreadReceipt ? 1 : 0)
                .tag(Tab.activity)

            settings
                .tabItem { Label("Settings", systemImage: "gearshape") }
                .tag(Tab.settings)
        }
        .toolbar(.hidden, for: .navigationBar)
        .tint(accent)
    }

    private var sendHeader: some View {
        HStack(spacing: 14) {
            Image("PipopuMark")
                .resizable()
                .frame(width: 52, height: 52)
                .clipShape(RoundedRectangle(cornerRadius: 14))
                .shadow(color: accent.opacity(0.3), radius: 12, y: 4)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 2) {
                Text("Pipopu").font(.caption.weight(.semibold)).foregroundStyle(.secondary)
                Text("Send").font(.largeTitle.weight(.bold)).accessibilityAddTraits(.isHeader)
            }
        }
        .padding(.vertical, 8)
    }

    private var activity: some View {
        List {
            screenHeading("Activity")
            if let transfer = bluetooth.received {
                Section("Received") {
                    if transfer.kind == .text {
                        if transfer.secure {
                            Button("Copy secure text", systemImage: "lock.doc") {
                                UIPasteboard.general.setItems(
                                    [[UTType.utf8PlainText.identifier: transfer.text]], options: [.localOnly: true])
                                status = "Copied secure text"
                            }
                        } else {
                            Text(transfer.text).textSelection(.enabled)
                            Button("Copy text", systemImage: "doc.on.doc") {
                                UIPasteboard.general.string = transfer.text
                                status = "Copied text"
                            }
                        }
                    } else {
                        if transfer.kind == .image, let image = imagePreview(transfer) {
                            Image(uiImage: image).resizable().scaledToFit().frame(maxHeight: 240)
                            Button("Copy image", systemImage: "doc.on.doc") {
                                UIPasteboard.general.image = image
                                status = "Copied image"
                            }
                        }
                        Button("Save " + (transfer.name.isEmpty ? "item" : transfer.name), systemImage: "square.and.arrow.down") {
                            if let file = transfer.file {
                                do { fileExport = try ExportFile(file, name: exportName) }
                                catch { status = error.localizedDescription }
                            } else {
                                exporting = true
                            }
                        }
                    }
                    Button("Clear", role: .destructive) {
                        bluetooth.clearReceived()
                        status = ""
                    }
                }
            }
            if bluetooth.received == nil {
                Section("Received") {
                    Label("No recent transfers", systemImage: "tray")
                        .foregroundStyle(.secondary)
                }
            }
            if !status.isEmpty { Text(status).foregroundStyle(.secondary) }
            if let error = bluetooth.receiveError { Text(error).foregroundStyle(.secondary) }
            cloudTransfers
        }
        .listStyle(.insetGrouped)
    }

    private var settings: some View {
        List {
            screenHeading("Settings")
            Section {
                VStack(alignment: .leading, spacing: 5) {
                    Text("Device name").font(.caption.weight(.semibold)).foregroundStyle(.secondary)
                    TextField("Device name", text: $receiverName, prompt: Text(UIDevice.current.model))
                        .textInputAutocapitalization(.words)
                        .onSubmit { updateListening() }
                }
                .padding(.vertical, 3)
                Toggle("Available to receive", isOn: $listening)
                Toggle("Save received files automatically", isOn: $saveReceivedFiles)
            } header: {
                Text("Receiving")
            } footer: {
                Text("Keep Pipopu open to receive. Automatically saved files appear in On My iPhone or iPad → Pipopu → Received in Files.")
            }
            Section("Trusted devices") {
                if bluetooth.trustedDevices.isEmpty {
                    Text("No trusted devices yet.").foregroundStyle(.secondary)
                }
                ForEach(bluetooth.trustedDevices) { peer in
                    LabeledContent(peer.name) {
                        Button("Forget", role: .destructive) { bluetooth.forget(peer) }
                            .buttonStyle(.borderless)
                    }
                }
            }
            Section("Setup") {
                Button("Setup guide", systemImage: "sparkles") { showingSetup = true }
                Link(destination: SetupView.downloadURL) {
                    Label("Download for Windows", systemImage: "arrow.down.circle")
                }
            }
            if let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String {
                Section("About") {
                    let build = Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String
                    LabeledContent("Version", value: build.map { "\(version) (\($0))" } ?? version)
                }
            }
        }
        .listStyle(.insetGrouped)
    }

    private func screenHeading(_ title: String) -> some View {
        Text(title)
            .font(.largeTitle.weight(.bold))
            .accessibilityAddTraits(.isHeader)
            .listRowBackground(Color.clear)
            .listRowInsets(EdgeInsets())
    }

    private var composer: some View {
        Section("Message or file") {
            if let attachment {
                HStack {
                    Label {
                        VStack(alignment: .leading, spacing: 3) {
                            Text(attachment.name).lineLimit(1)
                            Text(ByteCountFormatter.string(fromByteCount: attachment.size, countStyle: .file))
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }
                    } icon: {
                        Image(systemName: attachment.kind == .image ? "photo" : "doc")
                            .foregroundStyle(accent)
                    }
                    Spacer()
                    Button {
                        self.attachment = nil
                        sendStatus = ""
                    } label: {
                        Image(systemName: "xmark.circle.fill")
                            .foregroundStyle(.secondary)
                    }
                    .buttonStyle(.borderless)
                    .accessibilityLabel("Remove file")
                }
            } else {
                ZStack(alignment: .topLeading) {
                    if draft.isEmpty {
                        Text("Type or paste a message…")
                            .foregroundStyle(.secondary)
                            .padding(.top, 8)
                            .padding(.leading, 5)
                            .allowsHitTesting(false)
                            .accessibilityHidden(true)
                    }
                    TextEditor(text: $draft)
                        .scrollContentBackground(.hidden)
                        .focused($editingDraft)
                        .accessibilityLabel("Message to send")
                }
                .frame(minHeight: 152)
                .padding(.vertical, 4)
            }
            HStack(spacing: 16) {
                PasteButton(payloadType: String.self) { texts in
                    guard let text = texts.first else { return }
                    attachment = nil
                    draft = text
                    sendStatus = ""
                }
                Spacer()
                Button("Choose file", systemImage: "paperclip") {
                    editingDraft = false
                    importing = true
                }
                .buttonStyle(.borderless)
            }
            .buttonStyle(.borderless)
        }
        .disabled(bluetooth.progress != nil)
    }

    private var recipients: some View {
        Section("Nearby devices") {
            if bluetooth.peers.isEmpty {
                Label(bluetooth.state, systemImage: "dot.radiowaves.left.and.right")
                    .foregroundStyle(.secondary)
                Button("Set up another device", systemImage: "arrow.up.right.square") { showingSetup = true }
                    .buttonStyle(.borderless)
            }
            ForEach(bluetooth.peers) { peer in
                HStack(spacing: 12) {
                Button {
                    send(to: peer)
                } label: {
                    HStack(spacing: 12) {
                        Image(systemName: "desktopcomputer")
                            .foregroundStyle(accent)
                            .frame(width: 36, height: 36)
                            .background(accent.opacity(0.12), in: Circle())
                        Text(peer.name)
                            .foregroundStyle(.primary)
                        Spacer()
                        if recipient == peer.id, let progress = bluetooth.progress {
                            ProgressView(value: progress)
                                .frame(width: 54)
                                .accessibilityLabel("Sending")
                        } else {
                            Image(systemName: "arrow.up.right")
                                .font(.footnote.weight(.semibold))
                        }
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.borderless)
                .disabled(!peer.connected || (draft.isEmpty && attachment == nil) || bluetooth.progress != nil)
                .accessibilityHint("Sends the selected text or file")
                    Menu {
                        Button("Send securely", systemImage: "lock") { sendSecurely(to: peer) }
                        Button("Send over cloud", systemImage: "icloud.and.arrow.up") { send(to: peer, overCloud: true) }
                            .disabled(draft.isEmpty && attachment == nil)
                    } label: {
                        Image(systemName: "ellipsis.circle").accessibilityLabel("Send options")
                    }
                    .buttonStyle(.borderless)
                    .disabled(!peer.connected || !peer.supportsCloudAndSecure || bluetooth.progress != nil)
                }
            }
            if !sendStatus.isEmpty {
                Text(sendStatus).font(.footnote).foregroundStyle(.secondary)
            }
        }
    }

    private var cloudTransfers: some View {
        Section("Cloud transfers") {
            Button("Check cloud transfers", systemImage: "arrow.clockwise") { bluetooth.refreshCloud() }
            ForEach(bluetooth.cloudNotices) { notice in
                HStack {
                    VStack(alignment: .leading, spacing: 3) {
                        Text(notice.name.isEmpty ? "Text" : notice.name)
                        Text(bluetooth.hasCloudFile(notice) ? "Downloaded · \(notice.peer)" :
                                "\(notice.status) · \(notice.peer)")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                        if let error = notice.error {
                            Text(error).font(.caption).foregroundStyle(.secondary)
                        }
                    }
                    Spacer()
                    if bluetooth.hasCloudFile(notice) || (!notice.outgoing && notice.status == "Ready") {
                        Button(bluetooth.hasCloudFile(notice) ? "Open" : "Download") { bluetooth.getCloudFile(notice) }
                            .buttonStyle(.borderless)
                    }
                }
            }
            if let message = bluetooth.cloudStatus {
                Text(message).font(.footnote).foregroundStyle(.secondary)
            }
        }
    }

    private func chooseFile(_ selection: Result<URL, Error>) {
        do {
            let url = try selection.get()
            guard let file = try Transfer.loadFile(at: url) else { throw TransferError.invalid }
            let type = UTType(filenameExtension: url.pathExtension)
            if type == .png || type == .jpeg {
                attachment = try Transfer(kind: .image, name: file.name, data: file.data, file: file.file)
            } else {
                attachment = file
            }
            sendStatus = ""
        } catch {
            sendStatus = error.localizedDescription
        }
    }

    private func send(to peer: Bluetooth.Peer, overCloud: Bool = false) {
        do {
            let transfer = try attachment ?? Transfer(kind: .text, data: Data(draft.utf8))
            send(transfer, to: peer, overCloud: overCloud)
        } catch {
            sendStatus = error.localizedDescription
        }
    }

    private func sendSecurely(to peer: Bluetooth.Peer) {
        guard let text = UIPasteboard.general.string, !text.isEmpty else {
            sendStatus = "Copy the text you want to send securely first."
            return
        }
        do {
            let transfer = try Transfer(kind: .text, data: Data(text.utf8), secure: true)
            send(transfer, to: peer, overCloud: false)
        } catch {
            sendStatus = "Secure clipboard text must be 500 KB or smaller."
        }
    }

    private func send(_ transfer: Transfer, to peer: Bluetooth.Peer, overCloud: Bool) {
        editingDraft = false
        sendStatus = ""
        recipient = peer.id
        if transfer.kind != .text || overCloud {
            fileSend = FileSend(name: transfer.kind == .text ? "Message" : transfer.name,
                                size: transfer.size, recipient: peer.name)
        }
        let attempt = UUID()
        sendAttempt = attempt
        bluetooth.send(transfer, to: peer.id, overCloud: overCloud) {
            [sendStatus = $sendStatus, finishedAttempt = $finishedAttempt, weak bluetooth] in
            sendStatus.wrappedValue = bluetooth?.result ?? ""
            finishedAttempt.wrappedValue = attempt
        }
    }

    private var exportName: String {
        ReceivedFileArchive.name(bluetooth.received?.name ?? "")
    }

    private var exportType: UTType {
        UTType(filenameExtension: (exportName as NSString).pathExtension) ?? .data
    }

    private func imagePreview(_ transfer: Transfer) -> UIImage? {
        if let file = transfer.file {
            guard file.size <= 8_000_000 else { return nil }
            return UIImage(contentsOfFile: file.url.path)
        }
        return UIImage(data: transfer.data)
    }

    private func receive(_ transfer: Transfer?) {
        guard latestReceipt != transfer?.id else { return }
        latestReceipt = transfer?.id
        if transfer != nil && tab != .activity { unreadReceipt = true }
        if transfer == nil { unreadReceipt = false }
        status = ""
        guard saveReceivedFiles, let transfer, transfer.kind != .text, !transfer.replay else { return }

        status = "Saving automatically…"
        Task {
            let result = await Task.detached(priority: .utility) {
                do {
                    try ReceivedFileArchive.save(transfer)
                    return "Saved to Files."
                } catch {
                    return "Automatic save failed. " + error.localizedDescription
                }
            }.value
            if latestReceipt == transfer.id {
                status = result
            }
        }
    }

    private var displayName: String {
        let label = receiverName.trimmingCharacters(in: .whitespacesAndNewlines)
        return label.isEmpty ? UIDevice.current.model : label
    }

    private func updateListening() {
        guard hasFinishedOnboarding else { return }
        if phase == .active {
            let name = listening ? displayName : nil
            bluetooth.start(receivingAs: name)
            bluetooth.updateReceivingName(name)
        } else if phase == .background {
            bluetooth.stop()
        }
    }
}

private enum ReceivedFileArchive {
    static func name(_ original: String) -> String {
        let path = original.replacingOccurrences(of: "\\", with: "/")
        let basename = path.split(separator: "/").last.map(String.init) ?? ""
        let clean = basename.unicodeScalars.map { CharacterSet.controlCharacters.contains($0) ? "_" : String($0) }
            .joined().trimmingCharacters(in: CharacterSet.whitespacesAndNewlines.union(CharacterSet(charactersIn: ".")))
        return clean.isEmpty ? "Pipopu transfer" : clean
    }

    static func save(_ transfer: Transfer) throws {
        let files = FileManager.default
        let documents = try files.url(for: .documentDirectory, in: .userDomainMask, appropriateFor: nil, create: true)
        let folder = documents.appendingPathComponent("Received", isDirectory: true)
        try files.createDirectory(at: folder, withIntermediateDirectories: true)
        let staging = folder.appendingPathComponent(".pipopu-" + UUID().uuidString)
        defer { try? files.removeItem(at: staging) }
        if let file = transfer.file {
            try files.copyItem(at: file.url, to: staging)
        } else {
            try transfer.data.write(to: staging)
        }

        let original = name(transfer.name)
        let originalExt = (original as NSString).pathExtension
        let ext = originalExt.utf8.count <= 32 ? originalExt : ""
        let stem = ext.isEmpty ? original : (original as NSString).deletingPathExtension
        var number = 1
        while true {
            let suffix = number == 1 ? "" : " (" + String(number) + ")"
            let ending = suffix + (ext.isEmpty ? "" : "." + ext)
            var filename = stem
            while filename.utf8.count + ending.utf8.count > 255 && filename.count > 1 {
                filename.removeLast()
            }
            let destination = folder.appendingPathComponent(filename + ending)
            do {
                try files.moveItem(at: staging, to: destination)
                return
            } catch {
                guard files.fileExists(atPath: destination.path) else { throw error }
                number += 1
            }
        }
    }
}

private final class ExportFile: Identifiable {
    let id = UUID()
    let url: URL
    private let folder: URL

    init(_ file: TemporaryFile, name: String) throws {
        folder = FileManager.default.temporaryDirectory.appendingPathComponent("pipopu-export-" + id.uuidString)
        url = folder.appendingPathComponent(name)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: false)
        do { try FileManager.default.linkItem(at: file.url, to: url) }
        catch {
            try? FileManager.default.removeItem(at: folder)
            throw error
        }
    }

    deinit { try? FileManager.default.removeItem(at: folder) }
}

private struct LargeFileExporter: UIViewControllerRepresentable {
    let file: ExportFile

    func makeUIViewController(context: Context) -> UIDocumentPickerViewController {
        UIDocumentPickerViewController(forExporting: [file.url], asCopy: true)
    }

    func updateUIViewController(_ controller: UIDocumentPickerViewController, context: Context) {}
}

private struct FileSend {
    let name: String
    let size: Int64
    let recipient: String
}

private struct SetupView: View {
    static let downloadURL = URL(string: "https://github.com/lsuhadolnik/Pipopu/releases/latest")!

    let tint: Color
    let finish: () -> Void

    var body: some View {
        GeometryReader { geometry in
            ScrollView {
                VStack(alignment: .leading, spacing: 24) {
                    Image("PipopuMark")
                        .resizable()
                        .frame(width: 76, height: 76)
                        .clipShape(RoundedRectangle(cornerRadius: 20))
                        .shadow(color: tint.opacity(0.38), radius: 24, y: 6)
                        .accessibilityHidden(true)

                    Text("Welcome to Pipopu")
                        .font(.largeTitle.weight(.bold))
                        .accessibilityAddTraits(.isHeader)

                    step("Install Pipopu on your other device", icon: "arrow.down.circle", detail:
                        "Download the Windows app and open it on the device you want to send to.")
                    step("Connect and send", icon: "dot.radiowaves.left.and.right", detail:
                        "Turn on Bluetooth on both devices. When you first send, check that the pairing codes match.")
                    Link(destination: Self.downloadURL) {
                        Label("Download for Windows", systemImage: "arrow.up.right")
                            .fontWeight(.semibold)
                    }
                    .buttonStyle(.bordered)

                    Button(action: finish) {
                        Text("Continue").fontWeight(.semibold).frame(maxWidth: .infinity)
                    }
                    .buttonStyle(.borderedProminent)
                    .controlSize(.large)
                }
                .frame(maxWidth: 420, alignment: .leading)
                .padding(.horizontal, 28)
                .padding(.vertical, 32)
                .frame(maxWidth: .infinity, minHeight: geometry.size.height)
            }
        }
        .background {
            TransferBackdrop(tint: tint, secondary: .blue, animated: false)
        }
        .tint(tint)
    }

    private func step(_ title: String, icon: String, detail: String) -> some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: icon)
                .foregroundStyle(tint)
                .frame(width: 32, height: 32)
                .background(tint.opacity(0.12), in: RoundedRectangle(cornerRadius: 9))
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 4) {
                Text(title).font(.headline)
                Text(detail).font(.subheadline).foregroundStyle(.secondary)
            }
        }
    }
}

private struct FileSendView: View {
    let file: FileSend
    @ObservedObject var bluetooth: Bluetooth
    let tint: Color
    let close: () -> Void
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var cancelling = false
    @ScaledMetric(relativeTo: .title) private var ringSize = 192.0

    private var active: Bool { bluetooth.sendStage != nil }
    private var failed: Bool { !active && !bluetooth.sendCompleted && !cancelling }
    private var fraction: Double? { cancelling ? nil : bluetooth.sendFraction }
    private var highlight: Color { failed ? .orange : (bluetooth.sendCompleted ? .green : tint) }
    private var title: String {
        if cancelling && active { return "Canceling transfer…" }
        if let stage = bluetooth.sendStage { return stage.label }
        if cancelling { return "Transfer canceled" }
        if bluetooth.sendCompleted { return bluetooth.sendQueued ? "Uploaded to OneDrive" : "Transfer complete" }
        return "Transfer failed"
    }

    var body: some View {
        GeometryReader { geometry in
            ScrollView {
                VStack(spacing: 23) {
                    fileDetails
                    ring
                    transferDetails
                    transferAction
                }
                .multilineTextAlignment(.center)
                .frame(maxWidth: 410)
                .padding(.horizontal, 25)
                .padding(.vertical, 26)
                .frame(maxWidth: .infinity, minHeight: geometry.size.height)
            }
            .scrollBounceBehavior(.basedOnSize)
        }
        .background {
            TransferBackdrop(tint: highlight, secondary: .indigo, animated: active && !reduceMotion)
        }
        .tint(tint)
    }

    private var fileDetails: some View {
        VStack(spacing: 7) {
            Text(file.name.isEmpty ? "Shared file" : file.name)
                .font(.title2.weight(.semibold))
                .lineLimit(3)
            Text(ByteCountFormatter.string(fromByteCount: file.size, countStyle: .file))
                .font(.subheadline.monospacedDigit())
                .foregroundStyle(.secondary)
            VStack(spacing: 4) {
                Text("Sending to").font(.caption).foregroundStyle(.secondary)
                Label(file.recipient, systemImage: "desktopcomputer")
                    .font(.subheadline.weight(.medium))
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 9)
            .background(.thinMaterial, in: RoundedRectangle(cornerRadius: 14))
            .padding(.top, 4)
            .accessibilityElement(children: .combine)
        }
    }

    private var transferDetails: some View {
        VStack(spacing: 9) {
            Text(title)
                .font(.title3.weight(.semibold))
                .accessibilityAddTraits(.isHeader)
            if !cancelling, let speed = bluetooth.uploadSpeedLabel,
               let bytes = bluetooth.uploadByteLabel(total: file.size) {
                ViewThatFits(in: .horizontal) {
                    HStack(spacing: 12) {
                        Label(speed, systemImage: "speedometer")
                        Text(bytes)
                    }
                    VStack(spacing: 4) {
                        Label(speed, systemImage: "speedometer")
                        Text(bytes)
                    }
                }
                .font(.subheadline.monospacedDigit())
                .foregroundStyle(.secondary)
            }
            if bluetooth.sendStage == .pairing, let code = bluetooth.pairingCode {
                Text(code).font(.title3.monospacedDigit()).textSelection(.enabled)
                    .accessibilityLabel("Pairing code \(code)")
            }
            if failed {
                VStack(alignment: .leading, spacing: 6) {
                    if let stage = bluetooth.sendFailureStage {
                        Text(stage.label.trimmingCharacters(in: CharacterSet(charactersIn: "…")))
                            .font(.subheadline.weight(.semibold))
                    }
                    Text(bluetooth.result ?? "The other device did not confirm the transfer.")
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                        .textSelection(.enabled)
                }
                .multilineTextAlignment(.leading)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(14)
                .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 16))
            } else if bluetooth.sendQueued && bluetooth.sendCompleted, let result = bluetooth.result {
                Text(result).font(.subheadline).foregroundStyle(.secondary)
            }
        }
    }

    private var transferAction: some View {
        Group {
            if active {
                Button("Cancel transfer", role: .cancel) {
                    cancelling = true
                    bluetooth.cancelSend()
                }
                .buttonStyle(.bordered)
                .disabled(cancelling)
            } else {
                Button(bluetooth.sendCompleted ? "Done" : "Back to Send", action: close)
                    .buttonStyle(.borderedProminent)
            }
        }
        .controlSize(.large)
    }

    private var ring: some View {
        ZStack {
            Circle()
                .fill(.thinMaterial)
                .padding(7)
            Circle()
                .strokeBorder(highlight.opacity(0.23), lineWidth: 7)
            if let fraction {
                Circle()
                    .trim(from: 0, to: fraction)
                    .stroke(AngularGradient(colors: [tint, .cyan, tint], center: .center),
                            style: StrokeStyle(lineWidth: 7, lineCap: .round))
                    .rotationEffect(.degrees(-90))
                    .padding(3.5)
                    .animation(reduceMotion ? nil : .easeOut(duration: 0.25), value: fraction)
                VStack(spacing: 5) {
                    Image(systemName: "arrow.up").font(.title3.weight(.semibold))
                    Text(fraction, format: .percent.precision(.fractionLength(0)))
                        .font(.largeTitle.monospacedDigit().weight(.semibold))
                }
            } else if active {
                ProgressView().controlSize(.large).tint(highlight)
            } else {
                Image(systemName: bluetooth.sendCompleted ? "checkmark" : (cancelling ? "xmark" : "exclamationmark"))
                    .font(.system(size: 44, weight: .semibold))
            }
        }
        .foregroundStyle(highlight)
        .frame(width: min(ringSize, 240), height: min(ringSize, 240))
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("Transfer progress")
        .accessibilityValue(fraction?.formatted(.percent.precision(.fractionLength(0))) ?? title)
    }
}

private struct TransferBackdrop: View {
    let tint: Color
    let secondary: Color
    let animated: Bool

    var body: some View {
        GeometryReader { geometry in
            TimelineView(.animation(minimumInterval: 1.0 / 20, paused: !animated)) { timeline in
                let phase = animated ? timeline.date.timeIntervalSinceReferenceDate / 7 : 0
                ZStack {
                    Color(uiColor: .systemBackground)
                    Ellipse()
                        .fill(tint.opacity(0.26))
                        .frame(width: geometry.size.width * 0.96, height: geometry.size.height * 0.45)
                        .blur(radius: 78)
                        .position(x: geometry.size.width * CGFloat(0.24 + 0.08 * cos(phase)),
                                  y: geometry.size.height * CGFloat(0.35 + 0.07 * sin(phase)))
                    Ellipse()
                        .fill(secondary.opacity(0.18))
                        .frame(width: geometry.size.width * 0.74, height: geometry.size.height * 0.40)
                        .blur(radius: 70)
                        .position(x: geometry.size.width * CGFloat(0.78 - 0.10 * cos(phase)),
                                  y: geometry.size.height * CGFloat(0.66 + 0.06 * sin(phase)))
                }
                .clipped()
            }
        }
        .ignoresSafeArea()
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }
}

private struct ReceivedDocument: FileDocument {
    static let readableContentTypes: [UTType] = [.data]
    let data: Data

    init(data: Data) { self.data = data }

    init(configuration: ReadConfiguration) throws {
        guard let data = configuration.file.regularFileContents else { throw TransferError.invalid }
        self.data = data
    }

    func fileWrapper(configuration: WriteConfiguration) throws -> FileWrapper {
        FileWrapper(regularFileWithContents: data)
    }
}
