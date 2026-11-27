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
    @State private var listening = true
    @State private var exporting = false
    @State private var status = ""
    @State private var fileExport: ExportFile?
    @State private var latestReceipt: UUID?
    @State private var draft = ""
    @State private var attachment: Transfer?
    @State private var importing = false
    @State private var recipient: UUID?
    @State private var sendStatus = ""
    @State private var sendAttempt: UUID?
    @State private var finishedAttempt: UUID?
    @FocusState private var editingDraft: Bool

    private let accent = Color(red: 0.48, green: 0.32, blue: 0.89)

    var body: some View {
        NavigationStack {
            List {
                brand
                    .listRowBackground(Color.clear)
                    .listRowInsets(EdgeInsets())
                composer
                recipients
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
                if let transfer = bluetooth.received {
                    Section("Received") {
                        if transfer.kind == .text {
                            Text(transfer.text).textSelection(.enabled)
                            Button("Copy text", systemImage: "doc.on.doc") {
                                UIPasteboard.general.string = transfer.text
                                status = "Copied text"
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
                Section {
                    Toggle("Available to receive", isOn: $listening)
                    Toggle("Save received files automatically", isOn: $saveReceivedFiles)
                    TextField("Name on Windows", text: $receiverName, prompt: Text(UIDevice.current.model))
                        .onSubmit { updateListening() }
                    if let message = bluetooth.receiveError {
                        Text(message).font(.footnote).foregroundStyle(.secondary)
                    }
                } header: {
                    Text("Receiving")
                } footer: {
                    Text("Keep Pipopu open to receive. Automatically saved files appear in On My iPhone or iPad → Pipopu → Received in Files.")
                }
                if !status.isEmpty { Text(status).foregroundStyle(.secondary) }
            }
            .listStyle(.insetGrouped)
            .scrollDismissesKeyboard(.interactively)
            .toolbar(.hidden, for: .navigationBar)
            .tint(accent)
        }
        .onAppear(perform: updateListening)
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
        .alert("Trust this PC?", isPresented: Binding(get: { bluetooth.pairing != nil }, set: { _ in }),
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

    private var brand: some View {
        VStack(spacing: 12) {
            Image("PipopuMark")
                .resizable()
                .frame(width: 76, height: 76)
                .clipShape(RoundedRectangle(cornerRadius: 20))
                .shadow(color: accent.opacity(0.42), radius: 20, y: 5)
                .accessibilityHidden(true)
            Text("Pipopu")
                .font(.title2.weight(.semibold))
                .accessibilityAddTraits(.isHeader)
        }
        .padding(.vertical, 16)
        .frame(maxWidth: .infinity)
    }

    private var composer: some View {
        Section("Send") {
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
                TextField("Text to send", text: $draft, axis: .vertical)
                    .lineLimit(2...5)
                    .focused($editingDraft)
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
        Section("Nearby Windows PCs") {
            if bluetooth.peers.isEmpty {
                Label(bluetooth.state, systemImage: "dot.radiowaves.left.and.right")
                    .foregroundStyle(.secondary)
            }
            ForEach(bluetooth.peers) { peer in
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
                .disabled(!peer.connected || (draft.isEmpty && attachment == nil) || bluetooth.progress != nil)
                .accessibilityHint("Sends the selected text or file")
            }
            if !sendStatus.isEmpty {
                Text(sendStatus).font(.footnote).foregroundStyle(.secondary)
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

    private func send(to peer: Bluetooth.Peer) {
        do {
            let transfer = try attachment ?? Transfer(kind: .text, data: Data(draft.utf8))
            editingDraft = false
            sendStatus = ""
            recipient = peer.id
            let attempt = UUID()
            sendAttempt = attempt
            bluetooth.send(transfer, to: peer.id) { [sendStatus = $sendStatus, finishedAttempt = $finishedAttempt, weak bluetooth] in
                sendStatus.wrappedValue = bluetooth?.result ?? ""
                finishedAttempt.wrappedValue = attempt
            }
        } catch {
            sendStatus = error.localizedDescription
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
        status = ""
        guard saveReceivedFiles, let transfer, transfer.kind != .text else { return }

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
