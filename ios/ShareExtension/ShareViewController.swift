import SwiftUI
import UniformTypeIdentifiers
import UIKit

final class ShareViewController: UIViewController {
    private let bluetooth = Bluetooth()
    private var isClosing = false

    override func viewDidLoad() {
        super.viewDidLoad()
        let items = extensionContext?.inputItems as? [NSExtensionItem] ?? []
        let host = UIHostingController(rootView: ShareView(bluetooth: bluetooth, items: items) { [weak self] in
            guard let self, !self.isClosing else { return }
            self.isClosing = true
            self.bluetooth.stop()
            self.extensionContext?.completeRequest(returningItems: nil)
        })
        addChild(host)
        host.view.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(host.view)
        NSLayoutConstraint.activate([
            host.view.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            host.view.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            host.view.topAnchor.constraint(equalTo: view.topAnchor),
            host.view.bottomAnchor.constraint(equalTo: view.bottomAnchor)
        ])
        host.didMove(toParent: self)
        bluetooth.start()
    }

    deinit { Task { @MainActor [bluetooth] in bluetooth.stop() } }
}

private struct ShareView: View {
    @ObservedObject var bluetooth: Bluetooth
    let items: [NSExtensionItem]
    let close: () -> Void
    @State private var transfer: Transfer?
    @State private var error: String?
    @State private var selectedPeer: UUID?
    @State private var sendAttempt: UUID?
    @State private var finishedAttempt: UUID?
    @State private var cloudSelected = false
    @ScaledMetric(relativeTo: .body) private var tileWidth = 102.0

    private let accent = Color(red: 0.48, green: 0.32, blue: 0.89)

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: 26) {
                    if let transfer {
                        preview(transfer)
                    } else if let error {
                        Label(error, systemImage: "exclamationmark.circle")
                            .foregroundStyle(.secondary)
                    } else {
                        ProgressView("Loading shared item…")
                    }

                    VStack(alignment: .leading, spacing: 18) {
                        Text("Nearby devices").font(.headline)
                            .accessibilityAddTraits(.isHeader)
                        if bluetooth.peers.isEmpty {
                            Label(bluetooth.state, systemImage: "dot.radiowaves.left.and.right")
                                .font(.subheadline)
                                .foregroundStyle(.secondary)
                                .frame(maxWidth: .infinity, minHeight: 100)
                        } else {
                            LazyVGrid(columns: [GridItem(.adaptive(minimum: tileWidth), spacing: 12)], spacing: 18) {
                                ForEach(bluetooth.peers) { peer in peerButton(peer) }
                            }
                            .frame(maxWidth: bluetooth.peers.count < 3
                                ? tileWidth * Double(bluetooth.peers.count) + 12 : .infinity)
                            .frame(maxWidth: .infinity)
                        }
                        if let stage = bluetooth.sendStage {
                            Text(stage.label)
                                .font(.footnote)
                                .foregroundStyle(.secondary)
                            if let speed = bluetooth.uploadSpeedLabel {
                                Label(speed, systemImage: "speedometer")
                                    .font(.footnote.monospacedDigit())
                                    .foregroundStyle(.secondary)
                            }
                        } else if let result = bluetooth.result, !bluetooth.sendCompleted {
                            if let stage = bluetooth.sendFailureStage {
                                Text(stage.label.trimmingCharacters(in: CharacterSet(charactersIn: "…")))
                                    .font(.footnote.weight(.semibold))
                            }
                            Label(result, systemImage: "exclamationmark.circle")
                                .font(.footnote)
                                .foregroundStyle(.secondary)
                                .textSelection(.enabled)
                        }
                        if let code = bluetooth.pairingCode {
                            Text("Check code \(code) on the receiving device, then approve the pairing there.")
                                .font(.footnote)
                        }
                    }
                }
                .padding(22)
                .frame(maxWidth: 560)
                .frame(maxWidth: .infinity)
            }
            .background(Color(uiColor: .systemGroupedBackground))
            .navigationTitle("Pipopu")
            .navigationBarTitleDisplayMode(.inline)
            .tint(accent)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(bluetooth.sendCompleted ? "Done" : "Cancel", action: close)
                }
            }
        }
        .sensoryFeedback(.impact(weight: .light), trigger: sendAttempt)
        .sensoryFeedback(trigger: finishedAttempt) { _, _ in
            bluetooth.sendCompleted ? .success : .error
        }
        .alert("Trust this device?", isPresented: Binding(get: { bluetooth.pairing != nil }, set: { _ in }),
               presenting: bluetooth.pairing) { _ in
            Button("Trust device") { bluetooth.answerPairing(true) }
            Button("Decline", role: .cancel) { bluetooth.answerPairing(false) }
        } message: { request in
            Text("Only trust \(request.name) if it shows code \(request.code).")
        }
        .task(id: bluetooth.sendCompleted) {
            guard bluetooth.sendCompleted else { return }
            try? await Task.sleep(for: .milliseconds(400))
            guard !Task.isCancelled else { return }
            close()
        }
        .task {
            do { transfer = try await loadTransfer() }
            catch { self.error = error.localizedDescription }
        }
    }

    private func preview(_ item: Transfer) -> some View {
        HStack(spacing: 14) {
            Image(systemName: previewSymbol(for: item.kind))
                .font(.system(size: 19, weight: .semibold))
                .foregroundStyle(accent)
                .frame(width: 44, height: 44)
                .background(accent.opacity(0.12), in: RoundedRectangle(cornerRadius: 13))
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 4) {
                Text(item.kind == .text ? item.text : item.name)
                    .font(.subheadline.weight(.semibold))
                    .lineLimit(2)
                if item.kind != .text {
                    Text(ByteCountFormatter.string(fromByteCount: item.size, countStyle: .file))
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
            Spacer(minLength: 0)
        }
        .padding(12)
        .background(Color(uiColor: .secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 20))
    }

    private func previewSymbol(for kind: Transfer.Kind) -> String {
        switch kind {
        case .text: "text.alignleft"
        case .image: "photo"
        case .file: "doc"
        }
    }

    private func peerButton(_ peer: Bluetooth.Peer) -> some View {
        let isSelected = selectedPeer == peer.id
        let progress = isSelected ? bluetooth.progress : nil
        let delivered = isSelected && bluetooth.sendCompleted

        return Button {
            send(to: peer, overCloud: false)
        } label: {
            VStack(spacing: 8) {
                ZStack {
                    Circle()
                        .fill(LinearGradient(
                            colors: [accent.opacity(0.20), accent.opacity(0.07)],
                            startPoint: .topLeading, endPoint: .bottomTrailing
                        ))
                        .overlay {
                            Circle().strokeBorder(accent.opacity(0.14), lineWidth: 1)
                        }
                    if let progress {
                        Circle()
                            .trim(from: 0, to: progress)
                            .stroke(accent, style: StrokeStyle(lineWidth: 3, lineCap: .round))
                            .rotationEffect(.degrees(-90))
                            .padding(2)
                            .animation(.linear(duration: 0.12), value: progress)
                        Text(progress, format: .percent.precision(.fractionLength(0)))
                            .font(.caption.weight(.semibold).monospacedDigit())
                    } else {
                        Image(systemName: delivered ? "checkmark" : "desktopcomputer")
                            .font(.system(size: 25, weight: .medium))
                    }
                }
                .frame(width: 70, height: 70)
                .foregroundStyle(accent)
                .accessibilityHidden(true)

                Text(peer.name)
                    .font(.caption.weight(.semibold))
                    .multilineTextAlignment(.center)
                    .lineLimit(2, reservesSpace: true)
                    .foregroundStyle(.primary)
                if delivered {
                    Text(cloudSelected ? "Uploaded" : "Sent").font(.caption2).foregroundStyle(.secondary)
                }
            }
            .frame(maxWidth: .infinity)
            .contentShape(Rectangle())
        }
        .buttonStyle(DeviceButtonStyle())
        .contextMenu {
            if peer.supportsCloudAndSecure {
                Button("Send over cloud", systemImage: "icloud.and.arrow.up") { send(to: peer, overCloud: true) }
            }
        }
        .disabled(transfer == nil || !peer.connected || bluetooth.progress != nil || bluetooth.sendCompleted)
        .opacity(!isSelected && (bluetooth.progress != nil || bluetooth.sendCompleted) ? 0.55 : 1)
        .accessibilityLabel("Send to " + peer.name)
        .accessibilityValue(progress?.formatted(.percent.precision(.fractionLength(0))) ?? (delivered ? "Sent" : ""))
    }

    private func send(to peer: Bluetooth.Peer, overCloud: Bool) {
        guard let transfer else { return }
        selectedPeer = peer.id
        cloudSelected = overCloud
        let attempt = UUID()
        sendAttempt = attempt
        bluetooth.send(transfer, to: peer.id, overCloud: overCloud) { [finishedAttempt = $finishedAttempt] in
            finishedAttempt.wrappedValue = attempt
        }
    }

    private struct DeviceButtonStyle: ButtonStyle {
        func makeBody(configuration: Configuration) -> some View {
            configuration.label
                .scaleEffect(configuration.isPressed ? 0.95 : 1)
                .animation(.snappy(duration: 0.18), value: configuration.isPressed)
        }
    }

    private func loadTransfer() async throws -> Transfer {
        let providers = items.flatMap { $0.attachments ?? [] }
        for provider in providers {
            if let identifier = provider.registeredTypeIdentifiers.first(where: {
                UTType($0)?.conforms(to: .image) == true
            }) {
                return try await withCheckedThrowingContinuation { continuation in
                    provider.loadFileRepresentation(forTypeIdentifier: identifier) { url, error in
                        continuation.resume(with: Result {
                            guard let url else { throw error ?? TransferError.invalid }
                            return try Self.loadImage(at: url, identifier: identifier)
                        })
                    }
                }
            }
        }
        if let file = try await SharedFile.load(from: providers) { return file }
        return try await SharedText.load(from: items)
    }

    private static func loadImage(at url: URL, identifier: String) throws -> Transfer {
        let ext = UTType(identifier)?.preferredFilenameExtension ?? url.pathExtension
        return try Transfer.importFile(url, kind: .image, name: ext.isEmpty ? "Shared image" : "Shared image.\(ext)")
    }
}
