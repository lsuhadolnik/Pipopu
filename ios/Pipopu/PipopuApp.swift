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
    @State private var status = ""
    @State private var fileExport: ExportFile?
    @State private var activityItems = ActivityArchive.items()
    @State private var unsavedActivity: [String: Transfer] = [:]
    @State private var archivingActivity: Set<String> = []
    @State private var conversation: String?
    @State private var incomingBanner: Bluetooth.IncomingActivity?
    @State private var itemToDelete: ActivityItem?
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
        .onReceive(bluetooth.activityEvents, perform: recordActivity)
        .onChange(of: bluetooth.incomingActivity) { previous, current in
            if let started = current.values.first(where: { previous[$0.id] == nil }) {
                incomingBanner = started
            }
        }
        .overlay(alignment: .top) { transferBanner }
        .task(id: bannerDismissal) {
            guard bannerDismissal != nil else { return }
            do { try await Task.sleep(for: .seconds(4)) } catch { return }
            incomingBanner = nil
        }
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
        .alert("Delete this transfer from Activity?", isPresented: Binding(
            get: { itemToDelete != nil }, set: { if !$0 { itemToDelete = nil } }
        ), presenting: itemToDelete) { item in
            Button("Delete", role: .destructive) { deleteActivity(item) }
            Button("Cancel", role: .cancel) {}
        } message: { _ in
            Text("Copies you saved to Files remain available.")
        }
    }

    private var bannerDismissal: String? {
        guard let incomingBanner, bluetooth.incomingActivity[incomingBanner.id] == nil else { return nil }
        return incomingBanner.id
    }

    @ViewBuilder private var transferBanner: some View {
        if let banner = incomingBanner, tab != .activity || conversation != banner.device {
            Button {
                editingDraft = false
                fileSend = nil
                openConversation(banner.device)
                incomingBanner = nil
            } label: {
                if let active = bluetooth.incomingActivity[banner.id] {
                    incomingRow(active)
                } else {
                    Label("View transfer from \(banner.deviceName)", systemImage: "tray.and.arrow.down")
                }
            }
            .buttonStyle(.plain)
            .padding(14)
            .frame(maxWidth: 400)
            .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 16))
            .shadow(color: .black.opacity(0.2), radius: 12, y: 4)
            .padding(.horizontal, 16)
            .padding(.top, 6)
            .accessibilityHint("Opens Activity")
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
            if let conversation {
                Button("All conversations", systemImage: "chevron.left") {
                    self.conversation = nil
                    status = ""
                }
                screenHeading(deviceName(conversation))
                if !status.isEmpty { Text(status).foregroundStyle(.secondary) }
                ForEach(incomingTransfers.filter { $0.device == conversation }, id: \.id) { incomingRow($0) }
                ForEach(activityItems.filter { $0.device == conversation }.sorted { $0.date > $1.date }) { item in
                    activityBubble(item)
                }
                if let error = bluetooth.receiveError { Text(error).foregroundStyle(.secondary) }
            } else {
                screenHeading("Activity")
                Section("Conversations") {
                    if activityDevices.isEmpty {
                        Label("No transfers yet", systemImage: "tray").foregroundStyle(.secondary)
                    }
                    ForEach(activityDevices, id: \.self) { device in
                        Button { openConversation(device) } label: {
                            HStack {
                                Image(systemName: "desktopcomputer").foregroundStyle(accent)
                                VStack(alignment: .leading, spacing: 4) {
                                    Text(deviceName(device)).font(.headline)
                                    if let active = incomingTransfers.first(where: { $0.device == device }) {
                                        Text("Receiving \(active.name)").font(.subheadline).foregroundStyle(.secondary)
                                    } else if let item = activityItems.first(where: { $0.device == device }) {
                                        Text(item.isText ? (item.secure ? "Secure text" : "Message") : item.name)
                                            .font(.subheadline).foregroundStyle(.secondary).lineLimit(1)
                                    }
                                }
                                Spacer()
                                Image(systemName: "chevron.right").font(.caption).foregroundStyle(.secondary)
                            }
                        }
                        .buttonStyle(.plain)
                    }
                }
                if !status.isEmpty { Text(status).foregroundStyle(.secondary) }
                cloudTransfers
            }
        }
        .listStyle(.insetGrouped)
    }

    private var incomingTransfers: [Bluetooth.IncomingActivity] {
        bluetooth.incomingActivity.values.sorted { $0.id < $1.id }
    }

    private var activityDevices: [String] {
        var seen = Set<String>()
        return (incomingTransfers.map(\.device) + activityItems.map(\.device)).filter { seen.insert($0).inserted }
    }

    private func deviceName(_ device: String) -> String {
        incomingTransfers.first { $0.device == device }?.deviceName ??
            bluetooth.trustedDevices.first { $0.id == device }?.name ??
            activityItems.first { $0.device == device }?.deviceName ?? "Device"
    }

    private func openConversation(_ device: String) {
        conversation = device
        tab = .activity
        unreadReceipt = false
        status = ""
    }

    private func incomingRow(_ transfer: Bluetooth.IncomingActivity) -> some View {
        VStack(alignment: .leading, spacing: 5) {
            Label("Receiving \(transfer.name)", systemImage: "tray.and.arrow.down").font(.subheadline.weight(.semibold))
            Text(transfer.deviceName).font(.caption).foregroundStyle(.secondary)
            if let fraction = transfer.fraction {
                ProgressView(value: fraction) { Text(fraction.formatted(.percent.precision(.fractionLength(0)))) }
                    .font(.caption)
            } else {
                ProgressView("Preparing transfer…").font(.caption)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func activityBubble(_ item: ActivityItem) -> some View {
        VStack(alignment: item.outgoing ? .trailing : .leading, spacing: 4) {
            let direction = item.queued ? "Queued" : (item.outgoing ? "Sent" : "Received")
            Text("\(direction) · \(item.date.formatted(date: .abbreviated, time: .shortened))")
                .font(.caption).foregroundStyle(.secondary)
            Button {
                openActivityItem(item)
            } label: {
                Group {
                    if item.isText {
                        if item.secure {
                            Label("Copy secure text", systemImage: "lock.doc")
                        } else {
                            Text((try? activityText(item, preview: true)) ?? "Message unavailable")
                                .lineLimit(12)
                                .accessibilityHint("Copies the message")
                        }
                    } else {
                        Label(ReceivedFileArchive.name(item.name), systemImage: "square.and.arrow.up")
                    }
                }
                .padding(11)
                .background(item.outgoing ? accent.opacity(0.17) : Color.secondary.opacity(0.12),
                            in: RoundedRectangle(cornerRadius: 14))
            }
            .buttonStyle(.plain)
            .contextMenu {
                if item.kind == Transfer.Kind.image.rawValue && item.size <= 8_000_000 {
                    Button("Copy image", systemImage: "doc.on.doc") { copyActivityImage(item) }
                }
                Button("Delete from Activity", systemImage: "trash", role: .destructive) { itemToDelete = item }
                    .disabled(archivingActivity.contains(item.id))
            }
        }
        .frame(maxWidth: .infinity, alignment: item.outgoing ? .trailing : .leading)
        .listRowBackground(Color.clear)
        .listRowSeparator(.hidden)
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

    private func activityText(_ item: ActivityItem, preview: Bool = false) throws -> String {
        let data: Data
        if let transfer = unsavedActivity[item.id] {
            data = preview ? Data(transfer.data.prefix(8192)) : transfer.data
        } else {
            let file = try ActivityArchive.file(item)
            if preview {
                let input = try FileHandle(forReadingFrom: file.url)
                defer { try? input.close() }
                data = try input.read(upToCount: 8192) ?? Data()
            } else {
                data = try Data(contentsOf: file.url)
            }
        }
        return String(decoding: data, as: UTF8.self)
    }

    private func openActivityItem(_ item: ActivityItem) {
        do {
            if item.isText {
                let value = try activityText(item)
                if item.secure {
                    UIPasteboard.general.setItems([[UTType.utf8PlainText.identifier: value]], options: [.localOnly: true])
                } else {
                    UIPasteboard.general.string = value
                }
                status = item.secure ? "Copied secure text" : "Copied text"
            } else {
                let file: TemporaryFile
                if let transfer = unsavedActivity[item.id] {
                    if let existing = transfer.file { file = existing }
                    else {
                        file = TemporaryFile(size: transfer.size)
                        try transfer.data.write(to: file.url)
                    }
                } else {
                    file = try ActivityArchive.file(item)
                }
                fileExport = try ExportFile(file, name: ReceivedFileArchive.name(item.name))
            }
        } catch { status = error.localizedDescription }
    }

    private func copyActivityImage(_ item: ActivityItem) {
        let image: UIImage?
        if let transfer = unsavedActivity[item.id] {
            image = transfer.file.map { UIImage(contentsOfFile: $0.url.path) } ?? UIImage(data: transfer.data)
        } else if let file = try? ActivityArchive.file(item) {
            image = UIImage(contentsOfFile: file.url.path)
        } else {
            image = nil
        }
        guard let image else { status = "Could not open the image."; return }
        UIPasteboard.general.image = image
        status = "Copied image"
    }

    private func deleteActivity(_ item: ActivityItem) {
        do {
            if unsavedActivity.removeValue(forKey: item.id) == nil {
                try ActivityArchive.remove(item)
            }
            activityItems.removeAll { $0.id == item.id }
            status = ""
        } catch { status = error.localizedDescription }
    }

    private func recordActivity(_ event: Bluetooth.ActivityEvent) {
        let transfer = event.transfer
        if transfer.replay { openConversation(event.device) }
        guard !activityItems.contains(where: { $0.id == event.id }) else { return }
        let item = ActivityItem(event)
        activityItems.insert(item, at: 0)
        unsavedActivity[item.id] = transfer
        archivingActivity.insert(item.id)
        if !event.outgoing && (tab != .activity || conversation != event.device) { unreadReceipt = true }
        let autoSave = saveReceivedFiles && !event.outgoing && !item.isText && !transfer.replay

        Task {
            let result = await Task.detached(priority: .utility) {
                Result { try ActivityArchive.save(item, transfer: transfer) }
            }.value
            archivingActivity.remove(item.id)
            switch result {
            case .success(let stored):
                if let index = activityItems.firstIndex(where: { $0.id == stored.id }) {
                    activityItems[index] = stored
                }
                unsavedActivity[stored.id] = nil
            case .failure(let error):
                if conversation == item.device || conversation == nil {
                    status = "Could not keep this transfer in Activity. " + error.localizedDescription
                }
            }
            if autoSave {
                let message = await Task.detached(priority: .utility) {
                    do { try ReceivedFileArchive.save(transfer); return "Saved to Files." }
                    catch { return "Automatic save failed. " + error.localizedDescription }
                }.value
                if conversation == item.device || conversation == nil { status = message }
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
