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
    @State private var listening = true
    @State private var exporting = false
    @State private var status = ""
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
                if let transfer = bluetooth.received {
                    Section("Received") {
                        if transfer.kind == .text {
                            Text(transfer.text).textSelection(.enabled)
                            Button("Copy text", systemImage: "doc.on.doc") {
                                UIPasteboard.general.string = transfer.text
                                status = "Copied text"
                            }
                        } else {
                            if transfer.kind == .image, let image = UIImage(data: transfer.data) {
                                Image(uiImage: image).resizable().scaledToFit().frame(maxHeight: 240)
                                Button("Copy image", systemImage: "doc.on.doc") {
                                    UIPasteboard.general.image = image
                                    status = "Copied image"
                                }
                            }
                            Button("Save " + (transfer.name.isEmpty ? "item" : transfer.name), systemImage: "square.and.arrow.down") {
                                exporting = true
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
                    TextField("Name on Windows", text: $receiverName, prompt: Text(UIDevice.current.model))
                        .onSubmit { updateListening() }
                    if let message = bluetooth.receiveError {
                        Text(message).font(.footnote).foregroundStyle(.secondary)
                    }
                } header: {
                    Text("Receiving")
                } footer: {
                    Text("Keep Pipopu open to appear as a recipient on Windows.")
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
        .onChange(of: bluetooth.received?.id) { _, _ in status = "" }
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
                            Text(ByteCountFormatter.string(fromByteCount: Int64(attachment.data.count), countStyle: .file))
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
                .disabled((draft.isEmpty && attachment == nil) || bluetooth.progress != nil)
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
                attachment = try Transfer(kind: .image, name: file.name, data: file.data)
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
        guard let transfer = bluetooth.received, !transfer.name.isEmpty else { return "Pipopu transfer" }
        let name = transfer.name.replacingOccurrences(of: "\\", with: "/")
        let basename = name.split(separator: "/").last.map(String.init) ?? ""
        return basename.isEmpty || basename == "." || basename == ".." ? "Pipopu transfer" : basename
    }

    private var exportType: UTType {
        UTType(filenameExtension: (exportName as NSString).pathExtension) ?? .data
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
