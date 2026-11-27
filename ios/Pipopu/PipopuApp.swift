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

    var body: some View {
        NavigationStack {
            List {
                Section {
                    Toggle("Available to receive", isOn: $listening)
                    TextField("Name on Windows", text: $receiverName, prompt: Text(UIDevice.current.model))
                        .onSubmit(restart)
                } footer: {
                    Text("Keep Pipopu open while sending from Windows. To send from your Apple device, choose Pipopu in the Share Sheet.")
                }
                if listening {
                    Section("Windows PCs") {
                        if bluetooth.peers.isEmpty { Text(bluetooth.state).foregroundStyle(.secondary) }
                        ForEach(bluetooth.peers) { peer in
                            LabeledContent(peer.name, value: peer.connected ? "Ready" : "Connecting…")
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
                if let message = bluetooth.result { Text(message).foregroundStyle(.secondary) }
                if !status.isEmpty { Text(status).foregroundStyle(.secondary) }
            }
            .navigationTitle("Pipopu")
        }
        .onAppear(perform: updateListening)
        .onChange(of: phase) { _, _ in updateListening() }
        .onChange(of: listening) { _, _ in updateListening() }
        .onChange(of: bluetooth.received?.id) { _, _ in status = "" }
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

    private var exportName: String {
        guard let transfer = bluetooth.received, !transfer.name.isEmpty else { return "Pipopu transfer" }
        let name = transfer.name.replacingOccurrences(of: "\\", with: "/")
        let basename = name.split(separator: "/").last.map(String.init) ?? ""
        return basename.isEmpty || basename == "." || basename == ".." ? "Pipopu transfer" : basename
    }

    private var exportType: UTType {
        UTType(filenameExtension: (exportName as NSString).pathExtension) ?? .data
    }

    private func restart() {
        bluetooth.stop()
        updateListening()
    }

    private func updateListening() {
        if listening && phase == .active {
            let label = receiverName.trimmingCharacters(in: .whitespacesAndNewlines)
            bluetooth.start(receivingAs: label.isEmpty ? UIDevice.current.model : label)
        } else if !listening || phase == .background {
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
