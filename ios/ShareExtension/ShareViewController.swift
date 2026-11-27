import ImageIO
import SwiftUI
import UniformTypeIdentifiers
import UIKit

final class ShareViewController: UIViewController {
    private let bluetooth = Bluetooth()

    override func viewDidLoad() {
        super.viewDidLoad()
        let providers = (extensionContext?.inputItems as? [NSExtensionItem] ?? [])
            .flatMap { $0.attachments ?? [] }
        let host = UIHostingController(rootView: ShareView(bluetooth: bluetooth, providers: providers) { [weak self] in
            self?.bluetooth.stop()
            self?.extensionContext?.completeRequest(returningItems: nil)
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

    deinit { bluetooth.stop() }
}

private struct ShareView: View {
    @ObservedObject var bluetooth: Bluetooth
    let providers: [NSItemProvider]
    let close: () -> Void
    @State private var transfer: Transfer?
    @State private var error: String?

    var body: some View {
        NavigationStack {
            List {
                if let transfer {
                    Section("Sharing") {
                        Text(transfer.kind == .text ? transfer.text : transfer.name)
                            .lineLimit(3)
                    }
                } else if let error {
                    Text(error)
                } else {
                    ProgressView("Loading shared item…")
                }
                if let progress = bluetooth.progress {
                    ProgressView("Sending over Bluetooth…", value: progress)
                }
                if let result = bluetooth.result { Text(result).foregroundStyle(.secondary) }
                Section("Nearby Windows PCs") {
                    if bluetooth.peers.isEmpty { Text(bluetooth.state).foregroundStyle(.secondary) }
                    ForEach(bluetooth.peers) { peer in
                        Button {
                            if let transfer { bluetooth.send(transfer, to: peer.id) }
                        } label: {
                            Label(peer.name, systemImage: "desktopcomputer")
                        }
                        .disabled(transfer == nil || bluetooth.progress != nil || bluetooth.sendCompleted)
                    }
                }
            }
            .navigationTitle("Pipopu")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(bluetooth.sendCompleted ? "Done" : "Cancel", action: close)
                }
            }
        }
        .task {
            do { transfer = try await loadTransfer() }
            catch { self.error = error.localizedDescription }
        }
    }

    private func loadTransfer() async throws -> Transfer {
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
        for provider in providers {
            if provider.canLoadObject(ofClass: NSString.self) {
                let value: String = try await withCheckedThrowingContinuation { continuation in
                    _ = provider.loadObject(ofClass: NSString.self) { value, error in
                        guard let value = value as? String else {
                            continuation.resume(throwing: error ?? TransferError.invalid)
                            return
                        }
                        continuation.resume(returning: value)
                    }
                }
                return try Transfer(kind: .text, data: Data(value.utf8))
            }
            if provider.canLoadObject(ofClass: NSURL.self) {
                let value: URL = try await withCheckedThrowingContinuation { continuation in
                    _ = provider.loadObject(ofClass: NSURL.self) { value, error in
                        guard let value = value as? URL else {
                            continuation.resume(throwing: error ?? TransferError.invalid)
                            return
                        }
                        continuation.resume(returning: value)
                    }
                }
                return try Transfer(kind: .text, data: Data(value.absoluteString.utf8))
            }
        }
        throw TransferError.invalid
    }

    private static func loadImage(at url: URL, identifier: String) throws -> Transfer {
        let file = try FileHandle(forReadingFrom: url)
        defer { try? file.close() }
        let data = try file.read(upToCount: Transfer.limit + 1) ?? Data()
        let type = UTType(identifier)
        if data.count <= Transfer.limit, type == .jpeg || type == .png,
           let ext = type?.preferredFilenameExtension {
            return try Transfer(kind: .image, name: "Shared image.\(ext)", data: data)
        }

        // Convert HEIC and large photos to a size and format Windows can copy.
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
              let preview = CGImageSourceCreateThumbnailAtIndex(source, 0, [
                kCGImageSourceCreateThumbnailFromImageAlways: true,
                kCGImageSourceCreateThumbnailWithTransform: true,
                kCGImageSourceThumbnailMaxPixelSize: 2048
              ] as CFDictionary)
        else { throw TransferError.invalid }

        let image = UIImage(cgImage: preview)
        for quality in [0.9, 0.75, 0.55] {
            if let jpeg = image.jpegData(compressionQuality: quality), jpeg.count <= Transfer.limit {
                return try Transfer(kind: .image, name: "Shared image.jpg", data: jpeg)
            }
        }
        throw TransferError.tooLarge
    }
}
